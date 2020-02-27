﻿using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

public class RenderManager
{
	public void Draw (
		NativeArray<Color32> screenBuffer,
		NativeArray<Color32> rayBufferTopDown,
		NativeArray<Color32> rayBufferLeftRight,
		int screenWidth,
		int screenHeight,
		World world,
		Camera camera
	) {
		Debug.DrawLine(new Vector2(0f, 0f), new Vector2(screenWidth, 0f));
		Debug.DrawLine(new Vector2(screenWidth, 0f), new Vector2(screenWidth, screenHeight));
		Debug.DrawLine(new Vector2(screenWidth, screenHeight), new Vector2(0f, screenHeight));
		Debug.DrawLine(new Vector2(0f, screenHeight), new Vector2(0f, 0f));

		Profiler.BeginSample("Clear");
		ClearBuffer(screenBuffer);
		ClearBuffer(rayBufferTopDown);
		ClearBuffer(rayBufferLeftRight);
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
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
		float2 rayStartVPFloorSpace = vanishingPointWorldSpace.xz;
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<PlaneData> planes = new NativeArray<PlaneData>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);

		if (vanishingPointScreenSpace.y < screenHeight) {
			float distToOtherEnd = screenHeight - vanishingPointScreenSpace.y;
			planes[0] = GetGenericSegmentPlaneParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, 1), 1);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.y;
			planes[1] = GetGenericSegmentPlaneParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, -1), 1);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			float distToOtherEnd = screenWidth - vanishingPointScreenSpace.x;
			planes[2] = GetGenericSegmentPlaneParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(1, 0), 0);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.x;
			planes[3] = GetGenericSegmentPlaneParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(-1, 0), 0);
		}

		Profiler.EndSample();

		Profiler.BeginSample("Draw planes");
		DrawPlanes(planes,
			rayStartVPFloorSpace,
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

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static bool ProjectToScreen (float3 world, ref float4x4 worldToCameraMatrix, float2 screen, bool horizontalSegment, out float y)
	{
		float4 result = mul(worldToCameraMatrix, new float4(world, 1f));
		if (result.z < 0f) {
			y = 0;
			return false;
		}
		if (result.w == 0f) {
			result.w = 0.000001f;// would return 0,0 but that breaks rasterizing the line
		}
		float usedDimension = select(result.y, result.x, horizontalSegment);
		float scaler = select(screen.y, screen.x, horizontalSegment);
		y = (usedDimension / result.w + 1f) * .5f * scaler;
		return true;
	}

	static void DrawPlanes (
		NativeArray<PlaneData> planes,
		float2 startWorld,
		World world,
		CameraData camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
		NativeArray<Color32> rayBufferTopDown,
		NativeArray<Color32> rayBufferLeftRight
	)
	{
		int rayBufferTopDownWidth = screenWidth + 2 * screenHeight;
		int rayBufferLeftRightWidth = 2 * screenWidth + screenHeight;
		int rayIndexCumulative = 0;

		float2 screen = new float2(screenWidth, screenHeight);

		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++) {
			PlaneData plane = planes[planeIndex];

			bool horizontal = planeIndex > 1;
			NativeArray<Color32> activeRayBuffer;
			int activeRayBufferWidth, startNextFreeTopPixel, startNextFreeBottomPixel;

			if (planeIndex < 2) {
				activeRayBuffer = rayBufferTopDown;
				activeRayBufferWidth = rayBufferTopDownWidth;
				if (planeIndex == 0) { // top segment
					startNextFreeBottomPixel = max(0, Mathf.FloorToInt(vanishingPointScreenSpace.y));
					startNextFreeTopPixel = screenHeight - 1;
				} else { // bottom segment
					startNextFreeBottomPixel = 0;
					startNextFreeTopPixel = min(screenHeight - 1, Mathf.CeilToInt(vanishingPointScreenSpace.y));
				}
			} else {
				activeRayBuffer = rayBufferLeftRight;
				activeRayBufferWidth = rayBufferLeftRightWidth;
				if (planeIndex == 3) { // left segment
					startNextFreeBottomPixel = 0;
					startNextFreeTopPixel = min(screenWidth - 1, Mathf.CeilToInt(vanishingPointScreenSpace.x));
				} else { // right segment
					startNextFreeBottomPixel = max(0, Mathf.FloorToInt(vanishingPointScreenSpace.x));
					startNextFreeTopPixel = screenWidth - 1;
					rayIndexCumulative = 0; // swapping buffer, reset
				}
			}

			for (int planeRayIndex = 0; planeRayIndex < plane.RayCount; planeRayIndex++, rayIndexCumulative++) {
				float2 endWorld = lerp(plane.MinWorld, plane.MaxWorld, planeRayIndex / (float)plane.RayCount);
				PlaneDDAData ray = new PlaneDDAData(startWorld, endWorld);

				int nextFreeTopPixel = startNextFreeTopPixel;
				int nextFreeBottomPixel = startNextFreeBottomPixel;

				while (world.TryGetVoxelHeight(ray.position, out World.RLEColumn elements)) {
					float2 nextIntersection = ray.NextIntersection;
					float2 lastIntersection = ray.LastIntersection;

					for (int iElement = 0; iElement < elements.Count; iElement++) {
						World.RLEElement element = elements.GetAt(iElement);

						float topWorldY = element.Top;
						float bottomWorldY = element.Bottom - 1f;

						// this makes it "3D" instead of rotated vertical billboards
						float2 topWorldXZ = (topWorldY < camera.Height) ? nextIntersection : lastIntersection;
						float2 bottomWorldXZ = (bottomWorldY > camera.Height) ? nextIntersection : lastIntersection;

						if (!ProjectToScreen(new float3(topWorldXZ.x, topWorldY, topWorldXZ.y), ref camera.WorldToScreenMatrix, screen, horizontal, out float rayBufferYTopScreen)) {
							continue;
						}
						if (!ProjectToScreen(new float3(bottomWorldXZ.x, bottomWorldY, bottomWorldXZ.y), ref camera.WorldToScreenMatrix, screen, horizontal, out float rayBufferYBottomScreen)) {
							continue;
						}

						if (rayBufferYTopScreen < rayBufferYBottomScreen) {
							Swap(ref rayBufferYTopScreen, ref rayBufferYBottomScreen);
						}

						if (rayBufferYTopScreen < nextFreeBottomPixel || rayBufferYBottomScreen > nextFreeTopPixel) {
							continue; // off screen at top/bottom
						}

						int rayBufferYBottom = Mathf.RoundToInt(rayBufferYBottomScreen);
						int rayBufferYTop = Mathf.RoundToInt(rayBufferYTopScreen);

						if (rayBufferYBottom <= nextFreeBottomPixel) {
							rayBufferYBottom = nextFreeBottomPixel;
							nextFreeBottomPixel = max(nextFreeBottomPixel, rayBufferYTop);
						}

						if (rayBufferYTop >= nextFreeTopPixel) {
							rayBufferYTop = nextFreeTopPixel;
							nextFreeTopPixel = min(nextFreeTopPixel, rayBufferYBottom);
						}

						for (int rayBufferY = rayBufferYBottom; rayBufferY <= rayBufferYTop; rayBufferY++) {
							int idx = rayBufferY * activeRayBufferWidth + rayIndexCumulative;
							if (activeRayBuffer[idx].a == 0) {
								activeRayBuffer[idx] = element.Color;
							}
						}
					}

					if (ray.AtEnd) {
						break; // end of ray
					}

					ray.Step();
				}
			}
		}
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
		NativeArray<PlaneData> planes,
		float2 vpScreen,
		NativeArray<Color32> rayBufferTopDown,
		NativeArray<Color32> rayBufferLeftRight,
		NativeArray<Color32> screenBuffer)
	{
		int rayBufferWidthTopDown = screen.x + 2 * screen.y;
		int rayBufferWidthLeftRight = 2 * screen.x + screen.y;

		NativeArray<RaySegmentData> segments = new NativeArray<RaySegmentData>(4, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

		for (int i = 0; i < 4; i++) {
			RaySegmentData plane = segments[i];
			plane.UScale = planes[i].RayCount / (float)(i > 1 ? rayBufferWidthLeftRight : rayBufferWidthTopDown);
			segments[i] = plane;
		}
		{
			RaySegmentData segment = segments[0];
			segment.UOffsetStart = 0f;
			segment.Min = planes[0].MinScreen.x - vpScreen.x;
			segment.Max = planes[0].MaxScreen.x - vpScreen.x;
			segment.rayBufferWidth = rayBufferWidthTopDown;
			segments[0] = segment;

			segment = segments[1];
			segment.UOffsetStart = segments[0].UScale;
			segment.Min = planes[1].MinScreen.x - vpScreen.x;
			segment.Max = planes[1].MaxScreen.x - vpScreen.x;
			segment.rayBufferWidth = rayBufferWidthTopDown;
			segments[1] = segment;

			segment = segments[2];
			segment.UOffsetStart = 0f;
			segment.Min = planes[2].MinScreen.y - vpScreen.y;
			segment.Max = planes[2].MaxScreen.y - vpScreen.y;
			segment.rayBufferWidth = rayBufferWidthLeftRight;
			segments[2] = segment;

			segment = segments[3];
			segment.UOffsetStart = segments[2].UScale;
			segment.Min = planes[3].MinScreen.y - vpScreen.y;
			segment.Max = planes[3].MaxScreen.y - vpScreen.y;
			segment.rayBufferWidth = rayBufferWidthLeftRight;
			segments[3] = segment;

		}
		float2 oneOverDistTopToVP = 1f / (screen - vpScreen);
		float2 oneOverVPScreen = 1f / vpScreen;

		for (int y = 0; y < screen.y; y++) {
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
				RaySegmentData segment;
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

				NativeArray<Color32> rayBuffer = primaryDimension > 0 ? rayBufferLeftRight : rayBufferTopDown;
				int2 pixelScreen = new int2(x, y);
				float planeRayBufferX = unlerp(minmaxX.x, minmaxX.y, pixelScreen[primaryDimension]);
				float u = segment.UOffsetStart + clamp(planeRayBufferX, 0f, 1f) * segment.UScale;
				int rayBufferIdx = Mathf.FloorToInt(u * segment.rayBufferWidth) + pixelScreen[1 - primaryDimension] * segment.rayBufferWidth;
				screenBuffer[screenIdxY + x] = rayBuffer[rayBufferIdx];
			}
		}
	}

	static unsafe void ClearBuffer (NativeArray<Color32> buffer)
	{
		UnsafeUtility.MemClear(NativeArrayUnsafeUtility.GetUnsafePtr(buffer), buffer.Length * sizeof(Color32));
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

	static PlaneData GetGenericSegmentPlaneParameters (
		Camera camera,
		float2 screen,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		float distToOtherEnd,
		float2 neutral,
		int primaryAxis
	)
	{
		PlaneData plane = new PlaneData();

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
			return plane; // 45 degree angles aren't on screen
		}

		if (all(vpScreen >= 0f & vpScreen <= screen)) {
			// vp within bounds, so nothing to clamp angle wise
			plane.MinScreen = simpleCaseMin;
			plane.MaxScreen = simpleCaseMax;
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
			plane.MinScreen = select(cornerLeft, cornerRight, swap);
			plane.MaxScreen = select(cornerRight, cornerLeft, swap);
		}

		plane.MinWorld = ((float3)camera.ScreenToWorldPoint(new float3(plane.MinScreen, camera.farClipPlane))).xz;
		plane.MaxWorld = ((float3)camera.ScreenToWorldPoint(new float3(plane.MaxScreen, camera.farClipPlane))).xz;
		plane.RayCount = Mathf.RoundToInt(plane.MaxScreen[secondaryAxis] - plane.MinScreen[secondaryAxis]);
		return plane;
	}

	struct PlaneDDAData
	{
		public int2 position;

		int2 goal, step;
		float2 start, dir, tDelta, tMax;
		float nextIntersectionDistance;
		float lastIntersectionDistance;

		public bool AtEnd { get { return all(goal == position); } }

		public float2 LastIntersection { get { return start + dir * lastIntersectionDistance; } }
		public float2 NextIntersection { get { return start + dir * nextIntersectionDistance; } }

		public PlaneDDAData (float2 start, float2 end)
		{
			dir = end - start;
			this.start = start;
			if (dir.x == 0f) { dir.x = 0.00001f; }
			if (dir.y == 0f) { dir.y = 0.00001f; }
			position = new int2(floor(start));
			goal = new int2(floor(end));
			float2 rayDirInverse = rcp(dir);
			step = new int2(dir.x >= 0f ? 1 : -1, dir.y >= 0f ? 1 : -1);
			tDelta = min(rayDirInverse * step, 1f);
			tMax = abs((position + max(step, 0f) - start) * rayDirInverse);
			float2 tMaxReverse = abs((position + max(-step, 0f) - start) * -rayDirInverse);
			nextIntersectionDistance = min(tMax.x, tMax.y);
			lastIntersectionDistance = -min(tMaxReverse.x, tMaxReverse.y);
		}

		public void Step ()
		{
			if (tMax.x < tMax.y) {
				tMax.x += tDelta.x;
				position.x += step.x;
			} else {
				tMax.y += tDelta.y;
				position.y += step.y;
			}
			lastIntersectionDistance = nextIntersectionDistance;
			nextIntersectionDistance = min(tMax.x, tMax.y);
		}
	}

	struct PlaneData
	{
		public float2 MinScreen;
		public float2 MaxScreen;
		public float2 MinWorld;
		public float2 MaxWorld;
		public int RayCount;
	}

	struct RaySegmentData
	{
		public float UOffsetStart;
		public float UScale;
		public float Min;
		public float Max;
		public int rayBufferWidth;
	}

	struct CameraData
	{
		public float Height;
		public float4x4 WorldToScreenMatrix;

		public CameraData (Camera camera)
		{
			Height = camera.transform.position.y;
			WorldToScreenMatrix = camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix;
		}
	}
}
