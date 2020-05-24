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
		[ReadOnly] public NativeArray<Context> contexts;
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
					context = (Context*)contexts.GetUnsafeReadOnlyPtr() + j,
					planeRayIndex = planeIndex
				};
				break;
			}
		}
	}

	public unsafe struct RayContext
	{
		public Context* context;
		public int planeRayIndex;
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	public struct RenderJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<RayContext> rays;

		[BurstCompile]
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public unsafe void Execute (int index)
		{
			RayContext ray = rays[index];

			// This if-else stuff combined with inlining of ExecuteRay effectively turns the ITERATION_DIRECTION and Y_AXIS parameters into constants
			// they're used a lot, so this noticeably impacts performance
			// lots of rays will end up taking the same path here
			// it does quadruple the created assembly code though :)
			if (ray.context->camera.InverseElementIterationDirection) {
				if (ray.context->axisMappedToY == 0) {
					ExecuteRay(ray.context, ray.planeRayIndex, -1, 0);
				} else {
					ExecuteRay(ray.context, ray.planeRayIndex, -1, 1);
				}
			} else {
				if (ray.context->axisMappedToY == 0) {
					ExecuteRay(ray.context, ray.planeRayIndex, 1, 0);
				} else {
					ExecuteRay(ray.context, ray.planeRayIndex, 1, 1);
				}
			}
		}
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	unsafe static void ExecuteRay (Context* context, int planeRayIndex, int ITERATION_DIRECTION, int Y_AXIS) {
		ColorARGB32* rayColumn = context->activeRayBufferFull.GetRayColumn(planeRayIndex + context->segmentRayIndexOffset);

		int lod = 0;
		int voxelScale = 1;
		World* world = context->worldLODs + lod;
		float farClip = context->camera.FarClip;

		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)context->segment.RayCount;
			float2 camLocalPlaneRayDirection = lerp(context->segment.CamLocalPlaneRayMin, context->segment.CamLocalPlaneRayMax, endRayLerp);
			camLocalPlaneRayDirection = normalize(camLocalPlaneRayDirection);
			ray = new SegmentDDAData(context->camera.PositionXZ, camLocalPlaneRayDirection);
		}

		World.RLEColumn worldColumn = default;

		int2 startPos = ray.Position;
		int lodMax = context->camera.LODDistances[0];

		// we do a pre-loop to check if we'll actually be going to hit voxels in the world
		// if we don't we can skip clearing the per-pixel-buffer and go to a dedicated full-skybox method that doesn't have to check the pixel buffer
		// also means that rendering from outside the world works, as the main loop exits when it is outside of it
		while (true) {
			int2 rayPos = ray.Position << lod;
			int2 diff = rayPos - startPos;
			if (dot(diff, diff) >= lodMax) {
				lod++;
				voxelScale *= 2;
				farClip /= 2f;
				ray.NextLOD();
				world++;
				lodMax = context->camera.LODDistances[lod];
			}

			if (world->GetVoxelColumn(rayPos, ref worldColumn) > 0) {
				break;
			}
			ray.Step();
			if (ray.AtEnd(farClip)) {
				WriteSkyboxFull(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn);
				return;
			}
		}

		byte* seenPixelCache = stackalloc byte[context->seenPixelCacheLength];
		UnsafeUtility.MemClear(seenPixelCache, context->seenPixelCacheLength);

		int nextFreePixelMin = context->originalNextFreePixelMin;
		int nextFreePixelMax = context->originalNextFreePixelMax;

		float worldMaxY = world->DimensionY;
		float cameraPosYNormalized = context->camera.PositionY / worldMaxY;
		float screenHeightInverse = 1f / context->screen[Y_AXIS];
		float2 frustumBounds = float2(-1f, 1f);

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

					lodMax = context->camera.LODDistances[lod];
				}
			}

			int columnRuns = world->GetVoxelColumn(rayPos, ref worldColumn);
			if (columnRuns == -1) {
				// out of world bounds
				WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
				return;
			}
			if (columnRuns == 0) {
				ray.Step();
				if (ray.AtEnd(farClip)) {
					break;
				}
				continue;
			}

			float4 ddaIntersections = ray.Intersections * voxelScale; // xy last, zw next

			// so, what we're going to do:
			// create 2 lines from minY to maxY, one at the last intersection one at the next intersection
			// project those to the screen, and adjust UVs with them to track how much of the world is on screen
			// after frustum culling we'll know the minY and maxY visible on screen
			// which we can then use to cull world RLE elements

			float3 worldMinLast = float3(ddaIntersections.x, 0f, ddaIntersections.y);
			float3 worldMaxLast = float3(ddaIntersections.x, worldMaxY, ddaIntersections.y);

			float3 worldMinNext = float3(ddaIntersections.z, 0f, ddaIntersections.w);
			float3 worldMaxNext = float3(ddaIntersections.z, worldMaxY, ddaIntersections.w);

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

			float worldBoundsMinLast = 0;
			float worldBoundsMinNext = 0;
			float worldBoundsMaxLast = worldMaxY - 1f;
			float worldBoundsMaxNext = worldMaxY - 1f;

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

			// from the (clipped or not) camera space positions, get the following data:
			// min-max world space parts of the column visible - used to cull RLE elements early
			// min-max camera space parts visible - used to adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
			if (clippedLast) {
				if (clippedNext) {
					if (lod == 0 && ray.IntersectionDistances.x < 4f) {
						// if we're very close to the camera, it could be that we're clipping because the column we're standing in is behind the near clip plane
						ray.Step();
						if (ray.AtEnd(farClip)) {
							break;
						}
						continue;
					} else {
						WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
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

			if (lod > 0 || ray.IntersectionDistances.x > 4f) {
				// adjust the writable pixel range, which can late-cull elements or cancel the ray entirely
				camSpaceClippedMin = (camSpaceClippedMin * 0.5f + 0.5f) * context->screen[Y_AXIS];
				camSpaceClippedMax = (camSpaceClippedMax * 0.5f + 0.5f) * context->screen[Y_AXIS];

				int writableMinPixel = (int)floor(camSpaceClippedMin);
				int writableMaxPixel = (int)ceil(camSpaceClippedMax);

				if (writableMaxPixel < nextFreePixelMin || writableMinPixel > nextFreePixelMax) {
					// world column doesn't overlap any writable pixels
					WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}

				if (writableMinPixel > nextFreePixelMin) {
					nextFreePixelMin = writableMinPixel;
					while (nextFreePixelMin <= context->originalNextFreePixelMax && seenPixelCache[nextFreePixelMin] > 0) {
						nextFreePixelMin += 1;
					}
				}
				if (writableMaxPixel < nextFreePixelMax) {
					nextFreePixelMax = writableMaxPixel;
					while (nextFreePixelMax >= context->originalNextFreePixelMin && seenPixelCache[nextFreePixelMax] > 0) {
						nextFreePixelMax -= 1;
					}
				}
				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}
			}

			worldBoundsMin = floor(worldBoundsMin);
			worldBoundsMax = ceil(worldBoundsMax);

			int iElement, iElementEnd;
			float elementBoundsMin;
			float elementBoundsMax;

			if (ITERATION_DIRECTION > 0) {
				iElement = 0;
				iElementEnd = columnRuns;
				elementBoundsMin = worldMaxY;
				elementBoundsMax = worldMaxY;
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				iElement = columnRuns - 1;
				iElementEnd = -1;
				elementBoundsMin = 0f;
				elementBoundsMax = 0f;
			}

			ColorARGB32* worldColumnColors = worldColumn.ColorPointer;

			for (; iElement != iElementEnd; iElement += ITERATION_DIRECTION) {
				World.RLEElement element = worldColumn.GetIndex(iElement);

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
				DrawLine(context, camSpaceFrontBottom, camSpaceFrontTop, element.Length, 0f, ref nextFreePixelMin, ref nextFreePixelMax, seenPixelCache, rayColumn, element, worldColumnColors, Y_AXIS);

				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
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

				DrawLine(context, camSpaceSecondaryA, camSpaceSecondaryB, ref nextFreePixelMin, ref nextFreePixelMax, seenPixelCache, rayColumn, element, secondaryColor, Y_AXIS);

				if (nextFreePixelMin > nextFreePixelMax) {
					// wrote to the last pixels on screen - further writing will run out of bounds
					WriteSkybox(context->originalNextFreePixelMin, context->originalNextFreePixelMax, rayColumn, seenPixelCache);
					return;
				}
			}

			// adjust the frustum we use to determine our world-space-frustum-bounds based on the free unwritten pixels
			frustumBounds = (int2(nextFreePixelMin - 1, nextFreePixelMax + 1) * float2(screenHeightInverse) - 0.5f) * 2f;

			ray.Step();

			if (ray.AtEnd(farClip)) {
				break;
			}
		}

		throw new System.Exception("Unreachable");
	}

	// draw the textured side of a RLE element
	static unsafe void DrawLine (
		Context* context,
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
		if (!context->camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace, ref uA, ref uB)) {
			return; // behind the camera
		}

		float2 uvA = float2(1f, uA) / aCamSpace.w;
		float2 uvB = float2(1f, uB) / bCamSpace.w;

		float2 rayBufferBoundsFloat = context->camera.ProjectClippedToScreen(aCamSpace, bCamSpace, context->screen, Y_AXIS);
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
			context->originalNextFreePixelMin,
			context->originalNextFreePixelMax,
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
		Context* context,
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
		if (!context->camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace)) {
			return; // behind the camera
		}

		float2 rayBufferBoundsFloat = context->camera.ProjectClippedToScreen(aCamSpace, bCamSpace, context->screen, Y_AXIS);
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
			context->originalNextFreePixelMin,
			context->originalNextFreePixelMax,
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

	public unsafe struct Context
	{
		public int originalNextFreePixelMin; // vertical pixel bounds in the raybuffer for this segment
		public int originalNextFreePixelMax;
		public int axisMappedToY; // top/bottom segment is 0, left/right segment is 1
		public World* worldLODs;
		public CameraData camera;
		public float2 screen;
		public RayBuffer.Native activeRayBufferFull;

		public RenderManager.SegmentData segment;
		public int segmentRayIndexOffset;

		public int seenPixelCacheLength;
	}
}
