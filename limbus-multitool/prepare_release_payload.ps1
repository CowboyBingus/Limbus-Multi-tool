param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$payload = Join-Path $PSScriptRoot 'payload'
if (Test-Path -LiteralPath $payload) {
    Remove-Item -LiteralPath $payload -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $payload 'bin\Release') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'scripts') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'native') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'tools\patch-libcpp\bin\Release\net6.0') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\reapply-limbus-fix.ps1') -Destination (Join-Path $payload 'scripts') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\rebuild-resources.ps1') -Destination (Join-Path $payload 'scripts') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'data\il2cpp-api-functions-unity6000-no-profiler.txt') -Destination (Join-Path $payload 'data') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'data\System.JsonExtensions.dll-resources.dat.template') -Destination (Join-Path $payload 'data') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'vendor\limbus-winhttp-shim\winhttp.dll') -Destination (Join-Path $payload 'native') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'vendor\limbus-winhttp-shim\doorstop.dll') -Destination (Join-Path $payload 'native') -Force

Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\LimbusCanvasFix\bin\Release\LimbusCanvasFix.dll') -Destination (Join-Path $payload 'bin\Release') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\LimbusWindowResizeFix\bin\Release\LimbusWindowResizeFix.dll') -Destination (Join-Path $payload 'bin\Release') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\LimbusFramePacingFix\bin\Release\LimbusFramePacingFix.dll') -Destination (Join-Path $payload 'bin\Release') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\LimbusRuntimeUIInspector\bin\Release\LimbusRuntimeUIInspector.dll') -Destination (Join-Path $payload 'bin\Release') -Force

$patcherDir = Join-Path $RepoRoot 'tools\patch-libcpp\bin\Release\net6.0'
Copy-Item -Path (Join-Path $patcherDir '*') -Destination (Join-Path $payload 'tools\patch-libcpp\bin\Release\net6.0') -Force

Write-Host "Prepared release payload: $payload"
