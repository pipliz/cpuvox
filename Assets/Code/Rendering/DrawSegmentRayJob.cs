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

			// TODO change the DDA ray setup so that we can do an intersection test with the world to find the first position instead of stepping to it
			RayContinuation cont = new RayContinuation
			{
				segment = rayContext.segment,
				ddaRay = rayContext.ddaRay,
				planeRayIndex = rayContext.planeRayIndex,
				rayColumn = segmentContext->activeRayBufferFull.GetRayColumn(rayContext.planeRayIndex + segmentContext->segmentRayIndexOffset),
				lod = 0,
			};

			World* world = drawContext.worldLODs;
			float farClip = drawContext.camera.FarClip;
			float lodMax = drawContext.camera.LODDistances[0];

			if (!World.REPEAT_WORLD) {
				// with a non-repeating world, we want to start inside the first grid position that is inside of the world
				// this means we can simply stop the ray later on if it runs outside of the world
				// (plus we know there's possible a lot of air out there)
				int2 dimensions = world->Dimensions.xz;
				int2 startPos = cont.ddaRay.Position;
				if (any(startPos < 0 | startPos >= dimensions)) {
					// so the start is outside of the limited world
					if (cont.ddaRay.StepToWorldIntersection(dimensions)) {
						while (true) {
							int2 rayPos = cont.ddaRay.Position;
							float2 diff = rayPos - cont.ddaRay.Start;
							if (dot(diff, diff) >= lodMax) {
								cont.ddaRay.NextLOD(1 << cont.lod);
								cont.lod++;
								world++;
								lodMax = drawContext.camera.LODDistances[cont.lod];
								continue;
							} else {
								break;
							}
						}

						if (cont.ddaRay.IsBeyondFarClip(farClip)) {
							WriteSkyboxFull(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, cont.rayColumn);
						} else {
							int rayIdx = System.Threading.Interlocked.Increment(ref *(int*)outRayCounter.GetUnsafePtr()) - 1;
							outRays[rayIdx] = cont;
						}
					} else {
						WriteSkyboxFull(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, cont.rayColumn);
					}
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
					ExecuteRayHorizontalInverted(ray, ref DrawingContext);
				} else {
					ExecuteRayVerticalInverted(ray, ref DrawingContext);
				}
			} else {
				if (ray.segment->axisMappedToY == 0) {
					ExecuteRayHorizontal(ray, ref DrawingContext);
				} else {
					ExecuteRayVertical(ray, ref DrawingContext);
				}
			}
		}
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	unsafe static void ExecuteRayHorizontalInverted (RayContinuation rayContext, ref DrawContext drawContext)
	{
		ExecuteRay(rayContext, ref drawContext, -1, 0);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	unsafe static void ExecuteRayVerticalInverted (RayContinuation rayContext, ref DrawContext drawContext)
	{
		ExecuteRay(rayContext, ref drawContext, -1, 1);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	unsafe static void ExecuteRayHorizontal (RayContinuation rayContext, ref DrawContext drawContext)
	{
		ExecuteRay(rayContext, ref drawContext, 1, 0);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	unsafe static void ExecuteRayVertical (RayContinuation rayContext, ref DrawContext drawContext)
	{
		ExecuteRay(rayContext, ref drawContext, 1, 1);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	unsafe static void ExecuteRay (RayContinuation rayContext, ref DrawContext drawContext, int ITERATION_DIRECTION, int Y_AXIS) {
		SegmentContext* segmentContext = rayContext.segment;
		int planeRayIndex = rayContext.planeRayIndex;
		SegmentDDAData ray = rayContext.ddaRay;
		ColorARGB32* rayColumn = rayContext.rayColumn;

		int lod = rayContext.lod;
		int voxelScale = 1 << lod;
		World* world = drawContext.worldLODs + lod;
		float farClip = drawContext.camera.FarClip;
		World.RLEColumn worldColumn = default;
		float lodMax = drawContext.camera.LODDistances[lod];

		byte* seenPixelCache = stackalloc byte[segmentContext->seenPixelCacheLength]; // turns out, stackalloc is zero-initialized with burst (for now?)

		int nextFreePixelMin = segmentContext->originalNextFreePixelMin;
		int nextFreePixelMax = segmentContext->originalNextFreePixelMax;

		float worldMaxY = world->DimensionY;
		float cameraPosYNormalized = drawContext.camera.PositionY / worldMaxY;

		// small offset to the frustums to prevent have a division by zero in the clipping algorithm
		float frustumBoundsMin = nextFreePixelMin - 0.501f;
		float frustumBoundsMax = nextFreePixelMax + 0.501f;

		SetupProjectedPlaneParams(
			ref drawContext.camera,
			ref ray,
			worldMaxY,
			voxelScale,
			drawContext.screen,
			out float4 planeStartBottomProjected,
			out float4 planeStartTopProjected,
			out float4 planeRayDirectionProjected
		);

		while (true) {
			int2 rayPos = ray.Position;
			{
				// check whether we're at the end of the LOD
				float2 diff = rayPos - ray.Start;
				if (dot(diff, diff) >= lodMax) {
					ray.NextLOD(voxelScale);
					lod++;
					voxelScale *= 2;
					world++;
					lodMax = drawContext.camera.LODDistances[lod];
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

			float worldBoundsMin = 0f;
			float worldBoundsMax = worldMaxY;

			if (ray.IntersectionDistances.x > 2f) {
				// determine the world/clip space min/max of the writable frustum

				// clip the projected-world-column to fit in the writable-frustum; adjust the worldBounds accordingly
				bool clippedLast = CameraData.GetWorldBoundsClippingCamSpace(
					camSpaceMinLast,
					camSpaceMaxLast,
					Y_AXIS,
					frustumBoundsMin,
					frustumBoundsMax,
					out float clipLastMinLerp,
					out float clipLastMaxLerp
				);

				bool clippedNext = CameraData.GetWorldBoundsClippingCamSpace(
					camSpaceMinNext,
					camSpaceMaxNext,
					Y_AXIS,
					frustumBoundsMin,
					frustumBoundsMax,
					out float clipNextMinLerp,
					out float clipNextMaxLerp
				);

				float camSpaceClippedMin, camSpaceClippedMax;
				// from the (clipped or not) camera space positions, get the following data:
				// min-max world space parts of the column visible - used to cull RLE elements early
				// min-max camera space parts visible - used to adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
				if (clippedLast) {
					if (clippedNext) {
						WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
						return;
					} else {
						worldBoundsMin = lerp(0f, worldMaxY, clipNextMinLerp);
						worldBoundsMax = lerp(0f, worldMaxY, clipNextMaxLerp);
						float4 minClip = lerp(camSpaceMinNext, camSpaceMaxNext, clipNextMinLerp);
						float4 maxClip = lerp(camSpaceMinNext, camSpaceMaxNext, clipNextMaxLerp);

						camSpaceClippedMin = minClip[Y_AXIS] / minClip.w;
						camSpaceClippedMax = maxClip[Y_AXIS] / maxClip.w;
						if (camSpaceClippedMax < camSpaceClippedMin) {
							Swap(ref camSpaceClippedMin, ref camSpaceClippedMax);
						}
					}
				} else {
					if (clippedNext) {
						worldBoundsMin = lerp(0f, worldMaxY, clipLastMinLerp);
						worldBoundsMax = lerp(0f, worldMaxY, clipLastMaxLerp);
						float4 minClip = lerp(camSpaceMinLast, camSpaceMaxLast, clipLastMinLerp);
						float4 maxClip = lerp(camSpaceMinLast, camSpaceMaxLast, clipLastMaxLerp);

						camSpaceClippedMin = minClip[Y_AXIS] / minClip.w;
						camSpaceClippedMax = maxClip[Y_AXIS] / maxClip.w;
						if (camSpaceClippedMax < camSpaceClippedMin) {
							Swap(ref camSpaceClippedMin, ref camSpaceClippedMax);
						}
					} else {
						worldBoundsMin = lerp(0f, worldMaxY, min(clipLastMinLerp, clipNextMinLerp));
						worldBoundsMax = lerp(0f, worldMaxY, max(clipLastMaxLerp, clipNextMaxLerp));

						float4 minClipA = lerp(camSpaceMinLast, camSpaceMaxLast, clipLastMinLerp);
						float4 maxClipA = lerp(camSpaceMinLast, camSpaceMaxLast, clipLastMaxLerp);

						float4 minClipB = lerp(camSpaceMinNext, camSpaceMaxNext, clipNextMinLerp);
						float4 maxClipB = lerp(camSpaceMinNext, camSpaceMaxNext, clipNextMaxLerp);

						float minNext = minClipB[Y_AXIS] / minClipB.w;
						float minLast = minClipA[Y_AXIS] / minClipA.w;
						float maxNext = maxClipB[Y_AXIS] / maxClipB.w;
						float maxLast = maxClipA[Y_AXIS] / maxClipA.w;

						if (maxNext < minNext) { Swap(ref maxNext, ref minNext); }
						if (maxLast < minLast) { Swap(ref maxLast, ref minLast); }

						camSpaceClippedMin = min(minLast, minNext);
						camSpaceClippedMax = max(maxLast, maxNext);
					}
				}

				worldBoundsMin = floor(worldBoundsMin);
				worldBoundsMax = ceil(worldBoundsMax);

				// adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
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

				{
					float worldMin = worldColumn.WorldMin;
					float worldMax = worldColumn.WorldMax;
					if (worldMin > worldBoundsMax || worldMax < worldBoundsMin) {
						// this column doesn't overlap the writable world bounds
						if (ray.Step(farClip)) {
							break;
						}
						continue;
					}
				}
			}

			float elementBoundsMin;
			float elementBoundsMax;
			World.RLEElement* elementPointer;

			if (ITERATION_DIRECTION > 0) {
				elementBoundsMin = worldMaxY;
				elementBoundsMax = worldMaxY;
				elementPointer = worldColumn.ElementGuardStart(ref world->Storage);
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				elementBoundsMin = 0f;
				elementBoundsMax = 0f;
				elementPointer = worldColumn.ElementGuardEnd(ref world->Storage);
			}

			ColorARGB32* worldColumnColors = worldColumn.ColorPointer(ref world->Storage);

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
				{
					float uA = element.Length;
					float uB = 0f;

					// check if it's in bounds clip-wise
					if (drawContext.camera.ClipHomogeneousCameraSpaceLine(ref camSpaceFrontBottom, ref camSpaceFrontTop, ref uA, ref uB)) {
						float2 uvA = float2(1f, uA) / camSpaceFrontBottom.w;
						float2 uvB = float2(1f, uB) / camSpaceFrontTop.w;

						float2 rayBufferBoundsFloat = drawContext.camera.ProjectClippedToScreen(camSpaceFrontBottom, camSpaceFrontTop, Y_AXIS);
						// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate pixels in ascending order

						if (rayBufferBoundsFloat.x > rayBufferBoundsFloat.y) {
							Swap(ref rayBufferBoundsFloat.x, ref rayBufferBoundsFloat.y);
							Swap(ref uvA, ref uvB);
						}

						int rayBufferBoundsMin = (int)round(rayBufferBoundsFloat.x);
						int rayBufferBoundsMax = (int)round(rayBufferBoundsFloat.y);

						// check if the line overlaps with the area that's writable
						if (rayBufferBoundsMax >= nextFreePixelMin && rayBufferBoundsMin <= nextFreePixelMax) {
							// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
							ReducePixelHorizon(
								segmentContext->originalNextFreePixelMin,
								segmentContext->originalNextFreePixelMax,
								ref rayBufferBoundsMin,
								ref rayBufferBoundsMax,
								ref nextFreePixelMin,
								ref nextFreePixelMax,
								seenPixelCache,
								ref frustumBoundsMin,
								ref frustumBoundsMax
							);

							for (int y = rayBufferBoundsMin; y <= rayBufferBoundsMax; y++) {
								// only write to unseen pixels; update those values as well
								if (seenPixelCache[y] == 0) {
									seenPixelCache[y] = 1;

									float l = unlerp(rayBufferBoundsFloat.x, rayBufferBoundsFloat.y, y);
									float2 wu = lerp(uvA, uvB, l);
									// x is lerped 1/w, y is lerped u/w
									float u = wu.y / wu.x;

									int colorIdx = clamp((int)floor(u), 0, element.Length - 1) + element.ColorsIndex;
									rayColumn[y] = worldColumnColors[colorIdx];
								}
							}

							if (nextFreePixelMin > nextFreePixelMax) {
								// wrote to the last pixels on screen - further writing will run out of bounds
								WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
								return;
							}
						}
					}
				}

				// depending on whether the element is above/below/besides us, draw the top/bottom of the element if needed
				float4 camSpaceSecondaryA;
				float4 camSpaceSecondaryB;
				ColorARGB32 secondaryColor;

				if (portionTop < cameraPosYNormalized) {
					if (elementBoundsMax > worldBoundsMax) {
						continue; // we should draw the top, but it's outside the world bounds so no
					}
					secondaryColor = worldColumnColors[element.ColorsIndex + 0];
					camSpaceSecondaryA = lerp(camSpaceMinNext, camSpaceMaxNext, portionTop);
					camSpaceSecondaryB = camSpaceFrontTop;
				} else if (portionBottom  > cameraPosYNormalized) {
					if (elementBoundsMin < worldBoundsMin) {
						continue;
					}
					secondaryColor = worldColumnColors[element.ColorsIndex + element.Length - 1];
					camSpaceSecondaryA = lerp(camSpaceMinNext, camSpaceMaxNext, portionBottom);
					camSpaceSecondaryB = camSpaceFrontBottom;
				} else {
					continue; // looking straight from the side - no need to draw either top or bottom
				}

				// draw the top/bottom
				if (drawContext.camera.ClipHomogeneousCameraSpaceLine(ref camSpaceSecondaryA, ref camSpaceSecondaryB)) {
					float2 rayBufferBoundsFloat = drawContext.camera.ProjectClippedToScreen(camSpaceSecondaryA, camSpaceSecondaryB, Y_AXIS);
					rayBufferBoundsFloat = round(rayBufferBoundsFloat);

					int rayBufferBoundsMin = (int)rayBufferBoundsFloat.x;
					int rayBufferBoundsMax = (int)rayBufferBoundsFloat.y;

					// flip bounds; there's multiple reasons why we could be rendering 'upside down', but we just want to iterate in an increasing manner
					if (rayBufferBoundsMin > rayBufferBoundsMax) {
						Swap(ref rayBufferBoundsMin, ref rayBufferBoundsMax);
					}

					// check if the line overlaps with the area that's writable
					if (rayBufferBoundsMax >= nextFreePixelMin && rayBufferBoundsMin <= nextFreePixelMax) {
						// reduce the "writable" pixel bounds if possible, and also clamp the rayBufferBounds to those pixel bounds
						ReducePixelHorizon(
							segmentContext->originalNextFreePixelMin,
							segmentContext->originalNextFreePixelMax,
							ref rayBufferBoundsMin,
							ref rayBufferBoundsMax,
							ref nextFreePixelMin,
							ref nextFreePixelMax,
							seenPixelCache,
							ref frustumBoundsMin,
							ref frustumBoundsMax
						);

						for (int y = rayBufferBoundsMin; y <= rayBufferBoundsMax; y++) {
							// only write to unseen pixels; update those values as well
							if (seenPixelCache[y] == 0) {
								seenPixelCache[y] = 1;
								rayColumn[y] = secondaryColor;
							}
						}

						if (nextFreePixelMin > nextFreePixelMax) {
							// wrote to the last pixels on screen - further writing will run out of bounds
							WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
							return;
						}
					}
				}
			}

			if (ray.Step(farClip)) {
				break;
			}
		}

		// reached far clip
		WriteSkybox(segmentContext->originalNextFreePixelMin, segmentContext->originalNextFreePixelMax, rayColumn, seenPixelCache);
	}

	static void SetupProjectedPlaneParams (
		ref CameraData camera,
		ref SegmentDDAData ray,
		float worldMaxY,
		int voxelScale,
		float2 screen,
		out float4 planeStartBottomProjected,
		out float4 planeStartTopProjected,
		out float4 planeRayDirectionProjected)
	{
		float2 start = ray.Start;
		float3 planeStartBottom = float3(start.x, 0f, start.y);
		float3 planeStartTop = float3(start.x, worldMaxY, start.y);
		float3 planeRayDirection = float3(ray.Direction.x, 0f, ray.Direction.y);

		planeStartTopProjected = camera.ProjectToHomogeneousCameraSpace(planeStartTop);
		planeStartBottomProjected = camera.ProjectToHomogeneousCameraSpace(planeStartBottom);
		planeRayDirectionProjected = camera.ProjectVectorToHomogeneousCameraSpace(planeRayDirection);
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
		byte* seenPixelCache,
		ref float frustumBoundsMin,
		ref float frustumBoundsMax)
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

				frustumBoundsMin = nextFreePixelMin - 0.501f;
			}
		}
		if (rayBufferBoundsMax >= nextFreePixelMax) {
			rayBufferBoundsMax = nextFreePixelMax;
			if (rayBufferBoundsMin <= nextFreePixelMax) {
				nextFreePixelMax = rayBufferBoundsMin - 1;

				while (nextFreePixelMax >= originalNextFreePixelMin && seenPixelCache[nextFreePixelMax] > 0) {
					nextFreePixelMax -= 1;
				}

				frustumBoundsMax = nextFreePixelMax + 0.501f;
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
