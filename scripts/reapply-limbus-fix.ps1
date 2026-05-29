param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
    [string]$RepoDir = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild,
    [switch]$SkipCorePatch
)

$ErrorActionPreference = "Stop"

function Info([string]$Message) {
    Write-Host "[reapply] $Message"
}

function Require-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) {
        throw "Required file not found: $Path"
    }
}

function Resolve-FirstExistingPath([string[]]$Paths, [string]$Description) {
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    throw "$Description not found. Checked: $($Paths -join ', ')"
}

function Set-ConfigValue([string]$ConfigPath, [string]$Name, [string]$Value) {
    Require-File $ConfigPath
    $lines = Get-Content -LiteralPath $ConfigPath
    $pattern = "^\s*$([regex]::Escape($Name))\s*=.*$"
    $updated = $false
    $lines = $lines | ForEach-Object {
        if ($_ -match $pattern) {
            $updated = $true
            "$Name = $Value"
        } else {
            $_
        }
    }
    if (!$updated) {
        throw "Config key not found in ${ConfigPath}: $Name"
    }
    Set-Content -LiteralPath $ConfigPath -Value $lines
}

function New-CecilReaderParams([string]$AssemblyPath) {
    $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
    $resolver.AddSearchDirectory((Split-Path -Parent $AssemblyPath))
    $resolver.AddSearchDirectory((Join-Path $GameDir "BepInEx\core"))
    $resolver.AddSearchDirectory((Join-Path $GameDir "BepInEx\interop"))
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $resolver
    $rp.InMemory = $true
    return $rp
}

function Get-Il2CppSymbolMap([string]$ResourcePath) {
    Require-File $ResourcePath
    $bytes = [System.IO.File]::ReadAllBytes($ResourcePath)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $tableStart = $ascii.IndexOf("il2cpp_init=")
    if ($tableStart -lt 0) {
        throw "IL2CPP resource map is missing il2cpp_init=: $ResourcePath"
    }

    $map = @{}
    foreach ($line in $ascii.Substring($tableStart).Split("`n")) {
        if ($line -notmatch '^(il2cpp_[A-Za-z0-9_]+)=([A-Za-z_][A-Za-z0-9_]{10})') {
            continue
        }
        $map[$matches[1]] = $matches[2]
    }
    return $map
}

function Patch-Il2CppRuntimeSymbols([string]$RuntimePath, [hashtable]$Map) {
    Require-File $RuntimePath
    Info "Patching Il2CppInterop.Runtime P/Invoke symbols"

    $bak = "$RuntimePath.reapply-symbol-map.bak"
    if (!(Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $RuntimePath -Destination $bak
    }

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($RuntimePath, (New-CecilReaderParams $RuntimePath))
    $il2cpp = $ad.MainModule.GetType("Il2CppInterop.Runtime.IL2CPP")
    if ($null -eq $il2cpp) {
        throw "Il2CppInterop.Runtime.dll is missing Il2CppInterop.Runtime.IL2CPP"
    }

    $patched = 0
    foreach ($method in $il2cpp.Methods) {
        if ($null -ne $method.PInvokeInfo -and $Map.ContainsKey($method.Name)) {
            $method.PInvokeInfo.EntryPoint = [string]$Map[$method.Name]
            $patched++
        }
    }

    $ad.Write($RuntimePath)
    Info "Patched $patched Il2CppInterop.Runtime P/Invoke entrypoints"
}

function Patch-BepInExUnityRuntimeInvoke([string]$Path, [hashtable]$Map) {
    Require-File $Path
    if (!$Map.ContainsKey("il2cpp_runtime_invoke")) {
        throw "IL2CPP symbol map is missing il2cpp_runtime_invoke"
    }

    Info "Patching BepInEx.Unity.IL2CPP runtime_invoke lookup"
    $bak = "$Path.reapply-runtime-invoke.bak"
    if (!(Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak
    }

    $target = [string]$Map["il2cpp_runtime_invoke"]

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, (New-CecilReaderParams $Path))
    $changed = 0
    foreach ($type in $ad.MainModule.Types) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            $isRuntimeInvokeLookup = $type.FullName -eq "BepInEx.Unity.IL2CPP.IL2CPPChainloader" -and
                $method.Name -eq "Initialize" -and
                ($method.Body.Instructions | Where-Object {
                    $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
                    [string]$_.Operand -eq "Runtime invoke pointer: 0x"
                })
            if (!$isRuntimeInvokeLookup) { continue }

            foreach ($instruction in $method.Body.Instructions) {
                if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldstr) { continue }
                $operand = [string]$instruction.Operand
                if ($operand -eq "il2cpp_runtime_invoke" -or
                    $operand -match '^[A-Za-z_][A-Za-z0-9_]{10}$') {
                    $instruction.Operand = $target
                    $changed++
                }
            }
        }
    }
    if ($changed -gt 0) {
        $ad.Write($Path)
    }
    Info "Patched $changed BepInEx.Unity.IL2CPP runtime_invoke string reference(s)"
}

function Add-TypeEqualityOperators($Module, $Type) {
    $attrs = [Mono.Cecil.MethodAttributes](
        [int][Mono.Cecil.MethodAttributes]::Public -bor
        [int][Mono.Cecil.MethodAttributes]::Static -bor
        [int][Mono.Cecil.MethodAttributes]::HideBySig -bor
        [int][Mono.Cecil.MethodAttributes]::SpecialName
    )

    foreach ($spec in @(@("op_Equality", $false), @("op_Inequality", $true))) {
        $name = [string]$spec[0]
        $invert = [bool]$spec[1]
        if ($Type.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 2 }) {
            continue
        }

        $method = New-Object Mono.Cecil.MethodDefinition($name, $attrs, $Module.TypeSystem.Boolean)
        $method.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition("left", [Mono.Cecil.ParameterAttributes]::None, $Type)))
        $method.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition("right", [Mono.Cecil.ParameterAttributes]::None, $Type)))
        $body = New-Object Mono.Cecil.Cil.MethodBody($method)
        $method.Body = $body
        $il = $body.GetILProcessor()
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ceq))
        if ($invert) {
            $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0))
            $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ceq))
        }
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
        $Type.Methods.Add($method)
    }
}

function Patch-Il2CppMscorlib([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) {
        Info "Il2Cppmscorlib.dll is missing; launch once to generate interop, then rerun this script."
        return
    }

    Info "Patching Il2Cppmscorlib wrappers"
    $bak = "$Path.reapply.bak"
    if (!(Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak
    }

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, (New-CecilReaderParams $Path))
    $module = $ad.MainModule
    $type = $module.GetType("Il2CppSystem.Type")
    $gc = $module.GetType("Il2CppSystem.GC")
    $obj = $module.GetType("Il2CppSystem.Object")
    if ($null -eq $type -or $null -eq $gc -or $null -eq $obj) {
        throw "Il2Cppmscorlib.dll is missing Il2CppSystem.Type, Il2CppSystem.GC, or Il2CppSystem.Object"
    }

    Add-TypeEqualityOperators $module $type

    $runtimePath = Join-Path $GameDir "BepInEx\core\Il2CppInterop.Runtime.dll"
    Require-File $runtimePath
    $runtime = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($runtimePath)
    $il2cpp = $runtime.MainModule.GetType("Il2CppInterop.Runtime.IL2CPP")
    $typeGetObject = $il2cpp.Methods |
        Where-Object { $_.Name -eq "il2cpp_type_get_object" -and $_.Parameters.Count -eq 1 } |
        Select-Object -First 1
    $typeGetObjectRef = [Mono.Cecil.MethodReference]$module.ImportReference($typeGetObject)

    $internalFromHandle = $type.Methods |
        Where-Object { $_.Name -eq "internal_from_handle" -and $_.Parameters.Count -eq 1 } |
        Select-Object -First 1
    if ($null -eq $internalFromHandle) {
        $attrs = [Mono.Cecil.MethodAttributes](
            [int][Mono.Cecil.MethodAttributes]::Public -bor
            [int][Mono.Cecil.MethodAttributes]::Static -bor
            [int][Mono.Cecil.MethodAttributes]::HideBySig
        )
        $internalFromHandle = New-Object Mono.Cecil.MethodDefinition("internal_from_handle", $attrs, $type)
        $internalFromHandle.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition("handle", [Mono.Cecil.ParameterAttributes]::None, $module.TypeSystem.IntPtr)))
        $ctor = $type.Methods |
            Where-Object { $_.Name -eq ".ctor" -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq "System.IntPtr" } |
            Select-Object -First 1
        $body = New-Object Mono.Cecil.Cil.MethodBody($internalFromHandle)
        $internalFromHandle.Body = $body
        $il = $body.GetILProcessor()
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $typeGetObjectRef))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Newobj, [Mono.Cecil.MethodReference]$module.ImportReference($ctor)))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
        $type.Methods.Add($internalFromHandle)
    } else {
        $newAttrs = ([int]$internalFromHandle.Attributes -band (-bnot [int][Mono.Cecil.MethodAttributes]::MemberAccessMask)) -bor
            [int][Mono.Cecil.MethodAttributes]::Public
        $internalFromHandle.Attributes = [Mono.Cecil.MethodAttributes]$newAttrs
    }

    if (!($gc.Methods | Where-Object { $_.Name -eq "ReRegisterForFinalize" -and $_.Parameters.Count -eq 1 })) {
        $attrs = [Mono.Cecil.MethodAttributes](
            [int][Mono.Cecil.MethodAttributes]::Public -bor
            [int][Mono.Cecil.MethodAttributes]::Static -bor
            [int][Mono.Cecil.MethodAttributes]::HideBySig
        )
        $method = New-Object Mono.Cecil.MethodDefinition("ReRegisterForFinalize", $attrs, $module.TypeSystem.Void)
        $method.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition("obj", [Mono.Cecil.ParameterAttributes]::None, $obj)))
        $body = New-Object Mono.Cecil.Cil.MethodBody($method)
        $method.Body = $body
        $il = $body.GetILProcessor()
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
        $gc.Methods.Add($method)
    }

    $ad.Write($Path)
}

function Patch-UnityEngineUI([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) {
        Info "UnityEngine.UI.dll is missing; launch once to generate interop, then rerun this script."
        return
    }

    Info "Patching UnityEngine.UI CanvasScaler wrapper"
    $bak = "$Path.reapply.bak"
    if (!(Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak
    }

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, (New-CecilReaderParams $Path))
    $module = $ad.MainModule
    $canvas = $module.GetType("UnityEngine.UI.CanvasScaler")
    if ($null -eq $canvas) {
        throw "UnityEngine.UI.dll is missing UnityEngine.UI.CanvasScaler"
    }

    if (!($module.AssemblyReferences | Where-Object { $_.Name -eq "UnityEngine.CoreModule" })) {
        $module.AssemblyReferences.Add((New-Object Mono.Cecil.AssemblyNameReference("UnityEngine.CoreModule", [Version]"0.0.0.0")))
    }
    $coreRef = $module.AssemblyReferences | Where-Object { $_.Name -eq "UnityEngine.CoreModule" } | Select-Object -First 1
    $vec2 = New-Object Mono.Cecil.TypeReference("UnityEngine", "Vector2", $module, $coreRef)

    $scaleMode = $module.GetType("UnityEngine.UI.CanvasScaler/ScaleMode")
    if ($null -ne $scaleMode) {
        $mscorlib = $module.AssemblyReferences | Where-Object { $_.Name -eq "mscorlib" } | Select-Object -First 1
        $scaleMode.BaseType = New-Object Mono.Cecil.TypeReference("System", "Enum", $module, $mscorlib)
        $scaleMode.Attributes = $scaleMode.Attributes -bor [Mono.Cecil.TypeAttributes]::Sealed
        if (!($scaleMode.Fields | Where-Object { $_.Name -eq "value__" })) {
            $scaleMode.Fields.Insert(0, (New-Object Mono.Cecil.FieldDefinition(
                "value__",
                ([Mono.Cecil.FieldAttributes]::Public -bor [Mono.Cecil.FieldAttributes]::SpecialName -bor [Mono.Cecil.FieldAttributes]::RTSpecialName),
                $module.TypeSystem.Int32)))
        }
        foreach ($pair in @(@("ConstantPixelSize", 0), @("ScaleWithScreenSize", 1), @("ConstantPhysicalSize", 2))) {
            $name = [string]$pair[0]
            $constant = [int]$pair[1]
            $field = $scaleMode.Fields | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if ($null -eq $field) {
                $field = New-Object Mono.Cecil.FieldDefinition(
                    $name,
                    ([Mono.Cecil.FieldAttributes](
                        [int][Mono.Cecil.FieldAttributes]::Public -bor
                        [int][Mono.Cecil.FieldAttributes]::Static -bor
                        [int][Mono.Cecil.FieldAttributes]::Literal -bor
                        [int][Mono.Cecil.FieldAttributes]::HasDefault
                    )),
                    $scaleMode)
                $scaleMode.Fields.Add($field)
            }
            $field.Constant = $constant
        }
    }

    if (!($canvas.Methods | Where-Object { $_.Name -eq "OnEnable" -and $_.Parameters.Count -eq 0 })) {
        $method = New-Object Mono.Cecil.MethodDefinition(
            "OnEnable",
            ([Mono.Cecil.MethodAttributes](
                [int][Mono.Cecil.MethodAttributes]::Public -bor
                [int][Mono.Cecil.MethodAttributes]::HideBySig
            )),
            $module.TypeSystem.Void)
        $body = New-Object Mono.Cecil.Cil.MethodBody($method)
        $method.Body = $body
        $il = $body.GetILProcessor()
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
        $canvas.Methods.Add($method)
    }

    $nativeMethodField = $canvas.Fields | Where-Object { $_.Name -eq "NativeMethodInfoPtr_OnEnable" } | Select-Object -First 1
    if ($null -eq $nativeMethodField) {
        $nativeMethodField = New-Object Mono.Cecil.FieldDefinition(
            "NativeMethodInfoPtr_OnEnable",
            ([Mono.Cecil.FieldAttributes]::Private -bor [Mono.Cecil.FieldAttributes]::Static -bor [Mono.Cecil.FieldAttributes]::InitOnly),
            $module.TypeSystem.IntPtr)
        $canvas.Fields.Add($nativeMethodField)
    }

    $runtimePath = Join-Path $GameDir "BepInEx\core\Il2CppInterop.Runtime.dll"
    Require-File $runtimePath
    $runtime = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($runtimePath)
    $il2cpp = $runtime.MainModule.GetType("Il2CppInterop.Runtime.IL2CPP")
    $getMethod = $il2cpp.Methods |
        Where-Object { $_.Name -eq "GetIl2CppMethod" -and $_.Parameters.Count -eq 5 } |
        Select-Object -First 1
    $getMethodRef = [Mono.Cecil.MethodReference]$module.ImportReference($getMethod)

    $cctor = $canvas.Methods | Where-Object { $_.Name -eq ".cctor" } | Select-Object -First 1
    if ($null -eq $cctor) {
        throw "UnityEngine.UI.CanvasScaler is missing .cctor"
    }
    $nativeClassField = [Mono.Cecil.FieldReference](($cctor.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld } |
        Select-Object -First 1).Operand)
    $ret = $cctor.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret } |
        Select-Object -Last 1
    $il = $cctor.Body.GetILProcessor()
    if (!($cctor.Body.Instructions | Where-Object { $_.Operand -eq $nativeMethodField })) {
        $instructions = @(
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, $nativeClassField),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "OnEnable"),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "System.Void"),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Newarr, $module.TypeSystem.String),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $getMethodRef),
            $il.Create([Mono.Cecil.Cil.OpCodes]::Stsfld, [Mono.Cecil.FieldReference]$nativeMethodField)
        )
        foreach ($instruction in $instructions) {
            $il.InsertBefore($ret, $instruction)
        }
    }

    $onEnable = $canvas.Methods |
        Where-Object { $_.Name -eq "OnEnable" -and $_.Parameters.Count -eq 0 } |
        Select-Object -First 1
    $body = New-Object Mono.Cecil.Cil.MethodBody($onEnable)
    $onEnable.Body = $body
    $il = $body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, [Mono.Cecil.FieldReference]$nativeMethodField))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Pop))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))

    $ad.Write($Path)
}

$coreDir = Join-Path $GameDir "BepInEx\core"
$interopDir = Join-Path $GameDir "BepInEx\interop"
$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
$configPath = Join-Path $GameDir "BepInEx\config\BepInEx.cfg"

Require-File (Join-Path $coreDir "Mono.Cecil.dll")
Add-Type -Path (Join-Path $coreDir "Mono.Cecil.dll")

if (!$SkipBuild) {
    Info "Building patcher"
    dotnet build (Join-Path $RepoDir "tools\patch-libcpp\patch-libcpp.csproj") -c Release

    Info "Building LimbusCanvasFix"
    dotnet build (Join-Path $RepoDir "src\LimbusCanvasFix\LimbusCanvasFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusWindowResizeFix"
    dotnet build (Join-Path $RepoDir "src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusFramePacingFix"
    dotnet build (Join-Path $RepoDir "src\LimbusFramePacingFix\LimbusFramePacingFix.csproj") -c Release /p:SkipDeploy=true
}

if (!$SkipCorePatch) {
    $patcher = Join-Path $RepoDir "tools\patch-libcpp\bin\Release\net6.0\PatchLibCpp.exe"
    Require-File $patcher
    Info "Patching BepInEx core/C++ interop tools"
    & $patcher

    $rebuildResources = Join-Path $RepoDir "scripts\rebuild-resources.ps1"
    if (Test-Path -LiteralPath $rebuildResources) {
        Info "Rebuilding IL2CPP symbol resource map"
        & $rebuildResources -GameDir $GameDir
    }
}

$resourceMap = Join-Path $GameDir "LimbusCompany_Data\il2cpp_data\Resources\System.JsonExtensions.dll-resources.dat"
$il2cppMap = Get-Il2CppSymbolMap $resourceMap
Patch-Il2CppRuntimeSymbols (Join-Path $coreDir "Il2CppInterop.Runtime.dll") $il2cppMap
Patch-BepInExUnityRuntimeInvoke (Join-Path $coreDir "BepInEx.Unity.IL2CPP.dll") $il2cppMap

Info "Applying BepInEx config"
Set-ConfigValue $configPath "EnableAssemblyCache" "false"
Set-ConfigValue $configPath "UnityLogListening" "false"
Set-ConfigValue $configPath "PreloadIL2CPPInteropAssemblies" "false"

if (!(Test-Path -LiteralPath $pluginsDir)) {
    New-Item -ItemType Directory -Path $pluginsDir | Out-Null
}

$pluginDll = Resolve-FirstExistingPath @(
    (Join-Path $RepoDir "src\LimbusCanvasFix\bin\Release\LimbusCanvasFix.dll"),
    (Join-Path $RepoDir "bin\Release\LimbusCanvasFix.dll")
) "LimbusCanvasFix.dll"
Info "Deploying LimbusCanvasFix.dll"
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $pluginsDir "LimbusCanvasFix.dll") -Force

$resizePluginDll = Resolve-FirstExistingPath @(
    (Join-Path $RepoDir "src\LimbusWindowResizeFix\bin\Release\LimbusWindowResizeFix.dll"),
    (Join-Path $RepoDir "bin\Release\LimbusWindowResizeFix.dll")
) "LimbusWindowResizeFix.dll"
Info "Deploying LimbusWindowResizeFix.dll"
Copy-Item -LiteralPath $resizePluginDll -Destination (Join-Path $pluginsDir "LimbusWindowResizeFix.dll") -Force

$framePacingPluginDll = Resolve-FirstExistingPath @(
    (Join-Path $RepoDir "src\LimbusFramePacingFix\bin\Release\LimbusFramePacingFix.dll"),
    (Join-Path $RepoDir "bin\Release\LimbusFramePacingFix.dll")
) "LimbusFramePacingFix.dll"
Info "Deploying LimbusFramePacingFix.dll"
Copy-Item -LiteralPath $framePacingPluginDll -Destination (Join-Path $pluginsDir "LimbusFramePacingFix.dll") -Force

if (Test-Path -LiteralPath $interopDir) {
    Info "Reducing interop folder to known working load set"
    $disabledDir = Join-Path $GameDir "interop_disabled"
    if (!(Test-Path -LiteralPath $disabledDir)) {
        New-Item -ItemType Directory -Path $disabledDir | Out-Null
    }

    $keep = @(
        "assembly-hash.txt",
        "Il2Cppmscorlib.dll",
        "Il2CppSystem.Core.dll",
        "Il2CppSystem.dll",
        "Il2CppSystem.JsonExtensions.dll",
        "JsonExtensions.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.dll",
        "UnityEngine.UI.dll",
        "UnityEngine.UIModule.dll"
    )

    Get-ChildItem -LiteralPath $interopDir -File -Filter "*.dll" | ForEach-Object {
        if ($keep -notcontains $_.Name) {
            Move-Item -LiteralPath $_.FullName -Destination (Join-Path $disabledDir $_.Name) -Force
        }
    }

    $unityLibs = Join-Path $GameDir "BepInEx\unity-libs"
    foreach ($name in @("UnityEngine.CoreModule.dll", "UnityEngine.dll", "UnityEngine.UIModule.dll")) {
        $src = Join-Path $unityLibs $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $interopDir $name) -Force
        }
    }

    try {
        Patch-Il2CppMscorlib (Join-Path $interopDir "Il2Cppmscorlib.dll")
    } catch {
        Write-Error $_.ScriptStackTrace
        throw
    }
    Patch-UnityEngineUI (Join-Path $interopDir "UnityEngine.UI.dll")
} else {
    Info "BepInEx\interop does not exist yet. Launch once to generate interop, then rerun this script."
}

Info "Reapply complete"
