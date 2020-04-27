﻿using System;
using System.Collections.Generic;
using System.Threading;
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
		int taskCount = Environment.ProcessorCount;

		int vertCount = model.VertexCount;
		int indicesCount = model.IndexCount;
		int triangleCount = indicesCount / 3;

		Task[] tasks = new Task[taskCount];
		VoxelizerHelper.Initialize();

		for (int k = 0; k < taskCount; k++) {
			VoxelizerHelper.GetVoxelsContext context = new VoxelizerHelper.GetVoxelsContext();
			context.maxDimensions = Dimensions - 1;
			context.positions = (VoxelizerHelper.VoxelizedPosition*)UnsafeUtility.Malloc(
				UnsafeUtility.SizeOf<VoxelizerHelper.VoxelizedPosition>() * VOXELIZE_BUFFER_MAX,
				UnsafeUtility.AlignOf<VoxelizerHelper.VoxelizedPosition>(),
				Allocator.Persistent
			);
			context.positionLength = VOXELIZE_BUFFER_MAX;
			context.verts = model.Vertices;
			context.colors = model.VertexColors;
			context.indices = model.Indices;

			int iStart = 3 * k * (triangleCount / taskCount);
			int iStartNextTask = 3 * (k + 1) * (triangleCount / taskCount);
			if (k == taskCount - 1) {
				iStartNextTask = indicesCount;
			}

			tasks[k] = Task.Run(() =>
			{
				try {
					for (int i = iStart; i < iStartNextTask; i += 3) {
						VoxelizerHelper.GetVoxels(ref context, i);

						int written = context.writtenVoxelCount;
						ColorARGB32 color = context.averagedColor;

						for (int j = 0; j < written; j++) {
							VoxelizerHelper.VoxelizedPosition pos = context.positions[j];
							ref RLEColumnBuilder column = ref WorldColumns[pos.XZIndex];
							column.SetVoxel(pos.Y, color);
						}
					}
				} finally {
					UnsafeUtility.Free(context.positions, Allocator.Persistent);
				}
			});
		}

		Task.WaitAll(tasks);
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

		int totalVoxels = 0;

		Parallel.For(0, jobs, options, index =>
		{
			int iMin = index * itemsPerJob;
			int iMax = Mathf.Min(WorldColumns.Length, (index + 1) * itemsPerJob);
			World.RLEElement[] buffer = new World.RLEElement[1024 * 32];
			for (int i = iMin; i < iMax; i++) {
				world.SetVoxelColumn(i, WorldColumns[i].ToFinalColumn(maxY, buffer, ref totalVoxels));
			}
		});

		Debug.Log($"Loaded map with {totalVoxels} voxels");

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
			List<Voxel> copy;
			// loop to allocate the list and assign it
			while (true) {
				copy = voxels;
				if (copy == null) {
					copy = new List<Voxel>();
					if (Interlocked.CompareExchange(ref voxels, copy, null) == null) {
						break;
					}
				} else {
					break;
				}
			}

			lock (copy) {
				copy.Add(new Voxel()
				{
					Color = color,
					Y = (short)Y
				});
			}
		}

		public unsafe World.RLEColumn ToFinalColumn (short topY, World.RLEElement[] buffer, ref int totalVoxels)
		{
			if (voxels == null || voxels.Count == 0) {
				return default;
			}

			// we got a random ordered list of solid colors with possible duplicates
			// so sort it
			// then deduplicate and compact
			// then turn it into RLE (including 'air' RLE elements)

			voxels.Sort((a, b) => b.Y.CompareTo(a.Y));

			short dedupedCount = 0;
			{
				List<Voxel> voxelsCopy = voxels;
				int r = 0, g = 0, b = 0, weight = 1;
				for (int i = 0, lastY = -1; i < voxels.Count; i++) {
					Voxel voxel = voxels[i];

					if (voxel.Y == lastY) {
						// queue this up to be flushed when we find a different Y voxel
						r += voxel.Color.r;
						g += voxel.Color.g;
						b += voxel.Color.b;
						weight++;
					} else {
						if (weight > 1) { // have some extra data for the previous voxel written
							AddWeightsToPrevious();
						}
						voxels[dedupedCount++] = voxel;
						lastY = voxel.Y;
					}
				}

				if (weight > 1) {
					AddWeightsToPrevious();
				}

				void AddWeightsToPrevious ()
				{
					Voxel previous = voxelsCopy[dedupedCount - 1];
					ref ColorARGB32 col = ref previous.Color;
					col.r = (byte)((col.r + r) / weight);
					col.g = (byte)((col.g + g) / weight);
					col.b = (byte)((col.b + b) / weight);
					voxelsCopy[dedupedCount - 1] = previous;
					r = g = b = 0;
					weight = 1;
				}
			}

			System.Threading.Interlocked.Add(ref totalVoxels, dedupedCount);

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

			World.RLEColumn column = new World.RLEColumn(runs, dedupedCount);
			World.RLEElement* elementPtr = column.ElementsPointer;

			for (int i = 0; i < runs; i++) {
				elementPtr[i] = buffer[i];
			}

			ColorARGB32* colorPtr = column.ColorPointer;
			for (int i = 0; i < dedupedCount; i++) {
				colorPtr[i] = voxels[i].Color;
			}
			return column;
		}
	}
}
