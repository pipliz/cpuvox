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
	public int MaxDimension { get { return math.cmax(dimensions); } }

	int3 dimensions;
	int2 dimensionMaskXZ;

	public RLEColumn* WorldColumns;

	public bool Exists { get { return WorldColumns != null; } }

	public unsafe World (int3 dimensions)
	{
		this.dimensions = dimensions;
		dimensionMaskXZ = dimensions.xz - 1;
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

	public int GetVoxelColumn (int2 position, ref RLEColumn column)
	{
		int2 inBoundsPosition = position & dimensionMaskXZ;
		if (math.any(inBoundsPosition != position)) {
			return -1;
		}
		column = WorldColumns[inBoundsPosition.x * dimensions.z + inBoundsPosition.y];
		return column.RunCount;
	}

	public void SetVoxelColumn (int index, RLEColumn column)
	{
		WorldColumns[index].Dispose();
		WorldColumns[index] = column;
	}

	public struct RLEColumn
	{
		RLEElement* elementsAndColors;
		short runCount;

		public int RunCount { get { return runCount; } }
		public RLEElement* ElementsPointer { get { return elementsAndColors; } }
		public ColorARGB32* ColorPointer { get { return (ColorARGB32*)elementsAndColors + runCount; } }

		public RLEColumn (int runCount, int solidCount)
		{
			this.runCount = (short)runCount;
			elementsAndColors = (RLEElement*)UnsafeUtility.Malloc(
				UnsafeUtility.SizeOf<RLEElement>() * (runCount + solidCount),
				UnsafeUtility.AlignOf<RLEElement>(),
				Allocator.Persistent
			);
		}

		public RLEElement GetIndex (int idx)
		{
			return ElementsPointer[idx];
		}

		public void Dispose ()
		{
			if (ElementsPointer != null) {
				UnsafeUtility.Free(ElementsPointer, Allocator.Persistent);
			}
		}
	}

	/// <summary>
	/// Must be same size as the color struct!
	/// </summary>
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
