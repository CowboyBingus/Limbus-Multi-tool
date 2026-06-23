using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using LimbusShared;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static LimbusShared.NativeInterop;

namespace LimbusHdrBalanceFix;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbushdrbalancefix";
    public const string NAME = "LimbusHdrBalanceFix";
    public const string VERSION = "0.3.0";
    private const string BloomSection = "URP Bloom";

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

    internal static new ManualLogSource Log => log ?? throw new InvalidOperationException($"{NAME} logging is not initialized.");
    internal static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    internal static ConfigEntry<float> ReapplyIntervalSeconds => Required(reapplyIntervalSeconds, nameof(ReapplyIntervalSeconds));
    internal static ConfigEntry<bool> ApplyHdrOutputSettings => Required(applyHdrOutputSettings, nameof(ApplyHdrOutputSettings));
    internal static ConfigEntry<bool> DisableAutomaticHdrTonemapping => Required(disableAutomaticHdrTonemapping, nameof(DisableAutomaticHdrTonemapping));
    internal static ConfigEntry<float> PaperWhiteNits => Required(paperWhiteNits, nameof(PaperWhiteNits));
    internal static ConfigEntry<bool> ClampBloom => Required(clampBloom, nameof(ClampBloom));
    internal static ConfigEntry<float> BloomThresholdMin => Required(bloomThresholdMin, nameof(BloomThresholdMin));
    internal static ConfigEntry<float> BloomIntensityMax => Required(bloomIntensityMax, nameof(BloomIntensityMax));
    internal static ConfigEntry<float> BloomScatterMax => Required(bloomScatterMax, nameof(BloomScatterMax));
    internal static ConfigEntry<bool> ClampColorAdjustments => Required(clampColorAdjustments, nameof(ClampColorAdjustments));
    internal static ConfigEntry<float> PostExposureMaxEv => Required(postExposureMaxEv, nameof(PostExposureMaxEv));
    internal static ConfigEntry<int> TonemappingMode => Required(tonemappingMode, nameof(TonemappingMode));
    internal static ConfigEntry<bool> ForceParameterOverrides => Required(forceParameterOverrides, nameof(ForceParameterOverrides));
    internal static ConfigEntry<bool> DebugLogging => Required(debugLogging, nameof(DebugLogging));

    public override void Load()
    {
        InitializeLog(base.Log);
        BindConfig(Config);

        Log.LogInfo($"{NAME} {VERSION} loading...");

        HdrOutputSettingsPatcher.Apply("Plugin.Load", forceLog: true);
        VolumeOnEnableDetour.Install();
        VolumeProfileOnEnableDetour.Install();
        CanvasRenderPumpDetour.Install();

        Log.LogInfo(
            $"{NAME} {VERSION} loaded. Enabled={Enabled.Value}, PaperWhiteNits={PaperWhiteNits.Value:0.#}, " +
            $"ClampBloom={ClampBloom.Value}, BloomThresholdMin={BloomThresholdMin.Value:0.###}, " +
            $"BloomIntensityMax={BloomIntensityMax.Value:0.###}, BloomScatterMax={BloomScatterMax.Value:0.###}, " +
            $"ClampColorAdjustments={ClampColorAdjustments.Value}, PostExposureMaxEv={PostExposureMaxEv.Value:0.###}, " +
            $"TonemappingMode={TonemappingMode.Value}.");
    }

    public override bool Unload()
    {
        CanvasRenderPumpDetour.Uninstall();
        VolumeProfileOnEnableDetour.Uninstall();
        VolumeOnEnableDetour.Uninstall();
        VolumeProfilePatcher.Reset();
        return true;
    }

    private static void InitializeLog(ManualLogSource source)
    {
        log = source;
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

    internal static bool IsEnabled => IsSet(enabled);

    internal static void Debug(string message)
    {
        if (IsSet(debugLogging))
            Log.LogInfo($"[debug] {message}");
    }

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name) => PluginConfig.Required(entry, NAME, name);

    private static bool IsSet(ConfigEntry<bool>? entry) => PluginConfig.IsSet(entry);
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
        if (detour != null)
            return;

        try
        {
            var canvasClass = IL2CPP.GetIl2CppClass("UnityEngine.UIModule.dll", "UnityEngine", "Canvas");
            if (canvasClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("HDR balance pump skipped: UnityEngine.Canvas class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(canvasClass, "SendWillRenderCanvases", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("HDR balance pump skipped: Canvas.SendWillRenderCanvases was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("HDR balance pump skipped: method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<StaticVoidDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"HDR balance pump installed at {Ptr(methodPointer)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"HDR balance pump install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        DetourLifecycle.Free(ref detour, ref original);
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
        if (!Plugin.IsEnabled)
            return;

        var interval = Math.Max(0.05f, Plugin.ReapplyIntervalSeconds.Value);
        var now = DateTime.UtcNow;
        if (now < nextApplyUtc)
            return;

        nextApplyUtc = now.AddSeconds(interval);
        HdrOutputSettingsPatcher.Apply("Canvas.SendWillRenderCanvases", forceLog: false);
        VolumeProfilePatcher.ReapplyKnownProfiles("Canvas.SendWillRenderCanvases");
    }

}

internal static class VolumeOnEnableDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InstanceVoidDelegate(IntPtr self, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static InstanceVoidDelegate? original;
    private static readonly InstanceVoidDelegate replacement = Replacement;

    public static void Install()
    {
        if (detour != null)
            return;

        try
        {
            var volumeClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "Volume");
            if (volumeClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Volume.OnEnable HDR balance detour skipped: class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(volumeClass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Volume.OnEnable HDR balance detour skipped: method was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Volume.OnEnable HDR balance detour skipped: method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<InstanceVoidDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"Volume.OnEnable HDR balance detour installed at {Ptr(methodPointer)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Volume.OnEnable HDR balance detour install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        DetourLifecycle.Free(ref detour, ref original);
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        VolumeProfilePatcher.ApplyToVolume(self, "Volume.OnEnable");
    }

}

internal static class VolumeProfileOnEnableDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InstanceVoidDelegate(IntPtr self, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static InstanceVoidDelegate? original;
    private static readonly InstanceVoidDelegate replacement = Replacement;

    public static void Install()
    {
        if (detour != null)
            return;

        try
        {
            var profileClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "VolumeProfile");
            if (profileClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("VolumeProfile.OnEnable HDR balance detour skipped: class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(profileClass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("VolumeProfile.OnEnable HDR balance detour skipped: method was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("VolumeProfile.OnEnable HDR balance detour skipped: method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<InstanceVoidDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"VolumeProfile.OnEnable HDR balance detour installed at {Ptr(methodPointer)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"VolumeProfile.OnEnable HDR balance detour install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        DetourLifecycle.Free(ref detour, ref original);
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        VolumeProfilePatcher.ApplyToProfile(self, "VolumeProfile.OnEnable");
    }

}

internal static class HdrOutputSettingsPatcher
{
    private const int MainDisplayIndex = 0;

    private static bool initialized;
    private static GetBoolIntDelegate? getActive;
    private static GetBoolIntDelegate? getAvailable;
    private static GetBoolIntDelegate? getAutomaticHdrTonemapping;
    private static SetBoolIntDelegate? setAutomaticHdrTonemapping;
    private static GetFloatIntDelegate? getPaperWhiteNits;
    private static SetFloatIntDelegate? setPaperWhiteNits;
    private static GetIntIntDelegate? getMinToneMapLuminance;
    private static GetIntIntDelegate? getMaxToneMapLuminance;
    private static GetIntIntDelegate? getMaxFullFrameToneMapLuminance;
    private static int applyCount;
    private static int failureCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetBoolIntDelegate(int displayIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBoolIntDelegate(int displayIndex, [MarshalAs(UnmanagedType.I1)] bool value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetFloatIntDelegate(int displayIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFloatIntDelegate(int displayIndex, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetIntIntDelegate(int displayIndex);

    public static void Apply(string reason, bool forceLog)
    {
        if (!Plugin.IsEnabled || !Plugin.ApplyHdrOutputSettings.Value)
            return;

        try
        {
            EnsureInitialized();

            var state = ReadState();
            ApplyConfiguredChanges(state);
            LogApply(reason, forceLog, state);
        }
        catch (Exception ex)
        {
            ReportFailure("HDR output apply", ex);
        }
    }

    private static HdrOutputState ReadState()
    {
        var state = new HdrOutputState
        {
            Available = TryGetBool(getAvailable, "GetAvailable", out var availableValue) ? (bool?)availableValue : null,
            Active = TryGetBool(getActive, "GetActive", out var activeValue) ? (bool?)activeValue : null
        };

        if (state.Available != true)
            return state;

        state.Automatic = TryGetBool(getAutomaticHdrTonemapping, "GetAutomaticHDRTonemapping", out var automaticValue) ? (bool?)automaticValue : null;
        state.PaperWhiteBefore = TryGetFloat(getPaperWhiteNits, "GetPaperWhiteNits", out var currentPaperWhite) ? (float?)currentPaperWhite : null;
        state.MinTone = TryGetInt(getMinToneMapLuminance, "GetMinToneMapLuminance", out var minToneValue) ? (int?)minToneValue : null;
        state.MaxTone = TryGetInt(getMaxToneMapLuminance, "GetMaxToneMapLuminance", out var maxToneValue) ? (int?)maxToneValue : null;
        state.MaxFullFrameTone = TryGetInt(getMaxFullFrameToneMapLuminance, "GetMaxFullFrameToneMapLuminance", out var maxFullFrameValue) ? (int?)maxFullFrameValue : null;
        return state;
    }

    private static void ApplyConfiguredChanges(HdrOutputState state)
    {
        if (state.Available == false)
        {
            state.Changes.Add("paperWhiteNits skipped: HDR unavailable");
            return;
        }

        if (state.Available != true)
        {
            state.Changes.Add("paperWhiteNits skipped: HDR availability unknown");
            return;
        }

        ApplyAutomaticTonemappingSetting(state);
        ApplyPaperWhiteSetting(state);
    }

    private static void ApplyAutomaticTonemappingSetting(HdrOutputState state)
    {
        if (!Plugin.DisableAutomaticHdrTonemapping.Value)
            return;

        if (state.Automatic == false)
        {
            state.Changes.Add("automaticHDRTonemapping=false");
            return;
        }

        if (state.Automatic == true && TrySetBool(setAutomaticHdrTonemapping, "SetAutomaticHDRTonemapping", false))
        {
            state.Changes.Add("automaticHDRTonemapping true->false");
            state.Automatic = false;
        }
    }

    private static void ApplyPaperWhiteSetting(HdrOutputState state)
    {
        var paperWhite = Math.Clamp(Plugin.PaperWhiteNits.Value, 80f, 500f);
        if (setPaperWhiteNits == null)
        {
            state.Changes.Add("paperWhiteNits skipped: SetPaperWhiteNits icall missing");
            return;
        }

        if (state.PaperWhiteBefore.HasValue && Math.Abs(state.PaperWhiteBefore.Value - paperWhite) <= 0.25f)
        {
            state.Changes.Add($"paperWhiteNits={state.PaperWhiteBefore.Value:0.#}");
            return;
        }

        if (TrySetFloat(setPaperWhiteNits, "SetPaperWhiteNits", paperWhite))
        {
            state.Changes.Add(state.PaperWhiteBefore.HasValue
                ? $"paperWhiteNits {state.PaperWhiteBefore.Value:0.#}->{paperWhite:0.#}"
                : $"paperWhiteNits={paperWhite:0.#}");
        }
    }

    private static void LogApply(string reason, bool forceLog, HdrOutputState state)
    {
        var nextCount = applyCount + 1;
        applyCount = nextCount;
        if (!forceLog && nextCount > 6 && (state.Changes.Count == 0 || nextCount > 20) && nextCount % 120 != 0)
            return;

        Plugin.Log.LogInfo(
            $"HDR output apply #{nextCount} ({reason}): " +
            $"available={FormatBool(state.Available)}, active={FormatBool(state.Active)}, automatic={FormatBool(state.Automatic)}, " +
            $"paperWhite={FormatFloat(state.PaperWhiteBefore)}, toneMap={FormatInt(state.MinTone)}-{FormatInt(state.MaxTone)}/full={FormatInt(state.MaxFullFrameTone)}, " +
            (state.Changes.Count == 0 ? "no changes" : string.Join(", ", state.Changes)) + ".");
    }

    private sealed class HdrOutputState
    {
        public bool? Available { get; set; }
        public bool? Active { get; set; }
        public bool? Automatic { get; set; }
        public float? PaperWhiteBefore { get; set; }
        public int? MinTone { get; set; }
        public int? MaxTone { get; set; }
        public int? MaxFullFrameTone { get; set; }
        public List<string> Changes { get; } = new();
    }

    private static void EnsureInitialized()
    {
        if (initialized)
            return;

        var statuses = new List<string>();
        getActive = ResolveNative<GetBoolIntDelegate>("UnityEngine.HDROutputSettings::GetActive", statuses);
        getAvailable = ResolveNative<GetBoolIntDelegate>("UnityEngine.HDROutputSettings::GetAvailable", statuses);
        getAutomaticHdrTonemapping = ResolveNative<GetBoolIntDelegate>("UnityEngine.HDROutputSettings::GetAutomaticHDRTonemapping", statuses);
        setAutomaticHdrTonemapping = ResolveNative<SetBoolIntDelegate>("UnityEngine.HDROutputSettings::SetAutomaticHDRTonemapping", statuses);
        getPaperWhiteNits = ResolveNative<GetFloatIntDelegate>("UnityEngine.HDROutputSettings::GetPaperWhiteNits", statuses);
        setPaperWhiteNits = ResolveNative<SetFloatIntDelegate>("UnityEngine.HDROutputSettings::SetPaperWhiteNits", statuses);
        getMinToneMapLuminance = ResolveNative<GetIntIntDelegate>("UnityEngine.HDROutputSettings::GetMinToneMapLuminance", statuses);
        getMaxToneMapLuminance = ResolveNative<GetIntIntDelegate>("UnityEngine.HDROutputSettings::GetMaxToneMapLuminance", statuses);
        getMaxFullFrameToneMapLuminance = ResolveNative<GetIntIntDelegate>("UnityEngine.HDROutputSettings::GetMaxFullFrameToneMapLuminance", statuses);

        initialized = true;
        Plugin.Log.LogInfo("Resolved Unity HDR output icalls: " + string.Join(", ", statuses) + ".");
    }

    private static T? ResolveNative<T>(string name, List<string> statuses) where T : Delegate
    {
        var ptr = IL2CPP.il2cpp_resolve_icall(name);
        var shortName = name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        if (ptr == IntPtr.Zero)
        {
            statuses.Add($"{shortName}=missing");
            return null;
        }

        statuses.Add($"{shortName}=ok");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static bool TryGetBool(GetBoolIntDelegate? getter, string label, out bool result)
    {
        return TryGetValue(getter, label, false, out result, invoke: g => g(MainDisplayIndex));
    }

    private static bool TryGetFloat(GetFloatIntDelegate? getter, string label, out float result)
    {
        return TryGetValue(getter, label, 0f, out result, invoke: g => g(MainDisplayIndex));
    }

    private static bool TryGetInt(GetIntIntDelegate? getter, string label, out int result)
    {
        return TryGetValue(getter, label, 0, out result, invoke: g => g(MainDisplayIndex));
    }

    private static bool TrySetBool(SetBoolIntDelegate? setter, string label, bool value)
    {
        return TrySetValue(setter, label, value, invoke: (s, v) => s(MainDisplayIndex, v));
    }

    private static bool TrySetFloat(SetFloatIntDelegate? setter, string label, float value)
    {
        return TrySetValue(setter, label, value, invoke: (s, v) => s(MainDisplayIndex, v));
    }

    private static bool TryGetValue<TDelegate, TValue>(
        TDelegate? getter,
        string label,
        TValue defaultValue,
        out TValue result,
        Func<TDelegate, TValue> invoke)
        where TDelegate : Delegate
    {
        result = defaultValue;
        if (getter == null)
            return false;

        try
        {
            result = invoke(getter);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static bool TrySetValue<TDelegate, TValue>(
        TDelegate? setter,
        string label,
        TValue value,
        Action<TDelegate, TValue> invoke)
        where TDelegate : Delegate
    {
        if (setter == null)
            return false;

        try
        {
            invoke(setter, value);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static void ReportFailure(string phase, Exception ex)
    {
        var count = ++failureCount;
        if (count <= 6)
            Plugin.Log.LogWarning($"{phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
        else if (count % 200 == 0)
            Plugin.Log.LogDebug($"{phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
    }

    private static string FormatBool(bool? value) => value.HasValue ? value.Value.ToString() : "unknown";
    private static string FormatFloat(float? value) => value.HasValue ? value.Value.ToString("0.#") : "unknown";
    private static string FormatInt(int? value) => value.HasValue ? value.Value.ToString() : "unknown";
}

internal static class VolumeProfilePatcher
{
    private const string SerializedValueField = "m_Value";
    private static readonly List<IntPtr> knownProfileHandles = new();
    private static bool initialized;
    private static IntPtr volumeGetProfile;
    private static IntPtr volumeGetSharedProfile;
    private static IntPtr profileComponentsField;
    private static int profileApplyCount;
    private static int parameterChangeLogCount;
    private static int failureCount;

    public static void Reset()
    {
        foreach (var handle in knownProfileHandles)
        {
            try { IL2CPP.il2cpp_gchandle_free(handle); } catch { /* Weak handle cleanup is best-effort during plugin unload. */ }
        }

        knownProfileHandles.Clear();
        profileApplyCount = 0;
        parameterChangeLogCount = 0;
        failureCount = 0;
    }

    public static void ApplyToVolume(IntPtr volume, string reason)
    {
        if (!Plugin.IsEnabled || volume == IntPtr.Zero)
            return;

        try
        {
            EnsureInitialized();
            var profile = GetProfileFromVolume(volume);
            ApplyToProfile(profile, reason);
        }
        catch (Exception ex)
        {
            ReportFailure("volume patch", ex);
        }
    }

    public static void ApplyToProfile(IntPtr profile, string reason) => ApplyToProfile(profile, reason, remember: true);

    private static void ApplyToProfile(IntPtr profile, string reason, bool remember)
    {
        if (!Plugin.IsEnabled || profile == IntPtr.Zero)
            return;

        try
        {
            EnsureInitialized();
            if (remember)
                RememberProfile(profile);

            var components = IL2CPP.il2cpp_field_get_value_object(profileComponentsField, profile);
            if (components == IntPtr.Zero)
            {
                Plugin.Debug($"Volume profile has no component list: {DescribeObject(profile)}");
                return;
            }

            var changed = 0;
            var visited = 0;
            foreach (var component in EnumerateList(components))
            {
                if (component == IntPtr.Zero)
                    continue;

                visited++;
                changed += ApplyToComponent(component, reason);
            }

            if (changed > 0)
            {
                var count = ++profileApplyCount;
                if (count <= 20 || count % 200 == 0)
                {
                    Plugin.Log.LogInfo(
                        $"HDR volume profile patched #{count} ({reason}): components={visited}, changes={changed}, knownProfiles={knownProfileHandles.Count}.");
                }
            }
        }
        catch (Exception ex)
        {
            ReportFailure("profile patch", ex);
        }
    }

    public static void ReapplyKnownProfiles(string reason)
    {
        if (!Plugin.IsEnabled || knownProfileHandles.Count == 0)
            return;

        for (var i = knownProfileHandles.Count - 1; i >= 0; i--)
        {
            var handle = knownProfileHandles[i];
            var profile = IntPtr.Zero;
            try
            {
                profile = IL2CPP.il2cpp_gchandle_get_target(handle);
            }
            catch (Exception ex)
            {
                ReportFailure("weak profile target lookup", ex);
            }

            if (profile == IntPtr.Zero)
            {
                try { IL2CPP.il2cpp_gchandle_free(handle); } catch { /* Stale weak handles are removed from the cache even if native cleanup fails. */ }
                knownProfileHandles.RemoveAt(i);
                continue;
            }

            ApplyToProfile(profile, reason, remember: false);
        }
    }

    private static void RememberProfile(IntPtr profile)
    {
        for (var i = knownProfileHandles.Count - 1; i >= 0; i--)
        {
            var handle = knownProfileHandles[i];
            var target = IntPtr.Zero;
            try
            {
                target = IL2CPP.il2cpp_gchandle_get_target(handle);
            }
            catch
            {
                // Drop bad handles; they are only a convenience cache.
            }

            if (target == IntPtr.Zero)
            {
                try { IL2CPP.il2cpp_gchandle_free(handle); } catch { /* Stale weak handles are removed from the cache even if native cleanup fails. */ }
                knownProfileHandles.RemoveAt(i);
                continue;
            }

            if (target == profile)
                return;
        }

        try
        {
            knownProfileHandles.Add(IL2CPP.il2cpp_gchandle_new_weakref(profile, false));
        }
        catch (Exception ex)
        {
            ReportFailure("weak profile handle create", ex);
        }
    }

    private static void EnsureInitialized()
    {
        if (initialized)
            return;

        var volumeClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "Volume");
        var volumeProfileClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "VolumeProfile");

        if (volumeClass == IntPtr.Zero)
            throw new MissingMemberException("UnityEngine.Rendering.Volume class was not resolved.");
        if (volumeProfileClass == IntPtr.Zero)
            throw new MissingMemberException("UnityEngine.Rendering.VolumeProfile class was not resolved.");

        volumeGetProfile = IL2CPP.il2cpp_class_get_method_from_name(volumeClass, "get_profile", 0);
        volumeGetSharedProfile = IL2CPP.il2cpp_class_get_method_from_name(volumeClass, "get_sharedProfile", 0);
        profileComponentsField = FindFirstField(volumeProfileClass, "components", "m_Components");

        if (profileComponentsField == IntPtr.Zero)
            throw new MissingFieldException("VolumeProfile.components field was not resolved.");

        initialized = true;
        Plugin.Log.LogInfo("Resolved URP/Core volume profile APIs for HDR balance.");
    }

    private static IntPtr GetProfileFromVolume(IntPtr volume)
    {
        var profile = IntPtr.Zero;
        if (volumeGetProfile != IntPtr.Zero)
            profile = Il2CppInvoke.Object(volumeGetProfile, volume);
        if (profile != IntPtr.Zero)
            return profile;

        if (volumeGetSharedProfile != IntPtr.Zero)
            profile = Il2CppInvoke.Object(volumeGetSharedProfile, volume);
        if (profile != IntPtr.Zero)
            return profile;

        return GetObjectField(volume, "sharedProfile", "m_SharedProfile", "profile", "m_Profile");
    }

    private static int ApplyToComponent(IntPtr component, string reason)
    {
        var className = GetClassName(component);
        if (className.EndsWith(".Bloom", StringComparison.Ordinal) || className == "Bloom")
            return ApplyBloom(component, reason);

        if (className.EndsWith(".ColorAdjustments", StringComparison.Ordinal) || className == "ColorAdjustments")
            return ApplyColorAdjustments(component, reason);

        if (className.EndsWith(".Tonemapping", StringComparison.Ordinal) || className == "Tonemapping")
            return ApplyTonemapping(component, reason);

        return 0;
    }

    private static int ApplyBloom(IntPtr component, string reason)
    {
        if (!Plugin.ClampBloom.Value)
            return 0;

        var changed = 0;
        changed += PatchFloatParameter(component, "threshold", current => Math.Max(current, Plugin.BloomThresholdMin.Value), "Bloom.threshold", reason);
        changed += PatchFloatParameter(component, "intensity", current => Math.Min(current, Math.Max(0f, Plugin.BloomIntensityMax.Value)), "Bloom.intensity", reason);
        changed += PatchFloatParameter(component, "scatter", current => Math.Min(current, Math.Clamp(Plugin.BloomScatterMax.Value, 0f, 1f)), "Bloom.scatter", reason);
        return changed;
    }

    private static int ApplyColorAdjustments(IntPtr component, string reason)
    {
        if (!Plugin.ClampColorAdjustments.Value)
            return 0;

        return PatchFloatParameter(
            component,
            "postExposure",
            current => Math.Min(current, Plugin.PostExposureMaxEv.Value),
            "ColorAdjustments.postExposure",
            reason);
    }

    private static int ApplyTonemapping(IntPtr component, string reason)
    {
        var mode = Plugin.TonemappingMode.Value;
        if (mode < 0)
            return 0;

        return PatchIntParameter(component, "mode", mode, "Tonemapping.mode", reason);
    }

    private static int PatchFloatParameter(IntPtr component, string fieldName, Func<float, float> clamp, string label, string reason)
    {
        var parameter = GetObjectField(component, fieldName);
        if (parameter == IntPtr.Zero)
        {
            Plugin.Debug($"{label} skipped: parameter field not found on {DescribeObject(component)}.");
            return 0;
        }

        if (!TryGetFloatValue(parameter, out var before))
            return 0;

        var after = clamp(before);
        ForceOverride(parameter);
        if (Math.Abs(before - after) <= 0.0005f)
            return 0;

        if (!TrySetFloatValue(parameter, after))
            return 0;

        LogParameterChange(label, before, after, reason);
        return 1;
    }

    private static int PatchIntParameter(IntPtr component, string fieldName, int value, string label, string reason)
    {
        var parameter = GetObjectField(component, fieldName);
        if (parameter == IntPtr.Zero)
        {
            Plugin.Debug($"{label} skipped: parameter field not found on {DescribeObject(component)}.");
            return 0;
        }

        if (!TryGetIntValue(parameter, out var before))
            return 0;

        ForceOverride(parameter);
        if (before == value)
            return 0;

        if (!TrySetIntValue(parameter, value))
            return 0;

        LogParameterChange(label, before, value, reason);
        return 1;
    }

    private static bool TryGetFloatValue(IntPtr parameter, out float value)
    {
        return TryGetSerializedValue(parameter, "float parameter read", 0f, out value);
    }

    private static bool TrySetFloatValue(IntPtr parameter, float value)
    {
        return TrySetSerializedValue(parameter, "float parameter write", value);
    }

    private static bool TryGetIntValue(IntPtr parameter, out int value)
    {
        return TryGetSerializedValue(parameter, "int parameter read", 0, out value);
    }

    private static bool TrySetIntValue(IntPtr parameter, int value)
    {
        return TrySetSerializedValue(parameter, "int parameter write", value);
    }

    private static bool TryGetSerializedValue<T>(IntPtr parameter, string phase, T defaultValue, out T value)
        where T : unmanaged
    {
        value = defaultValue;
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), SerializedValueField);
            if (field == IntPtr.Zero)
                return false;

            unsafe
            {
                var local = defaultValue;
                IL2CPP.il2cpp_field_get_value(parameter, field, &local);
                value = local;
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(phase, ex);
            return false;
        }
    }

    private static bool TrySetSerializedValue<T>(IntPtr parameter, string phase, T value)
        where T : unmanaged
    {
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), SerializedValueField);
            if (field == IntPtr.Zero)
                return false;

            unsafe
            {
                var local = value;
                IL2CPP.il2cpp_field_set_value(parameter, field, &local);
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(phase, ex);
            return false;
        }
    }

    private static void ForceOverride(IntPtr parameter)
    {
        if (!Plugin.ForceParameterOverrides.Value || parameter == IntPtr.Zero)
            return;

        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), "m_OverrideState");
            if (field == IntPtr.Zero)
                return;

            unsafe
            {
                var value = (byte)1;
                IL2CPP.il2cpp_field_set_value(parameter, field, &value);
            }
        }
        catch (Exception ex)
        {
            ReportFailure("override-state write", ex);
        }
    }

    private static IEnumerable<IntPtr> EnumerateList(IntPtr list)
    {
        var klass = IL2CPP.il2cpp_object_get_class(list);
        var countMethod = IL2CPP.il2cpp_class_get_method_from_name(klass, "get_Count", 0);
        var itemMethod = IL2CPP.il2cpp_class_get_method_from_name(klass, "get_Item", 1);
        if (countMethod == IntPtr.Zero || itemMethod == IntPtr.Zero)
        {
            Plugin.Debug($"Could not enumerate component list: count={Ptr(countMethod)}, item={Ptr(itemMethod)}, list={DescribeObject(list)}.");
            yield break;
        }

        var count = Il2CppInvoke.Int32(countMethod, list);
        for (var i = 0; i < count; i++)
        {
            var item = Il2CppInvoke.ObjectWithIntArg(itemMethod, list, i);
            if (item != IntPtr.Zero)
                yield return item;
        }
    }

    private static IntPtr GetObjectField(IntPtr obj, params string[] fieldNames)
    {
        if (obj == IntPtr.Zero)
            return IntPtr.Zero;

        var klass = IL2CPP.il2cpp_object_get_class(obj);
        foreach (var fieldName in fieldNames)
        {
            var field = FindFieldInHierarchy(klass, fieldName);
            if (field == IntPtr.Zero)
                continue;

            return IL2CPP.il2cpp_field_get_value_object(field, obj);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindFirstField(IntPtr klass, params string[] names)
    {
        foreach (var name in names)
        {
            var field = FindFieldInHierarchy(klass, name);
            if (field != IntPtr.Zero)
                return field;
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindFieldInHierarchy(IntPtr klass, string name)
    {
        var current = klass;
        while (current != IntPtr.Zero)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(current, name);
            if (field != IntPtr.Zero)
                return field;

            current = IL2CPP.il2cpp_class_get_parent(current);
        }

        return IntPtr.Zero;
    }

    private static string GetClassName(IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return "";

        var klass = IL2CPP.il2cpp_object_get_class(obj);
        var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) ?? "";
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string DescribeObject(IntPtr obj) => obj == IntPtr.Zero ? "null" : $"{GetClassName(obj)}@{Ptr(obj)}";

    private static void LogParameterChange(string label, float before, float after, string reason)
    {
        var count = ++parameterChangeLogCount;
        if (count <= 32 || count % 500 == 0)
            Plugin.Log.LogInfo($"HDR balance {label}: {before:0.###}->{after:0.###} ({reason}).");
    }

    private static void LogParameterChange(string label, int before, int after, string reason)
    {
        var count = ++parameterChangeLogCount;
        if (count <= 32 || count % 500 == 0)
            Plugin.Log.LogInfo($"HDR balance {label}: {before}->{after} ({reason}).");
    }

    private static void ReportFailure(string phase, Exception ex)
    {
        var count = ++failureCount;
        if (count <= 12 || count % 500 == 0)
            Plugin.Log.LogDebug($"HDR volume {phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
    }

}
