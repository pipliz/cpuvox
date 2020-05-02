using System;
using System.Runtime.InteropServices;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct ColorARGB32 : IEquatable<ColorARGB32>
{
	public byte a;
	public byte r;
	public byte g;
	public byte b;

	public ColorARGB32 (byte r, byte g, byte b, byte a = 255)
	{
		this.a = a;
		this.r = r;
		this.g = g;
		this.b = b;
	}

	public static implicit operator ColorARGB32 (Color32 source)
	{
		return new ColorARGB32(source.r, source.g, source.b, source.a);
	}

	public static implicit operator Color32 (ColorARGB32 source)
	{
		return new Color32(source.r, source.g, source.b, source.a);
	}

	public static bool operator == (ColorARGB32 aRGB1, ColorARGB32 aRGB2)
	{
		return aRGB1.Equals(aRGB2);
	}

	public static bool operator != (ColorARGB32 aRGB1, ColorARGB32 aRGB2)
	{
		return !(aRGB1 == aRGB2);
	}

	public override bool Equals (object obj)
	{
		return obj is ColorARGB32 && Equals((ColorARGB32)obj);
	}

	public bool Equals (ColorARGB32 other)
	{
		return a == other.a &&
			   r == other.r &&
			   g == other.g &&
			   b == other.b;
	}

	public override int GetHashCode ()
	{
		var hashCode = 94257292;
		hashCode = hashCode * -1521134295 + a.GetHashCode();
		hashCode = hashCode * -1521134295 + r.GetHashCode();
		hashCode = hashCode * -1521134295 + g.GetHashCode();
		hashCode = hashCode * -1521134295 + b.GetHashCode();
		return hashCode;
	}
}
