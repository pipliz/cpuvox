using UnityEngine;

public class World
{
	public int Width { get { return 64; } }
	public int Height { get { return 64; } }

	public bool TryGetVoxelHeight (Vector2Int position, out int result, out Color32 color)
	{
		if (position.x < 0 || position.y < 0 || position.x >= Width || position.y >= Height) {
			result = default;
			color = default;
			return false;
		}
		result = 1 + (int)(Mathf.PerlinNoise(position.x * 0.1f, position.y * 0.1f) * 10f);
		int worldIdx = position.x * Width + position.y;
		color = new Color32(0, 0, 0, 255);
		switch (worldIdx % 3) {
			case 0:
				color.r = 255;
				break;
			case 1:
				color.g = 255;
				break;
			case 2:
				color.b = 255;
				break;
		}
		return true;
	}
}
