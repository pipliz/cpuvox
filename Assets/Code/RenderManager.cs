using Unity.Collections;
using UnityEngine;

public class RenderManager
{
	public void Draw (NativeArray<Color32> texture, int width, int height)
	{
		Color32 white = Color.white;
		Color32 black = Color.black;

		for (int y = 0; y < height; y++) {
			for (int x = 0; x < width; x++) {
				texture[y * width + x] = x > y ? white : black;
			}
		}
	}
}
