using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;

public class UnityManager : MonoBehaviour
{
	public RawImage BufferCanvas;
	public AnimationClip BenchmarkPath;

	Texture2D rayBufferTopDownActive;
	Texture2D rayBufferLeftRightActive;
	Texture2D screenBufferActive;

	Texture2D rayBufferTopDownNext;
	Texture2D rayBufferLeftRightNext;
	Texture2D screenBufferNext;

	RenderManager renderManager;
	World world;
	ERenderMode renderMode = ERenderMode.ScreenBuffer;
	float benchmarkTime = -1f;
	int benchmarkFrames = 0;
	float? lastBenchmarkResultFPS;

	int resolutionX = 1280;
	int resolutionY = 720;

	Camera fakeCamera;

	int lastScreenResX;
	int lastScreenResY;

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
		screenBufferActive = Create(resolutionX, resolutionY);
		screenBufferNext = Create(resolutionX, resolutionY);

		rayBufferTopDownActive = Create(resolutionY, resolutionX + 2 * resolutionY);
		rayBufferTopDownNext = Create(resolutionY, resolutionX + 2 * resolutionY);

		rayBufferLeftRightActive = Create(resolutionX, 2 * resolutionX + resolutionY);
		rayBufferLeftRightNext = Create(resolutionX, 2 * resolutionX + resolutionY);

		BufferCanvas.texture = screenBufferActive;

		renderManager = new RenderManager();

		world = new World(DIMENSION_X, DIMENSION_Y, DIMENSION_Z);

		Vector3 worldMid = new Vector3(world.DimensionX * 0.5f, 0f, world.DimensionZ * 0.5f);
		PlyModel model = new PlyModel("datasets/museum-100k.ply", MODEL_SCALE, worldMid);
		world.Import(model);
		transform.position = worldMid + Vector3.up * 10f;

		UpdateBufferCanvasRatio();

		GameObject child = new GameObject("fake-cam");
		child.transform.SetParent(transform);
		child.transform.localPosition = Vector3.zero;
		child.transform.localRotation = Quaternion.identity;
		fakeCamera = child.AddComponent<Camera>();
		fakeCamera.CopyFrom(GetComponent<Camera>());
		fakeCamera.enabled = false;
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
		Swap(ref screenBufferActive, ref screenBufferNext);
		Swap(ref rayBufferTopDownActive, ref rayBufferTopDownNext);
		Swap(ref rayBufferLeftRightActive, ref rayBufferLeftRightNext);

		switch (renderMode) {
			case ERenderMode.RayBufferLeftRight:
				Clear(rayBufferLeftRightActive);
				break;
			case ERenderMode.RayBufferTopDown:
				Clear(rayBufferTopDownActive);
				break;
		}

		if (screenBufferActive.width != resolutionX || screenBufferActive.height != resolutionY) {
			Profiler.BeginSample("Resize textures");
			screenBufferActive.Resize(resolutionX, resolutionY);
			rayBufferTopDownActive.Resize(resolutionY, resolutionX + 2 * resolutionY);
			rayBufferLeftRightActive.Resize(resolutionX, 2 * resolutionX + resolutionY);
			UpdateBufferCanvasRatio();
			Profiler.EndSample();
		}

		ApplyRenderMode();

		try {
			Profiler.BeginSample("Update fakeCam data");
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			Profiler.EndSample();

			Profiler.BeginSample("Get raw texture data");
			NativeArray<RenderManager.Color24> screenarray = screenBufferActive.GetRawTextureData<RenderManager.Color24>();
			NativeArray<RenderManager.Color24> rayTopDownArray = rayBufferTopDownActive.GetRawTextureData<RenderManager.Color24>();
			NativeArray<RenderManager.Color24> rayLeftRightArray = rayBufferLeftRightActive.GetRawTextureData<RenderManager.Color24>();
			Profiler.EndSample();

			renderManager.Draw(
				screenarray,
				rayTopDownArray,
				rayLeftRightArray,
				screenBufferActive.width,
				screenBufferActive.height,
				world,
				fakeCamera
			);
		} catch (System.Exception e) {
			benchmarkTime = -1f;
			Debug.LogException(e);
		}

		Profiler.BeginSample("Apply texture2d");

		switch (renderMode) {
			case ERenderMode.RayBufferLeftRight:
				rayBufferLeftRightActive.Apply(false, false);
				break;
			case ERenderMode.RayBufferTopDown:
				rayBufferTopDownActive.Apply(false, false);
				break;
			case ERenderMode.ScreenBuffer:
				screenBufferActive.Apply(false, false);
				break;
		}
		Profiler.EndSample();
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
		Destroy(screenBufferActive);
		Destroy(screenBufferNext);
		Destroy(rayBufferLeftRightActive);
		Destroy(rayBufferLeftRightNext);
		Destroy(rayBufferTopDownActive);
		Destroy(rayBufferTopDownNext);
		world.Dispose();
	}

	void ApplyRenderMode ()
	{
		switch (renderMode) {
			case ERenderMode.RayBufferTopDown:
				BufferCanvas.texture = rayBufferTopDownActive;
				break;
			case ERenderMode.ScreenBuffer:
				BufferCanvas.texture = screenBufferActive;
				break;
			case ERenderMode.RayBufferLeftRight:
				BufferCanvas.texture = rayBufferLeftRightActive;
				break;
		}
	}

	void UpdateBufferCanvasRatio ()
	{
		float ratio = resolutionX / (float)resolutionY;
		BufferCanvas.GetComponent<AspectRatioFitter>().aspectRatio = ratio;
	}

	enum ERenderMode
	{
		ScreenBuffer,
		RayBufferTopDown,
		RayBufferLeftRight
	}
}
