using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LimbusShared.Detours;
using LimbusShared.Interop;
using LimbusShared.Unity;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using static LimbusShared.Interop.Il2CppLookup;
using static LimbusShared.Interop.NativeInterop;

namespace LimbusCanvasFix
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID    = "com.you.limbuscanvasfix";
        public const string NAME    = "LimbusCanvasFix";
        public const string VERSION = "1.2.1";

        public override void Load()
        {
            CanvasFixHost.Initialize(base.Log);
            CanvasFixHost.Log.LogInfo($"{NAME} {VERSION} loading...");

            ApplyPatches();

            CanvasFixHost.Log.LogInfo($"{NAME} ready.");
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
                    CanvasFixHost.Log.LogDebug($"Guard stub pass skipped: {ex.Message}");
                }
                CanvasFixHost.Log.LogInfo("Runtime patches applied.");
            }
            catch (Exception ex)
            {
                CanvasFixHost.Log.LogError($"Patch failed: {ex}");
            }
        }

        private static void LogKnownIl2CppImages()
        {
            try
            {
                var field = AccessTools.Field(typeof(IL2CPP), "ourImagesMap");
                if (field?.GetValue(null) is not Dictionary<string, IntPtr> images)
                    return;

                CanvasFixHost.Log.LogInfo($"IL2CPP image count: {images.Count}");
                foreach (var key in images.Keys.Where(x =>
                    x.Contains("UI", StringComparison.OrdinalIgnoreCase) ||
                    x.Contains(UnityInteropNames.Namespace, StringComparison.OrdinalIgnoreCase)))
                {
                    CanvasFixHost.Log.LogInfo($"IL2CPP image: {key}");
                }
            }
            catch (Exception ex)
            {
                CanvasFixHost.Log.LogDebug($"Could not inspect IL2CPP images: {ex.Message}");
            }
        }
    }

    internal static class UnityInteropNames
    {
        public const string CoreModule = "UnityEngine.CoreModule.dll";
        public const string Namespace = "UnityEngine";
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
                CanvasFixHost.Log.LogWarning("CanvasScaler.OnEnable detour skipped: class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "OnEnable", 0);
            if (method == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("CanvasScaler.OnEnable detour skipped: method was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("CanvasScaler.OnEnable detour skipped: method pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<OnEnableDelegate>();
            detour.Apply();
            CanvasFixHost.Log.LogInfo($"CanvasScaler.OnEnable detour installed at {Ptr(methodPointer)}.");
        }

        public static void Uninstall()
        {
            DetourLifecycle.Free(ref detour, ref original);
        }

        private static void OnEnableReplacement(IntPtr self, IntPtr methodInfo)
        {
            original?.Invoke(self, methodInfo);
                NativeCanvasScaler.Apply(self);
            LayoutRuleMaintainer.ObserveCanvasScaler(self);
        }

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

            var klass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "RectTransform");
            if (klass == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Layout maintainer skipped: UnityEngine.RectTransform class was not resolved.");
                return;
            }

            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "SendReapplyDrivenProperties", 1);
            if (method == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Layout maintainer skipped: RectTransform.SendReapplyDrivenProperties was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Layout maintainer skipped: RectTransform.SendReapplyDrivenProperties pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<SendReapplyDrivenPropertiesDelegate>();
            detour.Apply();
            CanvasFixHost.Log.LogInfo($"Layout maintainer installed at {Ptr(methodPointer)}.");
        }

        public static void Uninstall()
        {
            DetourLifecycle.Free(ref detour, ref original);
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

    }

    internal static class RectTransformWriteDetours
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetVector2Delegate(IntPtr self, Vector2Value value, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetVector3Delegate(IntPtr self, Vector3Value value, IntPtr methodInfo);

        private static NativeDetour? anchoredPositionDetour;
        private static NativeDetour? sizeDeltaDetour;
        private static NativeDetour? localScaleDetour;
        private static SetVector2Delegate? anchoredPositionOriginal;
        private static SetVector2Delegate? sizeDeltaOriginal;
        private static SetVector3Delegate? localScaleOriginal;
        private static readonly SetVector2Delegate anchoredPositionReplacement = AnchoredPositionReplacement;
        private static readonly SetVector2Delegate sizeDeltaReplacement = SizeDeltaReplacement;
        private static readonly SetVector3Delegate localScaleReplacement = LocalScaleReplacement;

        public static void Install()
        {
            if (anchoredPositionDetour != null || sizeDeltaDetour != null || localScaleDetour != null)
                return;

            var rectClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "RectTransform");
            if (rectClass == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Layout write maintainer skipped: UnityEngine.RectTransform class was not resolved.");
            }
            else
            {
                InstallOne(
                    rectClass,
                    "set_anchoredPosition",
                    anchoredPositionReplacement,
                    ref anchoredPositionDetour,
                    ref anchoredPositionOriginal,
                    "RectTransform.set_anchoredPosition");
                InstallOne(
                    rectClass,
                    "set_sizeDelta",
                    sizeDeltaReplacement,
                    ref sizeDeltaDetour,
                    ref sizeDeltaOriginal,
                    "RectTransform.set_sizeDelta");
            }

            var transformClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Transform");
            if (transformClass == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Layout write maintainer skipped: UnityEngine.Transform class was not resolved.");
            }
            else
            {
                InstallOne(
                    transformClass,
                    "set_localScale",
                    localScaleReplacement,
                    ref localScaleDetour,
                    ref localScaleOriginal,
                    "Transform.set_localScale");
            }
        }

        public static void Uninstall()
        {
            DetourLifecycle.Free(ref anchoredPositionDetour, ref anchoredPositionOriginal);
            DetourLifecycle.Free(ref sizeDeltaDetour, ref sizeDeltaOriginal);
            DetourLifecycle.Free(ref localScaleDetour, ref localScaleOriginal);
        }

        private static void InstallOne<T>(
            IntPtr klass,
            string methodName,
            T replacement,
            ref NativeDetour? detour,
            ref T? original,
            string label)
            where T : Delegate
        {
            var method = IL2CPP.il2cpp_class_get_method_from_name(klass, methodName, 1);
            if (method == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning($"Layout write maintainer skipped: {label} was not resolved.");
                return;
            }

            var methodPointer = Marshal.ReadIntPtr(method);
            if (methodPointer == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning($"Layout write maintainer skipped: {label} pointer was null.");
                return;
            }

            detour = new NativeDetour(methodPointer, replacement);
            original = detour.GenerateTrampoline<T>();
            detour.Apply();
            CanvasFixHost.Log.LogInfo($"Layout write maintainer installed {label} at {Ptr(methodPointer)}.");
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

        private static void LocalScaleReplacement(IntPtr self, Vector3Value value, IntPtr methodInfo)
        {
            localScaleOriginal?.Invoke(self, value, methodInfo);
            try
            {
                if (!LayoutRuleMaintainer.IsApplying)
                    LayoutRuleMaintainer.ApplyIfTarget(self, LayoutWriteKind.LocalScale);
            }
            catch (Exception ex)
            {
                LayoutRuleMaintainer.ReportHookFailure("localScale write hook", ex);
            }
        }

    }

    internal static class NativeCanvasScaler
    {
        private const int ScaleWithScreenSize = 1;

        private static bool initialized;
        private static nint uiScaleModeField;
        private static nint referenceResolutionField;
        private static nint matchWidthOrHeightField;
        private static int appliedCount;

        public static void Apply(IntPtr obj)
        {
            if (obj == IntPtr.Zero || !EnsureInitialized()) return;

            unsafe
            {
                var mode = 0;
                IL2CPP.il2cpp_field_get_value(obj, uiScaleModeField, &mode);
                if (mode != ScaleWithScreenSize) return;

                var resolution = stackalloc float[2];
                resolution[0] = 3000f;
                resolution[1] = 1440f;
                IL2CPP.il2cpp_field_set_value(obj, referenceResolutionField, resolution);

                var match = 1f;
                IL2CPP.il2cpp_field_set_value(obj, matchWidthOrHeightField, &match);
            }

            appliedCount++;
            if (appliedCount <= 5)
                CanvasFixHost.Log.LogInfo($"Applied CanvasScaler ultrawide fix ({appliedCount}).");
        }

        private static bool EnsureInitialized()
        {
            if (initialized) return true;

            var klass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.UI", "CanvasScaler");
            if (klass == IntPtr.Zero)
            {
                CanvasFixHost.Log.LogWarning("Could not resolve UnityEngine.UI.CanvasScaler IL2CPP class.");
                return false;
            }

            uiScaleModeField = IL2CPP.GetIl2CppField(klass, "m_UiScaleMode");
            referenceResolutionField = IL2CPP.GetIl2CppField(klass, "m_ReferenceResolution");
            matchWidthOrHeightField = IL2CPP.GetIl2CppField(klass, "m_MatchWidthOrHeight");

            initialized = uiScaleModeField != IntPtr.Zero
                && referenceResolutionField != IntPtr.Zero
                && matchWidthOrHeightField != IntPtr.Zero;

            if (!initialized)
                CanvasFixHost.Log.LogWarning("Could not resolve one or more CanvasScaler IL2CPP fields.");
            else
                CanvasFixHost.Log.LogInfo("Resolved CanvasScaler IL2CPP fields.");

            return initialized;
        }
    }

    internal static class LayoutRuleMaintainer
    {
        private const int MaxPathDepth = 80;
        private const int MaxScanNodes = 6000;
        private const float DesignScreenWidth = 3440f;
        private const float DesignScreenHeight = 1492f;
        private const float MinHorizontalScale = 0.25f;
        private const float MaxHorizontalScale = 3.0f;
        private const float Epsilon = 0.01f;

        private static readonly LayoutRule[] rules =
        {
            new("/[Script]LoginSceneManager/[Canvas]/[Image]TouchToStart", width: -3000f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Rect]UnitStatusContent", anchoredXBySignMagnitude: 900f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Script]TabContentManager", anchoredXBySignMagnitude: 1000f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Script]SideButtonList", anchoredX: 335f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Image]UnitStatusPanel", width: 3625f),
            new("/[Script]BattleUIRoot/[Canvas]BattleFrontUI/[Script]UnitInformationController/[Script]UnitInformationController_Renewal/[Canvas]AboveSpine/[Trigger]SkillSummaryPanel/[Script]SkillSummaryPanel", anchoredXWhenCurrentNegative: 250f),
            new("/[Canvas]RatioMainUI/[Rect]PanelRoot/[UIPanel]RecordMemory_MainEvent(Clone)/[Rect]BG", scaleXYMax: 1.25f),
        };

        private static readonly Dictionary<string, LayoutRule> rulesByPath = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<LayoutRule>> rulesByLeafName = new(StringComparer.Ordinal);
        private static bool initialized;
        private static int appliedCount;
        private static int failureCount;
        private static int scanCount;
        [ThreadStatic] private static bool isApplying;

        private static IntPtr rectTransformClass;
        private static IntPtr objectGetName;
        private static IntPtr componentGetTransform;
        private static IntPtr transformGetParent;
        private static IntPtr transformGetChildCount;
        private static IntPtr transformGetChild;
        private static IntPtr transformGetLocalScale;
        private static IntPtr transformSetLocalScale;
        private static IntPtr rectGetAnchoredPosition;
        private static IntPtr rectSetAnchoredPosition;
        private static IntPtr rectGetSizeDelta;
        private static IntPtr rectSetSizeDelta;
        private static IntPtr screenGetWidth;
        private static IntPtr screenGetHeight;
        private static int lastLoggedScaleWidth;
        private static int lastLoggedScaleHeight;

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
                var transform = Il2CppInvoke.Object(componentGetTransform, canvasScaler);
                if (transform == IntPtr.Zero)
                    return;

                var visited = 0;
                ScanSubtree(transform, ref visited);
                var count = ++scanCount;
                if (count <= 12 || count % 100 == 0)
                    CanvasFixHost.Log.LogInfo($"Layout maintainer scanned CanvasScaler root #{count}: visited={visited}.");
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
            try
            {
                var name = Il2CppInvoke.String(objectGetName, rectTransform);
                if (!rulesByLeafName.TryGetValue(name, out var candidates))
                {
                    rule = null!;
                    return false;
                }

                var path = BuildPath(rectTransform);
                foreach (var candidate in candidates)
                {
                    if (rulesByPath.TryGetValue(path, out var matched) && ReferenceEquals(matched, candidate))
                    {
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

            var childCount = Il2CppInvoke.Int32(transformGetChildCount, transform);
            for (var i = 0; i < childCount && visited < MaxScanNodes; i++)
            {
                var child = Il2CppInvoke.ObjectWithIntArg(transformGetChild, transform, i);
                ScanSubtree(child, ref visited);
            }
        }

        private static bool IsRectTransformObject(IntPtr obj)
        {
            return Il2CppLookup.IsObjectClassOrNamed(obj, rectTransformClass, "RectTransform");
        }

        private static void ApplyRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind)
        {
            if (rect == IntPtr.Zero)
                return;

            var details = new List<string>();
            var horizontalScale = GetHorizontalScale();
            ApplyWidthRule(rule, rect, kind, horizontalScale, details);
            ApplyAnchoredSignRule(rule, rect, kind, horizontalScale, details);
            ApplyAnchoredXRule(rule, rect, kind, horizontalScale, details);
            ApplyAnchoredXWhenNegativeRule(rule, rect, kind, horizontalScale, details);
            ApplyScaleRule(rule, rect, kind, details);

            LogRuleApplication(rule, details);
        }

        private static void ApplyWidthRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind, float horizontalScale, List<string> details)
        {
            if (!ShouldApply(kind, LayoutWriteKind.SizeDelta) || !rule.Width.HasValue)
                return;

            var size = Il2CppInvoke.Struct<Vector2Value>(rectGetSizeDelta, rect);
            var targetWidth = ScaleHorizontal(rule.Width.Value, horizontalScale);
            if (Math.Abs(size.X - targetWidth) <= Epsilon)
                return;

            var previous = size.X;
            size.X = targetWidth;
            Il2CppInvoke.SetStruct(rectSetSizeDelta, rect, size);
            details.Add($"sizeDelta.x {previous:0.###}->{targetWidth:0.###} scale={horizontalScale:0.###}");
        }

        private static void ApplyAnchoredSignRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind, float horizontalScale, List<string> details)
        {
            if (!ShouldApply(kind, LayoutWriteKind.AnchoredPosition) || !rule.AnchoredXBySignMagnitude.HasValue)
                return;

            var position = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchoredPosition, rect);
            if (Math.Abs(position.X) <= Epsilon)
                return;

            var magnitude = ScaleHorizontal(rule.AnchoredXBySignMagnitude.Value, horizontalScale);
            var targetX = position.X < 0 ? -magnitude : magnitude;
            ApplyAnchoredX(rect, position, targetX, horizontalScale, details);
        }

        private static void ApplyAnchoredXRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind, float horizontalScale, List<string> details)
        {
            if (!ShouldApply(kind, LayoutWriteKind.AnchoredPosition) || !rule.AnchoredX.HasValue)
                return;

            var position = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchoredPosition, rect);
            ApplyAnchoredX(rect, position, ScaleHorizontal(rule.AnchoredX.Value, horizontalScale), horizontalScale, details);
        }

        private static void ApplyAnchoredXWhenNegativeRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind, float horizontalScale, List<string> details)
        {
            if (!ShouldApply(kind, LayoutWriteKind.AnchoredPosition) || !rule.AnchoredXWhenCurrentNegative.HasValue)
                return;

            var position = Il2CppInvoke.Struct<Vector2Value>(rectGetAnchoredPosition, rect);
            if (position.X >= -Epsilon)
                return;

            var targetX = ScaleHorizontal(rule.AnchoredXWhenCurrentNegative.Value, horizontalScale);
            ApplyAnchoredX(rect, position, targetX, horizontalScale, details);
        }

        private static void ApplyAnchoredX(IntPtr rect, Vector2Value position, float targetX, float horizontalScale, List<string> details)
        {
            if (Math.Abs(position.X - targetX) <= Epsilon)
                return;

            var previous = position.X;
            position.X = targetX;
            Il2CppInvoke.SetStruct(rectSetAnchoredPosition, rect, position);
            details.Add($"anchoredPosition.x {previous:0.###}->{targetX:0.###} scale={horizontalScale:0.###}");
        }

        private static void ApplyScaleRule(LayoutRule rule, IntPtr rect, LayoutWriteKind kind, List<string> details)
        {
            if (!ShouldApply(kind, LayoutWriteKind.LocalScale) || !rule.ScaleXYMax.HasValue)
                return;

            var scale = Il2CppInvoke.Struct<Vector3Value>(transformGetLocalScale, rect);
            var windowScale = GetUniformWindowScale();
            var targetScale = ScaleFromOneToMax(rule.ScaleXYMax.Value, windowScale);
            if (Math.Abs(scale.X - targetScale) <= Epsilon && Math.Abs(scale.Y - targetScale) <= Epsilon)
                return;

            var previousX = scale.X;
            var previousY = scale.Y;
            scale.X = targetScale;
            scale.Y = targetScale;
            Il2CppInvoke.SetStruct(transformSetLocalScale, rect, scale);
            details.Add($"localScale.xy ({previousX:0.###},{previousY:0.###})->{targetScale:0.###} windowScale={windowScale:0.###}");
        }

        private static void LogRuleApplication(LayoutRule rule, List<string> details)
        {
            if (details.Count == 0)
                return;

            var count = ++appliedCount;
            if (count <= 20 || count % 500 == 0)
                CanvasFixHost.Log.LogInfo($"Layout maintainer applied {rule.Description}; {string.Join("; ", details)} ({count}).");
        }

        private static bool ShouldApply(LayoutWriteKind actual, LayoutWriteKind requested)
        {
            return actual == LayoutWriteKind.Any || actual == requested;
        }

        private static void EnsureInitialized()
        {
            if (initialized)
                return;

            IntPtr objectClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Object");
            IntPtr componentClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Component");
            IntPtr transformClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Transform");
            rectTransformClass = RequireClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "RectTransform");
            var screenClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, UnityInteropNames.Namespace, "Screen");
            var deviceScreenClass = IL2CPP.GetIl2CppClass(UnityInteropNames.CoreModule, "UnityEngine.Device", "Screen");

            objectGetName = RequireMethod(objectClass, "get_name", 0);
            componentGetTransform = RequireMethod(componentClass, "get_transform", 0);
            transformGetParent = RequireMethod(transformClass, "get_parent", 0);
            transformGetChildCount = RequireMethod(transformClass, "get_childCount", 0);
            transformGetChild = RequireMethod(transformClass, "GetChild", 1);
            transformGetLocalScale = RequireMethod(transformClass, "get_localScale", 0);
            transformSetLocalScale = RequireMethod(transformClass, "set_localScale", 1);
            rectGetAnchoredPosition = RequireMethod(rectTransformClass, "get_anchoredPosition", 0);
            rectSetAnchoredPosition = RequireMethod(rectTransformClass, "set_anchoredPosition", 1);
            rectGetSizeDelta = RequireMethod(rectTransformClass, "get_sizeDelta", 0);
            rectSetSizeDelta = RequireMethod(rectTransformClass, "set_sizeDelta", 1);
            screenGetWidth = FindOptionalMethod(screenClass, deviceScreenClass, "get_width", 0);
            screenGetHeight = FindOptionalMethod(screenClass, deviceScreenClass, "get_height", 0);

            initialized = true;
            CanvasFixHost.Log.LogInfo("Layout maintainer resolved Unity RectTransform APIs.");
            if (screenGetWidth != IntPtr.Zero && screenGetHeight != IntPtr.Zero)
                CanvasFixHost.Log.LogInfo($"Layout maintainer will scale horizontal rules from {DesignScreenWidth:0}x{DesignScreenHeight:0} design aspect.");
            else
                CanvasFixHost.Log.LogWarning("Layout maintainer could not resolve Unity Screen width/height APIs; horizontal rules will use design values.");
        }

        private static string BuildPath(IntPtr transform)
        {
            var names = new Stack<string>();
            var current = transform;
            for (var depth = 0; depth < MaxPathDepth && current != IntPtr.Zero; depth++)
            {
                var name = Il2CppInvoke.String(objectGetName, current);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Push(name);
                current = Il2CppInvoke.Object(transformGetParent, current);
            }

            return "/" + string.Join("/", names);
        }

        private static bool PathMatchesRule(IntPtr transform, LayoutRule rule)
        {
            var name = Il2CppInvoke.String(objectGetName, transform);
            if (!string.Equals(name, rule.LeafName, StringComparison.Ordinal))
                return false;

            var currentPath = BuildPath(transform);
            return string.Equals(currentPath, rule.Path, StringComparison.Ordinal);
        }

        private static float GetHorizontalScale()
        {
            if (!TryGetScreenSize(out var width, out var height))
                return 1f;

            var designAspect = DesignScreenWidth / DesignScreenHeight;
            var currentAspect = width / (float)height;
            var scale = currentAspect / designAspect;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                return 1f;

            scale = Math.Clamp(scale, MinHorizontalScale, MaxHorizontalScale);
            if (width != lastLoggedScaleWidth || height != lastLoggedScaleHeight)
            {
                lastLoggedScaleWidth = width;
                lastLoggedScaleHeight = height;
                CanvasFixHost.Log.LogInfo($"Layout maintainer horizontal scale={scale:0.###} for screen {width}x{height}.");
            }

            return scale;
        }

        private static float ScaleHorizontal(float value, float scale) => value * scale;

        private static float GetUniformWindowScale()
        {
            if (!TryGetScreenSize(out var width, out var height))
                return 1f;

            var scale = Math.Min(width / DesignScreenWidth, height / DesignScreenHeight);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                return 1f;

            return Math.Clamp(scale, 0f, 1f);
        }

        private static float ScaleFromOneToMax(float maxValue, float scale)
        {
            if (maxValue <= 1f)
                return maxValue;

            return 1f + ((maxValue - 1f) * scale);
        }

        private static bool TryGetScreenSize(out int width, out int height)
        {
            width = 0;
            height = 0;
            if (screenGetWidth == IntPtr.Zero || screenGetHeight == IntPtr.Zero)
                return false;

            try
            {
                width = Il2CppInvoke.Int32(screenGetWidth, IntPtr.Zero);
                height = Il2CppInvoke.Int32(screenGetHeight, IntPtr.Zero);
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                ReportFailure("screen size lookup", ex);
                return false;
            }
        }

        private static IntPtr FindOptionalMethod(IntPtr primaryClass, IntPtr fallbackClass, string name, int args)
        {
            var method = primaryClass == IntPtr.Zero
                ? IntPtr.Zero
                : IL2CPP.il2cpp_class_get_method_from_name(primaryClass, name, args);
            if (method != IntPtr.Zero)
                return method;

            return fallbackClass == IntPtr.Zero
                ? IntPtr.Zero
                : IL2CPP.il2cpp_class_get_method_from_name(fallbackClass, name, args);
        }

        private static void ReportFailure(string phase, Exception ex)
        {
            var count = ++failureCount;
            if (count <= 12 || count % 500 == 0)
                CanvasFixHost.Log.LogDebug($"Layout maintainer {phase} failure #{count}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal sealed class LayoutRule
    {
        public LayoutRule(string path, float? width = null, float? anchoredXBySignMagnitude = null, float? anchoredX = null, float? anchoredXWhenCurrentNegative = null, float? scaleXYMax = null)
        {
            Path = path;
            LeafName = path[(path.LastIndexOf('/') + 1)..];
            Width = width;
            AnchoredXBySignMagnitude = anchoredXBySignMagnitude;
            AnchoredX = anchoredX;
            AnchoredXWhenCurrentNegative = anchoredXWhenCurrentNegative;
            ScaleXYMax = scaleXYMax;
            Description = BuildDescription(path, width, anchoredXBySignMagnitude, anchoredX, anchoredXWhenCurrentNegative, scaleXYMax);
        }

        public string Path { get; }
        public string LeafName { get; }
        public float? Width { get; }
        public float? AnchoredXBySignMagnitude { get; }
        public float? AnchoredX { get; }
        public float? AnchoredXWhenCurrentNegative { get; }
        public float? ScaleXYMax { get; }
        public string Description { get; }

        private static string BuildDescription(
            string path,
            float? width,
            float? anchoredXBySignMagnitude,
            float? anchoredX,
            float? anchoredXWhenCurrentNegative,
            float? scaleXYMax)
        {
            if (width.HasValue)
                return $"{path} width={width.Value}";
            if (anchoredX.HasValue)
                return $"{path} anchoredX={anchoredX.Value}";
            if (anchoredXWhenCurrentNegative.HasValue)
                return $"{path} anchoredXWhenCurrentNegative={anchoredXWhenCurrentNegative.Value}";
            if (scaleXYMax.HasValue)
                return $"{path} scaleXYMax={scaleXYMax.Value}";

            return $"{path} anchoredX=+/-{anchoredXBySignMagnitude.GetValueOrDefault()}";
        }
    }

    internal enum LayoutWriteKind
    {
        Any,
        AnchoredPosition,
        SizeDelta,
        LocalScale,
    }

}
