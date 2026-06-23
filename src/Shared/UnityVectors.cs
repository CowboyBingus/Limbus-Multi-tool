using System.Runtime.InteropServices;

namespace LimbusShared.Unity;

internal static class UnityValueTypes
{
    public static Vector2Value Vector2(float x, float y) => new() { X = x, Y = y };

    public static Vector3Value Vector3(float x, float y, float z) => new() { X = x, Y = y, Z = z };
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vector2Value
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vector3Value
{
    public float X;
    public float Y;
    public float Z;
}
