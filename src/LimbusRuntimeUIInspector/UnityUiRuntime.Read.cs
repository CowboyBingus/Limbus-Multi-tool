using LimbusShared.Interop;
using LimbusShared.Unity;
using System;
using LimbusRuntimeUIInspector.Contracts.Elements;

namespace LimbusRuntimeUIInspector.Unity.Runtime;

internal static partial class UnityUiRuntime
{
    public static UiElement? TryReadRectElement(IntPtr rect, bool includeInactive)
    {
        EnsureInitialized();
        var gameObject = Il2CppInvoke.Object(componentGetGameObject, rect);
        if (gameObject == IntPtr.Zero)
            return null;

        var activeInHierarchy = Il2CppInvoke.Boolean(gameObjectGetActiveInHierarchy, gameObject);
        if (!includeInactive && !activeInHierarchy)
            return null;

        return ReadElement(rect, gameObject, BuildPath(rect), activeInHierarchy);
    }

    public static UiElement? TryReadTransformElement(IntPtr transform, bool includeInactive)
    {
        EnsureInitialized();
        var gameObject = Il2CppInvoke.Object(componentGetGameObject, transform);
        if (gameObject == IntPtr.Zero)
            return null;

        var activeInHierarchy = Il2CppInvoke.Boolean(gameObjectGetActiveInHierarchy, gameObject);
        if (!includeInactive && !activeInHierarchy)
            return null;

        return ReadTransformElement(transform, gameObject, BuildPath(transform), activeInHierarchy);
    }

    private static UiElement ReadUpdatedElement(IntPtr transform, IntPtr gameObject, bool isRectTransform)
    {
        var path = BuildPath(transform);
        var activeInHierarchy = Il2CppInvoke.Boolean(gameObjectGetActiveInHierarchy, gameObject);
        return isRectTransform
            ? ReadElement(transform, gameObject, path, activeInHierarchy)
            : ReadTransformElement(transform, gameObject, path, activeInHierarchy);
    }

    private static UiElement ReadElement(IntPtr rect, IntPtr gameObject, string path, bool activeInHierarchy)
    {
        var anchored = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchoredPosition, rect);
        var size = Il2CppInvoke.Struct<Vector2Value>(rectGetSizeDelta, rect);
        var anchorMin = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchorMin, rect);
        var anchorMax = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchorMax, rect);
        var pivot = Il2CppInvoke.Struct<Vector2Value>(rectGetPivot, rect);
        var scale = Il2CppInvoke.Struct<Vector3Value>(transformGetLocalScale, rect);

        return new UiElement(
            Il2CppInvoke.Int32(objectGetInstanceId, gameObject),
            Il2CppInvoke.String(objectGetName, gameObject),
            path,
            "RectTransform",
            Il2CppInvoke.Boolean(gameObjectGetActiveSelf, gameObject),
            activeInHierarchy,
            Round(anchored.X),
            Round(anchored.Y),
            Round(size.X),
            Round(size.Y),
            Round(anchorMin.X),
            Round(anchorMin.Y),
            Round(anchorMax.X),
            Round(anchorMax.Y),
            Round(pivot.X),
            Round(pivot.Y),
            Round(scale.X),
            Round(scale.Y),
            Round(scale.Z));
    }

    private static UiElement ReadTransformElement(IntPtr transform, IntPtr gameObject, string path, bool activeInHierarchy)
    {
        var position = Il2CppInvoke.Struct<Vector3Value>(transformGetLocalPosition, transform);
        var scale = Il2CppInvoke.Struct<Vector3Value>(transformGetLocalScale, transform);

        return new UiElement(
            Il2CppInvoke.Int32(objectGetInstanceId, gameObject),
            Il2CppInvoke.String(objectGetName, gameObject),
            path,
            "Transform",
            Il2CppInvoke.Boolean(gameObjectGetActiveSelf, gameObject),
            activeInHierarchy,
            Round(position.X),
            Round(position.Y),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Round(scale.X),
            Round(scale.Y),
            Round(scale.Z));
    }

}
