using System;

namespace LimbusShared.Interop;

internal static class NativeInterop
{
    public static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
}
