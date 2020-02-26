using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class World
{
	public int DimensionX { get { return 64 * 3; } } // x
	public int DimensionY { get { return 32; } } // y
	public int DimensionZ { get { return 64 * 3; } } // z

	public RLEElement[][] Data;

	List<RLEElement> cullCache = new List<RLEElement>();

	public World ()
	{
		Data = new RLEElement[DimensionX * DimensionZ][];
		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				int noiseHeight = 1 + (int)(Mathf.PerlinNoise(x * 0.1f, z * 0.1f) * 10f);
				Color32 color = Color.white * (noiseHeight / 11f);
				RLEElement bottom = new RLEElement(0, (ushort)noiseHeight, color);
				RLEElement top = new RLEElement((ushort)(DimensionY - noiseHeight), (ushort)(DimensionY - 1), color);
				Data[x * DimensionZ + z] = new RLEElement[] { bottom, top };
			}
		}
	}

	public void CullToVisiblesOnly ()
	{
		RLEElement[][] NewData = new RLEElement[DimensionX * DimensionZ][];
		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				int idx = x * DimensionZ + z;
				NewData[idx] = CullColumn(Data[idx], x, z);
			}
		}

		Data = NewData;
	}

	RLEElement[] CullColumn (RLEElement[] elements, int x, int z)
	{
		cullCache.Clear();
		for (int iE = 0; iE < elements.Length; iE++) {
			RLEElement element = elements[iE];
			for (ushort y = element.Bottom; y <= element.Top; y++) {
				if (HasAirNext(x, y, z)) {
					cullCache.Add(new RLEElement(y, y, element.Color));
				}
			}
		}
		{
			int iE = 0;
			while (iE < cullCache.Count) {
				RLEElement running = cullCache[iE];

				int jE = iE + 1;
				while (jE < cullCache.Count) {
					RLEElement potential = cullCache[jE];
					if (running.Top + 1 == potential.Bottom
						&& running.Color.r == potential.Color.r
						&& running.Color.g == potential.Color.g
						&& running.Color.b == potential.Color.b
						&& running.Color.a == potential.Color.a
					) {
						running.Top++;
						cullCache.RemoveAt(jE);
					} else {
						jE++;
					}
				}

				cullCache[iE] = running;
				iE++;
			}
		}

		return cullCache.ToArray();
	}

	bool HasAirNext (int x, int y, int z)
	{
		if (x > 0 && IsAirAt(x - 1, y, z)) {
			return true;
		}
		if (x < DimensionX - 1 && IsAirAt(x + 1, y, z)) {
			return true;
		}
		if (y > 0 && IsAirAt(x, y - 1, z)) {
			return true;
		}
		if (y < DimensionY - 1 && IsAirAt(x, y + 1, z)) {
			return true;
		}
		if (z > 0 && IsAirAt(x, y, z - 1)) {
			return true;
		}
		if (z < DimensionZ - 1 && IsAirAt(x, y, z + 1)) {
			return true;
		}
		return false;
	}

	bool IsAirAt (int x, int y, int z)
	{
		RLEElement[] column = Data[x * DimensionZ + z];
		for (int iE = 0; iE < column.Length; iE++) {
			RLEElement elem = column[iE];
			if (elem.Bottom <= y && y <= elem.Top) {
				return false;
			}
		}
		return true;
	}

	public bool TryGetVoxelHeight (int2 position, out RLEElement[] elements)
	{
		if (position.x < 0 || position.y < 0 || position.x >= DimensionX || position.y >= DimensionZ) {
			elements = default;
			return false;
		}
		elements = Data[position.x * DimensionZ + position.y];
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
