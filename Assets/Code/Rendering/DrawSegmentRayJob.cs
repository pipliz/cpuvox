﻿using Unity.Burst;
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
	[ReadOnly] public bool cameraLookingUp;
	[ReadOnly] public bool vanishingPointOnScreen;
	[ReadOnly] public World world;
	[ReadOnly] public CameraData camera;
	[ReadOnly] public float2 screen;
	[ReadOnly] public Unity.Profiling.ProfilerMarker markerRay;

	[NativeDisableParallelForRestriction]
	[NativeDisableContainerSafetyRestriction]
	[WriteOnly]
	public NativeArray<Color24> activeRayBuffer;

	public void Execute (int planeRayIndex)
	{
		markerRay.Begin();

		int rayStepCount = 0;
		int2 nextFreePixel = originalNextFreePixel;

		int rayBufferIdxStart = (planeRayIndex + rayIndexOffset) * activeRayBufferWidth;
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
			// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
			float4 intersections = ray.Intersections; // xy = last, zw = next

			int2 columnBounds = SetupColumnBounds(frustumYBounds, ray.IntersectionDistancesUnnormalized);

			if ((rayStepCount++ & 31) == 31) {
				AdjustOpenPixelsRange(columnBounds, intersections, ref nextFreePixel, seenPixelCache);
			}

			World.RLEColumn elements = world.GetVoxelColumn(ray.position);

			// need to iterate the elements from close to far vertically to not overwrite pixels
			int2 elementRange = select(int2(elements.Count - 1, -1), int2(0, elements.Count), cameraLookingUp);

			for (int iElement = elementRange.x; iElement != elementRange.y; iElement += elementIterationDirection) {
				World.RLEElement element = elements[iElement];

				if (any(bool2(element.Top < columnBounds.x, element.Bottom > columnBounds.y))) {
					continue;
				}

				GetWorldPositions(intersections, element.Top, element.Bottom, out float3 bottomWorld, out float3 topWorld);

				if (!camera.ProjectToScreen(topWorld, bottomWorld, screen, axisMappedToY, out float2 rayBufferBoundsFloat)) {
					continue; // behind the camera for some reason
				}

				int2 rayBufferBounds = int2(round(float2(cmin(rayBufferBoundsFloat), cmax(rayBufferBoundsFloat))));

				// check if the line overlaps with the area that's writable
				if (any(bool2(rayBufferBounds.y < nextFreePixel.x, rayBufferBounds.x > nextFreePixel.y))) {
					continue;
				}

				ExtendPixelHorizon(ref rayBufferBounds, ref nextFreePixel, seenPixelCache);
				WriteLine(rayBufferBounds, seenPixelCache, rayBufferIdxStart, element.Color);
			}

			ray.Step();

			bool4 endConditions = bool4(
				columnBounds.y < 0,
				columnBounds.x > world.DimensionY,
				nextFreePixel.x > nextFreePixel.y,
				ray.AtEnd
			);

			if (any(endConditions)) {
				break;
			}
		}

		WriteSkybox(seenPixelCache, rayBufferIdxStart);
		markerRay.End();
	}

	void GetWorldPositions (float4 intersections, int elementTop, int elementBottom, out float3 bottomWorld, out float3 topWorld)
	{
		topWorld = default;
		bottomWorld = default;

		topWorld.y = elementTop;
		bottomWorld.y = elementBottom - 1f;

		// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
		topWorld.xz = select(intersections.xy, intersections.zw, topWorld.y < camera.Position.y);
		bottomWorld.xz = select(intersections.xy, intersections.zw, bottomWorld.y > camera.Position.y);
	}

	void ExtendPixelHorizon (ref int2 rayBufferBounds, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		bool2 xle = rayBufferBounds.xx <= nextFreePixel.xy;
		bool2 ygt = rayBufferBounds.yy >= nextFreePixel.xy;
		rayBufferBounds = select(rayBufferBounds, nextFreePixel, bool2(xle.x, ygt.y));

		if (xle.x & ygt.x) {
			nextFreePixel.x = rayBufferBounds.y + 1;
			// try to extend the floating horizon further if we already wrote stuff there
			for (int y = nextFreePixel.x; y <= originalNextFreePixel.y; y++) {
				byte val = seenPixelCache[y];
				nextFreePixel.x += select(0, 1, val > 0);
				if (val == 0) { break; }
			}
		}
		if (ygt.y & xle.y) {
			nextFreePixel.y = rayBufferBounds.x - 1;
			// try to extend the floating horizon further if we already wrote stuff there
			for (int y = nextFreePixel.y; y >= originalNextFreePixel.x; y--) {
				byte val = seenPixelCache[y];
				nextFreePixel.y -= select(0, 1, val > 0);
				if (val == 0) { break; }
			}
		}
	}

	void WriteLine (int2 rayBufferBounds, NativeArray<byte> seenPixelCache, int rayBufferIdxStart, Color24 color)
	{
		for (int y = rayBufferBounds.x; y <= rayBufferBounds.y; y++) {
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;
				activeRayBuffer[rayBufferIdxStart + y] = color;
			}
		}
	}

	void WriteSkybox (NativeArray<byte> seenPixelCache, int rayBufferIdxStart)
	{

		Color24 skybox = new Color24(255, 0, 255);
		for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
			if (seenPixelCache[y] == 0) {
				activeRayBuffer[rayBufferIdxStart + y] = skybox;
			}
		}
		//Color24 skybox = new Color24(255, 0, 255);
		//for (int y = startNextFreeBottomPixel; y <= startNextFreeTopPixel; y++) {
		//	if (seenPixelCache[y] == 0) {
		//		activeRayBuffer[rayBufferIdxStart + y] = skybox;
		//	} else {
		//		Color24 col = activeRayBuffer[rayBufferIdxStart + y];
		//		col.r = (byte)clamp(rayStepCount, 0, 255);
		//		activeRayBuffer[rayBufferIdxStart + y] = col;
		//	}
		//}
	}

	bool AdjustOpenPixelsRange (int2 columnBounds, float4 intersections, ref int2 nextFreePixel, NativeArray<byte> seenPixelCache)
	{
		// project the column bounds to the raybuffer and adjust next free top/bottom pixels accordingly
		// we may be waiting to have pixels written outside of the working frustum, which won't happen

		GetWorldPositions(intersections, columnBounds.y, columnBounds.x, out float3 bottomWorld, out float3 topWorld);

		if (camera.ProjectToScreen(topWorld, bottomWorld, screen, axisMappedToY, out float2 screenYCoords)) {
			int rayBufferYBottom = Mathf.RoundToInt(cmin(screenYCoords));
			int rayBufferYTop = Mathf.RoundToInt(cmax(screenYCoords));

			if (rayBufferYBottom > nextFreePixel.x) {
				nextFreePixel.x = rayBufferYBottom; // there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
															// and further increase the bottom free pixel according to write mask
				for (int y = nextFreePixel.x; y <= originalNextFreePixel.y; y++) {
					byte val = seenPixelCache[y];
					nextFreePixel.x += select(0, 1, val > 0);
					if (val == 0) { break; }
				}
			}
			if (rayBufferYTop < nextFreePixel.y) {
				nextFreePixel.y = rayBufferYTop;
				for (int y = nextFreePixel.y; y >= originalNextFreePixel.x; y--) {
					byte val = seenPixelCache[y];
					nextFreePixel.y += select(0, -1, val > 0);
					if (val == 0) { break; }
				}
			}
			if (nextFreePixel.x > nextFreePixel.y) {
				return false; // apparently we've written all pixels we can reach now
			}
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