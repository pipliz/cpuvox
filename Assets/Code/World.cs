using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using System;

public unsafe struct World : IDisposable
{
	public int DimensionX { get; }
	public int DimensionY { get; }
	public int DimensionZ { get; }

	[NativeDisableUnsafePtrRestriction]
	RLEColumn* Data;
	int DataItemCount;

	public unsafe World (int dimensionX, int dimensionY, int dimensionZ)
	{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		if (dimensionX <= 0 || dimensionY <= 0 || dimensionZ <= 0) {
			throw new ArgumentOutOfRangeException("x, y or z", "must be > 0");
		}
#endif

		DimensionX = dimensionX;
		DimensionY = dimensionY;
		DimensionZ = dimensionZ;

		DataItemCount = dimensionX * dimensionZ;

		int dataBytes = UnsafeUtility.SizeOf<RLEColumn>() * DataItemCount;
		Data = (RLEColumn*)UnsafeUtility.Malloc(dataBytes, UnsafeUtility.AlignOf<RLEColumn>(), Allocator.Persistent);
		UnsafeUtility.MemClear(Data, dataBytes);

		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				int noiseHeight = 1 + (int)(Mathf.PerlinNoise(x * 0.1f, z * 0.1f) * 10f);
				Color32 color = Color.white * (noiseHeight / 11f);
				RLEElement bottom = new RLEElement(0, (ushort)noiseHeight, color);
				RLEElement top = new RLEElement((ushort)(DimensionY - noiseHeight), (ushort)(DimensionY - 1), color);
				RLEColumn column = new RLEColumn(bottom, top);
				UnsafeUtility.WriteArrayElement(Data, x * dimensionZ + z, column);
			}
		}
	}

	public void Dispose ()
	{
		for (int i = 0; i < DataItemCount; i++) {
			UnsafeUtility.ReadArrayElement<RLEColumn>(Data, i).Dispose();
		}
		UnsafeUtility.Free(Data, Allocator.Persistent);
		Data = (RLEColumn*)IntPtr.Zero;
		DataItemCount = 0;
	}

	public World CullToVisiblesOnly ()
	{
		World copy = new World(DimensionX, DimensionY, DimensionZ);
		List<RLEElement> cullCache = new List<RLEElement>();

		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				int idx = x * DimensionZ + z;
				CullColumn(Data + idx, copy.Data + idx, x, z, cullCache);
			}
		}

		return copy;
	}

	void CullColumn (RLEColumn* source, RLEColumn* result, int x, int z, List<RLEElement> cullCache)
	{
		result->Clear();

		cullCache.Clear();

		for (int iE = 0; iE < source->Count; iE++) {
			RLEElement element = source->GetAt(iE);
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

		result->Set(cullCache);
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
		RLEColumn column = UnsafeUtility.ReadArrayElement<RLEColumn>(Data, x * DimensionZ + z);
		for (int iE = 0; iE < column.Count; iE++) {
			RLEElement elem = column.GetAt(iE);
			if (elem.Bottom <= y && y <= elem.Top) {
				return false;
			}
		}
		return true;
	}

	public bool TryGetVoxelHeight (int2 position, out RLEColumn elements)
	{
		if (position.x < 0 || position.y < 0 || position.x >= DimensionX || position.y >= DimensionZ) {
			elements = default;
			return false;
		}
		elements = UnsafeUtility.ReadArrayElement<RLEColumn>(Data, position.x * DimensionZ + position.y);
		return true;
	}

	public struct RLEColumn
	{
		RLEElement* elements;
		int count;
		int length;

		public RLEColumn (RLEElement a, RLEElement b)
		{
			count = 2;
			length = 2;
			int sizeBytes = UnsafeUtility.SizeOf<RLEElement>() * 2;
			elements = (RLEElement*)UnsafeUtility.Malloc(sizeBytes, UnsafeUtility.AlignOf<RLEElement>(), Allocator.Persistent);
			UnsafeUtility.WriteArrayElement(elements, 0, a);
			UnsafeUtility.WriteArrayElement(elements, 1, b);
		}

		public int Count { get { return count; } }

		public void Set (List<RLEElement> newElements)
		{
			if (length > newElements.Count) {
				Dispose();

				int sizeBytes = UnsafeUtility.SizeOf<RLEElement>() * newElements.Count;
				elements = (RLEElement*)UnsafeUtility.Malloc(sizeBytes, UnsafeUtility.AlignOf<RLEElement>(), Allocator.Persistent);
				length = newElements.Count;
			}

			count = newElements.Count;
			for (int i = 0; i < count; i++) {
				UnsafeUtility.WriteArrayElement(elements, i, newElements[i]);
			}
		}

		public RLEElement GetAt (int i)
		{
			return UnsafeUtility.ReadArrayElement<RLEElement>(elements, i);
		}

		public void Clear ()
		{
			count = 0;
		}

		public void Dispose ()
		{
			UnsafeUtility.Free(elements, Allocator.Persistent);
			elements = (RLEElement*)IntPtr.Zero;
			count = 0;
			length = 0;
		}
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
