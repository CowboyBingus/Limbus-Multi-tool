using BepInEx.Configuration;
using BepInEx.Logging;
using LimbusShared;
using System;

namespace LimbusHdrBalanceFix;

internal static class HdrBalanceHost
{
    public const string Name = "LimbusHdrBalanceFix";

    private static ManualLogSource? log;
    private static HdrBalanceSettings? settings;

    public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{Name} logging is not initialized.");
    public static ConfigEntry<bool> Enabled => Settings.Enabled;
    public static ConfigEntry<float> ReapplyIntervalSeconds => Settings.ReapplyIntervalSeconds;
    public static ConfigEntry<bool> ApplyHdrOutputSettings => Settings.ApplyHdrOutputSettings;
    public static ConfigEntry<bool> DisableAutomaticHdrTonemapping => Settings.DisableAutomaticHdrTonemapping;
    public static ConfigEntry<float> PaperWhiteNits => Settings.PaperWhiteNits;
    public static ConfigEntry<bool> ClampBloom => Settings.ClampBloom;
    public static ConfigEntry<float> BloomThresholdMin => Settings.BloomThresholdMin;
    public static ConfigEntry<float> BloomIntensityMax => Settings.BloomIntensityMax;
    public static ConfigEntry<float> BloomScatterMax => Settings.BloomScatterMax;
    public static ConfigEntry<bool> ClampColorAdjustments => Settings.ClampColorAdjustments;
    public static ConfigEntry<float> PostExposureMaxEv => Settings.PostExposureMaxEv;
    public static ConfigEntry<int> TonemappingMode => Settings.TonemappingMode;
    public static ConfigEntry<bool> ForceParameterOverrides => Settings.ForceParameterOverrides;
    public static ConfigEntry<bool> DebugLogging => Settings.DebugLogging;

    public static bool IsEnabled => SharedRuntime.IsSet(settings?.Enabled);

    public static void Initialize(ManualLogSource source, HdrBalanceSettings hostSettings)
    {
        log = source;
        settings = hostSettings;
    }

    public static void Debug(string message)
    {
        if (SharedRuntime.IsSet(settings?.DebugLogging))
            Log.LogInfo($"[debug] {message}");
    }

    private static HdrBalanceSettings Settings =>
        settings ?? throw new InvalidOperationException($"{Name} settings are not initialized.");
}

internal sealed class HdrBalanceSettings
{
    public ConfigEntry<bool> Enabled { get; init; } = null!;
    public ConfigEntry<float> ReapplyIntervalSeconds { get; init; } = null!;
    public ConfigEntry<bool> ApplyHdrOutputSettings { get; init; } = null!;
    public ConfigEntry<bool> DisableAutomaticHdrTonemapping { get; init; } = null!;
    public ConfigEntry<float> PaperWhiteNits { get; init; } = null!;
    public ConfigEntry<bool> ClampBloom { get; init; } = null!;
    public ConfigEntry<float> BloomThresholdMin { get; init; } = null!;
    public ConfigEntry<float> BloomIntensityMax { get; init; } = null!;
    public ConfigEntry<float> BloomScatterMax { get; init; } = null!;
    public ConfigEntry<bool> ClampColorAdjustments { get; init; } = null!;
    public ConfigEntry<float> PostExposureMaxEv { get; init; } = null!;
    public ConfigEntry<int> TonemappingMode { get; init; } = null!;
    public ConfigEntry<bool> ForceParameterOverrides { get; init; } = null!;
    public ConfigEntry<bool> DebugLogging { get; init; } = null!;
}
