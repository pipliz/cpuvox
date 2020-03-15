using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

public unsafe struct World : IDisposable
{
	public int DimensionX { get; }
	public int DimensionY { get; }
	public int DimensionZ { get; }

	[NativeDisableUnsafePtrRestriction]
	RLEColumn* Data;
	int DataItemCount;

	int2 dimensionMaskXZ;

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

		dimensionMaskXZ = new int2(DimensionX - 1, DimensionZ - 1);
		if (math.any((new int2(DimensionX - 1, DimensionZ - 1) & dimensionMaskXZ) + 1 != new int2(DimensionX, DimensionZ))) {
			throw new ArgumentException("Expected x/z to be powers of two");
		}

		DataItemCount = dimensionX * dimensionZ;

		int dataBytes = UnsafeUtility.SizeOf<RLEColumn>() * DataItemCount;
		Data = (RLEColumn*)UnsafeUtility.Malloc(dataBytes, UnsafeUtility.AlignOf<RLEColumn>(), Allocator.Persistent);
		UnsafeUtility.MemClear(Data, dataBytes);

		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				UnsafeUtility.WriteArrayElement(Data, x * dimensionZ + z, new RLEColumn(4));
			}
		}
	}

	public void Import (PlyModel model)
	{
		byte[] rawData = new byte[DimensionX * DimensionY * DimensionZ];
		
		int tris = model.Indices.Length / 3;
		for (int i = 0; i < model.Indices.Length; i += 3) {
			Vector3 a = model.Vertices[model.Indices[i]];
			Vector3 b = model.Vertices[model.Indices[i + 1]];
			Vector3 c = model.Vertices[model.Indices[i + 2]];

			Bounds bounds = new Bounds(a, Vector3.zero);
			bounds.Encapsulate(b);
			bounds.Encapsulate(c);

			Vector3Int min = Vector3Int.FloorToInt(bounds.min);
			Vector3Int max = Vector3Int.CeilToInt(bounds.max);

			min.x = Mathf.Clamp(min.x, 0, DimensionX - 1);
			min.y = Mathf.Clamp(min.y, 0, DimensionY - 1);
			min.z = Mathf.Clamp(min.z, 0, DimensionZ - 1);

			max.x = Mathf.Clamp(max.x, 0, DimensionX - 1);
			max.y = Mathf.Clamp(max.y, 0, DimensionY - 1);
			max.z = Mathf.Clamp(max.z, 0, DimensionZ - 1);

			for (int x = min.x; x <= max.x; x++) {
				int idxX = x * DimensionY * DimensionZ;
				for (int z = min.z; z <= max.z; z++) {
					int idxXZ = idxX + z * DimensionY;
					for (int y = min.y; y <= max.y; y++) {
						rawData[idxXZ + y] = 255;
					}
				}
			}
		}

		for (int x = 0; x < DimensionX; x++) {
			for (int z = 0; z < DimensionZ; z++) {
				int idx = x * DimensionZ + z;
				int idxRaw = x * DimensionY * DimensionZ + z * DimensionY;
				(Data + idx)->Set(rawData, idxRaw, DimensionY);
			}
		}
	}

	public void Dispose ()
	{
		for (int i = 0; i < DataItemCount; i++) {
			(Data + i)->Dispose();
		}
		UnsafeUtility.Free(Data, Allocator.Persistent);
		Data = (RLEColumn*)IntPtr.Zero;
		DataItemCount = 0;
	}

	public RLEColumn GetVoxelColumn (int2 position)
	{
		position = position & dimensionMaskXZ;
		return UnsafeUtility.ReadArrayElement<RLEColumn>(Data, position.x * DimensionZ + position.y);
	}

	public struct RLEColumn
	{
		RLEElement* elements;
		int count;
		int length;

		public RLEColumn (int capacity)
		{
			count = 0;
			length = capacity;
			int sizeBytes = UnsafeUtility.SizeOf<RLEElement>() * capacity;
			elements = (RLEElement*)UnsafeUtility.Malloc(sizeBytes, UnsafeUtility.AlignOf<RLEElement>(), Allocator.Persistent);
		}

		public int Count { get { return count; } }

		public RLEElement this[int idx]
		{
			get { return UnsafeUtility.ReadArrayElement<RLEElement>(elements, idx); }
			set { UnsafeUtility.WriteArrayElement(elements, idx, value); }
		}

		public void Set (byte[] data, int startIdx, int dimensionY)
		{
			int runStart = 0;
			int runCount = 1;
			byte runType = data[startIdx];
			int maxIdx = startIdx + dimensionY;
			for (int i = startIdx + 1; i < maxIdx; i++) {
				byte testType = data[i];
				if (testType == runType) {
					runCount++;
				} else {
					if (runType > 0) {
						float avgHeight = (runStart + 0.5f * runCount) / dimensionY;
						avgHeight = 0.3f + avgHeight * 0.7f;
						AddRun(new RLEElement(runStart, runStart + runCount - 1, Color.white * avgHeight));
					}
					runStart = i - startIdx;
					runCount = 1;
					runType = testType;
				}
			}
		}

		public void AddRun (RLEElement element)
		{
			if (count >= length) {
				int lengthNew = length * 2;
				int sizeBytesNew = lengthNew * UnsafeUtility.SizeOf<RLEElement>();
				RLEElement* newElements = (RLEElement*)UnsafeUtility.Malloc(sizeBytesNew, UnsafeUtility.AlignOf<RLEElement>(), Allocator.Persistent);
				UnsafeUtility.MemCpy(newElements, elements, UnsafeUtility.SizeOf<RLEElement>() * count);
				UnsafeUtility.Free(elements, Allocator.Persistent);
				elements = newElements;
				length = lengthNew;
			}

			elements[count++] = element;
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
		public ColorARGB32 Color;

		public RLEElement (int bottom, int top, Color32 color)
		{
			Bottom = (ushort)bottom;
			Top = (ushort)top;
			Color = color;
		}

		public RLEElement (int bottom, int top)
		{
			Bottom = (ushort)bottom;
			Top = (ushort)top;
			Color = new Color32(255, 255, 255, 255);
		}
	}
}
