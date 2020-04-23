using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

/// <summary>
/// Wrapper around some UnityEngine.Camera things so that it works with Burst
/// </summary>
public struct CameraData
{
	float4x4 WorldToScreenMatrix;
	public float2 PositionXZ;
	public float PositionY;
	public int CameraDepthIterationDirection;

	public CameraData (Camera camera)
	{
		float3 pos = camera.transform.position;
		PositionXZ = pos.xz;
		PositionY = pos.y;
		float4x4 worldToCameraMatrix = camera.worldToCameraMatrix;
		float4x4 cameraToScreenMatrix = camera.nonJitteredProjectionMatrix;
		WorldToScreenMatrix = mul(cameraToScreenMatrix, worldToCameraMatrix);

		CameraDepthIterationDirection = (camera.transform.forward.y >= 0f ? -1 : 1) * (camera.transform.up.y >= 0f ? 1 : -1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ProjectToHomogeneousCameraSpace (float3 worldA, float3 worldB, out float4 camA, out float4 camB)
	{
		camA = mul(WorldToScreenMatrix, float4(worldA, 1f));
		camB = mul(WorldToScreenMatrix, float4(worldB, 1f));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ClipHomogeneousCameraSpaceLine (ref float4 a, ref float4 b)
	{
		// near-plane clipping
		if (a.z <= 0f) {
			if (b.z <= 0f) {
				return false;
			}
			float v = b.z / (b.z - a.z);
			a = b + v * (a - b);
		} else if (b.z <= 0f) {
			float v = a.z / (a.z - b.z);
			b = a + v * (b - a);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ClipHomogeneousCameraSpaceLine (ref float4 a, ref float4 b, ref float2 uvA, ref float2 uvB)
	{
		// near-plane clipping
		if (a.z <= 0f) {
			if (b.z <= 0f) {
				return false;
			}
			float v = b.z / (b.z - a.z);
			a = b + v * (a - b);
			uvA = uvB + v * (uvA - uvB);
		} else if (b.z <= 0f) {
			float v = a.z / (a.z - b.z);
			b = a + v * (b - a);
			uvB = uvA + v * (uvB - uvA);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float2 ProjectClippedToScreen (float4 resultA, float4 resultB, float2 screen, int desiredAxis)
	{
		// perspective divide and mapping to screen pixels
		float2 result = float2(resultA[desiredAxis], resultB[desiredAxis]);
		float2 w = float2(resultA.w, resultB.w);
		return mad(result / w, 0.5f, 0.5f) * screen[desiredAxis];
	}
}
