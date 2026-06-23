using System;
using System.Collections.Generic;
using System.Threading;

namespace LimbusRuntimeUIInspector;

internal static class CanvasRootRegistry
{
    private const int MaxKnownRoots = 4096;
    private const long MaxRootObservationAge = 20000;

    private static readonly object sync = new();
    private static readonly Dictionary<IntPtr, long> rootsSeen = new();
    private static long sequence;
    private static int observedCount;
    private static int failureCount;

    public static int RootCount
    {
        get
        {
            lock (sync)
                return rootsSeen.Count;
        }
    }

    public static void ObserveComponent(IntPtr component, string source)
    {
        if (component == IntPtr.Zero)
            return;

        try
        {
            var transform = UnityUiRuntime.TryGetTransformFromComponent(component);
            ObserveTransformAndAncestors(transform, source);
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref failureCount);
            if (failures <= 8)
                Plugin.Debug($"Canvas root observe failed #{failures}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void ObserveGameObject(IntPtr gameObject, string source)
    {
        if (gameObject == IntPtr.Zero)
            return;

        try
        {
            var transform = UnityUiRuntime.TryGetTransformFromGameObject(gameObject);
            ObserveTransformAndAncestors(transform, source);
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref failureCount);
            if (failures <= 8)
                Plugin.Debug($"GameObject root observe failed #{failures}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void ObserveTransformAndAncestors(IntPtr transform, string source)
    {
        if (transform == IntPtr.Zero)
            return;

        int rootCount;
        try
        {
            var root = UnityUiRuntime.TryGetTopmostTransform(transform);
            if (root == IntPtr.Zero)
                return;

            var observed = Interlocked.Increment(ref observedCount);
            var added = false;
            lock (sync)
            {
                added = !rootsSeen.ContainsKey(root);
                rootsSeen[root] = ++sequence;
                TrimLocked();
                rootCount = rootsSeen.Count;
            }

            if (added || observed <= 8 || observed % 1000 == 0)
                Plugin.Debug($"Hierarchy root observed from {source}: start=0x{transform.ToString("X")}, root=0x{root.ToString("X")}, added={added}, roots={rootCount}, observed={observed}.");
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref failureCount);
            if (failures <= 8)
                Plugin.Debug($"Transform root observe failed #{failures} from {source}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static List<IntPtr> SnapshotRoots()
    {
        lock (sync)
        {
            PruneStaleLocked();
            var roots = new List<KeyValuePair<IntPtr, long>>(rootsSeen);
            roots.Sort((left, right) => left.Key.ToInt64().CompareTo(right.Key.ToInt64()));
            var result = new List<IntPtr>(roots.Count);
            foreach (var pair in roots)
                result.Add(pair.Key);
            return result;
        }
    }

    public static void ForgetRoot(IntPtr root, string reason)
    {
        if (root == IntPtr.Zero)
            return;

        var removed = false;
        lock (sync)
            removed = rootsSeen.Remove(root);

        if (removed)
            Plugin.Debug($"Forgot stale hierarchy root 0x{root.ToString("X")} ({reason}).");
    }

    private static void PruneStaleLocked()
    {
        if (rootsSeen.Count == 0)
            return;

        var newest = 0L;
        foreach (var pair in rootsSeen)
            newest = Math.Max(newest, pair.Value);

        var cutoff = newest - MaxRootObservationAge;
        if (cutoff <= 0)
            return;

        var stale = new List<IntPtr>();
        foreach (var pair in rootsSeen)
        {
            if (pair.Value < cutoff)
                stale.Add(pair.Key);
        }

        foreach (var root in stale)
            rootsSeen.Remove(root);

        if (stale.Count > 0)
            Plugin.Debug($"Pruned {stale.Count} stale hierarchy roots older than observation sequence {cutoff}.");
    }

    private static void TrimLocked()
    {
        while (rootsSeen.Count > MaxKnownRoots)
        {
            var oldestRoot = IntPtr.Zero;
            var oldestSequence = long.MaxValue;
            foreach (var pair in rootsSeen)
            {
                if (pair.Value < oldestSequence)
                {
                    oldestRoot = pair.Key;
                    oldestSequence = pair.Value;
                }
            }

            if (oldestRoot == IntPtr.Zero)
                return;

            rootsSeen.Remove(oldestRoot);
        }
    }
}

