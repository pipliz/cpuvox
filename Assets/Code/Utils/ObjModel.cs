using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public static class ObjModel
{
	public static SimpleMesh Import (string path)
	{
		long fileByteSize = new System.IO.FileInfo(path).Length;

		int verticesEstimate = (int)(fileByteSize / 116);
		int indexEstimate = (int)(fileByteSize / 58);
		NativeArrayList<float3> verticesList = new NativeArrayList<float3>(verticesEstimate, Unity.Collections.Allocator.Persistent);
		NativeArrayList<Color32> colorsList = new NativeArrayList<Color32>(verticesEstimate, Unity.Collections.Allocator.Persistent);
		NativeArrayList<int> indicesList = new NativeArrayList<int>(indexEstimate, Unity.Collections.Allocator.Persistent);

		Vector3 minimum = Vector3.positiveInfinity;
		Vector3 maximum = Vector3.negativeInfinity;

		Profiler.BeginSample("Read file");
		using (var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read)) {
			using (var text = new System.IO.StreamReader(file)) {
				while (true) {
					if (text.EndOfStream) {
						break;
					}
					string line = text.ReadLine();
					if (line == null || line.Length == 0) { continue; }

					int index = 1;
					// start of a line
					switch (line[0]) {
						case 'v':
							ParseVertexLine();
							break;
						case 'f':
							ParseFaceLine();
							break;
					}
					continue;

					void ParseVertexLine ()
					{
						Vector3 vertex = new Vector3(ParseFloat(), ParseFloat(), ParseFloat());
						minimum = Vector3.Min(minimum, vertex);
						maximum = Vector3.Max(maximum, vertex);
						verticesList.Add(vertex);
						Color color = new Color(ParseFloat(), ParseFloat(), ParseFloat());
						colorsList.Add(color);
					}

					void ParseFaceLine ()
					{
						index++; // skip space after 'f'
						indicesList.Add(ParseNumber(out int a, out int sa) - 1);
						index++;
						indicesList.Add(ParseNumber(out int b, out int sb) - 1);
						index++;
						indicesList.Add(ParseNumber(out int c, out int sc) - 1);
					}

					float ParseFloat ()
					{
						index++; // every float parsed has a space before it
						float beforePeriod = ParseNumber(out int beforePeriodScale, out int beforeSign);
						index++; // skip '.'
						float afterPeriod = ParseNumber(out int afterPeriodScale, out int afterSign);
						// text[index] is space or EOL
						return beforeSign * (beforePeriod + (afterPeriod / afterPeriodScale));
					}

					int ParseNumber (out int numberScale, out int sign)
					{
						int result = 0;
						sign = 1;
						numberScale = 1;

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
						return result;
					}
				}
			}
		}
		Profiler.EndSample();
		int vertexCount = verticesList.Count;
		int indexCount = indicesList.Count;

		unsafe {
			float3* vertices = (float3*)SimpleMesh.MallocHelper<float3>(verticesList.Count);
			UnsafeUtility.MemCpy(vertices, verticesList.Array.GetUnsafePtr(), UnsafeUtility.SizeOf<float3>() * verticesList.Count);
			verticesList.Dispose();

			Color32* colors = (Color32*)SimpleMesh.MallocHelper<Color32>(colorsList.Count);
			UnsafeUtility.MemCpy(colors, colorsList.Array.GetUnsafePtr(), UnsafeUtility.SizeOf<Color32>() * colorsList.Count);
			colorsList.Dispose();

			int* indices = (int*)SimpleMesh.MallocHelper<int>(indicesList.Count);
			UnsafeUtility.MemCpy(indices, indicesList.Array.GetUnsafePtr(), UnsafeUtility.SizeOf<int>() * indicesList.Count);
			indicesList.Dispose();

			return new SimpleMesh(vertices, indices, colors, vertexCount, indexCount);
		}

	}
}
