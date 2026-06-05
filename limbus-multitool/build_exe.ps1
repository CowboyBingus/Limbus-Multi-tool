param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd-HHmm')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $PSScriptRoot '.venv'
$versionFile = Join-Path $PSScriptRoot 'app_version.txt'

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (!(Test-Path -LiteralPath $venv)) {
    Invoke-Checked 'Creating Python virtual environment' { py -3.10 -m venv $venv }
}

Invoke-Checked 'Upgrading pip' { & (Join-Path $venv 'Scripts\python.exe') -m pip install --upgrade pip }
Invoke-Checked 'Installing Python requirements' { & (Join-Path $venv 'Scripts\pip.exe') install -r (Join-Path $PSScriptRoot 'requirements.txt') }

Invoke-Checked 'Building LimbusCanvasFix' { dotnet build (Join-Path $root 'src\LimbusCanvasFix\LimbusCanvasFix.csproj') -c Release -p:SkipDeploy=true }
Invoke-Checked 'Building LimbusWindowResizeFix' { dotnet build (Join-Path $root 'src\LimbusWindowResizeFix\LimbusWindowResizeFix.csproj') -c Release -p:SkipDeploy=true }
Invoke-Checked 'Building LimbusFramePacingFix' { dotnet build (Join-Path $root 'src\LimbusFramePacingFix\LimbusFramePacingFix.csproj') -c Release -p:SkipDeploy=true }
Invoke-Checked 'Building PatchLibCpp' { dotnet build (Join-Path $root 'tools\patch-libcpp\patch-libcpp.csproj') -c Release }
& (Join-Path $PSScriptRoot 'prepare_release_payload.ps1') -RepoRoot $root
Set-Content -LiteralPath $versionFile -Value $Version -Encoding UTF8

$sep = ';'
$buildPath = Join-Path $PSScriptRoot 'build'
$distPath = Join-Path $PSScriptRoot 'dist'
Invoke-Checked 'Building PyInstaller app' { & (Join-Path $venv 'Scripts\pyinstaller.exe') `
    --noconfirm `
    --clean `
    --windowed `
    --name "Limbus Multi-tool" `
    --workpath $buildPath `
    --distpath $distPath `
    --specpath $PSScriptRoot `
    --add-data "$(Join-Path $PSScriptRoot 'backend.ps1')${sep}." `
    --add-data "$versionFile${sep}." `
    --add-data "$(Join-Path $PSScriptRoot 'payload')${sep}payload" `
    --add-data "$(Join-Path $PSScriptRoot 'assets')${sep}assets" `
    (Join-Path $PSScriptRoot 'limbus_installer.py') }

Write-Host "Built installer at: $(Join-Path $PSScriptRoot 'dist\Limbus Multi-tool')"
