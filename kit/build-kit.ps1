# Builds the SparkLoad Audit Kit folder and zip. Needs the .NET 10 SDK on the build machine only.
#
#   build-kit.ps1 -SparkVsZip <SparkVS-0.4.0.zip> -SparkxExe <sparkx.exe> -OutRoot <folder>
#                 [-HookDll <prebuilt SparkLoad.Hook.dll>] [-OptimumDir <folder with Lib\0Harmony.dll>]
#                 [-KitName SparkLoad-Audit-Kit-v0.1] [-ExpectedSparkVsSha256 <hex>]
#
# Result: <OutRoot>\<KitName>\ and <OutRoot>\<KitName>.zip. Nothing is installed anywhere.
param(
    [Parameter(Mandatory = $true)][string]$SparkVsZip,
    [Parameter(Mandatory = $true)][string]$SparkxExe,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [string]$HookDll,
    [string]$OptimumDir,
    [string]$KitName = 'SparkLoad-Audit-Kit-v0.1',
    [string]$ExpectedSparkVsSha256
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$kitSrc = $PSScriptRoot
$repo = Split-Path -Parent $kitSrc
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($p in @($SparkVsZip, $SparkxExe)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing input file: $p" } }
if ($HookDll -and -not (Test-Path -LiteralPath $HookDll -PathType Leaf)) { throw "Missing hook dll: $HookDll" }
if ($ExpectedSparkVsSha256) {
    $h = (Get-FileHash -LiteralPath $SparkVsZip -Algorithm SHA256).Hash
    if ($h -ne $ExpectedSparkVsSha256.ToUpperInvariant()) { throw "SparkVS zip sha256 is $h, expected $ExpectedSparkVsSha256" }
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$OutRoot = (Resolve-Path -LiteralPath $OutRoot).Path
$kitDir = Join-Path $OutRoot $KitName
$zipPath = Join-Path $OutRoot ($KitName + '.zip')
if ((Split-Path -Parent $kitDir) -ne $OutRoot) { throw 'bad kit name' }
if (Test-Path -LiteralPath $kitDir) { Remove-Item -LiteralPath $kitDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $kitDir | Out-Null
$slDir = Join-Path $kitDir 'sparkload'

# 1. launcher + analyzer: self-contained, multi-file (runtime is not stripped), no pdb so no build paths are embedded
Write-Host '== publish launcher (self-contained win-x64)'
& dotnet publish (Join-Path $repo 'launcher\sparkload.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o $slDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem -LiteralPath $slDir -Recurse -Filter '*.pdb' | Remove-Item -Force

# 2. hook dll into sparkload\hook\
$hookOut = Join-Path $slDir 'hook'
New-Item -ItemType Directory -Path $hookOut | Out-Null
if (-not $HookDll) {
    Write-Host '== build hook dll from source'
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('sparkload-hook-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $hargs = @('build', (Join-Path $repo 'hook\SparkLoad.Hook.csproj'), '-c', 'Release', '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:OutputPath=' + $tmp + '\'))
    if ($OptimumDir) { $hargs += ('-p:OptimumDir=' + $OptimumDir) }
    & dotnet @hargs
    if ($LASTEXITCODE -ne 0) { throw 'hook build failed' }
    $HookDll = Join-Path $tmp 'SparkLoad.Hook.dll'
}
Copy-Item -LiteralPath $HookDll -Destination (Join-Path $hookOut 'SparkLoad.Hook.dll')

# 3. config template, scripts, readme (copied with Windows CRLF line endings, ASCII), mod zip, sparkx
function Copy-Text([string]$src, [string]$dstDir) {
    $t = [IO.File]::ReadAllText($src) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $dstDir (Split-Path -Leaf $src)), $t, (New-Object System.Text.UTF8Encoding($false)))
}
Copy-Text (Join-Path $kitSrc 'portable-kit.json') $slDir
Copy-Text (Join-Path $kitSrc 'Watch-Sparkx.ps1') $slDir
foreach ($f in @('RUN-AUDIT.cmd', 'Collect-Audit.ps1', 'Collect-Audit.cmd', 'README.txt')) { Copy-Text (Join-Path $kitSrc $f) $kitDir }
Copy-Item -LiteralPath $SparkVsZip -Destination (Join-Path $kitDir 'SparkVS-0.4.0.zip')
New-Item -ItemType Directory -Path (Join-Path $kitDir 'sparkx') | Out-Null
Copy-Item -LiteralPath $SparkxExe -Destination (Join-Path $kitDir 'sparkx\sparkx.exe')

# 4. zip with the kit folder as the single top-level folder, forward slashes in entry names
Write-Host '== zip'
$zs = [IO.File]::Create($zipPath)
try {
    $za = New-Object IO.Compression.ZipArchive($zs, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $base = $kitDir.TrimEnd('\').Length
        foreach ($f in (Get-ChildItem -LiteralPath $kitDir -Recurse -File | Sort-Object FullName)) {
            $rel = $KitName + '/' + $f.FullName.Substring($base).TrimStart('\').Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $za.Dispose() }
} finally { $zs.Dispose() }

# 5. verify: entry count equals file count and every entry reads back
$diskCount = @(Get-ChildItem -LiteralPath $kitDir -Recurse -File).Count
$zr = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $n = 0; $bytes = 0L
    foreach ($e in $zr.Entries) {
        $s = $e.Open(); try { $buf = New-Object byte[] 65536; while (($r = $s.Read($buf, 0, $buf.Length)) -gt 0) { $bytes += $r } } finally { $s.Dispose() }
        $n++
    }
} finally { $zr.Dispose() }
if ($n -ne $diskCount) { throw "zip has $n entries, folder has $diskCount files" }
Write-Host ("zip test OK: $n entries, $bytes bytes read back")
Write-Host ('kit folder: ' + $kitDir)
Write-Host ('kit zip:    ' + $zipPath + ' (' + (Get-Item -LiteralPath $zipPath).Length + ' bytes)')
