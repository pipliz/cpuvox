using UnityEngine;

public class World
{
	public int Width { get { return 64; } } // x
	public int Height { get { return 64; } } // z
	public int Depth { get { return 32; } } // y

	public RLEElement[][] Data;

	public World ()
	{
		Data = new RLEElement[Width * Height][];
		for (int x = 0; x < Width; x++) {
			for (int z = 0; z < Height; z++) {
				int noiseHeight = 1 + (int)(Mathf.PerlinNoise(x * 0.1f, z * 0.1f) * 10f);
				Color32 color = Color.white * (noiseHeight / 11f);
				RLEElement bottom = new RLEElement(0, (ushort)noiseHeight, color);
				RLEElement top = new RLEElement((ushort)(Depth - noiseHeight), (ushort)Depth, color);
				Data[x * Height + z] = new RLEElement[] { bottom, top };
			}
		}
	}

	public bool TryGetVoxelHeight (Vector2Int position, out RLEElement[] elements)
	{
		if (position.x < 0 || position.y < 0 || position.x >= Width || position.y >= Height) {
			elements = default;
			return false;
		}
		elements = Data[position.x * Height + position.y];
		return true;
	}

	public struct RLEElement
	{
		public ushort Bottom;
		public ushort Top;
		public Color32 Color;

		public RLEElement (ushort bottom, ushort top, Color32 color)
		{
			Bottom = bottom;
			Top = top;
			Color = color;
		}
	}
}
