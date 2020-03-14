using System.Runtime.InteropServices;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct Color24
{
	public byte r;
	public byte g;
	public byte b;

	public Color24 (byte r, byte g, byte b)
	{
		this.r = r;
		this.g = g;
		this.b = b;
	}

	public static implicit operator Color24 (Color32 source)
	{
		return new Color24(source.r, source.g, source.b);
	}
}
