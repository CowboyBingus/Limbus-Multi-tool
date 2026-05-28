param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('install','verify','uninstall','launch','audit')]
    [string]$Action,

    [string]$GameDir,
    [string]$PayloadRoot = (Join-Path $PSScriptRoot 'payload'),
    [string]$Plugins = 'canvas,resize'
)

$ErrorActionPreference = 'Stop'
$BepInExBuildPage = 'https://builds.bepinex.dev/projects/bepinex_be'
$BepInExFallbackUrl = 'https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip'

function Info([string]$Message) {
    Write-Output "[info] $Message"
}

function Warn([string]$Message) {
    Write-Output "[warn] $Message"
}

function Fail([string]$Message) {
    Write-Output "[error] $Message"
    throw $Message
}

function Require-File([string]$Path, [string]$Description) {
    if (!(Test-Path -LiteralPath $Path)) {
        Fail "$Description not found: $Path"
    }
}

function Require-GameFiles([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Fail "Game directory is required."
    }
    Require-File (Join-Path $Path 'LimbusCompany.exe') 'LimbusCompany.exe'
    Require-File (Join-Path $Path 'GameAssembly.dll') 'GameAssembly.dll'
    Require-File (Join-Path $Path 'UnityPlayer.dll') 'UnityPlayer.dll'
}

function Require-GameDir([string]$Path) {
    Require-GameFiles $Path
    Require-File (Join-Path $Path 'BepInEx\core\Mono.Cecil.dll') 'BepInEx core'
}

function Get-PluginDir([string]$Path) {
    $dir = Join-Path $Path 'BepInEx\plugins'
    if (!(Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    return $dir
}

function Stop-GameIfRunning {
    $processes = Get-Process -Name LimbusCompany -ErrorAction SilentlyContinue
    if ($processes) {
        Info "Stopping running LimbusCompany process so plugin DLLs and core files can be updated."
        Stop-Process -InputObject $processes -Force
        Start-Sleep -Seconds 2
    }
}

function Test-BepInExInstalled([string]$Path) {
    return (
        (Test-Path -LiteralPath (Join-Path $Path 'BepInEx\core\BepInEx.Core.dll')) -and
        (Test-Path -LiteralPath (Join-Path $Path 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')) -and
        (Test-Path -LiteralPath (Join-Path $Path 'doorstop_config.ini')) -and
        (Test-Path -LiteralPath (Join-Path $Path 'winhttp.dll'))
    )
}

function Get-BepInExDownloadUrl {
    try {
        Info "Checking official BepInEx build page for Unity IL2CPP Windows x64 package."
        $response = Invoke-WebRequest -Uri $BepInExBuildPage -UseBasicParsing -TimeoutSec 30
        $match = [regex]::Match($response.Content, 'href="([^"]*BepInEx-Unity\.IL2CPP-win-x64-[^"]*\.zip)"')
        if ($match.Success) {
            $href = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
            return ([Uri]::new([Uri]$BepInExBuildPage, $href)).AbsoluteUri
        }
    } catch {
        Warn "Could not inspect BepInEx build page: $($_.Exception.Message)"
    }

    Warn "Using bundled fallback BepInEx download URL."
    return $BepInExFallbackUrl
}

function Ensure-BepInEx {
    Require-GameFiles $GameDir
    if (Test-BepInExInstalled $GameDir) {
        Info "BepInEx Unity IL2CPP installation found."
        return
    }

    Stop-GameIfRunning
    $downloadUrl = Get-BepInExDownloadUrl | Select-Object -Last 1
    $workDir = Join-Path ([IO.Path]::GetTempPath()) ('limbus-bepinex-' + [guid]::NewGuid().ToString('N'))
    $zipPath = Join-Path $workDir 'bepinex.zip'
    $extractDir = Join-Path $workDir 'extract'

    New-Item -ItemType Directory -Path $workDir,$extractDir -Force | Out-Null
    try {
        Info "Downloading BepInEx Unity IL2CPP Windows x64 package."
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 120
        Info "Installing BepInEx into game folder."
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
        Copy-Item -Path (Join-Path $extractDir '*') -Destination $GameDir -Recurse -Force
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (!(Test-BepInExInstalled $GameDir)) {
        Fail "BepInEx install did not produce the expected Unity IL2CPP files."
    }
    Info "BepInEx install complete."
}

function Get-SelectedPlugins {
    $selected = @()
    foreach ($raw in ($Plugins -split ',')) {
        $name = $raw.Trim().ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -notin @('canvas','resize')) {
            Fail "Unknown plugin selection: $name"
        }
        if ($name -notin $selected) {
            $selected += $name
        }
    }
    if ($selected.Count -eq 0) {
        Fail "Select at least one plugin to install."
    }
    return $selected
}

function Sync-SelectedPlugins([string[]]$Selected) {
    $pluginDir = Get-PluginDir $GameDir
    $pluginMap = @{
        canvas = 'LimbusCanvasFix.dll'
        resize = 'LimbusWindowResizeFix.dll'
    }

    foreach ($key in @('canvas','resize')) {
        $dllName = $pluginMap[$key]
        $path = Join-Path $pluginDir $dllName
        if ($Selected -contains $key) {
            Info "Installed $dllName."
        } elseif (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            Info "Removed unselected plugin $dllName."
        }
    }
}

function Invoke-Install {
    Require-GameFiles $GameDir
    Require-File (Join-Path $PayloadRoot 'scripts\reapply-limbus-fix.ps1') 'reapply script'
    Require-File (Join-Path $PayloadRoot 'scripts\rebuild-resources.ps1') 'resource rebuild script'
    Require-File (Join-Path $PayloadRoot 'data\il2cpp-api-functions-unity6000-no-profiler.txt') 'bundled IL2CPP API list'
    Require-File (Join-Path $PayloadRoot 'bin\Release\LimbusCanvasFix.dll') 'LimbusCanvasFix payload'
    Require-File (Join-Path $PayloadRoot 'bin\Release\LimbusWindowResizeFix.dll') 'LimbusWindowResizeFix payload'

    $selected = Get-SelectedPlugins
    Ensure-BepInEx
    Stop-GameIfRunning
    Info "Applying core compatibility patch and deploying plugins."
    & (Join-Path $PayloadRoot 'scripts\reapply-limbus-fix.ps1') -GameDir $GameDir -RepoDir $PayloadRoot -SkipBuild
    if ($LASTEXITCODE) {
        Fail "Install script failed with exit code $LASTEXITCODE."
    }

    Sync-SelectedPlugins -Selected $selected
    Info "Install/reapply complete."
}

function Invoke-Verify {
    Require-GameDir $GameDir
    $pluginDir = Get-PluginDir $GameDir
    Require-File (Join-Path $pluginDir 'LimbusCanvasFix.dll') 'LimbusCanvasFix plugin'
    Require-File (Join-Path $pluginDir 'LimbusWindowResizeFix.dll') 'LimbusWindowResizeFix plugin'

    $logPath = Join-Path $GameDir 'BepInEx\LogOutput.log'
    Require-File $logPath 'BepInEx log'
    $log = Get-Content -LiteralPath $logPath -Raw

    if ($log -match 'Applied CanvasScaler ultrawide fix') {
        Info "Ultrawide CanvasScaler fix verified in log."
    } else {
        Warn "Ultrawide fix marker not found yet. Launch the game and wait for the main UI."
    }

    if ($log -match 'Enabled resizing for HWND' -or $log -match 'Window resize fix active') {
        Info "Window resize fix verified in log."
    } else {
        Warn "Window resize marker not found yet. Launch the game and wait for the window to appear."
    }

    $errors = Join-Path $GameDir 'BepInEx\ErrorLog.log'
    if ((Test-Path -LiteralPath $errors) -and (Get-Item -LiteralPath $errors).Length -gt 0) {
        Warn "BepInEx ErrorLog.log is not empty. Inspect it if the game did not start correctly."
    }
}

function Invoke-Uninstall {
    Require-GameDir $GameDir
    Stop-GameIfRunning
    $pluginDir = Get-PluginDir $GameDir
    foreach ($name in @('LimbusCanvasFix.dll','LimbusWindowResizeFix.dll')) {
        $path = Join-Path $pluginDir $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            Info "Removed $name."
        }
    }
    Info "Plugin uninstall complete. Core compatibility backups were left in place."
}

function Invoke-Launch {
    Require-GameFiles $GameDir
    Info "Launching Limbus Company."
    Start-Process -FilePath (Join-Path $GameDir 'LimbusCompany.exe') -WorkingDirectory $GameDir
}

function Invoke-Audit {
    Require-File $PayloadRoot 'payload root'
    $blockedTerms = @(
        [string]::Concat([char[]]@(108,101,116,104,101)),
        [string]::Concat([char[]]@(108,101,116,104,105,110,101,120))
    )
    $blockedNamePattern = '(?i)(' + (($blockedTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')'

    $matches = Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Force -File |
        Where-Object { $_.Name -match $blockedNamePattern }
    if ($matches) {
        foreach ($m in $matches) { Warn "Disallowed filename: $($m.FullName)" }
        Fail "Payload contains disallowed filenames."
    }

    $textExtensions = @('.ps1','.psm1','.psd1','.cs','.csproj','.txt','.md','.json','.xml','.config','.cfg')
    $textMatches = Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Force -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in $textExtensions } |
        Select-String -Pattern $blockedTerms -CaseSensitive:$false -SimpleMatch -ErrorAction SilentlyContinue
    if ($textMatches) {
        foreach ($m in $textMatches) { Warn "Disallowed text: $($m.Path):$($m.LineNumber)" }
        Fail "Payload contains disallowed text."
    }
    Info "Payload audit passed."
}

switch ($Action) {
    'install' { Invoke-Install }
    'verify' { Invoke-Verify }
    'uninstall' { Invoke-Uninstall }
    'launch' { Invoke-Launch }
    'audit' { Invoke-Audit }
}
