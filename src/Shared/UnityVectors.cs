using System.Runtime.InteropServices;

namespace LimbusShared;

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
