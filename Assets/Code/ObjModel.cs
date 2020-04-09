using System.Collections.Generic;
using UnityEngine;

public static class ObjModel
{
	public static SimpleMesh Import (string path, float maxDimensionSize)
	{
		using (var file = new System.IO.StreamReader(path)) {
			List<Vector3> vertices = new List<Vector3>();
			List<Color32> colors = new List<Color32>();
			List<int> indices = new List<int>();

			var numberFormat = System.Globalization.CultureInfo.InvariantCulture.NumberFormat;

			while (!file.EndOfStream) {
				string line = file.ReadLine();
				if (line.StartsWith("#")) {
					continue;
				} else if (line.StartsWith("v")) {
					string[] splits = line.Split(' ');
					Vector3 v = new Vector3(
						float.Parse(splits[1], numberFormat),
						-float.Parse(splits[3], numberFormat),
						float.Parse(splits[2], numberFormat)
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

			SimpleMesh mesh = new SimpleMesh(vertices, indices, colors);
			Bounds bounds =	mesh.CalculateBounds();
			Debug.Log($"Imported mesh with bounds {bounds}");
			Vector3 size = bounds.size;
			float scale = maxDimensionSize / Mathf.Max(size.x, size.y, size.z);
			Vector3 newSize = size * scale;
			mesh.Remap(bounds.min, scale);
			Bounds newBounds = mesh.CalculateBounds();
			Debug.Log($"Rescaled/positioned mesh to {newBounds}");
			return mesh;
		}
	}
}
