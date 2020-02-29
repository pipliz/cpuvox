using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class PlyModel
{
	public Vector3[] Vertices;
	public int[] Indices;

	public PlyModel (string path, float scale, Vector3 offset)
	{
		using (var file = new System.IO.StreamReader(path)) {
			if (file.ReadLine() != "ply") {
				Debug.LogWarning($"not a supported ply file");
				return;
			}
			file.ReadLine(); // format ascii 1.0
			file.ReadLine(); // blender comment
			int vertexCount = int.Parse(file.ReadLine().Split(' ')[2]); // element vertex {count}
			file.ReadLine(); // property float x
			file.ReadLine(); // propery float y
			file.ReadLine(); // property float z
			int triangleCount = int.Parse(file.ReadLine().Split(' ')[2]); // element face {count}
			file.ReadLine(); // property list ...
			file.ReadLine(); // end header

			var culture = System.Globalization.CultureInfo.InvariantCulture.NumberFormat;
			Vector3[] vectors = new Vector3[vertexCount];
			for (int i = 0; i < vertexCount; i++) {
				string line = file.ReadLine();
				string[] splits = line.Split(' ');
				Vector3 v = new Vector3(
					float.Parse(splits[0], culture),
					float.Parse(splits[1], culture),
					float.Parse(splits[2], culture)
				);
				vectors[i] = offset + v * scale;
			}

			int[] tris = new int[triangleCount * 3];
			for (int i = 0; i < triangleCount; i++) {
				string line = file.ReadLine();
				string[] splits = line.Split(' ');
				tris[i * 3] = int.Parse(splits[1]);
				tris[i * 3 + 1] = int.Parse(splits[2]);
				tris[i * 3 + 2] = int.Parse(splits[3]);
			}
			Vertices = vectors;
			Indices = tris;
		}
	}
}
