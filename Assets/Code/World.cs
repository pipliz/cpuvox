using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

public unsafe struct World : IDisposable
{
	public int3 Dimensions { get { return dimensions; } }
	public int DimensionX { get { return dimensions.x; } }
	public int DimensionY { get { return dimensions.y; } }
	public int DimensionZ { get { return dimensions.z; } }
	public int MaxDimension { get { return cmax(dimensions); } }
	public int ColumnCount { get { return (dimensions.x * dimensions.z) / ((lod + 1) * (lod + 1)); } }
	public int Lod { get { return lod; } }

	int3 dimensions; // always power of two
	int2 dimensionMaskXZ; // dimensions.xz - 1
	int lod; // 0 = 1x1, 1 = 2x2, etc -> bit count to shift
	int indexingMulX; // value to use as {A} in 'idx = x * {A} + y;', it's {A} == dimensions.z >> lod

	RLEColumn* WorldColumns;

	public bool Exists { get { return WorldColumns != null; } }

	public unsafe World (int3 dimensions, int lod) : this()
	{
		this.lod = lod;
		this.dimensions = dimensions;
		indexingMulX = dimensions.z >> lod;
		dimensionMaskXZ = dimensions.xz - 1;
		long bytes = UnsafeUtility.SizeOf<RLEColumn>() * (long)ColumnCount;
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

		// parallelize downsampling on the X-axis
		System.Threading.Tasks.Parallel.For(0, dimensions.x / step, (int i) =>
		{
			int yVoxels = subWorld.dimensions.y >> subWorld.lod;
			RLEElement[] elementBuffer = new RLEElement[yVoxels];
			WorldBuilder.RLEColumnBuilder builder = new WorldBuilder.RLEColumnBuilder();

			int x = i * step;
			for (int z = 0; z < subWorld.dimensions.z; z += step) {
				// downsample a {step, step} grid of columns into one
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

	// downsample a grid of columns into one column
	public RLEColumn DownSampleColumn (int xStart, int zStart, RLEElement[] buffer, int extraLods, ref WorldBuilder.RLEColumnBuilder columnBuilder, ref int totalVoxels)
	{
		// lod 0 = 0, 1
		// lod 1 = 0, 2
		int stepSize = 1 << lod;
		int steps = 1 << extraLods;
		int nextVoxelCountY = (dimensions.y >> (lod + extraLods)) - 1;
		columnBuilder.Clear();

		for (int ix = 0; ix < steps; ix++) {
			int x = xStart + ix * stepSize;
			for (int iz = 0; iz< steps; iz++) {
				int z = zStart + iz * stepSize;
				DownSamplePartial(x, z, extraLods, ref columnBuilder);
			}
		}

		return columnBuilder.ToFinalColumn((short)(nextVoxelCountY), buffer, ref totalVoxels); 
	}

	/// <summary>
	/// Output a column of data into the columnbuilder; after doing this with all columns the builder will be resolved to a new, merged column
	/// </summary>
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
		if (any(inBoundsPosition != position)) {
			return -1;
		}
		column = WorldColumns[GetIndexKnownInBounds(position)];
		return column.RunCount;
	}

	public int GetIndexKnownInBounds (int2 position)
	{
		position >>= lod;
		return position.x * indexingMulX + position.y;
	}

	public void SetVoxelColumn (int2 position, RLEColumn column)
	{
		int index = GetIndexKnownInBounds(position);
		WorldColumns[index].Dispose();
		WorldColumns[index] = column;
	}

	public struct RLEColumn
	{
		// disgusting hack
		// the RLE elements and the corresponding table of colors are appended into one memory allocation
		RLEElement* elementsAndColors;
		ushort runCount;

		public ushort RunCount { get { return runCount; } }
		public RLEElement* ElementGuardStart { get { return elementsAndColors; } }
		public RLEElement* ElementGuardEnd { get { return elementsAndColors + runCount + 1; } } // + 1 to skip start element guad

		RLEElement* FirstElementPointer { get { return ElementGuardStart + 1; } } // + 1 to skip start element guad
		public ColorARGB32* ColorPointer { get { return (ColorARGB32*)ElementGuardEnd + 1; } }

		public RLEColumn (int runCount, int solidCount)
		{
			if (runCount <= 0) {
				throw new ArgumentOutOfRangeException();
			}
			this.runCount = (ushort)runCount;
			elementsAndColors = (RLEElement*)UnsafeUtility.Malloc(
				UnsafeUtility.SizeOf<RLEElement>() * (runCount + solidCount + 2),
				UnsafeUtility.AlignOf<RLEElement>(),
				Allocator.Persistent
			);
			// initialize element guards
			*ElementGuardStart = new RLEElement(0, 0);
			*ElementGuardEnd = new RLEElement(0, 0);
		}

		public RLEElement GetIndex (int idx)
		{
			return FirstElementPointer[idx];
		}

		public void SetIndex (int idx, RLEElement element)
		{
			if (element.Length <= 0 || idx < 0 || idx >= runCount) {
				throw new ArgumentOutOfRangeException();
			}
			FirstElementPointer[idx] = element;
		}

		public void Dispose ()
		{
			if (elementsAndColors != null) {
				UnsafeUtility.Free(elementsAndColors, Allocator.Persistent);
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

		public bool IsValid { get { return Length != 0; } }

		public bool IsAir { get { return ColorsIndex < 0; } }
	}
}
