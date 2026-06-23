using LimbusShared;
using MonoMod.RuntimeDetour;
using System;
using System.Runtime.InteropServices;

namespace LimbusHdrBalanceFix;

internal static class HdrBalanceDetours
{
    private static readonly HdrInstanceOnEnableDetour volumeOnEnable = new(
        "Volume.OnEnable HDR balance detour",
        "Volume",
        "Volume.OnEnable",
        VolumeProfilePatcher.ApplyToVolume);

    private static readonly HdrInstanceOnEnableDetour volumeProfileOnEnable = new(
        "VolumeProfile.OnEnable HDR balance detour",
        "VolumeProfile",
        "VolumeProfile.OnEnable",
        VolumeProfilePatcher.ApplyToProfile);

    public static void Install()
    {
        volumeOnEnable.Install();
        volumeProfileOnEnable.Install();
        CanvasRenderPumpDetour.Install();
    }

    public static void Uninstall()
    {
        CanvasRenderPumpDetour.Uninstall();
        volumeProfileOnEnable.Uninstall();
        volumeOnEnable.Uninstall();
    }
}

internal static class CanvasRenderPumpDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StaticVoidDelegate(IntPtr methodInfo);

    private static NativeDetour? detour;
    private static StaticVoidDelegate? original;
    private static readonly StaticVoidDelegate replacement = Replacement;
    private static DateTime nextApplyUtc = DateTime.MinValue;
    [ThreadStatic] private static bool inReplacement;

    public static void Install()
    {
        HdrNativeDetour.TryInstall(
            "HDR balance pump",
            "UnityEngine.UIModule.dll",
            "UnityEngine",
            "Canvas",
            "SendWillRenderCanvases",
            0,
            replacement,
            ref detour,
            ref original);
    }

    public static void Uninstall()
    {
        SharedRuntime.FreeDetour(ref detour, ref original);
    }

    private static void Replacement(IntPtr methodInfo)
    {
        if (inReplacement)
        {
            original?.Invoke(methodInfo);
            return;
        }

        inReplacement = true;
        try
        {
            original?.Invoke(methodInfo);
            ApplyThrottled();
        }
        finally
        {
            inReplacement = false;
        }
    }

    private static void ApplyThrottled()
    {
        if (!HdrBalanceHost.IsEnabled)
            return;

        var interval = Math.Max(0.05f, HdrBalanceHost.ReapplyIntervalSeconds.Value);
        var now = DateTime.UtcNow;
        if (now < nextApplyUtc)
            return;

        nextApplyUtc = now.AddSeconds(interval);
        HdrOutputSettingsPatcher.Apply("Canvas.SendWillRenderCanvases", forceLog: false);
        VolumeProfilePatcher.ReapplyKnownProfiles("Canvas.SendWillRenderCanvases");
    }
}

internal sealed class HdrInstanceOnEnableDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InstanceVoidDelegate(IntPtr self, IntPtr methodInfo);

    private readonly string name;
    private readonly string typeName;
    private readonly string source;
    private readonly Action<IntPtr, string> afterOriginal;
    private readonly InstanceVoidDelegate replacement;
    private NativeDetour? detour;
    private InstanceVoidDelegate? original;

    public HdrInstanceOnEnableDetour(
        string name,
        string typeName,
        string source,
        Action<IntPtr, string> afterOriginal)
    {
        this.name = name;
        this.typeName = typeName;
        this.source = source;
        this.afterOriginal = afterOriginal;
        replacement = Replacement;
    }

    public void Install()
    {
        HdrNativeDetour.TryInstall(
            name,
            "Unity.RenderPipelines.Core.Runtime.dll",
            "UnityEngine.Rendering",
            typeName,
            "OnEnable",
            0,
            replacement,
            ref detour,
            ref original);
    }

    public void Uninstall()
    {
        SharedRuntime.FreeDetour(ref detour, ref original);
    }

    private void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        afterOriginal(self, source);
    }
}