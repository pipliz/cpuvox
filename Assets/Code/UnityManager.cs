using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

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

	int maxDimension = 1024;

	float moveSpeed = 50f;
	float lodError = 1f;
	Vector2 objScrollViewPosition;
	Vector2 datScrollViewPosition;

	/// <summary> we use a fake camera child to use as a helper for non-native resolution rendering with upscaling </summary>
	Camera fakeCamera;

	FileEntry[] meshPaths;

	int[] LODDistances;

	public const int LOD_LEVELS = 6;

	private void Start ()
	{
		meshPaths = GetFilePaths();

		resolutionX = Screen.width;
		resolutionY = Screen.height;

		renderManager = new RenderManager();

		worldLODs = new World[LOD_LEVELS];

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

	static FileEntry[] GetFilePaths ()
	{
		return Directory.EnumerateFiles("./datasets/", "*.obj.dat")
			.Concat(Directory.EnumerateFiles("./datasets/", "*.obj"))
			.Select(file =>
			{
				return new FileEntry
				{
					FileName = Path.GetFileName(file),
					IsDat = file.EndsWith(".obj.dat"),
					Path = file
				};
			})
			.ToArray();
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
			// res changed or no lod distances set up
			LODDistances = SetupLods(worldLODs[0].MaxDimension, resolutionX, resolutionY);
		}

		try {
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
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
	static void LimitRotationHorizon (Transform fakeCameraTransform)
	{
		fakeCameraTransform.localRotation = Quaternion.identity; // reset any previous frame fidling we did
		Vector3 forward = fakeCameraTransform.forward;
		if (Mathf.Abs(forward.y) < 0.001f) {
			forward.y = Mathf.Sign(forward.y) * 0.001f;
			fakeCameraTransform.forward = forward;
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
				GUILayout.Label($"Lod power: {lodError}");
				float newLOD = GUILayout.HorizontalSlider(lodError, 0.1f, 10f);
				if (newLOD != lodError) {
					lodError = newLOD;
					LODDistances = null; // will be remade in LateUpdate
				}
				if (GUILayout.Button("Reset LOD")) {
					lodError = 1f;
					LODDistances = null;
				}

				GUILayout.Label($"Near clip: {GetComponent<Camera>().nearClipPlane}");
				float newNearClip = GUILayout.HorizontalSlider(GetComponent<Camera>().nearClipPlane, 0.01f, 250f);
				if (GUILayout.Button("Reset Near Clip")) {
					newNearClip = 0.05f;
				}
				GetComponent<Camera>().nearClipPlane = newNearClip;

				if (GUILayout.Button("Return to menu")) {
					ReturnToMenu();
				}
				if (lastBenchmarkResultFPS != null) {
					GUILayout.Label($"FPS result: {lastBenchmarkResultFPS.Value}");
				}
				GUILayout.EndVertical();
			}
		} else {
			GUILayout.BeginHorizontal("box"); // super container
			GUILayout.BeginVertical("box"); // container for .obj list

			GUILayout.Label(".obj meshes");
			objScrollViewPosition = GUILayout.BeginScrollView(objScrollViewPosition, "box"); // .obj list
			for (int i = 0; i < meshPaths.Length; i++) {
				if (meshPaths[i].IsDat) {
					continue;
				}
				GUILayout.BeginHorizontal();
				GUILayout.Label(meshPaths[i].FileName);
				if (GUILayout.Button("Convert")) {
					var sw = System.Diagnostics.Stopwatch.StartNew();

					SimpleMesh mesh = ObjModel.Import(meshPaths[i].Path);
					mesh.Serialize(meshPaths[i].Path + ".dat");
					mesh.Dispose();

					meshPaths = GetFilePaths();
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndScrollView(); // end .obj list
			GUILayout.EndVertical(); // end list container

			// only super container left

			GUILayout.BeginVertical("box"); // container for .dat list

			GUILayout.Label(".dat binary meshes");
			GUILayout.BeginHorizontal("box"); // world dimensions horizontal
			GUILayout.Label("World Dimensions:");
			string newMaxDimensionStr = GUILayout.TextField(maxDimension.ToString());
			if (int.TryParse(newMaxDimensionStr, out int newMaxDimension)) {
				maxDimension = newMaxDimension;
			}
			GUILayout.EndHorizontal();

			objScrollViewPosition = GUILayout.BeginScrollView(objScrollViewPosition, "box"); // .dat list
			for (int i = 0; i < meshPaths.Length; i++) {
				if (!meshPaths[i].IsDat) {
					continue;
				}
				GUILayout.BeginHorizontal();
				GUILayout.Label(meshPaths[i].FileName);
				if (GUILayout.Button("Load")) {
					var sw = System.Diagnostics.Stopwatch.StartNew();
					SimpleMesh mesh = null;
					try {
						mesh = new SimpleMesh(meshPaths[i].Path);

						// rescaling/repositioning the mesh to fit in our world from 0 .. maxdimension
						Unity.Mathematics.int3 worldDimensions = mesh.Rescale(maxDimension);

						Debug.Log($"Loaded mesh in {sw.Elapsed.TotalSeconds} seconds");
						sw.Reset();
						sw.Start();

						WorldBuilder builder = new WorldBuilder(worldDimensions.x, worldDimensions.y, worldDimensions.z);
						builder.Import(mesh);

						Debug.Log($"Voxelized world in {sw.Elapsed.TotalSeconds} seconds");
						sw.Reset();
						sw.Start();

						worldLODs[0] = builder.ToLOD0World();
						for (int j = 1; j < LOD_LEVELS; j++) {
							worldLODs[j] = worldLODs[0].DownSample(j);
						}

						sw.Stop();

						Vector3 worldMid = new Vector3(worldLODs[0].DimensionX * 0.5f, 0f, worldLODs[0].DimensionZ * 0.5f);
						transform.position = worldMid + Vector3.up * worldLODs[0].DimensionY * 0.6f;

					} catch (System.Exception e) {
						Debug.LogException(e);
						for (int j = 0; j < worldLODs.Length; j++) {
							worldLODs[j] = default;
						}
					} finally {
						mesh?.Dispose();
					}

					LODDistances = null;
					Debug.Log($"Sorted and native-ified world in {sw.Elapsed.TotalSeconds} seconds");

				}
				if (GUILayout.Button("Delete")) {
					File.Delete(meshPaths[i].Path);
					meshPaths = GetFilePaths();
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndScrollView(); // end .dat list
			GUILayout.EndVertical(); // end dat container

			GUILayout.EndHorizontal(); // end super container

		}
	}

	/// <summary>
	/// calculates LOD distances by brute force checking the distance between 2 pixel rays
	/// </summary>
	int[] SetupLods (int worldMaxDimension, int resolutionX, int resolutionY)
	{
		Camera cam = GetComponent<Camera>();
		float clipMax = worldMaxDimension * 10;
		cam.farClipPlane = clipMax;

		float pixelW = (1f / resolutionX) * cam.pixelWidth;
		float pixelH = (1f / resolutionY) * cam.pixelHeight;

		int middleWidth = cam.pixelWidth / 2;
		int middleHeight = cam.pixelHeight / 2;

		Ray a = cam.ScreenPointToRay(new Vector3(middleWidth, middleHeight, 1f));
		Ray b = cam.ScreenPointToRay(new Vector3(middleWidth + pixelW, middleHeight + pixelH, 1f));

		float?[] lods = new float?[LOD_LEVELS];

		float pixelWidth = (1.41f / lodError);

		for (float p = 0f; p < 1f; p += 0.0001f) {
			float rayDist = p * clipMax;
			Vector3 pA = a.direction * rayDist;
			Vector3 pB = b.direction * rayDist;
			float pAB = Vector3.Distance(pA, pB);
			for (int j = 0; j < LOD_LEVELS; j++) {
				if (lods[j] == null && pAB > pixelWidth * (2 << j)) {
					lods[j] = p;
				}
			}
		}

		lods[LOD_LEVELS - 1] = 2f; // ensure the last LOD is never exited

		int[] distancesResult = new int[LOD_LEVELS];
		for (int i = 0; i < LOD_LEVELS; i++) {
			float f = (lods[i] ?? 2f) * clipMax;
			int v = Mathf.RoundToInt(f);
			distancesResult[i] = v * v;
		}
		return distancesResult;
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

	struct FileEntry
	{
		public string Path;
		public string FileName;
		public bool IsDat;
	}
}
