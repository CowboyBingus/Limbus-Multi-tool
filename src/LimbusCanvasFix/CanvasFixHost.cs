using BepInEx.Logging;
using System;

namespace LimbusCanvasFix
{
    internal static class CanvasFixHost
    {
        private const string PluginName = "LimbusCanvasFix";

        private static ManualLogSource? log;

        public static ManualLogSource Log => log ?? throw new InvalidOperationException($"{PluginName} logging is not initialized.");

        public static void Initialize(ManualLogSource source)
        {
            log = source;
        }
    }
}
