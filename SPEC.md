# SparkLoad v0.1 build SPEC (interface contract between parts)

Design source: ..\sparkload-design\DESIGN.md s3, BUILD-BRIEF.md. This file only fixes the interfaces so parts can be built in parallel. Measurement only, zero behaviour change. No em or en dashes in any file.

## 0. Layout
- Source: <repo root>\
  - launcher\   sparkload.csproj (net10.0, AssemblyName sparkload, OutputType WinExe + AttachConsole(-1), Version 0.1.0). Owns Program.cs, Launch\*.cs.
  - launcher\Analyzer\*.cs   analyzer, compiled into sparkload.exe. Entry: `public static int SparkLoad.Analyzer.AnalyzerMain.Run(string runDir, string? compareRunDir, TextWriter log)`.
  - hook\   SparkLoad.Hook.csproj (AssemblyName SparkLoad.Hook).
  - testtarget\   dummy net10 console app used for offline tests (no game needed).
  - analyzer-tests\   console test harness with synthetic inputs.
- Build outputs: <build root>\out\{launcher,hook,...}. Never write into the game install, the Optimum folder, VintagestoryData, or the live Mods folder.
- Packages: Microsoft.Diagnostics.Tracing.TraceEvent 3.1.28 (same as sparkx) for parsing. Do NOT use reflection into internals of Microsoft.Diagnostics.NETCore.Client.

## 1. Run folder (written by the launcher)
- run.json: run_id, arm (V|O|D), mode (passive|default|deep), timing_void (bool, true in deep), exe, args, data_path, launcher_version, t0_qpc, qpc_freq, t0_utc_filetime, child_pid, kernel_create_filetime, g1_delta_ms (kernel create minus t0, ms), finalize_log_ms, end_ms, exit_reason, present_mode (string or null).
- raw.nettrace (default, deep)
- samples.csv (default, deep): header `t_ms,pid,kind,tid,name,cpu_ms,state,wait_reason,ctx,read_bytes,write_bytes,other_bytes,working_set,private_bytes,threads`. kind P = one row per process per tick (tid empty; cpu_ms = process user+kernel cumulative); kind T = one row per thread per tick, written only when cpu_ms or ctx changed since the last written row for that tid (plus first sight). cpu_ms is cumulative. name = OS thread description (GetThreadDescription), may be empty.
- logtail.csv (all modes): header `t_ms,file,offset,len,text`. One row per complete log line, t_ms = arrival QPC relative to t0, offset = byte offset of line start, text CSV-quoted, cut at 400 chars. Files: client-main, client-debug, server-main, server-debug (under <data>\Logs) and optimum-launcher.log. Poll 50 ms. Handle missing file, truncation and recreation.
- ipc.log: `t_ms pid cookie action` per diagnostic-port connection (session | resume | resume-foreign | stop | idle).
- hook.log: appended by the hook in every process that loads it (see 3).
- launcher.log: everything the launcher would print.
- loading.csv, loading.md, analyzer.json: analyzer outputs. events.jsonl only with `analyze --events-jsonl` (debug export; it reached 18.8 GB on a full-data run).
All t_ms are milliseconds (double, 3 decimals) since t0. t0 = QueryPerformanceCounter read immediately before CreateProcessW.

## 2. Launcher
CLI (never interactive, never prints usage and exits 0 silently; bad usage = exit 2):
`sparkload run --exe <path> [--mode passive|default|deep] --out <root> [--run-id <id>] [--arm V|O|D] [--data <datapath>] [--launcher-log <path to optimum-launcher.log>] [--hook <SparkLoad.Hook.dll>] [--off m1,m2] [--tail-seconds 75] [--max-seconds 900] [--finalize-pattern <regex>] [--finalize-file client-main] [--then kill|wait|detach] [--guard-names Vintagestory,Optimum] [--compare <rundir>] [--manifest-cmd "<command line>"] -- <game args>`
`sparkload analyze --run <dir> [--compare <dir>] [--events-jsonl] [--max-analyzer-mb <n>]`
Analyzer options: `--events-jsonl` also writes events.jsonl (off by default; step 1 then streams straight into step 2). `--max-analyzer-mb <n>` (default 4096, 0 = off): the analyzer puts itself in a job object with that process memory limit, and a monitor thread ends it at 90% of the limit (private bytes, checked every 250 ms) with `MEMORY CEILING HIT` on stderr and exit 7. An allocation failure at the limit also exits 7. loading.md gets an `## Analyzer` section right before `## Gates` (peak working set, events processed, events/s, wall, step 1 detail) and analyzer.json holds the same numbers.
Memory options (run): `--max-trace-mb <n>` (default 1536): cap on raw.nettrace. When the copied stream reaches it, the launcher stops the tracing session cleanly, writes `trace_end="size_cap"` into run.json, logs a `trace: size cap reached` line in launcher.log, and the analyzer marks events.meta.json truncated with a note. `--max-child-gb <n>` (default 20, 0 = off): ProcessMemoryLimit (JOB_OBJECT_LIMIT_PROCESS_MEMORY, a commit limit per process in the job) set in the same job-limits struct as kill-on-close; it stays set on the detach path. The applied value is logged (`job: kill-on-close, process memory limit ...`) and recorded in run.json (`max_trace_mb`, `max_child_gb`).
`sparkload preflight [--guard-names ...]` (exit 0 none running, 3 foreign found; prints pid, name, start time).
Exit codes: 0 ok, 2 usage, 3 foreign game process present (nothing killed, nothing launched), 4 create failed, 5 no finalize before --max-seconds, 6 analyzer failed, 7 analyzer memory ceiling hit.

Flow:
1. Preflight guard: any process whose name starts with a guard name = abort 3.
2. default/deep: create the reverse diagnostic server (named pipe `sparkload-<launcherpid>-<8hex>`, multi-instance, always one instance listening).
3. Child environment (child only, never the launcher's own process env): default/deep add `DOTNET_DiagnosticPorts=<pipe>,suspend`, `DOTNET_STARTUP_HOOKS=<hook path>` (appended with `;` to an existing value), `SPARKLOAD_RUN_DIR`, `SPARKLOAD_LAUNCHER_PID`, `SPARKLOAD_TARGET_EXE`, `SPARKLOAD_OFF`. passive adds nothing.
4. t0 QPC + GetSystemTimePreciseAsFileTime, then CreateProcessW(CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT), cwd = exe folder; create Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE plus, unless `--max-child-gb 0`, JOB_OBJECT_LIMIT_PROCESS_MEMORY (ProcessMemoryLimit at offset 112 of the 144 byte extended limit struct), assign, ResumeThread. Kernel creation time from GetProcessTimes gives g1_delta_ms.
5. Diagnostic IPC (hand-rolled, section 2.1). First advertise from child_pid: CollectTracing (session), copy the stream to raw.nettrace on a dedicated thread that never blocks on anything else. Next advertise from child_pid: ResumeRuntime. Advertise from any other pid: ResumeRuntime at once, log `resume-foreign` with the process name. Keep the newest idle connection per pid for later commands.
6. Sampler thread every 250 ms (default/deep) over child_pid and every other process in the job: NtQuerySystemInformation(SystemProcessInformation), one call per tick.
7. Finalize = arrival of the first line matching --finalize-pattern in --finalize-file. Then keep collecting --tail-seconds, then StopTracing, wait for the copy thread to finish (disposed if it does not within the wait), close files, write run.json, then with `--then kill` TerminateJobObject BEFORE the analyzer runs (the game must not stay alive at 20+ GB while analysis uses memory), then run the analyzer, run --manifest-cmd if given, then --then: wait (default; wait for game exit) or detach (clear kill-on-close, keep the process memory limit, exit).
8. If the launcher dies at any point the Job Object kills the child (this is gate G7a).
Providers, default: `SparkLoad` (all keywords, Informational), `Microsoft-Windows-DotNETRuntime` keywords GC 0x1 | Loader 0x8 | Exception 0x8000 at Informational, rundown off, stackwalk off, buffer 256 MB. deep: add `Microsoft-DotNETCore-SampleProfiler` Informational, rundown on, stackwalk on, runtime keywords plus JIT 0x10 | NGEN 0x20 | Stack 0x40000000; set timing_void and prefix loading.md with a TIMING-VOID line.

### 2.1 Diagnostic IPC wire format (little endian)
- Runtime connects to `\\.\pipe\<name>` and sends a 34 byte advertise: ASCII `ADVR_V1\0` (8), runtime cookie GUID (16), pid u64 (8), reserved u16 (2).
- Command header 20 bytes: ASCII `DOTNET_IPC_V1\0` (14), size u16 (header + payload), command_set u8, command_id u8, reserved u16 = 0.
- Response: same header, command_set 0xFF; command_id 0x00 OK, 0xFF error (payload u32 hresult).
- ResumeRuntime: set 0x04 id 0x01, no payload.
- CollectTracing3: set 0x02 id 0x04. Payload: circularBufferMB u32, format u32 (1 = NetTrace), requestRundown u8, requestStackwalk u8, provider count u32, then per provider: keywords u64, logLevel u32, name string, filterData string. Fall back to CollectTracing2 (id 0x03, same payload without requestStackwalk) on an error response.
- String: u32 count of UTF-16 code units including the terminating null, then UTF-16LE chars with the null. Empty or null string = u32 0 and no bytes.
- CollectTracing OK response payload: session id u64. After it the SAME connection carries the nettrace stream until the session stops.
- StopTracing: set 0x02 id 0x01, payload session id u64, sent on a different (idle advertised) connection.
- One command per connection. After a command the runtime reconnects and advertises again with the same cookie.

## 3. Hook (SparkLoad.Hook.dll)
- `internal class StartupHook { public static void Initialize() }` in the global namespace. Must never throw and never write to the console.
- Role check: game only when Environment.ProcessPath equals SPARKLOAD_TARGET_EXE (case-insensitive) AND the parent pid equals SPARKLOAD_LAUNCHER_PID. Anything else: append `pid exe role=noop reason` to <SPARKLOAD_RUN_DIR>\hook.log and return without creating the EventSource or touching Harmony.
- Game role: append `role=game`, create EventSource `SparkLoad`, set AppContext data `SparkLoad.RunDir`, subscribe to AppDomain.AssemblyLoad, install patches when the assembly named VintagestoryLib loads (retry not-yet-patched targets on later assembly loads; VSSurvivalMod targets when that assembly loads). Harmony id `sparkload.hook`. Prefix and postfix only, no transpilers, no reverse patches.
- When the TryRunModPhase patch is in place: `AppContext.SetSwitch("SparkLoad.Hook.HoldsTryRunModPhase", true)`.
- Every target: resolved by exact signature from the embedded target table (stock and Optimum signature variants); mismatch or missing = stand down for that target only, Patch event with status. Off switch: marker name listed in SPARKLOAD_OFF. Hit counter per target.
- Events (EventSource `SparkLoad`, all Informational):
  1 Hello(pid, qpc, qpcFreq, hookVersion, runtime)
  2 MarkerDef(markerId, name)
  3 Begin(markerId, tid, seq, qpc, a, b, c, s)
  4 End(markerId, tid, seq, qpc, a, b, c, s)
  5 Thread(tid, managedId, name)            once per thread, on its first marker
  6 Patch(marker, target, status, detail)   status ok | missing | mismatch | off | error
  7 Frame(tid, seq, qpc, index, durTicks)   first 120 frames after finalize
  8 FrameAgg(tid, seq, qpc, count, sumTicks, maxTicks, over50) one per second after those 120
  9 Playable(tid, seq, qpc, frameIndex)     first run of 60 consecutive frames under 50 ms after finalize
  10 Stats(marker, hits)                    emitted at finalize end and at Playable
  tid = OS thread id (GetCurrentThreadId). seq = per-thread counter starting at 1, incremented for every event carrying it; a gap = lost markers. qpc = Stopwatch.GetTimestamp().
- column.load throttle (hook): a call is a Begin/End pair only for the first 4 calls per (x, z) column and for any call that lasts 1 ms or more (a slow call is emitted late, from the postfix, with the prefix timestamp). All other calls are only counted (lock-free hash of 65536 slots, no allocation on the suppressed path). Suppressed totals travel as `Stats` events whose marker is `column.load.suppressed <x> <z>` and whose hits = calls suppressed so far, sent at power-of-two counts and in a final flush at finalize end; the analyzer takes the LAST value per column. The analyzer treats these as hook counts, not as markers, and reports them in the Columns section of loading.md.
- Analyzer memory: step 1 is an external merge sort (sorted runs of at most 1M events spilled to `events.spill\` in the run dir, then a k-way merge by (t_ms, order); events.jsonl is byte-identical to the former in-memory sort). The full sort stays because runtime events get their time from an offset that is only final at the end of the trace (max over qpc events of payload time minus trace time). Each merged line goes straight into the step 2 fold (the same bytes events.jsonl would hold, so the result is identical by construction). Without --events-jsonl, exception and loader events (order-free counts in step 2; up to 97% of a trace) are counted in step 1 and never spilled. Retained column.load spans cap at 3,000,000 and markers at 6,000,000; anything past a cap is counted and flagged CAPPED in the Analyzer section. Step 2 folds column.load spans while reading: the first 4 per column and every span of 1 ms or more stay in the listing (loading.csv, G11 gap markers), the rest are counted into the Columns totals and the coverage windows only.
- Marker names: client.boot.main, client.boot.screenmanager, client.boot.stage1..5, server.launch, server.phase (a = run phase enum value; one Begin/End per EnterRunPhase call), mod.phase (a = phase enum value, s = `modid|client` or `modid|server`), server.blocklist.wait, server.blocklist.set, server.remap.blocks, server.remap.items, server.save.load, server.spawn.pregen, column.pass (a = chunkX, b = chunkZ, c = pass), column.load (a = chunkX, b = chunkZ, c = 1 from db, 0 generated, -1 unknown), client.assets.handle, client.assets.finish, client.shapes.block, client.shapes.item, client.shapes.entity, client.atlas.A, client.atlas.B, client.atlas.C, client.handbook, client.finalize.handle, client.finalize.trigger, frame.

## 4. Analyzer
- Step 1: raw.nettrace to events.jsonl (one JSON object per event: t_ms, src (sparkload|gc|loader|exception), name, fields). t_ms for SparkLoad events from the payload qpc and run.json t0_qpc. Runtime events mapped through the offset learned from the Hello event (payload qpc vs trace timestamp).
- Step 2: events.jsonl + samples.csv + logtail.csv + run.json to loading.csv and loading.md. Step 2 must work with events.jsonl absent (passive arm): phases then come from log lines only.
- loading.csv columns, exact order: run_id,arm,t_start_ms,t_end_ms,dur_ms,side,thread,phase,subphase,mod,x,z,pass,count,bytes,source,lost. source = marker | log | sample. First row: phase `process`, t_start_ms 0. Three columns are appended after `lost`: name_source, named, cause (filled on silence rows only; cause is no_thread_name, low_cpu_share, idle_or_io or no_samples when named is 0).
- loading.md sections: header (run, arm, mode, totals: launch-to-finalize by log and by marker, launch-to-playable, g1_delta_ms, markers lost, per-target hit table with zero-hit targets flagged), phase table (seconds, share), top 15 mods by phase time per side, column table (generated vs loaded, per pass ms, columns per second, count of distinct columns reaching the last pass), silence table (every gap over 2 s between consecutive signals, with top thread by CPU delta, its name and name source, CPU seconds, process I/O bytes, GC pause ms, verdict, status), an Unnamed silences section (each silence still unnamed with thread, cause, top threads, open marker spans, and the nearest event before and after on the thread and globally; the G2 gate line reports silences_over_2s, named, unnamed and the split no_thread_name / low_cpu_share / idle_or_io), GC table, coverage line (G2: share of 250 ms windows before finalize with neither an open marker span nor an attribution: top thread CPU at least 25 percent of the window, or I/O delta at least 64 KB, or GC pause), log cross-check table (G6: each server run-phase marker vs the matching log line, delta ms), answers (G11: client main thread idle seconds while the server launches, with window and CPU seconds; what ran between finalize and playable and whether frames rendered; seconds in the block-list wait), moved-bottleneck diff when --compare is given.
- Exit non-zero only on unreadable inputs; missing optional inputs become `n/a` rows.

## 5. SparkVS side
- LoadPhaseHook.InstallOnce: if `AppContext.TryGetSwitch("SparkLoad.Hook.HoldsTryRunModPhase", out var held) && held`, skip its own patch.
- WriteBundle: one Guard call copying loading.md and loading.csv from the run dir (`AppContext.GetData("SparkLoad.RunDir") as string`) when present.
