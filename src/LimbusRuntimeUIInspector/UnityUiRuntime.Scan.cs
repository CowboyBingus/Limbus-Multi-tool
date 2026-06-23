using LimbusShared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LimbusRuntimeUIInspector.Contracts;

namespace LimbusRuntimeUIInspector.Unity;

internal static partial class UnityUiRuntime
{
    public static ScanResult ScanObservedRoots(string? filter, bool includeInactive, bool includeTransforms, int maxResults)
    {
        var stopwatch = Stopwatch.StartNew();
        EnsureInitialized();

        var roots = CanvasRootRegistry.SnapshotRoots();
        InspectorHost.Log.LogInfo($"Inspector scan: live hierarchy traversal begin roots={roots.Count}, includeInactive={includeInactive}, includeTransforms={includeTransforms}, maxResults={maxResults}, filter='{filter ?? ""}'.");
        if (roots.Count == 0)
        {
            InspectorHost.Log.LogWarning("Inspector scan: no UI roots have been observed yet. Let the game reach an active UI screen, then scan again.");
            return new ScanResult(0, 0, 0, 0, 0, false, new List<ScannedUiElement>());
        }

        ForceUpdateCanvasesForInspector();
        var state = new ScanTraversalState(filter, includeInactive, includeTransforms, maxResults);
        PushRoots(roots, state);
        TraverseObservedRoots(state);
        SortCandidates(state.Candidates);

        var returnedCount = Math.Min(Math.Max(1, maxResults), state.Candidates.Count);
        var returned = new List<ScannedUiElement>(returnedCount);
        for (var i = 0; i < returnedCount; i++)
            returned.Add(state.Candidates[i]);

        var truncated = state.Stack.Count > 0 && state.VisitedCount >= MaxTraversalNodes;
        InspectorHost.Log.LogInfo($"Inspector scan: live hierarchy traversal complete roots={roots.Count}, visited={state.VisitedCount}, matched={state.Candidates.Count}, returned={returned.Count}, transformOnly={state.TransformOnlyCount}, truncated={truncated}, readFailures={state.ReadFailureCount}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        return new ScanResult(roots.Count, state.VisitedCount, state.Candidates.Count, state.TransformOnlyCount, state.ReadFailureCount, truncated, returned);
    }

    private static void PushRoots(IReadOnlyList<IntPtr> roots, ScanTraversalState state)
    {
        for (var i = roots.Count - 1; i >= 0; i--)
        {
            if (roots[i] != IntPtr.Zero)
                state.Stack.Push((roots[i], 0));
        }
    }

    private static void TraverseObservedRoots(ScanTraversalState state)
    {
        while (state.Stack.Count > 0 && state.VisitedCount < MaxTraversalNodes)
        {
            var (transform, depth) = state.Stack.Pop();
            if (!MarkVisited(transform, state))
                continue;

            state.VisitedCount++;
            if (!QueueChildrenForScan(transform, depth, state))
                continue;

            AddScanCandidate(transform, depth, state);
        }
    }

    private static bool MarkVisited(IntPtr transform, ScanTraversalState state)
    {
        return transform != IntPtr.Zero && state.VisitedTransforms.Add(transform);
    }

    private static bool QueueChildrenForScan(IntPtr transform, int depth, ScanTraversalState state)
    {
        if (depth >= 64)
            return true;

        var childrenPushed = PushChildren(transform, depth, state.Stack);
        if (childrenPushed || depth != 0)
            return true;

        CanvasRootRegistry.ForgetRoot(transform, "root child traversal failed");
        return false;
    }

    private static void AddScanCandidate(IntPtr transform, int depth, ScanTraversalState state)
    {
        var isRectTransform = IsRectTransformObject(transform);
        if (!isRectTransform && !state.IncludeTransforms)
            return;

        var item = TryReadScanItem(transform, depth, isRectTransform, state);
        if (item == null || !MatchesFilter(item, state.FilterText) || !state.SeenElements.Add(item.Id))
            return;

        state.Candidates.Add(new ScannedUiElement(item, transform));
        if (!isRectTransform)
            state.TransformOnlyCount++;
    }

    private static UiElement? TryReadScanItem(IntPtr transform, int depth, bool isRectTransform, ScanTraversalState state)
    {
        try
        {
            return isRectTransform
                ? TryReadRectElement(transform, state.IncludeInactive)
                : TryReadTransformElement(transform, state.IncludeInactive);
        }
        catch (Exception ex)
        {
            state.ReadFailureCount++;
            if (depth == 0)
                CanvasRootRegistry.ForgetRoot(transform, "root read failed");
            if (state.ReadFailureCount <= 12)
                InspectorHost.Debug($"Inspector scan: failed to read transform 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void SortCandidates(List<ScannedUiElement> candidates)
    {
        candidates.Sort((left, right) =>
        {
            var activeCompare = right.Element.ActiveInHierarchy.CompareTo(left.Element.ActiveInHierarchy);
            if (activeCompare != 0)
                return activeCompare;

            var selfCompare = right.Element.ActiveSelf.CompareTo(left.Element.ActiveSelf);
            if (selfCompare != 0)
                return selfCompare;

            var pathCompare = string.Compare(left.Element.Path, right.Element.Path, StringComparison.OrdinalIgnoreCase);
            return pathCompare != 0 ? pathCompare : left.Element.Id.CompareTo(right.Element.Id);
        });
    }

    private sealed class ScanTraversalState
    {
        public ScanTraversalState(string? filter, bool includeInactive, bool includeTransforms, int maxResults)
        {
            IncludeInactive = includeInactive;
            IncludeTransforms = includeTransforms;
            FilterText = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
            Candidates = new List<ScannedUiElement>(Math.Min(Math.Max(1, maxResults), 256));
        }

        public bool IncludeInactive { get; }
        public bool IncludeTransforms { get; }
        public string? FilterText { get; }
        public List<ScannedUiElement> Candidates { get; }
        public HashSet<IntPtr> VisitedTransforms { get; } = new();
        public HashSet<int> SeenElements { get; } = new();
        public Stack<(IntPtr Transform, int Depth)> Stack { get; } = new();
        public int VisitedCount { get; set; }
        public int ReadFailureCount { get; set; }
        public int TransformOnlyCount { get; set; }
    }

    private static bool PushChildren(IntPtr transform, int depth, Stack<(IntPtr Transform, int Depth)> stack)
    {
        int childCount;
        try
        {
            childCount = Il2CppInvoke.Int32(transformGetChildCount, transform);
        }
        catch (Exception ex)
        {
            InspectorHost.Debug($"Inspector scan: failed to read child count for 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        for (var i = childCount - 1; i >= 0; i--)
        {
            try
            {
                var child = Il2CppInvoke.ObjectWithIntArg(transformGetChild, transform, i);
                if (child != IntPtr.Zero)
                    stack.Push((child, depth + 1));
            }
            catch (Exception ex)
            {
                InspectorHost.Debug($"Inspector scan: failed to read child {i} for 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        return true;
    }

    private static bool MatchesFilter(UiElement item, string? filter)
    {
        if (filter == null)
            return true;

        return item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

}
