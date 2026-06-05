param(
    [Parameter(Mandatory=$true)]
    [string]$Version,

    [string]$ArtifactPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path "artifacts\Limbus-Multi-tool-$Version-win-x64.zip"),

    [switch]$Draft,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'

if (!(Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI not found. Install gh from https://cli.github.com/ and run 'gh auth login'."
}

if (!(Test-Path -LiteralPath $ArtifactPath)) {
    throw "Release artifact not found: $ArtifactPath"
}

$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

git tag $tag 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tag $tag already exists locally."
}

Invoke-Checked "Pushing tag $tag" { git push origin $tag }

$ghArgs = @(
    'release', 'create', $tag,
    $ArtifactPath,
    '--title', "Limbus Multi-tool $tag",
    '--notes', "Windows x64 release of Limbus Multi-tool."
)

if ($Draft) { $ghArgs += '--draft' }
if ($Prerelease) { $ghArgs += '--prerelease' }

Invoke-Checked "Creating GitHub release $tag" { & gh @ghArgs }
