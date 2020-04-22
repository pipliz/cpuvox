using UnityEngine;
using UnityEngine.Profiling;

public class UnityManager : MonoBehaviour
{
	public AnimationClip BenchmarkPath;
	public Material BlitMaterial;
	public SmoothMouseLook MouseLook;

	RenderManager renderManager;
	World world;
	ERenderMode renderMode = ERenderMode.ScreenBuffer;
	float benchmarkTime = -1f;
	int benchmarkFrames = 0;
	float? lastBenchmarkResultFPS;

	int resolutionX = -1;
	int resolutionY = -1;

	int maxDimension = 512;

	float moveSpeed = 50f;

	Camera fakeCamera;

	string[] meshPaths;

	private void Start ()
	{
		DrawSegmentRayJob.Initialize();

		meshPaths = System.IO.Directory.GetFiles("./datasets/", "*.obj", System.IO.SearchOption.AllDirectories);

		resolutionX = Screen.width;
		resolutionY = Screen.height;

		renderManager = new RenderManager();

		world = new World();

		GameObject child = new GameObject("fake-cam");
		child.transform.SetParent(transform);
		child.transform.localPosition = Vector3.zero;
		child.transform.localRotation = Quaternion.identity;
		fakeCamera = child.AddComponent<Camera>();
		fakeCamera.CopyFrom(GetComponent<Camera>());
		fakeCamera.enabled = false;

		renderMode = ERenderMode.ScreenBuffer;
		ApplyRenderMode();
	}

	private void Update ()
	{
		if (benchmarkTime >= 0f) {
			if (Input.GetKeyDown(KeyCode.Escape)) {
				benchmarkTime = -1f;
				MouseLook.enabled = true;
				return;
			}

			BenchmarkPath.SampleAnimation(gameObject, benchmarkTime / 40f);
			gameObject.transform.position = gameObject.transform.position * (Unity.Mathematics.float3)world.Dimensions;
			benchmarkTime += Time.deltaTime;
			benchmarkFrames++;

			if (benchmarkTime > BenchmarkPath.length * 40f) {
				lastBenchmarkResultFPS = benchmarkFrames / (BenchmarkPath.length * 40f);
				benchmarkTime = -1f;
				MouseLook.enabled = true;
			}
			return;
		}

		if (Input.GetKeyDown(KeyCode.Escape)) {
			MouseLook.IsControlled = !MouseLook.IsControlled;
		}
		if (MouseLook.IsControlled) {
			MouseLook.DoUpdate();
		}

		if (Input.GetKey(KeyCode.W)) {
			transform.position += transform.forward * Time.deltaTime * moveSpeed;
		}
		if (Input.GetKey(KeyCode.S)) {
			transform.position -= transform.forward * Time.deltaTime * moveSpeed;
		}
		if (Input.GetKey(KeyCode.A)) {
			transform.position -= transform.right * Time.deltaTime * moveSpeed;
		}
		if (Input.GetKey(KeyCode.D)) {
			transform.position += transform.right * Time.deltaTime * moveSpeed;
		}

		if (Screen.fullScreenMode <= FullScreenMode.FullScreenWindow) {
			var main = Display.main;
			if (Screen.width != main.systemWidth || Screen.height != main.systemHeight) {
				Screen.SetResolution(main.systemWidth, main.systemHeight, true);
			}
		}

		if (Input.GetKeyDown(KeyCode.Alpha1)) {
			renderMode = ERenderMode.ScreenBuffer;
			ApplyRenderMode();
		} else if (Input.GetKeyDown(KeyCode.Alpha2)) {
			renderMode = ERenderMode.RayBufferTopDown;
			ApplyRenderMode();
		} else if (Input.GetKeyDown(KeyCode.Alpha3)) {
			renderMode = ERenderMode.RayBufferLeftRight;
			ApplyRenderMode();
		} else if (Input.GetKeyDown(KeyCode.Alpha4)) {
			resolutionX *= 2;
			resolutionY *= 2;
		} else if (Input.GetKeyDown(KeyCode.Alpha5)) {
			resolutionX /= 2;
			resolutionY /= 2;
		} else if (Input.GetKeyDown(KeyCode.Alpha6)) {
			benchmarkTime = 0f;
			benchmarkFrames = 0;
			MouseLook.enabled = false;
			renderMode = ERenderMode.ScreenBuffer;
		}

		float scroll = Input.mouseScrollDelta.y;
		if (scroll < 0f) {
			moveSpeed *= 0.9f;
		} else if (scroll > 0f) {
			moveSpeed *= 1.1f;
		}

		if (resolutionX > Screen.width) {
			resolutionX = Screen.width;
		}
		if (resolutionY > Screen.height) {
			resolutionY = Screen.height;
		}
	}

	private void LateUpdate ()
	{
		if (!world.Exists) { return; }

		if (renderMode == ERenderMode.ScreenBuffer) {
			renderManager.SwapBuffers();
		}

		renderManager.SetResolution(resolutionX, resolutionY);

		try {
			Profiler.BeginSample("Update fakeCam data");
			// fakeCamera is used to get some matrices and things for our adjusted resolution/angle rendering versus the actual camera
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			Profiler.EndSample();

			renderManager.DrawWorld(BlitMaterial, world, fakeCamera, GetComponent<Camera>());
		} catch (System.Exception e) {
			benchmarkTime = -1f;
			Debug.LogException(e);
		}
	}

	private void OnGUI ()
	{
		if (benchmarkTime >= 0f) {
			return;
		}

		if (world.Exists) {
			if (!MouseLook.IsControlled) {
				GUILayout.BeginVertical("box");
				GUILayout.Label($"{resolutionX} by {resolutionY}");
				GUILayout.Label($"Movespeed: {moveSpeed}");
				GUILayout.Label($"[1] to view screen buffer");
				GUILayout.Label($"[2] to view top/down ray buffer");
				GUILayout.Label($"[3] to view left/right ray buffer");
				GUILayout.Label($"[4] to double resolution");
				GUILayout.Label($"[5] to half resolution");
				GUILayout.Label($"[6] to start a bechmark");
				GUILayout.Label($"[esc] to toggle mouse aim");
				GUILayout.Label($"Frame MS: {Time.deltaTime * 1000}");
				if (lastBenchmarkResultFPS != null) {
					GUILayout.Label($"FPS result: {lastBenchmarkResultFPS.Value}");
				}
				GUILayout.EndVertical();
			}
		} else {

			GUILayout.BeginArea(new Rect(Screen.width / 2 - 125, Screen.height / 2 - 150, 250, 300));
			GUILayout.BeginVertical("box");

			GUILayout.BeginHorizontal("box");
			GUILayout.Label("World Dimensions:");
			string newMaxDimensionStr = GUILayout.TextField(maxDimension.ToString());
			if (int.TryParse(newMaxDimensionStr, out int newMaxDimension)) {
				maxDimension = newMaxDimension;
			}
			GUILayout.EndHorizontal();

			for (int i = 0; i < meshPaths.Length; i++) {
				GUILayout.BeginHorizontal();
				GUILayout.Label($"File: {System.IO.Path.GetFileNameWithoutExtension(meshPaths[i])}");
				if (GUILayout.Button("Load")) {
					Profiler.BeginSample("Import mesh");
					SimpleMesh mesh = ObjModel.Import(meshPaths[i], maxDimension, out Vector3Int worldDimensions);
					Profiler.EndSample();
					WorldBuilder builder = new WorldBuilder(worldDimensions.x, worldDimensions.y, worldDimensions.z);
					Profiler.BeginSample("Copy mesh to world");
					builder.Import(mesh);
					Profiler.EndSample();
					Profiler.BeginSample("Convert world");
					world = builder.ToFinalWorld();
					Profiler.EndSample();

					mesh.Dispose();
					Vector3 worldMid = new Vector3(world.DimensionX * 0.5f, 0f, world.DimensionZ * 0.5f);
					transform.position = worldMid + Vector3.up * 10f;
					GetComponent<Camera>().farClipPlane = maxDimension * 2;
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
			GUILayout.EndArea();
		}
	}

	private void OnDestroy ()
	{
		renderManager.Destroy();
		if (world.Exists) {
			world.Dispose();
		}
	}

	void ApplyRenderMode ()
	{
		if (renderMode == ERenderMode.ScreenBuffer) {
			BlitMaterial.DisableKeyword("COPY_MAIN1");
			BlitMaterial.DisableKeyword("COPY_MAIN2");
		} else if (renderMode == ERenderMode.RayBufferTopDown) {
			BlitMaterial.EnableKeyword("COPY_MAIN1");
			BlitMaterial.DisableKeyword("COPY_MAIN2");
		} else if (renderMode == ERenderMode.RayBufferLeftRight) {
			BlitMaterial.DisableKeyword("COPY_MAIN1");
			BlitMaterial.EnableKeyword("COPY_MAIN2");
		}
	}

	enum ERenderMode
	{
		ScreenBuffer,
		RayBufferTopDown,
		RayBufferLeftRight
	}
}
