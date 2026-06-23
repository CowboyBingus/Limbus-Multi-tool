using BepInEx.Configuration;
using LimbusShared.Configuration;
using LimbusShared.Detours;
using LimbusShared.Interop;
using LimbusShared.Unity;
using MonoMod.RuntimeDetour;
using System;

namespace LimbusShared;

internal static class SharedRuntime
{
    public static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string pluginName, string name)
    {
        return PluginConfig.Required(entry, pluginName, name);
    }

    public static bool IsSet(ConfigEntry<bool>? entry)
    {
        return PluginConfig.IsSet(entry);
    }

    public static bool TryInstallDetour<T>(
        string name,
        IntPtr method,
        T replacement,
        ref NativeDetour? detour,
        ref T? original,
        Action<string> warn,
        Action<string> info)
        where T : Delegate
    {
        return DetourLifecycle.TryInstall(name, method, replacement, ref detour, ref original, warn, info);
    }

    public static void FreeDetour<T>(ref NativeDetour? detour, ref T? original)
        where T : Delegate
    {
        DetourLifecycle.Free(ref detour, ref original);
    }

    public static string Ptr(IntPtr ptr) => Il2CppInteropServices.Ptr(ptr);

    public static Vector2Value Vector2(float x, float y) => UnityValueTypes.Vector2(x, y);

    public static Vector3Value Vector3(float x, float y, float z) => UnityValueTypes.Vector3(x, y, z);
}
