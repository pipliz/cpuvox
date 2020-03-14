using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using static Unity.Mathematics.math;

public class RenderManager
{
	const int BUFFER_COUNT = 3;

	Texture2D[] rayBufferTopDown;
	Texture2D[] rayBufferLeftRight;
	Mesh[] blitMeshes;
	int bufferIndex;

	int screenWidth = -1;
	int screenHeight = -1;

	public RenderManager()
	{
		rayBufferLeftRight = new Texture2D[BUFFER_COUNT];
		rayBufferTopDown = new Texture2D[BUFFER_COUNT];
		blitMeshes = new Mesh[BUFFER_COUNT];

		screenWidth = Screen.width;
		screenHeight = Screen.height;

		for (int i = 0; i < BUFFER_COUNT; i++) {
			rayBufferLeftRight[i] = Create(screenWidth, 2 * screenWidth + screenHeight);
			rayBufferTopDown[i] = Create(screenHeight, screenWidth + 2 * screenHeight);
			blitMeshes[i] = new Mesh();
		}
	}

	public void Destroy ()
	{
		for (int i = 0; i < BUFFER_COUNT; i++) {
			Object.Destroy(rayBufferTopDown[i]);
			Object.Destroy(rayBufferLeftRight[i]);
			Object.Destroy(blitMeshes[i]);
		}
	}

	public void SwapBuffers ()
	{
		bufferIndex = (bufferIndex + 1) % BUFFER_COUNT;
	}

	public void SetResolution (int resolutionX, int resolutionY)
	{
		if (screenWidth != resolutionX || screenHeight != resolutionY) {
			Profiler.BeginSample("Resize textures");
			for (int i = 0; i < BUFFER_COUNT; i++) {
				rayBufferLeftRight[i].Resize(resolutionX, 2 * resolutionX + resolutionY);
				rayBufferTopDown[i].Resize(resolutionY, resolutionX + 2 * resolutionY);
			}

			screenWidth = resolutionX;
			screenHeight = resolutionY;
			Profiler.EndSample();
		}
	}

	public void DrawWorld (Material blitMaterial, World world, Camera camera) {
		Mesh blitMesh = blitMeshes[bufferIndex];
		Texture2D rayBufferTopDownTexture = this.rayBufferTopDown[bufferIndex];
		Texture2D rayBufferLeftRightTexture = this.rayBufferLeftRight[bufferIndex];

		Debug.DrawLine(new Vector2(0f, 0f), new Vector2(screenWidth, 0f));
		Debug.DrawLine(new Vector2(screenWidth, 0f), new Vector2(screenWidth, screenHeight));
		Debug.DrawLine(new Vector2(screenWidth, screenHeight), new Vector2(0f, screenHeight));
		Debug.DrawLine(new Vector2(0f, screenHeight), new Vector2(0f, 0f));

		Profiler.BeginSample("Get native pixel arrays");
		NativeArray<Color24> rayBufferTopDown = rayBufferTopDownTexture.GetRawTextureData<Color24>();
		NativeArray<Color24> rayBufferLeftRight = rayBufferLeftRightTexture.GetRawTextureData<Color24>();
		Profiler.EndSample();

		if (abs(camera.transform.eulerAngles.x) < 0.03f) {
			Vector3 eulers = camera.transform.eulerAngles;
			eulers.x = sign(eulers.x) * 0.03f;
			if (eulers.x == 0f) {
				eulers.x = 0.03f;
			}
			camera.transform.eulerAngles = eulers;
		}

		Profiler.BeginSample("Setup VP");
		float3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		float2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		Profiler.EndSample();
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<SegmentData> segments = new NativeArray<SegmentData>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);

		Profiler.BeginSample("Setup segment params");
		if (vanishingPointScreenSpace.y < screenHeight) {
			float distToOtherEnd = screenHeight - vanishingPointScreenSpace.y;
			segments[0] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, 1), 1, world.DimensionY);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.y;
			segments[1] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, -1), 1, world.DimensionY);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			float distToOtherEnd = screenWidth - vanishingPointScreenSpace.x;
			segments[2] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(1, 0), 0, world.DimensionY);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.x;
			segments[3] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(-1, 0), 0, world.DimensionY);
		}
		Profiler.EndSample();

		Profiler.BeginSample("Draw planes");
		DrawSegments(segments,
			vanishingPointWorldSpace,
			world,
			new CameraData(camera),
			screenWidth,
			screenHeight,
			vanishingPointScreenSpace,
			rayBufferTopDown,
			rayBufferLeftRight
		);
		Profiler.EndSample();

		Profiler.BeginSample("Apply textures");
		if (segments[0].RayCount > 0 || segments[1].RayCount > 0) {
			rayBufferTopDownTexture.Apply(false, false);
		}
		if (segments[2].RayCount > 0 || segments[3].RayCount > 0) {
			rayBufferLeftRightTexture.Apply(false, false);
		}
		Profiler.EndSample();

		Profiler.BeginSample("Blit raybuffer");
		BlitSegments(
			camera,
			blitMaterial,
			blitMesh,
			rayBufferTopDownTexture,
			rayBufferLeftRightTexture,
			segments,
			vanishingPointScreenSpace,
			screen
		);
		Profiler.EndSample();
	}

	static Texture2D Create (int x, int y)
	{
		return new Texture2D(x, y, TextureFormat.RGB24, false, false)
		{
			filterMode = FilterMode.Point
		};
	}

	static void BlitSegments (
		Camera camera,
		Material material,
		Mesh mesh,
		Texture2D rayBufferTopDownTexture,
		Texture2D rayBufferLeftRightTexture,
		NativeArray<SegmentData> segments,
		float2 vanishingPointScreenSpace,
		float2 screen
	)
	{
		NativeArray<float3> vertices = new NativeArray<float3>(12, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
		NativeArray<ushort> triangles = new NativeArray<ushort>(12, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
		NativeArray<float4> uv = new NativeArray<float4>(12, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

		for (int tri = 0; tri < vertices.Length; tri += 3) {
			vertices[tri + 0] = AdjustScreenPixelForMesh(vanishingPointScreenSpace, screen);
			vertices[tri + 1] = AdjustScreenPixelForMesh(segments[tri / 3].MaxScreen, screen);
			vertices[tri + 2] = AdjustScreenPixelForMesh(segments[tri / 3].MinScreen, screen);
			uv[tri + 0] = float4(0f, 0f, 1f, tri / 3f);
			uv[tri + 1] = float4(1f, 0f, 0f, tri / 3f);
			uv[tri + 2] = float4(0f, 1f, 0f, tri / 3f);
		}

		for (ushort i = 0; i < triangles.Length; i++) {
			triangles[i] = i;
		}

		mesh.SetVertices(vertices);
		mesh.SetUVs(0, uv, 0, uv.Length);
		mesh.SetIndices(triangles, MeshTopology.Triangles, 0, false, 0);

		mesh.UploadMeshData(false);
		mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000 * 1000);

		Vector4 scales = new Vector4(
			(float)segments[0].RayCount / rayBufferTopDownTexture.height,
			(float)segments[1].RayCount / rayBufferTopDownTexture.height,
			(float)segments[2].RayCount / rayBufferLeftRightTexture.height,
			(float)segments[3].RayCount / rayBufferLeftRightTexture.height
		);

		Vector4 offsets = new Vector4(0f, scales.x, 0f, scales.z);

		material.SetTexture("_MainTex1", rayBufferTopDownTexture);
		material.SetTexture("_MainTex2", rayBufferLeftRightTexture);
		material.SetVector("_RayOffset", offsets);
		material.SetVector("_RayScale", scales);

		Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);

		float3 AdjustScreenPixelForMesh (float2 screenPixel, float2 screenSize)
		{
			// go from 0 ... width in pixels to -1 .. 1
			return float3(2f * (screenPixel / screenSize) - 1f, 0.5f); // -1 .. 1 space 
		}
	}

	static void DrawSegments (
		NativeArray<SegmentData> segments,
		float3 vanishingPointWorldSpace,
		World world,
		CameraData camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
		NativeArray<Color24> rayBufferTopDown,
		NativeArray<Color24> rayBufferLeftRight
	)
	{
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<JobHandle> segmentHandles = new NativeArray<JobHandle>(4, Allocator.Temp);

		for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++) {
			if (segments[segmentIndex].RayCount <= 0) {
				continue;
			}

			Profiler.BeginSample("Segment setup overhead");

			DrawSegmentRayJob job = new DrawSegmentRayJob();
			job.segment = segments[segmentIndex];
			job.vanishingPointScreenSpace = vanishingPointScreenSpace;
			job.vanishingPointOnScreen = all(vanishingPointScreenSpace >= 0f & vanishingPointScreenSpace <= screen);
			job.axisMappedToY = (segmentIndex > 1) ? 0 : 1;
			job.seenPixelCacheLength = Mathf.RoundToInt((segmentIndex > 1 ) ? screen.x : screen.y);
			job.rayIndexOffset = 0;
			job.cameraLookingUp = camera.ForwardY >= 0f;
			job.vanishingPointCameraRayOnScreen = camera.Position + float3(1f, select(1, -1, !job.cameraLookingUp) * camera.FarClip * camera.FarClip, 0f);
			job.elementIterationDirection = job.cameraLookingUp ? 1 : -1;
			if (segmentIndex == 1) { job.rayIndexOffset = segments[0].RayCount; }
			if (segmentIndex == 3) { job.rayIndexOffset = segments[2].RayCount; }

			int2 nextFreePixel;
			if (segmentIndex < 2) {
				job.activeRayBuffer = rayBufferTopDown;
				job.activeRayBufferWidth = screenHeight;
				if (segmentIndex == 0) { // top segment
					nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1), screenHeight - 1);
				} else { // bottom segment
					nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1));
				}
			} else {
				job.activeRayBuffer = rayBufferLeftRight;
				job.activeRayBufferWidth = screenWidth;
				if (segmentIndex == 3) { // left segment
					nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1));
				} else { // right segment
					nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1), screenWidth - 1);
				}
			}

			job.originalNextFreePixel = nextFreePixel;
			job.world = world;
			job.camera = camera;
			job.screen = screen;
			job.markerRay = new Unity.Profiling.ProfilerMarker("DrawSegment.Ray");
			Profiler.EndSample();

			segmentHandles[segmentIndex] = job.Schedule(job.segment.RayCount, 1);
		}

		JobHandle.CompleteAll(segmentHandles);
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

	static SegmentData GetGenericSegmentParameters (
		Camera camera,
		float2 screen,
		float2 vpScreen, // vanishing point in screenspace (pixels, can be out of bounds)
		float distToOtherEnd,
		float2 neutral,
		int primaryAxis,
		int worldYMax
	)
	{
		SegmentData segment = new SegmentData();

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
			return segment; // 45 degree angles aren't on screen
		}

		if (all(vpScreen >= 0f & vpScreen <= screen)) {
			// vp within bounds, so nothing to clamp angle wise
			segment.MinScreen = simpleCaseMin;
			segment.MaxScreen = simpleCaseMax;
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
			segment.MinScreen = select(cornerLeft, cornerRight, swap);
			segment.MaxScreen = select(cornerRight, cornerLeft, swap);
		}

		segment.CamLocalPlaneRayMin = camera.ScreenToWorldPoint(new float3(segment.MinScreen, camera.farClipPlane)) - camera.transform.position;
		segment.CamLocalPlaneRayMax = camera.ScreenToWorldPoint(new float3(segment.MaxScreen, camera.farClipPlane)) - camera.transform.position;
		segment.RayCount = Mathf.RoundToInt(segment.MaxScreen[secondaryAxis] - segment.MinScreen[secondaryAxis]);
		return segment;
	}

	public struct SegmentData
	{
		public float2 MinScreen;
		public float2 MaxScreen;
		public float3 CamLocalPlaneRayMin;
		public float3 CamLocalPlaneRayMax;
		public int RayCount;
	}
}
