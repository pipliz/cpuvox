using Unity.Mathematics;
using static Unity.Mathematics.math;

public struct SegmentDDAData
{
	int2 position;
	int2 step;
	float2 start, dir, tDelta, tMax;
	float2 intersectionDistances;

	public float2 Start { get { return start; } }
	public int2 Position { get { return position; } }
	public float2 Direction { get { return dir; } }
	public float2 IntersectionDistances { get { return intersectionDistances; } } // x = last, y = next
	public float4 Intersections { get { return (start.xyxy + dir.xyxy * intersectionDistances.xxyy); } } // xy = last, zw = next

	public SegmentDDAData (float2 start, float2 dir)
	{
		this.start = start;
		this.dir = dir;

		position = int2(floor(start));
		tDelta = 1f / max(0.0000001f, abs(dir));
		float2 signDir = sign(dir);
		step = int2(signDir);
		tMax = (signDir * -frac(start) + (signDir * 0.5f) + 0.5f) * tDelta;
		intersectionDistances = float2(cmax(tMax - tDelta), cmin(tMax));
	}

	// this may likely be simplified
	public void NextLOD (int currentVoxelSize)
	{
		float4 intersections = Intersections;

		float2 oldIntersection = intersections.xy;
		float2 newIntersection = intersections.zw;
		int2 remainders = position & (currentVoxelSize * 2 - 1); // 1, 2, 4, 8

		float2 tMaxPrevious = tMax - tDelta;

		if (dir.x >= 0f) {
			if (remainders.x < currentVoxelSize) {
				tMax.x += tDelta.x; // move forward the next intersection to the new LOD
			} else {
				tMaxPrevious.x -= tDelta.x; // move back the previous intersection to the new LOD alignment
			}
		} else {
			if (remainders.x < currentVoxelSize) {
				tMaxPrevious.x -= tDelta.x;
			} else {
				tMax.x += tDelta.x;
			}
		}

		if (dir.y >= 0f) {
			if (remainders.y < currentVoxelSize) {
				tMax.y += tDelta.y; // move forward the next intersection to the new LOD
			} else {
				tMaxPrevious.y -= tDelta.y; // move back the previous intersection to the new LOD alignment
			}
		} else {
			if (remainders.y < currentVoxelSize) {
				tMaxPrevious.y -= tDelta.y;
			} else {
				tMax.y += tDelta.y;
			}
		}

		intersectionDistances = float2(cmax(tMaxPrevious), cmin(tMax));
		position -= remainders;
		tDelta *= 2f;
		step *= 2;
	}

	/// <summary>
	/// Returns true when we hit the farclip
	/// </summary>
	public bool Step (float farclip)
	{
		float crossedBoundaryDistance;
		if (tMax.x < tMax.y) {
			crossedBoundaryDistance = tMax.x;
			tMax.x += tDelta.x;
			position.x += step.x;
		} else {
			crossedBoundaryDistance = tMax.y;
			tMax.y += tDelta.y;
			position.y += step.y;
		}

		intersectionDistances = float2(crossedBoundaryDistance, cmin(tMax));
		return crossedBoundaryDistance >= farclip;
	}
}
