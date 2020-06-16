using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Mathematics;
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
	bool swapYZ = false;
	bool3 flipXYZ = new bool3(true, false, false);

	float moveSpeed = 50f;
	float lodError = 1f;
	Vector2 objScrollViewPosition;
	Vector2 worldScrollViewPosition;

	/// <summary> we use a fake camera child to use as a helper for non-native resolution rendering with upscaling </summary>
	Camera fakeCamera;

	FileEntry[] meshPaths;

	float[] LODDistances;

	public const int LOD_LEVELS = 6;

	private void Start ()
	{
		flipXYZ = new bool3(true, false, false);
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
		return Directory.EnumerateFiles("./datasets/", "*.obj", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles("./datasets/", "*.world", SearchOption.AllDirectories))
			.Select(file => new FileEntry(file))
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
			gameObject.transform.position = gameObject.transform.position * (float3)worldLODs[0].Dimensions;
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
				IngameUI();
			}
		} else {
			GUILayout.BeginHorizontal("box"); // super container
			LoadTextObjsView();
			LoadWorldsView();
			GUILayout.EndHorizontal(); // end super container
		}
	}

	void LoadWorldsView ()
	{
		GUILayout.BeginVertical("box");

		GUILayout.Label(".world binary dumps");

		worldScrollViewPosition = GUILayout.BeginScrollView(worldScrollViewPosition, "box");
		for (int i = 0; i < meshPaths.Length; i++) {
			if (meshPaths[i].FileType != EFileEntryType.BinaryWorld) {
				continue;
			}
			GUILayout.BeginHorizontal();
			GUILayout.Label(meshPaths[i].FileName);
			if (GUILayout.Button("Load")) {
				try {
					worldLODs = WorldSaveFile.Deserialize(meshPaths[i].Path);
					Debug.Log($"Loaded {meshPaths[i].FileName} in size {worldLODs[0].Dimensions}");

					Vector3 worldMid = new Vector3(worldLODs[0].DimensionX * 0.5f, 0f, worldLODs[0].DimensionZ * 0.5f);
					transform.position = worldMid + Vector3.up * worldLODs[0].DimensionY * 0.6f;
				} catch (System.Exception e) {
					Debug.LogException(e);
					for (int j = 0; j < worldLODs.Length; j++) {
						worldLODs[j] = default;
					}
				}

				LODDistances = null;
			}
			if (GUILayout.Button("Delete")) {
				File.Delete(meshPaths[i].Path);
				meshPaths = GetFilePaths();
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.EndScrollView(); // end .dat list
		GUILayout.EndVertical(); // end dat container
	}

	void LoadTextObjsView ()
	{
		GUILayout.BeginVertical("box"); // container for .obj list

		GUILayout.Label(".obj meshes");

		GUILayout.BeginHorizontal("box"); // world dimensions horizontal
		GUILayout.Label("World Dimensions:");
		string newMaxDimensionStr = GUILayout.TextField(maxDimension.ToString());
		if (int.TryParse(newMaxDimensionStr, out int newMaxDimension)) {
			maxDimension = newMaxDimension;
		}
		GUILayout.EndHorizontal();

		swapYZ = GUILayout.Toggle(swapYZ, "Load as Z up");
		flipXYZ.x = GUILayout.Toggle(flipXYZ.x, "Flip X axis");
		flipXYZ.y = GUILayout.Toggle(flipXYZ.y, "Flip Y axis");
		flipXYZ.z = GUILayout.Toggle(flipXYZ.z, "Flip Z axis");

		objScrollViewPosition = GUILayout.BeginScrollView(objScrollViewPosition, "box"); // .obj list
		for (int i = 0; i < meshPaths.Length; i++) {
			if (meshPaths[i].FileType != EFileEntryType.TextObj) {
				continue;
			}
			GUILayout.BeginHorizontal();
			GUILayout.Label(meshPaths[i].FileName);
			if (GUILayout.Button("Convert")) {

				SimpleMesh mesh = null;
				try {
					var sw = System.Diagnostics.Stopwatch.StartNew();
					mesh = ObjModel.Import(meshPaths[i].Path, swapYZ);

					Debug.Log($"Loaded model in {sw.Elapsed.TotalSeconds} seconds");
					sw.Reset();
					sw.Start();

					// rescaling/repositioning the mesh to fit in our world from 0 .. maxdimension
					// we flip X/Z as it seems to be needed for some reason (text in meshes is inverted otherwise)
					int3 worldDimensions = mesh.Rescale(maxDimension, new float3(flipXYZ.x ? -1f : 1f, flipXYZ.y ? -1f : 1f, flipXYZ.z ? -1f : 1f));

					WorldBuilder builder = new WorldBuilder(worldDimensions.x, worldDimensions.y, worldDimensions.z);
					builder.Import(mesh);

					mesh.Dispose();
					mesh = null;

					Debug.Log($"Voxelized world in {sw.Elapsed.TotalSeconds} seconds");
					sw.Reset();
					sw.Start();

					worldLODs[0] = builder.ToLOD0World();

					for (int j = 1; j < LOD_LEVELS; j++) {
						worldLODs[j] = worldLODs[0].DownSample(j);
					}

					sw.Stop();
					Debug.Log($"Sorted and native-ified world in {sw.Elapsed.TotalSeconds} seconds");

					string worldFilePath = meshPaths[i].Path.Substring(0, meshPaths[i].Path.Length - ".dat2".Length) + ".world";
					WorldSaveFile.Serialize(worldLODs, worldFilePath);

					Debug.Log($"Serialized to {worldFilePath}");


					for (int j = 0; j < LOD_LEVELS; j++) {
						worldLODs[j].Dispose();
						worldLODs[j] = default;
					}

					meshPaths = GetFilePaths();
				} catch (System.Exception e) {
					Debug.LogException(e);
					worldLODs[0] = default; // just to prevent it from rendering, as we probably have an invalid state due to the exception. memory leaks all over
				}
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.EndScrollView(); // end .obj list
		GUILayout.EndVertical(); // end list container
	}

	private void IngameUI ()
	{
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

	/// <summary>
	/// calculates LOD distances by brute force checking the distance between 2 pixel rays
	/// </summary>
	float[] SetupLods (int worldMaxDimension, int resolutionX, int resolutionY)
	{
		Camera cam = GetComponent<Camera>();

		int clipMultiplier = World.REPEAT_WORLD ? 10 : 2;
		float clipMax = worldMaxDimension * clipMultiplier;
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

		float[] distancesResult = new float[LOD_LEVELS];
		for (int i = 0; i < LOD_LEVELS; i++) {
			float f = math.ceil((lods[i] ?? 2f) * clipMax);
			distancesResult[i] = f;
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
		public EFileEntryType FileType;

		public FileEntry (string path)
		{
			Path = path;
			FileName = System.IO.Path.GetFileNameWithoutExtension(path);
			if (path.EndsWith(".world")) {
				FileType = EFileEntryType.BinaryWorld;
			} else if (path.EndsWith(".obj")) {
				FileType = EFileEntryType.TextObj;
			} else {
				FileType = EFileEntryType.Unknown;
			}
		}
	}

	public enum EFileEntryType
	{
		Unknown,
		TextObj,
		BinaryWorld
	}
}
