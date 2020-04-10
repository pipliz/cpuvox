using Unity.Mathematics;
using static Unity.Mathematics.math;

public struct SegmentDDAData
{
	public int2 position;

	int2 step;
	float2 start, dir, tDelta, tMax;
	float2 intersectionDistances;

	public bool AtEnd { get { return intersectionDistances.y > 1f; } }

	public float2 IntersectionDistancesUnnormalized { get { return intersectionDistances; } } // x = last, y = next
	public float4 Intersections { get { return start.xyxy + dir.xyxy * intersectionDistances.xxyy; } } // xy = last, zw = next

	public SegmentDDAData (float2 start, float2 dir)
	{
		this.start = start;
		position = new int2(floor(start));
		float2 negatedFracStart = -frac(start);

		this.dir = dir;
		dir = select(dir, 0.00001f, dir == 0f);
		float2 rayDirInverse = rcp(dir);
		step = int2(sign(dir));
		tDelta = min(rayDirInverse * step, 1f);
		tMax = abs((negatedFracStart + max(step, 0f)) * rayDirInverse);

		// also calculate the "previous" intersection, needed to render voxels exactly below/above us
		float2 tMaxReverse = abs((negatedFracStart + max(-step, 0f)) * -rayDirInverse);
		intersectionDistances = float2(-cmin(tMaxReverse), cmin(tMax));
	}

	public void Step ()
	{
		int dimension = select(0, 1, intersectionDistances.y == tMax.y);
		tMax[dimension] += tDelta[dimension];
		position[dimension] += step[dimension];
		intersectionDistances.x = intersectionDistances.y;
		intersectionDistances.y = cmin(tMax);
	}
}
