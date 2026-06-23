using Il2CppInterop.Runtime;
using LimbusShared;
using MonoMod.RuntimeDetour;
using System;

namespace LimbusHdrBalanceFix;

internal readonly record struct HdrNativeDetourTarget(
    string Name,
    string Assembly,
    string TypeNamespace,
    string TypeName,
    string MethodName,
    int ArgumentCount);

internal static class HdrNativeDetour
{
    public static bool TryInstall<T>(
        HdrNativeDetourTarget target,
        T replacement,
        ref NativeDetour? detour,
        ref T? original)
        where T : Delegate
    {
        if (detour != null)
            return true;

        var klass = IL2CPP.GetIl2CppClass(target.Assembly, target.TypeNamespace, target.TypeName);
        if (klass == IntPtr.Zero)
        {
            HdrBalanceHost.Log.LogWarning($"{target.Name} skipped: {target.TypeNamespace}.{target.TypeName} class was not resolved.");
            return false;
        }

        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, target.MethodName, target.ArgumentCount);
        return SharedRuntime.TryInstallDetour(
            target.Name,
            method,
            replacement,
            ref detour,
            ref original,
            message => HdrBalanceHost.Log.LogWarning(message),
            message => HdrBalanceHost.Log.LogInfo(message));
    }
}