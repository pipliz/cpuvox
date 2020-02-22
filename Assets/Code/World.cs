using UnityEngine;

public class World
{
	public int Width { get { return 32; } }
	public int Height { get { return 32; } }

	public bool TryGetVoxelHeight (Vector2Int position, out int result)
	{
		if (position.x < 0 || position.y < 0 || position.x >= Width || position.y >= Height) {
			result = default;
			return false;
		}
		result = 1 + (int)(Mathf.PerlinNoise(position.x * 0.1f, position.y * 0.1f) * 10f);
		return true;
	}
}
