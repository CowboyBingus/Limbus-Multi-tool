using Il2CppInterop.Runtime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LimbusHdrBalanceFix;

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
        if (!HdrBalanceHost.IsEnabled || !HdrBalanceHost.ApplyHdrOutputSettings.Value)
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
        if (!HdrBalanceHost.DisableAutomaticHdrTonemapping.Value)
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
        var paperWhite = Math.Clamp(HdrBalanceHost.PaperWhiteNits.Value, 80f, 500f);
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

        HdrBalanceHost.Log.LogInfo(
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
        HdrBalanceHost.Log.LogInfo("Resolved Unity HDR output icalls: " + string.Join(", ", statuses) + ".");
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
            HdrBalanceHost.Log.LogWarning($"{phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
        else if (count % 200 == 0)
            HdrBalanceHost.Log.LogDebug($"{phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
    }

    private static string FormatBool(bool? value) => value.HasValue ? value.Value.ToString() : "unknown";
    private static string FormatFloat(float? value) => value.HasValue ? value.Value.ToString("0.#") : "unknown";
    private static string FormatInt(int? value) => value.HasValue ? value.Value.ToString() : "unknown";
}
