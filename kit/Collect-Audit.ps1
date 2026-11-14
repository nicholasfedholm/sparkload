# Collect-Audit: packs the results of one audit run into ONE zip: output\VS-Audit-<yyyyMMdd-HHmmss>.zip
# Contents (and nothing else): loading.md + loading.csv, the newest SparkVS capture folder from this run,
# the sparkx output from this run, client-main.log / server-main.log / client-debug.log written during the run,
# mods.csv (name, size, sha256 of every zip in Mods), system.txt, MANIFEST.txt (every file in the zip).
# It never reads or copies saves, ModConfig, credentials or screenshots, and uploads nothing.
param(
    [string]$RunDir,
    [string]$DataDir,
    [string]$GameExe,
    [switch]$WaitForSparkx,
    [switch]$OpenFolder
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inv = [Globalization.CultureInfo]::InvariantCulture
$utf8 = New-Object System.Text.UTF8Encoding($false)
$kitRoot = $PSScriptRoot
$notes = New-Object System.Collections.Generic.List[string]
function Note([string]$m) { $notes.Add($m); Write-Host ('  note: ' + $m) }

# ---- find the run folder -------------------------------------------------------------------------
function Get-ConfiguredOutput {
    foreach ($c in @((Join-Path $kitRoot 'sparkload\portable-kit.json'), (Join-Path $kitRoot 'portable-kit.json'))) {
        if (Test-Path -LiteralPath $c) {
            try {
                $j = Get-Content -LiteralPath $c -Raw | ConvertFrom-Json
                if ($j.output_dir -and ([string]$j.output_dir).Trim().Length -gt 0) { return [Environment]::ExpandEnvironmentVariables(([string]$j.output_dir).Trim().Trim('"')) }
            } catch { }
            break
        }
    }
    return $null
}
if (-not $RunDir) {
    $outBase = Get-ConfiguredOutput
    if (-not $outBase) { $outBase = Join-Path $kitRoot 'output' }
    if (-not (Test-Path -LiteralPath $outBase)) { Write-Host "No output folder found at $outBase. Run RUN-AUDIT.cmd first."; exit 2 }
    $newest = Get-ChildItem -LiteralPath $outBase -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $newest) { Write-Host "No run found under $outBase. Run RUN-AUDIT.cmd first."; exit 2 }
    $RunDir = $newest.FullName
}
if (-not (Test-Path -LiteralPath $RunDir -PathType Container)) { Write-Host "Run folder not found: $RunDir"; exit 2 }
$RunDir = (Resolve-Path -LiteralPath $RunDir).Path.TrimEnd('\')
$zipDir = Split-Path -Parent $RunDir
Write-Host "Run folder: $RunDir"

# ---- run facts -----------------------------------------------------------------------------------
$run = $null
$runJson = Join-Path $RunDir 'run.json'
if (Test-Path -LiteralPath $runJson) { try { $run = Get-Content -LiteralPath $runJson -Raw | ConvertFrom-Json } catch { Note 'run.json could not be read' } }
$runStartUtc = $null
if ($null -ne $run -and $run.t0_utc_filetime) { try { $runStartUtc = [DateTime]::FromFileTimeUtc([long]$run.t0_utc_filetime) } catch { } }
if ($null -eq $runStartUtc) { $runStartUtc = (Get-Item -LiteralPath $RunDir).CreationTimeUtc; Note 'run start time taken from the run folder' }
Write-Host ('Run started (UTC): ' + $runStartUtc.ToString('yyyy-MM-dd HH:mm:ss', $inv))

if (-not $DataDir) {
    if ($null -ne $run -and $run.data_path) { $DataDir = [string]$run.data_path }
    elseif ($env:APPDATA) { $DataDir = Join-Path $env:APPDATA 'VintagestoryData' }
}
if (-not $GameExe -and $null -ne $run -and $run.exe) { $GameExe = [string]$run.exe }
Write-Host "Data folder: $DataDir"

# ---- optional: let the sparkx helper finish writing --------------------------------------------
if ($WaitForSparkx) {
    $marker = $RunDir + '.sparkx.done'
    $helperLog = $RunDir + '.sparkx-helper.log'
    if ((Test-Path -LiteralPath $helperLog) -and -not (Test-Path -LiteralPath $marker)) {
        Write-Host 'Waiting for the memory watch (sparkx) to finish ...'
        $until = (Get-Date).AddSeconds(90)
        while (-not (Test-Path -LiteralPath $marker) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
    }
}

# ---- staging -------------------------------------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('VS-Audit-stage-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $stage | Out-Null
$skipExt = @('.vcdbs', '.vcdbs-wal', '.vcdbs-shm', '.vcdbs-journal', '.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.dmp', '.hprof')
$skipName = '(?i)modconfig|credential|passw|token|secret|clientsettings|serverconfig|playerdata|\.vcdbs'

function Copy-Shared([string]$src, [string]$dst) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    $in = [IO.File]::Open($src, 'Open', 'Read', 'ReadWrite')
    try {
        $out = [IO.File]::Create($dst)
        try { $in.CopyTo($out) } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}
function Copy-Tree([string]$srcDir, [string]$dstDir, [string]$label) {
    $skipped = 0
    foreach ($f in (Get-ChildItem -LiteralPath $srcDir -Recurse -File -Force)) {
        $ext = $f.Extension.ToLowerInvariant()
        if (($skipExt -contains $ext) -or ($f.Name -match $skipName)) { $skipped++; continue }
        $rel = $f.FullName.Substring($srcDir.TrimEnd('\').Length).TrimStart('\')
        Copy-Shared $f.FullName (Join-Path $dstDir $rel)
    }
    if ($skipped -gt 0) { Note "$label : $skipped file(s) left out on purpose (saves, screenshots, dumps, settings or credentials by name or type)" }
}

# 1. loading.md and loading.csv
foreach ($n in @('loading.md', 'loading.csv')) {
    $p = Join-Path $RunDir $n
    if (Test-Path -LiteralPath $p) { Copy-Shared $p (Join-Path $stage $n) } else { Note "$n is not in the run folder (the analysis may not have finished)" }
}

# 2. newest SparkVS capture written during the run
$capRoot = if ($DataDir) { Join-Path $DataDir 'ModData\sparkvs\captures' } else { $null }
if ($capRoot -and (Test-Path -LiteralPath $capRoot)) {
    $cap = Get-ChildItem -LiteralPath $capRoot -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -ne $cap -and $cap.LastWriteTimeUtc -ge $runStartUtc) {
        Copy-Tree $cap.FullName (Join-Path $stage ('sparkvs-capture\' + $cap.Name)) 'SparkVS capture'
    } else { Note 'no SparkVS capture folder was written during this run' }
} else { Note 'no SparkVS capture folder found (is SparkVS-0.4.0.zip in your Mods folder?)' }

# 3. sparkx output from this run
$sx = Join-Path $RunDir 'sparkx'
if (Test-Path -LiteralPath $sx -PathType Container) { Copy-Tree $sx (Join-Path $stage 'sparkx') 'sparkx' } else { Note 'no sparkx output for this run' }

# 4. logs written during the run
$logsDir = if ($DataDir) { Join-Path $DataDir 'Logs' } else { $null }
foreach ($n in @('client-main.log', 'server-main.log', 'client-debug.log')) {
    $p = if ($logsDir) { Join-Path $logsDir $n } else { $null }
    if ($p -and (Test-Path -LiteralPath $p)) {
        if ((Get-Item -LiteralPath $p).LastWriteTimeUtc -ge $runStartUtc) { Copy-Shared $p (Join-Path $stage ('logs\' + $n)) } else { Note "$n was not written during this run, so it is not included" }
    } else { Note "$n not found" }
}

# 5. mods.csv: every zip in Mods
$modsDir = if ($DataDir) { Join-Path $DataDir 'Mods' } else { $null }
$modRows = New-Object System.Collections.Generic.List[object]
if ($modsDir -and (Test-Path -LiteralPath $modsDir)) {
    foreach ($z in (Get-ChildItem -LiteralPath $modsDir -Recurse -File -Filter '*.zip' | Sort-Object FullName)) {
        $rel = $z.FullName.Substring($modsDir.TrimEnd('\').Length).TrimStart('\')
        $h = ''
        try { $h = (Get-FileHash -LiteralPath $z.FullName -Algorithm SHA256).Hash } catch { $h = 'unreadable' }
        $modRows.Add([pscustomobject]@{ name = $rel; size_bytes = $z.Length; sha256 = $h })
    }
} else { Note 'Mods folder not found' }
$csv = New-Object System.Text.StringBuilder
[void]$csv.Append("name,size_bytes,sha256`r`n")
foreach ($r in $modRows) { [void]$csv.Append('"' + ($r.name -replace '"', '""') + '",' + $r.size_bytes + ',' + $r.sha256 + "`r`n") }
[IO.File]::WriteAllText((Join-Path $stage 'mods.csv'), $csv.ToString(), $utf8)

# 6. system.txt (no admin needed; every part has a fallback)
function Try-Get([scriptblock]$b, $fallback) { try { $v = & $b; if ($null -eq $v) { return $fallback } else { return $v } } catch { return $fallback } }
function Disk-Info([string]$path, [string]$label) {
    if (-not $path) { return "${label}: unknown (no path)" }
    $full = Try-Get { [IO.Path]::GetFullPath($path) } $path
    if ($full.StartsWith('\\')) { return "${label}: $full (network path, no drive details)" }
    $letter = $full.Substring(0, 1).ToUpperInvariant()
    $free = Try-Get { $di = New-Object IO.DriveInfo($letter); [math]::Round($di.AvailableFreeSpace / 1GB, 1).ToString($inv) + ' GB free of ' + [math]::Round($di.TotalSize / 1GB, 1).ToString($inv) + ' GB' } 'free space unknown'
    $type = Try-Get {
        $d = Get-Partition -DriveLetter $letter -ErrorAction Stop | Get-Disk -ErrorAction Stop | Get-PhysicalDisk -ErrorAction Stop | Select-Object -First 1
        $mt = [string]$d.MediaType
        if ($mt -eq 'SSD' -or $mt -eq 'HDD') { $mt } else { 'unspecified (' + $mt + ')' }
    } $null
    if ($null -eq $type) {
        $type = Try-Get {
            $all = @(Get-PhysicalDisk -ErrorAction Stop)
            if ($all.Count -eq 1) { [string]$all[0].MediaType + ' (only physical disk)' } else { $null }
        } 'unknown'
    }
    return "${label}: $full ; drive ${letter}: type $type ; $free"
}
$sys = New-Object System.Collections.Generic.List[string]
$sys.Add('SparkLoad audit kit: system information')
$sys.Add('collected: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$os = Try-Get { Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } $null
if ($null -ne $os) {
    $sys.Add('Windows: ' + $os.Caption + ' version ' + $os.Version + ' build ' + $os.BuildNumber + ' ' + $os.OSArchitecture)
    $sys.Add('RAM: total ' + [math]::Round($os.TotalVisibleMemorySize / 1MB, 1).ToString($inv) + ' GB, free ' + [math]::Round($os.FreePhysicalMemory / 1MB, 1).ToString($inv) + ' GB (at collection time)')
} else {
    $sys.Add('Windows: ' + [Environment]::OSVersion.VersionString)
    $sys.Add('RAM: unknown')
}
$cpu = Try-Get { @(Get-CimInstance Win32_Processor -ErrorAction Stop) } @()
foreach ($c in $cpu) { $sys.Add('CPU: ' + ([string]$c.Name).Trim() + ' (' + $c.NumberOfCores + ' cores, ' + $c.NumberOfLogicalProcessors + ' threads)') }
if ($cpu.Count -eq 0) { $sys.Add('CPU: ' + $env:PROCESSOR_IDENTIFIER) }
$gpu = Try-Get { @(Get-CimInstance Win32_VideoController -ErrorAction Stop) } @()
foreach ($g in $gpu) { $sys.Add('GPU: ' + $g.Name + ', driver ' + $g.DriverVersion + ' (' + $g.DriverDate + ')') }
if ($gpu.Count -eq 0) { $sys.Add('GPU: unknown') }
$gameDir = $null
if ($GameExe) { $gameDir = Try-Get { Split-Path -Parent $GameExe } $null }
$sys.Add((Disk-Info $gameDir 'Game folder'))
$sys.Add((Disk-Info $DataDir 'Data folder'))
if ($GameExe) {
    $sys.Add('Game exe: ' + $GameExe)
    $fv = Try-Get { (Get-Item -LiteralPath $GameExe -ErrorAction Stop).VersionInfo.FileVersion } $null
    if ($fv) { $sys.Add('Game exe file version: ' + $fv)}
}
$sys.Add('PowerShell: ' + $PSVersionTable.PSVersion)
$sys.Add('Mods found: ' + $modRows.Count + ' zip file(s)')
[IO.File]::WriteAllText((Join-Path $stage 'system.txt'), (($sys -join "`r`n") + "`r`n"), $utf8)

# 7. MANIFEST.txt: every file in the zip with its size (MANIFEST.txt itself is listed without a size)
$entries = New-Object System.Collections.Generic.List[object]
foreach ($f in (Get-ChildItem -LiteralPath $stage -Recurse -File)) {
    $rel = $f.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/')
    $entries.Add([pscustomobject]@{ path = $rel; size = $f.Length })
}
$sorted = @($entries | Sort-Object path)
$m = New-Object System.Collections.Generic.List[string]
$m.Add('VS audit package. Created ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$m.Add('Run folder name: ' + (Split-Path -Leaf $RunDir))
$m.Add('This zip contains exactly the files below. Nothing else was collected: no saves, no ModConfig, no credentials, no screenshots.')
$m.Add('Some files show folder paths, which can include your Windows user name.')
$m.Add('')
$m.Add('path<TAB>size in bytes')
foreach ($e in $sorted) { $m.Add($e.path + "`t" + $e.size) }
$m.Add('MANIFEST.txt' + "`t" + '(this file)')
if ($notes.Count -gt 0) {
    $m.Add('')
    $m.Add('Notes:')
    foreach ($n in $notes) { $m.Add('- ' + $n) }
}
[IO.File]::WriteAllText((Join-Path $stage 'MANIFEST.txt'), (($m -join "`r`n") + "`r`n"), $utf8)

# ---- zip (forward slashes in entry names) --------------------------------------------------------
$zipPath = Join-Path $zipDir ('VS-Audit-' + $stamp + '.zip')
$i = 1
while (Test-Path -LiteralPath $zipPath) { $zipPath = Join-Path $zipDir ('VS-Audit-' + $stamp + '-' + $i + '.zip'); $i++ }
$zs = [IO.File]::Create($zipPath)
try {
    $za = New-Object IO.Compression.ZipArchive($zs, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($f in (Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)) {
            $rel = $f.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $za.Dispose() }
} finally { $zs.Dispose() }

# verify: reopen, compare the entry list with the manifest
$ok = $true
$zr = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($zr.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $want = @(($sorted | ForEach-Object { $_.path }) + 'MANIFEST.txt' | Sort-Object)
    if (($names -join '|') -ne ($want -join '|')) { $ok = $false }
    Write-Host ('Zip check: ' + $names.Count + ' file(s) in the zip, manifest ' + $(if ($ok) { 'matches' } else { 'DOES NOT MATCH' }))
} finally { $zr.Dispose() }
try { Remove-Item -LiteralPath $stage -Recurse -Force } catch { }
if (-not $ok) { Write-Host 'Internal check failed, please report this.'; exit 4 }

Write-Host ''
Write-Host 'Done. Your file is ready:'
Write-Host ('  ' + $zipPath)
Write-Host 'Open it and read MANIFEST.txt to see exactly what is inside, then send that one file.'
if ($OpenFolder) { try { Start-Process explorer.exe -ArgumentList ('/select,"' + $zipPath + '"') } catch { } }
exit 0
