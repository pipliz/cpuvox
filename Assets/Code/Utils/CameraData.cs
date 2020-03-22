using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

public struct CameraData
{
	public float3 Position;
	public float ForwardY;
	public float FarClip;

	float4x4 worldToCameraMatrix;
	float4x4 cameraToScreenMatrix;

	float4x4 ScreenToWorldMatrix;
	float4x4 WorldToScreenMatrix;

	public CameraData (Camera camera)
	{
		FarClip = camera.farClipPlane;
		Position = camera.transform.position;
		ForwardY = camera.transform.forward.y;

		worldToCameraMatrix = camera.worldToCameraMatrix;
		cameraToScreenMatrix = camera.nonJitteredProjectionMatrix;
		WorldToScreenMatrix = mul(cameraToScreenMatrix, worldToCameraMatrix);
		ScreenToWorldMatrix = inverse(WorldToScreenMatrix);
	}

	public float3 ScreenToWorldPoint (float3 pos, float2 screenSize)
	{
		float4 pos4 = float4((pos.xy / screenSize) * 2f - 1f, pos.z, 1f);
		pos4 = mul(ScreenToWorldMatrix, pos4);
		return pos4.xyz / pos4.w;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ProjectToScreen (float3 worldA, float3 worldB, float2 screen, int desiredAxis, out float2 yResults)
	{
		float4 resultA = mul(WorldToScreenMatrix, float4(worldA, 1f));
		float4 resultB = mul(WorldToScreenMatrix, float4(worldB, 1f));

		if (resultA.z <= 0f) {
			if (resultB.z <= 0f) {
				yResults = default;
				return false;
			}
			resultA = resultB + (resultB.z / (resultB.z - resultA.z)) * (resultA - resultB);
		} else if (resultB.z <= 0f) {
			resultB = resultA + (resultA.z / (resultA.z - resultB.z)) * (resultB - resultA);
		}

		float2 result = float2(resultA[desiredAxis], resultB[desiredAxis]);
		float2 w = float2(resultA.w, resultB.w);
		yResults = mad(result / w, 0.5f, 0.5f) * screen[desiredAxis];
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ProjectToHomogeneousCameraSpace (float3 worldA, float3 worldB, out float4 camA, out float4 camB)
	{
		camA = mul(WorldToScreenMatrix, float4(worldA, 1f));
		camB = mul(WorldToScreenMatrix, float4(worldB, 1f));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool ProjectHomogeneousCameraSpaceToScreen (float4 resultA, float4 resultB, float2 screen, int desiredAxis, out float2 yResults)
	{
		if (resultA.z <= 0f) {
			if (resultB.z <= 0f) {
				yResults = default;
				return false;
			}
			resultA = resultB + (resultB.z / (resultB.z - resultA.z)) * (resultA - resultB);
		} else if (resultB.z <= 0f) {
			resultB = resultA + (resultA.z / (resultA.z - resultB.z)) * (resultB - resultA);
		}

		float2 result = float2(resultA[desiredAxis], resultB[desiredAxis]);
		float2 w = float2(resultA.w, resultB.w);
		yResults = mad(result / w, 0.5f, 0.5f) * screen[desiredAxis];
		return true;
	}
}
