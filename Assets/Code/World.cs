#define REPEATING_WORLD

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
	public bool Exists => Storage.Exists;

	public WorldAllocator Storage;
	int3 dimensions; // always power of two
	int2 dimensionMaskXZ; // dimensions.xz - 1
	int lod; // 0 = 1x1, 1 = 2x2, etc -> bit count to shift
	int indexingMulX; // value to use as {A} in 'idx = x * {A} + y;', it's {A} == dimensions.z >> lod

	public unsafe World (int3 dimensions, int lod) : this()
	{
		this.lod = lod;
		this.dimensions = dimensions;
		indexingMulX = dimensions.z >> lod;
		dimensionMaskXZ = dimensions.xz - 1;
		Storage = WorldAllocator.Allocate(ColumnCount);
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
				RLEColumn downSampled = thisWorld.DownSampleColumn(x, z, elementBuffer, extraLods, ref builder, ref totalVoxels, ref subWorld.Storage);
				*subWorld.Storage.GetColumnPointer(subWorld.GetIndexKnownInBounds(int2(x, z))) = downSampled;
			}
		});

		Debug.Log($"Downsampled world {extraLods} lods to {totalVoxels} voxels, every voxel is {step}^3. Took {sw.Elapsed.TotalMilliseconds} ms");
		return subWorld;
	}

	public void Dispose ()
	{
		int length = ColumnCount;
		Storage.Dispose();
	}

	// downsample a grid of columns into one column
	public RLEColumn DownSampleColumn (int xStart, int zStart, RLEElement[] buffer, int extraLods, ref WorldBuilder.RLEColumnBuilder columnBuilder, ref int totalVoxels, ref WorldAllocator newStorage)
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

		return columnBuilder.ToFinalColumn(1 << (lod + extraLods), (short)(nextVoxelCountY), buffer, ref totalVoxels, ref newStorage);
	}

	/// <summary>
	/// Output a column of data into the columnbuilder; after doing this with all columns the builder will be resolved to a new, merged column
	/// </summary>
	unsafe void DownSamplePartial (int x, int z, int extraLods, ref WorldBuilder.RLEColumnBuilder columnBuilder)
	{
		RLEColumn column = *Storage.GetColumnPointer(GetIndexKnownInBounds(int2(x, z)));
		if (column.RunCount <= 0) {
			return;
		}

		int2 elementBounds = dimensions.y >> lod;
		int nextLod = lod + extraLods;
		ColorARGB32* colorPointer = column.ColorPointer(ref Storage);

		for (int run = 0; run < column.RunCount; run++) {
			RLEElement element = column.GetIndex(ref Storage, run);

			elementBounds = int2(elementBounds.x - element.Length, elementBounds.x);

			if (element.IsAir) {
				continue;
			}

			for (int i = 0; i < element.Length; i++) {
				int Y = elementBounds.x + i;
				int colorIdx = element.ColorsIndex + element.Length - i - 1;
				columnBuilder.SetVoxel(Y >> nextLod, colorPointer[colorIdx]);
			}
		}
	}

	public int GetVoxelColumn (int2 position, ref RLEColumn column)
	{
#if REPEATING_WORLD
		position &= dimensionMaskXZ;
#else
		int2 inBoundsPosition = position & dimensionMaskXZ;
		if (any(inBoundsPosition != position)) {
			return -1;
		}
#endif
		column = *Storage.GetColumnPointer(GetIndexKnownInBounds(position));
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
		RLEColumn* pointer = Storage.GetColumnPointer(index);
		if (pointer->RunCount > 0) {
			throw new InvalidOperationException();
		}
		*pointer = column;
	}

	public struct RLEColumn
	{
		// disgusting hack
		// the RLE elements and the corresponding table of colors are appended into one memory allocation
		// memory layout: <guard> <run 0> <run 1> <run ...> <guard> <color 0> <color 1> <color ..>
		WorldAllocator.StoragePointer storagePointer;
		ushort runCount;
		ushort worldMin;
		ushort worldMax;

		public ushort RunCount { get { return runCount; } }
		public ushort WorldMin { get { return worldMin; } }
		public ushort WorldMax { get { return worldMax; } }

		public RLEElement* ElementGuardStart (ref WorldAllocator storage)
		{
			return storagePointer.ToPointer(ref storage);
		}

		public RLEElement* ElementGuardEnd (ref WorldAllocator storage)
		{
			return storagePointer.ToPointer(ref storage) + runCount + 1;
		}

		public ColorARGB32* ColorPointer (ref WorldAllocator storage)
		{
			return (ColorARGB32*)storagePointer.ToPointer(ref storage) + runCount + 2;
		}

		public RLEColumn (RLEElement[] buffer, int runCount, int solidCount, int voxelScale, ref WorldAllocator allocator)
		{
			this = default;
			if (runCount <= 0) {
				throw new ArgumentOutOfRangeException();
			}
			this.runCount = (ushort)runCount;

			int allocationElementCount = runCount + solidCount + 2;

			storagePointer = allocator.AllocateElements(allocationElementCount);

			RLEElement* startPointer = ElementGuardStart(ref allocator);

			// initialize element guards
			startPointer[0] = new RLEElement(0, 0);
			for (int i = 0; i < runCount; i++) {
				startPointer[i + 1] = buffer[i];
			}
			startPointer[runCount + 1] = new RLEElement(0, 0);

			int worldMin = int.MaxValue;
			int worldMax = int.MinValue;

			int elementBoundsMin = 0;
			int elementBoundsMax = 0;

			for (int i = runCount - 1; i >= 0; i--) {
				RLEElement element = buffer[i];
				elementBoundsMin = elementBoundsMax;
				elementBoundsMax = elementBoundsMin + element.Length;
				if (element.IsAir) {
					continue;
				}
				worldMin = Mathf.Min(worldMin, elementBoundsMin);
				worldMax = Mathf.Max(worldMax, elementBoundsMax);
			}

			if (worldMin == int.MaxValue) {
				throw new InvalidOperationException("only air elements in the RLE");
			}

			this.worldMin = (ushort)(worldMin * voxelScale);
			this.worldMax = (ushort)(worldMax * voxelScale);
		}

		public RLEElement GetIndex (ref WorldAllocator storage, int idx)
		{
			return ElementGuardStart(ref storage)[idx + 1];
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

	public struct WorldAllocator
	{
		void* pointer;
		void* elementsStart;

		public bool Exists => pointer != null;

		int columnCount;
		int elementAllocationCapacity;
		int elementAllocationCount;
		System.Threading.SpinLock allocationLock;

		public RLEColumn* GetColumnPointer (int offset)
		{
			return (RLEColumn*)pointer + offset;
		}

		public RLEElement* GetElementPointer (StoragePointer pointer)
		{
			return (RLEElement*)elementsStart + pointer.Offset;
		}

		public static WorldAllocator Allocate (int columnCount)
		{
			WorldAllocator storage = new WorldAllocator();
			storage.columnCount = columnCount;
			storage.allocationLock = new System.Threading.SpinLock(false);
			GrowMemory(ref storage, columnCount * 4);
			return storage;
		}

		static void GrowMemory (ref WorldAllocator storage, int newElementCapacity)
		{
			long extraBytes = UnsafeUtility.SizeOf<RLEElement>() * (newElementCapacity - storage.elementAllocationCapacity);

			long newBytes = UnsafeUtility.SizeOf<RLEColumn>() * storage.columnCount;
			newBytes += UnsafeUtility.SizeOf<RLEElement>() * newElementCapacity;
			void* newPointer = UnsafeUtility.Malloc(newBytes, UnsafeUtility.AlignOf<RLEColumn>(), Allocator.Persistent);

			if (storage.pointer == null) {
				UnsafeUtility.MemClear(newPointer, newBytes);
			} else {
				Debug.Log($"Grew world storage to {newBytes} bytes from {newBytes - extraBytes}");
				UnsafeUtility.MemCpy(newPointer, storage.pointer, newBytes - extraBytes);
				UnsafeUtility.MemClear((byte*)newPointer + newBytes - extraBytes, extraBytes);
				UnsafeUtility.Free(storage.pointer, Allocator.Persistent);
			}

			storage.pointer = newPointer;
			storage.elementAllocationCapacity = newElementCapacity;
			storage.elementsStart = storage.GetColumnPointer(storage.columnCount);
		}

		public StoragePointer AllocateElements (int elementCount)
		{
			bool taken = false;
			allocationLock.Enter(ref taken);
			try {
				while (true) {
					int oldCount = elementAllocationCount;
					int newCount = elementAllocationCount + elementCount;
					if (newCount <= elementAllocationCapacity) {
						elementAllocationCount = newCount;
						return new StoragePointer
						{
							Offset = oldCount
						};
					} else {
						GrowMemory(ref this, elementAllocationCapacity * 2);
					}
				}
				throw new InvalidOperationException();
			} finally {
				if (taken) {
					allocationLock.Exit();
				}
			}
		}

		public void Dispose ()
		{
			UnsafeUtility.Free(pointer, Allocator.Persistent);
			pointer = null;
		}

		public struct StoragePointer
		{
			public int Offset;

			public RLEElement* ToPointer (ref WorldAllocator storage)
			{
				return storage.GetElementPointer(this);
			}
		}
	}
}
