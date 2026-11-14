# SparkLoad

Launcher, in-process hook and trace analyzer for Vintage Story load timing. Source only: build with `dotnet build` / `dotnet publish` (net10.0). The hook project references Harmony from an Optimum install (`-p:OptimumDir=<path>`, default `%LOCALAPPDATA%\Programs\Optimum`). See SPEC.md for the CLI, trace format and analyzer gates.

## Portable audit kit

`sparkload kit` runs a player's own game under the load trace without killing it, then analyzes after the player closes it (`--dry-run` prints what it would use). `kit\build-kit.ps1` publishes the self-contained launcher, adds the hook, the scripts in `kit\` (RUN-AUDIT.cmd, Collect-Audit.ps1, Watch-Sparkx.ps1, README.txt, portable-kit.json) and the SparkVS zip and sparkx.exe you pass in, and zips the result. Optional settings live in `sparkload\portable-kit.json` (game_exe, data_dir, output_dir, max_trace_mb).
