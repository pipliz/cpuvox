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

	public bool StepToWorldIntersection (float2 dimensions)
	{
		float2 inverseDir = 1f / Direction;

		float2 tmin = float.NegativeInfinity;
		float2 tmax = float.PositiveInfinity;

		if (Direction.x != 0.0) {
			float tx1 = -start.x * inverseDir.x;
			float tx2 = (dimensions.x - start.x) * inverseDir.x;

			tmin.x = min(tx1, tx2);
			tmax.x = max(tx1, tx2);
		}

		if (Direction.y != 0.0) {
			float ty1 = -start.y * inverseDir.y;
			float ty2 = (dimensions.y - start.y) * inverseDir.y;

			tmin.y = min(ty1, ty2);
			tmax.y = max(ty1, ty2);
		}

		float tmint = cmax(tmin);
		float tmaxt = cmin(tmax);

		if (tmaxt < tmint || tmint <= 0f) {
			return false;
		}

		float2 tLast;

		if (tmin.x < tmin.y && tmin.x != float.NegativeInfinity) {
			tLast.y = tmin.y;

			// we only have the actual distance to the entry-distance for one dimensions; we must adjust the other dimension accordingly
			float offsetAxisToHit = tmint * dir.x;
			float hitPosition = start.x + offsetAxisToHit;
			hitPosition = dir.x > 0f ? floor(hitPosition) : ceil(hitPosition);
			offsetAxisToHit = hitPosition - start.x;
			tLast.x = offsetAxisToHit / dir.x;
		} else {
			tLast.x = tmin.x;

			float offsetAxisToHit = tmint * dir.y;
			float hitPosition = start.y + offsetAxisToHit;
			hitPosition = dir.y > 0f ? floor(hitPosition) : ceil(hitPosition);
			offsetAxisToHit = hitPosition - start.y;
			tLast.y = offsetAxisToHit / dir.y;
		}

		tMax = tLast + tDelta;
		intersectionDistances = float2(cmax(tLast), cmin(tMax));
		position = int2(floor(start + lerp(intersectionDistances.x, intersectionDistances.y, 0.5f) * dir));
		return true;
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

	public bool IsBeyondFarClip (float farClip)
	{
		return cmin(tMax) >= farClip;
	}
}
