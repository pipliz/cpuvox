using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public class VoxelizerHelper
{
	delegate int ExecuteDelegate (ref GetVoxelsContext context);
	static readonly ExecuteDelegate GetVoxelsInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(GetVoxelsInternal).Invoke;

	public unsafe static int GetVoxels (ref GetVoxelsContext context)
	{
		return GetVoxelsInvoker(ref context);
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	[AOT.MonoPInvokeCallbackAttribute(typeof(ExecuteDelegate))]
	static unsafe int GetVoxelsInternal (ref GetVoxelsContext context)
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

		for (int x = mini.x; x <= maxi.x; x++) {
			for (int z = mini.z; z <= maxi.z; z++) {
				for (int y = mini.y; y <= maxi.y; y++) {
					if (plane.GetDistanceToPoint(new Vector3(x, y, z)) <= 1f) {
						int idx = x * (maxDimensions.z + 1) + z;
						positions[written++] = new VoxelizedPosition
						{
							XZIndex = idx,
							Y = (short)y
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
		public short Y;
	}

	public unsafe struct GetVoxelsContext
	{
		public float3 a;
		public float3 b;
		public float3 c;
		public int3 maxDimensions;
		public VoxelizedPosition* positions;
		public int positionLength;
	}
}
