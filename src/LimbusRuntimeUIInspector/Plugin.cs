using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace LimbusRuntimeUIInspector;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbusruntimeuiinspector";
    public const string NAME = "LimbusRuntimeUIInspector";
    public const string VERSION = "0.2.0";

    internal static new ManualLogSource Log = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<int> Port = null!;
    internal static ConfigEntry<int> MaxResults = null!;
    internal static ConfigEntry<int> RequestTimeoutMilliseconds = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    public override void Load()
    {
        Log = base.Log;
        Enabled = Config.Bind("General", "Enabled", true, "Starts the local runtime UI inspector server.");
        Port = Config.Bind("Server", "Port", 43129, "Localhost HTTP port for the inspector browser UI.");
        MaxResults = Config.Bind("Inspector", "MaxResults", 1000, "Maximum live element rows returned per scan.");
        RequestTimeoutMilliseconds = Config.Bind("Inspector", "RequestTimeoutMilliseconds", 2000, "Reserved for future async inspector operations.");
        DebugLogging = Config.Bind("Diagnostics", "DebugLogging", true, "Writes detailed inspector server and scan/edit job lifecycle logs.");

        Debug("Plugin.Load begin.");
        if (Enabled.Value)
        {
            CanvasRootObserveDetour.Install();
            RectTransformRootObserveDetour.Install();
            GameObjectRootObserveDetour.Install();
            InspectorServer.Start(Port.Value);
        }

        Log.LogInfo($"{NAME} {VERSION} loaded. Enabled={Enabled.Value}, Url=http://127.0.0.1:{Port.Value}/");
    }

    public override bool Unload()
    {
        InspectorServer.Stop();
        UnityPumpDetour.Uninstall();
        GameObjectRootObserveDetour.Uninstall();
        RectTransformRootObserveDetour.Uninstall();
        CanvasRootObserveDetour.Uninstall();
        return true;
    }

    internal static void Debug(string message)
    {
        if (DebugLogging != null && DebugLogging.Value)
            Log.LogInfo($"[debug] {message}");
    }
}

internal static class InspectorServer
{
    private static readonly object sync = new();
    private static TcpListener? listener;
    private static Thread? thread;
    private static volatile bool running;
    private static int nextRequestId;

    public static void Start(int port)
    {
        lock (sync)
        {
            if (running)
                return;

            Plugin.Debug($"Starting localhost server on port {port}.");
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            thread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "LimbusRuntimeUIInspectorServer"
            };
            thread.Start();
            Plugin.Debug("Inspector server listener thread started.");
        }
    }

    public static void Stop()
    {
        lock (sync)
        {
            running = false;
            try { listener?.Stop(); } catch { }
            listener = null;
            Plugin.Debug("Inspector server stopped.");
        }
    }

    private static void AcceptLoop()
    {
        while (running)
        {
            try
            {
                var client = listener!.AcceptTcpClient();
                _ = ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException)
            {
                if (running)
                    Plugin.Log.LogWarning("Inspector server socket stopped unexpectedly.");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Inspector server accept failed: {ex.Message}");
            }
        }
    }

    private static void HandleClient(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            using var stream = client.GetStream();

            try
            {
                var request = HttpRequest.Read(stream);
                if (request == null)
                    return;

                var requestId = Interlocked.Increment(ref nextRequestId);
                Plugin.Debug($"HTTP {requestId} {request.Method} {request.Path} begin.");
                if (request.Method == "GET" && request.Path == "/")
                {
                    WriteResponse(stream, 200, "text/html; charset=utf-8", InspectorPage.Html);
                    Plugin.Debug($"HTTP {requestId} {request.Path} served page.");
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/scan")
                {
                    var filter = request.Query.TryGetValue("filter", out var value) ? value : string.Empty;
                    var includeInactive = request.Query.TryGetValue("includeInactive", out var inactive) && inactive == "1";
                    var includeTransforms = !request.Query.TryGetValue("includeTransforms", out var transforms) || transforms == "1";
                    var maxResults = Math.Max(1, Plugin.MaxResults.Value);
                    if (request.Query.TryGetValue("maxResults", out var rawMaxResults) &&
                        int.TryParse(rawMaxResults, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedMaxResults))
                    {
                        maxResults = Math.Clamp(requestedMaxResults, 1, 5000);
                    }

                    var job = InspectorJobs.QueueScan(filter, includeInactive, includeTransforms, maxResults);
                    WriteResponse(stream, 200, "application/json", JsonSerializer.Serialize(new ApiResponse<JobSnapshot>(true, job, null), JsonOptions.Default));
                    Plugin.Debug($"HTTP {requestId} queued scan job {job.JobId}.");
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/job")
                {
                    if (!request.Query.TryGetValue("id", out var rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jobId))
                    {
                        WriteResponse(stream, 400, "application/json", "{\"ok\":false,\"error\":\"Missing job id.\"}");
                        return;
                    }

                    var snapshot = InspectorJobs.Get(jobId);
                    if (snapshot == null)
                    {
                        WriteResponse(stream, 404, "application/json", "{\"ok\":false,\"error\":\"Job not found.\"}");
                        return;
                    }

                    WriteResponse(stream, 200, "application/json", JsonSerializer.Serialize(new ApiResponse<JobSnapshot>(true, snapshot, null), JsonOptions.Default));
                    Plugin.Debug($"HTTP {requestId} returned job {jobId} state={snapshot.State}.");
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/edit")
                {
                    var edit = JsonSerializer.Deserialize<EditRequest>(request.Body, JsonOptions.Default);
                    if (edit == null || edit.Id == 0)
                    {
                        WriteResponse(stream, 400, "application/json", "{\"ok\":false,\"error\":\"Missing element id.\"}");
                        return;
                    }

                    var job = InspectorJobs.QueueEdit(edit);
                    WriteResponse(stream, 200, "application/json", JsonSerializer.Serialize(new ApiResponse<JobSnapshot>(true, job, null), JsonOptions.Default));
                    Plugin.Debug($"HTTP {requestId} queued edit job {job.JobId} for id={edit.Id}.");
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/status")
                {
                    WriteResponse(stream, 200, "application/json", "{\"ok\":true,\"name\":\"LimbusRuntimeUIInspector\"}");
                    Plugin.Debug($"HTTP {requestId} status ok.");
                    return;
                }

                WriteResponse(stream, 404, "application/json", "{\"ok\":false,\"error\":\"Not found.\"}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Inspector request failed: {ex}");
                WriteResponse(stream, 500, "application/json", JsonSerializer.Serialize(new ApiResponse<object>(false, null, ex.Message), JsonOptions.Default));
            }
        }
    }

    private static void WriteResponse(Stream stream, int status, string contentType, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            429 => "Too Many Requests",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "OK"
        };
        var headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            "Access-Control-Allow-Origin: http://127.0.0.1\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
    }
}

internal static class UnityPumpDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StaticVoidDelegate(IntPtr methodInfo);

    private static NativeDetour? detour;
    private static StaticVoidDelegate? original;
    private static readonly StaticVoidDelegate replacement = Replacement;
    [ThreadStatic] private static bool inReplacement;

    public static bool EnsureInstalled()
    {
        if (detour != null)
            return true;

        try
        {
            Plugin.Debug("Installing on-demand Canvas.SendWillRenderCanvases pump.");
            var canvasClass = IL2CPP.GetIl2CppClass("UnityEngine.UIModule.dll", "UnityEngine", "Canvas");
            if (canvasClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Runtime UI inspector pump install failed: UnityEngine.Canvas class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(canvasClass, "SendWillRenderCanvases", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Runtime UI inspector pump install failed: Canvas.SendWillRenderCanvases was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Runtime UI inspector pump install failed: Canvas.SendWillRenderCanvases pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<StaticVoidDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"Runtime UI inspector on-demand pump installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Runtime UI inspector pump install failed: {ex}");
            return false;
        }
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
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
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
            InspectorJobs.Pump();
        }
        finally
        {
            inReplacement = false;
        }
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
            Plugin.Debug("Installing CanvasScaler.OnEnable root observer.");
            var scalerClass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
            if (scalerClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Canvas root observer install failed: UnityEngine.UI.CanvasScaler class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(scalerClass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Canvas root observer install failed: CanvasScaler.OnEnable was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Canvas root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<OnEnableDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"Canvas root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Canvas root observer install failed: {ex}");
            return false;
        }
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
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void Replacement(IntPtr self, IntPtr methodInfo)
    {
        original?.Invoke(self, methodInfo);
        CanvasRootRegistry.ObserveComponent(self, "CanvasScaler.OnEnable");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
            Plugin.Debug("Installing RectTransform relayout root observer.");
            var rectClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");
            if (rectClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("RectTransform root observer install failed: RectTransform class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(rectClass, "SendReapplyDrivenProperties", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("RectTransform root observer install failed: SendReapplyDrivenProperties was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("RectTransform root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SendReapplyDrivenPropertiesDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"RectTransform relayout root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"RectTransform root observer install failed: {ex}");
            return false;
        }
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
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void Replacement(IntPtr rectTransform, IntPtr methodInfo)
    {
        original?.Invoke(rectTransform, methodInfo);
        CanvasRootRegistry.ObserveTransformAndAncestors(rectTransform, "RectTransform.SendReapplyDrivenProperties");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
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
            Plugin.Debug("Installing GameObject.SetActive root observer.");
            var gameObjectClass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
            if (gameObjectClass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("GameObject root observer install failed: GameObject class was not resolved.");
                return false;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(gameObjectClass, "SetActive", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("GameObject root observer install failed: GameObject.SetActive was not resolved.");
                return false;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("GameObject root observer install failed: method pointer was null.");
                return false;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SetActiveDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"GameObject.SetActive root observer installed at {Ptr(methodPointer)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"GameObject root observer install failed: {ex}");
            return false;
        }
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
            // Unity teardown can race plugin unload.
        }
        finally
        {
            detour = null;
            original = null;
        }
    }

    private static void Replacement(IntPtr self, byte active, IntPtr methodInfo)
    {
        original?.Invoke(self, active, methodInfo);
        if (active != 0)
            CanvasRootRegistry.ObserveGameObject(self, "GameObject.SetActive");
    }

    private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
}

internal static class CanvasRootRegistry
{
    private const int MaxKnownRoots = 512;

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

        var addedCount = 0;
        int rootCount;
        try
        {
            var observed = Interlocked.Increment(ref observedCount);
            lock (sync)
            {
                if (rootsSeen.ContainsKey(transform))
                {
                    rootsSeen[transform] = ++sequence;
                    TrimLocked();
                    rootCount = rootsSeen.Count;
                    if (observed <= 8 || observed % 1000 == 0)
                        Plugin.Debug($"UI root candidate refreshed from {source}: start=0x{transform.ToString("X")}, roots={rootCount}, observed={observed}.");
                    return;
                }
            }

            var current = transform;
            for (var depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
            {
                lock (sync)
                {
                    if (!rootsSeen.ContainsKey(current))
                        addedCount++;
                    rootsSeen[current] = ++sequence;
                    TrimLocked();
                    rootCount = rootsSeen.Count;
                }

                current = UnityUiRuntime.TryGetParentTransform(current);
            }

            lock (sync)
                rootCount = rootsSeen.Count;

            if (observed <= 8 || observed % 1000 == 0)
                Plugin.Debug($"UI root candidates observed from {source}: start=0x{transform.ToString("X")}, added={addedCount}, roots={rootCount}, observed={observed}.");
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
            var roots = new List<KeyValuePair<IntPtr, long>>(rootsSeen);
            roots.Sort((left, right) => right.Value.CompareTo(left.Value));
            var result = new List<IntPtr>(roots.Count);
            foreach (var pair in roots)
                result.Add(pair.Key);
            return result;
        }
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

internal static class RectTransformCapture
{
    private const int MaxKnownElements = 2000;

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

        Plugin.Debug($"Inspector edit cache refreshed from live scan: cached={CachedCount}.");
    }

    public static bool TryGetRect(int id, out IntPtr rect)
    {
        lock (sync)
            return knownRects.TryGetValue(id, out rect);
    }
}

internal sealed class HttpRequest
{
    public string Method { get; private set; } = "";
    public string Path { get; private set; } = "";
    public Dictionary<string, string> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; private set; } = "";

    public static HttpRequest? Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
        var requestLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(requestLine))
            return null;

        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
            return null;

        var request = new HttpRequest
        {
            Method = parts[0].ToUpperInvariant()
        };

        var target = parts[1];
        var queryIndex = target.IndexOf('?');
        request.Path = Uri.UnescapeDataString(queryIndex >= 0 ? target[..queryIndex] : target);
        if (queryIndex >= 0)
            ParseQuery(target[(queryIndex + 1)..], request.Query);

        var contentLength = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
        }

        if (contentLength > 0)
        {
            var buffer = new char[Math.Min(contentLength, 1024 * 1024)];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = reader.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                    break;
                offset += read;
            }
            request.Body = new string(buffer, 0, offset);
        }

        return request;
    }

    private static void ParseQuery(string raw, Dictionary<string, string> query)
    {
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                query[WebUtility.UrlDecode(pair)] = "";
                continue;
            }

            query[WebUtility.UrlDecode(pair[..equals])] = WebUtility.UrlDecode(pair[(equals + 1)..]);
        }
    }
}

internal static unsafe class UnityUiRuntime
{
    private const int MaxTraversalNodes = 25000;

    private static bool initialized;
    private static IntPtr objectClass;
    private static IntPtr componentClass;
    private static IntPtr gameObjectClass;
    private static IntPtr transformClass;
    private static IntPtr rectTransformClass;
    private static IntPtr canvasClass;

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
        var gameObject = InvokeObject(componentGetGameObject, component);
        return TryGetTransformFromGameObject(gameObject);
    }

    public static IntPtr TryGetTransformFromGameObject(IntPtr gameObject)
    {
        EnsureInitialized();
        return gameObject == IntPtr.Zero ? IntPtr.Zero : InvokeObject(gameObjectGetTransform, gameObject);
    }

    public static IntPtr TryGetParentTransform(IntPtr transform)
    {
        EnsureInitialized();
        return transform == IntPtr.Zero ? IntPtr.Zero : InvokeObject(transformGetParent, transform);
    }

    public static ScanResult ScanObservedRoots(string? filter, bool includeInactive, bool includeTransforms, int maxResults)
    {
        var stopwatch = Stopwatch.StartNew();
        EnsureInitialized();

        var roots = CanvasRootRegistry.SnapshotRoots();
        Plugin.Log.LogInfo($"Inspector scan: live hierarchy traversal begin roots={roots.Count}, includeInactive={includeInactive}, includeTransforms={includeTransforms}, maxResults={maxResults}, filter='{filter ?? ""}'.");

        if (roots.Count == 0)
        {
            Plugin.Log.LogWarning("Inspector scan: no UI roots have been observed yet. Let the game reach an active UI screen, then scan again.");
            return new ScanResult(0, 0, 0, 0, 0, false, new List<ScannedUiElement>());
        }

        ForceUpdateCanvasesForInspector();

        var candidates = new List<ScannedUiElement>(Math.Min(Math.Max(1, maxResults), 256));
        var visitedTransforms = new HashSet<IntPtr>();
        var seenElements = new HashSet<int>();
        var stack = new Stack<(IntPtr Transform, int Depth)>();
        var visitedCount = 0;
        var readFailureCount = 0;
        var filterText = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var transformOnlyCount = 0;

        for (var i = roots.Count - 1; i >= 0; i--)
        {
            if (roots[i] != IntPtr.Zero)
                stack.Push((roots[i], 0));
        }

        while (stack.Count > 0 && visitedCount < MaxTraversalNodes)
        {
            var (transform, depth) = stack.Pop();
            if (transform == IntPtr.Zero || !visitedTransforms.Add(transform))
                continue;

            visitedCount++;
            if (depth < 64)
                PushChildren(transform, depth, stack);

            var isRectTransform = IsRectTransformObject(transform);
            if (!isRectTransform && !includeTransforms)
                continue;

            UiElement? item;
            try
            {
                item = isRectTransform
                    ? TryReadRectElement(transform, includeInactive)
                    : TryReadTransformElement(transform, includeInactive);
            }
            catch (Exception ex)
            {
                readFailureCount++;
                if (readFailureCount <= 12)
                    Plugin.Debug($"Inspector scan: failed to read transform 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (item == null)
                continue;

            if (!MatchesFilter(item, filterText))
                continue;

            if (!seenElements.Add(item.Id))
                continue;

            candidates.Add(new ScannedUiElement(item, transform));
            if (!isRectTransform)
                transformOnlyCount++;
            if (!includeInactive && candidates.Count >= maxResults)
                break;
        }

        candidates.Sort((left, right) =>
        {
            var activeCompare = right.Element.ActiveInHierarchy.CompareTo(left.Element.ActiveInHierarchy);
            if (activeCompare != 0)
                return activeCompare;

            var selfCompare = right.Element.ActiveSelf.CompareTo(left.Element.ActiveSelf);
            if (selfCompare != 0)
                return selfCompare;

            return string.Compare(left.Element.Path, right.Element.Path, StringComparison.OrdinalIgnoreCase);
        });

        var returnedCount = Math.Min(Math.Max(1, maxResults), candidates.Count);
        var returned = new List<ScannedUiElement>(returnedCount);
        for (var i = 0; i < returnedCount; i++)
            returned.Add(candidates[i]);

        var truncated = stack.Count > 0 && visitedCount >= MaxTraversalNodes;
        Plugin.Log.LogInfo($"Inspector scan: live hierarchy traversal complete roots={roots.Count}, visited={visitedCount}, matched={candidates.Count}, returned={returned.Count}, transformOnly={transformOnlyCount}, truncated={truncated}, readFailures={readFailureCount}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        return new ScanResult(roots.Count, visitedCount, candidates.Count, transformOnlyCount, readFailureCount, truncated, returned);
    }

    public static UiElement? TryReadRectElement(IntPtr rect, bool includeInactive)
    {
        EnsureInitialized();
        var gameObject = InvokeObject(componentGetGameObject, rect);
        if (gameObject == IntPtr.Zero)
            return null;

        var activeInHierarchy = InvokeBool(gameObjectGetActiveInHierarchy, gameObject);
        if (!includeInactive && !activeInHierarchy)
            return null;

        return ReadElement(rect, gameObject, BuildPath(rect), activeInHierarchy);
    }

    public static UiElement? TryReadTransformElement(IntPtr transform, bool includeInactive)
    {
        EnsureInitialized();
        var gameObject = InvokeObject(componentGetGameObject, transform);
        if (gameObject == IntPtr.Zero)
            return null;

        var activeInHierarchy = InvokeBool(gameObjectGetActiveInHierarchy, gameObject);
        if (!includeInactive && !activeInHierarchy)
            return null;

        return ReadTransformElement(transform, gameObject, BuildPath(transform), activeInHierarchy);
    }

    public static void ForceUpdateCanvasesForInspector()
    {
        var stopwatch = Stopwatch.StartNew();
        Plugin.Log.LogInfo("Inspector scan: Canvas.ForceUpdateCanvases begin.");
        EnsureInitialized();
        InvokeObject(canvasForceUpdateCanvases, IntPtr.Zero);
        Plugin.Log.LogInfo($"Inspector scan: Canvas.ForceUpdateCanvases complete after {stopwatch.ElapsedMilliseconds}ms.");
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

    public static UiElement ApplyEdit(EditRequest edit, IntPtr transform)
    {
        var stopwatch = Stopwatch.StartNew();
        Plugin.Log.LogInfo($"Inspector edit: apply begin for id={edit.Id}.");
        EnsureInitialized();

        var gameObject = InvokeObject(componentGetGameObject, transform);
        if (gameObject == IntPtr.Zero)
            throw new InvalidOperationException($"Cached inspector target for id {edit.Id} no longer has a GameObject.");

        var actualId = InvokeInt(objectGetInstanceId, gameObject);
        if (actualId != edit.Id)
            throw new InvalidOperationException($"Cached inspector target id mismatch. Expected {edit.Id}, found {actualId}. Run a fresh scan.");

        if (edit.Active.HasValue)
            InvokeSetBool(gameObjectSetActive, gameObject, edit.Active.Value);

        var isRectTransform = IsRectTransformObject(transform);

        if (isRectTransform && (edit.AnchoredX.HasValue || edit.AnchoredY.HasValue))
        {
            var value = InvokeVector2(rectGetAnchoredPosition, transform);
            if (edit.AnchoredX.HasValue) value.X = edit.AnchoredX.Value;
            if (edit.AnchoredY.HasValue) value.Y = edit.AnchoredY.Value;
            InvokeSetVector2(rectSetAnchoredPosition, transform, value);
        }
        else if (!isRectTransform && (edit.AnchoredX.HasValue || edit.AnchoredY.HasValue))
        {
            var value = InvokeVector3(transformGetLocalPosition, transform);
            if (edit.AnchoredX.HasValue) value.X = edit.AnchoredX.Value;
            if (edit.AnchoredY.HasValue) value.Y = edit.AnchoredY.Value;
            InvokeSetVector3(transformSetLocalPosition, transform, value);
        }

        if (isRectTransform && (edit.Width.HasValue || edit.Height.HasValue))
        {
            var value = InvokeVector2(rectGetSizeDelta, transform);
            if (edit.Width.HasValue) value.X = edit.Width.Value;
            if (edit.Height.HasValue) value.Y = edit.Height.Value;
            InvokeSetVector2(rectSetSizeDelta, transform, value);
        }

        if (isRectTransform && (edit.AnchorMinX.HasValue || edit.AnchorMinY.HasValue))
        {
            var value = InvokeVector2(rectGetAnchorMin, transform);
            if (edit.AnchorMinX.HasValue) value.X = edit.AnchorMinX.Value;
            if (edit.AnchorMinY.HasValue) value.Y = edit.AnchorMinY.Value;
            InvokeSetVector2(rectSetAnchorMin, transform, value);
        }

        if (isRectTransform && (edit.AnchorMaxX.HasValue || edit.AnchorMaxY.HasValue))
        {
            var value = InvokeVector2(rectGetAnchorMax, transform);
            if (edit.AnchorMaxX.HasValue) value.X = edit.AnchorMaxX.Value;
            if (edit.AnchorMaxY.HasValue) value.Y = edit.AnchorMaxY.Value;
            InvokeSetVector2(rectSetAnchorMax, transform, value);
        }

        if (isRectTransform && (edit.PivotX.HasValue || edit.PivotY.HasValue))
        {
            var value = InvokeVector2(rectGetPivot, transform);
            if (edit.PivotX.HasValue) value.X = edit.PivotX.Value;
            if (edit.PivotY.HasValue) value.Y = edit.PivotY.Value;
            InvokeSetVector2(rectSetPivot, transform, value);
        }

        if (edit.ScaleX.HasValue || edit.ScaleY.HasValue || edit.ScaleZ.HasValue)
        {
            var value = InvokeVector3(transformGetLocalScale, transform);
            if (edit.ScaleX.HasValue) value.X = edit.ScaleX.Value;
            if (edit.ScaleY.HasValue) value.Y = edit.ScaleY.Value;
            if (edit.ScaleZ.HasValue) value.Z = edit.ScaleZ.Value;
            InvokeSetVector3(transformSetLocalScale, transform, value);
        }

        var updated = isRectTransform
            ? ReadElement(transform, gameObject, BuildPath(transform), InvokeBool(gameObjectGetActiveInHierarchy, gameObject))
            : ReadTransformElement(transform, gameObject, BuildPath(transform), InvokeBool(gameObjectGetActiveInHierarchy, gameObject));
        Plugin.Log.LogInfo($"Inspector edit: applied id={edit.Id} in {stopwatch.ElapsedMilliseconds}ms.");
        return updated;
    }

    private static UiElement ReadElement(IntPtr rect, IntPtr gameObject, string path, bool activeInHierarchy)
    {
        var anchored = InvokeVector2(rectGetAnchoredPosition, rect);
        var size = InvokeVector2(rectGetSizeDelta, rect);
        var anchorMin = InvokeVector2(rectGetAnchorMin, rect);
        var anchorMax = InvokeVector2(rectGetAnchorMax, rect);
        var pivot = InvokeVector2(rectGetPivot, rect);
        var scale = InvokeVector3(transformGetLocalScale, rect);

        return new UiElement(
            InvokeInt(objectGetInstanceId, gameObject),
            InvokeString(objectGetName, gameObject),
            path,
            "RectTransform",
            InvokeBool(gameObjectGetActiveSelf, gameObject),
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
        var position = InvokeVector3(transformGetLocalPosition, transform);
        var scale = InvokeVector3(transformGetLocalScale, transform);

        return new UiElement(
            InvokeInt(objectGetInstanceId, gameObject),
            InvokeString(objectGetName, gameObject),
            path,
            "Transform",
            InvokeBool(gameObjectGetActiveSelf, gameObject),
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

    private static void PushChildren(IntPtr transform, int depth, Stack<(IntPtr Transform, int Depth)> stack)
    {
        int childCount;
        try
        {
            childCount = InvokeInt(transformGetChildCount, transform);
        }
        catch (Exception ex)
        {
            Plugin.Debug($"Inspector scan: failed to read child count for 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
            return;
        }

        for (var i = childCount - 1; i >= 0; i--)
        {
            try
            {
                var child = InvokeObjectIntArg(transformGetChild, transform, i);
                if (child != IntPtr.Zero)
                    stack.Push((child, depth + 1));
            }
            catch (Exception ex)
            {
                Plugin.Debug($"Inspector scan: failed to read child {i} for 0x{transform.ToString("X")} ({TryDescribeObject(transform)}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static bool MatchesFilter(UiElement item, string? filter)
    {
        if (filter == null)
            return true;

        return item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRectTransformObject(IntPtr obj)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (klass == IntPtr.Zero)
                return false;

            if (klass == rectTransformClass)
                return true;

            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
            return name == "RectTransform";
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPath(IntPtr transform)
    {
        var names = new List<string>();
        var current = transform;
        for (var depth = 0; depth < 64 && current != IntPtr.Zero; depth++)
        {
            var name = InvokeString(objectGetName, current);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
            current = InvokeObject(transformGetParent, current);
        }

        names.Reverse();
        return "/" + string.Join("/", names);
    }

    private static void EnsureInitialized()
    {
        if (initialized)
            return;

        objectClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
        Plugin.Debug("Resolved UnityEngine.Object.");
        componentClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Component");
        Plugin.Debug("Resolved UnityEngine.Component.");
        gameObjectClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "GameObject");
        Plugin.Debug("Resolved UnityEngine.GameObject.");
        transformClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
        Plugin.Debug("Resolved UnityEngine.Transform.");
        rectTransformClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");
        Plugin.Debug("Resolved UnityEngine.RectTransform.");
        canvasClass = RequireClass("UnityEngine.UIModule.dll", "UnityEngine", "Canvas");
        Plugin.Debug("Resolved UnityEngine.Canvas.");

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

    private static IntPtr RequireClass(string assembly, string ns, string name)
    {
        var klass = IL2CPP.GetIl2CppClass(assembly, ns, name);
        if (klass == IntPtr.Zero)
            throw new MissingMemberException($"IL2CPP class not found: {ns}.{name} in {assembly}");
        return klass;
    }

    private static IntPtr RequireMethod(IntPtr klass, string name, int args)
    {
        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, args);
        if (method == IntPtr.Zero)
            throw new MissingMethodException($"IL2CPP method not found: {name}/{args}");
        return method;
    }

    private static unsafe IntPtr InvokeObject(IntPtr method, IntPtr instance, void** args = null)
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, args, ref exception);
        if (exception != IntPtr.Zero)
            throw new InvalidOperationException($"IL2CPP invocation failed: exception=0x{exception.ToInt64():X}");
        return result;
    }

    private static string InvokeString(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return result == IntPtr.Zero ? "" : IL2CPP.Il2CppStringToManaged(result) ?? "";
    }

    private static int InvokeInt(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(result));
    }

    private static bool InvokeBool(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(result)) != 0;
    }

    private static unsafe void InvokeSetBool(IntPtr method, IntPtr instance, bool value)
    {
        var raw = value ? (byte)1 : (byte)0;
        var args = stackalloc void*[1];
        args[0] = &raw;
        InvokeObject(method, instance, args);
    }

    private static unsafe IntPtr InvokeObjectIntArg(IntPtr method, IntPtr instance, int value)
    {
        var args = stackalloc void*[1];
        args[0] = &value;
        return InvokeObject(method, instance, args);
    }

    private static Vector2Value InvokeVector2(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return Marshal.PtrToStructure<Vector2Value>(IL2CPP.il2cpp_object_unbox(result));
    }

    private static Vector3Value InvokeVector3(IntPtr method, IntPtr instance)
    {
        var result = InvokeObject(method, instance);
        return Marshal.PtrToStructure<Vector3Value>(IL2CPP.il2cpp_object_unbox(result));
    }

    private static unsafe void InvokeSetVector2(IntPtr method, IntPtr instance, Vector2Value value)
    {
        var args = stackalloc void*[1];
        args[0] = &value;
        InvokeObject(method, instance, args);
    }

    private static unsafe void InvokeSetVector3(IntPtr method, IntPtr instance, Vector3Value value)
    {
        var args = stackalloc void*[1];
        args[0] = &value;
        InvokeObject(method, instance, args);
    }

    private static float Round(float value) => MathF.Round(value, 3);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vector2Value
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vector3Value
{
    public float X;
    public float Y;
    public float Z;
}

internal sealed record ScannedUiElement(UiElement Element, IntPtr RectTransform);

internal sealed record ScanResult(
    int RootCount,
    int VisitedCount,
    int MatchedCount,
    int TransformOnlyCount,
    int ReadFailureCount,
    bool Truncated,
    List<ScannedUiElement> Elements);

internal sealed record ElementList(int Count, IReadOnlyList<UiElement> Elements);

internal sealed record JobSnapshot(
    int JobId,
    string Kind,
    string State,
    string? Error,
    ElementList? Result,
    UiElement? Element,
    long ElapsedMs);

internal sealed record UiElement(
    int Id,
    string Name,
    string Path,
    string Kind,
    bool ActiveSelf,
    bool ActiveInHierarchy,
    float AnchoredX,
    float AnchoredY,
    float Width,
    float Height,
    float AnchorMinX,
    float AnchorMinY,
    float AnchorMaxX,
    float AnchorMaxY,
    float PivotX,
    float PivotY,
    float ScaleX,
    float ScaleY,
    float ScaleZ);

internal sealed class EditRequest
{
    public int Id { get; set; }
    public bool? Active { get; set; }
    public float? AnchoredX { get; set; }
    public float? AnchoredY { get; set; }
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float? AnchorMinX { get; set; }
    public float? AnchorMinY { get; set; }
    public float? AnchorMaxX { get; set; }
    public float? AnchorMaxY { get; set; }
    public float? PivotX { get; set; }
    public float? PivotY { get; set; }
    public float? ScaleX { get; set; }
    public float? ScaleY { get; set; }
    public float? ScaleZ { get; set; }
}

internal sealed record ApiResponse<T>(bool Ok, T? Data, string? Error);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

internal static class InspectorPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Limbus Runtime UI Inspector</title>
<style>
:root{color-scheme:dark;--bg:#15171a;--panel:#20242a;--panel2:#272c33;--line:#3a424d;--text:#eef2f6;--muted:#9aa8b7;--accent:#46b3a2;--danger:#d46a6a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:13px/1.4 "Segoe UI",Arial,sans-serif}
header{height:52px;display:flex;align-items:center;gap:12px;padding:0 16px;border-bottom:1px solid var(--line);background:#111316}
h1{font-size:16px;margin:0;font-weight:650}button,input,label{font:inherit}button{height:30px;border:1px solid var(--line);background:var(--panel2);color:var(--text);border-radius:5px;padding:0 10px;cursor:pointer}button:hover{border-color:var(--accent)}button.primary{background:var(--accent);border-color:var(--accent);color:#07110f}button.danger{border-color:var(--danger);color:#ffdada}
input{height:30px;border:1px solid var(--line);background:#111316;color:var(--text);border-radius:5px;padding:0 8px;min-width:0}
main{display:grid;grid-template-columns:minmax(360px,1fr) 360px;height:calc(100vh - 52px)}
.left,.right{min-height:0}.left{display:flex;flex-direction:column}.toolbar{display:flex;gap:8px;align-items:center;padding:10px;border-bottom:1px solid var(--line);background:var(--panel)}
.toolbar input[type=text]{flex:1}.tablewrap{overflow:auto;min-height:0}table{width:100%;border-collapse:collapse;table-layout:fixed}th,td{padding:7px 8px;border-bottom:1px solid #2c333b;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-align:left}th{position:sticky;top:0;background:#1b1f24;color:var(--muted);z-index:1}tr{cursor:pointer}tr:hover, tr.selected{background:#263038}
.right{border-left:1px solid var(--line);background:var(--panel);padding:12px;overflow:auto}.muted{color:var(--muted)}.path{font-family:Consolas,monospace;word-break:break-all;white-space:normal;background:#15191d;border:1px solid var(--line);padding:8px;border-radius:5px;margin:8px 0 12px}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:10px}.grid.three{grid-template-columns:1fr 1fr 1fr}.field span{display:block;color:var(--muted);font-size:11px;margin-bottom:3px}.field input{width:100%}.actions{display:flex;gap:8px;align-items:center;margin:12px 0}.status{color:var(--muted);padding-left:6px}.empty{padding:24px;color:var(--muted)}
</style>
</head>
<body>
<header><h1>Limbus Runtime UI Inspector</h1><span class="muted">Local runtime Transform editor</span></header>
<main>
<section class="left">
<div class="toolbar">
<input id="filter" type="text" placeholder="Filter by name or hierarchy path">
<label><input id="inactive" type="checkbox"> include inactive</label>
<label><input id="transforms" type="checkbox" checked> transforms</label>
<input id="limit" type="number" min="1" max="5000" step="100" value="1000" title="Maximum returned rows" style="width:76px">
<button id="refresh" class="primary">Refresh</button>
</div>
<div class="tablewrap"><table><thead><tr><th style="width:80px">ID</th><th style="width:120px">Type</th><th style="width:210px">Name</th><th>Path</th><th style="width:70px">Active</th></tr></thead><tbody id="rows"></tbody></table></div>
</section>
<aside class="right">
<div id="empty" class="empty">Select an element to edit it.</div>
<div id="editor" hidden>
<h2 id="title" style="font-size:15px;margin:0"></h2>
<div id="path" class="path"></div>
<label><input id="active" type="checkbox"> active self</label>
<h3 id="posTitle">Position</h3>
<div class="grid"><label class="field"><span>X</span><input id="anchoredX" type="number" step="0.1"></label><label class="field"><span>Y</span><input id="anchoredY" type="number" step="0.1"></label></div>
<div id="rectOnly">
<h3>Size Delta</h3>
<div class="grid"><label class="field"><span>Width</span><input id="width" type="number" step="0.1"></label><label class="field"><span>Height</span><input id="height" type="number" step="0.1"></label></div>
<h3>Anchors</h3>
<div class="grid"><label class="field"><span>Min X</span><input id="anchorMinX" type="number" step="0.01"></label><label class="field"><span>Min Y</span><input id="anchorMinY" type="number" step="0.01"></label><label class="field"><span>Max X</span><input id="anchorMaxX" type="number" step="0.01"></label><label class="field"><span>Max Y</span><input id="anchorMaxY" type="number" step="0.01"></label></div>
<h3>Pivot</h3>
<div class="grid"><label class="field"><span>X</span><input id="pivotX" type="number" step="0.01"></label><label class="field"><span>Y</span><input id="pivotY" type="number" step="0.01"></label></div>
</div>
<h3>Local Scale</h3>
<div class="grid three"><label class="field"><span>X</span><input id="scaleX" type="number" step="0.01"></label><label class="field"><span>Y</span><input id="scaleY" type="number" step="0.01"></label><label class="field"><span>Z</span><input id="scaleZ" type="number" step="0.01"></label></div>
<div class="actions"><button id="apply" class="primary">Apply</button><button id="hide" class="danger">Hide</button><span id="status" class="status"></span></div>
</div>
</aside>
</main>
<script>
let selected=null, rows=[];
const $=id=>document.getElementById(id);
const fields=["anchoredX","anchoredY","width","height","anchorMinX","anchorMinY","anchorMaxX","anchorMaxY","pivotX","pivotY","scaleX","scaleY","scaleZ"];
async function api(path, opts){const r=await fetch(path, opts); const j=await r.json(); if(!j.ok) throw new Error(j.error||"request failed"); return j.data;}
function setStatus(s){$("status").textContent=s||""}
function fill(e){selected=e;$("empty").hidden=true;$("editor").hidden=false;$("title").textContent=`${e.name} (${e.id})`;$("path").textContent=e.path;$("active").checked=e.activeSelf;$("posTitle").textContent=e.kind==="RectTransform"?"Anchored Position":"Local Position";$("rectOnly").hidden=e.kind!=="RectTransform";for(const f of fields)$(`${f}`).value=e[f]??"";document.querySelectorAll("tr").forEach(tr=>tr.classList.toggle("selected",Number(tr.dataset.id)===e.id));}
function render(){const tbody=$("rows");tbody.innerHTML="";for(const e of rows){const tr=document.createElement("tr");tr.dataset.id=e.id;tr.innerHTML=`<td>${e.id}</td><td>${esc(e.kind||"")}</td><td title="${esc(e.name)}">${esc(e.name)}</td><td title="${esc(e.path)}">${esc(e.path)}</td><td>${e.activeInHierarchy?"yes":"no"}</td>`;tr.onclick=()=>fill(e);tbody.appendChild(tr);}}
function esc(s){return String(s).replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));}
async function waitJob(job, label){let current=job;while(current.state==="pending"||current.state==="running"){setStatus(`${label} ${current.state} (${current.elapsedMs}ms)`);await new Promise(r=>setTimeout(r,250));current=await api(`/api/job?id=${current.jobId}`);}if(current.state!=="complete")throw new Error(current.error||`${label} failed`);return current;}
async function refresh(){setStatus("queueing scan");const q=new URLSearchParams({filter:$("filter").value||"",includeInactive:$("inactive").checked?"1":"0",includeTransforms:$("transforms").checked?"1":"0",maxResults:$("limit").value||"1000"});const job=await api(`/api/scan?${q}`);const done=await waitJob(job,"scan");rows=done.result?.elements||[];render();setStatus(`${rows.length} elements`);if(selected){const next=rows.find(x=>x.id===selected.id);if(next)fill(next);}}
async function apply(activeOverride){if(!selected)return;const body={id:selected.id};body.active=activeOverride===undefined?$("active").checked:activeOverride;for(const f of fields){const v=$(`${f}`).value;if(v!=="")body[f]=Number(v);}setStatus("queueing edit");const job=await api("/api/edit",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});const done=await waitJob(job,"edit");if(done.element)selected=done.element;setStatus("applied");await refresh();}
$("refresh").onclick=()=>refresh().catch(e=>setStatus(e.message));$("apply").onclick=()=>apply().catch(e=>setStatus(e.message));$("hide").onclick=()=>apply(false).catch(e=>setStatus(e.message));$("filter").onkeydown=e=>{if(e.key==="Enter")refresh().catch(err=>setStatus(err.message));};setStatus("ready");
</script>
</body>
</html>
""";
}
