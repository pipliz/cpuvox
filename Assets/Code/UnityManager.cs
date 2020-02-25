﻿using UnityEngine;
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

	private void Start ()
	{
		screenBuffer = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false, false);
		rayBuffer = new Texture2D(Screen.width * 2 + Screen.height * 2, Screen.height, TextureFormat.RGBA32, false, false);
		BufferCanvas.texture = screenBuffer;
		renderManager = new RenderManager();
		world = new World();
	}

	private void Update ()
	{
		if (benchmarkTime >= 0f) {
			BenchmarkPath.SampleAnimation(gameObject, benchmarkTime);
			benchmarkTime += Time.deltaTime;
			benchmarkFrames++;

			if (benchmarkTime > BenchmarkPath.length) {
				Debug.Log($"Benchmarked {benchmarkFrames}, avg {benchmarkFrames / BenchmarkPath.length} fps");
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
			GetComponent<SmoothMouseLook>().enabled = false;
			renderMode = ERenderMode.ScreenBuffer;
		}
	}

	private void LateUpdate ()
	{
		if (screenBuffer.width != Screen.width || screenBuffer.height != Screen.height) {
			screenBuffer.Resize(Screen.width, Screen.height);
			rayBuffer.Resize(Screen.width * 2 + Screen.height * 2, Screen.height);
			ApplyRenderMode();
		}

		try {
			renderManager.Draw(
				screenBuffer.GetRawTextureData<Color32>(),
				rayBuffer.GetRawTextureData<Color32>(),
				screenBuffer.width,
				screenBuffer.height,
				world,
				gameObject
			);
		} catch (System.Exception e) {
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
