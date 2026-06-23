using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LimbusRuntimeUIInspector;

internal static class InspectorJobs
{
    private static readonly object sync = new();
    private static readonly Queue<InspectorJob> pending = new();
    private static readonly Dictionary<int, InspectorJob> jobs = new();
    private static int nextJobId;

    public static JobSnapshot QueueScan(string? filter, bool includeInactive, bool includeTransforms, int maxResults)
    {
        var job = InspectorJob.Scan(Interlocked.Increment(ref nextJobId), filter, includeInactive, includeTransforms, maxResults);
        Queue(job);
        return job.Snapshot();
    }

    public static JobSnapshot QueueEdit(EditRequest edit)
    {
        var job = InspectorJob.Edit(Interlocked.Increment(ref nextJobId), edit);
        Queue(job);
        return job.Snapshot();
    }

    public static JobSnapshot? Get(int id)
    {
        lock (sync)
            return jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;
    }

    public static void Pump()
    {
        InspectorJob? job = null;
        lock (sync)
        {
            if (pending.Count > 0)
                job = pending.Dequeue();
        }

        job?.Run();
    }

    private static void Queue(InspectorJob job)
    {
        lock (sync)
        {
            jobs[job.JobId] = job;
            if (UnityPumpDetour.EnsureInstalled())
            {
                pending.Enqueue(job);
                Plugin.Log.LogInfo($"Inspector {job.Kind} job {job.JobId} queued.");
            }
            else
            {
                job.Fail("Unable to install Unity main-thread pump.");
            }
        }
    }
}

internal sealed class InspectorJob
{
    private readonly EditRequest? edit;
    private readonly Stopwatch stopwatch = new();
    private readonly Dictionary<int, UiElement> scanElements = new();

    private ElementList? result;
    private UiElement? element;
    private string? error;

    private InspectorJob(int jobId, string kind, string? filter, bool includeInactive, bool includeTransforms, int maxResults, EditRequest? edit)
    {
        JobId = jobId;
        Kind = kind;
        State = "pending";
        Filter = filter;
        IncludeInactive = includeInactive;
        IncludeTransforms = includeTransforms;
        MaxResults = maxResults;
        this.edit = edit;
    }

    public int JobId { get; }
    public string Kind { get; }
    public string State { get; private set; }
    public string? Filter { get; }
    public bool IncludeInactive { get; }
    public bool IncludeTransforms { get; }
    public int MaxResults { get; }
    public long ElapsedMs => stopwatch.ElapsedMilliseconds;
    public int ResultCount => scanElements.Count;

    public static InspectorJob Scan(int jobId, string? filter, bool includeInactive, bool includeTransforms, int maxResults) =>
        new(jobId, "scan", filter, includeInactive, includeTransforms, maxResults, null);

    public static InspectorJob Edit(int jobId, EditRequest edit) =>
        new(jobId, "edit", null, includeInactive: true, includeTransforms: true, maxResults: 0, edit);

    public void Run()
    {
        State = "running";
        stopwatch.Restart();
        Plugin.Log.LogInfo($"Inspector {Kind} job {JobId} begin.");
        try
        {
            if (Kind == "scan")
            {
                var scan = UnityUiRuntime.ScanObservedRoots(Filter, IncludeInactive, IncludeTransforms, MaxResults);
                RectTransformCapture.ReplaceScanCache(scan.Elements);
                CompleteScan(scan.Elements.ConvertAll(item => item.Element), $"live-roots roots={scan.RootCount} visited={scan.VisitedCount} matched={scan.MatchedCount} returnedTransforms={scan.TransformOnlyCount} truncated={scan.Truncated} readFailures={scan.ReadFailureCount}");
            }
            else if (edit != null)
            {
                if (!RectTransformCapture.TryGetRect(edit.Id, out var rect))
                    throw new InvalidOperationException($"Element id {edit.Id} is not in the inspector cache. Run a scan first and select a captured element.");

                element = UnityUiRuntime.ApplyEdit(edit, rect);
                Plugin.Log.LogInfo($"Inspector edit job {JobId} complete: id={edit.Id}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
            }

            State = "complete";
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            State = "failed";
            Plugin.Log.LogWarning($"Inspector {Kind} job {JobId} failed after {stopwatch.ElapsedMilliseconds}ms: {ex}");
        }
        finally
        {
            if (State != "running")
                stopwatch.Stop();
        }
    }

    public void CompleteScan(IReadOnlyList<UiElement> elements, string reason)
    {
        if (State != "running")
            return;

        scanElements.Clear();
        foreach (var item in elements)
            scanElements[item.Id] = item;

        result = new ElementList(scanElements.Count, new List<UiElement>(elements));
        State = "complete";
        stopwatch.Stop();
        Plugin.Log.LogInfo($"Inspector scan job {JobId} complete ({reason}): count={result.Count}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    public void Fail(string message)
    {
        error = message;
        State = "failed";
        Plugin.Log.LogWarning($"Inspector {Kind} job {JobId} failed: {message}");
    }

    public JobSnapshot Snapshot() =>
        new(JobId, Kind, State, error, result, element, stopwatch.ElapsedMilliseconds);
}

