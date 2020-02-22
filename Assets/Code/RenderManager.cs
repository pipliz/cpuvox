using Unity.Collections;
using UnityEngine;

public class RenderManager
{
	Plane[] frustumPlanes = new Plane[6];

	public void Draw (NativeArray<Color32> texture, int textureWidth, int textureHeight, World world, GameObject cameraObject)
	{
		Color32 white = Color.white;
		Color32 black = Color.black;

		// clear to black
		for (int i = 0; i < texture.Length; i++) {
			texture[i] = black; 
		}

		Vector3 cameraPos = cameraObject.transform.position;
		Camera camera = cameraObject.GetComponent<Camera>();
		Matrix4x4 cameraMatrix = camera.worldToCameraMatrix;
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

		Vector3 vanishingPointScreenSpace = camera.WorldToScreenPoint(vanishingPointWorldSpace);

		// render 4 quarters of the world around the vanishing point below us

		if (vanishingPointScreenSpace.x < textureWidth) { // if false, this quarter around the camera position isn't on screen
			float pixelsToScreenBorder = Mathf.Abs(textureWidth - vanishingPointScreenSpace.x);
			float radialPlaneCount = Mathf.CeilToInt(2 * pixelsToScreenBorder);

			for (float lineIndex = 0; lineIndex < radialPlaneCount; lineIndex++) {
				/// Step 3. Render the planes on GPU:  In each concentric plane, a ray is cast in the x-z plane from the x-z-coordinates of the viewpoint to the maximal view-distance.

				// each plane starts at the vanishingpoint's x/z
				// from there they move out radially into the world
				Vector2 rayStartVPFloorSpace = new Vector2(cameraPos.x, cameraPos.z);

				float quarterProgress = lineIndex / radialPlaneCount;
				// quarterProgress == 0 -> "top-right"
				// quarterProgress == 1 -> "bottom-right"

				// the X end of any ray in this quarter is just the "wall" of the view square
				// the Z end goes from VP + pixelsToScreenBorder to VP - pixelsToScreenBorder
				Vector2 rayEndVPFloorSpace = new Vector2
				{
					x = rayStartVPFloorSpace.x + farClipPlane,
					y = rayStartVPFloorSpace.y + Mathf.Lerp(-farClipPlane, farClipPlane, quarterProgress)
				};

				DrawLine(rayStartVPFloorSpace, rayEndVPFloorSpace, Color.white);

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
					if (position == goal) {
						break; // end of ray
					}

					if (!world.TryGetVoxelHeight(position, out int voxelHeight)) {
						break; // out of bounds of the world
					}

					{
						Vector3 debugA = new Vector3(position.x, 0.05f, position.y);
						Vector3 debugB = debugA + voxelHeight * Vector3.up;
						Debug.DrawLine(debugA, debugB, Color.green);
					} 

					{
						Vector3 columnStartWorld = new Vector3(position.x, voxelHeight, position.y);
						Vector3 columnEndWorld = new Vector3(position.x, 0f, position.y);

						Vector3 columnStartCamera = cameraMatrix * columnStartWorld;
						Vector3 columnEndCamera = cameraMatrix * columnEndWorld;

						Vector2 columnStartScreen = (Vector2)columnStartCamera * (-1f / columnStartCamera.z);
						Vector2 columnEndScreen = (Vector2)columnEndCamera * (-1f / columnEndCamera.z);

						int rayBufferX = Mathf.Clamp((int)lineIndex, 0, textureWidth - 1);

						int rayBufferYStart = Mathf.FloorToInt(columnStartScreen.y);
						int rayBufferYEnd = Mathf.FloorToInt(columnEndScreen.y);

						rayBufferYStart = Mathf.Clamp(rayBufferYStart, 0, textureHeight - 1);
						rayBufferYEnd = Mathf.Clamp(rayBufferYEnd, 0, textureHeight - 1);

						for (int rayBufferY = rayBufferYStart; rayBufferY <= rayBufferYEnd; rayBufferY++) {
							texture[rayBufferY * textureWidth + rayBufferX] = white;
						}

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
		}

		//int radialLineCount2 = 2 * Mathf.Abs(height - vanishingPointScreenSpaceY); // above VP
		//int radialLineCount3 = 2 * Mathf.Abs(vanishingPointScreenSpaceX); // left of VP
		//int radialLineCount4 = 2 * Mathf.Abs(vanishingPointScreenSpaceY); // below VP
	}

	static void DrawLine (Vector2 start, Vector2 end, Color color)
	{
		Vector3 start3 = new Vector3(start.x, 0.05f, start.y);
		Vector3 end3 = new Vector3(end.x, 0.05f, end.y);
		Debug.DrawLine(start3, end3, color);
	}
}
