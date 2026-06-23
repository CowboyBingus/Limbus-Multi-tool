using Il2CppInterop.Runtime;
using LimbusShared;
using MonoMod.RuntimeDetour;
using System;

namespace LimbusHdrBalanceFix;

internal static class HdrNativeDetour
{
    public static bool TryInstall<T>(
        string name,
        string assembly,
        string typeNamespace,
        string typeName,
        string methodName,
        int argumentCount,
        T replacement,
        ref NativeDetour? detour,
        ref T? original)
        where T : Delegate
    {
        if (detour != null)
            return true;

        var klass = IL2CPP.GetIl2CppClass(assembly, typeNamespace, typeName);
        if (klass == IntPtr.Zero)
        {
            HdrBalanceHost.Log.LogWarning($"{name} skipped: {typeNamespace}.{typeName} class was not resolved.");
            return false;
        }

        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, methodName, argumentCount);
        return SharedRuntime.TryInstallDetour(
            name,
            method,
            replacement,
            ref detour,
            ref original,
            message => HdrBalanceHost.Log.LogWarning(message),
            message => HdrBalanceHost.Log.LogInfo(message));
    }
}
