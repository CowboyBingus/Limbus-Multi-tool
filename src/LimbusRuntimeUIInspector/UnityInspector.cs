using System;
using System.Collections.Generic;
using LimbusRuntimeUIInspector.Contracts.Api;
using LimbusRuntimeUIInspector.Contracts.Elements;
using LimbusRuntimeUIInspector.Contracts.Scanning;
using LimbusRuntimeUIInspector.Unity.Capture;
using LimbusRuntimeUIInspector.Unity.Detours;
using LimbusRuntimeUIInspector.Unity.Runtime;
using LimbusRuntimeUIInspector.Unity.Tracking;

namespace LimbusRuntimeUIInspector.Unity;

internal static class UnityInspector
{
    public static void ConfigureRootTracking()
    {
        CanvasRootRegistry.Configure(
            UnityUiRuntime.TryGetTransformFromComponent,
            UnityUiRuntime.TryGetTransformFromGameObject,
            UnityUiRuntime.TryGetTopmostTransform);
    }

    public static bool EnsurePumpInstalled(Action pumpAction) => UnityRootDetours.EnsurePumpInstalled(pumpAction);

    public static void InstallRootObservers() => UnityRootDetours.InstallObservers();

    public static void UninstallDetours() => UnityRootDetours.UninstallAll();

    public static ScanResult ScanObservedRoots(string? filter, bool includeInactive, bool includeTransforms, int maxResults)
    {
        return UnityUiRuntime.ScanObservedRoots(filter, includeInactive, includeTransforms, maxResults);
    }

    public static void ReplaceScanCache(IReadOnlyList<ScannedUiElement> elements)
    {
        RectTransformCapture.ReplaceScanCache(elements);
    }

    public static bool TryGetRect(int id, out IntPtr rect)
    {
        return RectTransformCapture.TryGetRect(id, out rect);
    }

    public static UiElement ApplyEdit(EditRequest edit, IntPtr rect)
    {
        return UnityUiRuntime.ApplyEdit(edit, rect);
    }
}
