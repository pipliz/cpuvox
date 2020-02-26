using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

public class RenderManager
{
	PlaneData[] Planes = new PlaneData[]
	{
		new PlaneData(0),
		new PlaneData(1),
		new PlaneData(2),
		new PlaneData(3)
	};

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
		if (abs(camera.transform.eulerAngles.x) < 0.01f) {
			Vector3 eulers = camera.transform.eulerAngles;
			eulers.x = sign(eulers.x) * 0.01f;
			if (eulers.x == 0f) {
				eulers.x = 0.01f;
			}
			camera.transform.eulerAngles = eulers;
		}

		float3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		float2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		float2 rayStartVPFloorSpace = vanishingPointWorldSpace.xz;
		Profiler.EndSample();

		for (int i = 0; i < Planes.Length; i++) {
			Planes[i] = new PlaneData(i);
		}

		if (vanishingPointScreenSpace.y < screenHeight) {
			GetTopSegmentPlaneParameters(camera, ref Planes[0], screenWidth, screenHeight, vanishingPointScreenSpace, Color.red);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			GetBottomSegmentPlaneParameters(camera, ref Planes[1], screenWidth, screenHeight, vanishingPointScreenSpace, Color.green);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			GetRightSegmentPlaneParameters(camera, ref Planes[2], screenWidth, screenHeight, vanishingPointScreenSpace, Color.cyan);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			GetLeftSegmentPlaneParameters(camera, ref Planes[3], screenWidth, screenHeight, vanishingPointScreenSpace, Color.yellow);
		}

		Profiler.BeginSample("Draw planes");
		DrawPlanes(Planes,
			rayStartVPFloorSpace,
			world,
			camera,
			screenWidth,
			screenHeight,
			vanishingPointScreenSpace,
			rayBufferTopDown,
			rayBufferLeftRight
		);
		Profiler.EndSample();

		Profiler.BeginSample("Blit raybuffer to screen");
		CopyTopRayBufferToScreen(
			screenWidth,
			screenHeight,
			Planes,
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
		PlaneData[] planes,
		float2 startWorld,
		World world,
		Camera camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
		NativeArray<Color32> rayBufferTopDown,
		NativeArray<Color32> rayBufferLeftRight
	)
	{
		float cameraHeight = camera.transform.position.y;
		float4x4 worldToScreenMatrix = camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix;
		int rayBufferTopDownWidth = screenWidth + 2 * screenHeight;
		int rayBufferLeftRightWidth = 2 * screenWidth + screenHeight;
		int rayIndexCumulative = 0;

		float nearClip = camera.nearClipPlane;
		float farClip = camera.farClipPlane;
		float2 screen = new float2(screenWidth, screenHeight);

		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++) {
			PlaneData plane = planes[planeIndex];

			bool horizontal = plane.IsHorizontal;
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

				while (world.TryGetVoxelHeight(ray.position, out World.RLEElement[] elements)) {
					float2 nextIntersection = ray.NextIntersection;
					float2 lastIntersection = ray.LastIntersection;

					for (int iElement = 0; iElement < elements.Length; iElement++) {
						World.RLEElement element = elements[iElement];

						float topWorldY = element.Top;
						float bottomWorldY = element.Bottom - 1f;

						// this makes it "3D" instead of rotated vertical billboards
						float2 topWorldXZ = (topWorldY < cameraHeight) ? nextIntersection : lastIntersection;
						float2 bottomWorldXZ = (bottomWorldY > cameraHeight) ? nextIntersection : lastIntersection;

						if (!ProjectToScreen(new float3(topWorldXZ.x, topWorldY, topWorldXZ.y), ref worldToScreenMatrix, screen, horizontal, out float rayBufferYTopScreen)) {
							continue;
						}
						if (!ProjectToScreen(new float3(bottomWorldXZ.x, bottomWorldY, bottomWorldXZ.y), ref worldToScreenMatrix, screen, horizontal, out float rayBufferYBottomScreen)) {
							continue;
						}

						if (rayBufferYTopScreen < rayBufferYBottomScreen) {
							Swap(ref rayBufferYTopScreen, ref rayBufferYBottomScreen);
						}

						if (rayBufferYTopScreen < nextFreeBottomPixel || rayBufferYBottomScreen > nextFreeTopPixel) {
							continue; // off screen at top/bottom
						}

						int rayBufferYBottom = Mathf.FloorToInt(rayBufferYBottomScreen);
						int rayBufferYTop = Mathf.CeilToInt(rayBufferYTopScreen);

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
		int screenWidth,
		int screenHeight,
		PlaneData[] planes,
		float2 vpScreen,
		NativeArray<Color32> rayBufferTopDown,
		NativeArray<Color32> rayBufferLeftRight,
		NativeArray<Color32> screenBuffer)
	{
		int rayBufferWidthTopDown = screenWidth + 2 * screenHeight;
		int rayBufferWidthLeftRight = 2 * screenWidth + screenHeight;

		{
			float rayOffsetCumulative = 0;
			for (int i = 0; i < 2; i++) {
				float scale = planes[i].RayCount / (float)(rayBufferWidthTopDown);
				planes[i].UScale = scale;
				planes[i].UOffsetStart = rayOffsetCumulative;
				rayOffsetCumulative += scale;
			}
			rayOffsetCumulative = 0;
			for (int i = 2; i < 4; i++) {
				float scale = planes[i].RayCount / (float)(rayBufferWidthLeftRight);
				planes[i].UScale = scale;
				planes[i].UOffsetStart = rayOffsetCumulative;
				rayOffsetCumulative += scale;
			}
		}

		for (int y = 0; y < screenHeight; y++) {
			for (int x = 0; x < screenWidth; x++) {
				int screenIdx = y * screenWidth + x;
				float2 pixelScreen = new float2(x, y);
				float2 deltaToVP = pixelScreen - vpScreen;
				float2 deltaToVPAbs = abs(deltaToVP);

				Color32 col = new Color32(0, 0, 0, 255);
				float u;
				int pixelY;

				NativeArray<Color32> activeBuffer;
				int activeBufferWidth;

				if (deltaToVPAbs.x < deltaToVPAbs.y) {
					activeBuffer = rayBufferTopDown;
					activeBufferWidth = rayBufferWidthTopDown;
					pixelY = y;
					if (deltaToVP.y >= 0f) {
						// top segment (VP below pixel)
						float normalizedY = (y - vpScreen.y) / (screenHeight - vpScreen.y);
						float xLeft = (planes[0].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[0].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = unlerp(xLeft, xRight, x);
						xLerp = clamp(xLerp, 0f, 1f); // sometimes it ends up at -0.000001 or something, floating point logic
						u = planes[0].UOffsetStart + xLerp * planes[0].UScale;
					} else {
						//bottom segment (VP above pixel)
						float normalizedY = 1f - (y / vpScreen.y);
						float xLeft = (planes[1].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[1].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = unlerp(xLeft, xRight, x);
						xLerp = clamp(xLerp, 0f, 1f);
						u = planes[1].UOffsetStart + xLerp * planes[1].UScale;
					}
				} else {
					activeBuffer = rayBufferLeftRight;
					activeBufferWidth = rayBufferWidthLeftRight;
					pixelY = x;
					if (deltaToVP.x >= 0f) {
						// right segment (VP left of pixel)
						float normalizedX = (x - vpScreen.x) / (screenWidth - vpScreen.x);
						float yBottom = (planes[2].MinScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yTop = (planes[2].MaxScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yLerp = unlerp(yBottom, yTop, y);
						yLerp = clamp(yLerp, 0f, 1f);
						u = planes[2].UOffsetStart + yLerp * planes[2].UScale;
					} else {
						// left segment (VP right of pixel
						float normalizedX = 1f - (x / vpScreen.x);
						float yBottom = (planes[3].MinScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yTop = (planes[3].MaxScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yLerp = unlerp(yBottom, yTop, y);
						yLerp = clamp(yLerp, 0f, 1f);
						u = planes[3].UOffsetStart + yLerp * planes[3].UScale;
					}
				}

				screenBuffer[screenIdx] = activeBuffer[Mathf.FloorToInt(u * activeBufferWidth) + pixelY * activeBufferWidth];
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

	static void ProjectPlaneParametersScreenToWorld (
		Camera camera,
		float2 screenMin,
		float2 screenMax,
		out float2 flatWorldMin,
		out float2 flatWorldMax)
	{
		float3 worldMin = camera.ScreenToWorldPoint(new Vector3(screenMin.x, screenMin.y, camera.farClipPlane));
		float3 worldMax = camera.ScreenToWorldPoint(new Vector3(screenMax.x, screenMax.y, camera.farClipPlane));
		flatWorldMin = worldMin.xz;
		flatWorldMax = worldMax.xz;
	}

	static void GetTopSegmentPlaneParameters (
		Camera camera,
		ref PlaneData plane,
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		Color debugColor
	)
	{
		float distToTop = abs(screenHeight - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			plane.MinScreen = new float2(vpScreen.x - distToTop, screenHeight);
			plane.MaxScreen = new float2(vpScreen.x + distToTop, screenHeight);
		} else {

			// bottom left corner of screen, etc
			float2 screenBottomLeft = new float2(0f, 0f);
			float2 screenBottomRight = new float2(screenWidth, 0f);

			if (vpScreen.x < 0f) {
				plane.MinScreen = new float2(0f, screenHeight);
				plane.MaxScreen = TryAngleClamp(screenBottomRight, true);
			} else if (vpScreen.x > screenWidth) {
				plane.MinScreen = TryAngleClamp(screenBottomLeft, false);
				plane.MaxScreen = new float2(screenWidth, screenHeight);
			} else {
				plane.MinScreen = TryAngleClamp(screenBottomLeft, false);
				plane.MaxScreen = TryAngleClamp(screenBottomRight, true);
			}
		}

		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MinScreen, debugColor);
		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MaxScreen, debugColor);

		ProjectPlaneParametersScreenToWorld(camera, plane.MinScreen, plane.MaxScreen, out plane.MinWorld, out plane.MaxWorld);
		plane.OnCoordinatesSet();

		float2 TryAngleClamp (float2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.up, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (distToTop / -vpScreen.y);
			} else {
				return new float2(vpScreen.x + (isRight ? distToTop : -distToTop), screenHeight);
			}
		}
	}

	static void GetBottomSegmentPlaneParameters (
		Camera camera,
		ref PlaneData plane,
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		Color debugColor
	) {
		float distToBottom = abs(0f - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			plane.MinScreen = new float2(vpScreen.x - distToBottom, 0f);
			plane.MaxScreen = new float2(vpScreen.x + distToBottom, 0f);
		} else {

			// bottom left corner of screen, etc
			float2 screenTopLeft = new float2(0f, screenHeight);
			float2 screenTopRight = new float2(screenWidth, screenHeight);

			if (vpScreen.x < 0f) {
				plane.MinScreen = new float2(0f, 0f);
				plane.MaxScreen = TryAngleClamp(screenTopRight, true);
			} else if (vpScreen.x > screenWidth) {
				plane.MinScreen = TryAngleClamp(screenTopLeft, false);
				plane.MaxScreen = new Vector2(screenWidth, 0f);
			} else {
				plane.MinScreen = TryAngleClamp(screenTopLeft, false);
				plane.MaxScreen = TryAngleClamp(screenTopRight, true);
			}
		}

		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MinScreen, debugColor);
		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MaxScreen, debugColor);

		ProjectPlaneParametersScreenToWorld(camera, plane.MinScreen, plane.MaxScreen, out plane.MinWorld, out plane.MaxWorld);
		plane.OnCoordinatesSet();

		float2 TryAngleClamp (float2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.down, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (vpScreen.y / (vpScreen.y - screenHeight));
			} else {
				return new float2(vpScreen.x + (isRight ? distToBottom : -distToBottom), 0f);
			}
		}
	}

	static void GetRightSegmentPlaneParameters (
		Camera camera,
		ref PlaneData plane,
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		Color debugColor
	)
	{
		float distToRight = screenWidth - vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			plane.MinScreen = new float2(screenWidth, vpScreen.y - distToRight);
			plane.MaxScreen = new float2(screenWidth, vpScreen.y + distToRight);
		} else {
			// bottom left corner of screen, etc
			float2 screenRightBottom = new float2(screenWidth, 0f);
			float2 screenRightTop = new float2(screenWidth, screenHeight);

			if (vpScreen.y < 0f) { // below screen, casting to the right
				plane.MinScreen = new float2(screenWidth, 0f);
				plane.MaxScreen = TryAngleClamp(new float2(0f, screenHeight), true);
			} else if (vpScreen.y > screenHeight) { // above screen, casting to the right
				plane.MinScreen = TryAngleClamp(new float2(0f, 0f), false);
				plane.MaxScreen = new float2(screenWidth, screenHeight);
			} else {
				plane.MinScreen = TryAngleClamp(new float2(0f, 0f), false);
				plane.MaxScreen = TryAngleClamp(new float2(0f, screenHeight), true);
			}
		}

		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MinScreen, debugColor);
		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MaxScreen, debugColor);

		ProjectPlaneParametersScreenToWorld(camera, plane.MinScreen, plane.MaxScreen, out plane.MinWorld, out plane.MaxWorld);
		plane.OnCoordinatesSet();

		float2 TryAngleClamp (float2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.right, point - vpScreen) < 45) {
				// scaler from -vp.x .. 0 to -vp.x .. screenWidth
				return vpScreen + (point - vpScreen) * (distToRight / -vpScreen.x);
			} else {
				return new float2(screenWidth, vpScreen.y + (isRight ? distToRight : -distToRight));
			}
		}
	}

	static void GetLeftSegmentPlaneParameters (
		Camera camera,
		ref PlaneData plane,
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		Color debugColor
	)
	{
		float distToLeft = vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			plane.MinScreen = new float2(0f, vpScreen.y - distToLeft);
			plane.MaxScreen = new float2(0f, vpScreen.y + distToLeft);
		} else {
			if (vpScreen.y < 0f) { // below screen, casting to the left
				plane.MinScreen = new float2(0f, 0f);
				plane.MaxScreen = TryAngleClamp(new float2(screenWidth, screenHeight), true);
			} else if (vpScreen.y > screenHeight) { // above screen, casting to the left
				plane.MinScreen = TryAngleClamp(new float2(screenWidth, 0), false);
				plane.MaxScreen = new float2(0f, screenHeight);
			} else {
				plane.MinScreen = TryAngleClamp(new float2(screenWidth, 0f), false);
				plane.MaxScreen = TryAngleClamp(new float2(screenWidth, screenHeight), true);
			}
		}

		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MinScreen, debugColor);
		Debug.DrawLine((Vector2)vpScreen, (Vector2)plane.MaxScreen, debugColor);

		ProjectPlaneParametersScreenToWorld(camera, plane.MinScreen, plane.MaxScreen, out plane.MinWorld, out plane.MaxWorld);
		plane.OnCoordinatesSet();

		float2 TryAngleClamp (float2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.left, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (vpScreen.x / (vpScreen.x - screenWidth));
			} else {
				return new float2(0f, vpScreen.y + (isRight ? distToLeft : -distToLeft));
			}
		}
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

		public float UOffsetStart;
		public float UScale;

		public bool IsHorizontal;

		public PlaneData (int idx) : this()
		{
			IsHorizontal = idx > 1;
		}

		public void OnCoordinatesSet ()
		{
			if (IsHorizontal) {
				RayCount = Mathf.RoundToInt(MaxScreen.y - MinScreen.y);
			} else {
				RayCount = Mathf.RoundToInt(MaxScreen.x - MinScreen.x);
			}
		}
	}
}
