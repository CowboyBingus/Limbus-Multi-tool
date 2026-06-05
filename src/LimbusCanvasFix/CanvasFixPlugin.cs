using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LimbusCanvasFix
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID    = "com.you.limbuscanvasfix";
        public const string NAME    = "LimbusCanvasFix";
        public const string VERSION = "1.1.0";

        internal static new ManualLogSource Log = null!;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo($"{NAME} {VERSION} loading...");

            ApplyPatches();

            Log.LogInfo($"{NAME} ready.");
        }

        public override bool Unload()
        {
            CanvasScalerOnEnableDetour.Uninstall();
            RectTransformWriteDetours.Uninstall();
            RectTransformLayoutDetour.Uninstall();
            LayoutRuleMaintainer.Reset();
            return true;
        }

        private static void ApplyPatches()
        {
            try
            {
                LogKnownIl2CppImages();
                CanvasScalerOnEnableDetour.Install();
                RectTransformWriteDetours.Install();
                RectTransformLayoutDetour.Install();
                try
                {
                    DisableGuard.StubGuardAssembly(new Harmony(GUID));
                }
                catch (Exception ex)
                {
                    Log.LogDebug($"Guard stub pass skipped: {ex.Message}");
                }
                Log.LogInfo("Runtime patches applied.");
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

        internal static void Apply(IntPtr scaler) => NativeCanvasScaler.Apply(scaler);
    }

    internal static class CanvasScalerOnEnableDetour
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnEnableDelegate(IntPtr self, IntPtr methodInfo);

        private static NativeDetour? detour;
        private static OnEnableDelegate? original;
        private static readonly OnEnableDelegate replacement = OnEnableReplacement;

        public static void Install()
        {
            if (detour != null)
                return;

            var klass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
            if (klass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("CanvasScaler.OnEnable detour skipped: class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("CanvasScaler.OnEnable detour skipped: method was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("CanvasScaler.OnEnable detour skipped: method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<OnEnableDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"CanvasScaler.OnEnable detour installed at {Ptr(methodPointer)}.");
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
                // Unload can race Unity teardown.
            }
            finally
            {
                detour = null;
                original = null;
            }
        }

        private static void OnEnableReplacement(IntPtr self, IntPtr methodInfo)
        {
            original?.Invoke(self, methodInfo);
            Plugin.Apply(self);
            LayoutRuleMaintainer.ObserveCanvasScaler(self);
        }

        private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
    }

    internal static class RectTransformLayoutDetour
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SendReapplyDrivenPropertiesDelegate(IntPtr rectTransform, IntPtr methodInfo);

        private static NativeDetour? detour;
        private static SendReapplyDrivenPropertiesDelegate? original;
        private static readonly SendReapplyDrivenPropertiesDelegate replacement = Replacement;
        [ThreadStatic] private static bool inReplacement;

        public static void Install()
        {
            if (detour != null)
                return;

            var klass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");
            if (klass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Layout maintainer skipped: UnityEngine.RectTransform class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SendReapplyDrivenProperties", 1);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Layout maintainer skipped: RectTransform.SendReapplyDrivenProperties was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Layout maintainer skipped: RectTransform.SendReapplyDrivenProperties pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SendReapplyDrivenPropertiesDelegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"Layout maintainer installed at {Ptr(methodPointer)}.");
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
                // Unload can race Unity teardown.
            }
            finally
            {
                detour = null;
                original = null;
            }
        }

        private static void Replacement(IntPtr rectTransform, IntPtr methodInfo)
        {
            if (inReplacement)
            {
                original?.Invoke(rectTransform, methodInfo);
                return;
            }

            inReplacement = true;
            try
            {
                original?.Invoke(rectTransform, methodInfo);
                LayoutRuleMaintainer.ApplyIfTarget(rectTransform, LayoutWriteKind.Any);
            }
            catch (Exception ex)
            {
                LayoutRuleMaintainer.ReportHookFailure("layout hook", ex);
            }
            finally
            {
                inReplacement = false;
            }
        }

        private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
    }

    internal static class RectTransformWriteDetours
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetVector2Delegate(IntPtr self, Vector2Value value, IntPtr methodInfo);

        private static NativeDetour? anchoredPositionDetour;
        private static NativeDetour? sizeDeltaDetour;
        private static SetVector2Delegate? anchoredPositionOriginal;
        private static SetVector2Delegate? sizeDeltaOriginal;
        private static readonly SetVector2Delegate anchoredPositionReplacement = AnchoredPositionReplacement;
        private static readonly SetVector2Delegate sizeDeltaReplacement = SizeDeltaReplacement;

        public static void Install()
        {
            if (anchoredPositionDetour != null || sizeDeltaDetour != null)
                return;

            var klass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");
            if (klass == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Layout write maintainer skipped: UnityEngine.RectTransform class was not resolved.");
                return;
            }

            InstallOne(
                klass,
                "set_anchoredPosition",
                anchoredPositionReplacement,
                ref anchoredPositionDetour,
                ref anchoredPositionOriginal,
                "RectTransform.set_anchoredPosition");
            InstallOne(
                klass,
                "set_sizeDelta",
                sizeDeltaReplacement,
                ref sizeDeltaDetour,
                ref sizeDeltaOriginal,
                "RectTransform.set_sizeDelta");
        }

        public static void Uninstall()
        {
            Free(ref anchoredPositionDetour, ref anchoredPositionOriginal);
            Free(ref sizeDeltaDetour, ref sizeDeltaOriginal);
        }

        private static void InstallOne(
            IntPtr klass,
            string methodName,
            SetVector2Delegate replacement,
            ref NativeDetour? detour,
            ref SetVector2Delegate? original,
            string label)
        {
            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, methodName, 1);
            if (method == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"Layout write maintainer skipped: {label} was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"Layout write maintainer skipped: {label} pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SetVector2Delegate>();
            detour.Apply();
            Plugin.Log.LogInfo($"Layout write maintainer installed {label} at {Ptr(methodPointer)}.");
        }

        private static void Free(ref NativeDetour? detour, ref SetVector2Delegate? original)
        {
            try
            {
                detour?.Undo();
                detour?.Free();
            }
            catch
            {
                // Unload can race Unity teardown.
            }
            finally
            {
                detour = null;
                original = null;
            }
        }

        private static void AnchoredPositionReplacement(IntPtr self, Vector2Value value, IntPtr methodInfo)
        {
            anchoredPositionOriginal?.Invoke(self, value, methodInfo);
            try
            {
                if (!LayoutRuleMaintainer.IsApplying)
                    LayoutRuleMaintainer.ApplyIfTarget(self, LayoutWriteKind.AnchoredPosition);
            }
            catch (Exception ex)
            {
                LayoutRuleMaintainer.ReportHookFailure("anchoredPosition write hook", ex);
            }
        }

        private static void SizeDeltaReplacement(IntPtr self, Vector2Value value, IntPtr methodInfo)
        {
            sizeDeltaOriginal?.Invoke(self, value, methodInfo);
            try
            {
                if (!LayoutRuleMaintainer.IsApplying)
                    LayoutRuleMaintainer.ApplyIfTarget(self, LayoutWriteKind.SizeDelta);
            }
            catch (Exception ex)
            {
                LayoutRuleMaintainer.ReportHookFailure("sizeDelta write hook", ex);
            }
        }

        private static string Ptr(IntPtr ptr) => ptr == IntPtr.Zero ? "0x0" : "0x" + ptr.ToString("X");
    }

    internal static class NativeCanvasScaler
    {
        private const int ScaleWithScreenSize = 1;

        private static bool initialized;
        private static nint uiScaleModeField;
        private static nint referenceResolutionField;
        private static nint matchWidthOrHeightField;
        private static int appliedCount;

        public static unsafe void Apply(IntPtr obj)
        {
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

    internal static unsafe class LayoutRuleMaintainer
    {
        private const int MaxPathDepth = 80;
        private const int MaxScanNodes = 6000;
        private const int MaxNonTargetNameCache = 32768;
        private const float Epsilon = 0.01f;

        private static readonly LayoutRule[] rules =
        {
            new("/[Script]LoginSceneManager/[Canvas]/[Image]TouchToStart", width: -3000f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Rect]UnitStatusContent", anchoredXBySignMagnitude: 900f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Script]TabContentManager", anchoredXBySignMagnitude: 1000f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Script]SideButtonList", anchoredX: 335f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Image]UnitStatusPanel", width: 3625f),
        };

        private static readonly Dictionary<string, LayoutRule> rulesByPath = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<LayoutRule>> rulesByLeafName = new(StringComparer.Ordinal);
        private static readonly Dictionary<IntPtr, LayoutRule> targetCache = new();
        private static readonly HashSet<IntPtr> nonTargetNameCache = new();
        private static readonly HashSet<IntPtr> scannedRoots = new();
        private static bool initialized;
        private static int appliedCount;
        private static int failureCount;
        private static int scanCount;
        [ThreadStatic] private static bool isApplying;

        private static IntPtr objectClass;
        private static IntPtr componentClass;
        private static IntPtr transformClass;
        private static IntPtr rectTransformClass;
        private static IntPtr objectGetName;
        private static IntPtr componentGetTransform;
        private static IntPtr transformGetParent;
        private static IntPtr transformGetChildCount;
        private static IntPtr transformGetChild;
        private static IntPtr rectGetAnchoredPosition;
        private static IntPtr rectSetAnchoredPosition;
        private static IntPtr rectGetSizeDelta;
        private static IntPtr rectSetSizeDelta;

        static LayoutRuleMaintainer()
        {
            foreach (var rule in rules)
            {
                rulesByPath[rule.Path] = rule;
                if (!rulesByLeafName.TryGetValue(rule.LeafName, out var bucket))
                {
                    bucket = new List<LayoutRule>();
                    rulesByLeafName[rule.LeafName] = bucket;
                }

                bucket.Add(rule);
            }
        }

        public static void Reset()
        {
            targetCache.Clear();
            nonTargetNameCache.Clear();
            scannedRoots.Clear();
            appliedCount = 0;
            failureCount = 0;
            scanCount = 0;
        }

        public static bool IsApplying => isApplying;

        public static void ObserveCanvasScaler(IntPtr canvasScaler)
        {
            if (canvasScaler == IntPtr.Zero)
                return;

            try
            {
                EnsureInitialized();
                var transform = InvokeObject(componentGetTransform, canvasScaler);
                if (transform == IntPtr.Zero)
                    return;

                if (!scannedRoots.Add(transform))
                    return;

                var visited = 0;
                ScanSubtree(transform, ref visited);
                var count = ++scanCount;
                if (count <= 12 || count % 100 == 0)
                    Plugin.Log.LogInfo($"Layout maintainer scanned CanvasScaler root #{count}: visited={visited}, cachedTargets={targetCache.Count}.");
            }
            catch (Exception ex)
            {
                ReportFailure("CanvasScaler root scan", ex);
            }
        }

        public static void ApplyIfTarget(IntPtr rectTransform, LayoutWriteKind kind)
        {
            if (rectTransform == IntPtr.Zero)
                return;

            try
            {
                EnsureInitialized();
                if (isApplying)
                    return;

                if (!TryGetRule(rectTransform, out var rule))
                    return;

                isApplying = true;
                try
                {
                    ApplyRule(rule, rectTransform, kind);
                }
                finally
                {
                    isApplying = false;
                }
            }
            catch (Exception ex)
            {
                ReportFailure("target check", ex);
            }
        }

        public static void ReportHookFailure(string phase, Exception ex) => ReportFailure(phase, ex);

        private static bool TryGetRule(IntPtr rectTransform, out LayoutRule rule)
        {
            if (targetCache.TryGetValue(rectTransform, out rule!))
                return true;

            if (nonTargetNameCache.Contains(rectTransform))
            {
                rule = null!;
                return false;
            }

            try
            {
                var name = InvokeString(objectGetName, rectTransform);
                if (!rulesByLeafName.TryGetValue(name, out var candidates))
                {
                    if (nonTargetNameCache.Count >= MaxNonTargetNameCache)
                        nonTargetNameCache.Clear();
                    nonTargetNameCache.Add(rectTransform);
                    rule = null!;
                    return false;
                }

                var path = BuildPath(rectTransform);
                foreach (var candidate in candidates)
                {
                    if (rulesByPath.TryGetValue(path, out var matched) && ReferenceEquals(matched, candidate))
                    {
                        targetCache[rectTransform] = matched;
                        rule = matched;
                        return true;
                    }
                }

                rule = null!;
                return false;
            }
            catch (Exception ex)
            {
                ReportFailure("rule lookup", ex);
                rule = null!;
                return false;
            }
        }

        private static void ScanSubtree(IntPtr transform, ref int visited)
        {
            if (transform == IntPtr.Zero || visited >= MaxScanNodes)
                return;

            visited++;
            if (IsRectTransformObject(transform) && TryGetRule(transform, out var rule))
            {
                isApplying = true;
                try
                {
                    ApplyRule(rule, transform, LayoutWriteKind.Any);
                }
                finally
                {
                    isApplying = false;
                }
            }

            var childCount = InvokeInt(transformGetChildCount, transform);
            for (var i = 0; i < childCount && visited < MaxScanNodes; i++)
            {
                var child = InvokeObjectIntArg(transformGetChild, transform, i);
                ScanSubtree(child, ref visited);
            }
        }

        private static bool IsRectTransformObject(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
                return false;

            try
            {
                var klass = IL2CPP.il2cpp_object_get_class(obj);
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

        private static void ApplyRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind)
        {
            if (rect == IntPtr.Zero)
                return;

            var changed = false;
            var details = "";
            if ((kind == LayoutWriteKind.Any || kind == LayoutWriteKind.SizeDelta) && rule.Width.HasValue)
            {
                var size = InvokeVector2(rectGetSizeDelta, rect);
                var targetWidth = rule.Width.Value;
                if (Math.Abs(size.X - targetWidth) > Epsilon)
                {
                    var previous = size.X;
                    size.X = targetWidth;
                    InvokeSetVector2(rectSetSizeDelta, rect, size);
                    details = $"sizeDelta.x {previous:0.###}->{targetWidth:0.###}";
                    changed = true;
                }
            }

            if ((kind == LayoutWriteKind.Any || kind == LayoutWriteKind.AnchoredPosition) && rule.AnchoredXBySignMagnitude.HasValue)
            {
                var position = InvokeVector2(rectGetAnchoredPosition, rect);
                if (Math.Abs(position.X) > Epsilon)
                {
                    var targetX = position.X < 0
                        ? -rule.AnchoredXBySignMagnitude.Value
                        : rule.AnchoredXBySignMagnitude.Value;
                    if (Math.Abs(position.X - targetX) > Epsilon)
                    {
                        var previous = position.X;
                        position.X = targetX;
                        InvokeSetVector2(rectSetAnchoredPosition, rect, position);
                        details = $"anchoredPosition.x {previous:0.###}->{targetX:0.###}";
                        changed = true;
                    }
                }
            }

            if ((kind == LayoutWriteKind.Any || kind == LayoutWriteKind.AnchoredPosition) && rule.AnchoredX.HasValue)
            {
                var position = InvokeVector2(rectGetAnchoredPosition, rect);
                var targetX = rule.AnchoredX.Value;
                if (Math.Abs(position.X - targetX) > Epsilon)
                {
                    var previous = position.X;
                    position.X = targetX;
                    InvokeSetVector2(rectSetAnchoredPosition, rect, position);
                    details = $"anchoredPosition.x {previous:0.###}->{targetX:0.###}";
                    changed = true;
                }
            }

            if (changed)
            {
                var count = ++appliedCount;
                if (count <= 20 || count % 500 == 0)
                    Plugin.Log.LogInfo($"Layout maintainer applied {rule.Description}; {details} ({count}).");
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized)
                return;

            objectClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Object");
            componentClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Component");
            transformClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "Transform");
            rectTransformClass = RequireClass("UnityEngine.CoreModule.dll", "UnityEngine", "RectTransform");

            objectGetName = RequireMethod(objectClass, "get_name", 0);
            componentGetTransform = RequireMethod(componentClass, "get_transform", 0);
            transformGetParent = RequireMethod(transformClass, "get_parent", 0);
            transformGetChildCount = RequireMethod(transformClass, "get_childCount", 0);
            transformGetChild = RequireMethod(transformClass, "GetChild", 1);
            rectGetAnchoredPosition = RequireMethod(rectTransformClass, "get_anchoredPosition", 0);
            rectSetAnchoredPosition = RequireMethod(rectTransformClass, "set_anchoredPosition", 1);
            rectGetSizeDelta = RequireMethod(rectTransformClass, "get_sizeDelta", 0);
            rectSetSizeDelta = RequireMethod(rectTransformClass, "set_sizeDelta", 1);

            initialized = true;
            Plugin.Log.LogInfo("Layout maintainer resolved Unity RectTransform APIs.");
        }

        private static string BuildPath(IntPtr transform)
        {
            var names = new List<string>();
            var current = transform;
            for (var depth = 0; depth < MaxPathDepth && current != IntPtr.Zero; depth++)
            {
                var name = InvokeString(objectGetName, current);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
                current = InvokeObject(transformGetParent, current);
            }

            names.Reverse();
            return "/" + string.Join("/", names);
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

        private static unsafe void InvokeSetVector2(IntPtr method, IntPtr instance, Vector2Value value)
        {
            var args = stackalloc void*[1];
            args[0] = &value;
            InvokeObject(method, instance, args);
        }

        private static void ReportFailure(string phase, Exception ex)
        {
            var count = ++failureCount;
            if (count <= 12 || count % 500 == 0)
                Plugin.Log.LogDebug($"Layout maintainer {phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal sealed class LayoutRule
    {
        public LayoutRule(string path, float? width = null, float? anchoredXBySignMagnitude = null, float? anchoredX = null)
        {
            Path = path;
            LeafName = path[(path.LastIndexOf('/') + 1)..];
            Width = width;
            AnchoredXBySignMagnitude = anchoredXBySignMagnitude;
            AnchoredX = anchoredX;
            Description = width.HasValue
                ? $"{path} width={width.Value}"
                : anchoredX.HasValue
                    ? $"{path} anchoredX={anchoredX.Value}"
                    : $"{path} anchoredX=+/-{anchoredXBySignMagnitude.GetValueOrDefault()}";
        }

        public string Path { get; }
        public string LeafName { get; }
        public float? Width { get; }
        public float? AnchoredXBySignMagnitude { get; }
        public float? AnchoredX { get; }
        public string Description { get; }
    }

    internal enum LayoutWriteKind
    {
        Any,
        AnchoredPosition,
        SizeDelta,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vector2Value
    {
        public float X;
        public float Y;
    }
}
