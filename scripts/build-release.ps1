param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd-HHmm'),
    [switch]$SkipInstallerBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installerDir = Join-Path $repoRoot 'limbus-multitool'
$distDir = Join-Path $installerDir 'dist\Limbus Multi-tool'
$artifactDir = Join-Path $repoRoot 'artifacts'
$zipPath = Join-Path $artifactDir "Limbus-Multi-tool-$Version-win-x64.zip"

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipInstallerBuild) {
    Invoke-Checked 'Building installer' { & (Join-Path $installerDir 'build_exe.ps1') -Version $Version }
}

if (-not (Test-Path -LiteralPath $distDir)) {
    throw "Installer dist folder not found: $distDir"
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath -Force

Write-Host "Built release artifact: $zipPath"
