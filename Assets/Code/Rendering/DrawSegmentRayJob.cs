using Unity.Burst;
using Unity.Collections;
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
	[ReadOnly] public bool cameraLookingUp;
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

		float oneOverWorldYMax = 1f / (world.DimensionY + 1f);

		float4 lastBottom, lastTop;
		float4 nextBottom, nextTop;
		{
			// prepare the last position in the next storage, to set-up the swap chain
			float2 intersections = ray.LastIntersection;
			float3 bottom = float3(intersections.x, 0f, intersections.y);
			float3 top = float3(intersections.x, world.DimensionY + 1, intersections.y);
			camera.ProjectToHomogeneousCameraSpace(bottom, top, out nextBottom, out nextTop);
		}

		while (true) {
			// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
			float4 intersections = ray.Intersections; // xy = last, zw = next

			int2 columnBounds = SetupColumnBounds(frustumYBounds, ray.IntersectionDistancesUnnormalized);

			lastBottom = nextBottom;
			lastTop = nextTop;
			{
				float3 bottom = float3(intersections.z, 0f, intersections.w);
				float3 top = float3(intersections.z, world.DimensionY + 1, intersections.w);
				camera.ProjectToHomogeneousCameraSpace(bottom, top, out nextBottom, out nextTop);
			}

			if ((rayStepCount++ & 31) == 31) {
				if (!camera.ClipHomogeneousCameraSpaceLine(lastBottom, lastTop, out float4 lastBottomClipped, out float4 lastTopClipped)) {
					break; // full world line is behind camera (wat)
				}
				float2 pixelBounds = camera.ProjectClippedToScreen(lastBottomClipped, lastTopClipped, screen, axisMappedToY);

				if (!AdjustOpenPixelsRange(pixelBounds, ref nextFreePixel, seenPixelCache)) {
					break; // all pixels that this world column can project to are written to
				}
			}

			World.RLEColumn elements = world.GetVoxelColumn(ray.position);

			// need to iterate the elements from close to far vertically to not overwrite pixels
			int2 elementRange = select(int2(elements.Count - 1, -1), int2(0, elements.Count), cameraLookingUp);

			for (int iElement = elementRange.x; iElement != elementRange.y; iElement += elementIterationDirection) {
				World.RLEElement element = elements[iElement];

				if (any(bool2(element.Top < columnBounds.x, element.Bottom > columnBounds.y))) {
					continue;
				}
				
				float4 bottomHomo, topHomo;
				{
					bool c = element.Bottom > camera.Position.y;
					float4 a = select(lastBottom, nextBottom, c);
					float4 b = select(lastTop, nextTop, c);
					bottomHomo = lerp(a, b, element.Bottom * oneOverWorldYMax);

					c = element.Top + 1f < camera.Position.y;
					a = select(lastBottom, nextBottom, c);
					b = select(lastTop, nextTop, c);
					topHomo = lerp(a, b, (element.Top + 1) * oneOverWorldYMax);
				}

				if (!camera.ClipHomogeneousCameraSpaceLine(topHomo, bottomHomo, out float4 clippedHomoA, out float4 clippedHomoB)) {
					continue; // behind the camera
				}

				float2 rayBufferBoundsFloat = camera.ProjectClippedToScreen(clippedHomoA, clippedHomoB, screen, axisMappedToY);

				int2 rayBufferBounds = int2(round(
					float2(
						cmin(rayBufferBoundsFloat),
						cmax(rayBufferBoundsFloat)
					)
				));

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

	void ExtendPixelHorizon (ref int2 rayBufferBounds, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		bool2 xle = rayBufferBounds.xx <= nextFreePixel.xy;
		bool2 yge = rayBufferBounds.yy >= nextFreePixel.xy;
		rayBufferBounds = select(rayBufferBounds, nextFreePixel, bool2(xle.x, yge.y)); // x = max(x, pixel.x), y = min(y, pixel.y)

		if (xle.x & yge.x) {
			nextFreePixel.x = rayBufferBounds.y + 1;
			// try to extend the floating horizon further if we already wrote stuff there
			for (int y = nextFreePixel.x; y <= originalNextFreePixel.y; y++) {
				byte val = seenPixelCache[y];
				nextFreePixel.x += select(0, 1, val > 0);
				if (val == 0) { break; }
			}
		}
		if (yge.y & xle.y) {
			nextFreePixel.y = rayBufferBounds.x - 1;
			// try to extend the floating horizon further if we already wrote stuff there
			for (int y = nextFreePixel.y; y >= originalNextFreePixel.x; y--) {
				byte val = seenPixelCache[y];
				nextFreePixel.y -= select(0, 1, val > 0);
				if (val == 0) { break; }
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

	bool AdjustOpenPixelsRange (float2 screenYCoordsFloat, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		// we may be waiting to have pixels written outside of the working frustum, which won't happen
		int2 screenYCoords = int2(round(
			float2(
				cmin(screenYCoordsFloat),
				cmax(screenYCoordsFloat)
			)
		));

		if (screenYCoords.x > nextFreePixel.x) {
			nextFreePixel.x = screenYCoords.x; // there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
														// and further increase the bottom free pixel according to write mask
			for (int y = nextFreePixel.x; y <= originalNextFreePixel.y; y++) {
				byte val = seenPixelCache[y];
				nextFreePixel.x += select(0, 1, val > 0);
				if (val == 0) { break; }
			}
		}
		if (screenYCoords.y < nextFreePixel.y) {
			nextFreePixel.y = screenYCoords.y;
			for (int y = nextFreePixel.y; y >= originalNextFreePixel.x; y--) {
				byte val = seenPixelCache[y];
				nextFreePixel.y += select(0, -1, val > 0);
				if (val == 0) { break; }
			}
		}
		if (nextFreePixel.x > nextFreePixel.y) {
			return false; // apparently we've written all pixels we can reach now
		}
		return true;
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
		return float2(cmax(bounds), cmin(bounds));
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