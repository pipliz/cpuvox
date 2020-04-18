﻿using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;

[BurstCompile(FloatMode = FloatMode.Fast)]
public struct DrawSegmentRayJob : IJobParallelFor
{
	const int RARE_COLUMN_ADJUST_THRESHOLD = 31; // must be chosen so that it equals some 2^x - 1 to work with masking

	[ReadOnly] public int2 originalNextFreePixel; // vertical pixel bounds in the raybuffer for this segment
	[ReadOnly] public int axisMappedToY; // top/bottom segment is 0, left/right segment is 1
	[ReadOnly] public int elementIterationDirection;
	[ReadOnly] public World world;
	[ReadOnly] public CameraData camera;
	[ReadOnly] public float2 screen;
	[ReadOnly] public RayBuffer.Native activeRayBufferFull;

	[ReadOnly] public RenderManager.SegmentData segment;
	[ReadOnly] public int segmentRayIndexOffset;

	[ReadOnly] public float2 vanishingPointScreenSpace; // pixels position of vanishing point in screenspace
	[ReadOnly] public float3 vanishingPointCameraRayOnScreen; // world position of the vanishing point if vanishingPointScreenSpace is on screen

	public void Execute (int planeRayIndex)
	{
		NativeArray<ColorARGB32> rayColumn = activeRayBufferFull.GetRayColumn(planeRayIndex + segmentRayIndexOffset);

		int rayStepCount = 0;
		int2 nextFreePixel = originalNextFreePixel;

		NativeArray<byte> seenPixelCache = new NativeArray<byte>((int)ceil(screen[axisMappedToY]), Allocator.Temp, NativeArrayOptions.ClearMemory);

		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)segment.RayCount;
			float2 camLocalPlaneRayDirection = lerp(segment.CamLocalPlaneRayMin, segment.CamLocalPlaneRayMax, endRayLerp);
			ray = new SegmentDDAData(camera.Position.xz, camLocalPlaneRayDirection);
		}

		while (true) {
			World.RLEColumn column = world.GetVoxelColumn(ray.position);

			if (column.RunCount == 0) {
				goto SKIP_COLUMN;
			}

			float4 ddaIntersections = ray.Intersections; // xy last, zw next

			if ((rayStepCount++ & 31) == 31) {
				// periodically, check whether there are still pixels left that we can write to with full-world-columns
				// kinda hack to make the writable-pixel-bounds work with skybox pixels, and with reduced raybuffer heights due to looking up/down
				unsafe {
					if (!RareColumnAdjustment(ddaIntersections, ref nextFreePixel, (byte*)seenPixelCache.GetUnsafeReadOnlyPtr())) {
						break;
					}
				}
			}

			int iElement, iElementEnd;
			float2 elementBounds;

			if (elementIterationDirection >= 0) {
				iElement = 0;
				iElementEnd = column.runcount;
				elementBounds = world.DimensionY;
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				iElement = column.runcount - 1;
				iElementEnd = -1;
				elementBounds = 0;
			}

			for (; iElement != iElementEnd; iElement += elementIterationDirection) {
				World.RLEElement element = column.GetIndex(iElement);

				if (elementIterationDirection >= 0) {
					elementBounds = float2(elementBounds.x - element.Length, elementBounds.x);
				} else {
					elementBounds = float2(elementBounds.y, elementBounds.y + element.Length);
				}

				if (element.IsAir) {
					continue;
				}

				float3 bottomFront = float3(ddaIntersections.x, elementBounds.x, ddaIntersections.y);
				float3 topFront = float3(ddaIntersections.x, elementBounds.y, ddaIntersections.y);

				DrawLine(bottomFront, topFront, element.Length, 0f, ref nextFreePixel, seenPixelCache, rayColumn, element);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING; // wrote to the last pixels on screen - further writing will run out of bounds
				}

				if (topFront.y < camera.Position.y) {
					float3 topBehind = float3(ddaIntersections.z, elementBounds.y, ddaIntersections.w);
					DrawLine(topBehind, topFront, 0f, 0f, ref nextFreePixel, seenPixelCache, rayColumn, element);
				} else if (bottomFront.y > camera.Position.y) {
					float3 bottomBehind = float3(ddaIntersections.z, elementBounds.x, ddaIntersections.w);
					DrawLine(bottomBehind, bottomFront, element.Length, element.Length, ref nextFreePixel, seenPixelCache, rayColumn, element);
				}

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING; // wrote to the last pixels on screen - further writing will run out of bounds
				}
			}

			SKIP_COLUMN:

			ray.Step();

			if (ray.AtEnd) {
				break;
			}
		}

		STOP_TRACING:

		WriteSkybox(rayColumn, seenPixelCache);
	}

	void DrawLine (
		float3 a,
		float3 b,
		float uA, 
		float uB,
		ref int2 nextFreePixel,
		NativeArray<byte> seenPixelCache,
		NativeArray<ColorARGB32> rayColumn,
		World.RLEElement element
	) {
		camera.ProjectToHomogeneousCameraSpace(a, b, out float4 aCamSpace, out float4 bCamSpace);

		float2 uvA = float2(1f, uA) / aCamSpace.w;
		float2 uvB = float2(1f, uB) / bCamSpace.w;

		if (!camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace, ref uvA, ref uvB)) {
			return; // behind the camera
		}

		float2 rayBufferBoundsFloat = camera.ProjectClippedToScreen(aCamSpace, bCamSpace, screen, axisMappedToY);
		// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate in an increasing manner
		if (rayBufferBoundsFloat.x > rayBufferBoundsFloat.y) {
			Swap(ref rayBufferBoundsFloat.x, ref rayBufferBoundsFloat.y);
			Swap(ref uvA, ref uvB);
		}

		int2 rayBufferBounds = int2(round(rayBufferBoundsFloat));

		// check if the line overlaps with the area that's writable
		if (any(bool2(rayBufferBounds.y < nextFreePixel.x, rayBufferBounds.x > nextFreePixel.y))) {
			return;
		}

		// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
		ReducePixelHorizon(ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

		WriteLine(
			rayColumn,
			seenPixelCache,
			rayBufferBounds,
			rayBufferBoundsFloat,
			uvA,
			uvB,
			element
		);
	}

	static void Swap<T> (ref T a, ref T b)
	{
		T temp = a;
		a = b;
		b = temp;
	}

	/// <summary>
	/// Not inlined on purpose due to not running every DDA step.
	/// Passed a byte* because the NativeArray is a fat struct to pass along (it increases stack by 80 bytes compared to passing the pointer)
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	unsafe bool RareColumnAdjustment (float4 bothIntersections, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		float4 worldbounds = float4(0f, world.DimensionY + 1f, 0f, 0f);
		float3 bottom = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightX, ShuffleComponent.LeftY);
		float3 top = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightY, ShuffleComponent.LeftY);

		camera.ProjectToHomogeneousCameraSpace(bottom, top, out float4 lastBottom, out float4 lastTop);

		if (!camera.ClipHomogeneousCameraSpaceLine(ref lastBottom, ref lastTop)) {
			return false; // full world line is behind camera (wat)
		}
		float2 pixelBounds = camera.ProjectClippedToScreen(lastBottom, lastTop, screen, axisMappedToY);

		// we may be waiting to have pixels written outside of the working frustum, which won't happen
		pixelBounds = select(pixelBounds.xy, pixelBounds.yx, pixelBounds.x > pixelBounds.y);
		int2 screenYCoords = int2(round(pixelBounds));

		if (screenYCoords.x > nextFreePixel.x) {
			// there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
			// and further increase the bottom free pixel according to write mask
			nextFreePixel.x = screenYCoords.x;
			ReduceBoundsBottom(ref nextFreePixel, seenPixelCache);
		}

		if (screenYCoords.y < nextFreePixel.y) {
			nextFreePixel.y = screenYCoords.y;
			ReduceBoundsTop(ref nextFreePixel, seenPixelCache);
		}

		if (nextFreePixel.x > nextFreePixel.y) {
			return false; // apparently we've written all pixels we can reach now
		}
		return true;
	}

	unsafe void ReducePixelHorizon (ref int2 rayBufferBounds, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		if (rayBufferBounds.x <= nextFreePixel.x) {
			rayBufferBounds.x = nextFreePixel.x;
			if (rayBufferBounds.y >= nextFreePixel.x) {
				// so the bottom of this line was in the bottom written pixels, and the top was above those
				// extend the written pixels bottom with the ones we're writing now, and further extend them based on what we find in the seen pixels
				nextFreePixel.x = rayBufferBounds.y + 1;
				ReduceBoundsBottom(ref nextFreePixel, (byte*)seenPixelCache.GetUnsafeReadOnlyPtr());
			}
		}
		if (rayBufferBounds.y >= nextFreePixel.y) {
			rayBufferBounds.y = nextFreePixel.y;
			if (rayBufferBounds.x <= nextFreePixel.y) {
				nextFreePixel.y = rayBufferBounds.x - 1;
				ReduceBoundsTop(ref nextFreePixel, (byte*)seenPixelCache.GetUnsafeReadOnlyPtr());
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	unsafe void ReduceBoundsTop (ref int2 nextFreePixel, byte* seenPixelCache)
	{
		// checks the seenPixelCache and reduces the free pixels based on found written pixels
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

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	unsafe void ReduceBoundsBottom (ref int2 nextFreePixel, byte* seenPixelCache)
	{
		// checks the seenPixelCache and reduces the free pixels based on found written pixels
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

	void WriteLine (
		NativeArray<ColorARGB32> rayColumn,
		NativeArray<byte> seenPixelCache,
		int2 adjustedRayBufferBounds,
		float2 originalRayBufferBounds,
		float2 bottomUV,
		float2 topUV,
		World.RLEElement element
	) {
		for (int y = adjustedRayBufferBounds.x; y <= adjustedRayBufferBounds.y; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;

				float l = unlerp(originalRayBufferBounds.x, originalRayBufferBounds.y, y);
				float2 wu = lerp(bottomUV, topUV, l);
				// x is lerped 1/w, y is lerped u/w
				float u = wu.y / wu.x;

				rayColumn[y] = element.GetColor(clamp((int)floor(u), 0, element.Length - 1));
			}
		}
	}

	void WriteSkybox (NativeArray<ColorARGB32> rayColumn, NativeArray<byte> seenPixelCache)
	{
		// write skybox colors to unseen pixels
		ColorARGB32 skybox = new ColorARGB32(255, 0, 255);
		for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
			if (seenPixelCache[y] == 0) {
				rayColumn[y] = skybox;
			}
		}
	}

	bool IntersectScreen (float2 start, float2 dir, out float distance)
	{
		// AABB intersection
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