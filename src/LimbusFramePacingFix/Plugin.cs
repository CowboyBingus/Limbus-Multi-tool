using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using LimbusShared;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static LimbusShared.NativeInterop;

namespace LimbusFramePacingFix;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbusframepacingfix";
    public const string NAME = "LimbusFramePacingFix";
    public const string VERSION = "0.4.1";
    internal const string UnityCoreModule = "UnityEngine.CoreModule.dll";
    private const string FramePacingSection = "Frame pacing";
    private const string DiagnosticsSection = "Diagnostics";

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

    internal static new ManualLogSource Log => log ?? throw new InvalidOperationException($"{NAME} logging is not initialized.");
    internal static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    internal static ConfigEntry<int> TargetFrameRate => Required(targetFrameRate, nameof(TargetFrameRate));
    internal static ConfigEntry<int> VSyncCount => Required(vSyncCount, nameof(VSyncCount));
    internal static ConfigEntry<bool> ForceMaximizedWindow => Required(forceMaximizedWindow, nameof(ForceMaximizedWindow));
    internal static ConfigEntry<bool> RunInBackground => Required(runInBackground, nameof(RunInBackground));
    internal static ConfigEntry<bool> AllowDisplayModeChanges => Required(allowDisplayModeChanges, nameof(AllowDisplayModeChanges));
    internal static ConfigEntry<bool> ForceOnDemandEveryFrame => Required(forceOnDemandEveryFrame, nameof(ForceOnDemandEveryFrame));
    internal static ConfigEntry<int> MaxQueuedFrames => Required(maxQueuedFrames, nameof(MaxQueuedFrames));
    internal static ConfigEntry<float> ReapplyIntervalSeconds => Required(reapplyIntervalSeconds, nameof(ReapplyIntervalSeconds));
    internal static ConfigEntry<bool> ApplyNativeUnitySettings => Required(applyNativeUnitySettings, nameof(ApplyNativeUnitySettings));
    internal static ConfigEntry<bool> PatchGameFrameRateMethods => Required(patchGameFrameRateMethods, nameof(PatchGameFrameRateMethods));
    internal static ConfigEntry<bool> DumpMetadataOnLoad => Required(dumpMetadataOnLoad, nameof(DumpMetadataOnLoad));

    public override void Load()
    {
        InitializeLog(base.Log);
        BindConfig(Config);

        CanvasScalerFramePacingDetour.Install();
        NativeUnitySettings.InstallSetterDetours();
        GameFrameRateDetours.Install();
        FramePacingEnforcer.Apply("Plugin.Load", forceLog: true);
        if (DumpMetadataOnLoad.Value)
            Il2CppFrameDiagnostics.DumpFrameRelatedMetadata();
        Log.LogInfo(
            $"{NAME} {VERSION} loaded. " +
            $"Enabled={Enabled.Value}, TargetFrameRate={TargetFrameRate.Value}, VSyncCount={VSyncCount.Value}, " +
            $"AllowDisplayModeChanges={AllowDisplayModeChanges.Value}, ForceMaximizedWindow={ForceMaximizedWindow.Value}, RunInBackground={RunInBackground.Value}, " +
            $"ForceOnDemandEveryFrame={ForceOnDemandEveryFrame.Value}, MaxQueuedFrames={MaxQueuedFrames.Value}, " +
            $"ApplyNativeUnitySettings={ApplyNativeUnitySettings.Value}, PatchGameFrameRateMethods={PatchGameFrameRateMethods.Value}, " +
            $"DumpMetadataOnLoad={DumpMetadataOnLoad.Value}.");
    }

    public override bool Unload()
    {
        CanvasScalerFramePacingDetour.Uninstall();
        GameFrameRateDetours.Uninstall();
        NativeUnitySettings.UninstallSetterDetours();
        return true;
    }

    private static void InitializeLog(ManualLogSource source)
    {
        log = source;
    }

    private static void BindConfig(ConfigFile config)
    {
        enabled = config.Bind("General", "Enabled", true, "Master switch for all frame pacing mitigations.");
        targetFrameRate = config.Bind(FramePacingSection, "TargetFrameRate", 240, "Forced Application.targetFrameRate. Use -1 to let Unity use platform default.");
        vSyncCount = config.Bind(FramePacingSection, "VSyncCount", 0, "Forced QualitySettings.vSyncCount. 0 disables Unity VSync so the explicit FPS cap is used.");
        forceOnDemandEveryFrame = config.Bind(FramePacingSection, "ForceOnDemandEveryFrame", true, "Forces OnDemandRendering.renderFrameInterval to 1 so Unity does not intentionally skip renders.");
        maxQueuedFrames = config.Bind(FramePacingSection, "MaxQueuedFrames", 1, "Forced QualitySettings.maxQueuedFrames. Use 0 to leave unchanged.");
        allowDisplayModeChanges = config.Bind("Display", "AllowDisplayModeChanges", false, "Opt-in guard for settings that change Unity's display mode or window size. Leave false to preserve the user's selected window mode.");
        forceMaximizedWindow = config.Bind("Display", "ForceMaximizedWindow", false, "When AllowDisplayModeChanges is true, forces Screen.fullScreenMode to FullScreenWindow, Unity's borderless maximized window mode.");
        runInBackground = config.Bind("Display", "RunInBackground", false, "Forced Application.runInBackground value.");
        reapplyIntervalSeconds = config.Bind(DiagnosticsSection, "ReapplyIntervalSeconds", 0.25f, "How often the runtime enforcer checks and reapplies settings.");
        applyNativeUnitySettings = config.Bind(DiagnosticsSection, "ApplyNativeUnitySettings", true, "Experimental. Applies Unity settings through IL2CPP runtime invocation.");
        patchGameFrameRateMethods = config.Bind(DiagnosticsSection, "PatchGameFrameRateMethods", true, "Hooks Limbus' own frame-rate apply methods so game-side 144 FPS writes are followed by the forced cap.");
        dumpMetadataOnLoad = config.Bind(DiagnosticsSection, "DumpMetadataOnLoad", false, "Writes an IL2CPP frame/display metadata scan for reverse engineering. Leave off for normal play.");
    }

    internal static bool IsEnabled => IsSet(enabled);
    internal static bool ShouldApplyNativeUnitySettings => IsEnabled && IsSet(applyNativeUnitySettings);
    internal static bool ShouldPatchGameFrameRateMethods => IsEnabled && IsSet(patchGameFrameRateMethods);
    internal static bool ShouldForceMaximizedWindow =>
        IsEnabled &&
        IsSet(allowDisplayModeChanges) &&
        IsSet(forceMaximizedWindow);

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name) => PluginConfig.Required(entry, NAME, name);

    private static bool IsSet(ConfigEntry<bool>? entry) => PluginConfig.IsSet(entry);
}

internal static class FramePacingEnforcer
{
    private const int FullScreenWindowMode = 1;
    private static DateTime nextApplyUtc = DateTime.MinValue;

    public static void ApplyThrottled(string reason)
    {
        if (!Plugin.ShouldApplyNativeUnitySettings)
            return;

        var interval = Math.Max(0.05f, Plugin.ReapplyIntervalSeconds.Value);
        var now = DateTime.UtcNow;
        if (now < nextApplyUtc)
            return;

        nextApplyUtc = now.AddSeconds(interval);
        Apply(reason, forceLog: FramePacingState.ApplyLogCount < 4);
    }

    public static void Apply(string reason, bool forceLog)
    {
        if (!Plugin.ShouldApplyNativeUnitySettings)
            return;

        try
        {
            var changes = new List<string>();
            NativeUnitySettings.EnsureInitialized();

            ApplyTargetFrameRate(changes);
            ApplyVSyncCount(changes);
            ApplyRenderFrameInterval(changes);
            ApplyMaxQueuedFrames(changes);
            ApplyRunInBackground(changes);
            ApplyDisplayMode(changes);
            LogApply(reason, forceLog, changes);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Frame pacing apply failed during {reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyTargetFrameRate(List<string> changes)
    {
        var value = Plugin.TargetFrameRate.Value;
        AddResult(changes, "targetFrameRate", $"targetFrameRate={value}", NativeUnitySettings.TrySetTargetFrameRate(value, out var error), error);
    }

    private static void ApplyVSyncCount(List<string> changes)
    {
        var value = Math.Max(0, Plugin.VSyncCount.Value);
        AddResult(changes, "vSyncCount", $"vSyncCount={value}", NativeUnitySettings.TrySetVSyncCount(value, out var error), error);
    }

    private static void ApplyRenderFrameInterval(List<string> changes)
    {
        if (!Plugin.ForceOnDemandEveryFrame.Value)
            return;

        AddResult(changes, "renderFrameInterval", "renderFrameInterval=1", NativeUnitySettings.TrySetRenderFrameInterval(1, out var error), error);
    }

    private static void ApplyMaxQueuedFrames(List<string> changes)
    {
        var value = Plugin.MaxQueuedFrames.Value;
        if (value <= 0)
            return;

        AddResult(changes, "maxQueuedFrames", $"maxQueuedFrames={value}", NativeUnitySettings.TrySetMaxQueuedFrames(value, out var error), error);
    }

    private static void ApplyRunInBackground(List<string> changes)
    {
        var value = Plugin.RunInBackground.Value;
        AddResult(changes, "runInBackground", $"runInBackground={value}", NativeUnitySettings.TrySetRunInBackground(value, out var error), error);
    }

    private static void ApplyDisplayMode(List<string> changes)
    {
        if (!Plugin.ShouldForceMaximizedWindow)
            return;

        if (NativeUnitySettings.TrySetFullScreenMode(FullScreenWindowMode, out var fullScreenModeError))
        {
            changes.Add("fullScreenMode=FullScreenWindow");
            return;
        }

        if (NativeWindow.TryMaximizeMainWindow(out var maximizeError))
            changes.Add("window=Maximized");
        else
            changes.Add($"fullScreenMode/window skipped: {fullScreenModeError}; {maximizeError}");
    }

    private static void AddResult(List<string> changes, string name, string success, bool applied, string error)
    {
        changes.Add(applied ? success : $"{name} skipped: {error}");
    }

    private static void LogApply(string reason, bool forceLog, List<string> changes)
    {
        var nextLogCount = FramePacingState.IncrementApplyLogCount();
        if (!forceLog && nextLogCount > 8 && nextLogCount % 120 != 0)
            return;

        Plugin.Log.LogInfo(
            $"Frame pacing apply #{nextLogCount} ({reason}): " +
            (changes.Count == 0 ? "no changes" : string.Join(", ", changes)) + ".");
    }
}

internal static class FramePacingState
{
    private static int applyLogCount;
    private static int detourLogCount;

    public static int ApplyLogCount => applyLogCount;
    public static int DetourLogCount => detourLogCount;

    public static int IncrementApplyLogCount() => ++applyLogCount;
    public static int IncrementDetourLogCount() => ++detourLogCount;
}

internal static class CanvasScalerFramePacingDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnEnableDelegate(IntPtr self, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static OnEnableDelegate? original;
    private static readonly OnEnableDelegate replacement = OnEnableReplacement;

    public static void Install()
    {
        if (detour != null)
            return;

        var klass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
        if (klass == IntPtr.Zero)
        {
            Plugin.Log.LogWarning("CanvasScaler.OnEnable frame pacing detour skipped: class was not resolved.");
            return;
        }

        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "OnEnable", 0);
        if (method == IntPtr.Zero)
        {
            Plugin.Log.LogWarning("CanvasScaler.OnEnable frame pacing detour skipped: method was not resolved.");
            return;
        }

        var methodPointer = Marshal.ReadIntPtr(method);
        if (methodPointer == IntPtr.Zero)
        {
            Plugin.Log.LogWarning("CanvasScaler.OnEnable frame pacing detour skipped: method pointer was null.");
            return;
        }

        detour = new NativeDetour(methodPointer, replacement);
        original = detour.GenerateTrampoline<OnEnableDelegate>();
        detour.Apply();
        Plugin.Log.LogInfo($"CanvasScaler.OnEnable frame pacing detour installed at {Ptr(methodPointer)}.");
    }

    public static void Uninstall()
    {
        DetourLifecycle.Free(ref detour, ref original);
    }

    private static void OnEnableReplacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        FramePacingEnforcer.ApplyThrottled("CanvasScaler.OnEnable");
    }

}

internal static class NativeUnitySettings
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetIntNativeDelegate(int value, IntPtr methodInfo);

    private static bool initialized;
    private static IntPtr setTargetFrameRate;
    private static IntPtr setVSyncCount;
    private static IntPtr setMaxQueuedFrames;
    private static IntPtr setRunInBackground;
    private static IntPtr setFullScreenMode;
    private static IntPtr setRenderFrameInterval;
    private static NativeDetour? targetFrameRateDetour;
    private static NativeDetour? vSyncCountDetour;
    private static SetIntNativeDelegate? originalSetTargetFrameRate;
    private static SetIntNativeDelegate? originalSetVSyncCount;
    private static readonly SetIntNativeDelegate SetTargetFrameRateReplacement = SetTargetFrameRateDetour;
    private static readonly SetIntNativeDelegate SetVSyncCountReplacement = SetVSyncCountDetour;

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        var statuses = new List<string>();
        var applicationClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine", "Application");
        var deviceApplicationClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine.Device", "Application");
        var qualitySettingsClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine", "QualitySettings");
        var screenClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine", "Screen");
        var deviceScreenClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine.Device", "Screen");
        var onDemandRenderingClass = IL2CPP.GetIl2CppClass(Plugin.UnityCoreModule, "UnityEngine.Rendering", "OnDemandRendering");

        setTargetFrameRate = FindMethod(applicationClass, deviceApplicationClass, "set_targetFrameRate", 1, statuses);
        setVSyncCount = FindMethod(qualitySettingsClass, "set_vSyncCount", 1, statuses);
        setMaxQueuedFrames = FindMethod(qualitySettingsClass, "set_maxQueuedFrames", 1, statuses);
        setRunInBackground = FindMethod(applicationClass, deviceApplicationClass, "set_runInBackground", 1, statuses);
        setFullScreenMode = FindMethod(screenClass, deviceScreenClass, "set_fullScreenMode", 1, statuses);
        setRenderFrameInterval = FindMethod(onDemandRenderingClass, "set_renderFrameInterval", 1, statuses);

        var initStatus = string.Join(", ", statuses);
        initialized = true;
        Plugin.Log.LogInfo($"Resolved native Unity setting icalls: {initStatus}");
    }

    public static bool TrySetTargetFrameRate(int value, out string error) => InvokeStaticInt(setTargetFrameRate, "set_targetFrameRate", value, out error);
    public static bool TrySetVSyncCount(int value, out string error) => InvokeStaticInt(setVSyncCount, "set_vSyncCount", value, out error);
    public static bool TrySetMaxQueuedFrames(int value, out string error) => InvokeStaticInt(setMaxQueuedFrames, "set_maxQueuedFrames", value, out error);
    public static bool TrySetRunInBackground(bool value, out string error) => InvokeStaticBool(setRunInBackground, "set_runInBackground", value, out error);
    public static bool TrySetFullScreenMode(int value, out string error) => InvokeStaticInt(setFullScreenMode, "set_fullScreenMode", value, out error);
    public static bool TrySetRenderFrameInterval(int value, out string error) => InvokeStaticInt(setRenderFrameInterval, "set_renderFrameInterval", value, out error);

    public static void InstallSetterDetours()
    {
        if (!Plugin.ShouldApplyNativeUnitySettings)
            return;

        EnsureInitialized();
        TryInstallSetIntDetour("Application.set_targetFrameRate", setTargetFrameRate, SetTargetFrameRateReplacement, ref targetFrameRateDetour, ref originalSetTargetFrameRate);
        TryInstallSetIntDetour("QualitySettings.set_vSyncCount", setVSyncCount, SetVSyncCountReplacement, ref vSyncCountDetour, ref originalSetVSyncCount);
    }

    public static void UninstallSetterDetours()
    {
        FreeDetour(ref targetFrameRateDetour, ref originalSetTargetFrameRate);
        FreeDetour(ref vSyncCountDetour, ref originalSetVSyncCount);
    }

    private static void TryInstallSetIntDetour(
        string name,
        IntPtr method,
        SetIntNativeDelegate replacement,
        ref NativeDetour? detour,
        ref SetIntNativeDelegate? original)
    {
        DetourLifecycle.TryInstall(
            name,
            method,
            replacement,
            ref detour,
            ref original,
            message => Plugin.Log.LogWarning(message),
            message => Plugin.Log.LogInfo(message));
    }

    private static void FreeDetour(ref NativeDetour? detour, ref SetIntNativeDelegate? original)
    {
        DetourLifecycle.Free(ref detour, ref original);
    }

    private static void SetTargetFrameRateDetour(int value, IntPtr methodInfo)
    {
        var forced = Plugin.IsEnabled ? Plugin.TargetFrameRate.Value : value;
        LogDetourChange("Application.set_targetFrameRate", value, forced);
        originalSetTargetFrameRate?.Invoke(forced, methodInfo);
    }

    private static void SetVSyncCountDetour(int value, IntPtr methodInfo)
    {
        var forced = Plugin.IsEnabled ? Math.Max(0, Plugin.VSyncCount.Value) : value;
        LogDetourChange("QualitySettings.set_vSyncCount", value, forced);
        originalSetVSyncCount?.Invoke(forced, methodInfo);
    }

    private static void LogDetourChange(string name, int requested, int applied)
    {
        if (requested == applied || FramePacingState.DetourLogCount >= 24)
            return;

        FramePacingState.IncrementDetourLogCount();
        Plugin.Log.LogInfo($"{name} intercepted: game requested {requested}, applied {applied}.");
    }

    private static IntPtr FindMethod(IntPtr klass, string name, int argsCount, List<string> statuses)
    {
        if (klass == IntPtr.Zero)
        {
            statuses.Add($"{name}=missing-class");
            return IntPtr.Zero;
        }

        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, argsCount);
        statuses.Add(method == IntPtr.Zero ? $"{name}=missing-method" : $"{name}=ok");
        return method;
    }

    private static IntPtr FindMethod(IntPtr primaryClass, IntPtr fallbackClass, string name, int argsCount, List<string> statuses)
    {
        var method = FindMethod(primaryClass, name, argsCount, statuses);
        if (method != IntPtr.Zero)
            return method;

        if (fallbackClass == IntPtr.Zero)
        {
            statuses.Add($"{name}@Device=missing-class");
            return IntPtr.Zero;
        }

        var fallbackMethod = IL2CPP.il2cpp_class_get_method_from_name(fallbackClass, name, argsCount);
        statuses.Add(fallbackMethod == IntPtr.Zero ? $"{name}@Device=missing-method" : $"{name}@Device=ok");
        if (fallbackMethod != IntPtr.Zero)
            return fallbackMethod;

        return IntPtr.Zero;
    }

    private static bool InvokeStaticInt(IntPtr method, string name, int value, out string error)
    {
        return InvokeStaticValue(method, name, value, out error);
    }

    private static bool InvokeStaticBool(IntPtr method, string name, bool value, out string error)
    {
        return InvokeStaticValue(method, name, value ? (byte)1 : (byte)0, out error);
    }

    private static bool InvokeStaticValue<T>(IntPtr method, string name, T value, out string error)
        where T : unmanaged
    {
        error = "";
        if (method == IntPtr.Zero)
        {
            error = $"{name} method was not resolved.";
            return false;
        }

        try
        {
            unsafe
            {
                var exc = IntPtr.Zero;
                var local = value;
                var args = stackalloc void*[1];
                args[0] = &local;
                IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, args, ref exc);
                if (exc == IntPtr.Zero)
                    return true;

                error = $"IL2CPP exception {Ptr(exc)}";
                Plugin.Log.LogWarning($"{name} runtime invoke failed: {error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.LogWarning($"{name} runtime invoke failed: {error}");
            return false;
        }
    }
}

internal static class GameFrameRateDetours
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApplyFrameRateDelegate(IntPtr self, byte isBattle, IntPtr methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SceneFrameRateDelegate(IntPtr self, IntPtr sceneName, IntPtr methodInfo);

    private static NativeDetour? applyFrameRateDetour;
    private static NativeDetour? sceneFrameRateDetour;

    private static ApplyFrameRateDelegate? originalApplyFrameRate;
    private static SceneFrameRateDelegate? originalSceneFrameRate;

    private static readonly ApplyFrameRateDelegate ApplyFrameRateReplacement = ApplyFrameRateDetour;
    private static readonly SceneFrameRateDelegate SceneFrameRateReplacement = SceneFrameRateDetour;

    public static void Install()
    {
        if (!Plugin.ShouldPatchGameFrameRateMethods)
            return;

        try
        {
            var optionClass = IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "LocalSave", "LocalGameOptionData");
            var managerClass = IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "GlobalGameManager");

            TryInstall("LocalGameOptionData.ApplyFrameRate", FindMethod(optionClass, "ApplyFrameRate", 1), ApplyFrameRateReplacement, ref applyFrameRateDetour, ref originalApplyFrameRate);
            TryInstall("GlobalGameManager.SetFrameRateOnSceneLoaded", FindMethod(managerClass, "SetFrameRateOnSceneLoaded", 1), SceneFrameRateReplacement, ref sceneFrameRateDetour, ref originalSceneFrameRate);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Game frame-rate detour install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        DetourLifecycle.Free(ref applyFrameRateDetour, ref originalApplyFrameRate);
        DetourLifecycle.Free(ref sceneFrameRateDetour, ref originalSceneFrameRate);
    }

    private static IntPtr FindMethod(IntPtr klass, string name, int argsCount)
    {
        return klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, name, argsCount);
    }

    private static void TryInstall<T>(string name, IntPtr method, T replacement, ref NativeDetour? detour, ref T? original)
        where T : Delegate
    {
        DetourLifecycle.TryInstall(
            name,
            method,
            replacement,
            ref detour,
            ref original,
            message => Plugin.Log.LogWarning(message),
            message => Plugin.Log.LogInfo(message));
    }

    private static void ApplyFrameRateDetour(IntPtr self, byte isBattle, IntPtr methodInfo)
    {
        originalApplyFrameRate?.Invoke(self, isBattle, methodInfo);
        FramePacingEnforcer.Apply($"LocalGameOptionData.ApplyFrameRate(isBattle={isBattle != 0})", forceLog: FramePacingState.DetourLogCount < 8);
    }

    private static void SceneFrameRateDetour(IntPtr self, IntPtr sceneName, IntPtr methodInfo)
    {
        originalSceneFrameRate?.Invoke(self, sceneName, methodInfo);
        FramePacingEnforcer.Apply("GlobalGameManager.SetFrameRateOnSceneLoaded", forceLog: FramePacingState.DetourLogCount < 8);
    }

}

internal static class Il2CppFrameDiagnostics
{
    private static readonly string[] Keywords =
    {
        "frame",
        "fps",
        "refresh",
        "quality",
        "resolution",
        "screen",
        "display",
        "fullscreen",
        "vsync",
        "verticalsync"
    };

    public static void DumpFrameRelatedMetadata()
    {
        try
        {
            var domain = IL2CPP.il2cpp_domain_get();
            if (domain == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("IL2CPP metadata scan skipped: domain pointer was null.");
                return;
            }

            var result = BuildMetadataReport(domain);
            WriteMetadataReport(result.Report, result.MatchedTypes);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"IL2CPP metadata scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MetadataScanResult BuildMetadataReport(IntPtr domain)
    {
        unsafe
        {
            uint assemblyCount = 0;
            var assemblies = IL2CPP.il2cpp_domain_get_assemblies(domain, ref assemblyCount);
            var report = CreateMetadataReport(assemblyCount);
            var matchedTypes = AppendAssemblyMetadata(report, assemblies, assemblyCount);
            return new MetadataScanResult(report, matchedTypes);
        }
    }

    private sealed class MetadataScanResult
    {
        public MetadataScanResult(StringBuilder report, int matchedTypes)
        {
            Report = report;
            MatchedTypes = matchedTypes;
        }

        public StringBuilder Report { get; }
        public int MatchedTypes { get; }
    }

    private static StringBuilder CreateMetadataReport(uint assemblyCount)
    {
        var report = new StringBuilder();
        report.AppendLine($"LimbusFramePacingFix IL2CPP frame/display metadata scan {DateTime.Now:O}");
        report.AppendLine($"Assembly count: {assemblyCount}");
        return report;
    }

    private static unsafe int AppendAssemblyMetadata(StringBuilder report, IntPtr* assemblies, uint assemblyCount)
    {
        var matchedTypes = 0;
        for (uint assemblyIndex = 0; assemblyIndex < assemblyCount; assemblyIndex++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[assemblyIndex]);
            if (image != IntPtr.Zero)
                matchedTypes += AppendImageMetadata(report, image);
        }

        return matchedTypes;
    }

    private static int AppendImageMetadata(StringBuilder report, IntPtr image)
    {
        var imageName = Safe(() => IL2CPP.il2cpp_image_get_name_(image));
        if (!ShouldScanImage(imageName))
            return 0;

        var matchedTypes = 0;
        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        for (uint classIndex = 0; classIndex < classCount; classIndex++)
        {
            var klass = IL2CPP.il2cpp_image_get_class(image, classIndex);
            if (klass != IntPtr.Zero && TryAppendClassMetadata(report, imageName, klass))
                matchedTypes++;
        }

        return matchedTypes;
    }

    private static bool TryAppendClassMetadata(StringBuilder report, string imageName, IntPtr klass)
    {
        var fullName = GetClassFullName(klass);
        var detailed = IsDetailedTarget(fullName);
        var fields = CollectFields(klass, detailed);
        var methods = CollectMethods(klass, detailed);
        var memberMatched = fields.Any(ContainsKeyword) || methods.Any(ContainsKeyword);
        if (!ContainsKeyword(fullName) && !memberMatched && !detailed)
            return false;

        report.AppendLine();
        report.AppendLine($"[{imageName}] {fullName}");
        if (fields.Count > 0)
            report.AppendLine("  fields: " + string.Join(", ", fields));
        if (methods.Count > 0)
            report.AppendLine("  methods: " + string.Join(", ", methods));

        return true;
    }

    private static string GetClassFullName(IntPtr klass)
    {
        var className = Safe(() => IL2CPP.il2cpp_class_get_name_(klass));
        var classNamespace = Safe(() => IL2CPP.il2cpp_class_get_namespace_(klass));
        return string.IsNullOrEmpty(classNamespace) ? className : $"{classNamespace}.{className}";
    }

    private static void WriteMetadataReport(StringBuilder report, int matchedTypes)
    {
        var dir = Path.Combine(Paths.BepInExRootPath, "plugins", Plugin.NAME);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "frame-metadata-scan.txt");
        File.WriteAllText(path, report.ToString());
        Plugin.Log.LogInfo($"IL2CPP metadata scan wrote {matchedTypes} matching types to {path}.");
    }

    private static bool ShouldScanImage(string imageName)
    {
        return imageName.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase)
            || imageName.StartsWith("Unity.AdaptivePerformance", StringComparison.OrdinalIgnoreCase)
            || imageName.StartsWith(Plugin.UnityCoreModule, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDetailedTarget(string fullName)
    {
        return fullName.Equals("GlobalGameManager", StringComparison.Ordinal)
            || fullName.Equals("LocalSave.LocalGameOptionData", StringComparison.Ordinal)
            || fullName.Equals("GameScreen", StringComparison.Ordinal)
            || fullName.Equals("GameResolution", StringComparison.Ordinal);
    }

    private static List<string> CollectFields(IntPtr klass, bool includeAll)
    {
        var fields = new List<string>();
        var iter = IntPtr.Zero;
        while (true)
        {
            var field = IL2CPP.il2cpp_class_get_fields(klass, ref iter);
            if (field == IntPtr.Zero)
                break;

            var name = Safe(() => IL2CPP.il2cpp_field_get_name_(field));
            if (!string.IsNullOrEmpty(name) && (includeAll || ContainsKeyword(name)))
            {
                var type = Safe(() => IL2CPP.il2cpp_type_get_name_(IL2CPP.il2cpp_field_get_type(field)));
                var offset = IL2CPP.il2cpp_field_get_offset(field);
                fields.Add($"{type} {name}@0x{offset:X}");
            }
        }

        return fields;
    }

    private static List<string> CollectMethods(IntPtr klass, bool includeAll)
    {
        var methods = new List<string>();
        var iter = IntPtr.Zero;
        while (true)
        {
            var method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (method == IntPtr.Zero)
                break;

            var name = Safe(() => IL2CPP.il2cpp_method_get_name_(method));
            if (!string.IsNullOrEmpty(name) && (includeAll || ContainsKeyword(name)))
            {
                var returnType = Safe(() => IL2CPP.il2cpp_type_get_name_(IL2CPP.il2cpp_method_get_return_type(method)));
                var paramCount = IL2CPP.il2cpp_method_get_param_count(method);
                var parameters = new List<string>();
                for (uint i = 0; i < paramCount; i++)
                {
                    var paramType = Safe(() => IL2CPP.il2cpp_type_get_name_(IL2CPP.il2cpp_method_get_param(method, i)));
                    var paramName = Safe(() => IL2CPP.il2cpp_method_get_param_name_(method, i));
                    parameters.Add(string.IsNullOrEmpty(paramName) ? paramType : $"{paramType} {paramName}");
                }

                methods.Add($"{returnType} {name}({string.Join(", ", parameters)}) token=0x{IL2CPP.il2cpp_method_get_token(method):X}");
            }
        }

        return methods;
    }

    private static bool ContainsKeyword(string value)
    {
        return Keywords.Any(keyword => value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string Safe(Func<string?> getter)
    {
        try
        {
            return getter() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

internal static class NativeWindow
{
    private const int SwMaximize = 3;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static bool TryMaximizeMainWindow(out string error)
    {
        error = "";
        try
        {
            var pid = Process.GetCurrentProcess().Id;
            var best = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var windowPid);
                if (windowPid != pid || !IsWindowVisible(hwnd))
                    return true;

                best = hwnd;
                return false;
            }, IntPtr.Zero);

            if (best == IntPtr.Zero)
            {
                error = "No visible process window found.";
                return false;
            }

            return ShowWindow(best, SwMaximize);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
