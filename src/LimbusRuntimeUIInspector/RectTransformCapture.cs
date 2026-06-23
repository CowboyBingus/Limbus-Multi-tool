using System;
using System.Collections.Generic;
using System.Threading;
using LimbusRuntimeUIInspector.Contracts;

namespace LimbusRuntimeUIInspector.Unity;

internal static class RectTransformCapture
{
    private const int MaxKnownElements = 50000;

    private static readonly object sync = new();
    private static readonly Dictionary<int, IntPtr> knownRects = new();
    private static int cachedCount;

    public static int CachedCount => Volatile.Read(ref cachedCount);

    public static void ReplaceScanCache(IReadOnlyList<ScannedUiElement> elements)
    {
        lock (sync)
        {
            knownRects.Clear();
            foreach (var item in elements)
            {
                if (knownRects.Count >= MaxKnownElements)
                    break;
                knownRects[item.Element.Id] = item.RectTransform;
            }

            Volatile.Write(ref cachedCount, knownRects.Count);
        }

        InspectorHost.Debug($"Inspector edit cache refreshed from live scan: cached={CachedCount}.");
    }

    public static bool TryGetRect(int id, out IntPtr rect)
    {
        lock (sync)
            return knownRects.TryGetValue(id, out rect);
    }
}

