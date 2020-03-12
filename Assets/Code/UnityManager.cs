using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;

public class UnityManager : MonoBehaviour
{
	public AnimationClip BenchmarkPath;
	public Material BlitMaterial;

	Texture2D rayBufferTopDownActive;
	Texture2D rayBufferLeftRightActive;

	Texture2D rayBufferTopDownNext;
	Texture2D rayBufferLeftRightNext;

	Mesh meshActive;
	Mesh meshNext;

	RenderManager renderManager;
	World world;
	ERenderMode renderMode = ERenderMode.ScreenBuffer;
	float benchmarkTime = -1f;
	int benchmarkFrames = 0;
	float? lastBenchmarkResultFPS;

	int resolutionX = -1;
	int resolutionY = -1;

	int usedResolutionX = -1;
	int usedResolutionY = -1;

	Camera fakeCamera;

	const float MODEL_SCALE = 8f;
	const int DIMENSION_X = 1024;
	const int DIMENSION_Y = 256 + 128;
	const int DIMENSION_Z = 1024;

	static Texture2D Create (int x, int y)
	{
		Texture2D tex = new Texture2D(x, y, TextureFormat.RGB24, false, false);
		tex.filterMode = FilterMode.Point;
		return tex;
	}

	private void Start ()
	{
		resolutionX = usedResolutionX = Screen.width;
		resolutionY = usedResolutionY = Screen.height;

		rayBufferTopDownActive = Create(resolutionY, resolutionX + 2 * resolutionY);
		rayBufferTopDownNext = Create(resolutionY, resolutionX + 2 * resolutionY);

		rayBufferLeftRightActive = Create(resolutionX, 2 * resolutionX + resolutionY);
		rayBufferLeftRightNext = Create(resolutionX, 2 * resolutionX + resolutionY);

		meshActive = new Mesh();
		meshNext = new Mesh();

		renderManager = new RenderManager();

		world = new World(DIMENSION_X, DIMENSION_Y, DIMENSION_Z);

		Vector3 worldMid = new Vector3(world.DimensionX * 0.5f, 0f, world.DimensionZ * 0.5f);
		PlyModel model = new PlyModel("datasets/museum-100k.ply", MODEL_SCALE, worldMid);
		world.Import(model);
		transform.position = worldMid + Vector3.up * 10f;

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
			BenchmarkPath.SampleAnimation(gameObject, benchmarkTime);
			benchmarkTime += Time.deltaTime;
			benchmarkFrames++;

			if (benchmarkTime > BenchmarkPath.length) {
				lastBenchmarkResultFPS = benchmarkFrames / BenchmarkPath.length;
				benchmarkTime = -1f;
				GetComponent<SmoothMouseLook>().enabled = true;
			}
			return;
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
			GetComponent<SmoothMouseLook>().enabled = false;
			renderMode = ERenderMode.ScreenBuffer;
		}

		if (resolutionX > Screen.width) {
			resolutionX = Screen.width;
		}
		if (resolutionY > Screen.height) {
			resolutionY = Screen.height;
		}
	}

	static void Clear (Texture2D texture)
	{
		RenderManager.Color24 clearcolor = new RenderManager.Color24(0, 0, 0);
		NativeArray<RenderManager.Color24> colors = texture.GetRawTextureData<RenderManager.Color24>();
		for (int i = 0; i < colors.Length; i++) {
			colors[i] = clearcolor;
		}
		texture.Apply(false, false);
	}

	static void Swap<T> (ref T a, ref T b)
	{
		T t = a;
		a = b;
		b = t;
	}

	private void LateUpdate ()
	{
		if (renderMode == ERenderMode.ScreenBuffer) {
			Swap(ref rayBufferTopDownActive, ref rayBufferTopDownNext);
			Swap(ref rayBufferLeftRightActive, ref rayBufferLeftRightNext);
			Swap(ref meshActive, ref meshNext);
		}

		if (usedResolutionX != resolutionX || usedResolutionY != resolutionY) {
			Profiler.BeginSample("Resize textures");
			rayBufferTopDownActive.Resize(resolutionY, resolutionX + 2 * resolutionY);
			rayBufferTopDownNext.Resize(resolutionY, resolutionX + 2 * resolutionY);
			rayBufferLeftRightActive.Resize(resolutionX, 2 * resolutionX + resolutionY);
			rayBufferLeftRightNext.Resize(resolutionX, 2 * resolutionX + resolutionY);

			usedResolutionX = resolutionX;
			usedResolutionY = resolutionY;
			Profiler.EndSample();
		}

		try {
			Profiler.BeginSample("Update fakeCam data");
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			Profiler.EndSample();

			renderManager.DrawWorld(
				meshActive,
				BlitMaterial,
				rayBufferTopDownActive,
				rayBufferLeftRightActive,
				usedResolutionX,
				usedResolutionY,
				world,
				fakeCamera
			);
		} catch (System.Exception e) {
			benchmarkTime = -1f;
			Debug.LogException(e);
		}
	}

	private void OnGUI ()
	{
		GUILayout.BeginVertical();
		GUILayout.Label($"{resolutionX} by {resolutionY}");
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

	private void OnDestroy ()
	{
		Destroy(rayBufferLeftRightActive);
		Destroy(rayBufferLeftRightNext);
		Destroy(rayBufferTopDownActive);
		Destroy(rayBufferTopDownNext);
		world.Dispose();
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
