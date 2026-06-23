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
    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<int>? targetFrameRate;
    private static ConfigEntry<int>? vSyncCount;
    private static ConfigEntry<bool>? forceMaximizedWindow;
    private static ConfigEntry<bool>? runInBackground;
    private static ConfigEntry<bool>? allowDisplayModeChanges;
    private static ConfigEntry<bool>? forceOnDemandEveryFrame;
    private static ConfigEntry<int>? maxQueuedFrames;
    private static ConfigEntry<float>? reapplyIntervalSeconds;
    private static ConfigEntry<bool>? applyNativeUnitySettings;
    private static ConfigEntry<bool>? patchGameFrameRateMethods;
    private static ConfigEntry<bool>? dumpMetadataOnLoad;

    public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{Name} logging is not initialized.");
    public static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    public static ConfigEntry<int> TargetFrameRate => Required(targetFrameRate, nameof(TargetFrameRate));
    public static ConfigEntry<int> VSyncCount => Required(vSyncCount, nameof(VSyncCount));
    public static ConfigEntry<bool> ForceMaximizedWindow => Required(forceMaximizedWindow, nameof(ForceMaximizedWindow));
    public static ConfigEntry<bool> RunInBackground => Required(runInBackground, nameof(RunInBackground));
    public static ConfigEntry<bool> AllowDisplayModeChanges => Required(allowDisplayModeChanges, nameof(AllowDisplayModeChanges));
    public static ConfigEntry<bool> ForceOnDemandEveryFrame => Required(forceOnDemandEveryFrame, nameof(ForceOnDemandEveryFrame));
    public static ConfigEntry<int> MaxQueuedFrames => Required(maxQueuedFrames, nameof(MaxQueuedFrames));
    public static ConfigEntry<float> ReapplyIntervalSeconds => Required(reapplyIntervalSeconds, nameof(ReapplyIntervalSeconds));
    public static ConfigEntry<bool> ApplyNativeUnitySettings => Required(applyNativeUnitySettings, nameof(ApplyNativeUnitySettings));
    public static ConfigEntry<bool> PatchGameFrameRateMethods => Required(patchGameFrameRateMethods, nameof(PatchGameFrameRateMethods));
    public static ConfigEntry<bool> DumpMetadataOnLoad => Required(dumpMetadataOnLoad, nameof(DumpMetadataOnLoad));

    public static bool IsEnabled => PluginConfig.IsSet(enabled);
    public static bool ShouldApplyNativeUnitySettings => IsEnabled && PluginConfig.IsSet(applyNativeUnitySettings);
    public static bool ShouldPatchGameFrameRateMethods => IsEnabled && PluginConfig.IsSet(patchGameFrameRateMethods);
    public static bool ShouldForceMaximizedWindow =>
        IsEnabled &&
        PluginConfig.IsSet(allowDisplayModeChanges) &&
        PluginConfig.IsSet(forceMaximizedWindow);

    public static void Initialize(
        ManualLogSource source,
        ConfigEntry<bool> enabledEntry,
        ConfigEntry<int> targetFrameRateEntry,
        ConfigEntry<int> vSyncCountEntry,
        ConfigEntry<bool> forceMaximizedWindowEntry,
        ConfigEntry<bool> runInBackgroundEntry,
        ConfigEntry<bool> allowDisplayModeChangesEntry,
        ConfigEntry<bool> forceOnDemandEveryFrameEntry,
        ConfigEntry<int> maxQueuedFramesEntry,
        ConfigEntry<float> reapplyIntervalSecondsEntry,
        ConfigEntry<bool> applyNativeUnitySettingsEntry,
        ConfigEntry<bool> patchGameFrameRateMethodsEntry,
        ConfigEntry<bool> dumpMetadataOnLoadEntry)
    {
        log = source;
        enabled = enabledEntry;
        targetFrameRate = targetFrameRateEntry;
        vSyncCount = vSyncCountEntry;
        forceMaximizedWindow = forceMaximizedWindowEntry;
        runInBackground = runInBackgroundEntry;
        allowDisplayModeChanges = allowDisplayModeChangesEntry;
        forceOnDemandEveryFrame = forceOnDemandEveryFrameEntry;
        maxQueuedFrames = maxQueuedFramesEntry;
        reapplyIntervalSeconds = reapplyIntervalSecondsEntry;
        applyNativeUnitySettings = applyNativeUnitySettingsEntry;
        patchGameFrameRateMethods = patchGameFrameRateMethodsEntry;
        dumpMetadataOnLoad = dumpMetadataOnLoadEntry;
    }

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name)
    {
        return PluginConfig.Required(entry, Name, name);
    }
}
