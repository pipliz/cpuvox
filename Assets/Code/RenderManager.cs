using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

public class RenderManager
{
	PlaneData[] Planes = new PlaneData[4];

	public void Draw (NativeArray<Color32> screenBuffer, NativeArray<Color32> rayBuffer, int screenWidth, int screenHeight, World world, Camera camera)
	{
		Debug.DrawLine(new Vector2(0f, 0f), new Vector2(screenWidth, 0f));
		Debug.DrawLine(new Vector2(screenWidth, 0f), new Vector2(screenWidth, screenHeight));
		Debug.DrawLine(new Vector2(screenWidth, screenHeight), new Vector2(0f, screenHeight));
		Debug.DrawLine(new Vector2(0f, screenHeight), new Vector2(0f, 0f));

		Profiler.BeginSample("Clear");
		ClearBuffer(screenBuffer);
		ClearBuffer(rayBuffer);
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
		int rayBufferWidth = screenWidth * 2 + screenHeight * 2;

		if (Mathf.Abs(camera.transform.eulerAngles.x) < 0.01f) {
			Vector3 eulers = camera.transform.eulerAngles;
			eulers.x = Mathf.Sign(eulers.x) * 0.01f;
			camera.transform.eulerAngles = eulers;
		}

		Vector3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		Vector2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		Vector2 rayStartVPFloorSpace = new Vector2(vanishingPointWorldSpace.x, vanishingPointWorldSpace.z);
		Profiler.EndSample();

		for (int i = 0; i < Planes.Length; i++) {
			Planes[i] = default;
		}

		if (vanishingPointScreenSpace.y < screenHeight) {
			GetTopSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out Vector2 topRayEndMinScreenSpace,
				out Vector2 topRayEndMaxScreenSpace
			);

			Debug.DrawLine(vanishingPointScreenSpace, topRayEndMinScreenSpace, Color.red);
			Debug.DrawLine(vanishingPointScreenSpace, topRayEndMaxScreenSpace, Color.red);

			ProjectPlaneParametersScreenToWorld(camera, topRayEndMinScreenSpace, topRayEndMaxScreenSpace, out Vector2 topRayEndMinWorldSpace, out Vector2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[0];

			plane.MinScreen = topRayEndMinScreenSpace;
			plane.MaxScreen = topRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(topRayEndMaxScreenSpace.x - topRayEndMinScreenSpace.x);
			plane.RayCount = Mathf.Max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			GetBottomSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out Vector2 bottomRayEndMinScreenSpace,
				out Vector2 bottomRayEndMaxScreenSpace
			);

			Debug.DrawLine(vanishingPointScreenSpace, bottomRayEndMinScreenSpace, Color.green);
			Debug.DrawLine(vanishingPointScreenSpace, bottomRayEndMaxScreenSpace, Color.green);

			ProjectPlaneParametersScreenToWorld(camera, bottomRayEndMinScreenSpace, bottomRayEndMaxScreenSpace, out Vector2 topRayEndMinWorldSpace, out Vector2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[1];

			plane.MinScreen = bottomRayEndMinScreenSpace;
			plane.MaxScreen = bottomRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(bottomRayEndMaxScreenSpace.x - bottomRayEndMinScreenSpace.x);
			plane.RayCount = Mathf.Max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			GetRightSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out Vector2 rightRayEndMinScreenSpace,
				out Vector2 rightRayEndMaxScreenSpace
			);

			Debug.DrawLine(vanishingPointScreenSpace, rightRayEndMinScreenSpace, Color.cyan);
			Debug.DrawLine(vanishingPointScreenSpace, rightRayEndMaxScreenSpace, Color.cyan);

			ProjectPlaneParametersScreenToWorld(camera, rightRayEndMinScreenSpace, rightRayEndMaxScreenSpace, out Vector2 topRayEndMinWorldSpace, out Vector2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[2];

			plane.MinScreen = rightRayEndMinScreenSpace;
			plane.MaxScreen = rightRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(rightRayEndMaxScreenSpace.y - rightRayEndMinScreenSpace.y);
			plane.RayCount = Mathf.Max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			GetLeftSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out Vector2 leftRayEndMinScreenSpace,
				out Vector2 leftRayEndMaxScreenSpace
			);

			Debug.DrawLine(vanishingPointScreenSpace, leftRayEndMinScreenSpace, Color.yellow);
			Debug.DrawLine(vanishingPointScreenSpace, leftRayEndMaxScreenSpace, Color.yellow);

			ProjectPlaneParametersScreenToWorld(camera, leftRayEndMinScreenSpace, leftRayEndMaxScreenSpace, out Vector2 topRayEndMinWorldSpace, out Vector2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[3];

			plane.MinScreen = leftRayEndMinScreenSpace;
			plane.MaxScreen = leftRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(leftRayEndMaxScreenSpace.y - leftRayEndMinScreenSpace.y);
			plane.RayCount = Mathf.Max(0, plane.RayCount);
		}

		Profiler.BeginSample("Draw planes");
		DrawPlanes(Planes,
			rayStartVPFloorSpace,
			world,
			camera,
			screenWidth,
			screenHeight,
			vanishingPointScreenSpace,
			rayBuffer,
			rayBufferWidth
		);
		Profiler.EndSample();

		Profiler.BeginSample("Blit raybuffer to screen");
		CopyTopRayBufferToScreen(
			screenWidth,
			screenHeight,
			Planes,
			vanishingPointScreenSpace,
			rayBuffer,
			screenBuffer,
			rayBufferWidth
		);
		Profiler.EndSample();
	}

	static Vector3 ProjectToScreen (Vector3 world, ref Matrix4x4 worldToCameraMatrix, float screenWidth, float screenHeight)
	{
		Vector4 result = worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f);
		if (result.w == 0f) {
			return new Vector3(0f, 0f, 0f);
		}
		result.x = (result.x / result.w + 1f) * .5f * screenWidth;
		result.y = (result.y / result.w + 1f) * .5f * screenHeight;
		return result;
	}

	static void DrawPlanes (
		PlaneData[] planes,
		Vector2 startWorld,
		World world,
		Camera camera,
		int screenWidth,
		int screenHeight,
		Vector2 vanishingPointScreenSpace,
		NativeArray<Color32> rayBuffer,
		int rayBufferWidth
	)
	{
		float cameraHeight = camera.transform.position.y;
		int totalRays = 0;
		for (int i = 0; i < planes.Length; i++) {
			totalRays += planes[i].RayCount;
		}

		float screenHeightToWidthRatio = (float)screenHeight / screenWidth;
		planes[0].ColumnHeightScaler = screenHeight / (screenHeight - vanishingPointScreenSpace.y);
		planes[1].ColumnHeightScaler = screenHeight / vanishingPointScreenSpace.y;
		planes[2].ColumnHeightScaler = screenHeight / (screenWidth - vanishingPointScreenSpace.x);
		planes[3].ColumnHeightScaler = screenHeight / vanishingPointScreenSpace.x;

		Matrix4x4 worldToScreenMatrix = camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix;

		int rayIndexCumulative = 0;

		float nearClip = camera.nearClipPlane;
		float farClip = camera.farClipPlane;

		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++) {
			PlaneData plane = planes[planeIndex];
			for (int planeRayIndex = 0; planeRayIndex < plane.RayCount; planeRayIndex++, rayIndexCumulative++) {
				Vector2 endWorld = Vector2.LerpUnclamped(plane.MinWorld, plane.MaxWorld, planeRayIndex / (float)plane.RayCount);
				PlaneDDAData ray = new PlaneDDAData(startWorld, endWorld);

				while (world.TryGetVoxelHeight(ray.position, out World.RLEElement[] elements)) {
					Vector2 nextIntersection = ray.NextIntersection;
					Vector2 lastIntersection = ray.LastIntersection;

					for (int iElement = 0; iElement < elements.Length; iElement++) {
						World.RLEElement element = elements[iElement];

						Vector3 columnTopWorld, columnBottomWorld;

						float topWorldY = element.Top;
						float bottomWorldY = element.Bottom - 1f;

						if (bottomWorldY < cameraHeight) {
							if (topWorldY < cameraHeight) {
								// entire RLE run is below the horizon -> slant it backwards to prevent looking down into a column
								columnTopWorld = new Vector3(nextIntersection.x, topWorldY, nextIntersection.y);
								columnBottomWorld = new Vector3(lastIntersection.x, bottomWorldY, lastIntersection.y);
							} else {
								// RLE run covers the horizon, render the "front plane" of the column
								columnTopWorld = new Vector3(lastIntersection.x, topWorldY, lastIntersection.y);
								columnBottomWorld = new Vector3(lastIntersection.x, bottomWorldY, lastIntersection.y);
							}
						} else {
							// entire RLE run is above the horizon -> slant it the other way around to prevent looking into it
							columnTopWorld = new Vector3(lastIntersection.x, topWorldY, lastIntersection.y);
							columnBottomWorld = new Vector3(nextIntersection.x, bottomWorldY, nextIntersection.y);
						}

						Vector3 columnTopScreen = ProjectToScreen(columnTopWorld, ref worldToScreenMatrix, screenWidth, screenHeight);
						if (columnTopScreen.z < 0f) {
							continue;
						}
						Vector3 columnBottomScreen = ProjectToScreen(columnBottomWorld, ref worldToScreenMatrix, screenWidth, screenHeight);
						if (columnBottomScreen.z < 0f) {
							continue;
						}

						float rayBufferYTopScreen, rayBufferYBottomScreen, unscaledMax;
						if (planeIndex > 1) {
							rayBufferYTopScreen = columnTopScreen.x;
							rayBufferYBottomScreen = columnBottomScreen.x;
							unscaledMax = screenWidth;
						} else {
							rayBufferYTopScreen = columnTopScreen.y;
							rayBufferYBottomScreen = columnBottomScreen.y;
							unscaledMax = screenHeight;
						}

						if (rayBufferYTopScreen < rayBufferYBottomScreen) {
							Swap(ref rayBufferYTopScreen, ref rayBufferYBottomScreen);
						}

						if (rayBufferYTopScreen <= 0f || rayBufferYBottomScreen >= unscaledMax) {
							continue; // off screen at top/bottom
						}

						if (planeIndex == 0) {
							if (vanishingPointScreenSpace.y > 0f) {
								// it's in vp.y .. screenheight space, map to 0 .. screenheight
								rayBufferYTopScreen = (rayBufferYTopScreen - vanishingPointScreenSpace.y) * plane.ColumnHeightScaler;
								rayBufferYBottomScreen = (rayBufferYBottomScreen - vanishingPointScreenSpace.y) * plane.ColumnHeightScaler;
							}
						} else if (planeIndex == 1) {
							if (vanishingPointScreenSpace.y < screenHeight) {
								// it's in 0 .. vp.y space, map to 0 .. screenheight
								rayBufferYTopScreen *= plane.ColumnHeightScaler;
								rayBufferYBottomScreen *= plane.ColumnHeightScaler;
							}
						} else if (planeIndex == 2) {
							if (vanishingPointScreenSpace.x > 0f) {
								// it's in vp.x .. screenwidth space, map to 0 .. screenheight
								rayBufferYTopScreen = (rayBufferYTopScreen - vanishingPointScreenSpace.x) * plane.ColumnHeightScaler;
								rayBufferYBottomScreen = (rayBufferYBottomScreen - vanishingPointScreenSpace.x) * plane.ColumnHeightScaler;
							} else {
								// still need to map from 0 .. screenwidth to 0 .. screenheight
								rayBufferYTopScreen *= screenHeightToWidthRatio;
								rayBufferYBottomScreen *= screenHeightToWidthRatio;
							}
						} else {
							if (vanishingPointScreenSpace.x < screenWidth) {
								// it's in 0 .. vp.x space, map to 0 .. screenheight
								rayBufferYTopScreen *= plane.ColumnHeightScaler;
								rayBufferYBottomScreen *= plane.ColumnHeightScaler;
							} else {
								// still need to map from 0 .. screenwidth to 0 .. screenheight
								rayBufferYTopScreen *= screenHeightToWidthRatio;
								rayBufferYBottomScreen *= screenHeightToWidthRatio;
							}
						}

						int rayBufferYBottom = Mathf.Max(0, Mathf.FloorToInt(rayBufferYBottomScreen));
						int rayBufferYTop = Mathf.Min(screenHeight - 1, Mathf.CeilToInt(rayBufferYTopScreen));

						for (int rayBufferY = rayBufferYBottom; rayBufferY <= rayBufferYTop; rayBufferY++) {
							int idx = rayBufferY * rayBufferWidth + rayIndexCumulative;
							if (rayBuffer[idx].a == 0) {
								rayBuffer[idx] = element.Color;
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
		Vector2 vpScreen,
		NativeArray<Color32> rayBuffer,
		NativeArray<Color32> screenBuffer,
		int rayBufferWidth)
	{
		float rayOffsetCumulative = 0;
		for (int i = 0; i < planes.Length; i++) {
			float scale = planes[i].RayCount / (float)rayBufferWidth;
			planes[i].UScale = scale;
			planes[i].UOffsetStart = rayOffsetCumulative;
			rayOffsetCumulative += scale;
		}

		for (int y = 0; y < screenHeight; y++) {
			for (int x = 0; x < screenWidth; x++) {
				int screenIdx = y * screenWidth + x;
				Vector2 pixelScreen = new Vector2(x, y);
				Vector2 deltaToVP = pixelScreen - vpScreen;
				Vector2 deltaToVPAbs = new Vector2(Mathf.Abs(deltaToVP.x), Mathf.Abs(deltaToVP.y));

				Color32 col = new Color32(0, 0, 0, 255);
				float u, v;

				if (deltaToVPAbs.x < deltaToVPAbs.y) {
					if (deltaToVP.y >= 0f) {
						// top segment (VP below pixel)
						float normalizedY = (y - vpScreen.y) / (screenHeight - vpScreen.y);
						float xLeft = (planes[0].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[0].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = Mathf.InverseLerp(xLeft, xRight, x);
						u = planes[0].UOffsetStart + xLerp * planes[0].UScale;

						float adjustedVP = Mathf.Max(0f, vpScreen.y);
						v = (y - adjustedVP) / (screenHeight - adjustedVP);
					} else {
						//bottom segment (VP above pixel)
						float normalizedY = 1f - (y / vpScreen.y);
						float xLeft = (planes[1].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[1].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = Mathf.InverseLerp(xLeft, xRight, x);
						u = planes[1].UOffsetStart + xLerp * planes[1].UScale;

						float adjustedVP = Mathf.Min(screenHeight, vpScreen.y);
						v = y / adjustedVP;
					}
				} else {
					if (deltaToVP.x > 0f) {
						// right segment (VP left of pixel)

						float normalizedX = (x - vpScreen.x) / (screenWidth - vpScreen.x);
						float yBottom = (planes[2].MinScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yTop = (planes[2].MaxScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yLerp = Mathf.InverseLerp(yBottom, yTop, y);
						u = planes[2].UOffsetStart + yLerp * planes[2].UScale;

						float adjustedVP = Mathf.Max(0f, vpScreen.x);
						v = (x - adjustedVP) / (screenWidth - adjustedVP);
					} else {
						// left segment (VP right of pixel
						float normalizedX = 1f - (x / vpScreen.x);
						float yBottom = (planes[3].MinScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yTop = (planes[3].MaxScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yLerp = Mathf.InverseLerp(yBottom, yTop, y);
						u = planes[3].UOffsetStart + yLerp * planes[3].UScale;

						float adjustedVP = Mathf.Min(screenWidth, vpScreen.x);
						v = x / adjustedVP;
					}
				}

				//screenBuffer[screenIdx] = new Color(u >= 0.5f ? 2f * (u - 0.5f) : 0f, u < 0.5f ? 2f * u : 0f, 0f, 1f);
				screenBuffer[screenIdx] = rayBuffer[Mathf.FloorToInt(u * rayBufferWidth) + Mathf.FloorToInt(v * screenHeight) * rayBufferWidth];
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

	static Vector2 ProjectVanishingPointScreenToWorld (Camera camera, Vector3 worldPos)
	{
		Vector3 screen3D = camera.WorldToScreenPoint(worldPos);
		return new Vector2(screen3D.x, screen3D.y);
	}

	static void ProjectPlaneParametersScreenToWorld (
		Camera camera,
		Vector2 screenMin,
		Vector2 screenMax,
		out Vector2 flatWorldMin,
		out Vector2 flatWorldMax)
	{
		Vector3 worldMin = camera.ScreenToWorldPoint(new Vector3(screenMin.x, screenMin.y, camera.farClipPlane));
		Vector3 worldMax = camera.ScreenToWorldPoint(new Vector3(screenMax.x, screenMax.y, camera.farClipPlane));
		flatWorldMin = new Vector2(worldMin.x, worldMin.z);
		flatWorldMax = new Vector2(worldMax.x, worldMax.z);
	}

	static void GetTopSegmentPlaneParameters (
		int screenWidth,
		int screenHeight,
		Vector2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out Vector2 endMinScreen,
		out Vector2 endMaxScreen
	) {
		float distToTop = Mathf.Abs(screenHeight - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight)
		{
			// VP is in bounds, simple case
			endMinScreen = new Vector2(vpScreen.x - distToTop, screenHeight);
			endMaxScreen = new Vector2(vpScreen.x + distToTop, screenHeight);
			return;
		}

		// bottom left corner of screen, etc
		Vector2 screenBottomLeft = new Vector2(0f, 0f);
		Vector2 screenBottomRight = new Vector2(screenWidth, 0f);

		if (vpScreen.x < 0f) {
			endMinScreen = new Vector2(0f, screenHeight);
			endMaxScreen = TryAngleClamp(screenBottomRight, true);
		} else if (vpScreen.x > screenWidth) {
			endMinScreen = TryAngleClamp(screenBottomLeft, false);
			endMaxScreen = new Vector2(screenWidth, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(screenBottomLeft, false);
			endMaxScreen = TryAngleClamp(screenBottomRight, true);
		}

		Vector2 TryAngleClamp (Vector2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.up, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (distToTop / -vpScreen.y);
			} else {
				return new Vector2(vpScreen.x + (isRight ? distToTop : -distToTop), screenHeight);
			}
		}
	}

	static void GetBottomSegmentPlaneParameters (
		int screenWidth,
		int screenHeight,
		Vector2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out Vector2 endMinScreen,
		out Vector2 endMaxScreen
	) {
		float distToBottom = Mathf.Abs(0f - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new Vector2(vpScreen.x - distToBottom, 0f);
			endMaxScreen = new Vector2(vpScreen.x + distToBottom, 0f);
			return;
		}

		// bottom left corner of screen, etc
		Vector2 screenTopLeft = new Vector2(0f, screenHeight);
		Vector2 screenTopRight = new Vector2(screenWidth, screenHeight);

		if (vpScreen.x < 0f) {
			endMinScreen = new Vector2(0f, 0f);
			endMaxScreen = TryAngleClamp(screenTopRight, true);
		} else if (vpScreen.x > screenWidth) {
			endMinScreen = TryAngleClamp(screenTopLeft, false);
			endMaxScreen = new Vector2(screenWidth, 0f);
		} else {
			endMinScreen = TryAngleClamp(screenTopLeft, false);
			endMaxScreen = TryAngleClamp(screenTopRight, true);
		}

		Vector2 TryAngleClamp (Vector2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.down, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (vpScreen.y / (vpScreen.y - screenHeight));
			} else {
				return new Vector2(vpScreen.x + (isRight ? distToBottom : -distToBottom), 0f);
			}
		}
	}

	static void GetRightSegmentPlaneParameters (
		int screenWidth,
		int screenHeight,
		Vector2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out Vector2 endMinScreen,
		out Vector2 endMaxScreen
	)
	{
		float distToRight = screenWidth - vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new Vector2(screenWidth, vpScreen.y - distToRight);
			endMaxScreen = new Vector2(screenWidth, vpScreen.y + distToRight);
			return;
		}

		// bottom left corner of screen, etc
		Vector2 screenRightBottom = new Vector2(screenWidth, 0f);
		Vector2 screenRightTop = new Vector2(screenWidth, screenHeight);

		if (vpScreen.y < 0f) { // below screen, casting to the right
			endMinScreen = new Vector2(screenWidth, 0f);
			endMaxScreen = TryAngleClamp(new Vector2(0f, screenHeight), true);
		} else if (vpScreen.y > screenHeight) { // above screen, casting to the right
			endMinScreen = TryAngleClamp(new Vector2(0f, 0f), false);
			endMaxScreen = new Vector2(screenWidth, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(new Vector2(0f, 0f), false);
			endMaxScreen = TryAngleClamp(new Vector2(0f, screenHeight), true);
		}

		Vector2 TryAngleClamp (Vector2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.right, point - vpScreen) < 45) {
				// scaler from -vp.x .. 0 to -vp.x .. screenWidth
				return vpScreen + (point - vpScreen) * (distToRight / -vpScreen.x);
			} else {
				return new Vector2(screenWidth, vpScreen.y + (isRight ? distToRight : -distToRight));
			}
		}
	}

	static void GetLeftSegmentPlaneParameters (
		int screenWidth,
		int screenHeight,
		Vector2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out Vector2 endMinScreen,
		out Vector2 endMaxScreen
	)
	{
		float distToLeft = vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new Vector2(0f, vpScreen.y - distToLeft);
			endMaxScreen = new Vector2(0f, vpScreen.y + distToLeft);
			return;
		}

		if (vpScreen.y < 0f) { // below screen, casting to the left
			endMinScreen = new Vector2(0f, 0f);
			endMaxScreen = TryAngleClamp(new Vector2(screenWidth, screenHeight), true);
		} else if (vpScreen.y > screenHeight) { // above screen, casting to the left
			endMinScreen = TryAngleClamp(new Vector2(screenWidth, 0), false);
			endMaxScreen = new Vector2(0f, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(new Vector2(screenWidth, 0f), false);
			endMaxScreen = TryAngleClamp(new Vector2(screenWidth, screenHeight), true);
		}

		Vector2 TryAngleClamp (Vector2 point, bool isRight)
		{
			if (Vector2.Angle(Vector2.left, point - vpScreen) < 45) {
				return vpScreen + (point - vpScreen) * (vpScreen.x / (vpScreen.x - screenWidth));
			} else {
				return new Vector2(0f, vpScreen.y + (isRight ? distToLeft : -distToLeft));
			}
		}
	}

	struct PlaneDDAData
	{
		public Vector2Int position;

		Vector2Int goal, step;
		Vector2 start, dir, tDelta, tMax;
		float nextIntersectionDistance;
		float lastIntersectionDistance;

		public bool AtEnd { get { return goal == position; } }

		public Vector2 LastIntersection { get { return start + dir * lastIntersectionDistance; } }
		public Vector2 NextIntersection { get { return start + dir * nextIntersectionDistance; } }

		public PlaneDDAData (Vector2 start, Vector2 end)
		{
			dir = end - start;
			this.start = start;
			if (dir.x == 0f) { dir.x = 0.00001f; }
			if (dir.y == 0f) { dir.y = 0.00001f; }
			position = Vector2Int.FloorToInt(start);
			goal = Vector2Int.FloorToInt(end);
			Vector2 rayDirInverse = new Vector2(1f / dir.x, 1f / dir.y);
			step = new Vector2Int(dir.x >= 0f ? 1 : -1, dir.y >= 0f ? 1 : -1);
			tDelta = new Vector2
			{
				x = Mathf.Min(rayDirInverse.x * step.x, 1f),
				y = Mathf.Min(rayDirInverse.y * step.y, 1f),
			};

			tMax = new Vector2
			{
				x = Mathf.Abs((position.x + Mathf.Max(step.x, 0f) - start.x) * rayDirInverse.x),
				y = Mathf.Abs((position.y + Mathf.Max(step.y, 0f) - start.y) * rayDirInverse.y),
			};

			Vector2 tMaxReverse = new Vector2
			{
				x = Mathf.Abs((position.x + Mathf.Max(-step.x, 0f) - start.x) * -rayDirInverse.x),
				y = Mathf.Abs((position.y + Mathf.Max(-step.y, 0f) - start.y) * -rayDirInverse.y),
			};

			nextIntersectionDistance = Mathf.Min(tMax.x, tMax.y);
			lastIntersectionDistance = -Mathf.Min(tMaxReverse.x, tMaxReverse.y);
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
			nextIntersectionDistance = Mathf.Min(tMax.x, tMax.y);
		}
	}

	struct PlaneData
	{
		public Vector2 MinScreen;
		public Vector2 MaxScreen;
		public Vector2 MinWorld;
		public Vector2 MaxWorld;
		public int RayCount;

		public float UOffsetStart;
		public float UScale;

		public float ColumnHeightScaler;
	}
}
