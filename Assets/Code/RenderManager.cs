using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

public class RenderManager
{
	public void Draw (
		NativeArray<Color24> screenBuffer,
		NativeArray<Color24> rayBufferTopDown,
		NativeArray<Color24> rayBufferLeftRight,
		int screenWidth,
		int screenHeight,
		World world,
		Camera camera
	) {
		Debug.DrawLine(new Vector2(0f, 0f), new Vector2(screenWidth, 0f));
		Debug.DrawLine(new Vector2(screenWidth, 0f), new Vector2(screenWidth, screenHeight));
		Debug.DrawLine(new Vector2(screenWidth, screenHeight), new Vector2(0f, screenHeight));
		Debug.DrawLine(new Vector2(0f, screenHeight), new Vector2(0f, 0f));

		if (abs(camera.transform.eulerAngles.x) < 0.03f) {
			Vector3 eulers = camera.transform.eulerAngles;
			eulers.x = sign(eulers.x) * 0.03f;
			if (eulers.x == 0f) {
				eulers.x = 0.03f;
			}
			camera.transform.eulerAngles = eulers;
		}

		Profiler.BeginSample("Setup VP");
		float3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		float2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		Profiler.EndSample();
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<SegmentData> planes = new NativeArray<SegmentData>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);

		Profiler.BeginSample("Setup segment params");
		if (vanishingPointScreenSpace.y < screenHeight) {
			float distToOtherEnd = screenHeight - vanishingPointScreenSpace.y;
			planes[0] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, 1), 1, world.DimensionY);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.y;
			planes[1] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, -1), 1, world.DimensionY);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			float distToOtherEnd = screenWidth - vanishingPointScreenSpace.x;
			planes[2] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(1, 0), 0, world.DimensionY);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.x;
			planes[3] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(-1, 0), 0, world.DimensionY);
		}
		Profiler.EndSample();

		Profiler.BeginSample("Draw planes");
		DrawSegments(planes,
			vanishingPointWorldSpace,
			world,
			new CameraData(camera),
			screenWidth,
			screenHeight,
			vanishingPointScreenSpace,
			rayBufferTopDown,
			rayBufferLeftRight
		);
		Profiler.EndSample();

		Profiler.BeginSample("Blit raybuffer to screen");
		CopyTopRayBufferToScreen(
			new int2(screenWidth, screenHeight),
			planes,
			vanishingPointScreenSpace,
			rayBufferTopDown,
			rayBufferLeftRight,
			screenBuffer
		);
		Profiler.EndSample();
	}

	static void DrawSegments (
		NativeArray<SegmentData> segments,
		float3 vanishingPointWorldSpace,
		World world,
		CameraData camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
		NativeArray<Color24> rayBufferTopDown,
		NativeArray<Color24> rayBufferLeftRight
	)
	{
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<JobHandle> segmentHandles = new NativeArray<JobHandle>(4, Allocator.Temp);

		for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++) {
			if (segments[segmentIndex].RayCount <= 0) {
				continue;
			}

			Profiler.BeginSample("Segment setup overhead");

			DrawSegmentRayJob.PerRayMutableContext context = default;

			DrawSegmentRayJob job = new DrawSegmentRayJob();
			job.segment = segments[segmentIndex];
			job.vanishingPointScreenSpace = vanishingPointScreenSpace;
			job.vanishingPointOnScreen = all(vanishingPointScreenSpace >= 0f & vanishingPointScreenSpace <= screen);
			job.axisMappedToY = (segmentIndex > 1) ? 0 : 1;
			job.seenPixelCacheLength = Mathf.RoundToInt((segmentIndex > 1 ) ? screen.x : screen.y);
			job.rayIndexOffset = 0;
			job.cameraLookingUp = camera.ForwardY >= 0f;
			job.vanishingPointCameraRayOnScreen = camera.Position + float3(1f, select(1, -1, !job.cameraLookingUp) * camera.FarClip * camera.FarClip, 0f);
			job.elementIterationDirection = job.cameraLookingUp ? 1 : -1;
			if (segmentIndex == 1) { job.rayIndexOffset = segments[0].RayCount; }
			if (segmentIndex == 3) { job.rayIndexOffset = segments[2].RayCount; }

			if (segmentIndex < 2) {
				job.activeRayBuffer = rayBufferTopDown;
				job.activeRayBufferWidth = screenHeight;
				if (segmentIndex == 0) { // top segment
					context.nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1), screenHeight - 1);
				} else { // bottom segment
					context.nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1));
				}
			} else {
				job.activeRayBuffer = rayBufferLeftRight;
				job.activeRayBufferWidth = screenWidth;
				if (segmentIndex == 3) { // left segment
					context.nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1));
				} else { // right segment
					context.nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1), screenWidth - 1);
				}
			}

			job.contextOriginal = context;
			job.world = world;
			job.camera = camera;
			job.screen = screen;
			job.markerProject = new Unity.Profiling.ProfilerMarker("DrawSegment.Project");
			job.markerDDA = new Unity.Profiling.ProfilerMarker("DrawSegment.DDA");
			job.markerSetup = new Unity.Profiling.ProfilerMarker("DrawSegment.Setup");
			job.markerWrite = new Unity.Profiling.ProfilerMarker("DrawSegment.Write");
			Profiler.EndSample();

			segmentHandles[segmentIndex] = job.Schedule(job.segment.RayCount, 1);
		}

		JobHandle.CompleteAll(segmentHandles);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static void Swap<T> (ref T a, ref T b)
	{
		T t = a;
		a = b;
		b = t;
	}

	static void CopyTopRayBufferToScreen (
		int2 screen,
		NativeArray<SegmentData> segments,
		float2 vpScreen,
		NativeArray<Color24> rayBufferTopDown,
		NativeArray<Color24> rayBufferLeftRight,
		NativeArray<Color24> screenBuffer)
	{
		NativeArray<SegmentRayData> raySegments = new NativeArray<SegmentRayData>(4, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

		for (int i = 0; i < 4; i++) {
			SegmentRayData segment = raySegments[i];
			segment.UScale = segments[i].RayCount / (float)(i > 1 ? screen.x : screen.y);
			raySegments[i] = segment;
		}
		{
			SegmentRayData segment = raySegments[0];
			segment.UOffsetStart = 0f;
			segment.Min = segments[0].MinScreen.x - vpScreen.x;
			segment.Max = segments[0].MaxScreen.x - vpScreen.x;
			segment.rayBufferWidth = screen.y;
			raySegments[0] = segment;

			segment = raySegments[1];
			segment.UOffsetStart = raySegments[0].UScale;
			segment.Min = segments[1].MinScreen.x - vpScreen.x;
			segment.Max = segments[1].MaxScreen.x - vpScreen.x;
			segment.rayBufferWidth = screen.y;
			raySegments[1] = segment;

			segment = raySegments[2];
			segment.UOffsetStart = 0f;
			segment.Min = segments[2].MinScreen.y - vpScreen.y;
			segment.Max = segments[2].MaxScreen.y - vpScreen.y;
			segment.rayBufferWidth = screen.x;
			raySegments[2] = segment;

			segment = raySegments[3];
			segment.UOffsetStart = raySegments[2].UScale;
			segment.Min = segments[3].MinScreen.y - vpScreen.y;
			segment.Max = segments[3].MaxScreen.y - vpScreen.y;
			segment.rayBufferWidth = screen.x;
			raySegments[3] = segment;

		}

		CopyRayBufferJob copyJob = new CopyRayBufferJob();
		copyJob.oneOverDistTopToVP = 1f / (screen - vpScreen);
		copyJob.oneOverVPScreen = 1f / vpScreen;
		copyJob.rayBufferLeftRight = rayBufferLeftRight;
		copyJob.rayBufferTopDown = rayBufferTopDown;
		copyJob.screen = screen;
		copyJob.screenBuffer = screenBuffer;
		copyJob.segments = raySegments;
		copyJob.vpScreen = vpScreen;

		JobHandle handle = copyJob.Schedule(screen.y, 1);
		handle.Complete();

		raySegments.Dispose();
	}

	static Vector3 CalculateVanishingPointWorld (Camera camera)
	{
		Transform transform = camera.transform;
		return transform.position + Vector3.up * (-camera.nearClipPlane / Mathf.Sin(transform.eulerAngles.x * Mathf.Deg2Rad));
	}

	static float2 ProjectVanishingPointScreenToWorld (Camera camera, float3 worldPos)
	{
		return ((float3)camera.WorldToScreenPoint(worldPos)).xy;
	}

	static SegmentData GetGenericSegmentParameters (
		Camera camera,
		float2 screen,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		float distToOtherEnd,
		float2 neutral,
		int primaryAxis,
		int worldYMax
	)
	{
		SegmentData segment = new SegmentData();

		int secondaryAxis = 1 - primaryAxis;

		float2 simpleCaseMin, simpleCaseMax;
		{
			// setup the end points for the 2 45 degree angle rays
			simpleCaseMin = vpScreen[secondaryAxis] - distToOtherEnd;
			simpleCaseMax = vpScreen[secondaryAxis] + distToOtherEnd;

			float a = vpScreen[primaryAxis] + distToOtherEnd * sign(neutral[primaryAxis]);
			simpleCaseMin[primaryAxis] = a;
			simpleCaseMax[primaryAxis] = a;
		}

		if (simpleCaseMax[secondaryAxis] <= 0f || simpleCaseMin[secondaryAxis] >= screen[secondaryAxis]) {
			return segment; // 45 degree angles aren't on screen
		}

		if (all(vpScreen >= 0f & vpScreen <= screen)) {
			// vp within bounds, so nothing to clamp angle wise
			segment.MinScreen = simpleCaseMin;
			segment.MaxScreen = simpleCaseMax;
		} else {
			// vp outside of bounds, so we want to check if we can clamp the segment to the screen area to prevent wasting precious buffer space
			float2 dirSimpleMiddle = lerp(simpleCaseMin, simpleCaseMax, 0.5f) - vpScreen;

			float angleLeft = 90f, angleRight = -90f;
			float2 dirRight = default, dirLeft = default;

			NativeArray<float2> vectors = new NativeArray<float2>(4, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			vectors[0] = new float2(0f, 0f);
			vectors[1] = new float2(0f, screen[1]);
			vectors[2] = new float2(screen[0], 0f);
			vectors[3] = screen;

			for (int i = 0; i < 4; i++) {
				float2 dir = vectors[i] - vpScreen;
				float2 scaledEnd = dir * (distToOtherEnd / abs(dir[primaryAxis]));
				float angle = Vector2.SignedAngle(neutral, dir);
				if (angle < angleLeft) {
					angleLeft = angle;
					dirLeft = scaledEnd;
				}
				if (angle > angleRight) {
					angleRight = angle;
					dirRight = scaledEnd;
				}
			}

			float2 cornerLeft = dirLeft + vpScreen;
			float2 cornerRight = dirRight + vpScreen;

			if (angleLeft < -45f) { // fallback to whatever the simple case left corner was
				cornerLeft = Vector2.SignedAngle(dirSimpleMiddle, simpleCaseMax) > 0f ? simpleCaseMin : simpleCaseMax;
			}
			if (angleRight > 45f) { // fallback to whatever the simple case right corner was
				cornerRight = Vector2.SignedAngle(dirSimpleMiddle, simpleCaseMax) < 0f ? simpleCaseMin : simpleCaseMax;
			}

			Debug.DrawLine((Vector2)vpScreen, (Vector2)cornerLeft, Color.red);
			Debug.DrawLine((Vector2)vpScreen, (Vector2)cornerRight, Color.red);

			bool swap = cornerLeft[secondaryAxis] > cornerRight[secondaryAxis];
			segment.MinScreen = select(cornerLeft, cornerRight, swap);
			segment.MaxScreen = select(cornerRight, cornerLeft, swap);
		}

		segment.CamLocalPlaneRayMin = camera.ScreenToWorldPoint(new float3(segment.MinScreen, camera.farClipPlane)) - camera.transform.position;
		segment.CamLocalPlaneRayMax = camera.ScreenToWorldPoint(new float3(segment.MaxScreen, camera.farClipPlane)) - camera.transform.position;
		segment.RayCount = Mathf.RoundToInt(segment.MaxScreen[secondaryAxis] - segment.MinScreen[secondaryAxis]);
		return segment;
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct DrawSegmentRayJob : IJobParallelFor
	{
		public struct PerRayMutableContext
		{
			public int2 nextFreePixel; // (bottom, top)
			public int rayStepCount; // just starts at 0
		}

		[ReadOnly] public PerRayMutableContext contextOriginal;

		[ReadOnly] public SegmentData segment;
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
		[ReadOnly] public Unity.Profiling.ProfilerMarker markerSetup;
		[ReadOnly] public Unity.Profiling.ProfilerMarker markerDDA;
		[ReadOnly] public Unity.Profiling.ProfilerMarker markerProject;
		[ReadOnly] public Unity.Profiling.ProfilerMarker markerWrite;

		[NativeDisableParallelForRestriction]
		[NativeDisableContainerSafetyRestriction]
		[WriteOnly]
		public NativeArray<Color24> activeRayBuffer;

		public unsafe void Execute (int planeRayIndex)
		{
			markerSetup.Begin();

			PerRayMutableContext context = contextOriginal;

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

			markerSetup.End();
			markerDDA.Begin();
			while (true) {
				// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
				float4 intersections = ray.Intersections; // xy = last, zw = next

				int2 columnBounds = SetupColumnBounds(frustumYBounds, ray.IntersectionDistancesUnnormalized);

				if ((context.rayStepCount & 31) == 31) {
					AdjustOpenPixelsRange(columnBounds, intersections, ref context, seenPixelCache);
				}

				World.RLEColumn elements = world.GetVoxelColumn(ray.position);

				// need to iterate the elements from close to far vertically to not overwrite pixels
				int2 elementRange = int2(select(elements.Count - 1, 0, cameraLookingUp), select(-1, elements.Count, cameraLookingUp));

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
					if (any(bool2(rayBufferBounds.y < context.nextFreePixel.x, rayBufferBounds.x > context.nextFreePixel.y))) {
						continue;
					}

					ExtendFreePixelsBottom(ref rayBufferBounds, ref context, seenPixelCache);
					ExtendFreePixelsTop(ref rayBufferBounds, ref context, seenPixelCache);
					WriteLine(rayBufferBounds, seenPixelCache, rayBufferIdxStart, element.Color);
				}

				ray.Step();

				bool4 endConditions = bool4(
					columnBounds.y < 0,
					columnBounds.x > world.DimensionY,
					context.nextFreePixel.x > context.nextFreePixel.y,
					ray.AtEnd
				);

				context.rayStepCount++;

				if (any(endConditions)) {
					break;
				}
			}

			markerDDA.End();
			WriteSkybox(seenPixelCache, rayBufferIdxStart);
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

		void ExtendFreePixelsBottom (ref int2 rayBufferBounds, ref PerRayMutableContext context, NativeArray<byte> seenPixelCache)
		{
			if (rayBufferBounds.x <= context.nextFreePixel.x) {
				rayBufferBounds.x = context.nextFreePixel.x;
				if (rayBufferBounds.y >= context.nextFreePixel.x) {
					context.nextFreePixel.x = rayBufferBounds.y + 1;
					// try to extend the floating horizon further if we already wrote stuff there
					for (int y = context.nextFreePixel.x; y <= contextOriginal.nextFreePixel.y; y++) {
						if (seenPixelCache[y] > 0) {
							context.nextFreePixel.x++;
						} else {
							break;
						}
					}
				}
			}
		}

		void ExtendFreePixelsTop (ref int2 rayBufferBounds, ref PerRayMutableContext context, NativeArray<byte> seenPixelCache)
		{
			if (rayBufferBounds.y >= context.nextFreePixel.y) {
				rayBufferBounds.y = context.nextFreePixel.y;
				if (rayBufferBounds.x <= context.nextFreePixel.y) {
					context.nextFreePixel.y = rayBufferBounds.x - 1;
					// try to extend the floating horizon further if we already wrote stuff there
					for (int y = context.nextFreePixel.y; y >= contextOriginal.nextFreePixel.x; y--) {
						if (seenPixelCache[y] > 0) {
							context.nextFreePixel.y--;
						} else {
							break;
						}
					}
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
			for (int y = contextOriginal.nextFreePixel.x; y <= contextOriginal.nextFreePixel.y; y++) {
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

		bool AdjustOpenPixelsRange (int2 columnBounds, float4 intersections, ref PerRayMutableContext context, NativeArray<byte> seenPixelCache)
		{
			// project the column bounds to the raybuffer and adjust next free top/bottom pixels accordingly
			// we may be waiting to have pixels written outside of the working frustum, which won't happen
			float bottomWorldY = columnBounds.x - 1f;
			float topWorldY = columnBounds.y;

			// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
			float3 topWorld = float3(select(intersections.xy, intersections.zw, topWorldY < camera.Position.y), topWorldY);
			float3 bottomWorld = float3(select(intersections.xy, intersections.zw, bottomWorldY > camera.Position.y), bottomWorldY);

			topWorld = topWorld.xzy;
			bottomWorld = bottomWorld.xzy;

			if (camera.ProjectToScreen(topWorld, bottomWorld, screen, axisMappedToY, out float2 screenYCoords)) {
				int rayBufferYBottom = Mathf.RoundToInt(cmin(screenYCoords));
				int rayBufferYTop = Mathf.RoundToInt(cmax(screenYCoords));

				if (rayBufferYBottom > context.nextFreePixel.x) {
					context.nextFreePixel.x = rayBufferYBottom; // there's some pixels near the bottom that we can't write to anymore with a full-frustum column, so skip those
																// and further increase the bottom free pixel according to write mask
					for (int y = context.nextFreePixel.x; y <= contextOriginal.nextFreePixel.y; y++) {
						if (seenPixelCache[y] > 0) {
							context.nextFreePixel.x++;
						} else {
							break;
						}
					}
				}
				if (rayBufferYTop < context.nextFreePixel.y) {
					context.nextFreePixel.y = rayBufferYTop;
					for (int y = context.nextFreePixel.y; y >= contextOriginal.nextFreePixel.x; y--) {
						if (seenPixelCache[y] > 0) {
							context.nextFreePixel.y--;
						} else {
							break;
						}
					}
				}
				if (context.nextFreePixel.x > context.nextFreePixel.y) {
					return false; // apparently we've written all pixels we can reach now
				}
			}
			return true;
		}

		int2 SetupColumnBounds (float2 frustumYBounds, float2 intersectionDistances)
		{
			// calculate world space frustum bounds of the world column we're at
			bool2 selection = bool2(frustumYBounds.x < 0, frustumYBounds.y > 0);
			float2 distances = select(intersectionDistances.y, intersectionDistances.x, selection);
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
				// find out where the ray from start->end starts coming on screen
				bool intersected = IntersectScreen(vanishingPointScreenSpace, dir, out float distance);
				float2 screenPosStart = vanishingPointScreenSpace + normalize(dir) * select(0f, distance, intersected);
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
			dir = normalizesafe(dir);

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
			} else if (start.y <= 0f || start.y >= screen.x) {
				return false;
			}

			distance = select(tmin, tmax, tmin < 0f);
			return !(tmin < 0f && tmax < 0f || tmax < tmin);
		}
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct CopyRayBufferJob : IJobParallelFor
	{
		[ReadOnly] public float2 vpScreen;
		[ReadOnly] public float2 oneOverDistTopToVP;
		[ReadOnly] public float2 oneOverVPScreen;
		[ReadOnly] public int2 screen;
		[ReadOnly] public NativeArray<SegmentRayData> segments;
		[ReadOnly] public NativeArray<Color24> rayBufferLeftRight;
		[ReadOnly] public NativeArray<Color24> rayBufferTopDown;

		[NativeDisableParallelForRestriction]
		[NativeDisableContainerSafetyRestriction]
		public NativeArray<Color24> screenBuffer;

		public void Execute (int y)
		{
			float topNormalizedY = (y - vpScreen.y) * oneOverDistTopToVP.y;
			float2 minmaxTop = new float2(
				segments[0].Min * topNormalizedY + vpScreen.x,
				segments[0].Max * topNormalizedY + vpScreen.x
			);
			float bottomNornmalizedY = 1f - (y * oneOverVPScreen.y);
			float2 minmaxBottom = new float2(
				segments[1].Min * bottomNornmalizedY + vpScreen.x,
				segments[1].Max * bottomNornmalizedY + vpScreen.x
			);
			int screenIdxY = y * screen.x;
			float deltaToVPY = y - vpScreen.y;
			float deltaToVPYAbs = abs(deltaToVPY);

			for (int x = 0; x < screen.x; x++) {
				SegmentRayData segment;
				float2 minmaxX;
				int primaryDimension;

				float deltaToVPX = x - vpScreen.x;
				if (abs(deltaToVPX) < deltaToVPYAbs) {
					primaryDimension = 0;
					if (deltaToVPY >= 0f) {
						segment = segments[0]; // top segment (VP below pixel)
						minmaxX = minmaxTop;
					} else {
						segment = segments[1]; //bottom segment (VP above pixel)
						minmaxX = minmaxBottom;
					}
				} else {
					primaryDimension = 1;
					float normalizedX;
					if (deltaToVPX >= 0f) {
						segment = segments[2]; // right segment (VP left of pixel)
						normalizedX = (x - vpScreen.x) * oneOverDistTopToVP.x;
					} else {
						segment = segments[3]; // left segment (VP right of pixel
						normalizedX = 1f - (x * oneOverVPScreen.x);
					}
					minmaxX = new float2(
						segment.Min * normalizedX + vpScreen.y,
						segment.Max * normalizedX + vpScreen.y
					);
				}

				NativeArray<Color24> rayBuffer = primaryDimension > 0 ? rayBufferLeftRight : rayBufferTopDown;
				int2 pixelScreen = new int2(x, y);
				float planeRayBufferX = unlerp(minmaxX.x, minmaxX.y, pixelScreen[primaryDimension]);
				float u = segment.UOffsetStart + clamp(planeRayBufferX, 0f, 1f) * segment.UScale;
				int rayBufferIdx = Mathf.FloorToInt(u * segment.rayBufferWidth) * segment.rayBufferWidth + pixelScreen[1 - primaryDimension];
				screenBuffer[screenIdxY + x] = rayBuffer[rayBufferIdx];
			}
		}
	}


	struct SegmentDDAData
	{
		public int2 position;

		int2 step;
		float2 start, dir, tDelta, tMax;
		float2 intersectionDistances;

		public bool AtEnd { get { return intersectionDistances.y > 1f; } }

		public float2 IntersectionDistancesUnnormalized { get { return intersectionDistances; } } // x = last, y = next
		public float LastIntersectionDistanceUnnormalized { get { return intersectionDistances.x; } }
		public float NextIntersectionDistanceUnnormalized { get { return intersectionDistances.y; } }

		public float4 Intersections { get { return start.xyxy + dir.xyxy * intersectionDistances.xxyy; } } // xy = last, zw = next
		public float2 LastIntersection { get { return start + dir * intersectionDistances.x; } }
		public float2 NextIntersection { get { return start + dir * intersectionDistances.y; } }

		public SegmentDDAData (float2 start, float2 dir)
		{
			this.start = start;
			position = new int2(floor(start));
			float2 negatedFracStart = -frac(start);

			this.dir = dir;
			dir = select(dir, 0.00001f, dir == 0f);
			float2 rayDirInverse = rcp(dir);
			step = int2(sign(dir));
			tDelta = min(rayDirInverse * step, 1f);
			tMax = abs((negatedFracStart + max(step, 0f)) * rayDirInverse);

			float2 tMaxReverse = abs((negatedFracStart + max(-step, 0f)) * -rayDirInverse);
			intersectionDistances = float2(-cmin(tMaxReverse), cmin(tMax));
		}

		public void Step ()
		{
			int dimension = select(0, 1, intersectionDistances.y == tMax.y);
			tMax[dimension] += tDelta[dimension];
			position[dimension] += step[dimension];
			intersectionDistances = float2(intersectionDistances.y, cmin(tMax));
		}
	}

	struct SegmentData
	{
		public float2 MinScreen;
		public float2 MaxScreen;
		public float3 CamLocalPlaneRayMin;
		public float3 CamLocalPlaneRayMax;
		public int RayCount;
	}

	struct SegmentRayData
	{
		public float UOffsetStart;
		public float UScale;
		public float Min;
		public float Max;
		public int rayBufferWidth;
	}

	struct CameraData
	{
		public float3 Position;
		public float ForwardY;
		public float FarClip;

		float4x4 worldToCameraMatrix;
		float4x4 cameraToScreenMatrix;

		float4x4 ScreenToWorldMatrix;
		float4x4 WorldToScreenMatrix;

		public CameraData (Camera camera)
		{
			FarClip = camera.farClipPlane;
			Position = camera.transform.position;
			ForwardY = camera.transform.forward.y;

			worldToCameraMatrix = camera.worldToCameraMatrix;
			cameraToScreenMatrix = camera.nonJitteredProjectionMatrix;
			WorldToScreenMatrix = mul(cameraToScreenMatrix, worldToCameraMatrix);
			ScreenToWorldMatrix = inverse(WorldToScreenMatrix);
		}

		public float3 ScreenToWorldPoint (float3 pos, float2 screenSize)
		{
			float4 pos4 = float4((pos.xy / screenSize) * 2f - 1f, pos.z, 1f);
			pos4 = mul(ScreenToWorldMatrix, pos4);
			return pos4.xyz / pos4.w;
		}

		public bool ProjectToScreen (float3 worldA, float3 worldB, float2 screen, int desiredAxis, out float2 yResults)
		{
			float4 resultA = mul(WorldToScreenMatrix, float4(worldA, 1f));
			float4 resultB = mul(WorldToScreenMatrix, float4(worldB, 1f));

			if (resultA.z <= 0f) {
				if (resultB.z <= 0f) {
					yResults = default;
					return false;
				}
				resultA = resultB + (resultB.z / (resultB.z - resultA.z)) * (resultA - resultB);
			} else if (resultB.z <= 0f) {
				resultB = resultA + (resultA.z / (resultA.z - resultB.z)) * (resultB - resultA);
			}

			float2 result = float2(resultA[desiredAxis], resultB[desiredAxis]);
			float2 w = float2(resultA.w, resultB.w);
			yResults = mad(result / w, 0.5f, 0.5f) * screen[desiredAxis];
			return true;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Color24
	{
		public byte r;
		public byte g;
		public byte b;

		public Color24 (byte r, byte g, byte b)
		{
			this.r = r;
			this.g = g;
			this.b = b;
		}

		public static implicit operator Color24 (Color32 source)
		{
			return new Color24(source.r, source.g, source.b);
		}
	}
}
