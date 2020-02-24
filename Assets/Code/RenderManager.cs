using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;

public class RenderManager
{
	public void Draw (NativeArray<Color32> screenBuffer, NativeArray<Color32> rayBuffer, int screenWidth, int screenHeight, World world, GameObject cameraObject)
	{
		Color32 white = Color.white;
		Color32 black = Color.black;
		Color32 clearCol = new Color32(0, 0, 0, 0);

		Profiler.BeginSample("Clear");
		unsafe {
			var ptr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(screenBuffer);
			Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(ptr, screenBuffer.Length * sizeof(Color32));

			ptr = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(rayBuffer);
			Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(ptr, rayBuffer.Length * sizeof(Color32));
		}
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
		Vector3 cameraPos = cameraObject.transform.position;
		Camera camera = cameraObject.GetComponent<Camera>();
		float farClipPlane = camera.farClipPlane;
		float nearClipPlane = camera.nearClipPlane;
		int rayBufferWidth = screenWidth + screenHeight * 2;

		Vector3 vanishingPointWorldSpace;
		{
			float rot = Mathf.Sin(camera.transform.eulerAngles.x * Mathf.Deg2Rad);
			if (rot == 0f) {
				rot = 0.0001f;
				//Debug.LogWarning($"Failed to find vanishing point; screen perfectly vertical?");
				//return;
			}
			vanishingPointWorldSpace = cameraPos + Vector3.up * (-nearClipPlane / rot);
		}

		Vector2 vanishingPointScreenSpace;
		{
			Vector3 tmp = camera.WorldToScreenPoint(vanishingPointWorldSpace);
			vanishingPointScreenSpace = new Vector2(tmp.x, tmp.y);
		}
		Vector2 vanishingPointScreenSpaceNormalized = vanishingPointScreenSpace / new Vector2(screenWidth, screenHeight);

		float pixelsToScreenBorder = Mathf.Abs(screenHeight - vanishingPointScreenSpace.y);
		int maxPlaneCount = screenWidth + 2 * screenHeight;
		
		int radialPlaneCount = Mathf.RoundToInt(2 * pixelsToScreenBorder);
		Vector2 rayEndMinScreenSpace, rayEndMaxScreenSpace;
		if (radialPlaneCount > maxPlaneCount) {
			radialPlaneCount = maxPlaneCount;
			// vanishingPoint is so far below the screen that some screen-space diagonal will remain out of bounds in screen space
			// so we need to clamp the min/max corners to keep the rays within screen space bounds
			float scaler = pixelsToScreenBorder / -vanishingPointScreenSpace.y;
			rayEndMinScreenSpace = vanishingPointScreenSpace + (-vanishingPointScreenSpace) * scaler;
			rayEndMaxScreenSpace = vanishingPointScreenSpace + (new Vector2(screenWidth, 0f) - vanishingPointScreenSpace) * scaler;
		} else {
			rayEndMinScreenSpace = new Vector2(vanishingPointScreenSpace.x - pixelsToScreenBorder, screenHeight);
			rayEndMaxScreenSpace = new Vector2(vanishingPointScreenSpace.x + pixelsToScreenBorder, screenHeight);
		}

		Vector3 rayEndMinWorldSpace = camera.ScreenToWorldPoint(new Vector3(rayEndMinScreenSpace.x, rayEndMinScreenSpace.y, camera.farClipPlane));
		Vector3 rayEndMaxWorldSpace = camera.ScreenToWorldPoint(new Vector3(rayEndMaxScreenSpace.x, rayEndMaxScreenSpace.y, camera.farClipPlane));
		Vector2 rayEndMin = new Vector2(rayEndMinWorldSpace.x, rayEndMinWorldSpace.z);
		Vector2 rayEndMax = new Vector2(rayEndMaxWorldSpace.x, rayEndMaxWorldSpace.z);

		Vector2 rayStartVPFloorSpace = new Vector2(vanishingPointWorldSpace.x, vanishingPointWorldSpace.z);

		Profiler.EndSample();

		Profiler.BeginSample("Planes to raybuffer");
		for (int planeIndex = 0; planeIndex < radialPlaneCount; planeIndex++) {
			float quarterProgress = planeIndex / (float)radialPlaneCount;
			Vector2 rayEndVPFloorSpace = Vector2.LerpUnclamped(rayEndMin, rayEndMax, quarterProgress);
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

					if (vanishingPointScreenSpaceNormalized.y > 0f) {
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

		Profiler.BeginSample("Raybuffer to screen");
		float usedRadialPlanesPortion = (float)radialPlaneCount / rayBufferWidth;

		for (int y = 0; y < screenHeight; y++) {
			for (int x = 0; x < screenWidth; x++) {
				if (y > vanishingPointScreenSpace.y) {
					// we're above the vanishing point (so not segment 4, hopefully 2, maybe 1 or 3)
					Vector2 deltaToVP = new Vector2(x, y) - vanishingPointScreenSpace;
					if (Mathf.Abs(deltaToVP.x) < deltaToVP.y) {
						// this pixel is segment 2

						float minmax = Mathf.Min(deltaToVP.y, screenWidth / 2f);
						float u = Mathf.InverseLerp(-deltaToVP.y, deltaToVP.y, deltaToVP.x) * usedRadialPlanesPortion;

						float adjustedVP = Mathf.Max(0f, vanishingPointScreenSpace.y);
						float v = (y - adjustedVP) / (screenHeight - adjustedVP);

						//u = v;
						//screenBuffer[y * screenWidth + x] = new Color(u < 0.5f ? u * 2f : 0f, u >= 0.5f ? (2f * (u - 0.5f)) : 0f, 0f);

						int rayBufferXPixel = Mathf.RoundToInt(u * rayBufferWidth);
						int rayBufferYPixel = Mathf.RoundToInt(v * screenHeight);
						Color32 rayBufferPixel = rayBuffer[rayBufferYPixel * rayBufferWidth + rayBufferXPixel];
						screenBuffer[y * screenWidth + x] = rayBufferPixel;
					}
				}
			}
		}
		Profiler.EndSample();
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
