using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Simple wrapper around a bunch of texture2D's.
/// Normally we'd use just the one buffer, but uploading all the pixels is a significant bottleneck and can only be done from the main thread
/// Splitting the buffer into smaller parts and only uploading touched ones helps save some time.
/// We can also start uploading the first textures while we're still drawing into the later textures
/// </summary>
public class RayBuffer
{
	public RenderTexture FinalTexture;
	public Texture2D[] Partials;
	public int[] CompletedRows;

	const int RAYS_PER_PARTIAL = 256;
	const int RAYS_SHIFT = 8;

	int UsedTextureCount;
	int UsedTextureLastSize;

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
		int partialCount = GetPartialsCount(y, out int unused);

		Partials = new Texture2D[partialCount];
		CompletedRows = new int[Partials.Length];
		for (int i = 0; i < Partials.Length; i++) {
			Partials[i] = new Texture2D(x, RAYS_PER_PARTIAL, TextureFormat.ARGB32, false, false)
			{
				filterMode = FilterMode.Point
			};
		}
	}

	private int GetPartialsCount (int y, out int lastTextureWidth)
	{
		int partialCount = 0;
		lastTextureWidth = 0;
		while (y > 0) {
			partialCount++;
			lastTextureWidth = y;
			y -= RAYS_PER_PARTIAL;
		}

		return partialCount;
	}

	public void Prepare (int usedWidth)
	{
		System.Array.Clear(CompletedRows, 0, CompletedRows.Length);
		UsedTextureCount = GetPartialsCount(usedWidth, out UsedTextureLastSize);
	}

	public bool Completed (int rayIndex)
	{
		int textureIdx = rayIndex >> 8;
		int readValue = Interlocked.Increment(ref CompletedRows[textureIdx]);
		int textureWidth = (textureIdx == UsedTextureCount - 1) ? UsedTextureLastSize : RAYS_PER_PARTIAL;
		return readValue == textureWidth;
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

	public void ApplyPartials (UnityEngine.Rendering.CommandBuffer commands)
	{
		Profiler.BeginSample("Copy partials to RT");
		for (int i = 0; i < UsedTextureCount; i++) {
			int columns = (i == UsedTextureCount - 1) ? UsedTextureLastSize : RAYS_PER_PARTIAL;
			commands.CopyTexture(
				Partials[i], 0, 0, 0, 0, Partials[i].width, columns,
				FinalTexture, 0, 0, 0, i * RAYS_PER_PARTIAL);
		}
		Profiler.EndSample();
	}

	public void UploadCompletes ()
	{
		for (int i = 0; i < UsedTextureCount; i++) {
			int width = (i == UsedTextureCount - 1) ? UsedTextureLastSize : RAYS_PER_PARTIAL;
			if (CompletedRows[i] == width) {
				Partials[i].Apply(false, false);
				CompletedRows[i] = -1;
			}
		}
	}

	public Native GetNativeData (Allocator allocator)
	{
		return new Native(Partials, allocator);
	}

	public unsafe struct Native
	{
		PartialBuffer* Partials;
		Allocator allocator;
		int PartialWidth;
		int PartialHeight;

		public Native (Texture2D[] partials, Allocator allocator)
		{
			this.allocator = allocator;
			PartialWidth = partials[0].width;
			PartialHeight = partials[0].height;
			Partials = (PartialBuffer*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<PartialBuffer>() * partials.Length, UnsafeUtility.AlignOf<PartialBuffer>(), allocator);
			for (int i = 0; i < partials.Length; i++) {
				Partials[i] = new PartialBuffer((ColorARGB32*)partials[i].GetRawTextureData<ColorARGB32>().GetUnsafePtr());
			}
		}

		public ColorARGB32* GetRayColumn (int rayIndex)
		{
			int partialIdx = rayIndex >> RAYS_SHIFT;
			int rowIdx = rayIndex & (RAYS_PER_PARTIAL - 1);

			PartialBuffer dataPointer = Partials[partialIdx];
			return dataPointer.Pixels + rowIdx * PartialWidth;
		}

		public void Dispose ()
		{
			UnsafeUtility.Free(Partials, allocator);
		}

		unsafe struct PartialBuffer
		{
			public ColorARGB32* Pixels;

			public PartialBuffer (ColorARGB32* pixels)
			{
				Pixels = pixels;
			}
		}
	}
}
