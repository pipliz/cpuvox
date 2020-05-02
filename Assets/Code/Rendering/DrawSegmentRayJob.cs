using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

[BurstCompile]
public static class DrawSegmentRayJob
{
	const int RARE_COLUMN_ADJUST_THRESHOLD = 31; // must be chosen so that it equals some 2^x - 1 to work with masking

	unsafe delegate void ExecuteDelegate (Context* context, int planeRayIndex, byte* seenPixelCache);
	unsafe static readonly ExecuteDelegate ExecuteInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(ExecuteWrapper).Invoke;
	static readonly CustomSampler ExecuteSampler = CustomSampler.Create("DrawRay");

	public static void Initialize ()
	{
		return; // calls static constructor
	}

	public unsafe static void Execute (Context* context, int planeRayIndex, byte* seenPixelCache)
	{
		ExecuteSampler.Begin();
		ExecuteInvoker(context, planeRayIndex, seenPixelCache);
		ExecuteSampler.End();
	}

	[AOT.MonoPInvokeCallback(typeof(ExecuteDelegate))]
	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	unsafe static void ExecuteWrapper (Context* context, int planeRayIndex, byte* seenPixelCache)
	{
		if (context->camera.InverseElementIterationDirection) {
			if (context->axisMappedToY == 0) {
				ExecuteRay(context, planeRayIndex, seenPixelCache, -1, 0);
			} else {
				ExecuteRay(context, planeRayIndex, seenPixelCache, -1, 1);
			}
		} else {
			if (context->axisMappedToY == 0) {
				ExecuteRay(context, planeRayIndex, seenPixelCache, 1, 0);
			} else {
				ExecuteRay(context, planeRayIndex, seenPixelCache, 1, 1);
			}
		}
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	unsafe static void ExecuteRay (Context* context, int planeRayIndex, byte* seenPixelCache, int ITERATION_DIRECTION, int Y_AXIS) {
		ColorARGB32* rayColumn = context->activeRayBufferFull.GetRayColumn(planeRayIndex + context->segmentRayIndexOffset);

		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)context->segment.RayCount;
			float2 camLocalPlaneRayDirection = lerp(context->segment.CamLocalPlaneRayMin, context->segment.CamLocalPlaneRayMax, endRayLerp);
			ray = new SegmentDDAData(context->camera.PositionXZ, camLocalPlaneRayDirection);
		}

		World.RLEColumn worldColumn = default;

		while (context->world.GetVoxelColumn(ray.position, ref worldColumn) < 0) {
			// loop until we run into the first column of data, or until we reach the end (never hitting world data)
			ray.Step();
			if (ray.AtEnd) {
				goto STOP_TRACING_FILL_FULL_SKYBOX;
			}
		}

		UnsafeUtility.MemClear(seenPixelCache, context->seenPixelCacheLength);

		int2 nextFreePixel = context->originalNextFreePixel;
		float worldMaxY = context->world.DimensionY;
		float cameraPosYNormalized = context->camera.PositionY / worldMaxY;
		float screenHeightInverse = 1f / context->screen[Y_AXIS];
		float2 frustumBounds = float2(-1f, 1f);

		while (true) {
			int columnRuns = context->world.GetVoxelColumn(ray.position, ref worldColumn);
			if (columnRuns == -1) {
				goto STOP_TRACING_FILL_PARTIAL_SKYBOX;
			}
			if (columnRuns == 0) {
				goto SKIP_COLUMN;
			}

			float4 ddaIntersections = ray.Intersections; // xy last, zw next

			int iElement, iElementEnd;
			float2 elementBounds;

			float3 worldMinLast = float3(ddaIntersections.x, 0f, ddaIntersections.y);
			float3 worldMaxLast = float3(ddaIntersections.x, worldMaxY, ddaIntersections.y);

			float3 worldMinNext = float3(ddaIntersections.z, 0f, ddaIntersections.w);
			float3 worldMaxNext = float3(ddaIntersections.z, worldMaxY, ddaIntersections.w);

			float worldBoundsMinLast = 0;
			float worldBoundsMinNext = 0;
			float worldBoundsMaxLast = worldMaxY - 1f;
			float worldBoundsMaxNext = worldMaxY - 1f;

			context->camera.ProjectToHomogeneousCameraSpace(
				worldMinLast,
				worldMaxLast,
				out float4 camSpaceMinLast,
				out float4 camSpaceMaxLast
			);

			context->camera.ProjectToHomogeneousCameraSpace(
				worldMinNext,
				worldMaxNext,
				out float4 camSpaceMinNext,
				out float4 camSpaceMaxNext
			);

			float4 camSpaceMinLastClipped = camSpaceMinLast;
			float4 camSpaceMaxLastClipped = camSpaceMaxLast;

			bool clippedLast = context->camera.GetWorldBoundsClippingCamSpace(
				ref camSpaceMinLastClipped,
				ref camSpaceMaxLastClipped,
				Y_AXIS,
				ref worldBoundsMinLast,
				ref worldBoundsMaxLast,
				frustumBounds
			);

			float4 camSpaceMinNextClipped = camSpaceMinNext;
			float4 camSpaceMaxNextClipped = camSpaceMaxNext;

			bool clippedNext = context->camera.GetWorldBoundsClippingCamSpace(
				ref camSpaceMinNextClipped,
				ref camSpaceMaxNextClipped,
				Y_AXIS,
				ref worldBoundsMinNext,
				ref worldBoundsMaxNext,
				frustumBounds
			);

			float worldBoundsMin, worldBoundsMax;

			float camSpaceClippedMin, camSpaceClippedMax;

			if (clippedLast) {
				if (clippedNext) {
					if (ray.IntersectionDistancesUnnormalized.x < (4f / context->camera.FarClip)) {
						// if we're very close to the camera, it could be that we're clipping because the column we're standing in is behind the near clip plane
						goto SKIP_COLUMN;
					} else {
						goto STOP_TRACING_FILL_PARTIAL_SKYBOX;
					}
				} else {
					worldBoundsMin = worldBoundsMinNext;
					worldBoundsMax = worldBoundsMaxNext;
					camSpaceClippedMin = camSpaceMinNextClipped[Y_AXIS] / camSpaceMinNextClipped.w;
					camSpaceClippedMax = camSpaceMaxNextClipped[Y_AXIS] / camSpaceMaxNextClipped.w;
					if (camSpaceClippedMax < camSpaceClippedMin) {
						Swap(ref camSpaceClippedMin, ref camSpaceClippedMax);
					}
				}
			} else {
				if (clippedNext) {
					worldBoundsMin = worldBoundsMinLast;
					worldBoundsMax = worldBoundsMaxLast;
					camSpaceClippedMin = camSpaceMinLastClipped[Y_AXIS] / camSpaceMinLastClipped.w;
					camSpaceClippedMax = camSpaceMaxLastClipped[Y_AXIS] / camSpaceMaxLastClipped.w;
					if (camSpaceClippedMax < camSpaceClippedMin) {
						Swap(ref camSpaceClippedMin, ref camSpaceClippedMax);
					}
				} else {
					worldBoundsMin = min(worldBoundsMinLast, worldBoundsMinNext);
					worldBoundsMax = max(worldBoundsMaxLast, worldBoundsMaxNext);
					float minNext = camSpaceMinNextClipped[Y_AXIS] / camSpaceMinNextClipped.w;
					float minLast = camSpaceMinLastClipped[Y_AXIS] / camSpaceMinLastClipped.w;
					float maxNext = camSpaceMaxNextClipped[Y_AXIS] / camSpaceMaxNextClipped.w;
					float maxLast = camSpaceMaxLastClipped[Y_AXIS] / camSpaceMaxLastClipped.w;
					if (maxNext < minNext) { Swap(ref maxNext, ref minNext); }
					if (maxLast < minLast) { Swap(ref maxLast, ref minLast); }
					camSpaceClippedMin = min(minLast, minNext);
					camSpaceClippedMax = max(maxLast, maxNext);
				}
			}

			if (ray.IntersectionDistancesUnnormalized.x > (4f / context->camera.FarClip)) {
				camSpaceClippedMin = (camSpaceClippedMin * 0.5f + 0.5f) * context->screen[Y_AXIS];
				camSpaceClippedMax = (camSpaceClippedMax * 0.5f + 0.5f) * context->screen[Y_AXIS];

				int writableMinPixel = (int)floor(camSpaceClippedMin);
				int writableMaxPixel = (int)ceil(camSpaceClippedMax);

				if (writableMaxPixel < nextFreePixel.x || writableMinPixel > nextFreePixel.y) {
					goto STOP_TRACING_FILL_PARTIAL_SKYBOX; // world column doesn't overlap any writable pixels
				}

				if (writableMinPixel > nextFreePixel.x) {
					nextFreePixel.x = writableMinPixel;
					while (nextFreePixel.x <= context->originalNextFreePixel.y && seenPixelCache[nextFreePixel.x] > 0) {
						nextFreePixel.x += 1;
					}
				}
				if (writableMaxPixel < nextFreePixel.y) {
					nextFreePixel.y = writableMaxPixel;
					while (nextFreePixel.y >= context->originalNextFreePixel.x && seenPixelCache[nextFreePixel.y] > 0) {
						nextFreePixel.y -= 1;
					}
				}
				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING_FILL_PARTIAL_SKYBOX; // wrote to the last pixels on screen - further writing will run out of bounds
				}
			}

			worldBoundsMin = floor(worldBoundsMin);
			worldBoundsMax = ceil(worldBoundsMax);

			if (ITERATION_DIRECTION > 0) {
				iElement = 0;
				iElementEnd = columnRuns;
				elementBounds = worldMaxY;
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				iElement = columnRuns - 1;
				iElementEnd = -1;
				elementBounds = 0;
			}

			ColorARGB32* worldColumnColors = worldColumn.ColorPointer;

			for (; iElement != iElementEnd; iElement += ITERATION_DIRECTION) {
				World.RLEElement element = worldColumn.GetIndex(iElement);

				if (ITERATION_DIRECTION > 0) {
					elementBounds = float2(elementBounds.x - element.Length, elementBounds.x);
				} else {
					elementBounds = float2(elementBounds.y, elementBounds.y + element.Length);
				}

				if (element.IsAir) {
					continue;
				}

				if (elementBounds.x > worldBoundsMax || elementBounds.y < worldBoundsMin) {
					continue; // does not overlap the world frustum bounds
				}

				float portionBottom = unlerp(0f, worldMaxY, elementBounds.x);
				float portionTop = unlerp(0f, worldMaxY, elementBounds.y);

				float4 camSpaceFrontBottom = lerp(camSpaceMinLast, camSpaceMaxLast, portionBottom);
				float4 camSpaceFrontTop = lerp(camSpaceMinLast, camSpaceMaxLast, portionTop);

				DrawLine(context, camSpaceFrontBottom, camSpaceFrontTop, element.Length, 0f, ref nextFreePixel, seenPixelCache, rayColumn, element, worldColumnColors, Y_AXIS);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING_FILL_PARTIAL_SKYBOX; // wrote to the last pixels on screen - further writing will run out of bounds
				}

				float4 camSpaceSecondaryA;
				float4 camSpaceSecondaryB;
				ColorARGB32 secondaryColor;

				if (portionTop < cameraPosYNormalized) {
					secondaryColor = worldColumnColors[element.ColorsIndex + 0];
					camSpaceSecondaryA = lerp(camSpaceMinNext, camSpaceMaxNext, portionTop);
					camSpaceSecondaryB = camSpaceFrontTop;
				} else if (portionBottom  > cameraPosYNormalized) {
					secondaryColor = worldColumnColors[element.ColorsIndex + element.Length - 1];
					camSpaceSecondaryA = lerp(camSpaceMinNext, camSpaceMaxNext, portionBottom);
					camSpaceSecondaryB = camSpaceFrontBottom;
				} else {
					goto SKIP_SECONDARY_DRAW;
				}

				DrawLine(context, camSpaceSecondaryA, camSpaceSecondaryB, ref nextFreePixel, seenPixelCache, rayColumn, element, secondaryColor, Y_AXIS);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING_FILL_PARTIAL_SKYBOX; // wrote to the last pixels on screen - further writing will run out of bounds
				}
				SKIP_SECONDARY_DRAW:
				continue;
			}

			frustumBounds = ((nextFreePixel + int2(-1, 1)) * float2(screenHeightInverse) - 0.5f) * 2f;

			SKIP_COLUMN:

			ray.Step();

			if (ray.AtEnd) {
				break;
			}
		}

		STOP_TRACING_FILL_PARTIAL_SKYBOX:
		WriteSkybox(context->originalNextFreePixel, rayColumn, seenPixelCache);
		return;

		STOP_TRACING_FILL_FULL_SKYBOX:
		WriteSkyboxFull(context->originalNextFreePixel, rayColumn);
		return;
	}

	static unsafe void DrawLine (
		Context* context,
		float4 aCamSpace,
		float4 bCamSpace,
		float uA,
		float uB,
		ref int2 nextFreePixel,
		byte* seenPixelCache,
		ColorARGB32* rayColumn,
		World.RLEElement element,
		ColorARGB32* worldColumnColors,
		int Y_AXIS
	)
	{
		if (!context->camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace, ref uA, ref uB)) {
			return; // behind the camera
		}

		float2 uvA = float2(1f, uA) / aCamSpace.w;
		float2 uvB = float2(1f, uB) / bCamSpace.w;

		float2 rayBufferBoundsFloat = context->camera.ProjectClippedToScreen(aCamSpace, bCamSpace, context->screen, Y_AXIS);
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
		ReducePixelHorizon(context->originalNextFreePixel, ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

		WriteLine(
			rayColumn,
			seenPixelCache,
			rayBufferBounds,
			rayBufferBoundsFloat,
			uvA,
			uvB,
			element,
			worldColumnColors
		);
	}

	static unsafe void DrawLine (
		Context* context,
		float4 aCamSpace,
		float4 bCamSpace,
		ref int2 nextFreePixel,
		byte* seenPixelCache,
		ColorARGB32* rayColumn,
		World.RLEElement element,
		ColorARGB32 color,
		int Y_AXIS
	)
	{
		if (!context->camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace)) {
			return; // behind the camera
		}

		float2 rayBufferBoundsFloat = context->camera.ProjectClippedToScreen(aCamSpace, bCamSpace, context->screen, Y_AXIS);
		// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate in an increasing manner
		if (rayBufferBoundsFloat.x > rayBufferBoundsFloat.y) {
			Swap(ref rayBufferBoundsFloat.x, ref rayBufferBoundsFloat.y);
		}

		int2 rayBufferBounds = int2(round(rayBufferBoundsFloat));

		// check if the line overlaps with the area that's writable
		if (any(bool2(rayBufferBounds.y < nextFreePixel.x, rayBufferBounds.x > nextFreePixel.y))) {
			return;
		}

		// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
		ReducePixelHorizon(context->originalNextFreePixel, ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

		WriteLine(
			rayColumn,
			seenPixelCache,
			rayBufferBounds,
			rayBufferBoundsFloat,
			element,
			color
		);
	}

	static void Swap<T> (ref T a, ref T b)
	{
		T temp = a;
		a = b;
		b = temp;
	}

	static unsafe void ReducePixelHorizon (int2 originalNextFreePixel, ref int2 rayBufferBounds, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		if (rayBufferBounds.x <= nextFreePixel.x) {
			rayBufferBounds.x = nextFreePixel.x;
			if (rayBufferBounds.y >= nextFreePixel.x) {
				// so the bottom of this line was in the bottom written pixels, and the top was above those
				// extend the written pixels bottom with the ones we're writing now, and further extend them based on what we find in the seen pixels
				nextFreePixel.x = rayBufferBounds.y + 1;

				while (nextFreePixel.x <= originalNextFreePixel.y && seenPixelCache[nextFreePixel.x] > 0) {
					nextFreePixel.x += 1;
				}
			}
		}
		if (rayBufferBounds.y >= nextFreePixel.y) {
			rayBufferBounds.y = nextFreePixel.y;
			if (rayBufferBounds.x <= nextFreePixel.y) {
				nextFreePixel.y = rayBufferBounds.x - 1;

				while (nextFreePixel.y >= originalNextFreePixel.x && seenPixelCache[nextFreePixel.y] > 0) {
					nextFreePixel.y -= 1;
				}
			}
		}
	}

	static unsafe void WriteLine (
		ColorARGB32* rayColumn,
		byte* seenPixelCache,
		int2 adjustedRayBufferBounds,
		float2 originalRayBufferBounds,
		float2 bottomUV,
		float2 topUV,
		World.RLEElement element,
		ColorARGB32* worldColumnColors
	)
	{
		for (int y = adjustedRayBufferBounds.x; y <= adjustedRayBufferBounds.y; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;

				float l = unlerp(originalRayBufferBounds.x, originalRayBufferBounds.y, y);
				float2 wu = lerp(bottomUV, topUV, l);
				// x is lerped 1/w, y is lerped u/w
				float u = wu.y / wu.x;

				int colorIdx = clamp((int)floor(u), 0, element.Length - 1) + element.ColorsIndex;
				rayColumn[y] = worldColumnColors[colorIdx];
			}
		}
	}

	static unsafe void WriteLine (
		ColorARGB32* rayColumn,
		byte* seenPixelCache,
		int2 adjustedRayBufferBounds,
		float2 originalRayBufferBounds,
		World.RLEElement element,
		ColorARGB32 color
	)
	{
		for (int y = adjustedRayBufferBounds.x; y <= adjustedRayBufferBounds.y; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;
				rayColumn[y] = color;
			}
		}
	}

	static unsafe void WriteSkybox (int2 originalNextFreePixel, ColorARGB32* rayColumn, byte* seenPixelCache)
	{
		// write skybox colors to unseen pixels
		ColorARGB32 skybox = new ColorARGB32(25, 25, 25);
		for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
			if (seenPixelCache[y] == 0) {
				rayColumn[y] = skybox;
			}
		}
	}

	static unsafe void WriteSkyboxFull (int2 originalNextFreePixel, ColorARGB32* rayColumn)
	{
		// write skybox colors to unseen pixels
		ColorARGB32 skybox = new ColorARGB32(25, 25, 25);
		for (int y = originalNextFreePixel.x; y <= originalNextFreePixel.y; y++) {
			rayColumn[y] = skybox;
		}
	}

	public unsafe struct Context
	{
		public int2 originalNextFreePixel; // vertical pixel bounds in the raybuffer for this segment
		public int axisMappedToY; // top/bottom segment is 0, left/right segment is 1
		public World world;
		public CameraData camera;
		public float2 screen;
		public RayBuffer.Native activeRayBufferFull;

		public RenderManager.SegmentData segment;
		public int segmentRayIndexOffset;

		public int seenPixelCacheLength;
	}
}
