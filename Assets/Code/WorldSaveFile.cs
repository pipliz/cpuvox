using System.IO.MemoryMappedFiles;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Unity.Mathematics.math;

public class WorldSaveFile
{
	public static unsafe void Serialize (World[] worlds, string filePath)
	{
		Header header = new Header();
		header.DimensionX = worlds[0].DimensionX;
		header.DimensionY = worlds[0].DimensionY;
		header.DimensionZ = worlds[0].DimensionZ;
		header.WorldCount = worlds.Length;

		long headerSize = UnsafeUtility.SizeOf<Header>();
		long[] offsets = new long[worlds.Length * 2];
		long offsetToStartOfWorld = headerSize;
		offsetToStartOfWorld += offsets.Length * sizeof(long);

		for (int i = 0; i < worlds.Length; i++) {
			long byteLength = worlds[i].Storage.GetByteLength();
			offsets[i * 2] = offsetToStartOfWorld;
			offsets[i * 2 + 1] = byteLength;
			offsetToStartOfWorld += byteLength;
		}

		using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(filePath, System.IO.FileMode.Create, null, offsetToStartOfWorld)) {
			using (MemoryMappedViewAccessor viewAccessor = file.CreateViewAccessor()) {
				byte* startPtr = (byte*)0;

				viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref startPtr);

				try {
					byte* ptr = startPtr;
					UnsafeUtility.CopyStructureToPtr(ref header, ptr);
					ptr += UnsafeUtility.SizeOf<Header>();

					long* offsetsFile = (long*)ptr;
					for (int i = 0; i < offsets.Length; i++) {
						offsetsFile[i] = offsets[i];
					}

					for (int i = 0; i < worlds.Length; i++) {
						void* filePtr = startPtr + offsets[i * 2];
						long length = offsets[i * 2 + 1];
						void* worldPtr = worlds[i].Storage.GetStartPointer();
						UnsafeUtility.MemCpy(filePtr, worldPtr, length);
					}
				} finally {
					viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
				}
			}
		}
	}

	public static unsafe World[] Deserialize (string filePath)
	{
		long headerSize = UnsafeUtility.SizeOf<Header>();
		long fileSize = new System.IO.FileInfo(filePath).Length;
		using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(filePath, System.IO.FileMode.Open, null, fileSize)) {
			using (MemoryMappedViewAccessor viewAccessor = file.CreateViewAccessor()) {
				byte* startPtr = (byte*)0;
				viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref startPtr);

				try {
					UnsafeUtility.CopyPtrToStructure(startPtr, out Header header);
					byte* ptr = startPtr + headerSize;

					long[] offsets = new long[header.WorldCount * 2];
					long* offsetsFile = (long*)ptr;
					for (int i = 0; i < offsets.Length; i++) {
						offsets[i] = offsetsFile[i];
					}

					World[] worlds = new World[header.WorldCount];

					int3 dimensions = int3(header.DimensionX, header.DimensionY, header.DimensionZ);

					for (int i = 0; i < worlds.Length; i++) {
						long offset = offsets[i * 2];
						long count = offsets[i * 2 + 1];
						void* source = startPtr + offset;
						void* goal = UnsafeUtility.Malloc(count, UnsafeUtility.AlignOf<World.RLEColumn>(), Unity.Collections.Allocator.Persistent);
						UnsafeUtility.MemCpy(goal, source, count);
						worlds[i] = new World(dimensions, i, goal);
					}
					return worlds;
				} finally {
					viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
				}
			}
		}
	}

	public struct Header
	{
		public long EmptyBytes;
		public int DimensionX;
		public int DimensionY;
		public int DimensionZ;
		public int WorldCount;
	}
}
