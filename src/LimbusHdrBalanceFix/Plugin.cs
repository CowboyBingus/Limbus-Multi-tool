using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using LimbusShared;

namespace LimbusHdrBalanceFix;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbushdrbalancefix";
    public const string NAME = "LimbusHdrBalanceFix";
    public const string VERSION = "0.3.0";
    private const string BloomSection = "URP Bloom";

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

    private static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    private static ConfigEntry<float> ReapplyIntervalSeconds => Required(reapplyIntervalSeconds, nameof(ReapplyIntervalSeconds));
    private static ConfigEntry<bool> ApplyHdrOutputSettings => Required(applyHdrOutputSettings, nameof(ApplyHdrOutputSettings));
    private static ConfigEntry<bool> DisableAutomaticHdrTonemapping => Required(disableAutomaticHdrTonemapping, nameof(DisableAutomaticHdrTonemapping));
    private static ConfigEntry<float> PaperWhiteNits => Required(paperWhiteNits, nameof(PaperWhiteNits));
    private static ConfigEntry<bool> ClampBloom => Required(clampBloom, nameof(ClampBloom));
    private static ConfigEntry<float> BloomThresholdMin => Required(bloomThresholdMin, nameof(BloomThresholdMin));
    private static ConfigEntry<float> BloomIntensityMax => Required(bloomIntensityMax, nameof(BloomIntensityMax));
    private static ConfigEntry<float> BloomScatterMax => Required(bloomScatterMax, nameof(BloomScatterMax));
    private static ConfigEntry<bool> ClampColorAdjustments => Required(clampColorAdjustments, nameof(ClampColorAdjustments));
    private static ConfigEntry<float> PostExposureMaxEv => Required(postExposureMaxEv, nameof(PostExposureMaxEv));
    private static ConfigEntry<int> TonemappingMode => Required(tonemappingMode, nameof(TonemappingMode));
    private static ConfigEntry<bool> ForceParameterOverrides => Required(forceParameterOverrides, nameof(ForceParameterOverrides));
    private static ConfigEntry<bool> DebugLogging => Required(debugLogging, nameof(DebugLogging));

    public override void Load()
    {
        BindConfig(Config);
        HdrBalanceHost.Initialize(
            base.Log,
            new HdrBalanceSettings
            {
                Enabled = Enabled,
                ReapplyIntervalSeconds = ReapplyIntervalSeconds,
                ApplyHdrOutputSettings = ApplyHdrOutputSettings,
                DisableAutomaticHdrTonemapping = DisableAutomaticHdrTonemapping,
                PaperWhiteNits = PaperWhiteNits,
                ClampBloom = ClampBloom,
                BloomThresholdMin = BloomThresholdMin,
                BloomIntensityMax = BloomIntensityMax,
                BloomScatterMax = BloomScatterMax,
                ClampColorAdjustments = ClampColorAdjustments,
                PostExposureMaxEv = PostExposureMaxEv,
                TonemappingMode = TonemappingMode,
                ForceParameterOverrides = ForceParameterOverrides,
                DebugLogging = DebugLogging
            });

        HdrBalanceHost.Log.LogInfo($"{NAME} {VERSION} loading...");

        HdrOutputSettingsPatcher.Apply("Load", forceLog: true);
        HdrBalanceDetours.Install();

        HdrBalanceHost.Log.LogInfo(
            $"{NAME} {VERSION} loaded. Enabled={Enabled.Value}, PaperWhiteNits={PaperWhiteNits.Value:0.#}, " +
            $"ClampBloom={ClampBloom.Value}, BloomThresholdMin={BloomThresholdMin.Value:0.###}, " +
            $"BloomIntensityMax={BloomIntensityMax.Value:0.###}, BloomScatterMax={BloomScatterMax.Value:0.###}, " +
            $"ClampColorAdjustments={ClampColorAdjustments.Value}, PostExposureMaxEv={PostExposureMaxEv.Value:0.###}, " +
            $"TonemappingMode={TonemappingMode.Value}.");
    }

    public override bool Unload()
    {
        HdrBalanceDetours.Uninstall();
        VolumeProfilePatcher.Reset();
        return true;
    }

    private static void BindConfig(ConfigFile config)
    {
        enabled = config.Bind("General", "Enabled", true, "Master switch for HDR highlight balancing.");
        reapplyIntervalSeconds = config.Bind("General", "ReapplyIntervalSeconds", 1.0f, "How often the runtime pump reapplies HDR output and known volume-profile clamps.");
        applyHdrOutputSettings = config.Bind("HDR output", "ApplyHdrOutputSettings", true, "Applies Unity HDR output paper-white settings without requesting HDR off.");
        disableAutomaticHdrTonemapping = config.Bind("HDR output", "DisableAutomaticHdrTonemapping", true, "Disables Unity's automatic HDR tonemapping so display auto-detection does not over-brighten SDR whites.");
        paperWhiteNits = config.Bind("HDR output", "PaperWhiteNits", 160f, "Manual HDR paper-white target. Lower values dim SDR whites on HDR displays. Typical values are 120-220.");
        clampBloom = config.Bind(BloomSection, "ClampBloom", true, "Clamps URP Bloom volume parameters so white UI/art does not bloom into detail-destroying highlights.");
        bloomThresholdMin = config.Bind(BloomSection, "BloomThresholdMin", 1.05f, "Minimum Bloom threshold. Values above 1.0 keep ordinary SDR white from feeding bloom.");
        bloomIntensityMax = config.Bind(BloomSection, "BloomIntensityMax", 0.35f, "Maximum Bloom intensity.");
        bloomScatterMax = config.Bind(BloomSection, "BloomScatterMax", 0.45f, "Maximum Bloom scatter.");
        clampColorAdjustments = config.Bind("URP Color", "ClampColorAdjustments", true, "Caps URP ColorAdjustments post exposure when present.");
        postExposureMaxEv = config.Bind("URP Color", "PostExposureMaxEv", -0.20f, "Maximum post exposure in EV. Negative values gently compress highlights before tonemapping.");
        tonemappingMode = config.Bind("URP Tonemapping", "TonemappingMode", -1, "URP Tonemapping mode override. -1 leaves the game value unchanged. Unity URP commonly uses 0=None, 1=Neutral, 2=ACES.");
        forceParameterOverrides = config.Bind("URP Volume", "ForceParameterOverrides", true, "Forces patched volume parameters to override so clamps apply even if the source profile left the override flag off.");
        debugLogging = config.Bind("Diagnostics", "DebugLogging", false, "Writes additional field/method resolution and patch diagnostics.");
    }

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name) => SharedRuntime.Required(entry, NAME, name);
}
