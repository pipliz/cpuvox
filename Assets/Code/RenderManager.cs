﻿using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
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

	public void ClearRayBuffer (UnityManager.ERenderMode renderMode)
	{
		if (renderMode == UnityManager.ERenderMode.RayBufferLeftRight) {
			Texture2D texExample = rayBufferLeftRight[bufferIndex].Partials[0];
			NativeArray<ColorARGB32> pixels = new NativeArray<ColorARGB32>(texExample.width * texExample.height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			for (int i = 0; i < pixels.Length; i++) {
				pixels[i] = new ColorARGB32(255, 20, 147);
			}
			foreach (var buf in rayBufferLeftRight[bufferIndex].Partials) {
				buf.LoadRawTextureData(pixels);
				buf.Apply(false, false);
			}

			CommandBuffer cmd = new CommandBuffer();
			cmd.SetRenderTarget(rayBufferLeftRight[bufferIndex].FinalTexture);
			cmd.ClearRenderTarget(true, true, new Color(1f, 0.1f, 0.5f));
			Graphics.ExecuteCommandBuffer(cmd);
		} else if (renderMode == UnityManager.ERenderMode.RayBufferTopDown) {
			Texture2D texExample = rayBufferTopDown[bufferIndex].Partials[0];
			NativeArray<ColorARGB32> pixels = new NativeArray<ColorARGB32>(texExample.width * texExample.height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			for (int i = 0; i < pixels.Length; i++) {
				pixels[i] = new ColorARGB32(255, 20, 147);
			}

			foreach (var buf in rayBufferTopDown[bufferIndex].Partials) {
				buf.LoadRawTextureData(pixels);
				buf.Apply(false, false);
			}

			CommandBuffer cmd = new CommandBuffer();
			cmd.SetRenderTarget(rayBufferTopDown[bufferIndex].FinalTexture);
			cmd.ClearRenderTarget(true, true, new Color(1f, 0.1f, 0.5f));
			Graphics.ExecuteCommandBuffer(cmd);
		}
	}

	public bool SetResolution (int resolutionX, int resolutionY)
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
			return true;
		}
		return false;
	}

	public unsafe void DrawWorld (Material blitMaterial, World[] worldLODs, Camera camera, Camera actualCamera, float[] LODDistances) {
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
			segments[0] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, screenHeight - vanishingPointScreenSpace.y, new float2(0, 1), 1, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.y > 0f) {
			segments[1] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, vanishingPointScreenSpace.y, new float2(0, -1), 1, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.x < screenWidth) {
			segments[2] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, screenWidth - vanishingPointScreenSpace.x, new float2(1, 0), 0, worldLODs[0].DimensionY);
		}

		if (vanishingPointScreenSpace.x > 0f) {
			segments[3] = GetGenericSegmentParameters(camera, screen, vanishingPointScreenSpace, vanishingPointScreenSpace.x, new float2(-1, 0), 0, worldLODs[0].DimensionY);
		}
		Profiler.EndSample();
		RayBuffer activeRaybufferTopDown = rayBufferTopDown[bufferIndex];
		RayBuffer activeRaybufferLeftRight = rayBufferLeftRight[bufferIndex];

		RayBuffer.Native topDownNative = activeRaybufferTopDown.GetNativeData(Allocator.TempJob);
		RayBuffer.Native leftRightNative = activeRaybufferLeftRight.GetNativeData(Allocator.TempJob);

		commandBuffer.Clear();

		CameraData camData = new CameraData(camera, LODDistances, screen);

		Profiler.BeginSample("Draw planes");
		fixed (World* worldPtr = worldLODs) {
			DrawSegments(segments,
				worldPtr,
				camData,
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
		DrawSegmentRayJob.DrawContext drawContext = new DrawSegmentRayJob.DrawContext
		{
			camera = camera,
			screen = screen,
			worldLODs = worldLODs
		};

		NativeArray<DrawSegmentRayJob.SegmentContext> segmentContexts = new NativeArray<DrawSegmentRayJob.SegmentContext>(4, Allocator.TempJob, NativeArrayOptions.ClearMemory);
		DrawSegmentRayJob.SegmentContext* segmentContextPtr = (DrawSegmentRayJob.SegmentContext*)segmentContexts.GetUnsafePtr();
		int totalRays = 0;
		for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++) {
			DrawSegmentRayJob.SegmentContext* context = segmentContextPtr + segmentIndex;
			context->segment = segments[segmentIndex];
			totalRays += segments[segmentIndex].RayCount;

			if (segments[segmentIndex].RayCount <= 0) {
				continue;
			}
			
			context->axisMappedToY = (segmentIndex > 1) ? 0 : 1;
			context->segmentRayIndexOffset = 0;
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

			context->originalNextFreePixelMin = nextFreePixel.x;
			context->originalNextFreePixelMax = nextFreePixel.y;
			context->seenPixelCacheLength = (int)ceil(drawContext.screen[context->axisMappedToY]);
		}

		Profiler.EndSample();

		rayBufferTopDownManaged.Prepare(segments[0].RayCount + segments[1].RayCount);
		rayBufferLeftRightManaged.Prepare(segments[2].RayCount + segments[3].RayCount);

		NativeArray<DrawSegmentRayJob.RayContext> rayContext = new NativeArray<DrawSegmentRayJob.RayContext>(
			totalRays, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		NativeArray<DrawSegmentRayJob.RayDDAContext> rayDDAContext = new NativeArray<DrawSegmentRayJob.RayDDAContext>(
			totalRays, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		NativeList<DrawSegmentRayJob.RayContinuation> rayContinuations = new NativeList<DrawSegmentRayJob.RayContinuation>(
			totalRays, Allocator.TempJob);

		DrawSegmentRayJob.RaySetupJob raySetupJob = new DrawSegmentRayJob.RaySetupJob()
		{
			contexts = segmentContexts,
			rays = rayContext
		};

		DrawSegmentRayJob.DDASetupJob ddaSetupJob = new DrawSegmentRayJob.DDASetupJob()
		{
			raysInput = rayContext,
			raysOutput = rayDDAContext,
			drawContext = drawContext,
		};

		DrawSegmentRayJob.TraceToFirstColumnJob firstColumnJob = new DrawSegmentRayJob.TraceToFirstColumnJob
		{
			drawContext = drawContext,
			inRays = rayDDAContext,
			outRays = rayContinuations.AsParallelWriter()
		};

		DrawSegmentRayJob.RenderJob renderJob = new DrawSegmentRayJob.RenderJob
		{
			rays = rayContinuations,
			DrawingContext = drawContext,
		};

		JobHandle setup = raySetupJob.Schedule(totalRays, 64);
		JobHandle ddaSetup = ddaSetupJob.Schedule(totalRays, 64, setup);
		JobHandle firstColumn = firstColumnJob.Schedule(totalRays, 4, ddaSetup);
		JobHandle render = renderJob.Schedule(totalRays, 1, firstColumn);
		
		render.Complete();

		rayContext.Dispose();
		segmentContexts.Dispose();
		rayDDAContext.Dispose();
		rayContinuations.Dispose();

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
		// does what code below does, but that one has precision issues due to the world space usage
		// set up a local space version instead
		//return ((float3)camera.WorldToScreenPoint(worldPos)).xy; < -precision issues

		float4x4 lookMatrix = Matrix4x4.LookAt(Vector3.zero, camera.transform.forward, camera.transform.up);
		float4x4 viewMatrix = mul(Matrix4x4.Scale(float3(1, 1, -1)), inverse(lookMatrix)); // -1*z because unity
		float4x4 localToScreenMatrix = mul(camera.nonJitteredProjectionMatrix, viewMatrix);

		float3 localPos = worldPos - (float3)camera.transform.position;
		float4 camPos = mul(localToScreenMatrix, float4(localPos, 1f));

		return ((camPos.xy / camPos.w) * 0.5f + 0.5f) * float2(camera.pixelWidth, camera.pixelHeight);
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

		segment.CamLocalPlaneRayMin = TransformPixel(segment.MinScreen);
		segment.CamLocalPlaneRayMax = TransformPixel(segment.MaxScreen);
		segment.RayCount = Mathf.RoundToInt(segment.MaxScreen[secondaryAxis] - segment.MinScreen[secondaryAxis]);
		segment.RayCount = Mathf.Max(0, segment.RayCount);

		return segment;

		float2 TransformPixel (float2 pixel)
		{
			// manual screenToLocal to avoid 'screenToWorld - world' introduced precision loss

			// localToScreen = local -> inverse look -> scale z -> projection -> screen
			// screenToLocal = screen -> inverse projection -> inverse scale -> look -> local

			float4x4 matrix = inverse(camera.nonJitteredProjectionMatrix);
			matrix = mul(inverse(Matrix4x4.Scale(float3(1,1,-1))), matrix);
			matrix = mul(Matrix4x4.LookAt(Vector3.zero, camera.transform.forward, camera.transform.up), matrix);

			float4 val = mul(matrix, float4(((pixel / float2(camera.pixelWidth, camera.pixelHeight)) - 0.5f) * 2f, 1f, 1f));
			return val.xz / val.w;
		}
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
