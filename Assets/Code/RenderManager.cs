using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;

public class RenderManager
{
	const int BUFFER_COUNT = 2;

	RayBuffer[] rayBufferTopDown;
	RayBuffer[] rayBufferLeftRight;
	Mesh[] blitMeshes;
	int bufferIndex;
	CommandBuffer commandBuffer;

	int screenWidth = -1;
	int screenHeight = -1;

	public RenderManager()
	{
		rayBufferLeftRight = new RayBuffer[BUFFER_COUNT];
		rayBufferTopDown = new RayBuffer[BUFFER_COUNT];
		blitMeshes = new Mesh[BUFFER_COUNT];

		screenWidth = Screen.width;
		screenHeight = Screen.height;

		for (int i = 0; i < BUFFER_COUNT; i++) {
			rayBufferLeftRight[i] = new RayBuffer(screenWidth, 2 * screenWidth + screenHeight);
			rayBufferTopDown[i] = new RayBuffer(screenHeight, screenWidth + 2 * screenHeight);
			blitMeshes[i] = new Mesh();
		}

		commandBuffer = new CommandBuffer();
	}

	public void Destroy ()
	{
		for (int i = 0; i < BUFFER_COUNT; i++) {
			rayBufferLeftRight[i].Destroy();
			rayBufferTopDown[i].Destroy();
			Object.Destroy(blitMeshes[i]);
		}
		commandBuffer.Dispose();
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

	public unsafe void DrawWorld (Material blitMaterial, World[] worldLODs, Camera camera, Camera actualCamera) {
		Mesh blitMesh = blitMeshes[bufferIndex];

		Debug.DrawLine(new Vector2(0f, 0f), new Vector2(screenWidth, 0f));
		Debug.DrawLine(new Vector2(screenWidth, 0f), new Vector2(screenWidth, screenHeight));
		Debug.DrawLine(new Vector2(screenWidth, screenHeight), new Vector2(0f, screenHeight));
		Debug.DrawLine(new Vector2(0f, screenHeight), new Vector2(0f, 0f));

		Profiler.BeginSample("Setup VP");
		float3 vanishingPointWorldSpace = CalculateVanishingPointWorld(camera);
		float2 vanishingPointScreenSpace = ProjectVanishingPointScreenToWorld(camera, vanishingPointWorldSpace);
		Profiler.EndSample();
		float2 screen = new float2(screenWidth, screenHeight);

		NativeArray<SegmentData> segments = new NativeArray<SegmentData>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);

		Profiler.BeginSample("Setup segment params");
		if (vanishingPointScreenSpace.y < screenHeight) {
			float distToOtherEnd = screenHeight - vanishingPointScreenSpace.y;
			segments[0] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, 1), 1, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.y;
			segments[1] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(0, -1), 1, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			float distToOtherEnd = screenWidth - vanishingPointScreenSpace.x;
			segments[2] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(1, 0), 0, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			float distToOtherEnd = vanishingPointScreenSpace.x;
			segments[3] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, distToOtherEnd, new float2(-1, 0), 0, worldLODs[0].DimensionY);
		}
		Profiler.EndSample();
		RayBuffer activeRaybufferTopDown = rayBufferTopDown[bufferIndex];
		RayBuffer activeRaybufferLeftRight = rayBufferLeftRight[bufferIndex];

		RayBuffer.Native topDownNative = activeRaybufferTopDown.GetNativeData(Allocator.TempJob);
		RayBuffer.Native leftRightNative = activeRaybufferLeftRight.GetNativeData(Allocator.TempJob);

		commandBuffer.Clear();

		Profiler.BeginSample("Draw planes");
		fixed (World* worldPtr = worldLODs) {
			DrawSegments(segments,
				vanishingPointWorldSpace,
				worldPtr,
				new CameraData(camera),
				screenWidth,
				screenHeight,
				vanishingPointScreenSpace,
				topDownNative,
				leftRightNative,
				activeRaybufferTopDown,
				activeRaybufferLeftRight
			);
		}
		Profiler.EndSample();

		topDownNative.Dispose();
		leftRightNative.Dispose();

		Profiler.BeginSample("Apply textures");
		activeRaybufferTopDown.ApplyPartials(commandBuffer);
		activeRaybufferLeftRight.ApplyPartials(commandBuffer);
		Profiler.EndSample();

		Profiler.BeginSample("Blit raybuffer");
		BlitSegments(
			camera,
			blitMaterial,
			blitMesh,
			activeRaybufferTopDown.FinalTexture,
			activeRaybufferLeftRight.FinalTexture,
			segments,
			vanishingPointScreenSpace,
			screen,
			commandBuffer
		);
		Profiler.EndSample();

		actualCamera.RemoveAllCommandBuffers();
		actualCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, commandBuffer);
	}

	/// <summary>
	/// Setup a mesh that'll blit the raybuffer segments to the screen. Mesh is made in screenspace coordinates, the shader doesn't transform them.
	/// </summary>
	static void BlitSegments (
		Camera camera,
		Material material,
		Mesh mesh,
		RenderTexture rayBufferTopDownTexture,
		RenderTexture rayBufferLeftRightTexture,
		NativeArray<SegmentData> segments,
		float2 vanishingPointScreenSpace,
		float2 screen,
		CommandBuffer commands
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

		commands.DrawMesh(mesh, Matrix4x4.identity, material, 0);

		float3 AdjustScreenPixelForMesh (float2 screenPixel, float2 screenSize)
		{
			// go from 0 ... width in pixels to -1 .. 1
			return float3(2f * (screenPixel / screenSize) - 1f, 0.5f); // -1 .. 1 space 
		}
	}

	static unsafe void DrawSegments (
		NativeArray<SegmentData> segments,
		float3 vanishingPointWorldSpace,
		World* worldLODs,
		CameraData camera,
		int screenWidth,
		int screenHeight,
		float2 vanishingPointScreenSpace,
		RayBuffer.Native rayBufferTopDown,
		RayBuffer.Native rayBufferLeftRight,
		RayBuffer rayBufferTopDownManaged,
		RayBuffer rayBufferLeftRightManaged
	)
	{
		float2 screen = new float2(screenWidth, screenHeight);

		Profiler.BeginSample("Segment setup overhead");
		NativeArray<DrawSegmentRayJob.Context> contextsArray = new NativeArray<DrawSegmentRayJob.Context>(4, Allocator.Temp, NativeArrayOptions.ClearMemory);
		DrawSegmentRayJob.Context* contexts = (DrawSegmentRayJob.Context*)contextsArray.GetUnsafePtr();
		int totalRays = 0;
		for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++) {
			DrawSegmentRayJob.Context* context = contexts + segmentIndex;
			context->segment = segments[segmentIndex];
			totalRays += segments[segmentIndex].RayCount;

			if (segments[segmentIndex].RayCount <= 0) {
				continue;
			}
			
			context->axisMappedToY = (segmentIndex > 1) ? 0 : 1;
			context->segmentRayIndexOffset = 0;
			// iterate such that closer elements are always done first - to preserve depth sorting
			if (segmentIndex == 1) { context->segmentRayIndexOffset = segments[0].RayCount; }
			if (segmentIndex == 3) { context->segmentRayIndexOffset = segments[2].RayCount; }

			int2 nextFreePixel;
			if (segmentIndex < 2) {
				context->activeRayBufferFull = rayBufferTopDown;
				if (segmentIndex == 0) { // top segment
					nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1), screenHeight - 1);
				} else { // bottom segment
					nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.y), 0, screenHeight - 1));
				}
			} else {
				context->activeRayBufferFull = rayBufferLeftRight;
				if (segmentIndex == 3) { // left segment
					nextFreePixel = int2(0, clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1));
				} else { // right segment
					nextFreePixel = int2(clamp(Mathf.RoundToInt(vanishingPointScreenSpace.x), 0, screenWidth - 1), screenWidth - 1);
				}
			}

			context->originalNextFreePixel = nextFreePixel;
			context->worldLODs = worldLODs;
			context->camera = camera;
			context->screen = screen;
			context->seenPixelCacheLength = (int)ceil(context->screen[context->axisMappedToY]);
		}
		Profiler.EndSample();

		List<Task> tasks = new List<Task>(System.Environment.ProcessorCount);
		AutoResetEvent[] waits = new AutoResetEvent[]
		{
			new AutoResetEvent(false),
			new AutoResetEvent(false)
		};

		int doneRays = 0;
		int startedRays = 0;
		CustomSampler sampler = CustomSampler.Create("Task");
		rayBufferTopDownManaged.Prepare(segments[0].RayCount + segments[1].RayCount);
		rayBufferLeftRightManaged.Prepare(segments[2].RayCount + segments[3].RayCount);

		for (int i = 0; i < System.Environment.ProcessorCount; i++) {
			tasks.Add(Task.Run(() =>
			{
				int seenPixelCacheLengthMax = 0;
				for (int k = 0; k < 4; k++) {
					seenPixelCacheLengthMax = max(contexts[k].seenPixelCacheLength, seenPixelCacheLengthMax);
				}
				byte* seenPixelCache = stackalloc byte[seenPixelCacheLengthMax];

				sampler.Begin();
				while (true) {
					int started = Interlocked.Increment(ref startedRays) - 1;
					if (started >= totalRays) {
						break;
					}

					int startedCopy = started;

					for (int j = 0; j < 4; j++) {
						int segmentRays = contexts[j].segment.RayCount;
						if (segmentRays > 0) {
							if (startedCopy < segmentRays) {
								int rayBufferIndex = startedCopy + contexts[j].segmentRayIndexOffset;
								DrawSegmentRayJob.Execute(contexts + j, startedCopy, seenPixelCache);
								if (j < 2) {
									if (rayBufferTopDownManaged.Completed(rayBufferIndex)) {
										waits[0].Set();
									}
								} else {
									if (rayBufferLeftRightManaged.Completed(rayBufferIndex)) {
										waits[0].Set();
									}
								}
								break;
							} else {
								startedCopy -= segmentRays;
							}
						}
					}

					int done = Interlocked.Increment(ref doneRays);
					if (done == totalRays) {
						waits[1].Set();
						break;
					}
				}
				sampler.End();
			}));
		}

		while (true) {
			int doneBeforeWait = doneRays;
			int wokeIndex = WaitHandle.WaitAny(waits, 500);
			int doneAfterWait = doneRays;

			if (wokeIndex == WaitHandle.WaitTimeout && doneBeforeWait == doneAfterWait) {
				Debug.LogError($"Timeout on waiting for rays to be rendered (max 500 ms between progress)");
				break;
			}
			if (wokeIndex == 1) {
				break;
			}
			rayBufferTopDownManaged.UploadCompletes();
			rayBufferLeftRightManaged.UploadCompletes();
		}
		rayBufferTopDownManaged.UploadCompletes();
		rayBufferLeftRightManaged.UploadCompletes();
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

	/// <summary>
	/// Sets up the triangles for each segment.
	/// If the triangle is bigger than the screen, some rays from the vanishing point will never touch the screen.
	/// Therefore we clamp the triangle to a close fit around the screen corners where possible
	/// This does complicate math a lot, but the alternatives are significant resolution loss when aiming horizontal-ish or a gigantic buffer with trash performance
	/// </summary>
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

		segment.CamLocalPlaneRayMin = ((float3)(camera.ScreenToWorldPoint(new float3(segment.MinScreen, camera.farClipPlane)) - camera.transform.position)).xz;
		segment.CamLocalPlaneRayMax = ((float3)(camera.ScreenToWorldPoint(new float3(segment.MaxScreen, camera.farClipPlane)) - camera.transform.position)).xz;
		segment.RayCount = Mathf.RoundToInt(segment.MaxScreen[secondaryAxis] - segment.MinScreen[secondaryAxis]);
		segment.RayCount = Mathf.Max(0, segment.RayCount);
		return segment;
	}

	public struct SegmentData
	{
		public float2 MinScreen;
		public float2 MaxScreen;
		public float2 CamLocalPlaneRayMin;
		public float2 CamLocalPlaneRayMax;
		public int RayCount;
	}
}
