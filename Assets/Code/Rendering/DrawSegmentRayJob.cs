using System.Runtime.CompilerServices;
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

		float2 frustumYDerivatives; // will contain frustum top/bottom planes' Y derivatives, used to skip voxels outside of the frustum very early on
		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)segment.RayCount;
			float3 camLocalPlaneRayDirection = lerp(segment.CamLocalPlaneRayMin, segment.CamLocalPlaneRayMax, endRayLerp);
			ray = new SegmentDDAData(camera.Position.xz, camLocalPlaneRayDirection.xz);
			frustumYDerivatives = SetupFrustumBounds(endRayLerp, camLocalPlaneRayDirection);
		}

		while (true) {
			int2 frustumBounds = SetupColumnBounds(frustumYDerivatives, ray.IntersectionDistancesUnnormalized); // get the min/max voxel Y that is inside the frustum
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

			World.RLEColumn column = world.GetVoxelColumn(ray.position);

			int2 elementMinMax = int2(column.elementIndex, column.elementIndex + column.elementCount);
			if (elementIterationDirection < 0) {
				elementMinMax = elementMinMax.yx - 1; // reverse iteration order to render from top to bottom for correct depth results
			}

			for (int iElement = elementMinMax.x; iElement != elementMinMax.y; iElement += elementIterationDirection) {
				World.RLEElement element = world.WorldElements[iElement];

				if (any(bool2(element.Top < frustumBounds.x, element.Bottom > frustumBounds.y))) {
					continue; // outside of frustum
				}

				// if we can see the top/bottom of a voxel column, use the further away intersection with the column
				// this will render a diagonal through the column, which makes it look like you're rendering both faces at once
				// will break if you want per-face data
				// consider actually rendering 2 lines later on to prevent overdraw with stacked voxels
				float2 bottomIntersection = select(ddaIntersections.xy, ddaIntersections.zw, element.Bottom > camera.Position.y);
				float2 topIntersection = select(ddaIntersections.xy, ddaIntersections.zw, element.Top < camera.Position.y);
				float2 worldbounds = float2(element.Bottom, element.Top);
				float3 bottomWorld = shuffle(bottomIntersection, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightX, ShuffleComponent.LeftY);
				float3 topWorld = shuffle(topIntersection, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightY, ShuffleComponent.LeftY);

				camera.ProjectToHomogeneousCameraSpace(bottomWorld, topWorld, out float4 bottomHomo, out float4 topHomo);

				if (!camera.ClipHomogeneousCameraSpaceLine(bottomHomo, topHomo, out float4 bottomHomoClipped, out float4 topHomoClipped)) {
					continue; // behind the camera
				}

				float2 rayBufferBoundsFloat = camera.ProjectClippedToScreen(bottomHomoClipped, topHomoClipped, screen, axisMappedToY);
				// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate in an increasing manner
				rayBufferBoundsFloat = select(rayBufferBoundsFloat.xy, rayBufferBoundsFloat.yx, rayBufferBoundsFloat.x > rayBufferBoundsFloat.y);

				int2 rayBufferBounds = int2(round(rayBufferBoundsFloat));

				// check if the line overlaps with the area that's writable
				if (any(bool2(rayBufferBounds.y < nextFreePixel.x, rayBufferBounds.x > nextFreePixel.y))) {
					continue;
				}

				// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
				ReducePixelHorizon(ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

				WriteLine(rayColumn, rayBufferBounds, seenPixelCache, element.Color);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING; // wrote to the last pixels on screen - further writing will run out of bounds
				}
			}

			ray.Step();

			if (ray.AtEnd) {
				break;
			}
		}

		STOP_TRACING:

		WriteSkybox(rayColumn, seenPixelCache);
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

		if (!camera.ClipHomogeneousCameraSpaceLine(lastBottom, lastTop, out float4 lastBottomClipped, out float4 lastTopClipped)) {
			return false; // full world line is behind camera (wat)
		}
		float2 pixelBounds = camera.ProjectClippedToScreen(lastBottomClipped, lastTopClipped, screen, axisMappedToY);

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

	void WriteLine (NativeArray<ColorARGB32> rayColumn, int2 rayBufferBounds, NativeArray<byte> seenPixelCache, ColorARGB32 color)
	{
		for (int y = rayBufferBounds.x; y <= rayBufferBounds.y; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;
				rayColumn[y] = color;
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

	int2 SetupColumnBounds (float2 frustumYBounds, float2 intersectionDistances)
	{
		// calculate world space frustum bounds of the world column we're at
		float2 distances = float2(
			select(intersectionDistances.x, intersectionDistances.y, frustumYBounds.x >= 0f),
			select(intersectionDistances.x, intersectionDistances.y, frustumYBounds.y <= 0f)
		);
		float2 frustumYBoundsThisColumn = camera.Position.y + frustumYBounds * distances;
		int2 columnBounds;
		columnBounds.x = max(0, (int)floor(frustumYBoundsThisColumn.y));
		columnBounds.y = min(world.DimensionY, (int)ceil(frustumYBoundsThisColumn.x));
		return columnBounds;
	}

	float2 SetupFrustumBounds (float endRayLerp, float3 camLocalPlaneRayDirection)
	{
		// used to setup the derivatives of the min/max frustum rays for this DDA line
		float3 worldB;
		if (all(vanishingPointScreenSpace >= 0f & vanishingPointScreenSpace <= screen)) {
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