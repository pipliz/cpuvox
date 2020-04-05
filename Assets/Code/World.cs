﻿using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Kinda readonly struct once build.
/// A map of start/end indices to the gigantic elements array, per column position of the world.
/// </summary>
public unsafe struct World : IDisposable
{
	public int DimensionX { get; }
	public int DimensionY { get; }
	public int DimensionZ { get; }

	public NativeArray<RLEElement> WorldElements;
	public NativeArray<RLEColumn> WorldColumns;

	int2 dimensionMaskXZ;
	int worldElementsUsed;

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

		WorldElements = new NativeArray<RLEElement>(dimensionX * dimensionZ, Allocator.Persistent);
		worldElementsUsed = 0;
		WorldColumns = new NativeArray<RLEColumn>(dimensionX * dimensionZ, Allocator.Persistent);
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

			// forgive me for this shameful voxelization code
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
				int idxRaw = x * DimensionY * DimensionZ + z * DimensionY;
				short elementCount = 0;
				int elementStart = 0;
				int runStart = 0;
				int runCount = 1;
				byte runType = rawData[idxRaw];
				int maxIdx = idxRaw + DimensionY;
				for (int i = idxRaw + 1; i < maxIdx; i++) {
					byte testType = rawData[i];
					if (testType == runType) {
						runCount++;
					} else {
						if (runType > 0) {
							float avgHeight = (runStart + 0.5f * runCount) / DimensionY;
							avgHeight = 0.3f + avgHeight * 0.7f;

							RLEElement element = new RLEElement(runStart, runStart + runCount, Color.white * avgHeight);
							
							int insertedIdx = AddElement(element);
							if (elementCount == 0) {
								elementStart = insertedIdx;
							}
							elementCount++;
						}
						runStart = i - idxRaw;
						runCount = 1;
						runType = testType;
					}
				}

				WorldColumns[x * DimensionZ + z] = new RLEColumn(elementStart, elementCount);
			}
		}
	}

	int AddElement (RLEElement element)
	{
		if (worldElementsUsed == WorldElements.Length) {
			NativeArray<RLEElement> newElements = new NativeArray<RLEElement>(WorldElements.Length * 2, Allocator.Persistent);
			NativeArray<RLEElement>.Copy(WorldElements, 0, newElements, 0, worldElementsUsed);
			WorldElements.Dispose();
			WorldElements = newElements;
		}
		WorldElements[worldElementsUsed] = element;
		return worldElementsUsed++;
	}

	public void Dispose ()
	{
		WorldColumns.Dispose();
		WorldElements.Dispose();
	}

	public RLEColumn GetVoxelColumn (int2 position)
	{
		position = position & dimensionMaskXZ;
		return WorldColumns[position.x * DimensionZ + position.y];
	}

	public struct RLEColumn
	{
		public readonly int elementIndex;
		public readonly short elementCount;

		public RLEColumn (int elementIndex, short elementCount)
		{
			this.elementIndex = elementIndex;
			this.elementCount = elementCount;
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
	}
}
