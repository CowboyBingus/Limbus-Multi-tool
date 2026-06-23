using BepInEx.Configuration;
using BepInEx.Logging;
using LimbusShared;
using System;

namespace LimbusHdrBalanceFix;

internal static class HdrBalanceHost
{
    public const string Name = "LimbusHdrBalanceFix";

    private static ManualLogSource? log;
    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<float>? reapplyIntervalSeconds;
    private static ConfigEntry<bool>? applyHdrOutputSettings;
    private static ConfigEntry<bool>? disableAutomaticHdrTonemapping;
    private static ConfigEntry<float>? paperWhiteNits;
    private static ConfigEntry<bool>? clampBloom;
    private static ConfigEntry<float>? bloomThresholdMin;
    private static ConfigEntry<float>? bloomIntensityMax;
    private static ConfigEntry<float>? bloomScatterMax;
    private static ConfigEntry<bool>? clampColorAdjustments;
    private static ConfigEntry<float>? postExposureMaxEv;
    private static ConfigEntry<int>? tonemappingMode;
    private static ConfigEntry<bool>? forceParameterOverrides;
    private static ConfigEntry<bool>? debugLogging;

    public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{Name} logging is not initialized.");
    public static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    public static ConfigEntry<float> ReapplyIntervalSeconds => Required(reapplyIntervalSeconds, nameof(ReapplyIntervalSeconds));
    public static ConfigEntry<bool> ApplyHdrOutputSettings => Required(applyHdrOutputSettings, nameof(ApplyHdrOutputSettings));
    public static ConfigEntry<bool> DisableAutomaticHdrTonemapping => Required(disableAutomaticHdrTonemapping, nameof(DisableAutomaticHdrTonemapping));
    public static ConfigEntry<float> PaperWhiteNits => Required(paperWhiteNits, nameof(PaperWhiteNits));
    public static ConfigEntry<bool> ClampBloom => Required(clampBloom, nameof(ClampBloom));
    public static ConfigEntry<float> BloomThresholdMin => Required(bloomThresholdMin, nameof(BloomThresholdMin));
    public static ConfigEntry<float> BloomIntensityMax => Required(bloomIntensityMax, nameof(BloomIntensityMax));
    public static ConfigEntry<float> BloomScatterMax => Required(bloomScatterMax, nameof(BloomScatterMax));
    public static ConfigEntry<bool> ClampColorAdjustments => Required(clampColorAdjustments, nameof(ClampColorAdjustments));
    public static ConfigEntry<float> PostExposureMaxEv => Required(postExposureMaxEv, nameof(PostExposureMaxEv));
    public static ConfigEntry<int> TonemappingMode => Required(tonemappingMode, nameof(TonemappingMode));
    public static ConfigEntry<bool> ForceParameterOverrides => Required(forceParameterOverrides, nameof(ForceParameterOverrides));
    public static ConfigEntry<bool> DebugLogging => Required(debugLogging, nameof(DebugLogging));

    public static bool IsEnabled => PluginConfig.IsSet(enabled);

    public static void Initialize(
        ManualLogSource source,
        ConfigEntry<bool> enabledEntry,
        ConfigEntry<float> reapplyIntervalSecondsEntry,
        ConfigEntry<bool> applyHdrOutputSettingsEntry,
        ConfigEntry<bool> disableAutomaticHdrTonemappingEntry,
        ConfigEntry<float> paperWhiteNitsEntry,
        ConfigEntry<bool> clampBloomEntry,
        ConfigEntry<float> bloomThresholdMinEntry,
        ConfigEntry<float> bloomIntensityMaxEntry,
        ConfigEntry<float> bloomScatterMaxEntry,
        ConfigEntry<bool> clampColorAdjustmentsEntry,
        ConfigEntry<float> postExposureMaxEvEntry,
        ConfigEntry<int> tonemappingModeEntry,
        ConfigEntry<bool> forceParameterOverridesEntry,
        ConfigEntry<bool> debugLoggingEntry)
    {
        log = source;
        enabled = enabledEntry;
        reapplyIntervalSeconds = reapplyIntervalSecondsEntry;
        applyHdrOutputSettings = applyHdrOutputSettingsEntry;
        disableAutomaticHdrTonemapping = disableAutomaticHdrTonemappingEntry;
        paperWhiteNits = paperWhiteNitsEntry;
        clampBloom = clampBloomEntry;
        bloomThresholdMin = bloomThresholdMinEntry;
        bloomIntensityMax = bloomIntensityMaxEntry;
        bloomScatterMax = bloomScatterMaxEntry;
        clampColorAdjustments = clampColorAdjustmentsEntry;
        postExposureMaxEv = postExposureMaxEvEntry;
        tonemappingMode = tonemappingModeEntry;
        forceParameterOverrides = forceParameterOverridesEntry;
        debugLogging = debugLoggingEntry;
    }

    public static void Debug(string message)
    {
        if (PluginConfig.IsSet(debugLogging))
            Log.LogInfo($"[debug] {message}");
    }

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name)
    {
        return PluginConfig.Required(entry, Name, name);
    }
}
