using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public static class ObjModel
{
	public static SimpleMesh Import (string path, float maxDimensionSize, out Vector3Int dimensions)
	{
		string text = System.IO.File.ReadAllText(path);

		NativeArrayList<float3> vertices = new NativeArrayList<float3>(1024, Unity.Collections.Allocator.Persistent);
		NativeArrayList<Color32> colors = new NativeArrayList<Color32>(1024, Unity.Collections.Allocator.Persistent);
		NativeArrayList<int> indices = new NativeArrayList<int>(1024, Unity.Collections.Allocator.Persistent);

		Profiler.BeginSample("Read file");
		int index = 0;
		while (index < text.Length) {
			// start of a line
			switch (text[index++]) {
				case '#':
					SkipToLineStart();
					break;
				case 'v':
					ParseVertexLine();
					break;
				case 'f':
					ParseFaceLine();
					break;
			}
		}

		void SkipToLineStart ()
		{
			while (index < text.Length) {
				switch (text[index++]) {
					case '\r': // check if followed by \n
						if (index + 1 < text.Length && text[index + 1] == '\n') {
							index++;
						}
						return;
					case '\n':
						return;
				}
			}
		}

		void ParseVertexLine ()
		{
			Vector3 vertex = new Vector3(ParseFloat(), ParseFloat(), ParseFloat());
			vertices.Add(vertex);
			Color color = new Color(ParseFloat(), ParseFloat(), ParseFloat());
			colors.Add(color);

			SkipToLineStart();
		}

		void ParseFaceLine ()
		{
			index++; // skip space after 'f'
			indices.Add(ParseNumber(out int a, out int sa) - 1);
			index++;
			indices.Add(ParseNumber(out int b, out int sb) - 1);
			index++;
			indices.Add(ParseNumber(out int c, out int sc) - 1);
			SkipToLineStart();
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

			char c = text[index];
			if (c == '-') {
				sign = -1;
				c = text[++index];
			}

			while (c >= '0' && c <= '9') {
				result = result * 10 + (c - '0');
				numberScale *= 10;
				c = text[++index];
			}

			// text[index] is something non-digit
			return result;
		}

		Profiler.BeginSample("Auto scale & position mesh");
		SimpleMesh mesh = new SimpleMesh(vertices, indices, colors);
		Bounds bounds = mesh.CalculateBounds();
		Vector3 size = bounds.size;
		float scale = maxDimensionSize / Mathf.Max(size.x, size.y, size.z);
		Vector3 newSize = size * scale;
		mesh.Remap(bounds.min, scale);
		Bounds newBounds = mesh.CalculateBounds();
		dimensions = new Vector3Int(
			Mathf.NextPowerOfTwo((int)newBounds.size.x),
			Mathf.NextPowerOfTwo((int)newBounds.size.y),
			Mathf.NextPowerOfTwo((int)newBounds.size.z)
		);
		Profiler.EndSample();
		Debug.Log($"Rescaled/positioned mesh from {bounds} to {newBounds}, dimensions {dimensions}");
		return mesh;
	}
}
