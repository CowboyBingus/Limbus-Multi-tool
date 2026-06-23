using System;

namespace LimbusShared.Interop;

internal static class Il2CppInteropServices
{
    public static string Ptr(IntPtr ptr) => NativeInterop.Ptr(ptr);

    public static IntPtr RequireClass(string assembly, string ns, string name)
    {
        return Il2CppLookup.RequireClass(assembly, ns, name);
    }

    public static IntPtr InvokeObject(IntPtr method, IntPtr instance)
    {
        return Il2CppInvoke.Object(method, instance);
    }
}
