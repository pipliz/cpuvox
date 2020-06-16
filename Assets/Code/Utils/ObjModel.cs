using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public static class ObjModel
{
	public static SimpleMesh Import (string path, bool swapYZ)
	{
		long fileByteSize = new System.IO.FileInfo(path).Length;

		// would use Allocator.Temp, but that doesn't clean up on re-allocation; so total RAM balloons
		NativeArrayList<float3> positionsLUT = new NativeArrayList<float3>(1024 * 64, Allocator.Persistent);
		NativeArrayList<Color32> colorsLUT = new NativeArrayList<Color32>(1024 * 64, Allocator.Persistent);
		NativeArrayList<float2> uvLookupTable = new NativeArrayList<float2>(1024 * 64, Allocator.Persistent);

		NativeArrayList<SimpleMesh.Vertex> vertexResult = new NativeArrayList<SimpleMesh.Vertex>(1024 * 64, Allocator.Persistent);

		SimpleMesh.MaterialLib activeMaterialLib = default;
		SimpleMesh.Material activeMaterial = default;
		char[] splits = new char[] { ' ' };

		Profiler.BeginSample("Read file");
		using (var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read)) {
			using (var text = new System.IO.StreamReader(file)) {
				while (true) {
					if (text.EndOfStream) {
						break;
					}
					string line = text.ReadLine();
					if (line == null || line.Length == 0) { continue; }

					// start of a line
					if (line.StartsWith("v ")) {
						ParsePositionLine();
					} else if (line.StartsWith("f ")) {
						ParseFaceLine();
					} else if (line.StartsWith("vt ")) {
						ParseUVLine();
					} else if (line.StartsWith("vn ")) {
						// 
					} else if (line.StartsWith("mtllib ")) {
						activeMaterialLib = SimpleMesh.MaterialLib.ParseFromObj(path, line.Substring("mtllib ".Length));
					} else if (line.StartsWith("o ")) {
						//
					} else if (line.StartsWith("usemtl ")) {
						activeMaterial = activeMaterialLib.GetByName(line.Substring("usemtl ".Length));
					}
					continue;

					void ParseUVLine ()
					{
						string[] subs = line.Split(splits, System.StringSplitOptions.RemoveEmptyEntries);
						float2 uv = new float2(ParseFloat(subs[1]), ParseFloat(subs[2]));
						uvLookupTable.Add(uv);
					}

					void ParsePositionLine ()
					{
						string[] subs = line.Split(splits, System.StringSplitOptions.RemoveEmptyEntries);
						float3 pos = new float3(ParseFloat(subs[1]), ParseFloat(subs[2]), ParseFloat(subs[3]));
						if (swapYZ) {
							float t = pos.z;
							pos.z = pos.y;
							pos.y = t;
						}
						positionsLUT.Add(pos);
						Color color;
						if (subs.Length > 6) {
							color.r = ParseFloat(subs[4]);
							color.g = ParseFloat(subs[5]);
							color.b = ParseFloat(subs[6]);
							color.a = 1f;
						} else {
							color = Color.white;
						}
						colorsLUT.Add(color);
					}

					float ParseFloat (string str)
					{
						return float.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
					}

					void ParseFaceLine ()
					{
						int index = 2;
						int entriesPerIndex = 1;
						for (int i = index; i < line.Length && line[i] != ' '; i++) {
							if (line[i] == '/') {
								entriesPerIndex++;
							}
						}

						switch (entriesPerIndex) {
							case 1: // f v1 v2 v3
								for (int i = 0; i < 3; i++) {
									vertexResult.Add(GatherVertex(ParseFaceIndex(line, ref index), -1, -1));
									index++;
								}
								break;
							case 2: // f v1/vt1 v2/vt2 v3/vt3
								for (int i = 0; i < 3; i++) {
									int v = ParseFaceIndex(line, ref index);
									index++; // skip /
									int vt = ParseFaceIndex(line, ref index);
									index++; // skip space between the 3 indices

									vertexResult.Add(GatherVertex(v, vt, -1));
								}
								break;
							case 3:
								// f v1/vt1/vn1 ..
								// f v1//vn1 ..
								for (int i = 0; i < 3; i++) {
									int v = ParseFaceIndex(line, ref index);
									index++; // skip /
									int vt = -1;
									if (line[index] != '/') {
										// vt1
										vt = ParseFaceIndex(line, ref index);
									}
									index++; // skip second /
									int vn = ParseFaceIndex(line, ref index);
									index++; // skip space between the 3 indices

									vertexResult.Add(GatherVertex(v, vt, vn));
								}
								break;
						}
					}

					SimpleMesh.Vertex GatherVertex (int positionIndex, int textureIndex, int normalIndex)
					{
						SimpleMesh.Vertex vertex = default;
						vertex.Color = colorsLUT[positionIndex];
						vertex.Position = positionsLUT[positionIndex];
						if (textureIndex >= 0) {
							vertex.UV = uvLookupTable[textureIndex];
						}

						vertex.MaterialIndex = activeMaterial?.MaterialIndex ?? -1;
						return vertex;
					}
				}
			}
		}

		positionsLUT.Dispose();
		uvLookupTable.Dispose();
		colorsLUT.Dispose();

		Profiler.EndSample();

		int vertexCount = vertexResult.Count;

		unsafe {
			SimpleMesh.Vertex* vertices = (SimpleMesh.Vertex*)SimpleMesh.MallocHelper<SimpleMesh.Vertex>(vertexCount);
			UnsafeUtility.MemCpy(vertices, vertexResult.Array.GetUnsafePtr(), UnsafeUtility.SizeOf<SimpleMesh.Vertex>() * vertexCount);
			vertexResult.Dispose();

			int* indices = (int*)SimpleMesh.MallocHelper<int>(vertexCount);
			for (int i = 0; i < vertexCount; i++) {
				indices[i] = i;
			}

			return new SimpleMesh(activeMaterialLib, vertices, indices, vertexCount, vertexCount);
		}
	}

	static int ParseFaceIndex (string line, ref int index)
	{
		int result = 0;
		int sign = 1;
		int numberScale = 1;

		char c = line[index];
		if (c == '-') {
			sign = -1;
			c = line[++index];
		}

		while (c >= '0' && c <= '9') {
			result = result * 10 + (c - '0');
			numberScale *= 10;
			if (++index == line.Length) {
				break;
			}
			c = line[index];
		}

		// text[index] is something non-digit
		return result * sign - 1;
	}
}
