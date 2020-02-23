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
		int radialPlaneCount = Mathf.Clamp(Mathf.RoundToInt(2 * pixelsToScreenBorder), 0, rayBufferWidth);

		Profiler.EndSample();
		Vector2 rayStartVPFloorSpace = new Vector2(vanishingPointWorldSpace.x, vanishingPointWorldSpace.z);

		Vector2 rayEndMin, rayEndMax;
		{
			Vector2 rayEndMinScreenSpace = new Vector2(vanishingPointScreenSpace.x - pixelsToScreenBorder, screenHeight);
			Vector2 rayEndMaxScreenSpace = new Vector2(vanishingPointScreenSpace.x + pixelsToScreenBorder, screenHeight);
			Vector3 rayEndMinWorldSpace = camera.ScreenToWorldPoint(new Vector3(rayEndMinScreenSpace.x, rayEndMinScreenSpace.y, camera.farClipPlane));
			Vector3 rayEndMaxWorldSpace = camera.ScreenToWorldPoint(new Vector3(rayEndMaxScreenSpace.x, rayEndMaxScreenSpace.y, camera.farClipPlane));
			rayEndMin = new Vector2(rayEndMinWorldSpace.x, rayEndMinWorldSpace.z);
			rayEndMax = new Vector2(rayEndMaxWorldSpace.x, rayEndMaxWorldSpace.z);
		}

		Profiler.BeginSample("Planes to raybuffer");
		for (int planeIndex = 0; planeIndex < radialPlaneCount; planeIndex++) {
			float quarterProgress = planeIndex / (float)radialPlaneCount;
			Vector2 rayEndVPFloorSpace = Vector2.LerpUnclamped(rayEndMin, rayEndMax, quarterProgress);

			//Debug.DrawLine(vanishingPointScreenSpace, rayEndScreenSpace, Color.red);
			//Debug.DrawLine(
			//	new Vector3(rayStartVPFloorSpace.x, 0.1f, rayStartVPFloorSpace.y),
			//	new Vector3(rayEndVPFloorSpace.x, 0.1f, rayEndVPFloorSpace.y),
			//	Color.red
			//);

			PlaneDDAData ddaData = PlaneDDAData.Create(rayStartVPFloorSpace, rayEndVPFloorSpace);

			while (world.TryGetVoxelHeight(ddaData.position, out int voxelHeight, out Color32 voxelColor)) {
				Vector2 nextIntersection = ddaData.NextIntersection;
				Vector2 lastIntersection = ddaData.LastIntersection;
				Vector3 columnTopScreen = camera.WorldToScreenPoint(new Vector3(nextIntersection.x, voxelHeight, nextIntersection.y));
				Vector3 columnBottomScreen = camera.WorldToScreenPoint(new Vector3(lastIntersection.x, 0f, lastIntersection.y));

				if (columnTopScreen.z < 0f && columnBottomScreen.z < 0f) {
					// column is not in view at all (z >= 0 -> behind camera)
					goto STEP;
				}

				float rayBufferYTopScreen = columnTopScreen.y;
				float rayBufferYBottomScreen = columnBottomScreen.y;

				if (vanishingPointScreenSpaceNormalized.y > 0f) {
					// it's in vp.y .. screenheight space, map to 0 .. screenhieght
					float scaler = screenHeight / (screenHeight - vanishingPointScreenSpace.y);

					rayBufferYTopScreen = (rayBufferYTopScreen - vanishingPointScreenSpace.y) * scaler;
					rayBufferYBottomScreen = (rayBufferYBottomScreen - vanishingPointScreenSpace.y) * scaler;
				}

				if (rayBufferYTopScreen < rayBufferYBottomScreen) {
					float temp = rayBufferYTopScreen;
					rayBufferYTopScreen = rayBufferYBottomScreen;
					rayBufferYBottomScreen = temp;
				}

				int rayBufferYTop = Mathf.CeilToInt(rayBufferYTopScreen);
				int rayBufferYBottom = Mathf.FloorToInt(rayBufferYBottomScreen);

				if (rayBufferYTop < 0 || rayBufferYBottom >= screenHeight) {
					goto STEP;
				}

				rayBufferYBottom = Mathf.Max(0, rayBufferYBottom);
				rayBufferYTop = Mathf.Min(screenHeight - 1, rayBufferYTop);

				for (int rayBufferY = rayBufferYBottom; rayBufferY <= rayBufferYTop; rayBufferY++) {
					int idx = rayBufferY * rayBufferWidth + planeIndex;
					if (rayBuffer[idx].a == 0) {
						rayBuffer[idx] = voxelColor;
					}
				}

				STEP:
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

		public static PlaneDDAData Create (Vector2 start, Vector2 end)
		{
			PlaneDDAData data;
			Vector2 rayDir = end - start;
			data.start = start;
			data.dir = rayDir;
			if (rayDir.x == 0f) { rayDir.x = 0.00001f; }
			if (rayDir.y == 0f) { rayDir.y = 0.00001f; }
			data.position = Vector2Int.FloorToInt(start);
			data.goal = Vector2Int.FloorToInt(end);
			Vector2 rayDirInverse = new Vector2(1f / rayDir.x, 1f / rayDir.y);
			data.step = new Vector2Int(rayDir.x >= 0f ? 1 : -1, rayDir.y >= 0f ? 1 : -1);
			data.tDelta = new Vector2
			{
				x = Mathf.Min(rayDirInverse.x * data.step.x, 1f),
				y = Mathf.Min(rayDirInverse.y * data.step.y, 1f),
			};
			data.tMax = new Vector2
			{
				x = Mathf.Abs((data.position.x + Mathf.Max(data.step.x, 0f) - data.position.x) * rayDirInverse.x),
				y = Mathf.Abs((data.position.y + Mathf.Max(data.step.y, 0f) - data.position.y) * rayDirInverse.y),
			};
			data.nextIntersectionDistance = Mathf.Min(data.tMax.x, data.tMax.y);
			data.lastIntersectionDistance = 0f;

			return data;
		}
	}
}
