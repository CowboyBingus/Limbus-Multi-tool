using LimbusShared;
using System;
using System.Diagnostics;
using LimbusRuntimeUIInspector.Contracts;

namespace LimbusRuntimeUIInspector.Unity;

internal static partial class UnityUiRuntime
{
    public static UiElement ApplyEdit(EditRequest edit, IntPtr transform)
    {
        var stopwatch = Stopwatch.StartNew();
        InspectorHost.Log.LogInfo($"Inspector edit: apply begin for id={edit.Id}.");
        EnsureInitialized();

        var gameObject = ValidateEditTarget(edit, transform);
        var isRectTransform = IsRectTransformObject(transform);
        ApplyActiveEdit(edit, gameObject);
        ApplyPositionEdit(edit, transform, isRectTransform);
        ApplySizeEdit(edit, transform, isRectTransform);
        ApplyAnchorMinEdit(edit, transform, isRectTransform);
        ApplyAnchorMaxEdit(edit, transform, isRectTransform);
        ApplyPivotEdit(edit, transform, isRectTransform);
        ApplyScaleEdit(edit, transform);

        var updated = ReadUpdatedElement(transform, gameObject, isRectTransform);
        InspectorHost.Log.LogInfo($"Inspector edit: applied id={edit.Id} in {stopwatch.ElapsedMilliseconds}ms.");
        return updated;
    }

    private static IntPtr ValidateEditTarget(EditRequest edit, IntPtr transform)
    {
        var gameObject = Il2CppInvoke.Object(componentGetGameObject, transform);
        if (gameObject == IntPtr.Zero)
            throw new InvalidOperationException($"Cached inspector target for id {edit.Id} no longer has a GameObject.");

        var actualId = Il2CppInvoke.Int32(objectGetInstanceId, gameObject);
        if (actualId != edit.Id)
            throw new InvalidOperationException($"Cached inspector target id mismatch. Expected {edit.Id}, found {actualId}. Run a fresh scan.");

        return gameObject;
    }

    private static void ApplyActiveEdit(EditRequest edit, IntPtr gameObject)
    {
        if (edit.Active.HasValue)
            Il2CppInvoke.SetBoolean(gameObjectSetActive, gameObject, edit.Active.Value);
    }

    private static void ApplyPositionEdit(EditRequest edit, IntPtr transform, bool isRectTransform)
    {
        if (!HasPositionEdit(edit))
            return;

        if (isRectTransform)
            ApplyVector2Edit(rectGetAnchoredPosition, rectSetAnchoredPosition, transform, edit.AnchoredX, edit.AnchoredY);
        else
            ApplyVector3Edit(transformGetLocalPosition, transformSetLocalPosition, transform, edit.AnchoredX, edit.AnchoredY, null);
    }

    private static void ApplySizeEdit(EditRequest edit, IntPtr transform, bool isRectTransform)
    {
        if (isRectTransform && (edit.Width.HasValue || edit.Height.HasValue))
            ApplyVector2Edit(rectGetSizeDelta, rectSetSizeDelta, transform, edit.Width, edit.Height);
    }

    private static void ApplyAnchorMinEdit(EditRequest edit, IntPtr transform, bool isRectTransform)
    {
        if (isRectTransform && (edit.AnchorMinX.HasValue || edit.AnchorMinY.HasValue))
            ApplyVector2Edit(rectGetAnchorMin, rectSetAnchorMin, transform, edit.AnchorMinX, edit.AnchorMinY);
    }

    private static void ApplyAnchorMaxEdit(EditRequest edit, IntPtr transform, bool isRectTransform)
    {
        if (isRectTransform && (edit.AnchorMaxX.HasValue || edit.AnchorMaxY.HasValue))
            ApplyVector2Edit(rectGetAnchorMax, rectSetAnchorMax, transform, edit.AnchorMaxX, edit.AnchorMaxY);
    }

    private static void ApplyPivotEdit(EditRequest edit, IntPtr transform, bool isRectTransform)
    {
        if (isRectTransform && (edit.PivotX.HasValue || edit.PivotY.HasValue))
            ApplyVector2Edit(rectGetPivot, rectSetPivot, transform, edit.PivotX, edit.PivotY);
    }

    private static void ApplyScaleEdit(EditRequest edit, IntPtr transform)
    {
        if (edit.ScaleX.HasValue || edit.ScaleY.HasValue || edit.ScaleZ.HasValue)
            ApplyVector3Edit(transformGetLocalScale, transformSetLocalScale, transform, edit.ScaleX, edit.ScaleY, edit.ScaleZ);
    }

    private static bool HasPositionEdit(EditRequest edit)
    {
        return edit.AnchoredX.HasValue || edit.AnchoredY.HasValue;
    }

    private static void ApplyVector2Edit(IntPtr getter, IntPtr setter, IntPtr target, float? x, float? y)
    {
        var value = Il2CppInvoke.Struct<Vector2Value>(getter, target);
        if (x.HasValue)
            value.X = x.Value;
        if (y.HasValue)
            value.Y = y.Value;
        Il2CppInvoke.SetStruct(setter, target, value);
    }

    private static void ApplyVector3Edit(IntPtr getter, IntPtr setter, IntPtr target, float? x, float? y, float? z)
    {
        var value = Il2CppInvoke.Struct<Vector3Value>(getter, target);
        if (x.HasValue)
            value.X = x.Value;
        if (y.HasValue)
            value.Y = y.Value;
        if (z.HasValue)
            value.Z = z.Value;
        Il2CppInvoke.SetStruct(setter, target, value);
    }

}
