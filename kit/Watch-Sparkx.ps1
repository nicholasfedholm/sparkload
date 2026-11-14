# Starts sparkx (live memory watch) on the game process once SparkLoad has finished its load trace, and
# stops it when the game closes. Runs in the background; never touches the game except through sparkx's
# normal read-only diagnostics connection. Started by RUN-AUDIT.cmd.
param(
    [Parameter(Mandatory = $true)][string]$RunDir,
    [Parameter(Mandatory = $true)][string]$SparkxExe,
    [int]$Minutes = 120,
    [double]$SnapshotEvery = 5,
    [int]$WaitForTraceMinutes = 40
)
$ErrorActionPreference = 'Stop'
$helperLog = $RunDir.TrimEnd('\', '/') + '.sparkx-helper.log'
function Log([string]$m) {
    try { Add-Content -LiteralPath $helperLog -Value ((Get-Date -Format 'HH:mm:ss') + ' ' + $m) } catch { }
}
$doneMarker = $RunDir.TrimEnd('\', '/') + '.sparkx.done'
function Done([string]$why) {
    Log ('done: ' + $why)
    try { Set-Content -LiteralPath $doneMarker -Value $why } catch { }
}
try {
    if (-not (Test-Path -LiteralPath $SparkxExe)) { Log 'sparkx.exe not found'; Done 'no sparkx'; exit 0 }
    $runJson = Join-Path $RunDir 'run.json'
    $started = Get-Date
    $deadline = $started.AddMinutes($WaitForTraceMinutes)
    $info = $null
    # Wait for the launcher to finish its load trace (exit_reason leaves "running"). sparkx pauses the game briefly
    # when it snapshots, so it must not start while loading is being measured.
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $runJson) {
            try {
                $j = Get-Content -LiteralPath $runJson -Raw | ConvertFrom-Json
                if ($j.exit_reason -eq 'create-failed') { Done 'game did not start'; exit 0 }
                if ($j.child_pid -and $j.exit_reason -and $j.exit_reason -ne 'running') { $info = $j; break }
                if ($j.launcher_pid -and -not (Get-Process -Id ([int]$j.launcher_pid) -ErrorAction SilentlyContinue)) { Done 'launcher gone'; exit 0 }
            } catch { }
        }
        # The launcher writes run.json within a second or two of starting; if it never does (for example the game was
        # already running and the launcher stopped), give up quietly.
        if (-not (Test-Path -LiteralPath $runJson) -and ((Get-Date) - $started).TotalSeconds -gt 120) { Done 'launcher never started a run'; exit 0 }
        Start-Sleep -Seconds 2
    }
    if ($null -eq $info) { Done 'load trace never finished'; exit 0 }
    $gamePid = [int]$info.child_pid
    if (-not (Get-Process -Id $gamePid -ErrorAction SilentlyContinue)) { Done 'game already closed'; exit 0 }
    $outDir = Join-Path $RunDir 'sparkx'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $argList = 'watch --pid ' + $gamePid + ' --minutes ' + $Minutes + ' --snapshot-every ' + $SnapshotEvery.ToString([Globalization.CultureInfo]::InvariantCulture) + ' --out "' + $outDir + '"'
    if ($info.data_path) { $argList += ' --data "' + $info.data_path + '"' }
    Log ('starting sparkx: ' + $argList)
    $so = Join-Path $outDir 'sparkx-stdout.txt'
    $se = Join-Path $outDir 'sparkx-stderr.txt'
    $sp = Start-Process -FilePath $SparkxExe -ArgumentList $argList -PassThru -WindowStyle Hidden -RedirectStandardOutput $so -RedirectStandardError $se
    $null = $sp.Handle
    # sparkx ends by itself when the game goes away. Give it a minute to write its outputs, then end only our own sparkx process.
    $goneAt = $null
    while (-not $sp.HasExited) {
        if (-not (Get-Process -Id $gamePid -ErrorAction SilentlyContinue)) {
            if ($null -eq $goneAt) { $goneAt = Get-Date; Log 'game closed; waiting for sparkx to finish' }
            elseif (((Get-Date) - $goneAt).TotalSeconds -gt 60) { Log 'sparkx still running 60 s after the game closed; ending it'; try { $sp.Kill() } catch { }; break }
        }
        Start-Sleep -Seconds 2
    }
    Done ('sparkx exit ' + $(if ($sp.HasExited) { $sp.ExitCode } else { 'killed' }))
} catch {
    Log ('helper error: ' + $_.Exception.Message)
    Done 'helper error'
}
