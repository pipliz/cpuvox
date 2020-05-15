using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

public class UnityManager : MonoBehaviour
{
	public AnimationClip BenchmarkPath;
	public Material BlitMaterial;
	public SmoothMouseLook MouseLook;

	RenderManager renderManager;

	World[] worldLODs;

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

	int[] LODDistances;

	private void Start ()
	{
		DrawSegmentRayJob.Initialize();

		meshPaths = System.IO.Directory.GetFiles("./datasets/", "*.obj", System.IO.SearchOption.AllDirectories);

		resolutionX = Screen.width;
		resolutionY = Screen.height;

		renderManager = new RenderManager();

		worldLODs = new World[6];

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
			gameObject.transform.position = gameObject.transform.position * (Unity.Mathematics.float3)worldLODs[0].Dimensions;
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
		if (!worldLODs[0].Exists) { return; }

		if (renderMode == ERenderMode.ScreenBuffer) {
			renderManager.SwapBuffers();
		}

		if (renderManager.SetResolution(resolutionX, resolutionY) || LODDistances == null) {
			LODDistances = SetupLods(worldLODs[0].MaxDimension, resolutionX, resolutionY);
		}

		try {
			Profiler.BeginSample("Update fakeCam data");
			// fakeCamera is used to get some matrices and things for our adjusted resolution/angle rendering versus the actual camera
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			Profiler.EndSample();
			LimitRotationHorizon(fakeCamera.transform);
			renderManager.DrawWorld(BlitMaterial, worldLODs, fakeCamera, GetComponent<Camera>(), LODDistances);

		} catch (System.Exception e) {
			benchmarkTime = -1f;
			Debug.LogException(e);
		}
	}
	
	/// <summary>
	/// If we look at the horizon, some math turns to infinite which is .. bad, so avoid that
	/// </summary>
	static void LimitRotationHorizon (Transform transform)
	{
		transform.localRotation = Quaternion.identity; // reset any previous frame fidling we did
		Vector3 forward = transform.forward;
		if (Mathf.Abs(forward.y) < 0.001f) {
			forward.y = Mathf.Sign(forward.y) * 0.001f;
			transform.forward = forward;
		}
	}

	void ReturnToMenu ()
	{
		for (int i = 0; i < worldLODs.Length; i++) {
			ref World world = ref worldLODs[i];
			if (world.Exists) {
				world.Dispose();
			}
		}
		GetComponent<Camera>().RemoveAllCommandBuffers();
	}

	private void OnGUI ()
	{
		if (benchmarkTime >= 0f) {
			return;
		}

		if (worldLODs[0].Exists) {
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
				if (GUILayout.Button("Return to menu")) {
					ReturnToMenu();
				}
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
				GUILayout.Label($"File: {Path.GetFileNameWithoutExtension(meshPaths[i])}");
				if (GUILayout.Button("Load")) {
					var sw = System.Diagnostics.Stopwatch.StartNew();

					SimpleMesh mesh;
					if (meshPaths[i].EndsWith(".dat")) {
						mesh = new SimpleMesh(meshPaths[i]);
					} else {
						string datFile = meshPaths[i] + ".dat";
						if (!File.Exists(datFile)) {
							mesh = ObjModel.Import(meshPaths[i]);
							mesh.Serialize(datFile);
						} else {
							mesh = new SimpleMesh(datFile);
						}
					}

					Unity.Mathematics.int3 worldDimensions = mesh.Rescale(maxDimension);
					Debug.Log($"Loaded mesh in {sw.Elapsed.TotalSeconds} seconds");
					sw.Reset();
					sw.Start();

					WorldBuilder builder = new WorldBuilder(worldDimensions.x, worldDimensions.y, worldDimensions.z);
					builder.Import(mesh);
					Debug.Log($"Voxelized world in {sw.Elapsed.TotalSeconds} seconds");
					sw.Reset();
					sw.Start();

					worldLODs[0] = builder.ToFinalWorld();
					worldLODs[1] = worldLODs[0].DownSample(1);
					worldLODs[2] = worldLODs[0].DownSample(2);
					worldLODs[3] = worldLODs[0].DownSample(3);
					worldLODs[4] = worldLODs[0].DownSample(4);
					worldLODs[5] = worldLODs[0].DownSample(5);

					Debug.Log($"Sorted and native-ified world in {sw.Elapsed.TotalSeconds} seconds");
					mesh.Dispose();
					Vector3 worldMid = new Vector3(worldLODs[0].DimensionX * 0.5f, 0f, worldLODs[0].DimensionZ * 0.5f);
					transform.position = worldMid + Vector3.up * 10f;
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
			GUILayout.EndArea();
		}
	}

	int[] SetupLods (int worldMaxDimension, int resolutionX, int resolutionY)
	{
		Camera cam = GetComponent<Camera>();

		float pixelW = (1f / resolutionX) * cam.pixelWidth;
		float pixelH = (1f / resolutionY) * cam.pixelHeight;

		int middleWidth = cam.pixelWidth / 2;
		int middleHeight = cam.pixelHeight / 2;

		Ray a = cam.ScreenPointToRay(new Vector3(middleWidth, middleHeight, 1f));
		Ray b = cam.ScreenPointToRay(new Vector3(middleWidth + pixelW, middleHeight + pixelH, 1f));

		float clipMax = worldMaxDimension * 2;

		float? dist0 = null;
		float? dist1 = null;
		float? dist2 = null;
		float? dist3 = null;
		float? dist4 = null;
		float? dist5 = null;

		float ALLOWED_PIXEL_ERROR = 1f;

		for (float p = 0f; p < 1f; p += 0.001f) {
			float rayDist = p * clipMax;
			Vector3 pA = a.direction * rayDist;
			Vector3 pB = b.direction * rayDist;
			float pAB = Vector3.Distance(pA, pB);
			if (dist0 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 2f) {
				dist0 = p;
			}
			if (dist1 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 4f) {
				dist1 = p;
			}
			if (dist2 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 8f) {
				dist2 = p;
			}
			if (dist3 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 16f) {
				dist3 = p;
			}
			if (dist4 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 32f) {
				dist4 = p;
			}
			if (dist5 == null && pAB > ALLOWED_PIXEL_ERROR * 1.41f * 64f) {
				dist5 = p;
			}
		}

		cam.farClipPlane = clipMax;

		System.Collections.Generic.List<int> ints = new System.Collections.Generic.List<int>();

		if (dist0.HasValue) { ints.Add(Mathf.RoundToInt(dist0.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }
		if (dist1.HasValue) { ints.Add(Mathf.RoundToInt(dist1.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }
		if (dist2.HasValue) { ints.Add(Mathf.RoundToInt(dist2.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }
		if (dist3.HasValue) { ints.Add(Mathf.RoundToInt(dist3.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }
		if (dist4.HasValue) { ints.Add(Mathf.RoundToInt(dist4.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }
		if (dist5.HasValue) { ints.Add(Mathf.RoundToInt(dist5.Value * cam.farClipPlane)); } else { ints.Add(Mathf.RoundToInt(2f * cam.farClipPlane)); }

		Debug.Log($"LOD splits: {string.Join(", ", ints)}");

		for (int i = 0; i < ints.Count; i++) {
			ints[i] *= ints[i];
		}

		return ints.ToArray();
	}

	private void OnDestroy ()
	{
		renderManager.Destroy();
		for (int i = 0; i < worldLODs.Length; i++) {
			ref World world = ref worldLODs[i];
			if (world.Exists) {
				world.Dispose();
			}
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
