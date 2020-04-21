using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public class SimpleMesh : IDisposable
{
	public NativeArrayList<float3> Vertices;
	public NativeArrayList<int> Indices;
	public NativeArrayList<Color32> VertexColors;

	public SimpleMesh (NativeArrayList<float3> vertices, NativeArrayList<int> indices, NativeArrayList<Color32> vertexColors)
	{
		Vertices = vertices;
		Indices = indices;
		VertexColors = vertexColors;
	}

	public void Dispose ()
	{
		Vertices.Dispose();
		Indices.Dispose();
		VertexColors.Dispose();
	}

	public unsafe void Remap (Vector3 oldMin, float scale)
	{
		float3 oldMinf3 = oldMin;
		Remap_Internal((float3*)Vertices.Array.GetUnsafePtr(), Vertices.Count, ref oldMinf3, scale);
	}

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	static unsafe void Remap_Internal (float3* Vertices, int count, ref float3 oldMin, float scale)
	{
		for (int i = 0; i < count; i++) {
			Vertices[i] = (Vertices[i] - oldMin) * scale;
		}
	}
}
