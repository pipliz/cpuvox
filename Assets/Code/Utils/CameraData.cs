﻿using System.Runtime.CompilerServices;
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
	public bool InverseElementIterationDirection;
	public float FarClip;

	public CameraData (Camera camera)
	{
		FarClip = camera.farClipPlane;
		float3 pos = camera.transform.position;
		PositionXZ = pos.xz;
		PositionY = pos.y;
		float4x4 worldToCameraMatrix = camera.worldToCameraMatrix;
		float4x4 cameraToScreenMatrix = camera.nonJitteredProjectionMatrix;
		WorldToScreenMatrix = mul(cameraToScreenMatrix, worldToCameraMatrix);

		InverseElementIterationDirection = camera.transform.forward.y >= 0f;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ProjectToHomogeneousCameraSpace (float3 worldA, float3 worldB, out float4 camA, out float4 camB)
	{
		camA = mul(WorldToScreenMatrix, float4(worldA, 1f));
		camB = mul(WorldToScreenMatrix, float4(worldB, 1f));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool GetWorldBoundsClippingCamSpace (ref float4 pMin, ref float4 pMax, int Y_AXIS, ref float uMin, ref float uMax)
	{
		if (pMin.z <= 0f) {
			if (pMax.z <= 0f) {
				return true; // both behind near plane/camera
			}
			float v = pMax.z / (pMax.z - pMin.z);
			pMin = lerp(pMax, pMin, v);
			uMin = lerp(uMax, uMin, v);
		} else if (pMax.z <= 0f) {
			float v = pMin.z / (pMin.z - pMax.z);
			pMax = lerp(pMin, pMax, v);
			uMax = lerp(uMin, uMax, v);
		}

		const float TOP_FRUSTUM = 1f;
		const float BOTTOM_FRUSTUM = -1f;

		const float TOP_FRUSTUM_INV = 1f / TOP_FRUSTUM;
		const float BOTTOM_FRUSTUM_INV = 1f / BOTTOM_FRUSTUM;

		if (pMin[Y_AXIS] > pMin.w * TOP_FRUSTUM) {
			if (pMax[Y_AXIS] > pMax.w * TOP_FRUSTUM) {
				return true; // both above the frustum
			}
			float c0 = cross(float2(1f, TOP_FRUSTUM_INV), float2(pMax[Y_AXIS], pMax.w));
			float c1 = cross(float2(1f, TOP_FRUSTUM_INV), float2(pMin[Y_AXIS], pMin.w));
			float v = c0 / (c0 - c1);
			pMin = lerp(pMax, pMin, v);
			uMin = lerp(uMax, uMin, v);
		} else if (pMax[Y_AXIS] > pMax.w * TOP_FRUSTUM) {
			float c0 = cross(float2(1f, TOP_FRUSTUM_INV), float2(pMax[Y_AXIS], pMax.w));
			float c1 = cross(float2(1f, TOP_FRUSTUM_INV), float2(pMin[Y_AXIS], pMin.w));
			float v = c1 / (c1 - c0);
			pMax = lerp(pMin, pMax, v);
			uMax = lerp(uMin, uMax, v);
		}

		if (pMin[Y_AXIS] < pMin.w * BOTTOM_FRUSTUM) {
			if (pMax[Y_AXIS] < pMax.w * BOTTOM_FRUSTUM) {
				return true; // both below the frustum
			}
			float c0 = cross(float2(1f, BOTTOM_FRUSTUM_INV), float2(pMax[Y_AXIS], pMax.w));
			float c1 = cross(float2(1f, BOTTOM_FRUSTUM_INV), float2(pMin[Y_AXIS], pMin.w));
			float v = c0 / (c0 - c1);
			pMin = lerp(pMax, pMin, v);
			uMin = lerp(uMax, uMin, v);
		} else if (pMax[Y_AXIS] < pMax.w * BOTTOM_FRUSTUM) {
			float c0 = cross(float2(1f, BOTTOM_FRUSTUM_INV), float2(pMax[Y_AXIS], pMax.w));
			float c1 = cross(float2(1f, BOTTOM_FRUSTUM_INV), float2(pMin[Y_AXIS], pMin.w));
			float v = c1 / (c1 - c0);
			pMax = lerp(pMin, pMax, v);
			uMax = lerp(uMin, uMax, v);
		}

		float cross (float2 a, float2 b)
		{
			return a.x * b.y - a.y * b.x;
		}

		return false;
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
			a = lerp(b, a, v);
		} else if (b.z <= 0f) {
			float v = a.z / (a.z - b.z);
			b = lerp(a, b, v);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ClipHomogeneousCameraSpaceLine (ref float4 pA, ref float4 pB, ref float uA, ref float uB)
	{
		// near-plane clipping
		if (pA.z <= 0f) {
			if (pB.z <= 0f) {
				return false;
			}
			float v = pB.z / (pB.z - pA.z);
			pA = lerp(pB, pA, v);
			uA = lerp(uB, uA, v);
		} else if (pB.z <= 0f) {
			float v = pA.z / (pA.z - pB.z);
			pB = lerp(pA, pB, v);
			uB = lerp(uA, uB, v);
		}
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float2 ProjectClippedToScreen (float4 resultA, float4 resultB, float2 screen, int Y_AXIS)
	{
		// perspective divide and mapping to screen pixels
		float2 result = float2(resultA[Y_AXIS], resultB[Y_AXIS]);
		float2 w = float2(resultA.w, resultB.w);
		return mad(result / w, 0.5f, 0.5f) * screen[Y_AXIS];
	}
}
