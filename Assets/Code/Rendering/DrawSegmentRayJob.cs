using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile(FloatMode = FloatMode.Fast)]
public struct DrawSegmentRayJob : IJobParallelFor
{
	[ReadOnly] public int2 originalNextFreePixel;
	[ReadOnly] public RenderManager.SegmentData segment;
	[ReadOnly] public float2 vanishingPointScreenSpace;
	[ReadOnly] public float3 vanishingPointCameraRayOnScreen;
	[ReadOnly] public int axisMappedToY;
	[ReadOnly] public int seenPixelCacheLength;
	[ReadOnly] public int activeRayBufferWidth;
	[ReadOnly] public int rayIndexOffset;
	[ReadOnly] public int elementIterationDirection;
	[ReadOnly] public bool vanishingPointOnScreen;
	[ReadOnly] public World world;
	[ReadOnly] public CameraData camera;
	[ReadOnly] public float2 screen;
	[ReadOnly] public Unity.Profiling.ProfilerMarker markerRay;
	[ReadOnly] public RayBuffer.Native activeRayBufferFull;

	public void Execute (int planeRayIndex)
	{
		NativeArray<ColorARGB32> rayColumn = activeRayBufferFull.GetRayColumn(planeRayIndex + rayIndexOffset);

		markerRay.Begin();

		int rayStepCount = 0;
		int2 nextFreePixel = originalNextFreePixel;

		NativeArray<byte> seenPixelCache = new NativeArray<byte>(seenPixelCacheLength, Allocator.Temp, NativeArrayOptions.ClearMemory);

		float2 frustumYBounds;
		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)segment.RayCount;
			float3 camLocalPlaneRayDirection = lerp(segment.CamLocalPlaneRayMin, segment.CamLocalPlaneRayMax, endRayLerp);
			ray = new SegmentDDAData(camera.Position.xz, camLocalPlaneRayDirection.xz);
			frustumYBounds = SetupFrustumBounds(endRayLerp, camLocalPlaneRayDirection);
		}

		while (true) {
			int2 columnBounds = SetupColumnBounds(frustumYBounds, ray.IntersectionDistancesUnnormalized);
			float4 bothIntersections = ray.Intersections; // xy last, zw next

			if ((rayStepCount++ & 31) == 31) {
				unsafe {
					if (!RareColumnAdjustment(bothIntersections, ref nextFreePixel, (byte*)seenPixelCache.GetUnsafeReadOnlyPtr())) {
						break;
					}
				}
			}

			World.RLEColumn column = world.GetVoxelColumn(ray.position);

			int2 elementMinMax = int2(column.elementIndex, column.elementIndex + column.elementCount);
			if (elementIterationDirection < 0) {
				elementMinMax = elementMinMax.yx - 1; // reverse order to render from top to bottom for correct depth results
			}

			for (int iElement = elementMinMax.x; iElement != elementMinMax.y; iElement += elementIterationDirection) {
				World.RLEElement element = world.WorldElements[iElement];

				if (any(bool2(element.Top < columnBounds.x, element.Bottom > columnBounds.y))) {
					continue;
				}

				float2 bottomIntersection = select(bothIntersections.xy, bothIntersections.zw, element.Bottom > camera.Position.y);
				float2 topIntersection = select(bothIntersections.xy, bothIntersections.zw, element.Top < camera.Position.y);
				float2 worldbounds = float2(element.Bottom, element.Top);
				float3 bottomWorld = shuffle(bottomIntersection, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightX, ShuffleComponent.LeftY);
				float3 topWorld = shuffle(topIntersection, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightY, ShuffleComponent.LeftY);

				camera.ProjectToHomogeneousCameraSpace(bottomWorld, topWorld, out float4 bottomHomo, out float4 topHomo);

				if (!camera.ClipHomogeneousCameraSpaceLine(bottomHomo, topHomo, out float4 bottomHomoClipped, out float4 topHomoClipped)) {
					continue; // behind the camera
				}

				float2 rayBufferBoundsFloat = camera.ProjectClippedToScreen(bottomHomoClipped, topHomoClipped, screen, axisMappedToY);
				rayBufferBoundsFloat = select(rayBufferBoundsFloat.xy, rayBufferBoundsFloat.yx, rayBufferBoundsFloat.x > rayBufferBoundsFloat.y);

				int2 rayBufferBounds = int2(round(rayBufferBoundsFloat));

				// check if the line overlaps with the area that's writable
				if (any(bool2(rayBufferBounds.y < nextFreePixel.x, rayBufferBounds.x > nextFreePixel.y))) {
					continue;
				}

				ExtendPixelHorizon(ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

				WriteLine(rayColumn, rayBufferBounds, seenPixelCache, element.Color);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING; // wrote to the last pixels on screen - further writing will run out of bounds
				}
			}

			ray.Step();

			bool3 endConditions = bool3(
				columnBounds.y < 0,
				columnBounds.x > world.DimensionY,
				ray.AtEnd
			);

			if (any(endConditions)) {
				break;
			}
		}

		STOP_TRACING:

		WriteSkybox(rayColumn, seenPixelCache);

		//if (firstCancelledStep != int.MaxValue) {
		//	for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
		//		ColorARGB32 color = rayColumn[y];
		//		color.r = (byte)min(255, rayStepCount - firstCancelledStep);
		//		rayColumn[y] = color;
		//	}
		//}

		markerRay.End();
	}

	/// <summary>
	/// Not inlined on purpose due to not running every DDA step
	/// Passed a byte* because the NativeArray is a fat struct to pass along (it increases stack by 80 bytes compared to passing the pointer)
	/// /// </summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	unsafe bool RareColumnAdjustment (float4 bothIntersections, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		float4 worldbounds = float4(0f, world.DimensionY + 1f, 0f, 0f);
		float3 bottom = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightX, ShuffleComponent.LeftY);
		float3 top = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightY, ShuffleComponent.LeftY);

		camera.ProjectToHomogeneousCameraSpace(bottom, top, out float4 lastBottom, out float4 lastTop);

		if (!camera.ClipHomogeneousCameraSpaceLine(lastBottom, lastTop, out float4 lastBottomClipped, out float4 lastTopClipped)) {
			return false; // full world line is behind camera (wat)
		}
		float2 pixelBounds = camera.ProjectClippedToScreen(lastBottomClipped, lastTopClipped, screen, axisMappedToY);

		if (!AdjustOpenPixelsRange(pixelBounds, ref nextFreePixel, seenPixelCache)) {
			return false; // all pixels that this world column can project to are written to
		}
		return true;
	}

	unsafe bool AdjustOpenPixelsRange (float2 screenYCoordsFloat, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		// we may be waiting to have pixels written outside of the working frustum, which won't happen
		screenYCoordsFloat = select(screenYCoordsFloat.xy, screenYCoordsFloat.yx, screenYCoordsFloat.x > screenYCoordsFloat.y);
		int2 screenYCoords = int2(round(screenYCoordsFloat));

		if (screenYCoords.x > nextFreePixel.x) {
			nextFreePixel.x = screenYCoords.x; // there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
											   // and further increase the bottom free pixel according to write mask
			int y = nextFreePixel.x;
			if (y <= originalNextFreePixel.y) {
				while (true) {
					byte val = seenPixelCache[y];
					if (val > 0) {
						nextFreePixel.x += 1;
						y++;
						if (y <= originalNextFreePixel.y) {
							continue;
						}
					}
					break;
				}
			}
		}

		if (screenYCoords.y < nextFreePixel.y) {
			nextFreePixel.y = screenYCoords.y;
			int y = nextFreePixel.y;
			if (y >= originalNextFreePixel.x) {
				while (true) {
					byte val = seenPixelCache[y];
					if (val > 0) {
						nextFreePixel.y -= 1;
						y--;
						if (y >= originalNextFreePixel.x) {
							continue;
						}
					}
					break;
				}
			}
		}

		if (nextFreePixel.x > nextFreePixel.y) {
			return false; // apparently we've written all pixels we can reach now
		}
		return true;
	}

	void ExtendPixelHorizon (ref int2 rayBufferBounds, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		bool2 xle = rayBufferBounds.xx <= nextFreePixel.xy;
		bool2 yge = rayBufferBounds.yy >= nextFreePixel.xy;
		rayBufferBounds = select(rayBufferBounds, nextFreePixel, bool2(xle.x, yge.y)); // x = max(x, pixel.x), y = min(y, pixel.y)

		if (xle.x & yge.x) {
			nextFreePixel.x = rayBufferBounds.y + 1;
			// try to extend the floating horizon further if we already wrote stuff there
			int y = nextFreePixel.x;
			if (y <= originalNextFreePixel.y) {
				while (true) {
					byte val = seenPixelCache[y];
					if (val > 0) {
						nextFreePixel.x += 1;
						y++;
						if (y <= originalNextFreePixel.y) {
							continue;
						}
					}
					break;
				}
			}
		}
		if (yge.y & xle.y) {
			nextFreePixel.y = rayBufferBounds.x - 1;
			// try to extend the floating horizon further if we already wrote stuff there
			int y = nextFreePixel.y;
			if (y >= originalNextFreePixel.x) {
				while (true) {
					byte val = seenPixelCache[y];
					if (val > 0) {
						nextFreePixel.y -= 1;
						y--;
						if (y >= originalNextFreePixel.x) {
							continue;
						}
					}
					break;
				}
			}
		}
	}

	void WriteLine (NativeArray<ColorARGB32> rayColumn, int2 rayBufferBounds, NativeArray<byte> seenPixelCache, ColorARGB32 color)
	{
		for (int y = rayBufferBounds.x; y <= rayBufferBounds.y; y++) {
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;
				rayColumn[y] = color;
			}
		}
	}

	void WriteSkybox (NativeArray<ColorARGB32> rayColumn, NativeArray<byte> seenPixelCache)
	{
		ColorARGB32 skybox = new ColorARGB32(255, 0, 255);
		for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
			if (seenPixelCache[y] == 0) {
				rayColumn[y] = skybox;
			}
		}
	}

	int2 SetupColumnBounds (float2 frustumYBounds, float2 intersectionDistances)
	{
		// calculate world space frustum bounds of the world column we're at
		bool2 selection = bool2(frustumYBounds.x < 0, frustumYBounds.y > 0);
		float2 distances = select(intersectionDistances.yy, intersectionDistances.xx, selection);
		float2 frustumYBoundsThisColumn = camera.Position.y + frustumYBounds * distances;
		int2 columnBounds;
		columnBounds.x = max(0, Mathf.FloorToInt(frustumYBoundsThisColumn.y));
		columnBounds.y = min(world.DimensionY, Mathf.CeilToInt(frustumYBoundsThisColumn.x));
		return columnBounds;
	}

	float2 SetupFrustumBounds (float endRayLerp, float3 camLocalPlaneRayDirection)
	{
		float3 worldB;
		if (vanishingPointOnScreen) {
			worldB = vanishingPointCameraRayOnScreen;
		} else {
			float2 dir = lerp(segment.MinScreen, segment.MaxScreen, endRayLerp) - vanishingPointScreenSpace;
			dir = normalize(dir);
			// find out where the ray from start->end starts coming on screen
			bool intersected = IntersectScreen(vanishingPointScreenSpace, dir, out float distance);
			float2 screenPosStart = vanishingPointScreenSpace + dir * select(0f, distance, intersected);
			worldB = camera.ScreenToWorldPoint(float3(screenPosStart, 1f), screen);
		}

		float3 dirB = worldB - camera.Position;
		float2 bounds = float2(camLocalPlaneRayDirection.y, dirB.y * (length(camLocalPlaneRayDirection.xz) / length(dirB.xz)));
		return select(bounds.xy, bounds.yx, bounds.x < bounds.y); // max() as X
	}

	bool IntersectScreen (float2 start, float2 dir, out float distance)
	{
		float tmin = float.NegativeInfinity;
		float tmax = float.PositiveInfinity;
		distance = default;

		if (dir.x != 0f) {
			float tx1 = -start.x / dir.x;
			float tx2 = (screen.x - start.x) / dir.x;
			tmin = max(tmin, min(tx1, tx2));
			tmax = min(tmax, max(tx1, tx2));
		} else if (start.x <= 0f || start.x >= screen.x) {
			return false;
		}

		if (dir.y != 0f) {
			float ty1 = -start.y / dir.y;
			float ty2 = (screen.y - start.y) / dir.y;
			tmin = max(tmin, min(ty1, ty2));
			tmax = min(tmax, max(ty1, ty2));
		} else if (start.y <= 0f || start.y >= screen.y) {
			return false;
		}

		distance = select(tmin, tmax, tmin < 0f);
		return !(tmin < 0f && tmax < 0f || tmax < tmin);
	}
}