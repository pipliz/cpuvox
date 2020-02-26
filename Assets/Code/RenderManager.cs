﻿using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

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
			Planes[i] = default;
		}

		if (vanishingPointScreenSpace.y < screenHeight) {
			GetTopSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out float2 topRayEndMinScreenSpace,
				out float2 topRayEndMaxScreenSpace
			);

			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)topRayEndMinScreenSpace, Color.red);
			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)topRayEndMaxScreenSpace, Color.red);

			ProjectPlaneParametersScreenToWorld(camera, topRayEndMinScreenSpace, topRayEndMaxScreenSpace, out float2 topRayEndMinWorldSpace, out float2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[0];

			plane.MinScreen = topRayEndMinScreenSpace;
			plane.MaxScreen = topRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(topRayEndMaxScreenSpace.x - topRayEndMinScreenSpace.x);
			plane.RayCount = max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			GetBottomSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out float2 bottomRayEndMinScreenSpace,
				out float2 bottomRayEndMaxScreenSpace
			);

			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)bottomRayEndMinScreenSpace, Color.green);
			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)bottomRayEndMaxScreenSpace, Color.green);

			ProjectPlaneParametersScreenToWorld(camera, bottomRayEndMinScreenSpace, bottomRayEndMaxScreenSpace, out float2 topRayEndMinWorldSpace, out float2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[1];

			plane.MinScreen = bottomRayEndMinScreenSpace;
			plane.MaxScreen = bottomRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(bottomRayEndMaxScreenSpace.x - bottomRayEndMinScreenSpace.x);
			plane.RayCount = max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			GetRightSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out float2 rightRayEndMinScreenSpace,
				out float2 rightRayEndMaxScreenSpace
			);

			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)rightRayEndMinScreenSpace, Color.cyan);
			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)rightRayEndMaxScreenSpace, Color.cyan);

			ProjectPlaneParametersScreenToWorld(camera, rightRayEndMinScreenSpace, rightRayEndMaxScreenSpace, out float2 topRayEndMinWorldSpace, out float2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[2];

			plane.MinScreen = rightRayEndMinScreenSpace;
			plane.MaxScreen = rightRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(rightRayEndMaxScreenSpace.y - rightRayEndMinScreenSpace.y);
			plane.RayCount = max(0, plane.RayCount);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			GetLeftSegmentPlaneParameters(
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				out float2 leftRayEndMinScreenSpace,
				out float2 leftRayEndMaxScreenSpace
			);

			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)leftRayEndMinScreenSpace, Color.yellow);
			Debug.DrawLine((Vector2)vanishingPointScreenSpace, (Vector2)leftRayEndMaxScreenSpace, Color.yellow);

			ProjectPlaneParametersScreenToWorld(camera, leftRayEndMinScreenSpace, leftRayEndMaxScreenSpace, out float2 topRayEndMinWorldSpace, out float2 topRayEndMaxWorldspace);

			ref var plane = ref Planes[3];

			plane.MinScreen = leftRayEndMinScreenSpace;
			plane.MaxScreen = leftRayEndMaxScreenSpace;
			plane.MinWorld = topRayEndMinWorldSpace;
			plane.MaxWorld = topRayEndMaxWorldspace;
			plane.RayCount = Mathf.RoundToInt(leftRayEndMaxScreenSpace.y - leftRayEndMinScreenSpace.y);
			plane.RayCount = max(0, plane.RayCount);
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

	static float2 ProjectToScreen (float3 world, ref float4x4 worldToCameraMatrix, float screenWidth, float screenHeight, bool horizontalSegment)
	{
		float4 result = mul(worldToCameraMatrix, new float4(world, 1f));
		if (result.w == 0f) {
			return new float2(0f, 0f);
		}
		float usedDimension = select(result.y, result.x, horizontalSegment);
		float scaler = select(screenHeight, screenWidth, horizontalSegment);
		return new float2((usedDimension / result.w + 1f) * .5f * scaler, result.z);
	}

	static void DrawPlanes (
		PlaneData[] planes,
		float2 startWorld,
		World world,
		Camera camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
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

		float4x4 worldToScreenMatrix = camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix;

		int rayIndexCumulative = 0;

		float nearClip = camera.nearClipPlane;
		float farClip = camera.farClipPlane;

		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++) {
			PlaneData plane = planes[planeIndex];
			float unscaledMaxY = planeIndex > 1 ? screenWidth : screenHeight;
			for (int planeRayIndex = 0; planeRayIndex < plane.RayCount; planeRayIndex++, rayIndexCumulative++) {
				float2 endWorld = lerp(plane.MinWorld, plane.MaxWorld, planeRayIndex / (float)plane.RayCount);
				PlaneDDAData ray = new PlaneDDAData(startWorld, endWorld);

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

						float3 columnTopWorld = new float3(topWorldXZ.x, topWorldY, topWorldXZ.y);
						float3 columnBottomWorld = new float3(bottomWorldXZ.x, bottomWorldY, bottomWorldXZ.y);

						bool horizontal = planeIndex > 1;
						float2 columnTopScreen = ProjectToScreen(columnTopWorld, ref worldToScreenMatrix, screenWidth, screenHeight, horizontal);
						if (columnTopScreen.y < 0f) { continue; } // Y is actually the Z dimension
						float2 columnBottomScreen = ProjectToScreen(columnBottomWorld, ref worldToScreenMatrix, screenWidth, screenHeight, horizontal);
						if (columnBottomScreen.y < 0f) { continue; }

						float rayBufferYTopScreen = columnTopScreen.x;
						float rayBufferYBottomScreen = columnBottomScreen.x;

						if (rayBufferYTopScreen < rayBufferYBottomScreen) {
							Swap(ref rayBufferYTopScreen, ref rayBufferYBottomScreen);
						}

						if (rayBufferYTopScreen <= 0f || rayBufferYBottomScreen >= unscaledMaxY) {
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

						int rayBufferYBottom = max(0, Mathf.FloorToInt(rayBufferYBottomScreen));
						int rayBufferYTop = min(screenHeight - 1, Mathf.CeilToInt(rayBufferYTopScreen));

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
		float2 vpScreen,
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
				float2 pixelScreen = new float2(x, y);
				float2 deltaToVP = pixelScreen - vpScreen;
				float2 deltaToVPAbs = abs(deltaToVP);

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

						float adjustedVP = max(0f, vpScreen.y);
						v = (y - adjustedVP) / (screenHeight - adjustedVP);
					} else {
						//bottom segment (VP above pixel)
						float normalizedY = 1f - (y / vpScreen.y);
						float xLeft = (planes[1].MinScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xRight = (planes[1].MaxScreen.x - vpScreen.x) * normalizedY + vpScreen.x;
						float xLerp = Mathf.InverseLerp(xLeft, xRight, x);
						u = planes[1].UOffsetStart + xLerp * planes[1].UScale;

						float adjustedVP = min(screenHeight, vpScreen.y);
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

						float adjustedVP = max(0f, vpScreen.x);
						v = (x - adjustedVP) / (screenWidth - adjustedVP);
					} else {
						// left segment (VP right of pixel
						float normalizedX = 1f - (x / vpScreen.x);
						float yBottom = (planes[3].MinScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yTop = (planes[3].MaxScreen.y - vpScreen.y) * normalizedX + vpScreen.y;
						float yLerp = Mathf.InverseLerp(yBottom, yTop, y);
						u = planes[3].UOffsetStart + yLerp * planes[3].UScale;

						float adjustedVP = min(screenWidth, vpScreen.x);
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
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out float2 endMinScreen,
		out float2 endMaxScreen
	) {
		float distToTop = abs(screenHeight - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight)
		{
			// VP is in bounds, simple case
			endMinScreen = new float2(vpScreen.x - distToTop, screenHeight);
			endMaxScreen = new float2(vpScreen.x + distToTop, screenHeight);
			return;
		}

		// bottom left corner of screen, etc
		float2 screenBottomLeft = new float2(0f, 0f);
		float2 screenBottomRight = new float2(screenWidth, 0f);

		if (vpScreen.x < 0f) {
			endMinScreen = new float2(0f, screenHeight);
			endMaxScreen = TryAngleClamp(screenBottomRight, true);
		} else if (vpScreen.x > screenWidth) {
			endMinScreen = TryAngleClamp(screenBottomLeft, false);
			endMaxScreen = new float2(screenWidth, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(screenBottomLeft, false);
			endMaxScreen = TryAngleClamp(screenBottomRight, true);
		}

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
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out float2 endMinScreen,
		out float2 endMaxScreen
	) {
		float distToBottom = abs(0f - vpScreen.y);
		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new float2(vpScreen.x - distToBottom, 0f);
			endMaxScreen = new float2(vpScreen.x + distToBottom, 0f);
			return;
		}

		// bottom left corner of screen, etc
		float2 screenTopLeft = new float2(0f, screenHeight);
		float2 screenTopRight = new float2(screenWidth, screenHeight);

		if (vpScreen.x < 0f) {
			endMinScreen = new float2(0f, 0f);
			endMaxScreen = TryAngleClamp(screenTopRight, true);
		} else if (vpScreen.x > screenWidth) {
			endMinScreen = TryAngleClamp(screenTopLeft, false);
			endMaxScreen = new Vector2(screenWidth, 0f);
		} else {
			endMinScreen = TryAngleClamp(screenTopLeft, false);
			endMaxScreen = TryAngleClamp(screenTopRight, true);
		}

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
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out float2 endMinScreen,
		out float2 endMaxScreen
	)
	{
		float distToRight = screenWidth - vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new float2(screenWidth, vpScreen.y - distToRight);
			endMaxScreen = new float2(screenWidth, vpScreen.y + distToRight);
			return;
		}

		// bottom left corner of screen, etc
		float2 screenRightBottom = new float2(screenWidth, 0f);
		float2 screenRightTop = new float2(screenWidth, screenHeight);

		if (vpScreen.y < 0f) { // below screen, casting to the right
			endMinScreen = new float2(screenWidth, 0f);
			endMaxScreen = TryAngleClamp(new float2(0f, screenHeight), true);
		} else if (vpScreen.y > screenHeight) { // above screen, casting to the right
			endMinScreen = TryAngleClamp(new float2(0f, 0f), false);
			endMaxScreen = new float2(screenWidth, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(new float2(0f, 0f), false);
			endMaxScreen = TryAngleClamp(new float2(0f, screenHeight), true);
		}

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
		int screenWidth,
		int screenHeight,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		out float2 endMinScreen,
		out float2 endMaxScreen
	)
	{
		float distToLeft = vpScreen.x;

		if (vpScreen.x >= 0f && vpScreen.x <= screenWidth
			&& vpScreen.y >= 0f && vpScreen.y <= screenHeight) {
			// VP is in bounds, simple case
			endMinScreen = new float2(0f, vpScreen.y - distToLeft);
			endMaxScreen = new float2(0f, vpScreen.y + distToLeft);
			return;
		}

		if (vpScreen.y < 0f) { // below screen, casting to the left
			endMinScreen = new float2(0f, 0f);
			endMaxScreen = TryAngleClamp(new float2(screenWidth, screenHeight), true);
		} else if (vpScreen.y > screenHeight) { // above screen, casting to the left
			endMinScreen = TryAngleClamp(new float2(screenWidth, 0), false);
			endMaxScreen = new float2(0f, screenHeight);
		} else {
			endMinScreen = TryAngleClamp(new float2(screenWidth, 0f), false);
			endMaxScreen = TryAngleClamp(new float2(screenWidth, screenHeight), true);
		}

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

		public float ColumnHeightScaler;
	}
}
