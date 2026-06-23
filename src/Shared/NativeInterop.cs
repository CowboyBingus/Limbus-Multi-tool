using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using MonoMod.RuntimeDetour;
using System;
using System.Runtime.InteropServices;

namespace LimbusShared;

internal static class DetourLifecycle
{
    public static bool TryInstall<T>(
        string name,
        IntPtr method,
        T replacement,
        ref NativeDetour? detour,
        ref T? original,
        Action<string> warn,
        Action<string> info)
        where T : Delegate
    {
        if (detour != null)
            return true;

        if (method == IntPtr.Zero)
        {
            warn($"{name} detour skipped: IL2CPP MethodInfo was not resolved.");
            return false;
        }

        try
        {
            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                warn($"{name} detour skipped: native method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<T>();
            detour.Apply();
            info($"{name} detour installed at {NativeInterop.Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            warn($"{name} detour install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void Free<T>(ref NativeDetour? detour, ref T? original)
        where T : Delegate
    {
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Plugin unload can race Unity teardown.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }
}

internal static class NativeInterop
{
    public static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
}

internal static class PluginConfig
{
    public static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string pluginName, string name)
    {
        return entry ?? throw new InvalidOperationException($"{pluginName} config entry '{name}' is not initialized.");
    }

    public static bool IsSet(ConfigEntry<bool>? entry)
    {
        return entry?.Value ?? false;
    }
}

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

internal static class Il2CppLookup
{
    public static IntPtr RequireClass(string assembly, string ns, string name)
    {
        var klass = IL2CPP.GetIl2CppClass(assembly, ns, name);
        if (klass == IntPtr.Zero)
            throw new MissingMemberException($"IL2CPP class not found: {ns}.{name} in {assembly}");

        return klass;
    }

    public static IntPtr RequireMethod(IntPtr klass, string name, int args)
    {
        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, args);
        if (method == IntPtr.Zero)
            throw new MissingMethodException($"IL2CPP method not found: {name}/{args}");

        return method;
    }

    public static bool IsObjectClassOrNamed(IntPtr obj, IntPtr expectedClass, string fallbackName)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (klass == IntPtr.Zero)
                return false;

            if (klass == expectedClass)
                return true;

            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
            return name == fallbackName;
        }
        catch
        {
            return false;
        }
    }
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
