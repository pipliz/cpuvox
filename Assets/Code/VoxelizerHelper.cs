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
		// runs static constructor (threadsafe requirement)
		return;
	}

	public unsafe static void GetVoxels (ref GetVoxelsContext context, int indexStart)
	{
		GetVoxelsInvoker(ref context, indexStart);
	}

	/// <summary>
	/// Takes a triangle starting at tri idx {indexStart} and voxelizes it, writing every voxel into {context.positions}
	/// </summary>
	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	[AOT.MonoPInvokeCallback(typeof(ExecuteDelegate))]
	static unsafe void GetVoxelsInternal (ref GetVoxelsContext context, int indexStart)
	{
		int i0 = context.indices[indexStart];
		int i1 = context.indices[indexStart + 1];
		int i2 = context.indices[indexStart + 2];

		SimpleMesh.Vertex v0 = context.verts[i0];
		SimpleMesh.Vertex v1 = context.verts[i1];
		SimpleMesh.Vertex v2 = context.verts[i2];

		float3 a = v0.Position;
		float3 b = v1.Position;
		float3 c = v2.Position;

		float3 normalTri;
		{
			float3 normalCross = cross(b - a, c - a);
			float normalCrossLengthSqrd = dot(normalCross, normalCross);
			if (normalCrossLengthSqrd == 0f) {
				return;
			}
			normalTri = normalCross * rcp(sqrt(normalCrossLengthSqrd));
		}

		// extend every tri by half a voxel by just extending it along the corner-middle dir.
		// it's a naive attempt to get a conservative rasterizer - works good enough in general
		float3 middle = (a + b + c) / 3f;
		a += normalize(a - middle) * 0.5f;
		b += normalize(b - middle) * 0.5f;
		c += normalize(c - middle) * 0.5f;

		// get the AABB
		float3 minf = min(a, min(b, c));
		float3 maxf = max(a, max(b, c));
		int3 maxDimensions = context.maxDimensions;
		int3 mini = clamp(int3(floor(minf)), 0, maxDimensions);
		int3 maxi = clamp(int3(ceil(maxf)), 0, maxDimensions);

		int written = 0;
		VoxelizedPosition* positions = context.positions;
		int positionsLength = context.positionLength;

		Color color0 = v0.Color;
		Color color1 = v1.Color;
		Color color2 = v2.Color;

		for (int x = mini.x; x <= maxi.x; x++) {
			for (int z = mini.z; z <= maxi.z; z++) {
				for (int y = mini.y; y <= maxi.y; y++) {
					float3 voxel = float3(x, y, z) + 0.5f;
					float normalDistToTriangle = dot(voxel - a, normalTri);
					if (abs(normalDistToTriangle) > 0.5f) {
						continue; // distance to the triangle plane is over half a voxel -> the other side will have a voxel instead if we're at 0.5-1.0 dist
					}

					// set up barycentric coordinates
					float3 p = voxel - normalTri * normalDistToTriangle;
					float3 p0 = b - a;
					float3 p1 = c - a;
					float3 p2 = p - a;
					float d00 = dot(p0, p0);
					float d01 = dot(p0, p1);
					float d11 = dot(p1, p1);
					float d20 = dot(p2, p0);
					float d21 = dot(p2, p1);
					float denom = 1f / (d00 * d11 - d01 * d01);
					float3 barry;
					barry.y = (d11 * d20 - d01 * d21) * denom; // v1
					barry.z = (d00 * d21 - d01 * d20) * denom; // v2
					barry.x = 1.0f - barry.y - barry.z; // v0

					if (any(barry < 0 | barry > 1)) {
						continue; // we're on the triangle plane, but outside the triangle
					}

					// interpolate vertex colors with the barycentric coordinates
					Color color;
					color.r = (color0.r * barry.x + color1.r * barry.y + color2.r * barry.z);
					color.g = (color0.g * barry.x + color1.g * barry.y + color2.g * barry.z);
					color.b = (color0.b * barry.x + color1.b * barry.y + color2.b * barry.z);
					color.a = 1f;

					float2 uv;
					uv.x = (v0.UV.x * barry.x + v1.UV.x * barry.y + v2.UV.x * barry.z);
					uv.y = (v0.UV.y * barry.x + v1.UV.y * barry.y + v2.UV.y * barry.z);

					int idx = x * (maxDimensions.z + 1) + z;
					positions[written++] = new VoxelizedPosition
					{
						XZIndex = idx,
						Y = (short)y,
						Color = color,
						MaterialIndex = (sbyte)v0.MaterialIndex,
						UV = uv
					};

					if (written == positionsLength) {
						goto END; // buffer full, our triangle must've been huge
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
		public float2 UV;
		public short Y;
		public sbyte MaterialIndex;
	}

	public unsafe struct GetVoxelsContext
	{
		public int3 maxDimensions;
		public VoxelizedPosition* positions;
		public int positionLength;
		public int writtenVoxelCount;

		public SimpleMesh.Vertex* verts;
		public int* indices;
	}
}
