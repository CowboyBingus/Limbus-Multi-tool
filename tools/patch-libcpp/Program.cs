// Patches LibCpp2IL.dll so that Il2CppBinary.GetGenericMethodFromIndex
// catches any exception inside its body and returns -1, instead of
// propagating IndexOutOfRangeException up through Cpp2IL.Init().
//
// The bug: Il2CppMethodSpec.MethodDefinition does theMetadata.methodDefs[methodDefinitionIndex]
// without bounds-checking. When PM's binary contains a method spec whose
// methodDefinitionIndex is outside the methodDefs array, the whole interop generation aborts.
//
// The wrapper makes failed GetGenericMethodFromIndex entries return -1.
// Generic-method spec entries that fail get skipped; the rest of the metadata
// parses normally, which is enough for our plugin (which only patches
// Unity-stock CanvasScaler).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace PatchLibCpp;

public static class Program
{
    private const string AsmResolverAssemblyPopulatorType = "Cpp2IL.Core.Utils.AsmResolver.AsmResolverAssemblyPopulator";
    private const string Il2CppBinaryType = "LibCpp2IL.Il2CppBinary";

    public static int Main(string[] args)
    {
        string coreDir = args.Length > 0
            ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\BepInEx\core";

        if (Directory.Exists(Path.Combine(coreDir, "BepInEx", "core")))
            coreDir = Path.Combine(coreDir, "BepInEx", "core");

        // Patch LibCpp2IL.dll first.
        PatchLibCpp2IL(Path.Combine(coreDir, "LibCpp2IL.dll"));
        // Then Cpp2IL.Core.dll (Skibidi2IL fork) for higher-level analysis-context wraps.
        PatchCpp2IlCore(Path.Combine(coreDir, "Cpp2IL.Core.dll"));
        // Finally patch the interop generator passes that assume fully-populated
        // Cpp2IL output. Stub-only output intentionally violates that assumption.
        PatchIl2CppInteropGenerator(Path.Combine(coreDir, "Il2CppInterop.Generator.dll"));
        return 0;
    }

    static void PatchIl2CppInteropGenerator(string dll)
    {
        if (!File.Exists(dll)) { Console.WriteLine($"WARN: {dll} not found, skipping"); return; }
        string bak = dll + ".unpatched";
        if (!File.Exists(bak)) { File.Copy(dll, bak); Console.WriteLine($"Backup created: {bak}"); }
        else { Console.WriteLine($"Restoring from backup: {bak}"); File.Copy(bak, dll, overwrite: true); }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dll));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(dll, rp);
        var module = asm.MainModule;

        var generatorTargets = new (string TypeName, string MethodName, int ParamCount)[]
        {
            // With sparse Cpp2IL stubs, this pass can fail looking for conversion
            // targets that were never populated. Conversions are nonessential for
            // BepInEx bootstrap, so skip the whole pass if it trips.
            ("Il2CppInterop.Generator.Passes.Pass60AddImplicitConversions", "DoPass", 1),
            // Stub-only Cpp2IL output can leave unstrip translation without a
            // matching rewritten method. Method bodies are not required for the
            // metadata wrappers BepInEx needs to bootstrap.
            ("Il2CppInterop.Generator.Passes.Pass81FillUnstrippedMethodBodies", "DoPass", 1),
        };

        foreach (var (typeName, methodName, paramCount) in generatorTargets)
        {
            var t = module.GetType(typeName);
            if (t == null) { Console.WriteLine($"WARN: type {typeName} not found"); continue; }
            var m = t.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.Parameters.Count == paramCount);
            if (m == null) { Console.WriteLine($"WARN: {typeName}.{methodName}({paramCount} params) not found"); continue; }
            Console.WriteLine($"Wrapping {typeName}.{m.Name}() -> {m.ReturnType.FullName}");
            WrapMethodInTryCatch(m);
        }

        asm.Write(dll);
        Console.WriteLine($"Patched: {dll}");
    }

    static void PatchCpp2IlCore(string dll)
    {
        if (!File.Exists(dll)) { Console.WriteLine($"WARN: {dll} not found, skipping"); return; }
        string bak = dll + ".unpatched";
        if (!File.Exists(bak)) { File.Copy(dll, bak); Console.WriteLine($"Backup created: {bak}"); }
        else { Console.WriteLine($"Restoring from backup: {bak}"); File.Copy(bak, dll, overwrite: true); }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dll));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(dll, rp);
        var module = asm.MainModule;

        var coreTargets = new (string TypeName, string MethodName, int ParamCount, int MaxDepth)[]
        {
            // Recursively-called helper that throws ArgumentOutOfRangeException when
            // genericTypeParameters[i] is out of range. Wrap with try-catch + depth
            // limit so an offending instantiation chain returns default instead of
            // propagating up through PopulateMethodsByAddressTable.
            ("Cpp2IL.Core.Utils.GenericInstantiation", "Instantiate",      3, 64),
            // ResolveIl2CppType is the choke point through which any Il2CppType -> context
            // resolution flows during PopulateMethodsByAddressTable. Catching exceptions
            // here lets one corrupted type entry not abort the whole analysis pass.
            // Recursive (calls itself for array element types etc.), so depth-limit it.
            ("Cpp2IL.Core.Utils.Il2CppTypeToContext", "ResolveIl2CppType", 2, 64),
            // ConcreteGenericMethodAnalysisContext is built per-generic-method during
            // PopulateMethodsByAddressTable. Wrapping ResolveDeclaringType lets one
            // bad entry be skipped instead of aborting the entire pass.
            ("Cpp2IL.Core.Model.Contexts.ConcreteGenericMethodAnalysisContext", "ResolveDeclaringType", 2, 0),
            // ToContext NREs when our LibCpp2IL wraps return null entries inside the
            // Il2CppTypeReflectionData[] array that ResolveTypeArray iterates over.
            // Wrapping it short-circuits null/bad reflection data to a null context.
            ("Cpp2IL.Core.Utils.Il2CppTypeReflectionDataToContext", "ToContext", 2, 0),
            // ResolveTypeArray throws "Unable to resolve generic parameter" when an
            // entry resolves to null. Wrap returns empty array so the ctor proceeds.
            ("Cpp2IL.Core.Model.Contexts.ConcreteGenericMethodAnalysisContext", "ResolveTypeArray", 2, 0),
            // Limbus metadata can contain malformed custom-attribute blobs
            // that make enum parameters point at non-enum types. The attribute pass
            // is nonessential for launching BepInEx; skip the bad owner's attributes
            // instead of aborting all interop generation before Il2Cppmscorlib exists.
            ("Cpp2IL.Core.Model.Contexts.HasCustomAttributes", "AnalyzeCustomAttributeDataV29", 0, 0),
            // The output pass can see corrupted type indices that were tolerated
            // during analysis but are absent from AsmResolverUtils' type-definition
            // cache. Missing hierarchy/generic wiring is better than aborting DLL
            // generation before any interop assemblies are written.
            (AsmResolverAssemblyPopulatorType, "ConfigureHierarchy", 1, 0),
            // Same idea one level later: if a type's field/method signature points
            // at invalid metadata, leave that managed stub type sparse and keep
            // generating the rest of the assembly set.
            (AsmResolverAssemblyPopulatorType, "CopyDataFromIl2CppToManaged", 1, 0),
            (AsmResolverAssemblyPopulatorType, "CopyIl2CppDataToManagedType", 2, 0),
            (AsmResolverAssemblyPopulatorType, "CopyFieldsInType", 3, 0),
            (AsmResolverAssemblyPopulatorType, "CopyMethodsInType", 3, 0),
            (AsmResolverAssemblyPopulatorType, "CopyPropertiesInType", 3, 0),
            (AsmResolverAssemblyPopulatorType, "CopyEventsInType", 3, 0),
            (AsmResolverAssemblyPopulatorType, "PopulateGenericParamsForType", 2, 0),
            (AsmResolverAssemblyPopulatorType, "AddExplicitInterfaceImplementations", 1, 0),
            (AsmResolverAssemblyPopulatorType, "AddExplicitInterfaceImplementations", 3, 0),
            (AsmResolverAssemblyPopulatorType, "PopulateCustomAttributes", 1, 0),
            (AsmResolverAssemblyPopulatorType, "CopyCustomAttributes", 2, 0),
        };

        foreach (var (typeName, methodName, paramCount, maxDepth) in coreTargets)
        {
            var t = module.GetType(typeName);
            if (t == null) { Console.WriteLine($"WARN: type {typeName} not found"); continue; }
            var m = t.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.Parameters.Count == paramCount);
            if (m == null) { Console.WriteLine($"WARN: {typeName}.{methodName}({paramCount} params) not found"); continue; }
            var ret = m.ReturnType.FullName;
            if (maxDepth > 0)
            {
                Console.WriteLine($"Wrapping (depth-limit={maxDepth}) {typeName}.{m.Name}() -> {ret}");
                WrapMethodWithDepthLimit(m, maxDepth);
            }
            else
            {
                Console.WriteLine($"Wrapping {typeName}.{m.Name}() -> {ret}");
                WrapMethodInTryCatch(m);
            }
        }

        ReplaceAsmResolverBuildAssembliesWithStubOnly(module);

        asm.Write(dll);
        Console.WriteLine($"Patched: {dll}");
    }

    static void ReplaceAsmResolverBuildAssembliesWithStubOnly(ModuleDefinition module)
    {
        var t = module.GetType("Cpp2IL.Core.OutputFormats.AsmResolverDllOutputFormat");
        if (t == null) { Console.WriteLine("WARN: AsmResolverDllOutputFormat not found"); return; }

        var buildAssemblies = t.Methods.FirstOrDefault(m => m.Name == "BuildAssemblies" && m.Parameters.Count == 1);
        var buildStubs = t.Methods.FirstOrDefault(m => m.Name == "BuildStubAssemblies" && m.Parameters.Count == 1);
        if (buildAssemblies == null || buildStubs == null)
        {
            Console.WriteLine("WARN: BuildAssemblies/BuildStubAssemblies not found");
            return;
        }

        Console.WriteLine("Replacing AsmResolverDllOutputFormat.BuildAssemblies() with stub-only output");

        var newBody = new MethodBody(buildAssemblies) { InitLocals = false };
        buildAssemblies.Body = newBody;
        var il = newBody.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Call, buildStubs));
        il.Append(il.Create(OpCodes.Ret));
        newBody.MaxStackSize = 2;
    }

    static void PatchLibCpp2IL(string dll)
    {
        string bak = dll + ".unpatched";
        if (!File.Exists(bak))
        {
            File.Copy(dll, bak, overwrite: false);
            Console.WriteLine($"Backup created: {bak}");
        }
        else
        {
            Console.WriteLine($"Backup already exists, restoring from it before patching: {bak}");
            File.Copy(bak, dll, overwrite: true);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dll));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };

        using var asm = AssemblyDefinition.ReadAssembly(dll, rp);
        var module = asm.MainModule;

        // Targets: methods that index unchecked into Cpp2IL data structures and throw
        // IndexOutOfRangeException on edge cases. We wrap each in a catch so the exception
        // is swallowed and the method returns its return-type's default value (0/null).
        // (type, method, param count, depth-limit). 0 depth-limit = plain try-catch
        // wrap. >0 = wrap also short-circuits when recursion depth on this thread
        // exceeds the limit (returns default(T) without invoking the inner body).
        // get_DeclaringType is the only one that needs depth limiting because it
        // forms a recursive chain via FullName.
        var targets = new (string TypeName, string MethodName, int ParamCount, int MaxDepth)[]
        {
            (Il2CppBinaryType,                            "GetGenericMethodFromIndex",   2, 0),
            (Il2CppBinaryType,                            "GetMethodPointer",            4, 0),
            (Il2CppBinaryType,                            "GetCodegenModuleByName",      1, 0),
            (Il2CppBinaryType,                            "GetCodegenModuleIndexByName", 1, 0),
            ("LibCpp2IL.Metadata.Il2CppMethodDefinition", "get_MethodPointer",           0, 0),
            ("LibCpp2IL.Metadata.Il2CppTypeDefinition",   "get_DeclaringType",           0, 0),
            // get_FullName recurses via DeclaringType.FullName — that's the actual
            // unbounded stack consumer when PM's metadata has a cycle. Cap it at 64.
            ("LibCpp2IL.Metadata.Il2CppTypeDefinition",   "get_FullName",                0, 64),
            // get_Class throws IndexOutOfRange when PM's binary has a Type whose
            // class index is out of range. Returning null lets AsClass throw the
            // catchable "Type is not a class" Exception that ResolveIl2CppType wrap
            // (in Cpp2IL.Core) absorbs.
            ("LibCpp2IL.BinaryStructures.Il2CppType",     "get_Class",                   0, 0),
            // Defense-in-depth around the generic-instantiation chain that
            // ConcreteGenericMethodAnalysisContext walks. Each method here can throw
            // IndexOutOfRange when PM's metadata indices are corrupt; wrapping all of
            // them lets a single bad entry be skipped instead of aborting the whole
            // PopulateMethodsByAddressTable pass.
            ("LibCpp2IL.LibCpp2ILUtils",                  "GetTypeReflectionData",       1, 0),
            ("LibCpp2IL.LibCpp2ILUtils",                  "GetGenericTypeParams",        1, 0),
            ("LibCpp2IL.BinaryStructures.Il2CppMethodSpec","get_GenericClassParams",     0, 0),
            ("LibCpp2IL.BinaryStructures.Il2CppMethodSpec","get_GenericMethodParams",    0, 0),
            ("LibCpp2IL.Cpp2IlMethodRef",                 "get_TypeGenericParams",       0, 0),
            ("LibCpp2IL.Cpp2IlMethodRef",                 "get_MethodGenericParams",     0, 0),
            ("LibCpp2IL.ClassReadingBinaryReader",         "ReadStringToNullNoLock",      1, 0),
            ("LibCpp2IL.Metadata.Il2CppMetadata",          "ReadStringFromIndexNoReadLock", 1, 0),
            ("LibCpp2IL.Metadata.Il2CppMetadata",          "GetStringFromIndex",          1, 0),
        };

        foreach (var (typeName, methodName, paramCount, maxDepth) in targets)
        {
            var t = module.GetType(typeName);
            if (t == null) { Console.WriteLine($"WARN: type {typeName} not found"); continue; }
            var m = t.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.Parameters.Count == paramCount);
            if (m == null) { Console.WriteLine($"WARN: {typeName}.{methodName}({paramCount} params) not found"); continue; }
            var ret = m.ReturnType.FullName;
            if (maxDepth > 0)
            {
                Console.WriteLine($"Wrapping (depth-limit={maxDepth}) {typeName}.{m.Name}() -> {ret}");
                WrapMethodWithDepthLimit(m, maxDepth);
            }
            else
            {
                Console.WriteLine($"Wrapping {typeName}.{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))}) -> {ret}");
                WrapMethodInTryCatch(m);
            }
        }

        var metadataLookupTargets = new (string MethodName, int ParamCount)[]
        {
            ("GetTypeDefinitionFromIndex", 1),
            ("GetMethodDefinitionFromIndex", 1),
            ("GetParameterDefinitionFromIndex", 1),
            ("GetFieldDefinitionFromIndex", 1),
            ("GetGenericContainerFromIndex", 1),
            ("GetGenericParameterFromIndex", 1),
            ("GetFieldDefaultValueFromIndex", 1),
            ("GetParameterDefaultValueFromIndex", 1),
        };

        var metadataType = module.GetType("LibCpp2IL.Metadata.Il2CppMetadata");
        if (metadataType == null)
        {
            Console.WriteLine("WARN: Il2CppMetadata not found");
        }
        else
        {
            foreach (var (methodName, paramCount) in metadataLookupTargets)
            {
                var m = metadataType.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.Parameters.Count == paramCount);
                if (m == null) { Console.WriteLine($"WARN: Il2CppMetadata.{methodName}({paramCount} params) not found"); continue; }
                Console.WriteLine($"Wrapping {metadataType.FullName}.{m.Name}() with constructed-object fallback");
                WrapMethodInTryCatchWithNewObjectFallback(m);
            }
        }

        // ----- Targeted body replacements: methods whose IL contains uncatchable
        // failures (Debug.Assert / FailFast) that no try-catch wrapper can stop.
        ReplaceGetGenericParameterDef(module);
        ReplaceRawArrayReader(module, "ReadClassArrayAtRawAddr", readableArray: false);
        ReplaceRawArrayReader(module, "ReadReadableArrayAtRawAddr", readableArray: true);
        ReplaceFillReadableArrayHereNoLock(module);

        // Save back
        asm.Write(dll);
        Console.WriteLine($"Patched: {dll}");
    }

    static void ReplaceRawArrayReader(ModuleDefinition module, string methodName, bool readableArray)
    {
        var t = module.GetType("LibCpp2IL.ClassReadingBinaryReader");
        if (t == null) { Console.WriteLine("WARN: ClassReadingBinaryReader not found"); return; }
        var m = t.Methods.FirstOrDefault(mm => mm.Name == methodName && mm.Parameters.Count == 2 && mm.HasGenericParameters);
        if (m == null) { Console.WriteLine($"WARN: {methodName} not found"); return; }

        Console.WriteLine($"Replacing body of {t.FullName}.{m.Name}<T>() with safe sparse-reader wrapper");

        var genericT = m.GenericParameters[0];
        var getLock = t.Methods.First(mm => mm.Name == "GetLockOrThrow" && mm.Parameters.Count == 0);
        var releaseLock = t.Methods.First(mm => mm.Name == "ReleaseLock" && mm.Parameters.Count == 0);
        var setPosition = t.Methods.First(mm => mm.Name == "set_Position" && mm.Parameters.Count == 1);
        var exceptionType = module.ImportReference(typeof(Exception));

        MethodReference readMethod;
        if (readableArray)
        {
            var fill = t.Methods.First(mm => mm.Name == "FillReadableArrayHereNoLock" && mm.Parameters.Count == 2);
            var gim = new GenericInstanceMethod(fill);
            gim.GenericArguments.Add(genericT);
            readMethod = gim;
        }
        else
        {
            var read = t.Methods.First(mm => mm.Name == "InternalReadClass" && mm.Parameters.Count == 1);
            var gim = new GenericInstanceMethod(read);
            gim.GenericArguments.Add(genericT);
            readMethod = gim;
        }

        var body = new MethodBody(m) { InitLocals = true };
        m.Body = body;
        var arrLocal = new VariableDefinition(new ArrayType(genericT));
        var iLocal = new VariableDefinition(module.TypeSystem.Int32);
        var retLocal = new VariableDefinition(new ArrayType(genericT));
        body.Variables.Add(arrLocal);
        body.Variables.Add(iLocal);
        body.Variables.Add(retLocal);
        var il = body.GetILProcessor();

        var outerTryStart = il.Create(OpCodes.Ldarg_2);
        var catchStart = il.Create(OpCodes.Pop);
        var returnStart = il.Create(OpCodes.Ldloc, retLocal);
        var innerTryStart = il.Create(OpCodes.Ldarg_0);
        var loopCheck = il.Create(OpCodes.Ldloc, iLocal);
        var loopBody = il.Create(OpCodes.Ldloc, arrLocal);
        var afterLoop = il.Create(OpCodes.Ldloc, arrLocal);
        var afterSeek = readableArray ? il.Create(OpCodes.Nop) : loopCheck;
        var finallyStart = il.Create(OpCodes.Ldarg_0);

        il.Append(outerTryStart);
        il.Append(il.Create(OpCodes.Conv_Ovf_I));
        il.Append(il.Create(OpCodes.Newarr, genericT));
        il.Append(il.Create(OpCodes.Stloc, arrLocal));

        il.Append(innerTryStart);
        il.Append(il.Create(OpCodes.Call, getLock));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldc_I4_M1));
        il.Append(il.Create(OpCodes.Conv_I8));
        il.Append(il.Create(OpCodes.Beq_S, afterSeek));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Call, setPosition));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, iLocal));

        if (readableArray)
        {
            il.Append(afterSeek);
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldloc, arrLocal));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Call, readMethod));
            il.Append(il.Create(OpCodes.Ldloc, arrLocal));
            il.Append(il.Create(OpCodes.Stloc, retLocal));
            il.Append(il.Create(OpCodes.Leave, returnStart));
        }
        else
        {
            il.Append(il.Create(OpCodes.Br_S, loopCheck));
            il.Append(loopBody);
            il.Append(il.Create(OpCodes.Ldloc, iLocal));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Call, readMethod));
            il.Append(il.Create(OpCodes.Stelem_Any, genericT));
            il.Append(il.Create(OpCodes.Ldloc, iLocal));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stloc, iLocal));
            il.Append(loopCheck);
            il.Append(il.Create(OpCodes.Conv_I8));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Blt_S, loopBody));
            il.Append(afterLoop);
            il.Append(il.Create(OpCodes.Stloc, retLocal));
            il.Append(il.Create(OpCodes.Leave, returnStart));
        }

        il.Append(finallyStart);
        il.Append(il.Create(OpCodes.Call, releaseLock));
        il.Append(il.Create(OpCodes.Endfinally));

        il.Append(catchStart);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Newarr, genericT));
        il.Append(il.Create(OpCodes.Stloc, retLocal));
        il.Append(il.Create(OpCodes.Leave, returnStart));

        il.Append(returnStart);
        il.Append(il.Create(OpCodes.Ret));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = innerTryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = catchStart,
        });
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = outerTryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = returnStart,
            CatchType = exceptionType,
        });
        body.MaxStackSize = 0;
    }

    static void ReplaceFillReadableArrayHereNoLock(ModuleDefinition module)
    {
        var t = module.GetType("LibCpp2IL.ClassReadingBinaryReader");
        if (t == null) { Console.WriteLine("WARN: ClassReadingBinaryReader not found"); return; }
        var m = t.Methods.FirstOrDefault(mm => mm.Name == "FillReadableArrayHereNoLock" && mm.Parameters.Count == 2 && mm.HasGenericParameters);
        if (m == null) { Console.WriteLine("WARN: FillReadableArrayHereNoLock not found"); return; }

        Console.WriteLine($"Replacing body of {t.FullName}.{m.Name}<T>() with safe per-entry reader");

        var genericT = m.GenericParameters[0];
        var internalRead = t.Methods.FirstOrDefault(mm => mm.Name == "InternalReadReadableClass" && mm.Parameters.Count == 0 && mm.HasGenericParameters);
        if (internalRead == null) { Console.WriteLine("WARN: InternalReadReadableClass<T>() not found"); return; }

        var gim = new GenericInstanceMethod(internalRead);
        gim.GenericArguments.Add(genericT);
        var activatorCreate = typeof(Activator).GetMethods()
            .First(mi => mi.Name == nameof(Activator.CreateInstance) && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 0);
        var createDefault = new GenericInstanceMethod(module.ImportReference(activatorCreate));
        createDefault.GenericArguments.Add(genericT);

        var body = new MethodBody(m) { InitLocals = true };
        m.Body = body;
        var iLocal = new VariableDefinition(module.TypeSystem.Int32);
        body.Variables.Add(iLocal);
        var il = body.GetILProcessor();

        var loopCheck = il.Create(OpCodes.Ldloc, iLocal);
        var loopBody = il.Create(OpCodes.Ldarg_1);
        var catchStart = il.Create(OpCodes.Pop);
        var increment = il.Create(OpCodes.Ldloc, iLocal);
        var ret = il.Create(OpCodes.Ret);

        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Stloc, iLocal));
        il.Append(il.Create(OpCodes.Br, loopCheck));

        il.Append(loopBody);
        il.Append(il.Create(OpCodes.Ldloc, iLocal));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, gim));
        il.Append(il.Create(OpCodes.Stelem_Any, genericT));
        il.Append(il.Create(OpCodes.Leave, increment));

        il.Append(catchStart);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldloc, iLocal));
        il.Append(il.Create(OpCodes.Call, createDefault));
        il.Append(il.Create(OpCodes.Stelem_Any, genericT));
        il.Append(il.Create(OpCodes.Leave, increment));

        il.Append(increment);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, iLocal));

        il.Append(loopCheck);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Blt, loopBody));

        il.Append(ret);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = loopBody,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = increment,
            CatchType = module.ImportReference(typeof(Exception)),
        });
        body.MaxStackSize = 0;
    }

    // Replace LibCpp2IL.BinaryStructures.Il2CppType.GetGenericParameterDef with
    // a body that keeps the null guard but skips the uncatchable Debug.Assert.
    static void ReplaceGetGenericParameterDef(ModuleDefinition module)
    {
        var t = module.GetType("LibCpp2IL.BinaryStructures.Il2CppType");
        if (t == null) { Console.WriteLine("WARN: Il2CppType not found"); return; }
        var m = t.Methods.FirstOrDefault(mm => mm.Name == "GetGenericParameterDef" && mm.Parameters.Count == 0);
        if (m == null) { Console.WriteLine("WARN: GetGenericParameterDef not found"); return; }
        var getGenericParam = t.Methods.FirstOrDefault(mm => mm.Name == "get_GenericParameter" && mm.Parameters.Count == 0);
        if (getGenericParam == null)
        {
            // Could also be a field — fall back
            var fieldGen = t.Fields.FirstOrDefault(f => f.Name == "GenericParameter");
            if (fieldGen == null) { Console.WriteLine("WARN: GenericParameter property/field not found"); return; }
            // Use field-access form
            ReplaceGenericParameterDef_WithFieldAccess(m, fieldGen, module);
            return;
        }

        Console.WriteLine($"Replacing body of {t.FullName}.{m.Name} (skip Debug.Assert)");

        var exceptionCtor = module.ImportReference(typeof(Exception).GetConstructor(new[] { typeof(string) }));

        var newBody = new MethodBody(m) { InitLocals = true };
        m.Body = newBody;
        var il = newBody.GetILProcessor();

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, getGenericParam));
        il.Append(il.Create(OpCodes.Dup));
        var notNull = il.Create(OpCodes.Ret);   // sentinel; we'll change to nop+ret pattern below
        var brtrue = il.Create(OpCodes.Brtrue, notNull);
        il.Append(brtrue);
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldstr, "Type is not a generic parameter"));
        il.Append(il.Create(OpCodes.Newobj, exceptionCtor));
        il.Append(il.Create(OpCodes.Throw));
        il.Append(notNull);

        newBody.MaxStackSize = 0;
    }

    static void ReplaceGenericParameterDef_WithFieldAccess(MethodDefinition m, FieldDefinition field, ModuleDefinition module)
    {
        Console.WriteLine($"Replacing body of {m.DeclaringType.FullName}.{m.Name} (field-access form)");
        var exceptionCtor = module.ImportReference(typeof(Exception).GetConstructor(new[] { typeof(string) }));
        var newBody = new MethodBody(m) { InitLocals = true };
        m.Body = newBody;
        var il = newBody.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, field));
        il.Append(il.Create(OpCodes.Dup));
        var notNull = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Brtrue, notNull));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldstr, "Type is not a generic parameter"));
        il.Append(il.Create(OpCodes.Newobj, exceptionCtor));
        il.Append(il.Create(OpCodes.Throw));
        il.Append(notNull);
        newBody.MaxStackSize = 0;
    }

    // Clone an instruction with whatever operand type it has.
    static Instruction CloneWithOperand(ILProcessor il, Instruction src)
    {
        switch (src.Operand)
        {
            case null:                     return il.Create(src.OpCode);
            case TypeReference t:          return il.Create(src.OpCode, t);
            case MethodReference m:        return il.Create(src.OpCode, m);
            case FieldReference f:         return il.Create(src.OpCode, f);
            case CallSite cs:              return il.Create(src.OpCode, cs);
            case ParameterDefinition p:    return il.Create(src.OpCode, p);
            case VariableDefinition v:     return il.Create(src.OpCode, v);
            case Instruction _:            return il.Create(src.OpCode, Instruction.Create(OpCodes.Nop));   // placeholder; fixup later
            case Instruction[] _:          return il.Create(src.OpCode, new Instruction[0]);                // placeholder
            case string s:                 return il.Create(src.OpCode, s);
            case sbyte sb:                 return il.Create(src.OpCode, sb);
            case byte b:                   return il.Create(src.OpCode, b);
            case int i:                    return il.Create(src.OpCode, i);
            case long l:                   return il.Create(src.OpCode, l);
            case float f:                  return il.Create(src.OpCode, f);
            case double d:                 return il.Create(src.OpCode, d);
            default:
                throw new InvalidOperationException($"Unhandled operand type: {src.Operand?.GetType().FullName}");
        }
    }

    // Move the original method's body into a new private "<name>__Inner" sibling
    // method on the same type. Returns the inner method. After this returns, the
    // original method has no body — the caller must build a new wrapper body.
    static MethodDefinition ExtractInnerMethod(MethodDefinition method)
    {
        var declaringType = method.DeclaringType;
        var innerMethod = CreateInnerMethodSignature(method);
        var oldBody = method.Body;
        var innerBody = new MethodBody(innerMethod) { InitLocals = oldBody.InitLocals };

        CopyVariables(oldBody, innerBody);
        var oldToNew = CloneInstructions(oldBody, innerBody);
        FixClonedInstructionOperands(oldBody, oldToNew);
        CopyExceptionHandlers(oldBody, innerBody, oldToNew);

        innerMethod.Body = innerBody;
        declaringType.Methods.Add(innerMethod);
        return innerMethod;
    }

    static MethodDefinition CreateInnerMethodSignature(MethodDefinition method)
    {
        var innerName = method.Name + "__Inner";
        var innerAttrs = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private;
        var innerMethod = new MethodDefinition(innerName, innerAttrs, method.ReturnType);
        foreach (var parameter in method.Parameters)
            innerMethod.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        foreach (var genericParameter in method.GenericParameters)
            CopyGenericParameter(innerMethod, genericParameter);

        return innerMethod;
    }

    static void CopyGenericParameter(MethodDefinition innerMethod, GenericParameter source)
    {
        var copy = new GenericParameter(source.Name, innerMethod);
        foreach (var constraint in source.Constraints)
            copy.Constraints.Add(constraint);
        innerMethod.GenericParameters.Add(copy);
    }

    static void CopyVariables(MethodBody source, MethodBody target)
    {
        foreach (var variable in source.Variables)
            target.Variables.Add(new VariableDefinition(variable.VariableType));
    }

    static Dictionary<Instruction, Instruction> CloneInstructions(MethodBody source, MethodBody target)
    {
        var il = target.GetILProcessor();
        var oldToNew = new Dictionary<Instruction, Instruction>();
        foreach (var instruction in source.Instructions)
        {
            var clone = instruction.Operand is null
                ? il.Create(instruction.OpCode)
                : CloneWithOperand(il, instruction);
            oldToNew[instruction] = clone;
            il.Append(clone);
        }

        return oldToNew;
    }

    static void FixClonedInstructionOperands(MethodBody source, Dictionary<Instruction, Instruction> oldToNew)
    {
        foreach (var instruction in source.Instructions)
        {
            var clone = oldToNew[instruction];
            if (instruction.Operand is Instruction target)
                clone.Operand = oldToNew[target];
            else if (instruction.Operand is Instruction[] targets)
                clone.Operand = targets.Select(target => oldToNew[target]).ToArray();
        }
    }

    static void CopyExceptionHandlers(
        MethodBody source,
        MethodBody target,
        IReadOnlyDictionary<Instruction, Instruction> oldToNew)
    {
        foreach (var handler in source.ExceptionHandlers)
        {
            target.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = handler.CatchType,
                FilterStart = Remap(handler.FilterStart, oldToNew),
                TryStart = Remap(handler.TryStart, oldToNew),
                TryEnd = Remap(handler.TryEnd, oldToNew),
                HandlerStart = Remap(handler.HandlerStart, oldToNew),
                HandlerEnd = Remap(handler.HandlerEnd, oldToNew),
            });
        }
    }

    static Instruction Remap(
        Instruction instruction,
        IReadOnlyDictionary<Instruction, Instruction> oldToNew)
    {
        return instruction != null ? oldToNew[instruction] : null;
    }

    // Emit ldarg.0..N appending to `il` to push `this` (if instance) and all parameters.
    // Returns the FIRST instruction emitted (so callers can use it as a TryStart anchor).
    static Instruction AppendArgLoads(ILProcessor il, MethodDefinition method)
    {
        Instruction first = null;
        if (method.HasThis)
        {
            first = il.Create(OpCodes.Ldarg_0);
            il.Append(first);
        }
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            var ins = il.Create(OpCodes.Ldarg, method.Parameters[i]);
            if (first == null) first = ins;
            il.Append(ins);
        }
        if (first == null)
        {
            first = il.Create(OpCodes.Nop);
            il.Append(first);
        }
        return first;
    }

    // Emit instructions to assign default(T) to retLocal in the catch path. For
    // array types we emit `ldc.i4.0; newarr <element>` (empty array) instead of
    // leaving the local at null, because callers commonly iterate the array with
    // .Length and would NRE on null. For everything else, the local already
    // holds default(T) due to InitLocals = true, so no work needed.
    static void EmitCatchDefaultValue(ILProcessor il, MethodDefinition method, VariableDefinition retLocal)
    {
        var retType = method.ReturnType;
        if (retType is ArrayType arr)
        {
            var elementType = arr.ElementType;
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Newarr, elementType));
            il.Append(il.Create(OpCodes.Stloc, retLocal));
        }
        else if (retType.FullName == "System.String")
        {
            il.Append(il.Create(OpCodes.Ldstr, string.Empty));
            il.Append(il.Create(OpCodes.Stloc, retLocal));
        }
        // else: retLocal already initialized to default(T) by the CLR (InitLocals=true).
    }

    static void WrapMethodInTryCatch(MethodDefinition method)
    {
        // Strategy: rename the original method to "<name>__Inner" and replace the
        // original method's body with a fresh try-catch wrapper that simply calls
        // the renamed inner method.
        //
        // This avoids ALL the pitfalls of mutating existing IL (branch targets,
        // multiple rets, stack-shape preservation across leave instructions). The
        // outer wrapper is tiny, mechanically correct, and the same shape regardless
        // of the inner method's complexity.
        // The generated body calls the inner method and falls back to the default
        // return value when the inner method throws.

        var module        = method.Module;
        var exceptionType = module.ImportReference(typeof(Exception));
        var innerMethod   = ExtractInnerMethod(method);

        var newBody = new MethodBody(method) { InitLocals = true };
        method.Body = newBody;
        var returnsVoid = method.ReturnType.MetadataType == MetadataType.Void;
        VariableDefinition retLocal = null;
        if (!returnsVoid)
        {
            retLocal = new VariableDefinition(method.ReturnType);
            newBody.Variables.Add(retLocal);
        }
        var il = newBody.GetILProcessor();

        var tryStart = AppendArgLoads(il, method);
        il.Append(il.Create(OpCodes.Call, innerMethod));
        if (!returnsVoid)
            il.Append(il.Create(OpCodes.Stloc, retLocal));

        var returnFinal = returnsVoid ? il.Create(OpCodes.Ret) : il.Create(OpCodes.Ldloc, retLocal);
        il.Append(il.Create(OpCodes.Leave, returnFinal));

        var catchPop = il.Create(OpCodes.Pop);
        il.Append(catchPop);
        // For array returns, store an empty array into retLocal (default null causes
        // NREs in callers that iterate with .Length).
        if (!returnsVoid)
            EmitCatchDefaultValue(il, method, retLocal);
        il.Append(il.Create(OpCodes.Leave, returnFinal));

        il.Append(returnFinal);
        if (!returnsVoid)
            il.Append(il.Create(OpCodes.Ret));

        newBody.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart     = tryStart,
            TryEnd       = catchPop,
            HandlerStart = catchPop,
            HandlerEnd   = returnFinal,
            CatchType    = exceptionType,
        });
        newBody.MaxStackSize = 0;
    }

    static void WrapMethodInTryCatchWithNewObjectFallback(MethodDefinition method)
    {
        var module = method.Module;
        var retType = method.ReturnType;
        var resolvedRet = retType.Resolve();
        var ctor = resolvedRet?.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        if (ctor == null)
        {
            Console.WriteLine($"WARN: {retType.FullName} has no parameterless constructor; using default fallback");
            WrapMethodInTryCatch(method);
            return;
        }

        var exceptionType = module.ImportReference(typeof(Exception));
        var ctorRef = module.ImportReference(ctor);
        var innerMethod = ExtractInnerMethod(method);

        var newBody = new MethodBody(method) { InitLocals = true };
        method.Body = newBody;
        var retLocal = new VariableDefinition(retType);
        newBody.Variables.Add(retLocal);
        var il = newBody.GetILProcessor();

        var tryStart = AppendArgLoads(il, method);
        il.Append(il.Create(OpCodes.Call, innerMethod));
        il.Append(il.Create(OpCodes.Stloc, retLocal));
        var returnFinal = il.Create(OpCodes.Ldloc, retLocal);
        il.Append(il.Create(OpCodes.Leave, returnFinal));

        var catchPop = il.Create(OpCodes.Pop);
        il.Append(catchPop);
        il.Append(il.Create(OpCodes.Newobj, ctorRef));
        il.Append(il.Create(OpCodes.Stloc, retLocal));
        il.Append(il.Create(OpCodes.Leave, returnFinal));

        il.Append(returnFinal);
        il.Append(il.Create(OpCodes.Ret));

        newBody.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = returnFinal,
            CatchType = exceptionType,
        });
        newBody.MaxStackSize = 0;
    }

    // Wrap with try/catch AND a thread-local recursion-depth guard. When
    // recursion depth on the current thread reaches `maxDepth`, the wrapper
    // short-circuits and returns default(T) without invoking __Inner — the
    // same behavior the original IndexOutOfRangeException accidentally produced
    // when it terminated the cycle. The depth field is added to the declaring
    // type as a [ThreadStatic] static int.
    static void WrapMethodWithDepthLimit(MethodDefinition method, int maxDepth)
    {
        var module        = method.Module;
        var declaringType = method.DeclaringType;
        var exceptionType = module.ImportReference(typeof(Exception));
        var innerMethod   = ExtractInnerMethod(method);

        // Find or create the [ThreadStatic] depth field on the declaring type.
        var depthFieldName = "__" + method.Name + "_DepthGuard";
        var depthField = declaringType.Fields.FirstOrDefault(f => f.Name == depthFieldName);
        if (depthField == null)
        {
            depthField = new FieldDefinition(
                depthFieldName,
                FieldAttributes.Private | FieldAttributes.Static,
                module.TypeSystem.Int32);
            var tsCtor = module.ImportReference(
                typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes));
            depthField.CustomAttributes.Add(new CustomAttribute(tsCtor));
            declaringType.Fields.Add(depthField);
        }

        var newBody = new MethodBody(method) { InitLocals = true };
        method.Body = newBody;
        var retLocal = new VariableDefinition(method.ReturnType);
        newBody.Variables.Add(retLocal);
        var il = newBody.GetILProcessor();

        // Trailing instructions are reused as branch targets.
        var ldlocFinal = il.Create(OpCodes.Ldloc, retLocal);
        var afterTry   = il.Create(OpCodes.Ldsfld, depthField);

        // Stop recursion at the configured depth and keep the default return value.
        il.Append(il.Create(OpCodes.Ldsfld, depthField));
        il.Append(il.Create(OpCodes.Ldc_I4, maxDepth));
        il.Append(il.Create(OpCodes.Bge, ldlocFinal));

        // Increase tracked recursion depth before calling the inner body.
        il.Append(il.Create(OpCodes.Ldsfld, depthField));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stsfld, depthField));

        // Begin the guarded inner call.
        var tryStart = AppendArgLoads(il, method);
        il.Append(il.Create(OpCodes.Call, innerMethod));
        il.Append(il.Create(OpCodes.Stloc, retLocal));
        il.Append(il.Create(OpCodes.Leave, afterTry));

        // Build the catch block and preserve the fallback return value.
        var catchPop = il.Create(OpCodes.Pop);
        il.Append(catchPop);
        EmitCatchDefaultValue(il, method, retLocal);
        il.Append(il.Create(OpCodes.Leave, afterTry));

        // Decrease tracked recursion depth after the guarded call.
        il.Append(afterTry);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Stsfld, depthField));

        // Append the final return.
        il.Append(ldlocFinal);
        il.Append(il.Create(OpCodes.Ret));

        newBody.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart     = tryStart,
            TryEnd       = catchPop,
            HandlerStart = catchPop,
            HandlerEnd   = afterTry,
            CatchType    = exceptionType,
        });
        newBody.MaxStackSize = 0;
    }
}
