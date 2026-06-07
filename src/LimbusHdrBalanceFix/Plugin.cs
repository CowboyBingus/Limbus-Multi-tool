using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LimbusHdrBalanceFix;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbushdrbalancefix";
    public const string NAME = "LimbusHdrBalanceFix";
    public const string VERSION = "0.3.0";

    internal static new ManualLogSource Log = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<float> ReapplyIntervalSeconds = null!;
    internal static ConfigEntry<bool> ApplyHdrOutputSettings = null!;
    internal static ConfigEntry<bool> DisableAutomaticHdrTonemapping = null!;
    internal static ConfigEntry<float> PaperWhiteNits = null!;
    internal static ConfigEntry<bool> ClampBloom = null!;
    internal static ConfigEntry<float> BloomThresholdMin = null!;
    internal static ConfigEntry<float> BloomIntensityMax = null!;
    internal static ConfigEntry<float> BloomScatterMax = null!;
    internal static ConfigEntry<bool> ClampColorAdjustments = null!;
    internal static ConfigEntry<float> PostExposureMaxEv = null!;
    internal static ConfigEntry<int> TonemappingMode = null!;
    internal static ConfigEntry<bool> ForceParameterOverrides = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    public override void Load()
    {
        Log = base.Log;
        BindConfig();

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

    private void BindConfig()
    {
        Enabled = Config.Bind("General", "Enabled", true, "Master switch for HDR highlight balancing.");
        ReapplyIntervalSeconds = Config.Bind("General", "ReapplyIntervalSeconds", 1.0f, "How often the runtime pump reapplies HDR output and known volume-profile clamps.");
        ApplyHdrOutputSettings = Config.Bind("HDR output", "ApplyHdrOutputSettings", true, "Applies Unity HDR output paper-white settings without requesting HDR off.");
        DisableAutomaticHdrTonemapping = Config.Bind("HDR output", "DisableAutomaticHdrTonemapping", true, "Disables Unity's automatic HDR tonemapping so display auto-detection does not over-brighten SDR whites.");
        PaperWhiteNits = Config.Bind("HDR output", "PaperWhiteNits", 160f, "Manual HDR paper-white target. Lower values dim SDR whites on HDR displays. Typical values are 120-220.");
        ClampBloom = Config.Bind("URP Bloom", "ClampBloom", true, "Clamps URP Bloom volume parameters so white UI/art does not bloom into detail-destroying highlights.");
        BloomThresholdMin = Config.Bind("URP Bloom", "BloomThresholdMin", 1.05f, "Minimum Bloom threshold. Values above 1.0 keep ordinary SDR white from feeding bloom.");
        BloomIntensityMax = Config.Bind("URP Bloom", "BloomIntensityMax", 0.35f, "Maximum Bloom intensity.");
        BloomScatterMax = Config.Bind("URP Bloom", "BloomScatterMax", 0.45f, "Maximum Bloom scatter.");
        ClampColorAdjustments = Config.Bind("URP Color", "ClampColorAdjustments", true, "Caps URP ColorAdjustments post exposure when present.");
        PostExposureMaxEv = Config.Bind("URP Color", "PostExposureMaxEv", -0.20f, "Maximum post exposure in EV. Negative values gently compress highlights before tonemapping.");
        TonemappingMode = Config.Bind("URP Tonemapping", "TonemappingMode", -1, "URP Tonemapping mode override. -1 leaves the game value unchanged. Unity URP commonly uses 0=None, 1=Neutral, 2=ACES.");
        ForceParameterOverrides = Config.Bind("URP Volume", "ForceParameterOverrides", true, "Forces patched volume parameters to override so clamps apply even if the source profile left the override flag off.");
        DebugLogging = Config.Bind("Diagnostics", "DebugLogging", false, "Writes additional field/method resolution and patch diagnostics.");
    }

    internal static bool IsEnabled => Enabled != null && Enabled.Value;

    internal static void Debug(string message)
    {
        if (DebugLogging != null && DebugLogging.Value)
            Log.LogInfo($"[debug] {message}");
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
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
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

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        VolumeProfilePatcher.ApplyToVolume(self, "Volume.OnEnable");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        VolumeProfilePatcher.ApplyToProfile(self, "VolumeProfile.OnEnable");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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

            var changes = new List<string>();
            var available = TryGetBool(getAvailable, "GetAvailable", out var availableValue) ? availableValue : (bool?)null;
            var active = TryGetBool(getActive, "GetActive", out var activeValue) ? activeValue : (bool?)null;
            bool? automatic = null;
            float? paperBefore = null;
            int? minTone = null;
            int? maxTone = null;
            int? maxFullFrame = null;

            if (available == true)
            {
                automatic = TryGetBool(getAutomaticHdrTonemapping, "GetAutomaticHDRTonemapping", out var automaticValue) ? automaticValue : (bool?)null;
                paperBefore = TryGetFloat(getPaperWhiteNits, "GetPaperWhiteNits", out var currentPaperWhite) ? currentPaperWhite : (float?)null;
                minTone = TryGetInt(getMinToneMapLuminance, "GetMinToneMapLuminance", out var minToneValue) ? minToneValue : (int?)null;
                maxTone = TryGetInt(getMaxToneMapLuminance, "GetMaxToneMapLuminance", out var maxToneValue) ? maxToneValue : (int?)null;
                maxFullFrame = TryGetInt(getMaxFullFrameToneMapLuminance, "GetMaxFullFrameToneMapLuminance", out var maxFullFrameValue) ? maxFullFrameValue : (int?)null;

                if (Plugin.DisableAutomaticHdrTonemapping.Value && automatic == true)
                {
                    if (TrySetBool(setAutomaticHdrTonemapping, "SetAutomaticHDRTonemapping", false))
                    {
                        changes.Add("automaticHDRTonemapping true->false");
                        automatic = false;
                    }
                }
                else if (Plugin.DisableAutomaticHdrTonemapping.Value && automatic == false)
                {
                    changes.Add("automaticHDRTonemapping=false");
                }

                var paperWhite = Math.Clamp(Plugin.PaperWhiteNits.Value, 80f, 500f);
                if (setPaperWhiteNits != null)
                {
                    if (!paperBefore.HasValue || Math.Abs(paperBefore.Value - paperWhite) > 0.25f)
                    {
                        if (TrySetFloat(setPaperWhiteNits, "SetPaperWhiteNits", paperWhite))
                            changes.Add(paperBefore.HasValue ? $"paperWhiteNits {paperBefore.Value:0.#}->{paperWhite:0.#}" : $"paperWhiteNits={paperWhite:0.#}");
                    }
                    else
                    {
                        changes.Add($"paperWhiteNits={paperBefore.Value:0.#}");
                    }
                }
                else
                {
                    changes.Add("paperWhiteNits skipped: SetPaperWhiteNits icall missing");
                }
            }
            else if (available == false)
            {
                changes.Add("paperWhiteNits skipped: HDR unavailable");
            }
            else
            {
                changes.Add("paperWhiteNits skipped: HDR availability unknown");
            }

            var nextCount = applyCount + 1;
            if (forceLog || nextCount <= 6 || (changes.Count > 0 && nextCount <= 20) || nextCount % 120 == 0)
            {
                applyCount = nextCount;
                Plugin.Log.LogInfo(
                    $"HDR output apply #{nextCount} ({reason}): " +
                    $"available={FormatBool(available)}, active={FormatBool(active)}, automatic={FormatBool(automatic)}, " +
                    $"paperWhite={FormatFloat(paperBefore)}, toneMap={FormatInt(minTone)}-{FormatInt(maxTone)}/full={FormatInt(maxFullFrame)}, " +
                    (changes.Count == 0 ? "no changes" : string.Join(", ", changes)) + ".");
            }
            else
            {
                applyCount = nextCount;
            }
        }
        catch (Exception ex)
        {
            ReportFailure("HDR output apply", ex);
        }
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
        result = false;
        if (getter == null)
            return false;

        try
        {
            result = getter(MainDisplayIndex);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static bool TryGetFloat(GetFloatIntDelegate? getter, string label, out float result)
    {
        result = 0f;
        if (getter == null)
            return false;

        try
        {
            result = getter(MainDisplayIndex);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static bool TryGetInt(GetIntIntDelegate? getter, string label, out int result)
    {
        result = 0;
        if (getter == null)
            return false;

        try
        {
            result = getter(MainDisplayIndex);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static bool TrySetBool(SetBoolIntDelegate? setter, string label, bool value)
    {
        if (setter == null)
            return false;

        try
        {
            setter(MainDisplayIndex, value);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure($"HDR output {label}", ex);
            return false;
        }
    }

    private static bool TrySetFloat(SetFloatIntDelegate? setter, string label, float value)
    {
        if (setter == null)
            return false;

        try
        {
            setter(MainDisplayIndex, value);
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

internal static unsafe class VolumeProfilePatcher
{
    private static readonly List<IntPtr> knownProfileHandles = new();
    private static bool initialized;
    private static IntPtr volumeClass;
    private static IntPtr volumeProfileClass;
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
            try { IL2CPP.il2cpp_gchandle_free(handle); } catch { }
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
                try { IL2CPP.il2cpp_gchandle_free(handle); } catch { }
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
                try { IL2CPP.il2cpp_gchandle_free(handle); } catch { }
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

        volumeClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "Volume");
        volumeProfileClass = IL2CPP.GetIl2CppClass("Unity.RenderPipelines.Core.Runtime.dll", "UnityEngine.Rendering", "VolumeProfile");

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
            profile = InvokeObject(volumeGetProfile, volume);
        if (profile != IntPtr.Zero)
            return profile;

        if (volumeGetSharedProfile != IntPtr.Zero)
            profile = InvokeObject(volumeGetSharedProfile, volume);
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
        value = 0f;
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), "m_Value");
            if (field == IntPtr.Zero)
                return false;

            var local = 0f;
            IL2CPP.il2cpp_field_get_value(parameter, field, &local);
            value = local;
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure("float parameter read", ex);
            return false;
        }
    }

    private static bool TrySetFloatValue(IntPtr parameter, float value)
    {
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), "m_Value");
            if (field == IntPtr.Zero)
                return false;

            var local = value;
            IL2CPP.il2cpp_field_set_value(parameter, field, &local);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure("float parameter write", ex);
            return false;
        }
    }

    private static bool TryGetIntValue(IntPtr parameter, out int value)
    {
        value = 0;
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), "m_Value");
            if (field == IntPtr.Zero)
                return false;

            var local = 0;
            IL2CPP.il2cpp_field_get_value(parameter, field, &local);
            value = local;
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure("int parameter read", ex);
            return false;
        }
    }

    private static bool TrySetIntValue(IntPtr parameter, int value)
    {
        try
        {
            var field = FindFieldInHierarchy(IL2CPP.il2cpp_object_get_class(parameter), "m_Value");
            if (field == IntPtr.Zero)
                return false;

            var local = value;
            IL2CPP.il2cpp_field_set_value(parameter, field, &local);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure("int parameter write", ex);
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

            var value = (byte)1;
            IL2CPP.il2cpp_field_set_value(parameter, field, &value);
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

        var count = InvokeInt(countMethod, list);
        for (var i = 0; i < count; i++)
        {
            var item = InvokeObjectIntArg(itemMethod, list, i);
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

    private static IntPtr InvokeObject(IntPtr method, IntPtr instance, void** args = null)
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, args, ref exception);
        if (exception != IntPtr.Zero)
            throw new InvalidOperationException($"IL2CPP invocation failed: exception=0x{exception.ToInt64():X}");
        return result;
    }

    private static int InvokeInt(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(result));
    }

    private static IntPtr InvokeObjectIntArg(IntPtr method, IntPtr instance, int value)
    {
        var args = stackalloc void*[1];
        args[0] = &value;
        return InvokeObject(method, instance, args);
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

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
}
