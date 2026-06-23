using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LimbusRuntimeUIInspector;

internal static class InspectorServer
{
    private static readonly object sync = new();
    private static TcpListener? listener;
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
            var thread = new Thread(AcceptLoop)
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
            try { listener?.Stop(); } catch { /* Stop is best-effort when the listener is already disposed. */ }
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
            catch (SocketException ex)
            {
                if (!HandleAcceptSocketException(ex))
                    return;
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

    private static bool HandleAcceptSocketException(SocketException ex)
    {
        if (!running)
            return false;

        Plugin.Log.LogWarning($"Inspector server socket stopped unexpectedly: {ex.SocketErrorCode}.");
        return true;
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
                HandleRequest(stream, request, requestId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Inspector request failed: {ex}");
                WriteResponse(stream, 500, ContentTypes.Json, JsonSerializer.Serialize(new ApiResponse<object>(false, null, ex.Message), JsonOptions.Default));
            }
        }
    }

    private static void HandleRequest(Stream stream, HttpRequest request, int requestId)
    {
        if (TryHandlePage(stream, request, requestId))
            return;
        if (TryHandleScan(stream, request, requestId))
            return;
        if (TryHandleJob(stream, request, requestId))
            return;
        if (TryHandleEdit(stream, request, requestId))
            return;
        if (TryHandleStatus(stream, request, requestId))
            return;

        WriteResponse(stream, 404, ContentTypes.Json, "{\"ok\":false,\"error\":\"Not found.\"}");
    }

    private static bool TryHandlePage(Stream stream, HttpRequest request, int requestId)
    {
        if (request.Method != "GET" || request.Path != "/")
            return false;

        WriteResponse(stream, 200, "text/html; charset=utf-8", InspectorPage.Html);
        Plugin.Debug($"HTTP {requestId} {request.Path} served page.");
        return true;
    }

    private static bool TryHandleScan(Stream stream, HttpRequest request, int requestId)
    {
        if (request.Method != "GET" || request.Path != "/api/scan")
            return false;

        var filter = request.Query.TryGetValue("filter", out var value) ? value : string.Empty;
        var includeInactive = request.Query.TryGetValue("includeInactive", out var inactive) && inactive == "1";
        var includeTransforms = !request.Query.TryGetValue("includeTransforms", out var transforms) || transforms == "1";
        var maxResults = GetRequestedMaxResults(request);
        var job = InspectorJobs.QueueScan(filter, includeInactive, includeTransforms, maxResults);
        WriteJson(stream, 200, new ApiResponse<JobSnapshot>(true, job, null));
        Plugin.Debug($"HTTP {requestId} queued scan job {job.JobId}.");
        return true;
    }

    private static bool TryHandleJob(Stream stream, HttpRequest request, int requestId)
    {
        if (request.Method != "GET" || request.Path != "/api/job")
            return false;

        if (!TryGetJobId(request, out var jobId))
        {
            WriteResponse(stream, 400, ContentTypes.Json, "{\"ok\":false,\"error\":\"Missing job id.\"}");
            return true;
        }

        var snapshot = InspectorJobs.Get(jobId);
        if (snapshot == null)
        {
            WriteResponse(stream, 404, ContentTypes.Json, "{\"ok\":false,\"error\":\"Job not found.\"}");
            return true;
        }

        WriteJson(stream, 200, new ApiResponse<JobSnapshot>(true, snapshot, null));
        Plugin.Debug($"HTTP {requestId} returned job {jobId} state={snapshot.State}.");
        return true;
    }

    private static bool TryHandleEdit(Stream stream, HttpRequest request, int requestId)
    {
        if (request.Method != "POST" || request.Path != "/api/edit")
            return false;

        var edit = JsonSerializer.Deserialize<EditRequest>(request.Body, JsonOptions.Default);
        if (edit == null || edit.Id == 0)
        {
            WriteResponse(stream, 400, ContentTypes.Json, "{\"ok\":false,\"error\":\"Missing element id.\"}");
            return true;
        }

        var job = InspectorJobs.QueueEdit(edit);
        WriteJson(stream, 200, new ApiResponse<JobSnapshot>(true, job, null));
        Plugin.Debug($"HTTP {requestId} queued edit job {job.JobId} for id={edit.Id}.");
        return true;
    }

    private static bool TryHandleStatus(Stream stream, HttpRequest request, int requestId)
    {
        if (request.Method != "GET" || request.Path != "/api/status")
            return false;

        WriteResponse(stream, 200, ContentTypes.Json, "{\"ok\":true,\"name\":\"LimbusRuntimeUIInspector\"}");
        Plugin.Debug($"HTTP {requestId} status ok.");
        return true;
    }

    private static int GetRequestedMaxResults(HttpRequest request)
    {
        var maxResults = Math.Max(1, Plugin.MaxResults.Value);
        if (request.Query.TryGetValue("maxResults", out var rawMaxResults) &&
            int.TryParse(rawMaxResults, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedMaxResults))
        {
            return Math.Clamp(requestedMaxResults, 1, 20000);
        }

        return maxResults;
    }

    private static bool TryGetJobId(HttpRequest request, out int jobId)
    {
        jobId = 0;
        return request.Query.TryGetValue("id", out var rawId) &&
               int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobId);
    }

    private static void WriteJson<T>(Stream stream, int status, T payload)
    {
        WriteResponse(stream, status, ContentTypes.Json, JsonSerializer.Serialize(payload, JsonOptions.Default));
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


internal sealed class HttpRequest
{
    public string Method { get; private set; } = "";
    public string Path { get; private set; } = "";
    public Dictionary<string, string> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; private set; } = "";

    public static HttpRequest? Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
        var request = ReadRequestLine(reader);
        if (request == null)
            return null;

        var contentLength = ReadContentLength(reader);
        request.Body = ReadBody(reader, contentLength);
        return request;
    }

    private static HttpRequest? ReadRequestLine(StreamReader reader)
    {
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

        ParseTarget(parts[1], request);
        return request;
    }

    private static void ParseTarget(string target, HttpRequest request)
    {
        var queryIndex = target.IndexOf('?');
        request.Path = Uri.UnescapeDataString(queryIndex >= 0 ? target[..queryIndex] : target);
        if (queryIndex >= 0)
            ParseQuery(target[(queryIndex + 1)..], request.Query);
    }

    private static int ReadContentLength(StreamReader reader)
    {
        var contentLength = 0;
        while (true)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line))
                break;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
        }

        return contentLength;
    }

    private static string ReadBody(StreamReader reader, int contentLength)
    {
        if (contentLength <= 0)
            return "";

        var buffer = new char[Math.Min(contentLength, 1024 * 1024)];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = reader.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
                break;
            offset += read;
        }

        return new string(buffer, 0, offset);
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

