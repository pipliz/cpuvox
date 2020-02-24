﻿using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

public class RenderManager
{
	public void Draw (NativeArray<Color32> screenBuffer, NativeArray<Color32> rayBuffer, int screenWidth, int screenHeight, World world, GameObject cameraObject)
	{
		Profiler.BeginSample("Clear");
		ClearBuffer(screenBuffer);
		ClearBuffer(rayBuffer);
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
		Vector3 cameraPos = cameraObject.transform.position;
		Camera camera = cameraObject.GetComponent<Camera>();
		int rayBufferWidth = screenWidth + screenHeight * 2;

		Vector3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		Vector2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);

		GetTopSegmentPlaneParameters(
			screenWidth,
			screenHeight,
			vanishingPointScreenSpace,
			out Vector2 topRayEndMinScreenSpace,
			out Vector2 topRayEndMaxScreenSpace
		);
		ProjectPlaneParametersScreenToWorld(
			camera,
			topRayEndMinScreenSpace,
			topRayEndMaxScreenSpace,
			out Vector2 topRayEndMinWorldSpace,
			out Vector2 topRayEndMaxWorldspace
		);

		int radialPlaneCount = Mathf.RoundToInt(topRayEndMaxScreenSpace.x - topRayEndMinScreenSpace.x);
		Vector2 rayStartVPFloorSpace = new Vector2(vanishingPointWorldSpace.x, vanishingPointWorldSpace.z);
		Profiler.EndSample();

		Profiler.BeginSample("Planes to raybuffer");
		for (int planeIndex = 0; planeIndex < radialPlaneCount; planeIndex++) {
			float quarterProgress = planeIndex / (float)radialPlaneCount;
			Vector2 rayEndVPFloorSpace = Vector2.LerpUnclamped(topRayEndMinWorldSpace, topRayEndMaxWorldspace, quarterProgress);
			PlaneDDAData ddaData = new PlaneDDAData(rayStartVPFloorSpace, rayEndVPFloorSpace);

			while (world.TryGetVoxelHeight(ddaData.position, out World.RLEElement[] elements)) {
				Vector2 nextIntersection = ddaData.NextIntersection;
				Vector2 lastIntersection = ddaData.LastIntersection;
				for (int iElement = 0; iElement < elements.Length; iElement++) {
					World.RLEElement element = elements[iElement];

					Vector3 columnTopScreen, columnBottomScreen;
					
					if (element.Bottom < cameraPos.y) {
						if (element.Top < cameraPos.y) {
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

					float rayBufferYTopScreen = columnTopScreen.y;
					float rayBufferYBottomScreen = columnBottomScreen.y;

					if (rayBufferYTopScreen <= 0f || rayBufferYBottomScreen >= screenHeight) {
						continue; // off screen at top/bottom
					}

					if (vanishingPointScreenSpace.y > 0f) {
						// it's in vp.y .. screenheight space, map to 0 .. screenhieght
						float scaler = screenHeight / (screenHeight - vanishingPointScreenSpace.y);

						rayBufferYTopScreen = (rayBufferYTopScreen - vanishingPointScreenSpace.y) * scaler;
						rayBufferYBottomScreen = (rayBufferYBottomScreen - vanishingPointScreenSpace.y) * scaler;
					}

					int rayBufferYBottom = Mathf.Max(0, Mathf.FloorToInt(rayBufferYBottomScreen));
					int rayBufferYTop = Mathf.Min(screenHeight - 1, Mathf.CeilToInt(rayBufferYTopScreen));

					for (int rayBufferY = rayBufferYBottom; rayBufferY <= rayBufferYTop; rayBufferY++) {
						int idx = rayBufferY * rayBufferWidth + planeIndex;
						if (rayBuffer[idx].a == 0) {
							rayBuffer[idx] = element.Color;
						}
					}
				}

				if (ddaData.AtEnd) {
					break; // end of ray
				}

				ddaData.Step();
			}
		}

		Profiler.EndSample();

		Debug.DrawLine(vanishingPointScreenSpace, topRayEndMinScreenSpace, Color.red);
		Debug.DrawLine(vanishingPointScreenSpace, topRayEndMaxScreenSpace, Color.red);

		Profiler.BeginSample("Raybuffer to screen");
		{
			float usedRadialPlanesPortion = (float)radialPlaneCount / rayBufferWidth;

			if (vanishingPointScreenSpace.y < screenHeight)
			{
				// draw top segment

				Vector2 topLeft = topRayEndMinScreenSpace;
				Vector2 topRight = topRayEndMaxScreenSpace;
				Vector2 bottom = vanishingPointScreenSpace;

				float leftSlope = (topLeft.x - bottom.x) / (topLeft.y - bottom.y);
				float rightSlope = (topRight.x - bottom.x) / (topRight.y - bottom.y);

				float leftX = bottom.x;
				float rightX = bottom.x;

				for (float scanlineY = bottom.y; scanlineY <= topLeft.y; scanlineY++) {
					int y = (int)scanlineY;
					if (y >= 0 && y >= vanishingPointScreenSpace.y && y < screenHeight) {
						int minX = Mathf.Max(Mathf.RoundToInt(leftX), 0);
						int maxX = Mathf.Min(Mathf.RoundToInt(rightX), screenWidth - 1);

						float adjustedVP = Mathf.Max(0f, vanishingPointScreenSpace.y);
						float v = (y - adjustedVP) / (screenHeight - adjustedVP);
						int rayBufferYPixel = Mathf.RoundToInt(v * screenHeight);
						int rayBufferYIndex = rayBufferYPixel * rayBufferWidth;
						int screenYIndex = y * screenWidth;

						float deltaToVPY = y - vanishingPointScreenSpace.y;

						for (int x = minX; x <= maxX; x++) {
							float u = Mathf.InverseLerp(leftX, rightX, x) * usedRadialPlanesPortion;
							int rayBufferXPixel = Mathf.RoundToInt(u * rayBufferWidth);

							Color32 rayBufferPixel = rayBuffer[rayBufferYIndex + rayBufferXPixel];
							screenBuffer[screenYIndex + x] = rayBufferPixel;
						}
					}

					leftX += leftSlope;
					rightX += rightSlope;
				}
			}
		}
		Profiler.EndSample();
	}

	static unsafe void ClearBuffer (NativeArray<Color32> buffer)
	{
		UnsafeUtility.MemClear(NativeArrayUnsafeUtility.GetUnsafePtr(buffer), buffer.Length * sizeof(Color32));
	}

	static Vector3 CalculateVanishingPointWorld (Camera camera)
	{
		Transform transform = camera.transform;
		float rot = Mathf.Sin(transform.eulerAngles.x * Mathf.Deg2Rad);
		if (rot == 0f) { rot = 0.0001f; }
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
		out Vector2 boundsMinScreen,
		out Vector2 boundsMaxScreen
	) {
		float distToTop = Mathf.Abs(screenHeight - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight)
		{
			// VP is in bounds, simple mode
			boundsMinScreen = new Vector2(vpScreen.x - distToTop, screenHeight);
			boundsMaxScreen = new Vector2(vpScreen.x + distToTop, screenHeight);
			return;
		}

		// bottom left corner of screen, etc
		Vector2 screenBL = new Vector2(0f, 0f);
		Vector2 screenTL = new Vector2(0f, screenHeight);
		Vector2 screenTR = new Vector2(screenWidth, screenHeight);
		Vector2 screenBR = new Vector2(screenWidth, 0f);

		// scales ray from (-vp.y .. 0) to (-vp.y .. screenheight)
		float scaler = distToTop / -vpScreen.y;

		if (vpScreen.x < 0f) {
			// vp is off screen to the bottomleft (camera roll)
			boundsMinScreen = screenTL;
			if (Vector2.Angle(Vector2.up, screenBR - vpScreen) < 45) {
				// bottom right fits on screen ?
				boundsMaxScreen = vpScreen + (screenBR - vpScreen) * scaler;
			} else {
				// bottomright doesn't fit, use 45 degrees
				boundsMaxScreen = new Vector2(vpScreen.x + distToTop, screenHeight);
			}
		} else if (vpScreen.x > screenWidth) {
			// vp is off screen to the right (camera roll)
			boundsMaxScreen = screenTR;
			if (Vector2.Angle(Vector2.up, screenBL - vpScreen) < 45) {
				// bottom left fits on screen ?
				boundsMinScreen = vpScreen + (screenBL - vpScreen) * scaler;
			} else {
				// bottomleft doesn't fit, use 45 degrees
				boundsMinScreen = new Vector2(vpScreen.x - distToTop, screenHeight);
			}
		} else {
			// vp is only below the screen
			if (-vpScreen.y < screenWidth * 0.5f) {
				// no excess size, use the 'easy' way
				boundsMinScreen = new Vector2(vpScreen.x - distToTop, screenHeight);
				boundsMaxScreen = new Vector2(vpScreen.x + distToTop, screenHeight);
			} else {
				// some rays of the segment are totally of screen, clamp it to the lower corners
				boundsMinScreen = vpScreen + (screenBL - vpScreen) * scaler;
				boundsMaxScreen = vpScreen + (screenBR - vpScreen) * scaler;
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
}
