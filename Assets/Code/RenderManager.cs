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

		JobHandle screenBufferClearJob = ClearBuffer(screenBuffer);

		if (abs(camera.transform.eulerAngles.x) < 0.03f) {
			Vector3 eulers = camera.transform.eulerAngles;
			eulers.x = sign(eulers.x) * 0.03f;
			if (eulers.x == 0f) {
				eulers.x = 0.03f;
			}
			camera.transform.eulerAngles = eulers;
		}

		float3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		float2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<SegmentData> planes = new NativeArray<SegmentData>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);

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

		screenBufferClearJob.Complete();

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
			DrawSegmentRayJob job = new DrawSegmentRayJob();
			job.segment = segments[segmentIndex];
			job.vanishingPointScreenSpace = vanishingPointScreenSpace;
			job.isHorizontal = segmentIndex > 1;
			job.rayIndexOffset = 0;
			if (segmentIndex == 1) { job.rayIndexOffset = segments[0].RayCount; }
			if (segmentIndex == 3) { job.rayIndexOffset = segments[2].RayCount; }

			if (segmentIndex < 2) {
				job.activeRayBuffer = rayBufferTopDown;
				job.activeRayBufferWidth = screenHeight;
				if (segmentIndex == 0) { // top segment
					job.startNextFreeBottomPixel = max(0, Mathf.FloorToInt(vanishingPointScreenSpace.y));
					job.startNextFreeTopPixel = screenHeight - 1;
				} else { // bottom segment
					job.startNextFreeBottomPixel = 0;
					job.startNextFreeTopPixel = min(screenHeight - 1, Mathf.CeilToInt(vanishingPointScreenSpace.y));
				}
			} else {
				job.activeRayBuffer = rayBufferLeftRight;
				job.activeRayBufferWidth = screenWidth;
				if (segmentIndex == 3) { // left segment
					job.startNextFreeBottomPixel = 0;
					job.startNextFreeTopPixel = min(screenWidth - 1, Mathf.CeilToInt(vanishingPointScreenSpace.x));
				} else { // right segment
					job.startNextFreeBottomPixel = max(0, Mathf.FloorToInt(vanishingPointScreenSpace.x));
					job.startNextFreeTopPixel = screenWidth - 1;
				}
			}

			job.world = world;
			job.camera = camera;
			job.screen = screen;
			Profiler.EndSample();

			segmentHandles[segmentIndex] = job.Schedule(job.segment.RayCount, 16);
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

		JobHandle handle = copyJob.Schedule(screen.y, 16);
		handle.Complete();

		raySegments.Dispose();
	}

	static unsafe JobHandle ClearBuffer (NativeArray<Color24> buffer)
	{
		ClearBufferJob job = new ClearBufferJob()
		{
			buffer = buffer
		};
		return job.Schedule(1 + buffer.Length / ClearBufferJob.ITERATE_SIZE, 1);
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

		segment.MinWorld = camera.ScreenToWorldPoint(new float3(segment.MinScreen, camera.farClipPlane));
		segment.MaxWorld = camera.ScreenToWorldPoint(new float3(segment.MaxScreen, camera.farClipPlane));
		segment.RayCount = Mathf.RoundToInt(segment.MaxScreen[secondaryAxis] - segment.MinScreen[secondaryAxis]);
		return segment;
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct DrawSegmentRayJob : IJobParallelFor
	{
		[ReadOnly] public SegmentData segment;
		[ReadOnly] public float2 vanishingPointScreenSpace;
		[ReadOnly] public bool isHorizontal;
		[ReadOnly] public int activeRayBufferWidth;
		[ReadOnly] public int startNextFreeTopPixel;
		[ReadOnly] public int startNextFreeBottomPixel;
		[ReadOnly] public int rayIndexOffset;
		[ReadOnly] public World world;
		[ReadOnly] public CameraData camera;
		[ReadOnly] public float2 screen;

		[NativeDisableParallelForRestriction]
		[NativeDisableContainerSafetyRestriction]
		[WriteOnly]
		public NativeArray<Color24> activeRayBuffer;

		public unsafe void Execute (int planeRayIndex)
		{
			int seenPixelCacheLength = Mathf.RoundToInt(isHorizontal ? screen.x : screen.y);
			byte* seenPixelCache;
			void* seenPixelCachePtr = stackalloc byte[seenPixelCacheLength];
			seenPixelCache = (byte*)seenPixelCachePtr;
			UnsafeUtility.MemClear(seenPixelCache, seenPixelCacheLength);

			float endRayLerp = planeRayIndex / (float)segment.RayCount;

			float3 endWorld = lerp(segment.MinWorld, segment.MaxWorld, endRayLerp);
			float3 worldDir = endWorld - camera.Position;

			SegmentDDAData ray = new SegmentDDAData(camera.Position.xz, worldDir.xz);
			int axisMappedToY = isHorizontal ? 0 : 1;

			int nextFreeTopPixel = startNextFreeTopPixel;
			int nextFreeBottomPixel = startNextFreeBottomPixel;
			int rayBufferX = planeRayIndex + rayIndexOffset;
			int rayBufferIdxStart = rayBufferX * activeRayBufferWidth;

			float2 frustumYBounds = 0f;
			{
				float3 worldB;
				if (all(vanishingPointScreenSpace >= 0f & vanishingPointScreenSpace <= screen)) {
					worldB = camera.Position + float3(1f, select(1, -1, camera.ForwardY < 0f) * camera.FarClip * camera.FarClip, 0f);
				} else {
					float2 screenPosEnd = lerp(segment.MinScreen, segment.MaxScreen, endRayLerp);
					float2 screenPosStart = vanishingPointScreenSpace;
					float2 dir = screenPosEnd - screenPosStart;
					// find out where the ray from start->end starts coming on screen
					Bounds b = new Bounds();
					b.SetMinMax(float3(0f), float3(screen, 1f));
					if (b.IntersectRay(new Ray(float3(screenPosStart, 0.5f), float3(dir, 0f)), out float distance)) {
						screenPosStart += normalize(dir) * (distance - 2f); // -1f because we don't want to get the Y ray of the bottom pixel, but just outside the screen
					}
					worldB = camera.ScreenToWorldPoint(float3(screenPosStart, 1f), screen);
				}

				frustumYBounds.x = worldDir.y;

				float3 dirB = worldB - camera.Position;
				frustumYBounds.y = dirB.y * (length(worldDir.xz) / length(dirB.xz));
			}

			bool cameraLookingUp = camera.ForwardY >= 0f;
			int elementIterationDirection = cameraLookingUp ? 1 : -1;
			int rayStepCount = 0;

			while (!ray.AtEnd) {
				// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
				float2 nextIntersection = ray.NextIntersection;
				float2 lastIntersection = ray.LastIntersection;

				if (nextFreeBottomPixel > nextFreeTopPixel) { break; } // wrote to all pixels, so just end this ray

				int maxColumnY = world.DimensionY + 1;
				int minColumnY = 0;
				if (rayStepCount > 10) {
					// step > 10 is to avoid some issues with this with columns directly above/below the camera
					// get the min or max world Y position of the frustum at this position
					float2 frustumYBoundsThisColumn = camera.Position.y + frustumYBounds * ray.NextIntersectionDistanceUnnormalized;
					if (frustumYBoundsThisColumn.x < frustumYBoundsThisColumn.y) {
						Swap(ref frustumYBoundsThisColumn.x, ref frustumYBoundsThisColumn.y);
					}
					if (frustumYBoundsThisColumn.x < minColumnY || frustumYBoundsThisColumn.y > maxColumnY) {
						break; // frustum bounds are entirely out of world bounds
					}

					maxColumnY = Mathf.CeilToInt(frustumYBoundsThisColumn.x + 2f);
					minColumnY = Mathf.FloorToInt(frustumYBoundsThisColumn.y - 2f);
				}

				World.RLEColumn elements = world.GetVoxelColumn(ray.position);

				// need to iterate the elements from close to far vertically to not overwrite pixels
				int elementStart = select(elements.Count - 1, 0, cameraLookingUp);
				int elementEnd = select(-1, elements.Count, cameraLookingUp);
				
				for (int iElement = elementStart; iElement != elementEnd; iElement += elementIterationDirection) {
					World.RLEElement element = elements[iElement];

					if (element.Bottom > maxColumnY || element.Top < minColumnY) {
						continue;
					}

					float topWorldY = element.Top;
					float bottomWorldY = element.Bottom - 1f;

					// need to use last/next intersection point instead of column position or it'll look like rotating billboards instead of a box
					float2 topWorldXZ = select(lastIntersection, nextIntersection, topWorldY < camera.Position.y);
					float2 bottomWorldXZ = select(lastIntersection, nextIntersection, bottomWorldY > camera.Position.y);

					float3 topWorld = new float3(topWorldXZ.x, topWorldY, topWorldXZ.y);
					float3 bottomWorld = new float3(bottomWorldXZ.x, bottomWorldY, bottomWorldXZ.y);

					if (!camera.ProjectToScreen(topWorld, bottomWorld, screen, axisMappedToY, out float yBottomExact, out float yTopExact)) {
						continue; // behind the camera for some reason
					}

					if (yTopExact < yBottomExact) {
						Swap(ref yTopExact, ref yBottomExact);
					}

					int rayBufferYBottom = Mathf.RoundToInt(yBottomExact);
					int rayBufferYTop = Mathf.RoundToInt(yTopExact);

					// check if the line overlaps with the area that's writable
					if (rayBufferYTop < nextFreeBottomPixel || rayBufferYBottom > nextFreeTopPixel) {
						continue;
					}
					
					// adjust writable area bounds
					if (rayBufferYBottom <= nextFreeBottomPixel) {
						rayBufferYBottom = nextFreeBottomPixel;
						if (rayBufferYTop >= nextFreeBottomPixel) {
							nextFreeBottomPixel = rayBufferYTop + 1;
							// try to extend the floating horizon further if we already wrote stuff there
							for (int y = nextFreeBottomPixel; y <= startNextFreeTopPixel; y++) {
								if (seenPixelCache[y] > 0) {
									nextFreeBottomPixel++;
								} else {
									break;
								}
							}
						}
					}
					if (rayBufferYTop >= nextFreeTopPixel) {
						rayBufferYTop = nextFreeTopPixel;
						if (rayBufferYBottom <= nextFreeTopPixel) {
							nextFreeTopPixel = rayBufferYBottom - 1;
							// try to extend the floating horizon further if we already wrote stuff there
							for (int y = nextFreeTopPixel; y >= startNextFreeBottomPixel; y--) {
								if (seenPixelCache[y] > 0) {
									nextFreeTopPixel--;
								} else {
									break;
								}
							}
						}
					}

					// actually write the line to the buffer
					for (int y = rayBufferYBottom; y <= rayBufferYTop; y++) {
						if (seenPixelCache[y] == 0) {
							seenPixelCache[y] = 1;
							activeRayBuffer[rayBufferIdxStart + y] = element.Color;
						}
					}
				}

				ray.Step();
				rayStepCount++;
			}
			{
				Color24 black = new Color24(0, 0, 0);
				for (int y = startNextFreeBottomPixel; y <= startNextFreeTopPixel; y++) {
					if (seenPixelCache[y] == 0) {
						activeRayBuffer[rayBufferIdxStart + y] = black;
					}
				}
			}
		}
	}

	[BurstCompile]
	unsafe struct ClearBufferJob : IJobParallelFor
	{
		public const int ITERATE_SIZE = 131072;
		public NativeArray<Color24> buffer;

		public unsafe void Execute (int i)
		{
			int count = ITERATE_SIZE;
			int start = i * ITERATE_SIZE;
			if (start + count >= buffer.Length) {
				count = buffer.Length - start;
			}

			Color24* ptr = (Color24*)NativeArrayUnsafeUtility.GetUnsafePtr(buffer);
			UnsafeUtility.MemClear(ptr + start, count * sizeof(Color24));
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
		float nextIntersectionDistance;
		float lastIntersectionDistance;

		public bool AtEnd { get { return nextIntersectionDistance > 1f; } }

		public float NextIntersectionDistanceUnnormalized { get { return nextIntersectionDistance; } }

		public float2 LastIntersection { get { return start + dir * lastIntersectionDistance; } }
		public float2 NextIntersection { get { return start + dir * nextIntersectionDistance; } }

		public SegmentDDAData (float2 start, float2 dir)
		{
			this.dir = dir;
			this.start = start;
			if (dir.x == 0f) { dir.x = 0.00001f; }
			if (dir.y == 0f) { dir.y = 0.00001f; }
			position = new int2(floor(start));
			float2 rayDirInverse = rcp(dir);
			step = new int2(dir.x >= 0f ? 1 : -1, dir.y >= 0f ? 1 : -1);
			tDelta = min(rayDirInverse * step, 1f);
			tMax = abs((-frac(start) + max(step, 0f)) * rayDirInverse);
			nextIntersectionDistance = min(tMax.x, tMax.y);

			float2 tMaxReverse = abs((-frac(start) + max(-step, 0f)) * -rayDirInverse);
			lastIntersectionDistance = -min(tMaxReverse.x, tMaxReverse.y);
		}

		public void Step ()
		{
			int dimension = select(0, 1, nextIntersectionDistance == tMax.y);
			tMax[dimension] += tDelta[dimension];
			position[dimension] += step[dimension];
			lastIntersectionDistance = nextIntersectionDistance;
			nextIntersectionDistance = min(tMax.x, tMax.y);
		}
	}

	struct SegmentData
	{
		public float2 MinScreen;
		public float2 MaxScreen;
		public float3 MinWorld;
		public float3 MaxWorld;
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public float3 ScreenToWorldPoint (float3 pos, float2 screenSize)
		{
			float4 pos4 = float4((pos.xy / screenSize) * 2f - 1f, pos.z, 1f);
			pos4 = mul(ScreenToWorldMatrix, pos4);
			return pos4.xyz / pos4.w;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool ProjectToScreen (float3 worldA, float3 worldB, float2 screen, int desiredAxis, out float yA, out float yB)
		{
			float4 resultA = mul(WorldToScreenMatrix, new float4(worldA, 1f));
			float4 resultB = mul(WorldToScreenMatrix, new float4(worldB, 1f));
			bool aIsBehind = resultA.z < 0f;
			bool bIsBehind = resultB.z < 0f;
			if (aIsBehind && bIsBehind) {
				yA = yB = default;
				return false;
			}
			if (aIsBehind || bIsBehind) {
				// janky fix for lines intersecting the near clip plane
				// project them to camera space, clamp there, project results to screen space

				float4 camSpaceA = mul(worldToCameraMatrix, float4(worldA, 1f));
				float4 camSpaceB = mul(worldToCameraMatrix, float4(worldB, 1f));

				camSpaceA.xyz /= camSpaceA.w;
				camSpaceB.xyz /= camSpaceB.w;

				if (aIsBehind) {
					float4 dir = camSpaceA - camSpaceB;
					float dirHappy = camSpaceB.z / -dir.z;
					camSpaceA = camSpaceB + (dirHappy - 0.001f) * dir;
					resultA = mul(cameraToScreenMatrix, camSpaceA);
				} else {
					float4 dir = camSpaceB - camSpaceA;
					float dirHappy = camSpaceA.z / -dir.z;
					camSpaceB = camSpaceA + (dirHappy - 0.001f) * dir;
					resultB = mul(cameraToScreenMatrix, camSpaceB);
				}
			}

			if (resultA.w == 0f) { resultA.w = 0.000001f; }
			if (resultB.w == 0f) { resultB.w = 0.000001f; }
			yA = (resultA[desiredAxis] / resultA.w + 1f) * .5f * screen[desiredAxis];
			yB = (resultB[desiredAxis] / resultB.w + 1f) * .5f * screen[desiredAxis];
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
