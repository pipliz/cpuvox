using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

/// <summary>
/// Kinda readonly struct once build.
/// A map of start/end indices to the gigantic elements array, per column position of the world.
/// </summary>
public class WorldBuilder
{
	public int3 Dimensions { get; }

	public RLEColumnBuilder[] WorldColumns;

	int2 dimensionMaskXZ;
	int2 inverseDimensionMaskXZ;

	public WorldBuilder (int x, int y, int z)
	{
		Dimensions = int3(x, y, z);

		dimensionMaskXZ = int2(x, z) - 1;
		inverseDimensionMaskXZ = ~dimensionMaskXZ;

		if (any(dimensionMaskXZ + 1 != int2(x, z))) {
			throw new ArgumentException("Expected x/z to be powers of two");
		}

		WorldColumns = new RLEColumnBuilder[x * z];
	}

	const int VOXELIZE_BUFFER_MAX = 1024 * 256;

	public unsafe void Import (SimpleMesh model)
	{
		NativeArray<float3> verts = model.Vertices.Array;
		NativeArray<Color32> colors = model.VertexColors.Array;
		int vertCount = model.Vertices.Count;
		NativeArray<int> indices = model.Indices.Array;
		int indicesCount = model.Indices.Count;

		VoxelizerHelper.GetVoxelsContext context = new VoxelizerHelper.GetVoxelsContext();
		context.maxDimensions = Dimensions - 1;
		context.positions = (VoxelizerHelper.VoxelizedPosition*)UnsafeUtility.Malloc(
			UnsafeUtility.SizeOf<VoxelizerHelper.VoxelizedPosition>() * VOXELIZE_BUFFER_MAX,
			UnsafeUtility.AlignOf<VoxelizerHelper.VoxelizedPosition>(),
			Allocator.Temp
		);
		context.positionLength = VOXELIZE_BUFFER_MAX;

		for (int i = 0; i < indicesCount; i += 3) {
			context.a = verts[indices[i]];
			context.b = verts[indices[i + 1]];
			context.c = verts[indices[i + 2]];
			context.color = colors[indices[i]];

			int written = VoxelizerHelper.GetVoxels(ref context);

			for (int j = 0; j < written; j++) {
				VoxelizerHelper.VoxelizedPosition pos = context.positions[j];
				ref RLEColumnBuilder column = ref WorldColumns[pos.XZIndex];
				column.SetVoxel(pos.Y, pos.Color);
			}
		}
	}

	public World ToFinalWorld ()
	{
		World world = new World(Dimensions);
		short maxY = (short)(Dimensions.y - 1);
		int jobs = Environment.ProcessorCount * 2;
		int itemsPerJob = (WorldColumns.Length / jobs) + 1;

		ParallelOptions options = new ParallelOptions
		{
			MaxDegreeOfParallelism = Environment.ProcessorCount
		};
		Parallel.For(0, jobs, options, index =>
		{
			int iMin = index * itemsPerJob;
			int iMax = Mathf.Min(WorldColumns.Length, (index + 1) * itemsPerJob);
			World.RLEElement[] buffer = new World.RLEElement[1024 * 32];
			for (int i = iMin; i < iMax; i++) {
				world.SetVoxelColumn(i, WorldColumns[i].ToFinalColumn(maxY, buffer));
			}
		});

		return world;
	}

	public struct RLEColumnBuilder
	{
		struct Voxel : IEquatable<Voxel>
		{
			public short Y;
			public ColorARGB32 Color;

			public bool Equals (Voxel other)
			{
				return Y == other.Y;
			}
		}

		List<Voxel> voxels;

		public void SetVoxel (int Y, ColorARGB32 color)
		{
			if (voxels == null) {
				voxels = new List<Voxel>();
			}
			voxels.Add(new Voxel()
			{
				Color = color,
				Y = (short)Y
			});
		}

		public unsafe World.RLEColumn ToFinalColumn (short topY, World.RLEElement[] buffer)
		{
			World.RLEColumn column = new World.RLEColumn();
			if (voxels == null || voxels.Count == 0) {
				return column;
			}

			// we got a random ordered list of solid colors with possible duplicates
			// so sort it
			// then deduplicate and compact
			// then turn it into RLE (including 'air' RLE elements)

			voxels.Sort((a, b) => b.Y.CompareTo(a.Y));

			short dedupedCount = 0;
			for (int i = 0, lastY = -1; i < voxels.Count; i++) {
				Voxel voxel = voxels[i];
				if (voxel.Y != lastY) {
					voxels[dedupedCount++] = voxel;
					lastY = voxel.Y;
				}
			}

			column.colors = MallocColors(dedupedCount);
			for (int i = 0; i < dedupedCount; i++) {
				column.colors[i] = voxels[i].Color;
			}

			int runs = 0;
			for (short i = 0; i < dedupedCount;) {
				short voxelY = voxels[i].Y;
				short airFromTop = (short)(topY - voxelY);
				if (airFromTop > 0) { // insert some air before this run
					buffer[runs++] = new World.RLEElement(-1, airFromTop);
					topY -= airFromTop;
				}

				short runLength = 1;
				for (short j = (short)(i + 1); j < dedupedCount; j++) {
					if (topY - (j - i) == voxels[j].Y) {
						runLength++;
					} else {
						break;
					}
				}

				buffer[runs++] = new World.RLEElement(i, runLength);
				topY -= runLength;
				i += runLength;
			}

			if (topY >= 0) {
				buffer[runs++] = new World.RLEElement(-1, (short)(topY + 1));
			}

			column.runcount = (ushort)runs;
			column.elements = MallocElements(runs);
			for (int i = 0; i < runs; i++) {
				column.elements[i] = buffer[i];
			}
			return column;
		}
	}

	static unsafe World.RLEElement* MallocElements (int count)
	{
		return (World.RLEElement*)UnsafeUtility.Malloc(
			UnsafeUtility.SizeOf<World.RLEElement>() * count,
			UnsafeUtility.AlignOf<World.RLEElement>(),
			Allocator.Persistent
		);
	}

	static unsafe ColorARGB32* MallocColors (int count)
	{
		return (ColorARGB32*)UnsafeUtility.Malloc(
			UnsafeUtility.SizeOf<ColorARGB32>() * count,
			UnsafeUtility.AlignOf<ColorARGB32>(),
			Allocator.Persistent
		);
	}
}
