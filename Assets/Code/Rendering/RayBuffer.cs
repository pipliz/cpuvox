using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Simple wrapper around a bunch of texture2D's.
/// Normally we'd use just the one buffer, but uploading all the pixels is a significant bottleneck.
/// Splitting the buffer into smaller parts and only uploading touched ones helps save some time.
/// </summary>
public class RayBuffer
{
	public RenderTexture FinalTexture;
	public Texture2D[] Partials;

	const int RAYS_PER_PARTIAL = 256;

	public RayBuffer (int x, int y)
	{
		Setup(x, y);
	}

	public void Setup (int x, int y)
	{
		FinalTexture = new RenderTexture(new RenderTextureDescriptor(x, y, RenderTextureFormat.ARGB32, 0, 0));
		FinalTexture.filterMode = FilterMode.Point;

		// x = screenwidth or screenheight
		// y = max ray count for the segment and its mirror
		Partials = new Texture2D[GetPartialCount(y)];
		for (int i = 0; i < Partials.Length; i++) {
			Partials[i] = new Texture2D(x, RAYS_PER_PARTIAL, TextureFormat.ARGB32, false, false)
			{
				filterMode = FilterMode.Point
			};
		}
	}

	static int GetPartialCount (int rays)
	{
		return ((rays + 1) / RAYS_PER_PARTIAL) + 1;
	}

	public void Destroy ()
	{
		Object.Destroy(FinalTexture);
		for (int i = 0; i < Partials.Length; i++) {
			Object.Destroy(Partials[i]);
		}
	}

	public void Resize (int x, int y)
	{
		Destroy();
		Setup(x, y);
	}

	public void ApplyPartials (int raysUsed, UnityEngine.Rendering.CommandBuffer commands)
	{
		if (raysUsed <= 0) {
			return;
		}
		Profiler.BeginSample("Apply partials");
		int partialsMax = GetPartialCount(raysUsed);
		for (int i = 0; i < partialsMax; i++) {
			Partials[i].Apply(false, false);
		}
		Profiler.EndSample();
		Profiler.BeginSample("Copy partials to RT");
		for (int i = 0; i < partialsMax; i++) {
			int columnsTillEnd = FinalTexture.height - i * RAYS_PER_PARTIAL;
			int columns = Mathf.Min(columnsTillEnd, RAYS_PER_PARTIAL);
			commands.CopyTexture(
				Partials[i], 0, 0, 0, 0, Partials[i].width, columns,
				FinalTexture, 0, 0, 0, i * RAYS_PER_PARTIAL);
		}
		Profiler.EndSample();
	}

	public Native GetNativeData (Allocator allocator)
	{
		return new Native(Partials, allocator);
	}

	public unsafe struct Native
	{
		NativeArray<System.IntPtr> Partials;
		int PartialWidth;
		int PartialHeight;

		public Native (Texture2D[] partials, Allocator allocator)
		{
			PartialWidth = partials[0].width;
			PartialHeight = partials[0].height;
			Partials = new NativeArray<System.IntPtr>(partials.Length, allocator, NativeArrayOptions.UninitializedMemory);
			for (int i = 0; i < partials.Length; i++) {
				Partials[i] = new System.IntPtr(partials[i].GetRawTextureData<ColorARGB32>().GetUnsafePtr());
			}
		}

		public NativeArray<ColorARGB32> GetRayColumn (int rayIndex)
		{
			int partialIdx = rayIndex / RAYS_PER_PARTIAL;
			int rowIdx = rayIndex % RAYS_PER_PARTIAL;

			ColorARGB32* colPtr = (ColorARGB32*)Partials[partialIdx].ToPointer();
			return NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<ColorARGB32>(colPtr + rowIdx * PartialWidth, PartialWidth, Allocator.None);
		}

		public void Dispose ()
		{
			Partials.Dispose();
		}
	}
}
