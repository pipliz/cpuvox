using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public class VoxelizerHelper
{
	delegate void ExecuteDelegate (ref GetVoxelsContext context, int indexStart);
	static readonly ExecuteDelegate GetVoxelsInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(GetVoxelsInternal).Invoke;

	public static void Initialize ()
	{
		// runs static constructor (threadsafe requiremnet)
		return;
	}

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

		Color color2 = context.colors[i0];
		Color color0 = context.colors[i1];
		Color color1 = context.colors[i2];

		float3 a = context.verts[i0];
		float3 b = context.verts[i1];
		float3 c = context.verts[i2];
		float3 middle = (a + b + c) / 3f;
		float3 normalTri = normalize(cross(b - a, c - a));
		a += normalize(a - middle) * 0.5f;
		b += normalize(b - middle) * 0.5f;
		c += normalize(c - middle) * 0.5f;

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
					float3 voxel = float3(x, y, z) + 0.5f;
					float normalDistToTriangle = dot(voxel - a, normalTri);
					if (abs(normalDistToTriangle) > 0.5f) {
						continue;
					}
					float3 p = voxel - normalTri * normalDistToTriangle;


					float3 v0 = b - a;
					float3 v1 = c - a;
					float3 v2 = p - a;
					float d00 = dot(v0, v0);
					float d01 = dot(v0, v1);
					float d11 = dot(v1, v1);
					float d20 = dot(v2, v0);
					float d21 = dot(v2, v1);
					float denom = 1f / (d00 * d11 - d01 * d01);
					float3 barry;
					barry.x = (d11 * d20 - d01 * d21) * denom;
					barry.y = (d00 * d21 - d01 * d20) * denom;
					barry.z = 1.0f - barry.x - barry.y;
					if (any(barry < 0 | barry > 1)) {
						continue;
					}

					Color color;
					color.r = (color0.r * barry.x + color1.r * barry.y + color2.r * barry.z);
					color.g = (color0.g * barry.x + color1.g * barry.y + color2.g * barry.z);
					color.b = (color0.b * barry.x + color1.b * barry.y + color2.b * barry.z);
					color.a = 1f;

					int idx = x * (maxDimensions.z + 1) + z;
					positions[written++] = new VoxelizedPosition
					{
						XZIndex = idx,
						Y = (short)y,
						Color = color
					};

					if (written == positionsLength) {
						goto END;
					}
				}
			}
		}
		END:
		context.writtenVoxelCount = written;
	}

	public struct VoxelizedPosition
	{
		public Color32 Color;
		public int XZIndex;
		public short Y;
	}

	public unsafe struct GetVoxelsContext
	{
		public int3 maxDimensions;
		public VoxelizedPosition* positions;
		public int positionLength;
		public int writtenVoxelCount;

		public float3* verts;
		public Color32* colors;
		public int* indices;
	}
}
