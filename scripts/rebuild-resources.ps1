# Rebuilds LimbusCompany_Data\il2cpp_data\Resources\System.JsonExtensions.dll-resources.dat
# by zipping Unity's canonical IL2CPP API names with the obfuscated export names
# referenced by UnityPlayer.dll. Unity 6000 no longer has raw string-file order
# matching API order, so the obfuscated names are ordered by their RIP-relative
# code references in UnityPlayer's .text section.
#
# Run this once after Steam Verify has updated GameAssembly.dll/UnityPlayer.dll.

param(
  [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Limbus Company',
  [string]$CanonicalApiList = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data\il2cpp-api-functions-unity6000-no-profiler.txt')
)

$ErrorActionPreference = 'Stop'

$base = $GameDir
$res  = "$base\LimbusCompany_Data\il2cpp_data\Resources\System.JsonExtensions.dll-resources.dat"
$bak  = "$base\LimbusCompany_Data\il2cpp_data\Resources\System.JsonExtensions.dll-resources.dat.resource-head-bak"
$up   = "$base\UnityPlayer.dll"
$ga   = "$base\GameAssembly.dll"
$unityHeader = 'C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Data\il2cpp\libil2cpp\il2cpp-api-functions.h'

if (-not (Test-Path $up))  { throw "UnityPlayer.dll not found: $up" }
if (-not (Test-Path $ga))  { throw "GameAssembly.dll not found: $ga" }
if (-not (Test-Path $res)) { throw "Resources file not found: $res" }

# Make a backup of the live file if one doesn't already exist (idempotent)
if (-not (Test-Path $bak)) {
  Copy-Item -LiteralPath $res -Destination $bak -Force
  "Created backup: $bak"
}

# 1) Read canonical names in Unity IL2CPP API order. Profiler-only entries are
# not requested from GameAssembly in this player build, so exclude that block.
$rb = [System.IO.File]::ReadAllBytes($res)
$rascii = [System.Text.Encoding]::ASCII.GetString($rb)
$tableStart = $rascii.IndexOf("il2cpp_init=")
if ($tableStart -lt 0) { throw "Backup file does not contain the il2cpp_init= mapping table." }
if (Test-Path -LiteralPath $CanonicalApiList) {
  $canonical = [System.IO.File]::ReadAllLines($CanonicalApiList) |
    Where-Object { $_ -match '^il2cpp_[A-Za-z0-9_]+$' }
  "Canonical names from bundled IL2CPP API list: $($canonical.Count)"
} elseif (Test-Path -LiteralPath $unityHeader) {
  $canonical = New-Object System.Collections.ArrayList
  $inProfiler = $false
  foreach ($line in [System.IO.File]::ReadLines($unityHeader)) {
    if ($line -match '#if\s+IL2CPP_ENABLE_PROFILER') { $inProfiler = $true; continue }
    if ($inProfiler -and $line -match '#endif') { $inProfiler = $false; continue }
    if ($inProfiler) { continue }
    if ($line -match 'DO_API(?:_NO_RETURN)?\([^,]+,\s*(il2cpp_[A-Za-z0-9_]+)') {
      [void]$canonical.Add($matches[1])
    }
  }
  "Canonical names from Unity IL2CPP header: $($canonical.Count)"
} else {
  $tableText = $rascii.Substring($tableStart)
  $lines = $tableText.Split("`n") | Where-Object { $_ -match '^il2cpp_[A-Za-z0-9_]+=' }
  $canonical = $lines | ForEach-Object { ($_ -split '=')[0] }
  "Canonical names from existing mapping table: $($canonical.Count)"
}

# 2) Obfuscated names in UnityPlayer code-reference order, filtered to current GA exports
$ub = [System.IO.File]::ReadAllBytes($up)
$uascii = [System.Text.Encoding]::ASCII.GetString($ub)

# Parse GameAssembly.dll exports
$gb = [System.IO.File]::ReadAllBytes($ga)
$peOff = [BitConverter]::ToInt32($gb, 0x3C)
$optOff = $peOff + 24
$is64 = [BitConverter]::ToUInt16($gb, $optOff) -eq 0x20B
$dataDirOff = $optOff + ($(if ($is64){112}else{96}))
$expRva = [BitConverter]::ToInt32($gb, $dataDirOff)
$numSec = [BitConverter]::ToUInt16($gb, $peOff + 6)
$secStart = $optOff + [BitConverter]::ToUInt16($gb, $peOff + 20)
function R2O([byte[]]$bb,$rva,$ns,$ss) {
  for ($i=0; $i -lt $ns; $i++) {
    $va  = [BitConverter]::ToInt32($bb, $ss + $i*40 + 12)
    $sz  = [BitConverter]::ToInt32($bb, $ss + $i*40 + 8)
    $raw = [BitConverter]::ToInt32($bb, $ss + $i*40 + 20)
    if ($rva -ge $va -and $rva -lt $va + $sz) { return $rva - $va + $raw }
  }; -1
}
function O2R($off,$sections) {
  foreach ($s in $sections) {
    if ($off -ge $s.Raw -and $off -lt $s.Raw + $s.RawSize) { return $s.Va + ($off - $s.Raw) }
  }; -1
}
function ReadAscii([byte[]]$bb,$off) {
  $sb = New-Object Text.StringBuilder
  while ($off -lt $bb.Length -and $bb[$off] -ne 0) { [void]$sb.Append([char]$bb[$off]); $off++ }
  $sb.ToString()
}
$exp = R2O $gb $expRva $numSec $secStart
$nameCount = [BitConverter]::ToInt32($gb, $exp + 24)
$namesRva  = [BitConverter]::ToInt32($gb, $exp + 32)
$namesOff  = R2O $gb $namesRva $numSec $secStart
$gaExports = New-Object System.Collections.Generic.HashSet[string]
for ($i=0; $i -lt $nameCount; $i++) {
  $rva = [BitConverter]::ToInt32($gb, $namesOff + $i*4)
  [void]$gaExports.Add((ReadAscii $gb (R2O $gb $rva $numSec $secStart)))
}
"GameAssembly.dll export count: $($gaExports.Count)"

# Find every \0XYZ\0 11-char obfuscated-pattern run, dedup, intersect with GA exports.
$matches = [regex]::Matches($uascii, '(?<=\x00)[A-Za-z_][A-Za-z0-9_]{10}\x00')
$seen = New-Object System.Collections.Generic.HashSet[string]
$rvaToName = @{}

$upPeOff = [BitConverter]::ToInt32($ub, 0x3C)
$upOptOff = $upPeOff + 24
$upNumSec = [BitConverter]::ToUInt16($ub, $upPeOff + 6)
$upSecStart = $upOptOff + [BitConverter]::ToUInt16($ub, $upPeOff + 20)
$upSections = for ($i=0; $i -lt $upNumSec; $i++) {
  $nameBytes = $ub[($upSecStart + $i*40)..($upSecStart + $i*40 + 7)]
  $zero = [Array]::IndexOf($nameBytes, [byte]0)
  if ($zero -lt 0) { $zero = 8 }
  [pscustomobject]@{
    Name = [Text.Encoding]::ASCII.GetString($nameBytes, 0, $zero)
    Va = [BitConverter]::ToInt32($ub, $upSecStart + $i*40 + 12)
    VirtualSize = [BitConverter]::ToInt32($ub, $upSecStart + $i*40 + 8)
    RawSize = [BitConverter]::ToInt32($ub, $upSecStart + $i*40 + 16)
    Raw = [BitConverter]::ToInt32($ub, $upSecStart + $i*40 + 20)
  }
}

foreach ($m in $matches) {
  $name = $m.Value.TrimEnd("`0")
  if ($gaExports.Contains($name) -and $seen.Add($name)) {
    $rva = O2R $m.Index $upSections
    if ($rva -ge 0) { $rvaToName[[uint32]$rva] = $name }
  }
}

$text = $upSections | Where-Object { $_.Name -eq '.text' } | Select-Object -First 1
if ($null -eq $text) { throw "UnityPlayer.dll has no .text section." }
$leaRefs = New-Object System.Collections.ArrayList
$textStart = $text.Raw
$textEnd = $text.Raw + $text.RawSize - 7
for ($off = $textStart; $off -le $textEnd; $off++) {
  if ($ub[$off] -ne 0x48 -or $ub[$off + 1] -ne 0x8D) { continue }
  $modrm = $ub[$off + 2]
  if (($modrm -band 0xC7) -ne 0x05) { continue }
  $disp = [BitConverter]::ToInt32($ub, $off + 3)
  $insRva = O2R $off $upSections
  $target = [int64]$insRva + 7 + $disp
  if ($target -lt 0 -or $target -gt [uint32]::MaxValue) { continue }
  $target32 = [uint32]$target
  if ($rvaToName.ContainsKey($target32)) {
    [void]$leaRefs.Add([pscustomobject]@{ Rva = $insRva; Name = $rvaToName[$target32] })
  }
}

$obfOrdered = @($leaRefs | Sort-Object Rva | ForEach-Object { $_.Name })
"Obfuscated names extracted from UnityPlayer code references: $($obfOrdered.Count)"

if ($obfOrdered.Count -lt $canonical.Count) {
  throw "Count mismatch: canonical=$($canonical.Count), obfuscated=$($obfOrdered.Count). Cannot zip safely."
}

if ($obfOrdered.Count -gt $canonical.Count) {
  $extra = @($obfOrdered | Select-Object -Skip $canonical.Count)
  "Obfuscated export candidates include $($extra.Count) extra entries not present in the canonical map; ignoring extras: $($extra -join ', ')"
}

# 3) Build new mapping text
$pairs = for ($i = 0; $i -lt $canonical.Count; $i++) { "$($canonical[$i])=$($obfOrdered[$i])" }
$newTable = ($pairs -join "`n") + "`n"

"=== First 5 new mappings ==="
$pairs | Select-Object -First 5 | ForEach-Object { "  $_" }
"=== Last 5 new mappings ==="
$pairs | Select-Object -Last 5 | ForEach-Object { "  $_" }

# 4) Compose new file: [known-good binary head from .resource-head-bak] + [new mapping]
#    Doorstop reads the Resources file with a loader-specific seekable layout
#    (the offset table doorstop walks is at known positions in this format).
#    The canonical Metadata\global-metadata.dat layout is different and breaks
#    doorstop with "Cannot seek to string data location" / error 5. Keep the
#    known-good binary head from the backup; only swap the trailing
#    text mapping. The Cpp2IL IndexOutOfRange that this binary causes is
#    handled by the patched LibCpp2IL.dll (try-catch in GetGenericMethodFromIndex).
$head = New-Object byte[] $tableStart
[Array]::Copy($rb, 0, $head, 0, $tableStart)
$newTableBytes = [System.Text.Encoding]::ASCII.GetBytes($newTable)
$out = New-Object byte[] ($head.Length + $newTableBytes.Length)
[Array]::Copy($head, 0, $out, 0, $head.Length)
[Array]::Copy($newTableBytes, 0, $out, $head.Length, $newTableBytes.Length)

"Old file size: $($rb.Length)  /  table started at: $tableStart"
"New file size: $($out.Length)  (head=$($head.Length) + new_table=$($newTableBytes.Length))"

# 5) Atomic write
$tmp = "$res.new"
[System.IO.File]::WriteAllBytes($tmp, $out)
Move-Item -LiteralPath $tmp -Destination $res -Force
"Written: $res"

# 6) Verify
$verify = [System.IO.File]::ReadAllBytes($res)
$vAscii = [System.Text.Encoding]::ASCII.GetString($verify)
$vIdx = $vAscii.IndexOf("il2cpp_init=")
"Verify: il2cpp_init= at offset $vIdx in new file"
"First 200 chars of mapping section:"
$vAscii.Substring($vIdx, [Math]::Min(200, $verify.Length - $vIdx))
