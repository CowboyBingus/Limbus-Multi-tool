using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace LimbusCanvasFix
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID    = "com.you.limbuscanvasfix";
        public const string NAME    = "LimbusCanvasFix";
        public const string VERSION = "1.0.0";

        internal static new ManualLogSource Log = null!;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo($"{NAME} {VERSION} loading...");

            ApplyPatches();

            Log.LogInfo($"{NAME} ready.");
        }

        private static void ApplyPatches()
        {
            try
            {
                LogKnownIl2CppImages();
                var harmony = new Harmony(GUID);
                harmony.PatchAll(typeof(Plugin).Assembly);
                DisableGuard.StubGuardAssembly(harmony);
                Log.LogInfo("Harmony patches applied.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Patch failed: {ex}");
            }
        }

        private static void LogKnownIl2CppImages()
        {
            try
            {
                var field = typeof(IL2CPP).GetField("ourImagesMap", BindingFlags.Static | BindingFlags.NonPublic);
                if (field?.GetValue(null) is not Dictionary<string, IntPtr> images)
                    return;

                Log.LogInfo($"IL2CPP image count: {images.Count}");
                foreach (var key in images.Keys)
                {
                    if (key.Contains("UI", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("UnityEngine", StringComparison.OrdinalIgnoreCase))
                        Log.LogInfo($"IL2CPP image: {key}");
                }
            }
            catch (Exception ex)
            {
                Log.LogDebug($"Could not inspect IL2CPP images: {ex.Message}");
            }
        }

        internal static void Apply(CanvasScaler scaler)
        {
            NativeCanvasScaler.Apply(scaler);
        }
    }

    [HarmonyPatch(typeof(CanvasScaler), "OnEnable")]
    internal static class CanvasScaler_OnEnable_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CanvasScaler __instance) => Plugin.Apply(__instance);
    }

    internal static class NativeCanvasScaler
    {
        private const int ScaleWithScreenSize = 1;

        private static bool initialized;
        private static nint uiScaleModeField;
        private static nint referenceResolutionField;
        private static nint matchWidthOrHeightField;
        private static int appliedCount;

        public static unsafe void Apply(CanvasScaler scaler)
        {
            if (scaler == null) return;
            var obj = IL2CPP.Il2CppObjectBaseToPtr(scaler);
            if (obj == IntPtr.Zero || !EnsureInitialized()) return;

            var mode = 0;
            IL2CPP.il2cpp_field_get_value(obj, uiScaleModeField, &mode);
            if (mode != ScaleWithScreenSize) return;

            var resolution = stackalloc float[2];
            resolution[0] = 3000f;
            resolution[1] = 1440f;
            IL2CPP.il2cpp_field_set_value(obj, referenceResolutionField, resolution);

            var match = 1f;
            IL2CPP.il2cpp_field_set_value(obj, matchWidthOrHeightField, &match);

            appliedCount++;
            if (appliedCount <= 5)
                Plugin.Log.LogInfo($"Applied CanvasScaler ultrawide fix ({appliedCount}).");
        }

        private static bool EnsureInitialized()
        {
            if (initialized) return true;

            var klass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
            if (klass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Could not resolve UnityEngine.UI.CanvasScaler IL2CPP class.");
                return false;
            }

            uiScaleModeField = IL2CPP.GetIl2CppField(klass, "m_UiScaleMode");
            referenceResolutionField = IL2CPP.GetIl2CppField(klass, "m_ReferenceResolution");
            matchWidthOrHeightField = IL2CPP.GetIl2CppField(klass, "m_MatchWidthOrHeight");

            initialized = uiScaleModeField != IntPtr.Zero
                && referenceResolutionField != IntPtr.Zero
                && matchWidthOrHeightField != IntPtr.Zero;

            if (!initialized)
                Plugin.Log.LogWarning("Could not resolve one or more CanvasScaler IL2CPP fields.");
            else
                Plugin.Log.LogInfo("Resolved CanvasScaler IL2CPP fields.");

            return initialized;
        }
    }
}
