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
