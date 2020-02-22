using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;

public class RenderManager
{
	Plane[] frustumPlanes = new Plane[6];

	public void Draw (NativeArray<Color32> screenBuffer, NativeArray<Color32> rayBuffer, int screenWidth, int screenHeight, World world, GameObject cameraObject)
	{
		Color32 white = Color.white;
		Color32 black = Color.black;
		Color32 clearCol = new Color32(0, 0, 0, 0);

		Profiler.BeginSample("Clear");
		for (int i = 0; i < screenBuffer.Length; i++) {
			screenBuffer[i] = clearCol;
		}
		for (int i = 0; i < rayBuffer.Length; i++) {
			rayBuffer[i] = clearCol;
		}
		Profiler.EndSample();

		Profiler.BeginSample("Setup");
		Vector3 cameraPos = cameraObject.transform.position;
		Camera camera = cameraObject.GetComponent<Camera>();
		Matrix4x4 cameraMatrix = camera.worldToCameraMatrix;
		Matrix4x4 screenMatrix = Matrix4x4.Scale(new Vector3(screenWidth, screenHeight, 1f)) * cameraMatrix;
		float farClipPlane = camera.farClipPlane;

		GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
		// Ordering: [0] = Left, [1] = Right, [2] = Down, [3] = Up, [4] = Near, [5] = Far
		if (!frustumPlanes[4].Raycast(new Ray(cameraPos, Vector3.down), out float downToScreenPlaneDistance)) {
			if (downToScreenPlaneDistance == 0f) {
				Debug.LogWarning($"Failed to find vanishing point; screen perfectly vertical?");
				return;
			}
			// returned false with negative distance -> was behind the plane
			downToScreenPlaneDistance = -downToScreenPlaneDistance;
		}

		Vector3 vanishingPointWorldSpace = cameraPos + Vector3.down * downToScreenPlaneDistance;
		//Debug.DrawLine(cameraPos, vanishingPointWorldSpace, Color.green);

		Vector2 vanishingPointScreenSpace;
		{
			Vector3 tmp = camera.WorldToScreenPoint(vanishingPointWorldSpace);
			vanishingPointScreenSpace = new Vector2(tmp.x, tmp.y);
		}
		Plane vanishingPointWorldPlane = new Plane(Vector3.up, vanishingPointWorldSpace);

		// render 4 quarters of the world around the vanishing point below us

		float pixelsToScreenBorder = Mathf.Abs(screenHeight - vanishingPointScreenSpace.y);
		int radialPlaneCount = Mathf.Clamp(Mathf.RoundToInt(2 * pixelsToScreenBorder), 0, screenWidth * 2);

		Profiler.EndSample();

		Profiler.BeginSample("Planes to raybuffer");
		for (int planeIndex = 0; planeIndex < radialPlaneCount; planeIndex++) {
			/// Step 3. Render the planes on GPU:  In each concentric plane, a ray is cast in the x-z plane from the x-z-coordinates of the viewpoint to the maximal view-distance.

			// each plane starts at the vanishingpoint's x/z
			// from there they move out radially into the world
			Vector2 rayStartVPFloorSpace = new Vector2(cameraPos.x, cameraPos.z);

			float quarterProgress = planeIndex / (float)radialPlaneCount;
			// quarterProgress == 0 -> "top-left"
			// quarterProgress == 1 -> "top-right"

			Vector2 rayEndScreenSpace;
			rayEndScreenSpace.x = vanishingPointScreenSpace.x + Mathf.Lerp(-pixelsToScreenBorder, pixelsToScreenBorder, quarterProgress);
			rayEndScreenSpace.y = screenHeight;

			// correct rays going out of screen space
			if (rayEndScreenSpace.x < 0f || rayEndScreenSpace.x > screenWidth) {
				Vector2 screenDir = rayEndScreenSpace - vanishingPointScreenSpace;
				if (rayEndScreenSpace.x < 0f) {
					screenDir = screenDir * (vanishingPointScreenSpace.x / -screenDir.x);
				} else {
					screenDir = screenDir * ((screenWidth - vanishingPointScreenSpace.x) / screenDir.x);
				}
				rayEndScreenSpace = vanishingPointScreenSpace + screenDir;
			}

			//Debug.DrawLine(new Vector3(vanishingPointScreenSpace.x, vanishingPointScreenSpace.y), rayEndScreenSpace, Color.red);

			Vector3 rayEndWorldSpace = camera.ScreenToWorldPoint(new Vector3(rayEndScreenSpace.x, rayEndScreenSpace.y, camera.farClipPlane));
			Vector2 rayEndVPFloorSpace = new Vector2(rayEndWorldSpace.x, rayEndWorldSpace.z);

			// set up DDA raycast
			Vector2 rayDir = rayEndVPFloorSpace - rayStartVPFloorSpace;
			if (rayDir.x == 0f) { rayDir.x = 0.00001f; }
			if (rayDir.y == 0f) { rayDir.y = 0.00001f; }
			Vector2Int position = Vector2Int.FloorToInt(rayStartVPFloorSpace);
			Vector2Int goal = Vector2Int.FloorToInt(rayEndVPFloorSpace);
			Vector2 rayDirInverse = new Vector2(1f / rayDir.x, 1f / rayDir.y);
			Vector2Int step = new Vector2Int(rayDir.x >= 0f ? 1 : -1, rayDir.y >= 0f ? 1 : -1);
			Vector2 tDelta = new Vector2
			{
				x = Mathf.Min(rayDirInverse.x * step.x, 1f),
				y = Mathf.Min(rayDirInverse.y * step.y, 1f),
			};
			Vector2 tMax = new Vector2
			{
				x = Mathf.Abs((position.x + Mathf.Max(step.x, 0f) - position.x) * rayDirInverse.x),
				y = Mathf.Abs((position.y + Mathf.Max(step.y, 0f) - position.y) * rayDirInverse.y),
			};

			while (true) {
				if (!world.TryGetVoxelHeight(position, out int voxelHeight, out Color32 voxelColor)) {
					break; // out of bounds of the world
				}

				Vector4 columnStartWorld = new Vector4(position.x, voxelHeight, position.y, 1f);
				Vector4 columnEndWorld = new Vector4(position.x, 0f, position.y, 1f);

				Vector3 columnStartScreen = screenMatrix * columnStartWorld;
				Vector3 columnEndScreen = screenMatrix * columnEndWorld;

				if (columnStartScreen.z >= 0f && columnEndScreen.z >= 0f) {
					// column is not in view at all
					goto STEP;
				}

				Vector2 columnStartScreenScaled = (Vector2)columnStartScreen * (-1f / columnStartScreen.z);
				Vector2 columnEndScreenScaled = (Vector2)columnEndScreen * (-1f / columnEndScreen.z);

				int rayBufferYStart = Mathf.FloorToInt(columnStartScreenScaled.y);
				int rayBufferYEnd = Mathf.FloorToInt(columnEndScreenScaled.y);

				if (rayBufferYStart > rayBufferYEnd) {
					int temp = rayBufferYStart;
					rayBufferYStart = rayBufferYEnd;
					rayBufferYEnd = temp;
				}

				if (rayBufferYEnd < 0 || rayBufferYStart >= screenHeight) {
					goto STEP;
				}

				rayBufferYStart = Mathf.Max(0, rayBufferYStart);
				rayBufferYEnd = Mathf.Min(screenHeight - 1, rayBufferYEnd);

				for (int rayBufferY = rayBufferYStart; rayBufferY <= rayBufferYEnd; rayBufferY++) {
					int idx = rayBufferY * screenWidth * 2 + planeIndex;
					if (rayBuffer[idx].a == 0) {
						rayBuffer[idx] = voxelColor;
					}
				}

				STEP:
				if (position == goal) {
					break; // end of ray
				}

				// step the ray
				if (tMax.x < tMax.y) {
					tMax.x += tDelta.x;
					position.x += step.x;
				} else {
					tMax.y += tDelta.y;
					position.y += step.y;
				}
			}
		}

		Profiler.EndSample();

		Profiler.BeginSample("Raybuffer to screen");
		float vanishingPointXScreenNormalized = vanishingPointScreenSpace.x / screenWidth;
		float vanishingPointYScreenNormalized = vanishingPointScreenSpace.y / screenHeight;

		float segment2Height = Mathf.Min(screenHeight, screenHeight - vanishingPointScreenSpace.y);
		float usedRadialPlanesPortion = (float)radialPlaneCount / (screenWidth * 2);

		for (int y = 0; y < screenHeight; y++) {
			for (int x = 0; x < screenWidth; x++) {
				if (y > vanishingPointScreenSpace.y) {
					// we're above the vanishing point (so not segment 4, hopefully 2, maybe 1 or 3)
					Vector2 deltaToVP = new Vector2(x, y) - vanishingPointScreenSpace;
					if (Mathf.Abs(deltaToVP.x) < deltaToVP.y) {
						// this pixel is segment 2

						float u = (deltaToVP.x + deltaToVP.y) / (2f * deltaToVP.y) * usedRadialPlanesPortion;
						float adjustedVP = Mathf.Max(0f, vanishingPointScreenSpace.y);
						float v = (y - adjustedVP) / (screenHeight - adjustedVP);
						//screenBuffer[y * screenWidth + x] = new Color(u, v, 0f);

						int rayBufferXPixel = Mathf.RoundToInt(u * screenWidth * 2);
						int rayBufferYPixel = Mathf.RoundToInt(v * screenHeight);
						Color32 rayBufferPixel = rayBuffer[rayBufferYPixel * screenWidth * 2 + rayBufferXPixel];
						screenBuffer[y * screenWidth + x] = rayBufferPixel;
					}
				}
			}
		}
	}
}
