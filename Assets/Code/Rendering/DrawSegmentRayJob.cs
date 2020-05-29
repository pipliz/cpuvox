using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;

[BurstCompile]
public static class DrawSegmentRayJob
{
	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	public struct RaySetupJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<SegmentContext> contexts;
		[WriteOnly] public NativeArray<RayContext> rays;

		[BurstCompile]
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public unsafe void Execute (int startIndex)
		{
			int planeIndex = startIndex;
			for (int j = 0; j < 4; j++) {
				int segmentRays = contexts[j].segment.RayCount;
				if (segmentRays <= 0) {
					continue;
				}
				if (planeIndex >= segmentRays) {
					planeIndex -= segmentRays;
					continue;
				}

				rays[startIndex] = new RayContext
				{
					context = (SegmentContext*)contexts.GetUnsafeReadOnlyPtr() + j,
					planeRayIndex = planeIndex
				};
				break;
			}
		}
	}

	public unsafe struct RayContext
	{
		public SegmentContext* context;
		public int planeRayIndex;
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	public unsafe struct DDASetupJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<RayContext> raysInput;
		[WriteOnly] public NativeArray<RayDDAContext> raysOutput;

		public DrawContext drawContext;

		[BurstCompile]
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public unsafe void Execute (int i)
		{
			float endRayLerp = raysInput[i].planeRayIndex / (float)raysInput[i].context->segment.RayCount;
			float2 camLocalPlaneRayDirection = lerp(
				raysInput[i].context->segment.CamLocalPlaneRayMin,
				raysInput[i].context->segment.CamLocalPlaneRayMax,
				endRayLerp
			);
			SegmentDDAData ray = new SegmentDDAData(
				drawContext.camera.PositionXZ,
				normalize(camLocalPlaneRayDirection)
			);
			raysOutput[i] = new RayDDAContext
			{
				segment = raysInput[i].context,
				planeRayIndex = raysInput[i].planeRayIndex,
				ddaRay = ray
			};
		}
	}

	public unsafe struct RayDDAContext
	{
		public SegmentContext* segment;
		public int planeRayIndex;
		public SegmentDDAData ddaRay;
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	public unsafe struct TraceToFirstColumnJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<RayDDAContext> inRays;
		[ReadOnly] public DrawContext drawContext;
		[WriteOnly, NativeDisableParallelForRestriction] public NativeArray<RayContinuation> outRays;
		public NativeArray<int> outRayCounter;

		[BurstCompile]
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public unsafe void Execute (int index)
		{
			RayDDAContext rayContext = inRays[index];
			SegmentContext* segmentContext = rayContext.segment;

			RayContinuation cont = new RayContinuation
			{
				segment = rayContext.segment,
				ddaRay = rayContext.ddaRay,
				planeRayIndex = rayContext.planeRayIndex,
				rayColumn = segmentContext->activeRayBufferFull.GetRayColumn(rayContext.planeRayIndex + segmentContext->segmentRayIndexOffset),
				lod = 0,
			};

			World* world = drawContext.worldLODs + cont.lod;
			float farClip = drawContext.camera.FarClip;
			int2 startPos = cont.ddaRay.Position;
			int lodMax = drawContext.camera.LODDistances[0];

			// we do a pre-loop to check if we'll actually be going to hit voxels in the world
			// if we don't we can skip clearing the per-pixel-buffer and go to a dedicated full-skybox method that doesn't have to check the pixel buffer
			// also means that rendering from outside the world works, as the main loop exits when it is outside of it
			while (true) {
				int2 rayPos = cont.ddaRay.Position << cont.lod;
				int2 diff = rayPos - startPos;
				if (dot(diff, diff) >= lodMax) {
					cont.lod++;
					farClip /= 2f;
					cont.ddaRay.NextLOD();
					world++;
					lodMax = drawContext.camera.LODDistances[cont.lod];
				}

				World.RLEColumn worldColumn = default;
				if (world->GetVoxelColumn(rayPos, ref worldColumn) > 0) {
					break;
				}
				if (cont.ddaRay.Step(farClip)) {
					WriteSkyboxFull(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, cont.rayColumn);
					return;
				}
			}

			int idx = System.Threading.Interlocked.Increment(ref *(int*)outRayCounter.GetUnsafePtr()) - 1;
			outRays[idx] = cont;
		}
	}

	public unsafe struct RayContinuation
	{
		public SegmentContext* segment;
		public ColorARGB32* rayColumn;
		public int planeRayIndex;
		public SegmentDDAData ddaRay;
		public int lod;
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	public unsafe struct RenderJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<RayContinuation> rays;
		[ReadOnly] public NativeArray<int> raysCount;

		[ReadOnly] public DrawContext DrawingContext;

		[BurstCompile]
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public unsafe void Execute (int index)
		{
			if (index >= raysCount[0]) { return; } // ray is culled in the previous job

			RayContinuation ray = rays[index];

			// This if-else stuff combined with inlining of ExecuteRay effectively turns the ITERATION_DIRECTION and Y_AXIS parameters into constants
			// they're used a lot, so this noticeably impacts performance
			// lots of rays will end up taking the same path here
			// it does quadruple the created assembly code though :)
			if (DrawingContext.camera.InverseElementIterationDirection) {
				if (ray.segment->axisMappedToY == 0) {
					ExecuteRay(ray, ref DrawingContext, -1, 0);
				} else {
					ExecuteRay(ray, ref DrawingContext, -1, 1);
				}
			} else {
				if (ray.segment->axisMappedToY == 0) {
					ExecuteRay(ray, ref DrawingContext, 1, 0);
				} else {
					ExecuteRay(ray, ref DrawingContext, 1, 1);
				}
			}
		}
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	unsafe static void ExecuteRay (RayContinuation rayContext, ref DrawContext drawContext, int ITERATION_DIRECTION, int Y_AXIS) {
		SegmentContext* segmentContext = rayContext.segment;
		int planeRayIndex = rayContext.planeRayIndex;
		SegmentDDAData ray = rayContext.ddaRay;
		ColorARGB32* rayColumn = rayContext.rayColumn;

		int lod = rayContext.lod;
		int lodScaler = 1 << lod;
		int voxelScale = 1 * lodScaler;
		World* world = drawContext.worldLODs + lod;
		float farClip = drawContext.camera.FarClip / lodScaler;
		World.RLEColumn worldColumn = default;
		int2 startPos = ray.Position;
		int lodMax = drawContext.camera.LODDistances[lod];

		byte* seenPixelCache = stackalloc byte[segmentContext->seenPixelCacheLength];
		UnsafeUtility.MemClear(seenPixelCache, segmentContext->seenPixelCacheLength);

		int nextFreePixelMin = segmentContext->originalNextFreePixelMin;
		int nextFreePixelMax = segmentContext->originalNextFreePixelMax;

		float worldMaxY = world->DimensionY;
		float cameraPosYNormalized = drawContext.camera.PositionY / worldMaxY;
		float screenHeightInverse = 1f / drawContext.screen[Y_AXIS];
		float2 frustumBounds = float2(-1f, 1f);

		SetupProjectedPlaneParams(
			ref drawContext.camera,
			ref ray,
			worldMaxY,
			1,
			out float4 planeStartBottomProjected,
			out float4 planeStartTopProjected,
			out float4 planeRayDirectionProjected
		);

		while (true) {
			int2 rayPos = ray.Position << lod;

			{
				// check whether we're at the end of the LOD
				int2 diff = rayPos - startPos;
				if (dot(diff, diff) >= lodMax) {
					lod++;
					voxelScale *= 2;
					farClip /= 2f;
					ray.NextLOD();
					world++;
					lodMax = drawContext.camera.LODDistances[lod];
					SetupProjectedPlaneParams(
						ref drawContext.camera,
						ref ray,
						worldMaxY,
						voxelScale,
						out planeStartBottomProjected,
						out planeStartTopProjected,
						out planeRayDirectionProjected
					);
				}
			}

			int columnRuns = world->GetVoxelColumn(rayPos, ref worldColumn);
			if (columnRuns == -1) {
				// out of world bounds
				WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
				return;
			}
			if (columnRuns == 0) {
				if (ray.Step(farClip)) {
					break;
				}
				continue;
			}

			// so, what we're going to do:
			// create 2 lines from minY to maxY, one at the last intersection one at the next intersection
			// project those to the screen, and adjust UVs with them to track how much of the world is on screen
			// after frustum culling we'll know the minY and maxY visible on screen
			// which we can then use to cull world RLE elements

			float4 camSpaceMinLast = planeStartBottomProjected + planeRayDirectionProjected * ray.IntersectionDistances.x;
			float4 camSpaceMinNext = planeStartBottomProjected + planeRayDirectionProjected * ray.IntersectionDistances.y;

			float4 camSpaceMaxLast = planeStartTopProjected + planeRayDirectionProjected * ray.IntersectionDistances.x;
			float4 camSpaceMaxNext = planeStartTopProjected + planeRayDirectionProjected * ray.IntersectionDistances.y;

			float worldBoundsMin, worldBoundsMax;
			float camSpaceClippedMin, camSpaceClippedMax;
			{
				// determine the world/clip space min/max of the writable frustum
				float4 camSpaceMinLastClipped = camSpaceMinLast;
				float4 camSpaceMaxLastClipped = camSpaceMaxLast;

				float worldBoundsMinLast = 0;
				float worldBoundsMinNext = 0;
				float worldBoundsMaxLast = worldMaxY - 1f;
				float worldBoundsMaxNext = worldMaxY - 1f;

				// clip the projected-world-column to fit in the writable-frustum; adjust the worldBounds accordingly
				bool clippedLast = drawContext.camera.GetWorldBoundsClippingCamSpace(
					ref camSpaceMinLastClipped,
					ref camSpaceMaxLastClipped,
					Y_AXIS,
					ref worldBoundsMinLast,
					ref worldBoundsMaxLast,
					frustumBounds
				);

				float4 camSpaceMinNextClipped = camSpaceMinNext;
				float4 camSpaceMaxNextClipped = camSpaceMaxNext;

				bool clippedNext = drawContext.camera.GetWorldBoundsClippingCamSpace(
					ref camSpaceMinNextClipped,
					ref camSpaceMaxNextClipped,
					Y_AXIS,
					ref worldBoundsMinNext,
					ref worldBoundsMaxNext,
					frustumBounds
				);

				// from the (clipped or not) camera space positions, get the following data:
				// min-max world space parts of the column visible - used to cull RLE elements early
				// min-max camera space parts visible - used to adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
				if (clippedLast) {
					if (clippedNext) {
						if (lod == 0 && ray.IntersectionDistances.x < 4f) {
							// if we're very close to the camera, it could be that we're clipping because the column we're standing in is behind the near clip plane
							if (ray.Step(farClip)) {
								break;
							}
							continue;
						} else {
							WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
							return;
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
			}

			if (lod > 0 || ray.IntersectionDistances.x > 4f) {
				// adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
				camSpaceClippedMin = (camSpaceClippedMin * 0.5f + 0.5f) * drawContext.screen[Y_AXIS];
				camSpaceClippedMax = (camSpaceClippedMax * 0.5f + 0.5f) * drawContext.screen[Y_AXIS];

				int writableMinPixel = (int)floor(camSpaceClippedMin);
				int writableMaxPixel = (int)ceil(camSpaceClippedMax);

				if (writableMaxPixel < nextFreePixelMin || writableMinPixel > nextFreePixelMax) {
					// world column doesn't overlap any writable pixels
					WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}

				if (writableMinPixel > nextFreePixelMin) {
					nextFreePixelMin = writableMinPixel;
					while (nextFreePixelMin <= segmentContext->originalNextFreePixelMax && seenPixelCache[nextFreePixelMin] > 0) {
						nextFreePixelMin += 1;
					}
				}
				if (writableMaxPixel < nextFreePixelMax) {
					nextFreePixelMax = writableMaxPixel;
					while (nextFreePixelMax >= segmentContext->originalNextFreePixelMin && seenPixelCache[nextFreePixelMax] > 0) {
						nextFreePixelMax -= 1;
					}
				}
				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}
			}

			worldBoundsMin = floor(worldBoundsMin);
			worldBoundsMax = ceil(worldBoundsMax);

			float elementBoundsMin;
			float elementBoundsMax;
			World.RLEElement* elementPointer;

			if (ITERATION_DIRECTION > 0) {
				elementBoundsMin = worldMaxY;
				elementBoundsMax = worldMaxY;
				elementPointer = worldColumn.ElementGuardStart;
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				elementBoundsMin = 0f;
				elementBoundsMax = 0f;
				elementPointer = worldColumn.ElementGuardEnd;
			}

			ColorARGB32* worldColumnColors = worldColumn.ColorPointer;

			while (true) {
				if (ITERATION_DIRECTION > 0) {
					elementPointer++;
				} else {
					elementPointer--;
				}
				// normally we'd do the line below; but current burst has a bug with this ( https://forum.unity.com/threads/burst-compiler-cant-do-pointer-1-or-pointer-1.900434/ )
				// elementPointer += ITERATION_DIRECTION;

				World.RLEElement element = *elementPointer;
				if (!element.IsValid) {
					break;
				}

				if (ITERATION_DIRECTION > 0) {
					elementBoundsMax = elementBoundsMin;
					elementBoundsMin = elementBoundsMin - element.Length * voxelScale;
				} else {
					elementBoundsMin = elementBoundsMax;
					elementBoundsMax = elementBoundsMin + element.Length * voxelScale;
				}

				if (element.IsAir) {
					continue;
				}

				if (elementBoundsMin > worldBoundsMax) {
					if (ITERATION_DIRECTION < 0) {
						break; // bottom of the row is above the world, and we are iterating from the bottom to the top -> done
					} else {
						continue;
					}
				}

				if (elementBoundsMax < worldBoundsMin) {
					if (ITERATION_DIRECTION > 0) {
						break; // top of this row is below the world, and we are iterating from the top to the bottom -> done
					} else {
						continue;
					}
				}

				// we can re-use the projected full-world-lines by just lerping the camera space positions
				float portionBottom = unlerp(0f, worldMaxY, elementBoundsMin);
				float portionTop = unlerp(0f, worldMaxY, elementBoundsMax);
				float4 camSpaceFrontBottom = lerp(camSpaceMinLast, camSpaceMaxLast, portionBottom);
				float4 camSpaceFrontTop = lerp(camSpaceMinLast, camSpaceMaxLast, portionTop);

				// draw the side of the RLE elements
				DrawLine(ref drawContext, segmentContext, camSpaceFrontBottom, camSpaceFrontTop, element.Length, 0f, ref nextFreePixelMin, ref nextFreePixelMax, seenPixelCache, rayColumn, element, worldColumnColors, Y_AXIS);

				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}

				// depending on whether the element is above/below/besides us, draw the top/bottom of the element if needed
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
					continue;
				}

				DrawLine(ref drawContext, segmentContext, camSpaceSecondaryA, camSpaceSecondaryB, ref nextFreePixelMin, ref nextFreePixelMax, seenPixelCache, rayColumn, element, secondaryColor, Y_AXIS);

				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}
			}

			// adjust the frustum we use to determine our world-space-frustum-bounds based on the free unwritten pixels
			frustumBounds = (int2(nextFreePixelMin - 1, nextFreePixelMax + 1) * float2(screenHeightInverse) - 0.5f) * 2f;

			if (ray.Step(farClip)) {
				break;
			}
		}

		throw new System.Exception("Unreachable");
	}

	static void SetupProjectedPlaneParams (
		ref CameraData camera,
		ref SegmentDDAData ray,
		float worldMaxY,
		int voxelScale,
		out float4 planeStartBottomProjected,
		out float4 planeStartTopProjected,
		out float4 planeRayDirectionProjected)
	{


		float2 start = ray.Start * voxelScale;
		float3 planeStartBottom = float3(start.x, 0f, start.y);
		float3 planeStartTop = float3(start.x, worldMaxY, start.y);
		float3 planeRayDirection = float3(ray.Direction.x, 0f, ray.Direction.y);

		planeStartTopProjected = camera.ProjectToHomogeneousCameraSpace(planeStartTop);
		planeStartBottomProjected = camera.ProjectToHomogeneousCameraSpace(planeStartBottom);
		planeRayDirectionProjected = camera.ProjectToHomogeneousCameraSpace(planeStartBottom + planeRayDirection * voxelScale) - planeStartBottomProjected;
	}

	// draw the textured side of a RLE element
	static unsafe void DrawLine (
		ref DrawContext drawContext,
		SegmentContext* segmentContext,
		float4 aCamSpace,
		float4 bCamSpace,
		float uA,
		float uB,
		ref int nextFreePixelMin,
		ref int nextFreePixelMax,
		byte* seenPixelCache,
		ColorARGB32* rayColumn,
		World.RLEElement element,
		ColorARGB32* worldColumnColors,
		int Y_AXIS
	)
	{
		if (!drawContext.camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace, ref uA, ref uB)) {
			return; // behind the camera
		}

		float2 uvA = float2(1f, uA) / aCamSpace.w;
		float2 uvB = float2(1f, uB) / bCamSpace.w;

		float2 rayBufferBoundsFloat = drawContext.camera.ProjectClippedToScreen(aCamSpace, bCamSpace, drawContext.screen, Y_AXIS);
		// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate pixels in ascending order

		if (rayBufferBoundsFloat.x > rayBufferBoundsFloat.y) {
			Swap(ref rayBufferBoundsFloat.x, ref rayBufferBoundsFloat.y);
			Swap(ref uvA, ref uvB);
		}

		int rayBufferBoundsMin = (int)round(rayBufferBoundsFloat.x);
		int rayBufferBoundsMax = (int)round(rayBufferBoundsFloat.y);

		// check if the line overlaps with the area that's writable
		if (any(bool2(rayBufferBoundsMax < nextFreePixelMin, rayBufferBoundsMin > nextFreePixelMax))) {
			return;
		}

		// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
		ReducePixelHorizon(
			segmentContext->originalNextFreePixelMin,
			segmentContext->originalNextFreePixelMax,
			ref rayBufferBoundsMin,
			ref rayBufferBoundsMax,
			ref nextFreePixelMin,
			ref nextFreePixelMax,
			seenPixelCache
		);

		WriteLine(
			rayColumn,
			seenPixelCache,
			rayBufferBoundsMin,
			rayBufferBoundsMax,
			rayBufferBoundsFloat.x,
			rayBufferBoundsFloat.y,
			uvA,
			uvB,
			element,
			worldColumnColors
		);
	}

	static unsafe void DrawLine (
		ref DrawContext drawContext,
		SegmentContext* segmentContext,
		float4 aCamSpace,
		float4 bCamSpace,
		ref int nextFreePixelMin,
		ref int nextFreePixelMax,
		byte* seenPixelCache,
		ColorARGB32* rayColumn,
		World.RLEElement element,
		ColorARGB32 color,
		int Y_AXIS
	)
	{
		if (!drawContext.camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace)) {
			return; // behind the camera
		}

		float2 rayBufferBoundsFloat = drawContext.camera.ProjectClippedToScreen(aCamSpace, bCamSpace, drawContext.screen, Y_AXIS);
		rayBufferBoundsFloat = round(rayBufferBoundsFloat);

		int rayBufferBoundsMin = (int)rayBufferBoundsFloat.x;
		int rayBufferBoundsMax = (int)rayBufferBoundsFloat.y;

		// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate in an increasing manner
		if (rayBufferBoundsMin > rayBufferBoundsMax) {
			Swap(ref rayBufferBoundsMin, ref rayBufferBoundsMax);
		}

		// check if the line overlaps with the area that's writable
		if (any(bool2(rayBufferBoundsMax < nextFreePixelMin, rayBufferBoundsMin > nextFreePixelMax))) {
			return;
		}

		// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
		ReducePixelHorizon(
			segmentContext->originalNextFreePixelMin,
			segmentContext->originalNextFreePixelMax,
			ref rayBufferBoundsMin,
			ref rayBufferBoundsMax,
			ref nextFreePixelMin,
			ref nextFreePixelMax,
			seenPixelCache
		);

		WriteLine(
			rayColumn,
			seenPixelCache,
			rayBufferBoundsMin,
			rayBufferBoundsMax,
			color
		);
	}

	static void Swap<T> (ref T a, ref T b)
	{
		T temp = a;
		a = b;
		b = temp;
	}

	static unsafe void ReducePixelHorizon (
		int originalNextFreePixelMin,
		int originalNextFreePixelMax,
		ref int rayBufferBoundsMin,
		ref int rayBufferBoundsMax,
		ref int nextFreePixelMin,
		ref int nextFreePixelMax,
		byte* seenPixelCache)
	{
		if (rayBufferBoundsMin <= nextFreePixelMin) {
			rayBufferBoundsMin = nextFreePixelMin;
			if (rayBufferBoundsMax >= nextFreePixelMin) {
				// so the bottom of this line was in the bottom written pixels, and the top was above those
				// extend the written pixels bottom with the ones we're writing now, and further extend them based on what we find in the seen pixels
				nextFreePixelMin = rayBufferBoundsMax + 1;

				while (nextFreePixelMin <= originalNextFreePixelMax && seenPixelCache[nextFreePixelMin] > 0) {
					nextFreePixelMin += 1;
				}
			}
		}
		if (rayBufferBoundsMax >= nextFreePixelMax) {
			rayBufferBoundsMax = nextFreePixelMax;
			if (rayBufferBoundsMin <= nextFreePixelMax) {
				nextFreePixelMax = rayBufferBoundsMin - 1;

				while (nextFreePixelMax >= originalNextFreePixelMin && seenPixelCache[nextFreePixelMax] > 0) {
					nextFreePixelMax -= 1;
				}
			}
		}
	}

	/// <summary>
	/// 'UV' mapped version, taking a color from the color pointer per element
	/// </summary>
	static unsafe void WriteLine (
		ColorARGB32* rayColumn,
		byte* seenPixelCache,
		int adjustedRayBufferBoundsMin,
		int adjustedRayBufferBoundsMax,
		float originalRayBufferBoundsMin,
		float originalRayBufferBoundsMax,
		float2 bottomUV,
		float2 topUV,
		World.RLEElement element,
		ColorARGB32* worldColumnColors
	)
	{
		for (int y = adjustedRayBufferBoundsMin; y <= adjustedRayBufferBoundsMax; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;

				float l = unlerp(originalRayBufferBoundsMin, originalRayBufferBoundsMax, y);
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
		int adjustedRayBufferBoundsMin,
		int adjustedRayBufferBoundsMax,
		ColorARGB32 color
	)
	{
		for (int y = adjustedRayBufferBoundsMin; y <= adjustedRayBufferBoundsMax; y++) {
			// only write to unseen pixels; update those values as well
			if (seenPixelCache[y] == 0) {
				seenPixelCache[y] = 1;
				rayColumn[y] = color;
			}
		}
	}

	static unsafe void WriteSkybox (int originalNextFreePixelMin, int originalNextFreePixelMax, ColorARGB32* rayColumn, byte* seenPixelCache)
	{
		// write skybox colors to unseen pixels
		ColorARGB32 skybox = new ColorARGB32(25, 25, 25);
		for (int y = originalNextFreePixelMin; y <= originalNextFreePixelMax; y++) {
			if (seenPixelCache[y] == 0) {
				rayColumn[y] = skybox;
			}
		}
	}

	static unsafe void WriteSkyboxFull (int originalNextFreePixelMin, int originalNextFreePixelMax, ColorARGB32* rayColumn)
	{
		ColorARGB32 skybox = new ColorARGB32(25, 25, 25);
		for (int y = originalNextFreePixelMin; y <= originalNextFreePixelMax; y++) {
			rayColumn[y] = skybox;
		}
	}

	public unsafe struct SegmentContext
	{
		public RayBuffer.Native activeRayBufferFull;
		public RenderManager.SegmentData segment;
		public int originalNextFreePixelMin; // vertical pixel bounds in the raybuffer for this segment
		public int originalNextFreePixelMax;
		public int axisMappedToY; // top/bottom segment is 0, left/right segment is 1
		public int segmentRayIndexOffset;
		public int seenPixelCacheLength;
	}

	public unsafe struct DrawContext
	{
		[NativeDisableUnsafePtrRestriction] public World* worldLODs;
		public CameraData camera;
		public float2 screen;
	}
}
