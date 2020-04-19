using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

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
		((IDisposable)Vertices).Dispose();
		((IDisposable)Indices).Dispose();
		((IDisposable)VertexColors).Dispose();
	}

	public void Remap (Vector3 oldMin, float scale)
	{
		RemapJob job = new RemapJob();
		job.Vertices = Vertices.Array;
		job.Count = Vertices.Count;
		job.oldMin = oldMin;
		job.scale = scale;
		job.Schedule().Complete();
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct RemapJob : IJob
	{
		public NativeArray<float3> Vertices;
		public int Count;

		public float3 oldMin;
		public float scale;

		public void Execute ()
		{
			for (int i = 0; i < Count; i++) {
				Vertices[i] = (Vertices[i] - oldMin) * scale;
			}
		}
	}
}
