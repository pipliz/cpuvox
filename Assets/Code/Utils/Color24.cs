using System.Runtime.InteropServices;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct ColorARGB32
{
	byte a;
	public byte r;
	public byte g;
	public byte b;

	public ColorARGB32 (byte r, byte g, byte b)
	{
		a = 255;
		this.r = r;
		this.g = g;
		this.b = b;
	}

	public static implicit operator ColorARGB32 (Color32 source)
	{
		return new ColorARGB32(source.r, source.g, source.b);
	}
}
