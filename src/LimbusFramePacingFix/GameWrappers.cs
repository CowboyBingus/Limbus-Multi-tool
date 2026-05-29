using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System;

namespace LimbusFramePacingFix.GameWrappers
{
    public sealed class GlobalGameManager : Il2CppObjectBase
    {
        private static readonly IntPtr NativeMethodInfoPtr_SetFrameRateOnSceneLoaded;

        static GlobalGameManager()
        {
            Il2CppClassPointerStore<GlobalGameManager>.NativeClassPtr =
                IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "GlobalGameManager");
            IL2CPP.il2cpp_runtime_class_init(Il2CppClassPointerStore<GlobalGameManager>.NativeClassPtr);
            NativeMethodInfoPtr_SetFrameRateOnSceneLoaded = IL2CPP.GetIl2CppMethod(
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

        public void SetFrameRateOnSceneLoaded(IntPtr sceneName)
        {
            _ = NativeMethodInfoPtr_SetFrameRateOnSceneLoaded;
        }
    }
}

namespace LimbusFramePacingFix.GameWrappers.LocalSave
{
    public sealed class LocalGameOptionData : Il2CppObjectBase
    {
        private static readonly IntPtr NativeMethodInfoPtr_ApplyFrameRate;

        static LocalGameOptionData()
        {
            Il2CppClassPointerStore<LocalGameOptionData>.NativeClassPtr =
                IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "LocalSave", "LocalGameOptionData");
            IL2CPP.il2cpp_runtime_class_init(Il2CppClassPointerStore<LocalGameOptionData>.NativeClassPtr);
            NativeMethodInfoPtr_ApplyFrameRate = IL2CPP.GetIl2CppMethod(
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

        public void ApplyFrameRate(bool isBattle)
        {
            _ = NativeMethodInfoPtr_ApplyFrameRate;
        }
    }
}
