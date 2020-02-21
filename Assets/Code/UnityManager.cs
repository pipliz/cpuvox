using UnityEngine;
using UnityEngine.UI;

public class UnityManager : MonoBehaviour
{
	public RawImage BufferCanvas;

	Texture2D screenBuffer;
	RenderManager renderManager;

	private void Start ()
	{
		screenBuffer = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false, false);
		BufferCanvas.texture = screenBuffer;
		renderManager = new RenderManager();
	}

	private void LateUpdate ()
	{
		if (screenBuffer.width != Screen.width || screenBuffer.height != Screen.height) {
			screenBuffer.Resize(Screen.width, Screen.height);
			BufferCanvas.texture = screenBuffer;
		}
		renderManager.Draw(screenBuffer.GetRawTextureData<Color32>(), screenBuffer.width, screenBuffer.height);
		screenBuffer.Apply(false, false);
	}

	private void OnDestroy ()
	{
		Destroy(screenBuffer);
	}
}
