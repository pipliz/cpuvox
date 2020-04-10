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

	public Bounds CalculateBounds ()
	{
		BoundsJob job = new BoundsJob();
		job.Vertices = Vertices;
		job.Result = new NativeArray<Bounds>(1, Allocator.TempJob);
		job.Schedule().Complete();
		Bounds result = job.Result[0];
		job.Result.Dispose();
		return result;
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
		job.Vertices = Vertices;
		job.oldMin = oldMin;
		job.scale = scale;
		job.Schedule().Complete();
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct BoundsJob : IJob
	{
		[ReadOnly]
		public NativeArrayList<float3> Vertices;

		[WriteOnly]
		public NativeArray<Bounds> Result;

		public void Execute ()
		{
			float3 posMin = float.PositiveInfinity;
			float3 posMax = float.NegativeInfinity;

			for (int i = 0; i < Vertices.Count; i++) {
				float3 vertex = Vertices[i];
				posMin = min(vertex, posMin);
				posMax = max(vertex, posMax);
			}

			Bounds b = new Bounds();
			b.SetMinMax(posMin, posMax);
			Result[0] = b;
		}
	}

	[BurstCompile(FloatMode = FloatMode.Fast)]
	struct RemapJob : IJob
	{
		public NativeArrayList<float3> Vertices;
		public float3 oldMin;
		public float scale;

		public void Execute ()
		{
			for (int i = 0; i < Vertices.Count; i++) {
				Vertices[i] = (Vertices[i] - oldMin) * scale;
			}
		}
	}
}
