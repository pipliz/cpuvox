﻿using Unity.Mathematics;
using static Unity.Mathematics.math;

public struct SegmentDDAData
{
	int2 position;
	int2 step;
	float2 start, dir, tDelta, tMax;
	float2 intersectionDistances;

	public int2 Position { get { return position; } }
	public float2 Direction { get { return dir; } }
	public float2 IntersectionDistances { get { return intersectionDistances; } } // x = last, y = next
	public float4 Intersections { get { return (start.xyxy + dir.xyxy * intersectionDistances.xxyy); } } // xy = last, zw = next

	public SegmentDDAData (float2 start, float2 dir)
	{
		this.start = start;
		this.dir = dir;

		position = int2(floor(start));
		tDelta = abs(1f / dir);
		float2 signDir = sign(dir);
		step = int2(signDir);
		tMax = (signDir * -frac(start) + (signDir * 0.5f) + 0.5f) * tDelta;

		signDir = -signDir;
		float2 tMaxReverse = (signDir * -frac(start) + (signDir * 0.5f) + 0.5f) * tDelta;

		intersectionDistances = float2(-cmin(tMaxReverse), cmin(tMax));
	}

	// this may likely be simplified
	public void NextLOD ()
	{
		float4 ints = Intersections;
		start = lerp(ints.xy, ints.zw, 0.5f) / 2f;

		position = int2(floor(start));
		float2 signDir = sign(dir);
		tMax = (signDir * -frac(start) + (signDir * 0.5f) + 0.5f) * tDelta;

		signDir = -signDir;
		float2 tMaxReverse = (signDir * -frac(start) + (signDir * 0.5f) + 0.5f) * tDelta;

		intersectionDistances = float2(-cmin(tMaxReverse), cmin(tMax));
	}

	public bool AtEnd (float farClip)
	{
		return IntersectionDistances.x >= farClip;
	}

	public void Step ()
	{
		if (intersectionDistances.y == tMax.y) {
			tMax.y += tDelta.y;
			position.y += step.y;
		} else {
			tMax.x += tDelta.x;
			position.x += step.x;
		}

		intersectionDistances = float2(intersectionDistances.y, cmin(tMax));
	}
}
