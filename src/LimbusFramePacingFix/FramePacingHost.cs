using BepInEx.Configuration;
using BepInEx.Logging;
using LimbusShared;
using System;

namespace LimbusFramePacingFix;

internal static class FramePacingHost
{
    public const string Name = "LimbusFramePacingFix";
    public const string UnityCoreModule = "UnityEngine.CoreModule.dll";

    private static ManualLogSource? log;
    private static FramePacingSettings? settings;

    public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{Name} logging is not initialized.");
    public static ConfigEntry<bool> Enabled => Settings.Enabled;
    public static ConfigEntry<int> TargetFrameRate => Settings.TargetFrameRate;
    public static ConfigEntry<int> VSyncCount => Settings.VSyncCount;
    public static ConfigEntry<bool> ForceMaximizedWindow => Settings.ForceMaximizedWindow;
    public static ConfigEntry<bool> RunInBackground => Settings.RunInBackground;
    public static ConfigEntry<bool> AllowDisplayModeChanges => Settings.AllowDisplayModeChanges;
    public static ConfigEntry<bool> ForceOnDemandEveryFrame => Settings.ForceOnDemandEveryFrame;
    public static ConfigEntry<int> MaxQueuedFrames => Settings.MaxQueuedFrames;
    public static ConfigEntry<float> ReapplyIntervalSeconds => Settings.ReapplyIntervalSeconds;
    public static ConfigEntry<bool> ApplyNativeUnitySettings => Settings.ApplyNativeUnitySettings;
    public static ConfigEntry<bool> PatchGameFrameRateMethods => Settings.PatchGameFrameRateMethods;
    public static ConfigEntry<bool> DumpMetadataOnLoad => Settings.DumpMetadataOnLoad;

    public static bool IsEnabled => SharedRuntime.IsSet(settings?.Enabled);
    public static bool ShouldApplyNativeUnitySettings => IsEnabled && SharedRuntime.IsSet(settings?.ApplyNativeUnitySettings);
    public static bool ShouldPatchGameFrameRateMethods => IsEnabled && SharedRuntime.IsSet(settings?.PatchGameFrameRateMethods);
    public static bool ShouldForceMaximizedWindow =>
        IsEnabled &&
        SharedRuntime.IsSet(settings?.AllowDisplayModeChanges) &&
        SharedRuntime.IsSet(settings?.ForceMaximizedWindow);

    public static void Initialize(ManualLogSource source, FramePacingSettings hostSettings)
    {
        log = source;
        settings = hostSettings;
    }

    private static FramePacingSettings Settings =>
        settings ?? throw new InvalidOperationException($"{Name} settings are not initialized.");
}

internal sealed class FramePacingSettings
{
    public ConfigEntry<bool> Enabled { get; init; } = null!;
    public ConfigEntry<int> TargetFrameRate { get; init; } = null!;
    public ConfigEntry<int> VSyncCount { get; init; } = null!;
    public ConfigEntry<bool> ForceMaximizedWindow { get; init; } = null!;
    public ConfigEntry<bool> RunInBackground { get; init; } = null!;
    public ConfigEntry<bool> AllowDisplayModeChanges { get; init; } = null!;
    public ConfigEntry<bool> ForceOnDemandEveryFrame { get; init; } = null!;
    public ConfigEntry<int> MaxQueuedFrames { get; init; } = null!;
    public ConfigEntry<float> ReapplyIntervalSeconds { get; init; } = null!;
    public ConfigEntry<bool> ApplyNativeUnitySettings { get; init; } = null!;
    public ConfigEntry<bool> PatchGameFrameRateMethods { get; init; } = null!;
    public ConfigEntry<bool> DumpMetadataOnLoad { get; init; } = null!;
}
