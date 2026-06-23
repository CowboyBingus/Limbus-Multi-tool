using Il2CppInterop.Runtime;
using System;
using System.Runtime.InteropServices;

namespace LimbusShared.Interop;

internal static class Il2CppInvoke
{
    public static IntPtr Object(IntPtr method, IntPtr instance)
    {
        unsafe
        {
            return ObjectUnsafe(method, instance, null);
        }
    }

    public static unsafe IntPtr ObjectUnsafe(IntPtr method, IntPtr instance, void** args)
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, args, ref exception);
        if (exception != IntPtr.Zero)
            throw new InvalidOperationException($"IL2CPP invocation failed: exception={NativeInterop.Ptr(exception)}");

        return result;
    }

    public static string String(IntPtr method, IntPtr instance)
    {
        var result = Object(method, instance);
        return result == IntPtr.Zero ? "" : IL2CPP.Il2CppStringToManaged(result) ?? "";
    }

    public static int Int32(IntPtr method, IntPtr instance)
    {
        var result = Object(method, instance);
        return Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(result));
    }

    public static bool Boolean(IntPtr method, IntPtr instance)
    {
        var result = Object(method, instance);
        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(result)) != 0;
    }

    public static T Struct<T>(IntPtr method, IntPtr instance)
        where T : struct
    {
        var result = Object(method, instance);
        return Marshal.PtrToStructure<T>(IL2CPP.il2cpp_object_unbox(result));
    }

    public static IntPtr ObjectWithIntArg(IntPtr method, IntPtr instance, int value)
    {
        unsafe
        {
            var args = stackalloc void*[1];
            args[0] = &value;
            return ObjectUnsafe(method, instance, args);
        }
    }

    public static void SetBoolean(IntPtr method, IntPtr instance, bool value)
    {
        unsafe
        {
            var raw = value ? (byte)1 : (byte)0;
            var args = stackalloc void*[1];
            args[0] = &raw;
            ObjectUnsafe(method, instance, args);
        }
    }

    public static void SetStruct<T>(IntPtr method, IntPtr instance, T value)
        where T : unmanaged
    {
        unsafe
        {
            var args = stackalloc void*[1];
            args[0] = &value;
            ObjectUnsafe(method, instance, args);
        }
    }
}
