using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

/// <summary>
/// Wrapper around some UnityEngine.Camera things so that it works with Burst
/// </summary>
public unsafe struct CameraData
{
	float4x4 WorldToScreenMatrix;
	public float2 PositionXZ;
	public float PositionY;
	public bool InverseElementIterationDirection;
	public float FarClip;
	public fixed float LODDistances[UnityManager.LOD_LEVELS];

	public CameraData (Camera camera, float[] LODDistancesArray, float2 screen)
	{
		FarClip = camera.farClipPlane;
		float3 pos = camera.transform.position;
		PositionXZ = pos.xz;
		PositionY = pos.y;
		float4x4 worldToCameraMatrix = camera.worldToCameraMatrix;
		float4x4 cameraToScreenMatrix = camera.nonJitteredProjectionMatrix;
		WorldToScreenMatrix = mul(cameraToScreenMatrix, worldToCameraMatrix);
		WorldToScreenMatrix = mul(Unity.Mathematics.float4x4.Scale(0.5f, 0.5f, 1f), WorldToScreenMatrix); // scale from -1 .. 1 to -0.5 .. 0.5
		WorldToScreenMatrix = mul(Unity.Mathematics.float4x4.Translate(float3(0.5f, 0.5f, 1f)), WorldToScreenMatrix); // translate from -0.5 .. 0.5 to 0 .. 1
		WorldToScreenMatrix = mul(Unity.Mathematics.float4x4.Scale(screen.x, screen.y, 1f), WorldToScreenMatrix); // scale from 0 .. 1 to 0 .. screen

		InverseElementIterationDirection = camera.transform.forward.y >= 0f;

		for (int i = 0; i < LODDistancesArray.Length; i++) {
			LODDistances[i] = LODDistancesArray[i];
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float4 ProjectToHomogeneousCameraSpace (float3 worldA)
	{
		return mul(WorldToScreenMatrix, float4(worldA, 1f));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float4 ProjectVectorToHomogeneousCameraSpace (float3 vector)
	{
		return mul(WorldToScreenMatrix, float4(vector, 0f));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool GetWorldBoundsClippingCamSpace (
		float3 pMin,
		float3 pMax,
		float frustumBoundMin,
		float frustumBoundMax,
		out float minLerp,
		out float maxLerp
	) {
		if (pMin.x > pMin.z * frustumBoundMax) {
			if (pMax.x > pMax.z * frustumBoundMax) {
				minLerp = 0f;
				maxLerp = 1f;
				return true; // both above the frustum
			}


			ClipMin(pMin, pMax, frustumBoundMax, out minLerp);
			if (pMax.x < pMax.z * frustumBoundMin) {
				ClipMax(pMin, pMax, frustumBoundMin, out maxLerp);
			} else {
				maxLerp = 1f;
			}
		} else if (pMax.x > pMax.z * frustumBoundMax) {
			ClipMax(pMin, pMax, frustumBoundMax, out maxLerp);
			if (pMin.x < pMin.z * frustumBoundMin) {
				ClipMin(pMin, pMax, frustumBoundMin, out minLerp);
			} else {
				minLerp = 0f;
			}
		} else {
			if (pMin.x < pMin.z * frustumBoundMin) {
				if (pMax.x < pMax.z * frustumBoundMin) {
					minLerp = 0f;
					maxLerp = 1f;
					return true; // both below the frustum
				}
				ClipMin(pMin, pMax, frustumBoundMin, out minLerp);
				maxLerp = 1f;
			} else if (pMax.x < pMax.z * frustumBoundMin) {
				ClipMax(pMin, pMax, frustumBoundMin, out maxLerp);
				minLerp = 0f;
			} else {
				// nothing at all clipped
				minLerp = 0f;
				maxLerp = 1f;
			}
		}

		return false;

		void ClipMin (float3 pMinL, float3 pMaxL, float frustum, out float uMinResultL)
		{
			float frustum_inv = 1f / frustum;
			float c0 = cross(float2(1f, frustum_inv), float2(pMaxL.x, pMaxL.z));
			float c1 = cross(float2(1f, frustum_inv), float2(pMinL.x, pMinL.z));
			uMinResultL = 1f - (c0 / (c0 - c1));
		}

		void ClipMax (float3 pMinL, float3 pMaxL, float frustum, out float uMaxResultL)
		{
			float frustum_inv = 1f / frustum;
			float c0 = cross(float2(1f, frustum_inv), float2(pMaxL.x, pMaxL.z));
			float c1 = cross(float2(1f, frustum_inv), float2(pMinL.x, pMinL.z));
			uMaxResultL = c1 / (c1 - c0);
		}

		float cross (float2 a, float2 b)
		{
			return a.x * b.y - a.y * b.x;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ClipHomogeneousCameraSpaceLine (ref float3 a, ref float3 b)
	{
		// near-plane clipping
		if (a.y <= 0f) {
			if (b.y <= 0f) {
				return false;
			}
			float v = b.y / (b.y - a.y);
			a = lerp(b, a, v);
		} else if (b.y <= 0f) {
			float v = a.y / (a.y - b.y);
			b = lerp(a, b, v);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ClipHomogeneousCameraSpaceLine (ref float3 pA, ref float3 pB, ref float uA, ref float uB)
	{
		// near-plane clipping
		if (pA.y <= 0f) {
			if (pB.y <= 0f) {
				return false;
			}
			float v = pB.y / (pB.y - pA.y);
			pA = lerp(pB, pA, v);
			uA = lerp(uB, uA, v);
		} else if (pB.y <= 0f) {
			float v = pA.y / (pA.y - pB.y);
			pB = lerp(pA, pB, v);
			uB = lerp(uA, uB, v);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float2 ProjectClippedToScreen (float3 resultA, float3 resultB)
	{
		return float2(resultA.x, resultB.x) / float2(resultA.z, resultB.z);
	}
}
