using Il2CppInterop.Runtime;
using LimbusShared.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LimbusRuntimeUIInspector.Unity.Interop;
using static LimbusShared.Interop.Il2CppLookup;

namespace LimbusRuntimeUIInspector.Unity.Runtime;

internal static partial class UnityUiRuntime
{
    private const int MaxTraversalNodes = 100000;

    private static bool initialized;
    private static IntPtr rectTransformClass;

    private static IntPtr objectGetName;
    private static IntPtr objectGetInstanceId;
    private static IntPtr componentGetGameObject;
    private static IntPtr gameObjectGetTransform;
    private static IntPtr gameObjectGetActiveSelf;
    private static IntPtr gameObjectGetActiveInHierarchy;
    private static IntPtr gameObjectSetActive;
    private static IntPtr transformGetParent;
    private static IntPtr transformGetChildCount;
    private static IntPtr transformGetChild;
    private static IntPtr transformGetLocalPosition;
    private static IntPtr transformSetLocalPosition;
    private static IntPtr transformGetLocalScale;
    private static IntPtr transformSetLocalScale;
    private static IntPtr rectGetAnchoredPosition;
    private static IntPtr rectSetAnchoredPosition;
    private static IntPtr rectGetSizeDelta;
    private static IntPtr rectSetSizeDelta;
    private static IntPtr rectGetAnchorMin;
    private static IntPtr rectSetAnchorMin;
    private static IntPtr rectGetAnchorMax;
    private static IntPtr rectSetAnchorMax;
    private static IntPtr rectGetPivot;
    private static IntPtr rectSetPivot;
    private static IntPtr canvasForceUpdateCanvases;

    public static IntPtr TryGetTransformFromComponent(IntPtr component)
    {
        EnsureInitialized();
        var gameObject = Il2CppInvoke.Object(componentGetGameObject, component);
        return TryGetTransformFromGameObject(gameObject);
    }

    public static IntPtr TryGetTransformFromGameObject(IntPtr gameObject)
    {
        EnsureInitialized();
        return gameObject == IntPtr.Zero ? IntPtr.Zero : Il2CppInvoke.Object(gameObjectGetTransform, gameObject);
    }

    public static IntPtr TryGetParentTransform(IntPtr transform)
    {
        EnsureInitialized();
        return transform == IntPtr.Zero ? IntPtr.Zero : Il2CppInvoke.Object(transformGetParent, transform);
    }

    public static IntPtr TryGetTopmostTransform(IntPtr transform)
    {
        EnsureInitialized();
        var current = transform;
        var topmost = transform;
        for (var depth = 0; depth < 256 && current != IntPtr.Zero; depth++)
        {
            topmost = current;
            current = Il2CppInvoke.Object(transformGetParent, current);
        }

        return topmost;
    }

    public static void ForceUpdateCanvasesForInspector()
    {
        var stopwatch = Stopwatch.StartNew();
        InspectorHost.Log.LogInfo("Inspector scan: Canvas.ForceUpdateCanvases begin.");
        EnsureInitialized();
        Il2CppInvoke.Object(canvasForceUpdateCanvases, IntPtr.Zero);
        InspectorHost.Log.LogInfo($"Inspector scan: Canvas.ForceUpdateCanvases complete after {stopwatch.ElapsedMilliseconds}ms.");
    }

    public static string TryDescribeObject(IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return "null";

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (klass == IntPtr.Zero)
                return "class=null";
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) ?? "";
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        catch (Exception ex)
        {
            return "describe-failed:" + ex.GetType().Name;
        }
    }

    private static bool IsRectTransformObject(IntPtr obj)
    {
        return Il2CppLookup.IsObjectClassOrNamed(obj, rectTransformClass, "RectTransform");
    }

    private static string BuildPath(IntPtr transform)
    {
        var names = new Stack<string>();
        var current = transform;
        for (var depth = 0; depth < 64 && current != IntPtr.Zero; depth++)
        {
            var name = Il2CppInvoke.String(objectGetName, current);
            if (!string.IsNullOrWhiteSpace(name))
                names.Push(name);
            current = Il2CppInvoke.Object(transformGetParent, current);
        }

        return "/" + string.Join("/", names);
    }

    private static void EnsureInitialized()
    {
        if (initialized)
            return;

        var objectClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Object");
        InspectorHost.Debug("Resolved UnityEngine.Object.");
        var componentClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Component");
        InspectorHost.Debug("Resolved UnityEngine.Component.");
        var gameObjectClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "GameObject");
        InspectorHost.Debug("Resolved UnityEngine.GameObject.");
        var transformClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Transform");
        InspectorHost.Debug("Resolved UnityEngine.Transform.");
        rectTransformClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "RectTransform");
        InspectorHost.Debug("Resolved UnityEngine.RectTransform.");
        var canvasClass = RequireClass("UnityEngine.UIModule.dll", UnityInteropNames.Namespace, "Canvas");
        InspectorHost.Debug("Resolved UnityEngine.Canvas.");

        objectGetName = RequireMethod(objectClass, "get_name", 0);
        objectGetInstanceId = RequireMethod(objectClass, "GetInstanceID", 0);
        componentGetGameObject = RequireMethod(componentClass, "get_gameObject", 0);
        gameObjectGetTransform = RequireMethod(gameObjectClass, "get_transform", 0);
        gameObjectGetActiveSelf = RequireMethod(gameObjectClass, "get_activeSelf", 0);
        gameObjectGetActiveInHierarchy = RequireMethod(gameObjectClass, "get_activeInHierarchy", 0);
        gameObjectSetActive = RequireMethod(gameObjectClass, "SetActive", 1);
        transformGetParent = RequireMethod(transformClass, "get_parent", 0);
        transformGetChildCount = RequireMethod(transformClass, "get_childCount", 0);
        transformGetChild = RequireMethod(transformClass, "GetChild", 1);
        transformGetLocalPosition = RequireMethod(transformClass, "get_localPosition", 0);
        transformSetLocalPosition = RequireMethod(transformClass, "set_localPosition", 1);
        transformGetLocalScale = RequireMethod(transformClass, "get_localScale", 0);
        transformSetLocalScale = RequireMethod(transformClass, "set_localScale", 1);
        rectGetAnchoredPosition = RequireMethod(rectTransformClass, "get_anchoredPosition", 0);
        rectSetAnchoredPosition = RequireMethod(rectTransformClass, "set_anchoredPosition", 1);
        rectGetSizeDelta = RequireMethod(rectTransformClass, "get_sizeDelta", 0);
        rectSetSizeDelta = RequireMethod(rectTransformClass, "set_sizeDelta", 1);
        rectGetAnchorMin = RequireMethod(rectTransformClass, "get_anchorMin", 0);
        rectSetAnchorMin = RequireMethod(rectTransformClass, "set_anchorMin", 1);
        rectGetAnchorMax = RequireMethod(rectTransformClass, "get_anchorMax", 0);
        rectSetAnchorMax = RequireMethod(rectTransformClass, "set_anchorMax", 1);
        rectGetPivot = RequireMethod(rectTransformClass, "get_pivot", 0);
        rectSetPivot = RequireMethod(rectTransformClass, "set_pivot", 1);
        canvasForceUpdateCanvases = RequireMethod(canvasClass, "ForceUpdateCanvases", 0);

        initialized = true;
    }

    private static float Round(float value) => MathF.Round(value, 3);
}
