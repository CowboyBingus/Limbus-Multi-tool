using Il2CppInterop.Runtime;
using LimbusShared;
using MonoMod.RuntimeDetour;
using System;
using System.Runtime.InteropServices;
using LimbusRuntimeUIInspector.Unity.Interop;
using LimbusRuntimeUIInspector.Unity.Tracking;
using static LimbusShared.Interop.NativeInterop;

namespace LimbusRuntimeUIInspector.Unity.Detours;

internal static class UnityRootDetours
{
    public static bool EnsurePumpInstalled(Action pumpAction) => UnityPumpDetour.EnsureInstalled(pumpAction);

    public static void InstallObservers()
    {
        CanvasRootObserveDetour.Install();
        RectTransformRootObserveDetour.Install();
        GameObjectRootObserveDetour.Install();
    }

    public static void UninstallAll()
    {
        UnityPumpDetour.Uninstall();
        GameObjectRootObserveDetour.Uninstall();
        RectTransformRootObserveDetour.Uninstall();
        CanvasRootObserveDetour.Uninstall();
    }
}

internal static class UnityPumpDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StaticVoidDelegate(IntPtr methodInfo);

    private static NativeDetour? detour;
    private static StaticVoidDelegate? original;
    private static readonly StaticVoidDelegate replacement = Replacement;
    private static Action? pump;
    [ThreadStatic] private static bool inReplacement;

    public static bool EnsureInstalled(Action pumpAction)
    {
        pump = pumpAction;
        if (detour != null)
            return true;

        try
        {
            InspectorHost.Debug("Installing on-demand Canvas.SendWillRenderCanvases pump.");
            var canvasClass = IL2CPP.GetIl2CppClass("UnityEngine.UIModule.dll", UnityInteropNames.Namespace, "Canvas");
            if (canvasClass == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Runtime UI inspector pump install failed: UnityEngine.Canvas class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(canvasClass, "SendWillRenderCanvases", 0);
            if (method == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Runtime UI inspector pump install failed: Canvas.SendWillRenderCanvases was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Runtime UI inspector pump install failed: Canvas.SendWillRenderCanvases pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<StaticVoidDelegate>();
            detour.Apply();
            InspectorHost.Log.LogInfo($"Runtime UI inspector on-demand pump installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            InspectorHost.Log.LogWarning($"Runtime UI inspector pump install failed: {ex}");
            return false;
        }
    }

    public static void Uninstall()
    {
        pump = null;
        SharedRuntime.FreeDetour(ref detour, ref original);
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
            pump?.Invoke();
        }
        finally
        {
            inReplacement = false;
        }
    }

}

internal static class CanvasRootObserveDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnEnableDelegate(IntPtr self, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static OnEnableDelegate? original;
    private static readonly OnEnableDelegate replacement = Replacement;

    public static bool Install()
    {
        if (detour != null)
            return true;

        try
        {
            InspectorHost.Debug("Installing CanvasScaler.OnEnable root observer.");
            var scalerClass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
            if (scalerClass == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Canvas root observer install failed: UnityEngine.UI.CanvasScaler class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(scalerClass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Canvas root observer install failed: CanvasScaler.OnEnable was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("Canvas root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<OnEnableDelegate>();
            detour.Apply();
            InspectorHost.Log.LogInfo($"Canvas root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            InspectorHost.Log.LogWarning($"Canvas root observer install failed: {ex}");
            return false;
        }
    }

    public static void Uninstall()
    {
        SharedRuntime.FreeDetour(ref detour, ref original);
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        CanvasRootRegistry.ObserveComponent(self, "CanvasScaler.OnEnable");
    }

}

internal static class RectTransformRootObserveDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendReapplyDrivenPropertiesDelegate(IntPtr rectTransform, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static SendReapplyDrivenPropertiesDelegate? original;
    private static readonly SendReapplyDrivenPropertiesDelegate replacement = Replacement;

    public static bool Install()
    {
        if (detour != null)
            return true;

        try
        {
            InspectorHost.Debug("Installing RectTransform relayout root observer.");
            var rectClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "RectTransform");
            if (rectClass == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("RectTransform root observer install failed: RectTransform class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(rectClass, "SendReapplyDrivenProperties", 1);
            if (method == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("RectTransform root observer install failed: SendReapplyDrivenProperties was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("RectTransform root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SendReapplyDrivenPropertiesDelegate>();
            detour.Apply();
            InspectorHost.Log.LogInfo($"RectTransform relayout root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            InspectorHost.Log.LogWarning($"RectTransform root observer install failed: {ex}");
            return false;
        }
    }

    public static void Uninstall()
    {
        SharedRuntime.FreeDetour(ref detour, ref original);
    }

    private static void Replacement(IntPtr rectTransform, IntPtr methodInfo)
    {
        original?.Invoke(rectTransform, methodInfo);
        CanvasRootRegistry.ObserveTransformAndAncestors(rectTransform, "RectTransform.SendReapplyDrivenProperties");
    }

}

internal static class GameObjectRootObserveDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetActiveDelegate(IntPtr self, byte active, IntPtr methodInfo);

    private static NativeDetour? detour;
    private static SetActiveDelegate? original;
    private static readonly SetActiveDelegate replacement = Replacement;

    public static bool Install()
    {
        if (detour != null)
            return true;

        try
        {
            InspectorHost.Debug("Installing GameObject.SetActive root observer.");
            var gameObjectClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "GameObject");
            if (gameObjectClass == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("GameObject root observer install failed: GameObject class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(gameObjectClass, "SetActive", 1);
            if (method == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("GameObject root observer install failed: GameObject.SetActive was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                InspectorHost.Log.LogWarning("GameObject root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SetActiveDelegate>();
            detour.Apply();
            InspectorHost.Log.LogInfo($"GameObject.SetActive root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            InspectorHost.Log.LogWarning($"GameObject root observer install failed: {ex}");
            return false;
        }
    }

    public static void Uninstall()
    {
        SharedRuntime.FreeDetour(ref detour, ref original);
    }

    private static void Replacement(IntPtr self, byte active, IntPtr methodInfo)
    {
        original?.Invoke(self, active, methodInfo);
        if (active != 0)
            CanvasRootRegistry.ObserveGameObject(self, "GameObject.SetActive");
    }

}

