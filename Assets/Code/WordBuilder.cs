using System;
using System.Collections.Generic;
using Unity.Burst;
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
				column.SetVoxel(Dimensions.y, pos.Y, pos.Color);
			}
		}
	}

	public World ToFinalWorld ()
	{
		World world = new World(Dimensions);
		for (int i = 0; i < WorldColumns.Length; i++) {
			world.SetVoxelColumn(i, WorldColumns[i].ToFinalColumn());
		}
		return world;
	}

	public struct RLEColumnBuilder
	{
		List<RLEElementBuilder> Runs;

		public void SetVoxel (int DimensionY, int Y, ColorARGB32 color)
		{
			if (Runs == null) {
				Runs = new List<RLEElementBuilder>(4)
				{
					RLEElementBuilder.NewAir((short)DimensionY)
				};
			}

			int runTop = DimensionY;
			int nextRunTop = DimensionY;

			for (int i = 0; i < Runs.Count; i++) {
				RLEElementBuilder run = Runs[i];
				runTop = nextRunTop;
				// bottom is actually bottom coord - 1 (since with length 1, top/bottom are the same Y-voxel, but these 'indices' are different)
				nextRunTop -= run.Length;

				if (Y <= nextRunTop) {
					continue;
				}


				if (run.IsSolids) {
					run.Colors[runTop - Y] = color;
				} else {
					RLEElementBuilder newRun = RLEElementBuilder.NewSolo(color);
					if (Y == runTop) {
						run.Length--;
						Runs[i] = run;
						Runs.Insert(i, newRun);
					} else if (Y == nextRunTop - 1) {
						run.Length--;
						Runs[i] = run;
						Runs.Insert(i + 1, newRun);
					} else {
						run.Length = (short)(runTop - Y);
						Runs[i] = run;
						Runs.Insert(i + 1, newRun);
						Runs.Insert(i + 2, RLEElementBuilder.NewAir((short)(Y - nextRunTop - 1)));
					}
				}

				for (int j = i - 1; j <= i + 1; j++) {
					if (j < 0) { continue; }

					while (j < Runs.Count && Runs[j].Length == 0) {
						Runs[j].ReturnToPool();
						Runs.RemoveAt(j);
					}

					while (j < Runs.Count - 1) {
						RLEElementBuilder prev = Runs[j];
						if (!prev.IsSolids) { break; }
						RLEElementBuilder next = Runs[j + 1];
						if (!next.IsSolids) { break; }

						prev.Colors.AddRange(next.Colors);
						prev.Length += next.Length;
						Runs[j] = prev;
						next.ReturnToPool();
						Runs.RemoveAt(j + 1);
					}
				}

				return;
			}
		}

		public unsafe World.RLEColumn ToFinalColumn ()
		{
			World.RLEColumn column = new World.RLEColumn();
			if (Runs == null) {
				return column;
			}
			column.elements = MallocElements(Runs.Count);
			column.runcount = (ushort)Runs.Count;

			short colorCount = 0;
			for (int i = 0; i < Runs.Count; i++) {
				RLEElementBuilder run = Runs[i];
				if (run.IsSolids) {
					colorCount += (short)run.Colors.Count;
				}
			}

			if (colorCount > 0) {
				column.colors = MallocColors(colorCount);
			}

			colorCount = 0;
			for (int i = 0; i < Runs.Count; i++) {
				column.elements[i] = Runs[i].ToFinalElement(ref colorCount, column.colors);
			}


			return column;
		}
	}

	public struct RLEElementBuilder
	{
		public short Length;
		public List<ColorARGB32> Colors;

		const int COLORS_POOL_SIZE = 20;
		static Stack<List<ColorARGB32>> ColorsPool = new Stack<List<ColorARGB32>>(COLORS_POOL_SIZE);

		public bool IsSolids { get { return Colors != null; } }

		public unsafe World.RLEElement ToFinalElement (ref short colorIndexStart, ColorARGB32* colors)
		{
			if (!IsSolids) {
				return new World.RLEElement(-1, Length);
			}

			World.RLEElement element = new World.RLEElement(colorIndexStart, Length);
			for (int i = 0; i < Colors.Count; i++) {
				colors[colorIndexStart + i] = Colors[i];
			}
			colorIndexStart += Length;
			return element;
		}

		public void ReturnToPool ()
		{
			if (Colors != null && ColorsPool.Count < COLORS_POOL_SIZE) {
				Colors.Clear();
				ColorsPool.Push(Colors);
			}
		}

		public static RLEElementBuilder NewAir (short length)
		{
			return new RLEElementBuilder
			{
				Length = length
			};
		}

		public static RLEElementBuilder NewSolo (ColorARGB32 color)
		{
			var colors = ColorsPool.Count > 0 ? ColorsPool.Pop() : new List<ColorARGB32>(4);
			colors.Add(color);

			return new RLEElementBuilder
			{
				Length = 1,
				Colors = colors
			};
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
