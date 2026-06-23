using Il2CppInterop.Runtime;
using LimbusShared.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static LimbusShared.Interop.NativeInterop;

namespace LimbusHdrBalanceFix;

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
        if (!HdrBalanceHost.IsEnabled || volume == IntPtr.Zero)
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
        if (!HdrBalanceHost.IsEnabled || profile == IntPtr.Zero)
            return;

        try
        {
            EnsureInitialized();
            if (remember)
                RememberProfile(profile);

            var components = IL2CPP.il2cpp_field_get_value_object(profileComponentsField, profile);
            if (components == IntPtr.Zero)
            {
                HdrBalanceHost.Debug($"Volume profile has no component list: {DescribeObject(profile)}");
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
                    HdrBalanceHost.Log.LogInfo(
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
        if (!HdrBalanceHost.IsEnabled || knownProfileHandles.Count == 0)
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
        HdrBalanceHost.Log.LogInfo("Resolved URP/Core volume profile APIs for HDR balance.");
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
        if (!HdrBalanceHost.ClampBloom.Value)
            return 0;

        var changed = 0;
        changed += PatchFloatParameter(component, "threshold", current => Math.Max(current, HdrBalanceHost.BloomThresholdMin.Value), "Bloom.threshold", reason);
        changed += PatchFloatParameter(component, "intensity", current => Math.Min(current, Math.Max(0f, HdrBalanceHost.BloomIntensityMax.Value)), "Bloom.intensity", reason);
        changed += PatchFloatParameter(component, "scatter", current => Math.Min(current, Math.Clamp(HdrBalanceHost.BloomScatterMax.Value, 0f, 1f)), "Bloom.scatter", reason);
        return changed;
    }

    private static int ApplyColorAdjustments(IntPtr component, string reason)
    {
        if (!HdrBalanceHost.ClampColorAdjustments.Value)
            return 0;

        return PatchFloatParameter(
            component,
            "postExposure",
            current => Math.Min(current, HdrBalanceHost.PostExposureMaxEv.Value),
            "ColorAdjustments.postExposure",
            reason);
    }

    private static int ApplyTonemapping(IntPtr component, string reason)
    {
        var mode = HdrBalanceHost.TonemappingMode.Value;
        if (mode < 0)
            return 0;

        return PatchIntParameter(component, "mode", mode, "Tonemapping.mode", reason);
    }

    private static int PatchFloatParameter(IntPtr component, string fieldName, Func<float, float> clamp, string label, string reason)
    {
        var parameter = GetObjectField(component, fieldName);
        if (parameter == IntPtr.Zero)
        {
            HdrBalanceHost.Debug($"{label} skipped: parameter field not found on {DescribeObject(component)}.");
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
            HdrBalanceHost.Debug($"{label} skipped: parameter field not found on {DescribeObject(component)}.");
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
        if (!HdrBalanceHost.ForceParameterOverrides.Value || parameter == IntPtr.Zero)
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
            HdrBalanceHost.Debug($"Could not enumerate component list: count={Ptr(countMethod)}, item={Ptr(itemMethod)}, list={DescribeObject(list)}.");
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
            HdrBalanceHost.Log.LogInfo($"HDR balance {label}: {before:0.###}->{after:0.###} ({reason}).");
    }

    private static void LogParameterChange(string label, int before, int after, string reason)
    {
        var count = ++parameterChangeLogCount;
        if (count <= 32 || count % 500 == 0)
            HdrBalanceHost.Log.LogInfo($"HDR balance {label}: {before}->{after} ({reason}).");
    }

    private static void ReportFailure(string phase, Exception ex)
    {
        var count = ++failureCount;
        if (count <= 12 || count % 500 == 0)
            HdrBalanceHost.Log.LogDebug($"HDR volume {phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
    }

}
