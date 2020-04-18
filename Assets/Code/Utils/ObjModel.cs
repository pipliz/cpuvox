using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

public static class ObjModel
{
	public static SimpleMesh Import (string path, float maxDimensionSize, out Vector3Int dimensions)
	{
		using (var file = new System.IO.StreamReader(path)) {
			NativeArrayList<float3> vertices = new NativeArrayList<float3>(1024, Unity.Collections.Allocator.Persistent);
			NativeArrayList<Color32> colors = new NativeArrayList<Color32>(1024, Unity.Collections.Allocator.Persistent);
			NativeArrayList<int> indices = new NativeArrayList<int>(1024, Unity.Collections.Allocator.Persistent);

			var numberFormat = System.Globalization.CultureInfo.InvariantCulture.NumberFormat;

			Profiler.BeginSample("Read file");
			while (!file.EndOfStream) {
				string line = file.ReadLine();
				if (line.StartsWith("#")) {
					continue;
				} else if (line.StartsWith("v")) {
					string[] splits = line.Split(' ');
					Vector3 v = new Vector3(
						float.Parse(splits[1], numberFormat),
						float.Parse(splits[2], numberFormat),
						float.Parse(splits[3], numberFormat)
					);
					Color col = new Color(
						float.Parse(splits[4], numberFormat),
						float.Parse(splits[5], numberFormat),
						float.Parse(splits[6], numberFormat)
					);

					vertices.Add(v);
					colors.Add(col);
				} else if (line.StartsWith("f")) {
					string[] splits = line.Split(' ');
					indices.Add(int.Parse(splits[1]) - 1);
					indices.Add(int.Parse(splits[2]) - 1);
					indices.Add(int.Parse(splits[3]) - 1);
				}
			}
			Profiler.EndSample();

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
}
