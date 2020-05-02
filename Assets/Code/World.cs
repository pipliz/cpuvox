using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

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
	public int MaxDimension { get { return cmax(dimensions); } }
	public int ColumnCount { get { return (dimensions.x * dimensions.z) / ((lod + 1) * (lod + 1)); } }
	public int Lod { get { return lod; } }

	int3 dimensions;
	int2 dimensionMaskXZ;
	int lod; // 0 = 1x1, 1 = 2x2, etc -> bit count to shift
	int indexingMulX;

	public RLEColumn* WorldColumns;

	public bool Exists { get { return WorldColumns != null; } }

	public unsafe World (int3 dimensions, int lod)
	{
		this.lod = lod;
		this.dimensions = dimensions;
		this.indexingMulX = dimensions.z >> lod;
		dimensionMaskXZ = dimensions.xz - 1;
		long bytes = UnsafeUtility.SizeOf<RLEColumn>() * (long)((dimensions.x * dimensions.z) / ((lod + 1) * (lod + 1)));
		WorldColumns = (RLEColumn*)UnsafeUtility.Malloc(bytes, UnsafeUtility.AlignOf<RLEColumn>(), Allocator.Persistent);
		UnsafeUtility.MemClear(WorldColumns, bytes);
	}

	public unsafe World DownSample (int extraLods)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		World subWorld = new World(dimensions, lod + extraLods);
		World thisWorld = this;
		int step = 1 << subWorld.lod;
		int totalVoxels = 0;

		System.Threading.Tasks.Parallel.For(0, dimensions.x / step, (int i) =>
		{
			int yVoxels = subWorld.dimensions.y >> subWorld.lod;
			RLEElement[] elementBuffer = new RLEElement[yVoxels];
			WorldBuilder.RLEColumnBuilder builder = new WorldBuilder.RLEColumnBuilder();

			int x = i * step;
			for (int z = 0; z < subWorld.dimensions.z; z += step) {
				RLEColumn downSampled = thisWorld.DownSampleColumn(x, z, elementBuffer, extraLods, ref builder, ref totalVoxels);
				subWorld.WorldColumns[subWorld.GetIndexKnownInBounds(int2(x, z))] = downSampled;
			}
		});

		Debug.Log($"Downsampled world {extraLods} lods to {totalVoxels} voxels, every voxel is {step}^3. Took {sw.Elapsed.TotalMilliseconds} ms");
		return subWorld;
	}

	public void Dispose ()
	{
		int length = ColumnCount;
		for (int i = 0; i < length; i++) {
			WorldColumns[i].Dispose();
		}
		UnsafeUtility.Free(WorldColumns, Allocator.Persistent);
		WorldColumns = null;
	}

	public RLEColumn DownSampleColumn (int xStart, int zStart, RLEElement[] buffer, int extraLods, ref WorldBuilder.RLEColumnBuilder columnBuilder, ref int totalVoxels)
	{
		// lod 0 = 0, 1
		// lod 1 = 0, 2
		int stepSize = 1 << lod;
		int steps = 1 << extraLods;
		int nextVoxelCountY = (dimensions.y >> (lod + extraLods)) - 1;
		columnBuilder.Clear();

		for (int ix = 0; ix < steps; ix++) {
			for (int iz = 0; iz< steps; iz++) {
				int x = xStart + ix * stepSize;
				int z = zStart + iz * stepSize;
				DownSamplePartial(x, z, extraLods, ref columnBuilder);
			}
		}

		return columnBuilder.ToFinalColumn((short)(nextVoxelCountY), buffer, ref totalVoxels); 
	}

	unsafe void DownSamplePartial (int x, int z, int extraLods, ref WorldBuilder.RLEColumnBuilder columnBuilder)
	{
		RLEColumn column = WorldColumns[GetIndexKnownInBounds(int2(x, z))];
		if (column.RunCount <= 0) {
			return;
		}

		int2 elementBounds = dimensions.y >> lod;
		int nextLod = lod + extraLods;

		for (int run = 0; run < column.RunCount; run++) {
			RLEElement element = column.GetIndex(run);

			elementBounds = int2(elementBounds.x - element.Length, elementBounds.x);

			if (element.IsAir) {
				continue;
			}

			for (int i = 0; i < element.Length; i++) {
				int Y = elementBounds.x + i;
				int colorIdx = element.ColorsIndex + element.Length - i - 1;
				ColorARGB32 color = column.ColorPointer[colorIdx];
				columnBuilder.SetVoxel(Y >> nextLod, color);
			}
		}
	}

	public int GetVoxelColumn (int2 position, ref RLEColumn column)
	{
		int2 inBoundsPosition = position & dimensionMaskXZ;
		if (math.any(inBoundsPosition != position)) {
			return -1;
		}
		inBoundsPosition >>= lod;
		column = WorldColumns[inBoundsPosition.x * indexingMulX + inBoundsPosition.y];
		return column.RunCount;
	}

	public int GetIndexKnownInBounds (int2 position)
	{
		position >>= lod;
		return position.x * indexingMulX + position.y;
	}

	public int2 GetPositionFromLoddedIndex (int index)
	{
		return math.int2(index / indexingMulX, index % indexingMulX) << lod;
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
