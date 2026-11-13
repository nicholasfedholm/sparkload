# SparkLoad

Launcher, in-process hook and trace analyzer for Vintage Story load timing. Source only: build with `dotnet build` / `dotnet publish` (net10.0). The hook project references Harmony from an Optimum install (`-p:OptimumDir=<path>`, default `%LOCALAPPDATA%\Programs\Optimum`). See SPEC.md for the CLI, trace format and analyzer gates.
