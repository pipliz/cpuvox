using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

[BurstCompile]
public static class DrawSegmentRayJob
{
	const int RARE_COLUMN_ADJUST_THRESHOLD = 31; // must be chosen so that it equals some 2^x - 1 to work with masking

	unsafe delegate void ExecuteDelegate (ref Context context, int planeRayIndex, byte* seenPixelCache);
	unsafe static readonly ExecuteDelegate ExecuteInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(ExecuteInternal).Invoke;
	static readonly CustomSampler ExecuteSampler = CustomSampler.Create("DrawRay");

	public static void Initialize ()
	{
		return; // calls static constructor
	}

	public unsafe static void Execute (ref Context context, int planeRayIndex, byte* seenPixelCache)
	{
		ExecuteSampler.Begin();
		ExecuteInvoker(ref context, planeRayIndex, seenPixelCache);
		ExecuteSampler.End();
	}

	[AOT.MonoPInvokeCallbackAttribute(typeof(ExecuteDelegate))]
	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	unsafe static void ExecuteInternal (ref Context context, int planeRayIndex, byte* seenPixelCache)
	{
		ColorARGB32* rayColumn = context.activeRayBufferFull.GetRayColumn(planeRayIndex + context.segmentRayIndexOffset);

		int rayStepCount = 0;
		int2 nextFreePixel = context.originalNextFreePixel;

		UnsafeUtility.MemClear(seenPixelCache, context.seenPixelCacheLength);

		SegmentDDAData ray;
		{
			float endRayLerp = planeRayIndex / (float)context.segment.RayCount;
			float2 camLocalPlaneRayDirection = lerp(context.segment.CamLocalPlaneRayMin, context.segment.CamLocalPlaneRayMax, endRayLerp);
			ray = new SegmentDDAData(context.camera.PositionXZ, camLocalPlaneRayDirection);
		}

		while (true) {
			World.RLEColumn column = context.world.GetVoxelColumn(ray.position);

			if (column.RunCount == 0) {
				goto SKIP_COLUMN;
			}

			float4 ddaIntersections = ray.Intersections; // xy last, zw next

			if ((rayStepCount++ & 31) == 31) {
				// periodically, check whether there are still pixels left that we can write to with full-world-columns
				// kinda hack to make the writable-pixel-bounds work with skybox pixels, and with reduced raybuffer heights due to looking up/down
				if (!RareColumnAdjustment(ref context, ddaIntersections, ref nextFreePixel, seenPixelCache)) {
					break;
				}
			}

			int iElement, iElementEnd;
			float2 elementBounds;

			if (context.camera.CameraDepthIterationDirection >= 0) {
				iElement = 0;
				iElementEnd = column.runcount;
				elementBounds = context.world.DimensionY;
			} else {
				// reverse iteration order to render from bottom to top for correct depth results
				iElement = column.runcount - 1;
				iElementEnd = -1;
				elementBounds = 0;
			}

			ColorARGB32* worldColumnColors = column.ColorPointer;

			for (; iElement != iElementEnd; iElement += context.camera.CameraDepthIterationDirection) {
				World.RLEElement element = column.GetIndex(iElement);

				if (context.camera.CameraDepthIterationDirection >= 0) {
					elementBounds = float2(elementBounds.x - element.Length, elementBounds.x);
				} else {
					elementBounds = float2(elementBounds.y, elementBounds.y + element.Length);
				}

				if (element.IsAir) {
					continue;
				}

				float3 bottomFront = float3(ddaIntersections.x, elementBounds.x, ddaIntersections.y);
				float3 topFront = float3(ddaIntersections.x, elementBounds.y, ddaIntersections.y);

				DrawLine(ref context, bottomFront, topFront, element.Length, 0f, ref nextFreePixel, seenPixelCache, rayColumn, element, worldColumnColors);

				if (nextFreePixel.x > nextFreePixel.y) {
					goto STOP_TRACING; // wrote to the last pixels on screen - further writing will run out of bounds
				}

				float3 secondaryA = default;
				float3 secondaryB = default;
				float secondaryUV = default;

				if (topFront.y < context.camera.PositionY) {
					secondaryUV = 0f;
					secondaryA = float3(ddaIntersections.z, elementBounds.y, ddaIntersections.w);
					secondaryB = topFront;
				} else if (bottomFront.y > context.camera.PositionY) {
					secondaryUV = element.Length;
					secondaryA = float3(ddaIntersections.z, elementBounds.x, ddaIntersections.w);
					secondaryB = bottomFront;
				} else {
					goto SKIP_SECONDARY_DRAW;
				}

				DrawLine(ref context, secondaryA, secondaryB, secondaryUV, secondaryUV, ref nextFreePixel, seenPixelCache, rayColumn, element, worldColumnColors);

				SKIP_SECONDARY_DRAW:
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

		WriteSkybox(context.originalNextFreePixel, rayColumn, seenPixelCache);
	}

	static unsafe void DrawLine (
		ref Context context,
		float3 a,
		float3 b,
		float uA,
		float uB,
		ref int2 nextFreePixel,
		byte* seenPixelCache,
		ColorARGB32* rayColumn,
		World.RLEElement element,
		ColorARGB32* worldColumnColors
	)
	{
		context.camera.ProjectToHomogeneousCameraSpace(a, b, out float4 aCamSpace, out float4 bCamSpace);

		float2 uvA = float2(1f, uA);
		float2 uvB = float2(1f, uB);

		if (!context.camera.ClipHomogeneousCameraSpaceLine(ref aCamSpace, ref bCamSpace, ref uvA, ref uvB)) {
			return; // behind the camera
		}

		uvA /= aCamSpace.w;
		uvB /= bCamSpace.w;

		float2 rayBufferBoundsFloat = context.camera.ProjectClippedToScreen(aCamSpace, bCamSpace, context.screen, context.axisMappedToY);
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
		ReducePixelHorizon(context.originalNextFreePixel, ref rayBufferBounds, ref nextFreePixel, seenPixelCache);

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
	static unsafe bool RareColumnAdjustment (ref Context context, float4 bothIntersections, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		float4 worldbounds = float4(0f, context.world.DimensionY + 1f, 0f, 0f);
		float3 bottom = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightX, ShuffleComponent.LeftY);
		float3 top = shuffle(bothIntersections, worldbounds, ShuffleComponent.LeftX, ShuffleComponent.RightY, ShuffleComponent.LeftY);

		context.camera.ProjectToHomogeneousCameraSpace(bottom, top, out float4 lastBottom, out float4 lastTop);

		if (!context.camera.ClipHomogeneousCameraSpaceLine(ref lastBottom, ref lastTop)) {
			return false; // full world line is behind camera (wat)
		}
		float2 pixelBounds = context.camera.ProjectClippedToScreen(lastBottom, lastTop, context.screen, context.axisMappedToY);

		// we may be waiting to have pixels written outside of the working frustum, which won't happen
		pixelBounds = select(pixelBounds.xy, pixelBounds.yx, pixelBounds.x > pixelBounds.y);
		int2 screenYCoords = int2(round(pixelBounds));

		if (screenYCoords.x > nextFreePixel.x) {
			// there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
			// and further increase the bottom free pixel according to write mask
			nextFreePixel.x = screenYCoords.x;
			ReduceBoundsBottom(context.originalNextFreePixel, ref nextFreePixel, seenPixelCache);
		}

		if (screenYCoords.y < nextFreePixel.y) {
			nextFreePixel.y = screenYCoords.y;
			ReduceBoundsTop(context.originalNextFreePixel, ref nextFreePixel, seenPixelCache);
		}

		if (nextFreePixel.x > nextFreePixel.y) {
			return false; // apparently we've written all pixels we can reach now
		}
		return true;
	}

	static unsafe void ReducePixelHorizon (int2 originalNextFreePixel, ref int2 rayBufferBounds, ref int2 nextFreePixel, byte* seenPixelCache)
	{
		if (rayBufferBounds.x <= nextFreePixel.x) {
			rayBufferBounds.x = nextFreePixel.x;
			if (rayBufferBounds.y >= nextFreePixel.x) {
				// so the bottom of this line was in the bottom written pixels, and the top was above those
				// extend the written pixels bottom with the ones we're writing now, and further extend them based on what we find in the seen pixels
				nextFreePixel.x = rayBufferBounds.y + 1;
				ReduceBoundsBottom(originalNextFreePixel, ref nextFreePixel, seenPixelCache);
			}
		}
		if (rayBufferBounds.y >= nextFreePixel.y) {
			rayBufferBounds.y = nextFreePixel.y;
			if (rayBufferBounds.x <= nextFreePixel.y) {
				nextFreePixel.y = rayBufferBounds.x - 1;
				ReduceBoundsTop(originalNextFreePixel, ref nextFreePixel, seenPixelCache);
			}
		}
	}

	static unsafe void ReduceBoundsTop (int2 originalNextFreePixel, ref int2 nextFreePixel, byte* seenPixelCache)
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

	static unsafe void ReduceBoundsBottom (int2 originalNextFreePixel, ref int2 nextFreePixel, byte* seenPixelCache)
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
