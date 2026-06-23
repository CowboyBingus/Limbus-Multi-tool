using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System;

namespace LimbusFramePacingFix.GameWrappers
{
    public sealed class GlobalGameManager : Il2CppObjectBase
    {
        private static readonly IntPtr NativeMethodInfoPtr_SetFrameRateOnSceneLoaded = InitializeSetFrameRateOnSceneLoaded();

        private static IntPtr InitializeSetFrameRateOnSceneLoaded()
        {
            Il2CppClassPointerStore<GlobalGameManager>.NativeClassPtr =
                IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "GlobalGameManager");
            IL2CPP.il2cpp_runtime_class_init(Il2CppClassPointerStore<GlobalGameManager>.NativeClassPtr);
            return IL2CPP.GetIl2CppMethod(
                Il2CppClassPointerStore<GlobalGameManager>.NativeClassPtr,
                false,
                "SetFrameRateOnSceneLoaded",
                "System.Void",
                new[] { "System.String" });
        }

        public GlobalGameManager(IntPtr pointer)
            : base(pointer)
        {
        }

        public static void SetFrameRateOnSceneLoaded(IntPtr sceneName)
        {
            _ = sceneName;
            _ = NativeMethodInfoPtr_SetFrameRateOnSceneLoaded;
        }
    }
}

namespace LimbusFramePacingFix.GameWrappers.LocalSave
{
    public sealed class LocalGameOptionData : Il2CppObjectBase
    {
        private static readonly IntPtr NativeMethodInfoPtr_ApplyFrameRate = InitializeApplyFrameRate();

        private static IntPtr InitializeApplyFrameRate()
        {
            Il2CppClassPointerStore<LocalGameOptionData>.NativeClassPtr =
                IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "LocalSave", "LocalGameOptionData");
            IL2CPP.il2cpp_runtime_class_init(Il2CppClassPointerStore<LocalGameOptionData>.NativeClassPtr);
            return IL2CPP.GetIl2CppMethod(
                Il2CppClassPointerStore<LocalGameOptionData>.NativeClassPtr,
                false,
                "ApplyFrameRate",
                "System.Void",
                new[] { "System.Boolean" });
        }

        public LocalGameOptionData(IntPtr pointer)
            : base(pointer)
        {
        }

        public static void ApplyFrameRate(bool isBattle)
        {
            _ = isBattle;
            _ = NativeMethodInfoPtr_ApplyFrameRate;
        }
    }
}
