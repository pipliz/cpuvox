using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

/// <summary>
/// Kinda readonly struct once build.
/// A map of start/end indices to the gigantic elements array, per column position of the world.
/// </summary>
public unsafe struct World : IDisposable
{
	public int3 Dimensions { get { return dimensions; } }
	public int DimensionX { get { return dimensions.x; } }
	public int DimensionY { get { return dimensions.y; } }
	public int DimensionZ { get { return dimensions.z; } }

	int3 dimensions;
	int2 dimensionMaskXZ;
	int2 inverseDimensionMaskXZ;

	public NativeArray<RLEColumn> WorldColumns;

	public bool Exists { get { return WorldColumns.IsCreated; } }

	public unsafe World (int3 dimensions)
	{
		this.dimensions = dimensions;
		dimensionMaskXZ = dimensions.xz - 1;
		inverseDimensionMaskXZ = ~dimensionMaskXZ;
		WorldColumns = new NativeArray<RLEColumn>(dimensions.x * dimensions.z, Allocator.Persistent);
	}

	public void Dispose ()
	{
		for (int i = 0; i < WorldColumns.Length; i++) {
			WorldColumns[i].Dispose();
		}
		WorldColumns.Dispose();
	}

	public RLEColumn GetVoxelColumn (int2 position)
	{
		if (math.any((position & inverseDimensionMaskXZ) != 0)) {
			return default; // out of bounds
		}
		position = position & dimensionMaskXZ;
		return WorldColumns[position.x * dimensions.z + position.y];
	}

	public void SetVoxelColumn (int index, RLEColumn column)
	{
		WorldColumns[index].Dispose();
		WorldColumns[index] = column;
	}

	public struct RLEColumn
	{
		public RLEElement* pointer;
		public ushort runcount;

		public int RunCount { get { return runcount; } }

		public RLEElement GetIndex (int idx)
		{
			return pointer[idx];
		}

		public void Dispose ()
		{
			if (pointer != null) {
				for (int i = 0; i < runcount; i++) {
					pointer[i].Dispose();
				}
				UnsafeUtility.Free(pointer, Allocator.Persistent);
			}
		}
	}

	public unsafe struct RLEElement
	{
		public ushort Length;
		public ColorARGB32* Colors;

		public bool IsAir { get { return Colors == null; } }

		public RLEElement (ushort length, ColorARGB32* colors)
		{
			Length = length;
			Colors = colors;
		}

		public ColorARGB32 GetColor (int idx)
		{
			return Colors[idx];
		}

		public void Dispose ()
		{
			if (Colors != null) {
				UnsafeUtility.Free(Colors, Allocator.Persistent);
			}
		}
	}
}
