using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public class VoxelizerHelper
{
	delegate void ExecuteDelegate (ref GetVoxelsContext context, int indexStart);
	static readonly ExecuteDelegate GetVoxelsInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(GetVoxelsInternal).Invoke;

	public unsafe static void GetVoxels (ref GetVoxelsContext context, int indexStart)
	{
		GetVoxelsInvoker(ref context, indexStart);
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	[AOT.MonoPInvokeCallbackAttribute(typeof(ExecuteDelegate))]
	static unsafe void GetVoxelsInternal (ref GetVoxelsContext context, int indexStart)
	{
		int i0 = context.indices[indexStart];
		int i1 = context.indices[indexStart + 1];
		int i2 = context.indices[indexStart + 2];

		Color32 color0 = context.colors[i0];
		Color32 color1 = context.colors[i1];
		Color32 color2 = context.colors[i2];
		ColorARGB32 color;
		color.r = (byte)((color0.r + color1.r + color2.r) / 3);
		color.g = (byte)((color0.g + color1.g + color2.g) / 3);
		color.b = (byte)((color0.b + color1.b + color2.b) / 3);
		color.a = 255;
		context.averagedColor = color;

		float3 a = context.verts[i0];
		float3 b = context.verts[i1];
		float3 c = context.verts[i2];
		float3 middle = (a + b + c) / 3f;
		float3 normal = normalize(cross(b - a, c - a));

		float3 minf = min(a, min(b, c));
		float3 maxf = max(a, max(b, c));

		int3 maxDimensions = context.maxDimensions;
		int3 mini = clamp(int3(floor(minf)), 0, maxDimensions);
		int3 maxi = clamp(int3(ceil(maxf)), 0, maxDimensions);

		int written = 0;
		VoxelizedPosition* positions = context.positions;
		int positionsLength = context.positionLength;

		for (int x = mini.x; x <= maxi.x; x++) {
			for (int z = mini.z; z <= maxi.z; z++) {
				for (int y = mini.y; y <= maxi.y; y++) {
					float planeDistance = dot(float3(x, y, z) - middle, normal);
					if (planeDistance <= 1f) {
						int idx = x * (maxDimensions.z + 1) + z;
						positions[written++] = new VoxelizedPosition
						{
							XZIndex = idx,
							Y = (short)y
						};

						if (written == positionsLength) {
							goto END;
						}
					}
				}
			}
		}
		END:
		context.writtenVoxelCount = written;
	}

	public struct VoxelizedPosition
	{
		public int XZIndex;
		public short Y;
	}

	public unsafe struct GetVoxelsContext
	{
		public int3 maxDimensions;
		public VoxelizedPosition* positions;
		public int positionLength;
		public int writtenVoxelCount;
		public ColorARGB32 averagedColor;

		public float3* verts;
		public Color32* colors;
		public int* indices;
	}
}
