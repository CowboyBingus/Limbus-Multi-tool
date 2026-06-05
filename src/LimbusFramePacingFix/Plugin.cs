using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LimbusFramePacingFix;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbusframepacingfix";
    public const string NAME = "LimbusFramePacingFix";
    public const string VERSION = "0.4.0";

    internal static new ManualLogSource Log = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<int> TargetFrameRate = null!;
    internal static ConfigEntry<int> VSyncCount = null!;
    internal static ConfigEntry<bool> ForceMaximizedWindow = null!;
    internal static ConfigEntry<bool> RunInBackground = null!;
    internal static ConfigEntry<bool> ForceOnDemandEveryFrame = null!;
    internal static ConfigEntry<int> MaxQueuedFrames = null!;
    internal static ConfigEntry<float> ReapplyIntervalSeconds = null!;
    internal static ConfigEntry<bool> ApplyNativeUnitySettings = null!;
    internal static ConfigEntry<bool> PatchGameFrameRateMethods = null!;
    internal static ConfigEntry<bool> DumpMetadataOnLoad = null!;

    public override void Load()
    {
        Log = base.Log;
        BindConfig();

        CanvasScalerFramePacingDetour.Install();
        NativeUnitySettings.InstallSetterDetours();
        GameFrameRateDetours.Install();
        FramePacingEnforcer.Apply("Plugin.Load", forceLog: true);
        if (DumpMetadataOnLoad.Value)
            Il2CppFrameDiagnostics.DumpFrameRelatedMetadata();
        Log.LogInfo(
            $"{NAME} {VERSION} loaded. " +
            $"Enabled={Enabled.Value}, TargetFrameRate={TargetFrameRate.Value}, VSyncCount={VSyncCount.Value}, " +
            $"ForceMaximizedWindow={ForceMaximizedWindow.Value}, RunInBackground={RunInBackground.Value}, " +
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

    private void BindConfig()
    {
        Enabled = Config.Bind("General", "Enabled", true, "Master switch for all frame pacing mitigations.");
        TargetFrameRate = Config.Bind("Frame pacing", "TargetFrameRate", 240, "Forced Application.targetFrameRate. Use -1 to let Unity use platform default.");
        VSyncCount = Config.Bind("Frame pacing", "VSyncCount", 0, "Forced QualitySettings.vSyncCount. 0 disables Unity VSync so the explicit FPS cap is used.");
        ForceOnDemandEveryFrame = Config.Bind("Frame pacing", "ForceOnDemandEveryFrame", true, "Forces OnDemandRendering.renderFrameInterval to 1 so Unity does not intentionally skip renders.");
        MaxQueuedFrames = Config.Bind("Frame pacing", "MaxQueuedFrames", 1, "Forced QualitySettings.maxQueuedFrames. Use 0 to leave unchanged.");
        ForceMaximizedWindow = Config.Bind("Display", "ForceMaximizedWindow", true, "Forces Screen.fullScreenMode to FullScreenWindow, Unity's borderless maximized window mode.");
        RunInBackground = Config.Bind("Display", "RunInBackground", false, "Forced Application.runInBackground value.");
        ReapplyIntervalSeconds = Config.Bind("Diagnostics", "ReapplyIntervalSeconds", 0.25f, "How often the runtime enforcer checks and reapplies settings.");
        ApplyNativeUnitySettings = Config.Bind("Diagnostics", "ApplyNativeUnitySettings", true, "Experimental. Applies Unity settings through IL2CPP runtime invocation.");
        PatchGameFrameRateMethods = Config.Bind("Diagnostics", "PatchGameFrameRateMethods", true, "Hooks Limbus' own frame-rate apply methods so game-side 144 FPS writes are followed by the forced cap.");
        DumpMetadataOnLoad = Config.Bind("Diagnostics", "DumpMetadataOnLoad", false, "Writes an IL2CPP frame/display metadata scan for reverse engineering. Leave off for normal play.");
    }

    internal static bool IsEnabled => Enabled != null && Enabled.Value;
    internal static bool ShouldApplyNativeUnitySettings => IsEnabled && ApplyNativeUnitySettings != null && ApplyNativeUnitySettings.Value;
    internal static bool ShouldPatchGameFrameRateMethods => IsEnabled && PatchGameFrameRateMethods != null && PatchGameFrameRateMethods.Value;
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

            var targetFrameRate = Plugin.TargetFrameRate.Value;
            if (NativeUnitySettings.TrySetTargetFrameRate(targetFrameRate, out var targetFrameRateError))
            {
                changes.Add($"targetFrameRate={targetFrameRate}");
            }
            else
            {
                changes.Add($"targetFrameRate skipped: {targetFrameRateError}");
            }

            var vSyncCount = Math.Max(0, Plugin.VSyncCount.Value);
            if (NativeUnitySettings.TrySetVSyncCount(vSyncCount, out var vSyncError))
            {
                changes.Add($"vSyncCount={vSyncCount}");
            }
            else
            {
                changes.Add($"vSyncCount skipped: {vSyncError}");
            }

            if (Plugin.ForceOnDemandEveryFrame.Value)
            {
                if (NativeUnitySettings.TrySetRenderFrameInterval(1, out var renderIntervalError))
                {
                    changes.Add("renderFrameInterval=1");
                }
                else
                {
                    changes.Add($"renderFrameInterval skipped: {renderIntervalError}");
                }
            }

            var maxQueuedFrames = Plugin.MaxQueuedFrames.Value;
            if (maxQueuedFrames > 0)
            {
                if (NativeUnitySettings.TrySetMaxQueuedFrames(maxQueuedFrames, out var maxQueuedFramesError))
                {
                    changes.Add($"maxQueuedFrames={maxQueuedFrames}");
                }
                else
                {
                    changes.Add($"maxQueuedFrames skipped: {maxQueuedFramesError}");
                }
            }

            var runInBackground = Plugin.RunInBackground.Value;
            if (NativeUnitySettings.TrySetRunInBackground(runInBackground, out var runInBackgroundError))
            {
                changes.Add($"runInBackground={runInBackground}");
            }
            else
            {
                changes.Add($"runInBackground skipped: {runInBackgroundError}");
            }

            if (Plugin.ForceMaximizedWindow.Value)
            {
                if (NativeUnitySettings.TrySetFullScreenMode(FullScreenWindowMode, out var fullScreenModeError))
                {
                    changes.Add("fullScreenMode=FullScreenWindow");
                }
                else if (NativeWindow.TryMaximizeMainWindow(out var maximizeError))
                {
                    changes.Add("window=Maximized");
                }
                else
                {
                    changes.Add($"fullScreenMode/window skipped: {fullScreenModeError}; {maximizeError}");
                }
            }

            var nextLogCount = FramePacingState.ApplyLogCount + 1;
            if (forceLog || nextLogCount <= 8 || nextLogCount % 120 == 0)
            {
                FramePacingState.ApplyLogCount = nextLogCount;
                Plugin.Log.LogInfo(
                    $"Frame pacing apply #{nextLogCount} ({reason}): " +
                    (changes.Count == 0 ? "no changes" : string.Join(", ", changes)) + ".");
            }
            else
            {
                FramePacingState.ApplyLogCount = nextLogCount;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Frame pacing apply failed during {reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal static class FramePacingState
{
    public static int ApplyLogCount;
    public static int DetourLogCount;
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
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Unload can race Unity teardown.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void OnEnableReplacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        FramePacingEnforcer.ApplyThrottled("CanvasScaler.OnEnable");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
}

internal static class NativeUnitySettings
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetIntNativeDelegate(int value, IntPtr methodInfo);

    private static bool initialized;
    private static string initStatus = "";
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
        var applicationClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "Application");
        var deviceApplicationClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine.Device", "Application");
        var qualitySettingsClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "QualitySettings");
        var screenClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "Screen");
        var deviceScreenClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine.Device", "Screen");
        var onDemandRenderingClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine.Rendering", "OnDemandRendering");

        setTargetFrameRate = FindMethod(applicationClass, deviceApplicationClass, "set_targetFrameRate", 1, statuses);
        setVSyncCount = FindMethod(qualitySettingsClass, "set_vSyncCount", 1, statuses);
        setMaxQueuedFrames = FindMethod(qualitySettingsClass, "set_maxQueuedFrames", 1, statuses);
        setRunInBackground = FindMethod(applicationClass, deviceApplicationClass, "set_runInBackground", 1, statuses);
        setFullScreenMode = FindMethod(screenClass, deviceScreenClass, "set_fullScreenMode", 1, statuses);
        setRenderFrameInterval = FindMethod(onDemandRenderingClass, "set_renderFrameInterval", 1, statuses);

        initStatus = string.Join(", ", statuses);
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
        if (detour != null)
            return;

        if (method == IntPtr.Zero)
        {
            Plugin.Log.LogWarning($"{name} detour skipped: IL2CPP MethodInfo was not resolved.");
            return;
        }

        try
        {
            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"{name} detour skipped: native method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SetIntNativeDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"{name} detour installed at {Ptr(methodPointer)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"{name} detour install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void FreeDetour(ref NativeDetour? detour, ref SetIntNativeDelegate? original)
    {
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Plugin unload can happen while Unity is tearing down native state.
        }
        finally
        {
            detour = null;
            original = null;
        }
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

        FramePacingState.DetourLogCount++;
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

    private static unsafe bool InvokeStaticInt(IntPtr method, string name, int value, out string error)
    {
        error = "";
        if (method == IntPtr.Zero)
        {
            error = $"{name} method was not resolved.";
            return false;
        }

        try
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
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.LogWarning($"{name} runtime invoke failed: {error}");
            return false;
        }
    }

    private static unsafe bool InvokeStaticBool(IntPtr method, string name, bool value, out string error)
    {
        error = "";
        if (method == IntPtr.Zero)
        {
            error = $"{name} method was not resolved.";
            return false;
        }

        try
        {
            var exc = IntPtr.Zero;
            var local = value ? (byte)1 : (byte)0;
            var args = stackalloc void*[1];
            args[0] = &local;
            IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, args, ref exc);
            if (exc == IntPtr.Zero)
                return true;

            error = $"IL2CPP exception {Ptr(exc)}";
            Plugin.Log.LogWarning($"{name} runtime invoke failed: {error}");
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Plugin.Log.LogWarning($"{name} runtime invoke failed: {error}");
            return false;
        }
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
        Free(ref applyFrameRateDetour, ref originalApplyFrameRate);
        Free(ref sceneFrameRateDetour, ref originalSceneFrameRate);
    }

    private static IntPtr FindMethod(IntPtr klass, string name, int argsCount)
    {
        return klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, name, argsCount);
    }

    private static void TryInstall<T>(string name, IntPtr method, T replacement, ref NativeDetour? detour, ref T? original)
        where T : Delegate
    {
        if (detour != null)
            return;

        if (method == IntPtr.Zero)
        {
            Plugin.Log.LogWarning($"{name} detour skipped: IL2CPP MethodInfo was not resolved.");
            return;
        }

        try
        {
            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"{name} detour skipped: native method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<T>();
            detour.Apply();
            Plugin.Log.LogInfo($"{name} detour installed at {Ptr(methodPointer)}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"{name} detour install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Free<T>(ref NativeDetour? detour, ref T? original)
        where T : Delegate
    {
        try
        {
            detour?.Undo();
            detour?.Free();
        }
        catch
        {
            // Plugin unload can happen while Unity is tearing down native state.
        }
        finally
        {
            detour = null;
            original = null;
        }
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

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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

    public static unsafe void DumpFrameRelatedMetadata()
    {
        try
        {
            var domain = IL2CPP.il2cpp_domain_get();
            if (domain == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("IL2CPP metadata scan skipped: domain pointer was null.");
                return;
            }

            uint assemblyCount = 0;
            var assemblies = IL2CPP.il2cpp_domain_get_assemblies(domain, ref assemblyCount);
            if (assemblies == null || assemblyCount == 0)
            {
                Plugin.Log.LogWarning("IL2CPP metadata scan skipped: no assemblies were visible.");
                return;
            }

            var report = new StringBuilder();
            report.AppendLine($"LimbusFramePacingFix IL2CPP frame/display metadata scan {DateTime.Now:O}");
            report.AppendLine($"Assembly count: {assemblyCount}");

            var matchedTypes = 0;
            for (uint assemblyIndex = 0; assemblyIndex < assemblyCount; assemblyIndex++)
            {
                var assembly = assemblies[assemblyIndex];
                var image = IL2CPP.il2cpp_assembly_get_image(assembly);
                if (image == IntPtr.Zero)
                    continue;

                var imageName = Safe(() => IL2CPP.il2cpp_image_get_name_(image));
                if (!ShouldScanImage(imageName))
                    continue;

                var classCount = IL2CPP.il2cpp_image_get_class_count(image);
                for (uint classIndex = 0; classIndex < classCount; classIndex++)
                {
                    var klass = IL2CPP.il2cpp_image_get_class(image, classIndex);
                    if (klass == IntPtr.Zero)
                        continue;

                    var className = Safe(() => IL2CPP.il2cpp_class_get_name_(klass));
                    var classNamespace = Safe(() => IL2CPP.il2cpp_class_get_namespace_(klass));
                    var fullName = string.IsNullOrEmpty(classNamespace) ? className : $"{classNamespace}.{className}";
                    var classMatched = ContainsKeyword(fullName);
                    var detailed = IsDetailedTarget(fullName);

                    var fields = CollectFields(klass, detailed);
                    var methods = CollectMethods(klass, detailed);
                    var memberMatched = fields.Exists(ContainsKeyword) || methods.Exists(ContainsKeyword);
                    if (!classMatched && !memberMatched && !detailed)
                        continue;

                    matchedTypes++;
                    report.AppendLine();
                    report.AppendLine($"[{imageName}] {fullName}");
                    if (fields.Count > 0)
                        report.AppendLine("  fields: " + string.Join(", ", fields));
                    if (methods.Count > 0)
                        report.AppendLine("  methods: " + string.Join(", ", methods));
                }
            }

            var dir = Path.Combine(Paths.BepInExRootPath, "plugins", Plugin.NAME);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "frame-metadata-scan.txt");
            File.WriteAllText(path, report.ToString());
            Plugin.Log.LogInfo($"IL2CPP metadata scan wrote {matchedTypes} matching types to {path}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"IL2CPP metadata scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldScanImage(string imageName)
    {
        return imageName.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase)
            || imageName.StartsWith("Unity.AdaptivePerformance", StringComparison.OrdinalIgnoreCase)
            || imageName.StartsWith("UnityEngine.CoreModule", StringComparison.OrdinalIgnoreCase);
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
        foreach (var keyword in Keywords)
        {
            if (value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
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
