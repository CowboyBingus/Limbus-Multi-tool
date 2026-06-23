param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
    [string]$RepoDir = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild,
    [switch]$SkipCorePatch,
    [switch]$EnableInteropGeneration
)

$ErrorActionPreference = "Stop"

function Info([string]$Message) {
    Write-Verbose "[reapply] $Message"
}

function Require-File([string]$Path, [string]$Description = "file") {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required $Description not found: $Path"
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

function Get-ConfigLines([string]$ConfigPath) {
    if (Test-Path -LiteralPath $ConfigPath) {
        return @(Get-Content -LiteralPath $ConfigPath)
    }

    Info "BepInEx config not found; creating $ConfigPath"
    return @()
}

function Update-ConfigValueLine([string[]]$Lines, [string]$Name, [string]$Value) {
    $pattern = "^\s*$([regex]::Escape($Name))\s*=.*$"
    $updated = $false
    $result = New-Object System.Collections.Generic.List[string]

    foreach ($line in $Lines) {
        if ($line -match $pattern) {
            $result.Add("$Name = $Value")
            $updated = $true
        } else {
            $result.Add($line)
        }
    }

    return [pscustomobject]@{
        Lines = @($result.ToArray())
        Updated = $updated
    }
}

function Find-ConfigSectionIndex([string[]]$Lines, [string]$Section) {
    $sectionPattern = "^\s*\[$([regex]::Escape($Section))\]\s*$"
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $sectionPattern) {
            return $i
        }
    }

    return -1
}

function Add-ConfigSectionValue([string[]]$Lines, [string]$Section, [string]$Name, [string]$Value) {
    $result = @($Lines)
    if ($result.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($result[-1])) {
        $result += ""
    }

    $result += "[$Section]"
    $result += "$Name = $Value"
    return $result
}

function Insert-ConfigValueInSection([string[]]$Lines, [int]$SectionIndex, [string]$Name, [string]$Value) {
    $anySectionPattern = "^\s*\[[^\]]+\]\s*$"
    $insertIndex = $Lines.Count
    for ($i = $SectionIndex + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $anySectionPattern) {
            $insertIndex = $i
            break
        }
    }

    $before = if ($insertIndex -gt 0) { @($Lines[0..($insertIndex - 1)]) } else { @() }
    $after = if ($insertIndex -lt $Lines.Count) { @($Lines[$insertIndex..($Lines.Count - 1)]) } else { @() }
    return @($before + "$Name = $Value" + $after)
}

function Add-ConfigValueLine([string[]]$Lines, [string]$Section, [string]$Name, [string]$Value) {
    $sectionIndex = Find-ConfigSectionIndex $Lines $Section
    if ($sectionIndex -lt 0) {
        return Add-ConfigSectionValue $Lines $Section $Name $Value
    }

    return Insert-ConfigValueInSection $Lines $sectionIndex $Name $Value
}

function Set-ConfigValue([string]$ConfigPath, [string]$Section, [string]$Name, [string]$Value) {
    $configDir = Split-Path -Parent $ConfigPath
    if (-not (Test-Path -LiteralPath $configDir)) {
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }

    $update = Update-ConfigValueLine (Get-ConfigLines $ConfigPath) $Name $Value
    $lines = if ($update.Updated) {
        @($update.Lines)
    } else {
        Add-ConfigValueLine @($update.Lines) $Section $Name $Value
    }

    Set-Content -LiteralPath $ConfigPath -Value $lines
}

function Add-HashBytes([System.Security.Cryptography.HashAlgorithm]$Hash, [byte[]]$Bytes, [int]$Count = -1) {
    if ($Count -lt 0) {
        $Count = $Bytes.Length
    }
    if ($Count -le 0) {
        return
    }
    [void]$Hash.TransformBlock($Bytes, 0, $Count, $Bytes, 0)
}

function Add-HashFile([System.Security.Cryptography.HashAlgorithm]$Hash, [string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] 81920
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            Add-HashBytes $Hash $buffer $read
        }
    } finally {
        $stream.Dispose()
    }
}

function Add-HashString([System.Security.Cryptography.HashAlgorithm]$Hash, [string]$Value) {
    Add-HashBytes $Hash ([System.Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-BepInExInteropHash([string]$GameDir, [string]$CoreDir) {
    $hash = [System.Security.Cryptography.MD5]::Create()
    try {
        Add-HashFile $hash (Join-Path $GameDir "GameAssembly.dll")

        $unityLibs = Join-Path $GameDir "BepInEx\unity-libs"
        if (Test-Path -LiteralPath $unityLibs) {
            foreach ($path in [System.IO.Directory]::EnumerateFiles($unityLibs, "*.dll", [System.IO.SearchOption]::TopDirectoryOnly)) {
                Add-HashString $hash ([System.IO.Path]::GetFileName($path))
                Add-HashFile $hash $path
            }
        }

        $renameMap = Join-Path $GameDir "BepInEx\DeobfuscationMap.csv.gz"
        if (Test-Path -LiteralPath $renameMap) {
            Add-HashFile $hash $renameMap
        }

        Add-HashString $hash ([System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $CoreDir "Il2CppInterop.Generator.dll")).Version.ToString())
        Add-HashString $hash ([System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $CoreDir "Cpp2IL.Core.dll")).Version.ToString())
        [void]$hash.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return (($hash.Hash | ForEach-Object { $_.ToString("x2") }) -join "")
    } finally {
        $hash.Dispose()
    }
}

function Get-UnityVersion([string]$GameDir) {
    $globalGameManagers = Join-Path $GameDir "LimbusCompany_Data\globalgamemanagers"
    Require-File $globalGameManagers "Unity globalgamemanagers"
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($globalGameManagers))
    $match = [regex]::Match($text, '\d{4}\.\d+\.\d+[abfp]\d+')
    if (-not $match.Success) {
        throw "Could not determine Unity version from $globalGameManagers"
    }
    return $match.Value
}

function Ensure-UnityBaseLibraries([string]$GameDir) {
    $unityVersion = (Get-UnityVersion $GameDir) -replace '[a-z]\d+$', ''
    $unityLibs = Join-Path $GameDir "BepInEx\unity-libs"
    $zipPath = Join-Path $unityLibs "$unityVersion.zip"
    $coreModule = Join-Path $unityLibs "UnityEngine.CoreModule.dll"

    if (-not (Test-Path -LiteralPath $unityLibs)) {
        New-Item -ItemType Directory -Path $unityLibs -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $zipPath)) {
        $url = "https://unity.bepinex.dev/libraries/$unityVersion.zip"
        Info "Downloading Unity base libraries for $unityVersion"
        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing -TimeoutSec 120
    }

    if (-not (Test-Path -LiteralPath $coreModule)) {
        Info "Extracting Unity base libraries for $unityVersion"
        Expand-Archive -LiteralPath $zipPath -DestinationPath $unityLibs -Force
    }
}

function Copy-UnityBaseLibrariesToInterop([string]$GameDir) {
    $interopDir = Join-Path $GameDir "BepInEx\interop"
    if (-not (Test-Path -LiteralPath $interopDir)) {
        New-Item -ItemType Directory -Path $interopDir -Force | Out-Null
    }

    $unityLibs = Join-Path $GameDir "BepInEx\unity-libs"
    $copied = 0
    foreach ($path in [System.IO.Directory]::EnumerateFiles($unityLibs, "*.dll", [System.IO.SearchOption]::TopDirectoryOnly)) {
        Copy-Item -LiteralPath $path -Destination (Join-Path $interopDir ([System.IO.Path]::GetFileName($path))) -Force
        $copied++
    }
    Info "Copied $copied Unity base librar$(if ($copied -eq 1) { 'y' } else { 'ies' }) into BepInEx\interop"
}

function Write-BepInExInteropSkipHash([string]$GameDir, [string]$CoreDir) {
    $interopDir = Join-Path $GameDir "BepInEx\interop"
    if (-not (Test-Path -LiteralPath $interopDir)) {
        New-Item -ItemType Directory -Path $interopDir -Force | Out-Null
    }
    $hashPath = Join-Path $interopDir "assembly-hash.txt"
    $hash = Get-BepInExInteropHash $GameDir $CoreDir
    [System.IO.File]::WriteAllText($hashPath, $hash)
    Info "Wrote BepInEx interop hash marker to skip Cpp2IL generation"
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
    if (-not (Test-Path -LiteralPath $bak)) {
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

function Test-RuntimeInvokeLookupMethod($Type, $Method) {
    if (-not $Method.HasBody) {
        return $false
    }

    if ($Type.FullName -ne "BepInEx.Unity.IL2CPP.IL2CPPChainloader" -or
        $Method.Name -ne "Initialize") {
        return $false
    }

    return [bool]($Method.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
        [string]$_.Operand -eq "Runtime invoke pointer: 0x"
    })
}

function Set-RuntimeInvokeStrings($Method, [string]$Target) {
    $changed = 0
    foreach ($instruction in $Method.Body.Instructions) {
        if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldstr) { continue }

        $operand = [string]$instruction.Operand
        if ($operand -eq "il2cpp_runtime_invoke" -or
            $operand -match '^[A-Za-z_][A-Za-z0-9_]{10}$') {
            $instruction.Operand = $Target
            $changed++
        }
    }

    return $changed
}

function Patch-BepInExUnityRuntimeInvoke([string]$Path, [hashtable]$Map) {
    Require-File $Path
    if (-not $Map.ContainsKey("il2cpp_runtime_invoke")) {
        throw "IL2CPP symbol map is missing il2cpp_runtime_invoke"
    }

    Info "Patching BepInEx.Unity.IL2CPP runtime_invoke lookup"
    $bak = "$Path.reapply-runtime-invoke.bak"
    if (-not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak
    }

    $target = [string]$Map["il2cpp_runtime_invoke"]

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, (New-CecilReaderParams $Path))
    $changed = 0
    foreach ($type in $ad.MainModule.Types) {
        foreach ($method in $type.Methods) {
            if (Test-RuntimeInvokeLookupMethod $type $method) {
                $changed += Set-RuntimeInvokeStrings $method $target
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
    if (-not (Test-Path -LiteralPath $Path)) {
        Info "Il2Cppmscorlib.dll is missing; launch once to generate interop, then rerun this script."
        return
    }

    Info "Patching Il2Cppmscorlib wrappers"
    $bak = "$Path.reapply.bak"
    if (-not (Test-Path -LiteralPath $bak)) {
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

    if (-not ($gc.Methods | Where-Object { $_.Name -eq "ReRegisterForFinalize" -and $_.Parameters.Count -eq 1 })) {
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

function Add-AssemblyReferenceIfMissing($Module, [string]$Name) {
    if (-not ($Module.AssemblyReferences | Where-Object { $_.Name -eq $Name })) {
        $Module.AssemblyReferences.Add((New-Object Mono.Cecil.AssemblyNameReference($Name, [Version]"0.0.0.0")))
    }

    return $Module.AssemblyReferences | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
}

function Set-CanvasScaleModeEnum($Module) {
    $scaleMode = $Module.GetType("UnityEngine.UI.CanvasScaler/ScaleMode")
    if ($null -eq $scaleMode) {
        return
    }

    $mscorlib = $Module.AssemblyReferences | Where-Object { $_.Name -eq "mscorlib" } | Select-Object -First 1
    $scaleMode.BaseType = New-Object Mono.Cecil.TypeReference("System", "Enum", $Module, $mscorlib)
    $scaleMode.Attributes = $scaleMode.Attributes -bor [Mono.Cecil.TypeAttributes]::Sealed
    if (-not ($scaleMode.Fields | Where-Object { $_.Name -eq "value__" })) {
        $scaleMode.Fields.Insert(0, (New-Object Mono.Cecil.FieldDefinition(
            "value__",
            ([Mono.Cecil.FieldAttributes]::Public -bor [Mono.Cecil.FieldAttributes]::SpecialName -bor [Mono.Cecil.FieldAttributes]::RTSpecialName),
            $Module.TypeSystem.Int32)))
    }

    foreach ($pair in @(@("ConstantPixelSize", 0), @("ScaleWithScreenSize", 1), @("ConstantPhysicalSize", 2))) {
        Set-ScaleModeLiteralField $scaleMode ([string]$pair[0]) ([int]$pair[1])
    }
}

function Set-ScaleModeLiteralField($ScaleMode, [string]$Name, [int]$Constant) {
    $field = $ScaleMode.Fields | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $field) {
        $field = New-Object Mono.Cecil.FieldDefinition(
            $Name,
            ([Mono.Cecil.FieldAttributes](
                [int][Mono.Cecil.FieldAttributes]::Public -bor
                [int][Mono.Cecil.FieldAttributes]::Static -bor
                [int][Mono.Cecil.FieldAttributes]::Literal -bor
                [int][Mono.Cecil.FieldAttributes]::HasDefault
            )),
            $ScaleMode)
        $ScaleMode.Fields.Add($field)
    }

    $field.Constant = $Constant
}

function Ensure-CanvasOnEnableMethod($Module, $Canvas) {
    $method = $Canvas.Methods |
        Where-Object { $_.Name -eq "OnEnable" -and $_.Parameters.Count -eq 0 } |
        Select-Object -First 1
    if ($null -ne $method) {
        return $method
    }

    $method = New-Object Mono.Cecil.MethodDefinition(
        "OnEnable",
        ([Mono.Cecil.MethodAttributes](
            [int][Mono.Cecil.MethodAttributes]::Public -bor
            [int][Mono.Cecil.MethodAttributes]::HideBySig
        )),
        $Module.TypeSystem.Void)
    $body = New-Object Mono.Cecil.Cil.MethodBody($method)
    $method.Body = $body
    $il = $body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $Canvas.Methods.Add($method)
    return $method
}

function Ensure-NativeOnEnableField($Module, $Canvas) {
    $field = $Canvas.Fields |
        Where-Object { $_.Name -eq "NativeMethodInfoPtr_OnEnable" } |
        Select-Object -First 1
    if ($null -ne $field) {
        return $field
    }

    $field = New-Object Mono.Cecil.FieldDefinition(
        "NativeMethodInfoPtr_OnEnable",
        ([Mono.Cecil.FieldAttributes]::Private -bor [Mono.Cecil.FieldAttributes]::Static -bor [Mono.Cecil.FieldAttributes]::InitOnly),
        $Module.TypeSystem.IntPtr)
    $Canvas.Fields.Add($field)
    return $field
}

function Get-Il2CppGetMethodReference($Module) {
    $runtimePath = Join-Path $GameDir "BepInEx\core\Il2CppInterop.Runtime.dll"
    Require-File $runtimePath
    $runtime = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($runtimePath)
    $il2cpp = $runtime.MainModule.GetType("Il2CppInterop.Runtime.IL2CPP")
    $getMethod = $il2cpp.Methods |
        Where-Object { $_.Name -eq "GetIl2CppMethod" -and $_.Parameters.Count -eq 5 } |
        Select-Object -First 1
    if ($null -eq $getMethod) {
        throw "Il2CppInterop.Runtime.IL2CPP is missing GetIl2CppMethod"
    }

    return [Mono.Cecil.MethodReference]$Module.ImportReference($getMethod)
}

function Get-NativeClassFieldFromClassInitializer($ClassInitializer) {
    $instruction = $ClassInitializer.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld } |
        Select-Object -First 1
    if ($null -eq $instruction) {
        throw "UnityEngine.UI.CanvasScaler .cctor is missing its native class field load"
    }

    return [Mono.Cecil.FieldReference]$instruction.Operand
}

function Add-OnEnableLookupToClassInitializer($Module, $Canvas, $NativeMethodField, $GetMethodRef) {
    $cctor = $Canvas.Methods | Where-Object { $_.Name -eq ".cctor" } | Select-Object -First 1
    if ($null -eq $cctor) {
        throw "UnityEngine.UI.CanvasScaler is missing .cctor"
    }

    if ($cctor.Body.Instructions | Where-Object { $_.Operand -eq $NativeMethodField }) {
        return
    }

    $nativeClassField = Get-NativeClassFieldFromClassInitializer $cctor
    $ret = $cctor.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret } |
        Select-Object -Last 1
    if ($null -eq $ret) {
        throw "UnityEngine.UI.CanvasScaler .cctor is missing a return instruction"
    }

    $il = $cctor.Body.GetILProcessor()
    $instructions = @(
        $il.Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, $nativeClassField),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "OnEnable"),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "System.Void"),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Newarr, $Module.TypeSystem.String),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $GetMethodRef),
        $il.Create([Mono.Cecil.Cil.OpCodes]::Stsfld, [Mono.Cecil.FieldReference]$NativeMethodField)
    )
    foreach ($instruction in $instructions) {
        $il.InsertBefore($ret, $instruction)
    }
}

function Set-CanvasOnEnableBody($Module, $Canvas, $NativeMethodField) {
    $onEnable = Ensure-CanvasOnEnableMethod $Module $Canvas
    $body = New-Object Mono.Cecil.Cil.MethodBody($onEnable)
    $onEnable.Body = $body
    $il = $body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, [Mono.Cecil.FieldReference]$NativeMethodField))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Pop))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
}

function Patch-UnityEngineUI([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Info "UnityEngine.UI.dll is missing; launch once to generate interop, then rerun this script."
        return
    }

    Info "Patching UnityEngine.UI CanvasScaler wrapper"
    $bak = "$Path.reapply.bak"
    if (-not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak
    }

    $ad = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, (New-CecilReaderParams $Path))
    $module = $ad.MainModule
    $canvas = $module.GetType("UnityEngine.UI.CanvasScaler")
    if ($null -eq $canvas) {
        throw "UnityEngine.UI.dll is missing UnityEngine.UI.CanvasScaler"
    }

    Add-AssemblyReferenceIfMissing $module "UnityEngine.CoreModule" | Out-Null
    Set-CanvasScaleModeEnum $module
    $nativeMethodField = Ensure-NativeOnEnableField $module $canvas
    $getMethodRef = Get-Il2CppGetMethodReference $module
    Add-OnEnableLookupToClassInitializer $module $canvas $nativeMethodField $getMethodRef
    Set-CanvasOnEnableBody $module $canvas $nativeMethodField

    $ad.Write($Path)
}

function Test-InteropReady([string]$Path) {
    $required = @(
        "Il2Cppmscorlib.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.dll",
        "UnityEngine.UI.dll",
        "UnityEngine.UIModule.dll"
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name))) {
            return $false
        }
    }

    return $true
}

if ([string]::IsNullOrWhiteSpace($GameDir) -or $GameDir.TrimStart().StartsWith("-")) {
    throw "GameDir was not parsed correctly: '$GameDir'. Run the installer backend from version 1.5 or newer."
}

$coreDir = Join-Path $GameDir "BepInEx\core"
$interopDir = Join-Path $GameDir "BepInEx\interop"
$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
$configPath = Join-Path $GameDir "BepInEx\config\BepInEx.cfg"
$interopReady = Test-InteropReady $interopDir

Require-File (Join-Path $coreDir "Mono.Cecil.dll") "BepInEx core Mono.Cecil.dll"
Add-Type -Path (Join-Path $coreDir "Mono.Cecil.dll")

if (-not $SkipBuild) {
    Info "Building patcher"
    dotnet build (Join-Path $RepoDir "tools\patch-libcpp\patch-libcpp.csproj") -c Release

    Info "Building LimbusCanvasFix"
    dotnet build (Join-Path $RepoDir "src\LimbusCanvasFix\LimbusCanvasFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusWindowResizeFix"
    dotnet build (Join-Path $RepoDir "src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusFramePacingFix"
    dotnet build (Join-Path $RepoDir "src\LimbusFramePacingFix\LimbusFramePacingFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusHdrBalanceFix"
    dotnet build (Join-Path $RepoDir "src\LimbusHdrBalanceFix\LimbusHdrBalanceFix.csproj") -c Release /p:SkipDeploy=true

    Info "Building LimbusRuntimeUIInspector"
    dotnet build (Join-Path $RepoDir "src\LimbusRuntimeUIInspector\LimbusRuntimeUIInspector.csproj") -c Release /p:SkipDeploy=true
}

if (-not $SkipCorePatch) {
    $patcher = Join-Path $RepoDir "tools\patch-libcpp\bin\Release\net6.0\PatchLibCpp.exe"
    Require-File $patcher
    Info "Patching BepInEx core/C++ interop tools"
    & $patcher $coreDir

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
Set-ConfigValue $configPath "Caching" "EnableAssemblyCache" "false"
Set-ConfigValue $configPath "Logging" "UnityLogListening" "false"
Set-ConfigValue $configPath "IL2CPP" "PreloadIL2CPPInteropAssemblies" "false"
if ($EnableInteropGeneration -and -not $interopReady) {
    Set-ConfigValue $configPath "IL2CPP" "GlobalMetadataPath" "{GameDataPath}/il2cpp_data/Metadata/limbus-generated-metadata.dat"
    Set-ConfigValue $configPath "IL2CPP" "UpdateInteropAssemblies" "true"
    Info "Enabled BepInEx interop generation for the next launch"
} else {
    Set-ConfigValue $configPath "IL2CPP" "GlobalMetadataPath" "{GameDataPath}/il2cpp_data/Resources/System.JsonExtensions.dll-resources.dat"
    Set-ConfigValue $configPath "IL2CPP" "UpdateInteropAssemblies" "false"
    Ensure-UnityBaseLibraries $GameDir
    Copy-UnityBaseLibrariesToInterop $GameDir
    Write-BepInExInteropSkipHash $GameDir $coreDir
}

if (-not (Test-Path -LiteralPath $pluginsDir)) {
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

$hdrBalancePluginDll = Resolve-FirstExistingPath @(
    (Join-Path $RepoDir "src\LimbusHdrBalanceFix\bin\Release\LimbusHdrBalanceFix.dll"),
    (Join-Path $RepoDir "bin\Release\LimbusHdrBalanceFix.dll")
) "LimbusHdrBalanceFix.dll"
Info "Deploying LimbusHdrBalanceFix.dll"
Copy-Item -LiteralPath $hdrBalancePluginDll -Destination (Join-Path $pluginsDir "LimbusHdrBalanceFix.dll") -Force

$inspectorPluginDll = Resolve-FirstExistingPath @(
    (Join-Path $RepoDir "src\LimbusRuntimeUIInspector\bin\Release\LimbusRuntimeUIInspector.dll"),
    (Join-Path $RepoDir "bin\Release\LimbusRuntimeUIInspector.dll")
) "LimbusRuntimeUIInspector.dll"
Info "Deploying LimbusRuntimeUIInspector.dll"
Copy-Item -LiteralPath $inspectorPluginDll -Destination (Join-Path $pluginsDir "LimbusRuntimeUIInspector.dll") -Force

if ($interopReady) {
    Info "Reducing interop folder to known working load set"
    $disabledDir = Join-Path $GameDir "interop_disabled"
    if (-not (Test-Path -LiteralPath $disabledDir)) {
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
    Info "Generated game interop DLL set is not present; skipping legacy wrapper patching."
}

Info "Reapply complete"
