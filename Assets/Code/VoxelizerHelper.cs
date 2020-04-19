using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public class VoxelizerHelper
{
	[BurstCompile]
	public static unsafe int GetVoxels (ref GetVoxelsContext context)
	{
		int3 maxDimensions = context.maxDimensions;

		float3 a = context.a;
		float3 b = context.b;
		float3 c = context.c;
		Plane plane = new Plane(a, b, c);

		float3 minf = min(a, min(b, c));
		float3 maxf = max(a, max(b, c));

		int3 mini = clamp(int3(floor(minf)), 0, maxDimensions);
		int3 maxi = clamp(int3(ceil(maxf)), 0, maxDimensions);

		int written = 0;
		VoxelizedPosition* positions = context.positions;
		int positionsLength = context.positionLength;
		ColorARGB32 color = context.color;

		for (int x = mini.x; x <= maxi.x; x++) {
			for (int z = mini.z; z <= maxi.z; z++) {
				for (int y = mini.y; y <= maxi.y; y++) {
					if (plane.GetDistanceToPoint(new Vector3(x, y, z)) <= 1f) {
						int idx = x * (maxDimensions.z + 1) + z;
						positions[written++] = new VoxelizedPosition
						{
							Color = color,
							XZIndex = idx,
							Y = y
						};

						if (written == positionsLength) {
							return written;
						}
					}
				}
			}
		}

		return written;
	}

	public struct VoxelizedPosition
	{
		public int XZIndex;
		public int Y;
		public ColorARGB32 Color;
	}

	public unsafe struct GetVoxelsContext
	{
		public float3 a;
		public float3 b;
		public float3 c;
		public ColorARGB32 color;
		public int3 maxDimensions;
		public VoxelizedPosition* positions;
		public int positionLength;
	}
}
