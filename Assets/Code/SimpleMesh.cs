using System.Collections.Generic;
using UnityEngine;

public class SimpleMesh
{
	public List<Vector3> Vertices;
	public List<int> Indices;
	public List<Color32> VertexColors;

	public SimpleMesh (List<Vector3> vertices, List<int> indices, List<Color32> vertexColors)
	{
		Vertices = vertices;
		Indices = indices;
		VertexColors = vertexColors;
	}

	public Bounds CalculateBounds ()
	{
		Vector3 min = Vector3.positiveInfinity;
		Vector3 max = Vector3.negativeInfinity;

		for (int i = 0; i < Vertices.Count; i++) {
			Vector3 vertex = Vertices[i];
			min = Vector3.Min(min, vertex);
			max = Vector3.Max(max, vertex);
		}

		Bounds b = new Bounds();
		b.SetMinMax(min, max);
		return b;
	}

	public void Remap (Vector3 oldMin, float scale)
	{
		for (int i = 0; i < Vertices.Count; i++) {
			Vertices[i] = (Vertices[i] - oldMin) * scale;
		}
	}
}
