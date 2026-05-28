using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LimbusCanvasFix
{
    internal static class DisableGuard
    {
        public static void StubGuardAssembly(Harmony harmony)
        {
            var stub       = new HarmonyMethod(typeof(DisableGuard), nameof(Stub));
            var stringStub = new HarmonyMethod(typeof(DisableGuard), nameof(StringStub));
            var boolStub   = new HarmonyMethod(typeof(DisableGuard), nameof(BoolStub));

            int patched = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.Contains("JsonExtensions")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types!; }

                foreach (var type in types)
                {
                    if (type?.FullName == null) continue;

                    var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                  BindingFlags.Public   | BindingFlags.NonPublic);
                    foreach (var method in methods)
                    {
                        if (!AccessTools.IsDeclaredMember<MethodInfo>(method)
                            || method.IsSpecialName
                            || method.Name.Contains("Invoke"))
                            continue;

                        try
                        {
                            var prefix =
                                method.ReturnType == typeof(string) ? stringStub :
                                method.ReturnType == typeof(bool)   ? boolStub   :
                                                                       stub;
                            harmony.Patch(method, prefix);
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogDebug($"[FAIL] {type.FullName}.{method.Name}: {ex.Message}");
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"Patched {patched} methods to disable guard.");
        }

        private static bool Stub() => false;

        private static bool StringStub(ref string __result)
        {
            __result = "";
            return false;
        }

        private static bool BoolStub(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(Environment), nameof(Environment.Exit), typeof(int))]
        [HarmonyPrefix]
        private static bool PreventEnvironmentExit(int exitCode)
        {
            Plugin.Log.LogInfo($"[DISABLED] Environment.Exit({exitCode})");
            return false;
        }

        [HarmonyPatch(typeof(Application), nameof(Application.Quit), new Type[] { })]
        [HarmonyPrefix]
        private static bool PreventApplicationQuit()
        {
            Plugin.Log.LogInfo("[DISABLED] Application.Quit()");
            return false;
        }

        [HarmonyPatch(typeof(Application), nameof(Application.Quit), typeof(int))]
        [HarmonyPrefix]
        private static bool PreventApplicationQuit_Int(int exitCode)
        {
            Plugin.Log.LogInfo($"[DISABLED] Application.Quit({exitCode})");
            return false;
        }

        [HarmonyPatch(typeof(Environment), nameof(Environment.FailFast), typeof(string), typeof(Exception))]
        [HarmonyPrefix]
        private static bool PreventFailFast_1(string message, Exception exception)
        {
            Plugin.Log.LogInfo($"[DISABLED] FailFast: {message}");
            return false;
        }

        [HarmonyPatch(typeof(Environment), nameof(Environment.FailFast), typeof(string))]
        [HarmonyPrefix]
        private static bool PreventFailFast_2(string message)
        {
            Plugin.Log.LogInfo($"[DISABLED] FailFast: {message}");
            return false;
        }
    }
}
