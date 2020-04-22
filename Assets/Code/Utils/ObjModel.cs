﻿using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public static class ObjModel
{
	public static SimpleMesh Import (string path, float maxDimensionSize, out Vector3Int dimensions)
	{
		NativeArrayList<float3> vertices = new NativeArrayList<float3>(128 * 1024, Unity.Collections.Allocator.Persistent);
		NativeArrayList<Color32> colors = new NativeArrayList<Color32>(128 * 1024, Unity.Collections.Allocator.Persistent);
		NativeArrayList<int> indices = new NativeArrayList<int>(128 * 1024, Unity.Collections.Allocator.Persistent);

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
						vertices.Add(vertex);
						Color color = new Color(ParseFloat(), ParseFloat(), ParseFloat());
						colors.Add(color);
					}

					void ParseFaceLine ()
					{
						index++; // skip space after 'f'
						indices.Add(ParseNumber(out int a, out int sa) - 1);
						index++;
						indices.Add(ParseNumber(out int b, out int sb) - 1);
						index++;
						indices.Add(ParseNumber(out int c, out int sc) - 1);
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

		Profiler.BeginSample("Auto scale & position mesh");
		SimpleMesh mesh = new SimpleMesh(vertices, indices, colors);
		Bounds bounds = new Bounds();
		bounds.SetMinMax(minimum, maximum);
		Vector3 size = bounds.size;
		float scale = maxDimensionSize / Mathf.Max(size.x, size.y, size.z);
		mesh.Remap(bounds.min, scale);

		dimensions = new Vector3Int(
			Mathf.NextPowerOfTwo((int)(size.x * scale)),
			Mathf.NextPowerOfTwo((int)(size.y * scale)),
			Mathf.NextPowerOfTwo((int)(size.z * scale))
		);

		Profiler.EndSample();
		Debug.Log($"Rescaled/positioned mesh from {bounds} to dimensions {dimensions}");
		return mesh;
	}
}
