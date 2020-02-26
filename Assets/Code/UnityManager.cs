using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;

public class UnityManager : MonoBehaviour
{
	public RawImage BufferCanvas;
	public AnimationClip BenchmarkPath;

	Texture2D rayBuffer;
	Texture2D screenBuffer;
	RenderManager renderManager;
	World world;
	ERenderMode renderMode = ERenderMode.ScreenBuffer;
	float benchmarkTime = -1f;
	int benchmarkFrames = 0;
	float? lastBenchmarkResultFPS;

	int resolutionX = 160;
	int resolutionY = 120;

	Camera fakeCamera;

	int lastScreenResX;
	int lastScreenResY;

	private void Start ()
	{
		screenBuffer = new Texture2D(resolutionX, resolutionY, TextureFormat.RGBA32, false, false);
		screenBuffer.filterMode = FilterMode.Point;
		rayBuffer = new Texture2D(resolutionX * 2 + resolutionY * 2, resolutionY, TextureFormat.RGBA32, false, false);
		rayBuffer.filterMode = FilterMode.Point;
		BufferCanvas.texture = screenBuffer;
		renderManager = new RenderManager();
		world = new World();
		world.CullToVisiblesOnly();
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
			renderMode = ERenderMode.RayBuffer;
			ApplyRenderMode();
		} else if (Input.GetKeyDown(KeyCode.Alpha3)) {
			benchmarkTime = 0f;
			benchmarkFrames = 0;
			GetComponent<SmoothMouseLook>().enabled = false;
			renderMode = ERenderMode.ScreenBuffer;
		} else if (Input.GetKeyDown(KeyCode.Alpha4)) {
			resolutionX *= 2;
			resolutionY *= 2;
		} else if (Input.GetKeyDown(KeyCode.Alpha5)) {
			resolutionX /= 2;
			resolutionY /= 2;
		}
	}

	private void LateUpdate ()
	{
		if (screenBuffer.width != resolutionX || screenBuffer.height != resolutionY) {
			screenBuffer.Resize(resolutionX, resolutionY);
			rayBuffer.Resize(resolutionX * 2 + resolutionY * 2, resolutionY);
			UpdateBufferCanvasRatio();
			ApplyRenderMode();
		}

		try {
			fakeCamera.CopyFrom(GetComponent<Camera>());
			fakeCamera.pixelRect = new Rect(0, 0, resolutionX, resolutionY);
			renderManager.Draw(
				screenBuffer.GetRawTextureData<Color32>(),
				rayBuffer.GetRawTextureData<Color32>(),
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

		if (renderMode == ERenderMode.ScreenBuffer) {
			screenBuffer.Apply(false, false);
		}
		if (renderMode == ERenderMode.RayBuffer) {
			rayBuffer.Apply(false, false);
		}
		Profiler.EndSample();
	}

	private void OnGUI ()
	{
		GUILayout.BeginVertical();
		GUILayout.Label($"{resolutionX} by {resolutionY}");
		GUILayout.Label($"[1] to view screen buffer");
		GUILayout.Label($"[2] to view ray buffer");
		GUILayout.Label($"[3] to start a bechmark");
		GUILayout.Label($"[4] to double resolution");
		GUILayout.Label($"[5] to half resolution");
		GUILayout.Label($"[esc] to toggle mouse aim");
		GUILayout.Label($"Frame MS: {Time.deltaTime * 1000}");
		if (lastBenchmarkResultFPS != null) {
			GUILayout.Label($"FPS result: {lastBenchmarkResultFPS.Value}");
		}
		GUILayout.EndVertical();
	}

	void ApplyRenderMode ()
	{
		switch (renderMode) {
			case ERenderMode.RayBuffer:
				BufferCanvas.texture = rayBuffer;
				break;
			case ERenderMode.ScreenBuffer:
				BufferCanvas.texture = screenBuffer;
				break;
		}
	}

	void UpdateBufferCanvasRatio ()
	{
		float ratio = resolutionX / (float)resolutionY;
		BufferCanvas.GetComponent<AspectRatioFitter>().aspectRatio = ratio;
	}

	private void OnDestroy ()
	{
		Destroy(screenBuffer);
	}

	enum ERenderMode
	{
		ScreenBuffer,
		RayBuffer
	}
}
