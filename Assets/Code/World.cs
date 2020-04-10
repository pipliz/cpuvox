using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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

	public NativeArray<RLEColumn> WorldColumns;

	int2 dimensionMaskXZ;
	int2 inverseDimensionMaskXZ;

	public bool HasModel { get; private set; }

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
		inverseDimensionMaskXZ = ~dimensionMaskXZ;

		if (math.any(dimensionMaskXZ + 1 != new int2(DimensionX, DimensionZ))) {
			throw new ArgumentException("Expected x/z to be powers of two");
		}

		WorldColumns = new NativeArray<RLEColumn>(dimensionX * dimensionZ, Allocator.Persistent);
		HasModel = false;
	}

	public void Import (SimpleMesh model)
	{
		int tris = model.Indices.Count / 3;
		for (int i = 0; i < model.Indices.Count; i += 3) {
			Vector3 a = model.Vertices[model.Indices[i]];
			Vector3 b = model.Vertices[model.Indices[i + 1]];
			Vector3 c = model.Vertices[model.Indices[i + 2]];
			Color32 color = model.VertexColors[model.Indices[i]];

			Plane plane = new Plane(a, b, c);

			Vector3 minf = Vector3.Min(Vector3.Min(a, b), c);
			Vector3 maxf = Vector3.Max(Vector3.Max(a, b), c);

			Vector3Int min = Vector3Int.FloorToInt(minf);
			Vector3Int max = Vector3Int.CeilToInt(maxf);

			min.x = Mathf.Clamp(min.x, 0, DimensionX - 1);
			min.y = Mathf.Clamp(min.y, 0, DimensionY - 1);
			min.z = Mathf.Clamp(min.z, 0, DimensionZ - 1);

			max.x = Mathf.Clamp(max.x, 0, DimensionX - 1);
			max.y = Mathf.Clamp(max.y, 0, DimensionY - 1);
			max.z = Mathf.Clamp(max.z, 0, DimensionZ - 1);

			for (int x = min.x; x <= max.x; x++) {
				for (int z = min.z; z <= max.z; z++) {
					for (int y = min.y; y <= max.y; y++) {
						if (plane.GetDistanceToPoint(new Vector3(x, y, z)) <= 1f) {
							int idx = x * DimensionZ + z;
							RLEColumn column = WorldColumns[idx];
							column.AddVoxel(y, color);
							WorldColumns[idx] = column;
						}
					}
				}
			}
		}

		for (int i = 0; i < WorldColumns.Length; i++) {
			var col = WorldColumns[i];
			col.Sort();
			WorldColumns[i] = col;
		}

		HasModel = true;
	}

	public void Dispose ()
	{
		WorldColumns.Dispose();
	}

	public RLEColumn GetVoxelColumn (int2 position)
	{
		if (math.any((position & inverseDimensionMaskXZ) != 0)) {
			return default; // out of bounds
		}
		position = position & dimensionMaskXZ;
		return WorldColumns[position.x * DimensionZ + position.y];
	}

	public struct RLEColumn
	{
		RLEElement* pointer;
		ushort runcount;
		ushort capacity;

		public int RunCount { get { return runcount; } }

		public RLEElement GetIndex (int idx)
		{
			return pointer[idx];
		}

		public void AddVoxel (int Y, Color32 color)
		{
			if (pointer == null) {
				pointer = MallocRuns(2);
				runcount = 0;
				capacity = 2;
			}

			for (int i = 0; i < runcount; i++) {
				RLEElement element = pointer[i];
				// cases:
				// extend bottom
				// override color
				// extend top
				// not near, continue until end or one is near
				if (element.Bottom == Y + 1) {
					element.Bottom--;
					pointer[i] = element;
					return;
				}
				if (element.Top == Y) {
					element.Top++;
					pointer[i] = element;
					return;
				}

				if (element.Bottom <= Y && element.Top > Y) {
					return; // overlap
				}
			}

			// couldnt find overlap/extension, so add one
			if (runcount >= capacity) {
				pointer = ReallocRuns(pointer, runcount, capacity * 2);
				capacity = (ushort)(capacity * 2);
			}

			pointer[runcount++] = new RLEElement(Y, Y + 1, color);
		}

		public void Sort ()
		{
			if (runcount < 2) {
				return;
			}

			for (int i = 0; i < runcount - 1; i++) {
				int jMin = i;

				for (int j = i + 1; j < runcount; j++) {
					if (pointer[j].Bottom < pointer[jMin].Bottom) {
						jMin = j;
					}
				}

				if (jMin != i) {
					RLEElement tmp = pointer[i];
					pointer[i] = pointer[jMin];
					pointer[jMin] = tmp;
				}
			}
		}

		static RLEElement* MallocRuns (int count)
		{
			return (RLEElement*)UnsafeUtility.Malloc(
				UnsafeUtility.SizeOf<RLEElement>() * count,
				UnsafeUtility.AlignOf<RLEElement>(),
				Allocator.Persistent
			);
		}

		static RLEElement* ReallocRuns (RLEElement* ptr, int oldCount, int newCapacity)
		{
			RLEElement* newPtr = MallocRuns(newCapacity);
			UnsafeUtility.MemCpy(newPtr, ptr, UnsafeUtility.SizeOf<RLEElement>() * oldCount);
			UnsafeUtility.Free(ptr, Allocator.Persistent);
			return newPtr;
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
