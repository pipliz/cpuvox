using System;
using Unity.Collections;

public struct NativeArrayList<T> : IDisposable where T : struct
{
	public NativeArray<T> Array;
	public int Count;
	public Allocator Allocator;

	public NativeArrayList (int capacity, Allocator allocator)
	{
		Allocator = allocator;
		Array = new NativeArray<T>(capacity, allocator, NativeArrayOptions.UninitializedMemory);
		Count = 0;
	}

	public void Add (T item)
	{
		if (Count >= Array.Length) {
			DoubleArraySize();
		}
		Array[Count++] = item;
	}

	public T this[int idx]
	{
		get { return Array[idx]; }
		set { Array[idx] = value; }
	}

	void DoubleArraySize ()
	{
		NativeArray<T> newArray = new NativeArray<T>(Array.Length * 2, Allocator, NativeArrayOptions.UninitializedMemory);
		NativeArray<T>.Copy(Array, newArray, Array.Length);

		for (int i = Array.Length; i < newArray.Length; i++) {
			newArray[i] = default;
		}

		Array.Dispose();
		Array = newArray;
	}

	public void Dispose ()
	{
		((IDisposable)Array).Dispose();
	}
}
