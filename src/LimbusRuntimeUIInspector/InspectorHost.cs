using BepInEx.Configuration;
using BepInEx.Logging;
using System;

namespace LimbusRuntimeUIInspector;

internal static class InspectorHost
{
    private const string PluginName = "LimbusRuntimeUIInspector";

    private static ManualLogSource? log;
    private static ConfigEntry<int>? maxResults;
    private static ConfigEntry<bool>? debugLogging;

    public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{PluginName} logging is not initialized.");

    public static int MaxResults => Math.Max(1, maxResults?.Value ?? 1);

    public static void Initialize(ManualLogSource source, ConfigEntry<int> maxResultsEntry, ConfigEntry<bool> debugLoggingEntry)
    {
        log = source;
        maxResults = maxResultsEntry;
        debugLogging = debugLoggingEntry;
    }

    public static void Debug(string message)
    {
        if (debugLogging?.Value ?? false)
            Log.LogInfo($"[debug] {message}");
    }
}
