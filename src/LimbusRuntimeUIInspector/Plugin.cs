using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using LimbusShared;
using System;

namespace LimbusRuntimeUIInspector;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class Plugin : BasePlugin
{
    public const string GUID = "com.you.limbusruntimeuiinspector";
    public const string NAME = "LimbusRuntimeUIInspector";
    public const string VERSION = "0.3.0";

    private static ManualLogSource? log;
    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<int>? port;
    private static ConfigEntry<int>? maxResults;
    private static ConfigEntry<int>? requestTimeoutMilliseconds;
    private static ConfigEntry<bool>? debugLogging;

    internal static new ManualLogSource Log => log ?? throw new InvalidOperationException($"{NAME} logging is not initialized.");
    internal static ConfigEntry<bool> Enabled => Required(enabled, nameof(Enabled));
    internal static ConfigEntry<int> Port => Required(port, nameof(Port));
    internal static ConfigEntry<int> MaxResults => Required(maxResults, nameof(MaxResults));
    internal static ConfigEntry<int> RequestTimeoutMilliseconds => Required(requestTimeoutMilliseconds, nameof(RequestTimeoutMilliseconds));
    internal static ConfigEntry<bool> DebugLogging => Required(debugLogging, nameof(DebugLogging));

    public override void Load()
    {
        InitializeLog(base.Log);
        BindConfig(Config);

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
        if (IsSet(debugLogging))
            Log.LogInfo($"[debug] {message}");
    }

    private static void InitializeLog(ManualLogSource source)
    {
        log = source;
    }

    private static void BindConfig(ConfigFile config)
    {
        enabled = config.Bind("General", "Enabled", true, "Starts the local runtime UI inspector server.");
        port = config.Bind("Server", "Port", 43129, "Localhost HTTP port for the inspector browser UI.");
        maxResults = config.Bind("Inspector", "MaxResults", 5000, "Maximum live element rows returned per scan.");
        requestTimeoutMilliseconds = config.Bind("Inspector", "RequestTimeoutMilliseconds", 2000, "Reserved for future async inspector operations.");
        debugLogging = config.Bind("Diagnostics", "DebugLogging", true, "Writes detailed inspector server and scan/edit job lifecycle logs.");
    }

    private static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string name) => PluginConfig.Required(entry, NAME, name);

    private static bool IsSet(ConfigEntry<bool>? entry) => PluginConfig.IsSet(entry);
}
