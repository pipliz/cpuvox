using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

public class RenderManager
{
	PlaneData[] Planes = new PlaneData[4];

	public void Draw (NativeArray<Color32> screenBuffer, NativeArray<Color32> rayBuffer, int screenWidth, int screenHeight, World world, GameObject cameraObject)
	{
		Profiler.BeginSample("Clear");
		ClearBuffer(screenBuffer);
		ClearBuffer(rayBuffer);
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
		Camera camera = cameraObject.GetComponent<Camera>();
		int rayBufferWidth = screenWidth + screenHeight * 2;

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
			plane.PlaneCount = Mathf.RoundToInt(topRayEndMaxScreenSpace.x - topRayEndMinScreenSpace.x);
			plane.PlaneCount = Mathf.Max(0, plane.PlaneCount);
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
			plane.PlaneCount = Mathf.RoundToInt(bottomRayEndMaxScreenSpace.x - bottomRayEndMinScreenSpace.x);
			plane.PlaneCount = Mathf.Max(0, plane.PlaneCount);
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
			totalRays += planes[i].PlaneCount;
		}

		int rayIndexCumulative = 0;
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++) {
			PlaneData plane = planes[planeIndex];
			for (int planeRayIndex = 0; planeRayIndex < plane.PlaneCount; planeRayIndex++, rayIndexCumulative++) {
				Vector2 endWorld = Vector2.LerpUnclamped(plane.MinWorld, plane.MaxWorld, planeRayIndex / (float)plane.PlaneCount);
				PlaneDDAData ray = new PlaneDDAData(startWorld, endWorld);

				while (world.TryGetVoxelHeight(ray.position, out World.RLEElement[] elements)) {
					Vector2 nextIntersection = ray.NextIntersection;
					Vector2 lastIntersection = ray.LastIntersection;

					for (int iElement = 0; iElement < elements.Length; iElement++) {
						World.RLEElement element = elements[iElement];

						Vector3 columnTopScreen, columnBottomScreen;

						if (element.Bottom < cameraHeight) {
							if (element.Top < cameraHeight) {
								// entire RLE run is below the horizon -> slant it backwards to prevent looking down into a column
								columnTopScreen = new Vector3(nextIntersection.x, element.Top, nextIntersection.y);
								columnBottomScreen = new Vector3(lastIntersection.x, element.Bottom, lastIntersection.y);
							} else {
								// RLE run covers the horizon, render the "front plane" of the column
								columnTopScreen = new Vector3(lastIntersection.x, element.Top, lastIntersection.y);
								columnBottomScreen = new Vector3(lastIntersection.x, element.Bottom, lastIntersection.y);
							}
						} else {
							// entire RLE run is above the horizon -> slant it the other way around to prevent looking into it
							columnTopScreen = new Vector3(lastIntersection.x, element.Top, lastIntersection.y);
							columnBottomScreen = new Vector3(nextIntersection.x, element.Bottom, nextIntersection.y);
						}

						columnTopScreen = camera.WorldToScreenPoint(columnTopScreen);
						columnBottomScreen = camera.WorldToScreenPoint(columnBottomScreen);

						if (columnTopScreen.z < 0f || columnBottomScreen.z < 0f) {
							// column (partially) not in view (z >= 0 -> behind camera)
							continue;
						}

						if (columnTopScreen.y < columnBottomScreen.y) {
							float temp = columnTopScreen.y;
							columnTopScreen.y = columnBottomScreen.y;
							columnBottomScreen.y = temp;
						}

						float rayBufferYTopScreen = columnTopScreen.y;
						float rayBufferYBottomScreen = columnBottomScreen.y;

						if (rayBufferYTopScreen <= 0f || rayBufferYBottomScreen >= screenHeight) {
							continue; // off screen at top/bottom
						}

						if (planeIndex == 0) {
							if (vanishingPointScreenSpace.y > 0f) {
								// it's in vp.y .. screenheight space, map to 0 .. screenheight
								float scaler = screenHeight / (screenHeight - vanishingPointScreenSpace.y);
								rayBufferYTopScreen = (rayBufferYTopScreen - vanishingPointScreenSpace.y) * scaler;
								rayBufferYBottomScreen = (rayBufferYBottomScreen - vanishingPointScreenSpace.y) * scaler;
							}
						} else if (planeIndex == 1) {
							if (vanishingPointScreenSpace.y < screenHeight) {
								// it's in 0 .. vp.y space, map to 0 .. screenheight
								float scaler = screenHeight / vanishingPointScreenSpace.y;
								rayBufferYTopScreen = rayBufferYTopScreen * scaler;
								rayBufferYBottomScreen = rayBufferYBottomScreen * scaler;
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
			float scale = planes[i].PlaneCount / (float)rayBufferWidth;
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
				if (deltaToVPAbs.x < deltaToVPAbs.y) {
					if (deltaToVP.y >= 0f) {
						// top segment (VP below pixel)
						float normalizedY = (y - vpScreen.y) / (screenHeight - vpScreen.y);
						float xLeft = (planes[0].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[0].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = Mathf.InverseLerp(xLeft, xRight, x);
						float u = planes[0].UOffsetStart + xLerp * planes[0].UScale;

						float adjustedVP = Mathf.Max(0f, vpScreen.y);
						float v = (y - adjustedVP) / (screenHeight - adjustedVP);

						col = rayBuffer[Mathf.RoundToInt(u * rayBufferWidth) + Mathf.RoundToInt(v * screenHeight) * rayBufferWidth];
					} else {
						//bottom segment (VP above pixel)
						float normalizedY = 1f - (y / vpScreen.y);
						float xLeft = (planes[1].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[1].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = Mathf.InverseLerp(xLeft, xRight, x);
						float u = planes[1].UOffsetStart + xLerp * planes[1].UScale;

						float adjustedVP = Mathf.Min(screenHeight, vpScreen.y);
						float v = y / adjustedVP;

						col = rayBuffer[Mathf.FloorToInt(u * rayBufferWidth) + Mathf.FloorToInt(v * screenHeight) * rayBufferWidth];
					}
				} else {
					if (deltaToVP.x > 0f) {
						col = new Color32(0, 0, 255, 255);
					} else {
						col = new Color32(255, 255, 0, 255);
					}
				}

				screenBuffer[screenIdx] = col;
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
		float rot = Mathf.Sin(transform.eulerAngles.x * Mathf.Deg2Rad);
		if (rot >= 0f && rot < 0.01f) { rot = 0.01f; }
		if (rot < 0f && rot > -0.01f) { rot = -0.01f; }
		return transform.position + Vector3.up * (-camera.nearClipPlane / rot);
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

			nextIntersectionDistance = Mathf.Min(tMax.x, tMax.y);

			float tNowX = start.x - position.x;
			if (step.x < 0) {
				tNowX = 1f - tNowX;
			}
			tNowX *= tMax.x;

			float tNowY = start.y - position.y;
			if (step.y < 0) {
				tNowY = 1f - tNowY;
			}
			tNowY *= tMax.y;
			lastIntersectionDistance = Mathf.Min(Mathf.Abs(tNowX), Mathf.Abs(tNowY));
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

		public int PlaneCount;

		public float UOffsetStart;
		public float UScale;
	}
}
