using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;

public class UnityManager : MonoBehaviour
{
	public RawImage BufferCanvas;
	public AnimationClip BenchmarkPath;

	Texture2D rayBufferTopDown;
	Texture2D rayBufferLeftRight;
	Texture2D screenBuffer;

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

	private void Start ()
	{
		screenBuffer = new Texture2D(resolutionX, resolutionY, TextureFormat.RGBA32, false, false);
		screenBuffer.filterMode = FilterMode.Point;
		rayBufferTopDown = new Texture2D(resolutionX + 2 * resolutionY, resolutionY, TextureFormat.RGBA32, false, false);
		rayBufferTopDown.filterMode = FilterMode.Point;
		rayBufferLeftRight = new Texture2D(2 * resolutionX + resolutionY, resolutionX, TextureFormat.RGBA32, false, false);
		rayBufferLeftRight.filterMode = FilterMode.Point;
		BufferCanvas.texture = screenBuffer;

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

	private void LateUpdate ()
	{
		if (screenBuffer.width != resolutionX || screenBuffer.height != resolutionY) {
			screenBuffer.Resize(resolutionX, resolutionY);
			rayBufferTopDown.Resize(resolutionX + 2 * resolutionY, resolutionY);
			rayBufferLeftRight.Resize(2 * resolutionX + resolutionY, resolutionX);
			UpdateBufferCanvasRatio();
			ApplyRenderMode();
		}

		try {
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			renderManager.Draw(
				screenBuffer.GetRawTextureData<Color32>(),
				rayBufferTopDown.GetRawTextureData<Color32>(),
				rayBufferLeftRight.GetRawTextureData<Color32>(),
				screenBuffer.width,
				screenBuffer.height,
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
				rayBufferLeftRight.Apply(false, false);
				break;
			case ERenderMode.RayBufferTopDown:
				rayBufferTopDown.Apply(false, false);
				break;
			case ERenderMode.ScreenBuffer:
				screenBuffer.Apply(false, false);
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
		Destroy(screenBuffer);
		Destroy(rayBufferLeftRight);
		Destroy(rayBufferTopDown);
		world.Dispose();
	}

	void ApplyRenderMode ()
	{
		switch (renderMode) {
			case ERenderMode.RayBufferTopDown:
				BufferCanvas.texture = rayBufferTopDown;
				break;
			case ERenderMode.ScreenBuffer:
				BufferCanvas.texture = screenBuffer;
				break;
			case ERenderMode.RayBufferLeftRight:
				BufferCanvas.texture = rayBufferLeftRight;
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
