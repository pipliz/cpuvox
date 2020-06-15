using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

[BurstCompile]
public unsafe class SimpleMesh : IDisposable
{
	public Vertex* Vertices;
	public int* Indices;

	public int VertexCount;
	public int IndexCount;

	public MaterialLib Materials;

	/// <summary>
	/// RAM pointer setup
	/// </summary>
	public SimpleMesh (MaterialLib materials, Vertex* vertices, int* indices, int vertexCount, int indexCount)
	{
		Materials = materials;
		Vertices = vertices;
		Indices = indices;
		VertexCount = vertexCount;
		IndexCount = indexCount;
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
	}

	public unsafe int3 Rescale (float maxDimension, float3 dimensionFlips)
	{
		int3 result = default;
		RemapInvoker(Vertices, VertexCount, maxDimension, ref dimensionFlips, ref result);
		return result;
	}

	unsafe delegate void ExecuteDelegate (Vertex* vertices, int vertexCount, float maxDimension, ref float3 dimensionFlips, ref int3 result);
	unsafe static readonly ExecuteDelegate RemapInvoker = BurstCompiler.CompileFunctionPointer<ExecuteDelegate>(Remap_Internal).Invoke;

	/// <summary>
	/// Rescales+repositions the mesh to fill the world from 0 ... maxDimension
	/// </summary>
	[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
	[AOT.MonoPInvokeCallback(typeof(ExecuteDelegate))]
	static unsafe void Remap_Internal (Vertex* vertices, int vertexCount, float maxDimension, ref float3 dimensionFlips, ref int3 result)
	{
		float3 minimum = vertices[0].Position;
		float3 maximum = vertices[0].Position;
		for (int i = 1; i < vertexCount; i++) {
			float3 v = vertices[i].Position;
			minimum = min(v, minimum);
			maximum = max(v, maximum);
		}

		float3 size = maximum - minimum;
		float scale = maxDimension / cmax(size);

		result = new int3(
			Mathf.NextPowerOfTwo((int)(size.x * scale)),
			Mathf.NextPowerOfTwo((int)(size.y * scale)),
			Mathf.NextPowerOfTwo((int)(size.z * scale))
		);

		for (int i = 0; i < vertexCount; i++) {
			(vertices + i)->Position = (vertices[i].Position - minimum) * scale;
		}

		float3 flipScales = result;
		if (dimensionFlips.x < 1f) {
			for (int i = 0; i < vertexCount; i++) {
				float3* v = &(vertices + i)->Position;
				v->x = flipScales.x - v->x;
			}
		}
		if (dimensionFlips.y < 1f) {
			for (int i = 0; i < vertexCount; i++) {
				float3* v = &(vertices + i)->Position;
				v->y = flipScales.y - v->y;
			}
		}
		if (dimensionFlips.z < 1f) {
			for (int i = 0; i < vertexCount; i++) {
				float3* v = &(vertices + i)->Position;
				v->z = flipScales.z - v->z;
			}
		}
	}

	public struct Vertex
	{
		public float3 Position;
		public Color32 Color;
		public float2 UV;
		public int MaterialIndex;
	}

	public class Material
	{
		public string Name;
		public int MaterialIndex;

		Color32[] DiffuseTexture;
		int2 DiffuseTextureSize;

		public void SetDiffuse (Texture2D texture)
		{
			DiffuseTexture = texture.GetPixels32();
			DiffuseTextureSize = int2(texture.width, texture.height);
		}

		public Color GetDiffusePixel (float2 uv)
		{
			int2 pixel = int2(floor(uv * (DiffuseTextureSize - 1)));
			return DiffuseTexture[pixel.x + pixel.y * DiffuseTextureSize.x];
		}
	}

	public class MaterialLib
	{
		public List<Material> Materials;

		public Material GetByName (string name)
		{
			for (int i = 0; i < Materials.Count; i++) {
				if (Materials[i].Name == name) {
					return Materials[i];
				}
			}
			return null;
		}

		public static MaterialLib ParseFromObj (string objPath, string relativeFilePath)
		{
			MaterialLib result = new MaterialLib();

			string libPath = System.IO.Path.Combine(new System.IO.FileInfo(objPath).Directory.FullName, relativeFilePath);
			Debug.Log($"Loading mtllib at {libPath}");

			result.Materials = new List<Material>();
			Material tempMaterial = default;
			using (var file = new System.IO.FileStream(libPath, System.IO.FileMode.Open, System.IO.FileAccess.Read)) {
				using (var text = new System.IO.StreamReader(file)) {
					while (true) {
						if (text.EndOfStream) {
							break;
						}
						string line = text.ReadLine();
						if (line == null || line.Length == 0) { continue; }

						if (line.StartsWith("#")) { continue; }
						if (line.StartsWith("newmtl ")) {
							tempMaterial = new Material();
							tempMaterial.MaterialIndex = result.Materials.Count;
							tempMaterial.Name = line.Substring("newmtl ".Length);
							result.Materials.Add(tempMaterial);
						} else if (line.StartsWith("Ns ")) {
							// specular exponent
						} else if (line.StartsWith("Ka ")) {
							//int index = 2;
							//tempMaterial.Ambient = new Color(ParseFloat(line, ref index), ParseFloat(line, ref index), ParseFloat(line, ref index), 1f);
						} else if (line.StartsWith("Kd ")) {
							//int index = 2;
							//tempMaterial.Diffuse = new Color(ParseFloat(line, ref index), ParseFloat(line, ref index), ParseFloat(line, ref index), 1f);
						} else if (line.StartsWith("Ks ")) {
							// specular color
						} else if (line.StartsWith("Ke ")) {
							// emissive color
						} else if (line.StartsWith("Ni ")) {
							// index of refraction
						} else if (line.StartsWith("d ")) {
							// transparency
						} else if (line.StartsWith("illum ")) {
							// illumination mode (only support ambient & color)
						} else if (line.StartsWith("map_Kd ")) {
							int idx = "map_Kd ".Length;
							if (line[idx] == '-') {
								if (line[idx+1] == 'b' && line[idx+2] == 'm') {
									idx += 4;
									while (line[idx] != ' ') {
										idx++; // skip the -bm {x}
									}
									idx++; // set it to first of path
								}
							}

							string relativeMapPath = line.Substring(idx);
							string imagePath = System.IO.Path.Combine(new System.IO.FileInfo(libPath).Directory.FullName, relativeMapPath);
							Texture2D tex = new Texture2D(1, 1);
							byte[] imageBytes = System.IO.File.ReadAllBytes(imagePath);
							tex.LoadImage(imageBytes, false);
							tempMaterial.SetDiffuse(tex);
							UnityEngine.Object.Destroy(tex);
							Debug.Log($"Loaded img file {relativeMapPath} for material {tempMaterial.Name}");
						}
					}
				}
			}
			return result;
		}
	}
}
