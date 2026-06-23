using Il2CppInterop.Runtime;
using System;
using System.Runtime.InteropServices;

namespace LimbusShared;

internal static class Il2CppLookup
{
    public static IntPtr RequireClass(string assembly, string ns, string name)
    {
        var klass = IL2CPP.GetIl2CppClass(assembly, ns, name);
        if (klass == IntPtr.Zero)
            throw new MissingMemberException($"IL2CPP class not found: {ns}.{name} in {assembly}");

        return klass;
    }

    public static IntPtr RequireMethod(IntPtr klass, string name, int args)
    {
        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, args);
        if (method == IntPtr.Zero)
            throw new MissingMethodException($"IL2CPP method not found: {name}/{args}");

        return method;
    }

    public static bool IsObjectClassOrNamed(IntPtr obj, IntPtr expectedClass, string fallbackName)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (klass == IntPtr.Zero)
                return false;

            if (klass == expectedClass)
                return true;

            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "";
            return name == fallbackName;
        }
        catch
        {
            return false;
        }
    }
}
