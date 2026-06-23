using BepInEx.Configuration;
using System;

namespace LimbusShared.Configuration;

internal static class PluginConfig
{
    public static ConfigEntry<T> Required<T>(ConfigEntry<T>? entry, string pluginName, string name)
    {
        return entry ?? throw new InvalidOperationException($"{pluginName} config entry '{name}' is not initialized.");
    }

    public static bool IsSet(ConfigEntry<bool>? entry)
    {
        return entry?.Value ?? false;
    }
}
