using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace LimbusCanvasFix
{
    internal static class DisableGuard
    {
        private static readonly bool SkipOriginal = false;

        public static void StubGuardAssembly(Harmony harmony)
        {
            var prefixes = CreatePrefixMethods();
            var patched = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                patched += PatchGuardAssembly(harmony, asm, prefixes);
            }

            Plugin.Log.LogInfo($"Patched {patched} methods to disable guard.");
        }

        private static (HarmonyMethod Default, HarmonyMethod String, HarmonyMethod Bool) CreatePrefixMethods()
        {
            return (
                new HarmonyMethod(typeof(DisableGuard), nameof(Stub)),
                new HarmonyMethod(typeof(DisableGuard), nameof(StringStub)),
                new HarmonyMethod(typeof(DisableGuard), nameof(BoolStub)));
        }

        private static int PatchGuardAssembly(
            Harmony harmony,
            Assembly assembly,
            (HarmonyMethod Default, HarmonyMethod String, HarmonyMethod Bool) prefixes)
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName == null || !assemblyName.Contains("JsonExtensions"))
                return 0;

            var patched = 0;
            foreach (var type in GetLoadableTypes(assembly))
            {
                patched += PatchTypeMethods(harmony, type, prefixes);
            }

            return patched;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
        }

        private static int PatchTypeMethods(
            Harmony harmony,
            Type type,
            (HarmonyMethod Default, HarmonyMethod String, HarmonyMethod Bool) prefixes)
        {
            if (type.FullName == null)
                return 0;

            var patched = 0;
            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if (!ShouldPatch(method))
                    continue;

                try
                {
                    harmony.Patch(method, ChoosePrefix(method, prefixes));
                    patched++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogDebug($"[FAIL] {type.FullName}.{method.Name}: {ex.Message}");
                }
            }

            return patched;
        }

        private static bool ShouldPatch(MethodInfo method)
        {
            return !method.IsSpecialName && !method.Name.Contains("Invoke");
        }

        private static HarmonyMethod ChoosePrefix(
            MethodInfo method,
            (HarmonyMethod Default, HarmonyMethod String, HarmonyMethod Bool) prefixes)
        {
            if (method.ReturnType == typeof(string))
                return prefixes.String;
            if (method.ReturnType == typeof(bool))
                return prefixes.Bool;

            return prefixes.Default;
        }

        private static bool Stub(MethodBase __originalMethod)
        {
            _ = __originalMethod;
            return SkipOriginal;
        }

        private static bool StringStub(ref string __result)
        {
            __result = "";
            return SkipOriginal;
        }

        private static bool BoolStub(ref bool __result)
        {
            __result = true;
            return SkipOriginal;
        }

    }
}
