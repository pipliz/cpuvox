using System;
using System.IO.MemoryMappedFiles;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public unsafe class SimpleMesh : IDisposable
{
	public float3* Vertices;
	public int* Indices;
	public Color32* VertexColors;

	public int VertexCount;
	public int IndexCount;

	public SimpleMesh (float3* vertices, int* indices, Color32* vertexColors, int vertexCount, int indexCount)
	{
		Vertices = vertices;
		Indices = indices;
		VertexColors = vertexColors;
		VertexCount = vertexCount;
		IndexCount = indexCount;
	}

	public SimpleMesh (string datPath)
	{
		long fileSize = new System.IO.FileInfo(datPath).Length;
		using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(datPath, System.IO.FileMode.Open, null, fileSize)) {
			using (MemoryMappedViewAccessor viewAccessor = file.CreateViewAccessor()) {
				VertexCount = viewAccessor.ReadInt32(0);
				IndexCount = viewAccessor.ReadInt32(4);

				byte* ptr = (byte*)0;
				viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

				try {
					int positionBytes = VertexCount * UnsafeUtility.SizeOf<float3>();
					int colorBytes = VertexCount * UnsafeUtility.SizeOf<Color32>();
					int indexBytes = IndexCount * UnsafeUtility.SizeOf<int>();
					int headerSize = 8;

					Vertices = (float3*)MallocHelper<float3>(VertexCount);
					VertexColors = (Color32*)MallocHelper<Color32>(VertexCount);
					Indices = (int*)MallocHelper<int>(IndexCount);

					ptr += headerSize;
					UnsafeUtility.MemCpy(Vertices, ptr, positionBytes);
					ptr += positionBytes;
					UnsafeUtility.MemCpy(VertexColors, ptr, colorBytes);
					ptr += colorBytes;
					UnsafeUtility.MemCpy(Indices, ptr, indexBytes);
				} finally {
					viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
				}
			}

		}
	}

	public void Serialize (string filePath)
	{
		int positionBytes = VertexCount * UnsafeUtility.SizeOf<float3>();
		int colorBytes = VertexCount * UnsafeUtility.SizeOf<Color32>();
		int indexBytes = IndexCount * UnsafeUtility.SizeOf<int>();
		int meshBytes = positionBytes + colorBytes + indexBytes;
		int headerSize = 8;

		using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(filePath, System.IO.FileMode.Create, null, meshBytes + headerSize)) {
			using (MemoryMappedViewAccessor viewAccessor = file.CreateViewAccessor()) {
				viewAccessor.Write(0, VertexCount);
				viewAccessor.Write(4, IndexCount);

				byte* ptr = (byte*)0;

				viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
				try {
					ptr += headerSize;
					UnsafeUtility.MemCpy(ptr, Vertices, positionBytes);
					ptr += positionBytes;
					UnsafeUtility.MemCpy(ptr, VertexColors, colorBytes);
					ptr += colorBytes;
					UnsafeUtility.MemCpy(ptr, Indices, indexBytes);
				} finally {
					viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
				}
			}
		}
	}

	public unsafe static void* MallocHelper<T> (int count) where T : struct
	{
		return UnsafeUtility.Malloc(count * UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), Allocator.Persistent);
	}

	public unsafe static void FreeHelper (void* ptr)
	{
		UnsafeUtility.Free(ptr, Allocator.Persistent);
	}

	public void Dispose ()
	{
		FreeHelper(Vertices);
		FreeHelper(Indices);
		FreeHelper(VertexColors);
	}

	public unsafe int3 Rescale (float maxDimension)
	{
		int3 result = default;
		RemapInvoker(Vertices, VertexCount, maxDimension, ref result);
		return result;
	}

	unsafe delegate void ExecuteDelegate (float3* vertices, int vertexCount, float maxDimension, ref int3 result);
	unsafe static readonly ExecuteDelegate RemapInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(Remap_Internal).Invoke;

	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	[AOT.MonoPInvokeCallback(typeof(ExecuteDelegate))]
	static unsafe void Remap_Internal (float3* vertices, int vertexCount, float maxDimension, ref int3 result)
	{
		float3 minimum = vertices[0];
		float3 maximum = vertices[0];
		for (int i = 1; i < vertexCount; i++) {
			Vector3 v = vertices[i];
			minimum = min(v, minimum);
			maximum = max(v, maximum);
		}

		float3 size = maximum - minimum;
		float scale = maxDimension / cmax(size);

		for (int i = 0; i < vertexCount; i++) {
			vertices[i] = (vertices[i] - minimum) * scale;
		}

		result = new int3(
			Mathf.NextPowerOfTwo((int)(size.x * scale)),
			Mathf.NextPowerOfTwo((int)(size.y * scale)),
			Mathf.NextPowerOfTwo((int)(size.z * scale))
		);
	}
}
