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

	public RLEColumn* WorldColumns;

	public bool Exists { get { return WorldColumns != null; } }

	public unsafe World (int3 dimensions)
	{
		this.dimensions = dimensions;
		dimensionMaskXZ = dimensions.xz - 1;
		inverseDimensionMaskXZ = ~dimensionMaskXZ;
		long bytes = (long)UnsafeUtility.SizeOf<RLEColumn>() * (dimensions.x * dimensions.z);
		WorldColumns = (RLEColumn*)UnsafeUtility.Malloc(bytes, UnsafeUtility.AlignOf<RLEColumn>(), Allocator.Persistent);
		UnsafeUtility.MemClear(WorldColumns, bytes);
	}

	public void Dispose ()
	{
		int length = dimensions.x * dimensions.z;
		for (int i = 0; i < length; i++) {
			WorldColumns[i].Dispose();
		}
		UnsafeUtility.Free(WorldColumns, Allocator.Persistent);
		WorldColumns = null;
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
		public RLEElement* elements;
		public ColorARGB32* colors;
		public ushort runcount;

		public int RunCount { get { return runcount; } }

		public RLEElement GetIndex (int idx)
		{
			return elements[idx];
		}

		public ColorARGB32 GetColor (int idx)
		{
			return colors[idx];
		}

		public void Dispose ()
		{
			if (elements != null) {
				UnsafeUtility.Free(elements, Allocator.Persistent);
			}
			if (colors != null) {
				UnsafeUtility.Free(colors, Allocator.Persistent);
			}
		}
	}

	public unsafe struct RLEElement
	{
		public short ColorsIndex;
		public short Length;

		public RLEElement (short colorsIndex, short length)
		{
			ColorsIndex = colorsIndex;
			Length = length;
		}

		public bool IsAir { get { return ColorsIndex < 0; } }
	}
}
