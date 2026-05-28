// Standalone test: feed the live Resources file + GameAssembly.dll into STOCK LibCpp2IL/Cpp2IL.
// If this works, stock LibCpp2IL can parse the current PM build.
// If it fails at "magic mismatch" or similar, the metadata format still needs custom handling.

using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TestStockCpp2IL;

public static class Program
{
    public static int Main(string[] args)
    {
        // Resolve assemblies from the stock BepInEx zip extraction
        string stockDir = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "bepin-be755");
        string coreDir = Path.Combine(stockDir, "BepInEx", "core");
        if (!Directory.Exists(coreDir))
        {
            Console.Error.WriteLine($"Stock BepInEx core not found: {coreDir}");
            Console.Error.WriteLine("Pass the path to the unzipped stock BepInEx as the first argument.");
            return 2;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var p = Path.Combine(coreDir, name);
            if (File.Exists(p))
            {
                Console.WriteLine($"  resolved: {name}");
                return Assembly.LoadFrom(p);
            }
            return null;
        };

        // Locate game files
        string game = @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        string gaPath = Path.Combine(game, "GameAssembly.dll");
        string resPath = Path.Combine(game, "LimbusCompany_Data", "il2cpp_data", "Resources",
                                       "System.JsonExtensions.dll-resources.dat");
        string metaPath = Path.Combine(game, "LimbusCompany_Data", "il2cpp_data", "Metadata",
                                        "global-metadata.dat");

        // First check: print magic bytes of both metadata candidates and the canonical-expected magic
        Console.WriteLine("=== File magic check ===");
        ShowMagic("Resources/...JsonExtensions.dat", resPath);
        ShowMagic("Metadata/global-metadata.dat",   metaPath);
        Console.WriteLine("Canonical IL2CPP magic = AF 1B B1 FA");
        Console.WriteLine();

        // Load LibCpp2IL via reflection so we can call it with the stock binary
        var libCpp = Assembly.LoadFrom(Path.Combine(coreDir, "LibCpp2IL.dll"));
        var libMain = libCpp.GetType("LibCpp2IL.LibCpp2IlMain");
        var unityVerType = libCpp.GetType("LibCpp2IL.Versions.UnityVersion");
        var loadFromFile = libMain.GetMethod("LoadFromFile",
            BindingFlags.Public | BindingFlags.Static);

        // Build UnityVersion 2021.3.28
        var fromString = unityVerType.GetMethod("Parse",
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        object unityVer = fromString != null
            ? fromString.Invoke(null, new object[] { "2021.3.28" })
            : Activator.CreateInstance(unityVerType, new object[] { 2021, 3, 28 });

        foreach (var (label, mPath) in new[]
                 {
                     ("Resources/JsonExtensions", resPath),
                     ("Metadata/global-metadata", metaPath),
                 })
        {
            Console.WriteLine($"=== Stock LibCpp2IL.LoadFromFile(GameAssembly.dll, {label}) ===");
            try
            {
                var ok = loadFromFile.Invoke(null, new object[] { gaPath, mPath, unityVer });
                Console.WriteLine($"  Result: {ok}");
            }
            catch (TargetInvocationException tie)
            {
                Console.WriteLine($"  EXCEPTION: {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                if (tie.InnerException?.StackTrace != null)
                {
                    var lines = tie.InnerException.StackTrace.Split('\n');
                    foreach (var l in lines.Take(6)) Console.WriteLine($"    {l.TrimEnd()}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            Console.WriteLine();
        }

        return 0;
    }

    static void ShowMagic(string label, string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  {label}: MISSING"); return; }
        var fs = File.OpenRead(path);
        var b = new byte[16];
        fs.Read(b, 0, 16);
        fs.Close();
        Console.WriteLine($"  {label,-32}  size={new FileInfo(path).Length}  magic={BitConverter.ToString(b, 0, 8).Replace('-',' ')}");
    }
}
