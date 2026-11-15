using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SparkLoad.Analyzer
{
    public sealed class AnalyzerOptions
    {
        /// <summary>Also write events.jsonl (debug export, can be many GB). Off by default.</summary>
        public bool ExportEvents;
        /// <summary>The memory ceiling this analysis runs under (0 = none), shown in the report.</summary>
        public long MaxAnalyzerMb;
    }

    public static class AnalyzerMain
    {
        /// <summary>
        /// Analyzes one run folder: step 1 (raw.nettrace merged in time order, when a trace exists) streamed straight
        /// into step 2 (loading.csv and loading.md). With compareRunDir, loading.md gains a moved-bottleneck section.
        /// Returns 0 on success, non-zero only when the inputs are unreadable.
        /// </summary>
        public static int Run(string runDir, string? compareRunDir, TextWriter log) => Run(runDir, compareRunDir, log, new AnalyzerOptions());

        public static int Run(string runDir, string? compareRunDir, TextWriter log, AnalyzerOptions opts)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
                {
                    log.WriteLine("analyzer: run folder not found: " + Txt.Clean(runDir));
                    return 1;
                }
                runDir = Path.GetFullPath(runDir);

                RunData d;
                try { d = RunData.Load(runDir, log, true, opts.ExportEvents); }
                catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    log.WriteLine("analyzer: cannot read run.json: " + Txt.Clean(ex.Message));
                    return 1;
                }

                Result? cmp = null;
                if (!string.IsNullOrWhiteSpace(compareRunDir))
                {
                    try
                    {
                        var cd = RunData.Load(Path.GetFullPath(compareRunDir), log, true, false);
                        cmp = Result.Build(cd);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        log.WriteLine("analyzer: compare run unreadable, diff skipped: " + Txt.Clean(ex.Message));
                    }
                }

                var r = Result.Build(d);
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(runDir, "loading.csv"), Report.BuildCsv(r), utf8);
                string md = Report.BuildMarkdown(r, cmp);
                var m = Metrics.Take(d.Ev, sw.Elapsed.TotalSeconds, opts);
                // The analyzer section goes right before the gates block, so loading.md still ends with the gates.
                int gi = md.LastIndexOf("## Gates", StringComparison.Ordinal);
                md = gi < 0 ? md + m.Markdown() : md.Substring(0, gi) + m.Markdown() + md.Substring(gi);
                File.WriteAllText(Path.Combine(runDir, "loading.md"), md, utf8);
                File.WriteAllText(Path.Combine(runDir, "analyzer.json"), m.Json(), utf8);
                foreach (var g in r.Gates) log.WriteLine("analyzer: " + g);
                log.WriteLine("analyzer: " + m.OneLine());
                log.WriteLine("analyzer: wrote loading.csv, loading.md and analyzer.json in " + Txt.Clean(runDir));
                return 0;
            }
            catch (OutOfMemoryException)
            {
                throw; // the caller reports the memory ceiling
            }
            catch (Exception ex)
            {
                log.WriteLine("analyzer: failed: " + Txt.Clean(ex.GetType().Name + ": " + ex.Message));
                return 1;
            }
        }

        /// <summary>Analyzer self-measurement for the report: peak working set, events, throughput.</summary>
        internal sealed class Metrics
        {
            public double WallS;
            public long PeakWsBytes;
            public long PeakPrivateBytes;
            public long Events;          // events folded by step 2 (incl. those counted in step 1)
            public long TraceEvents = -1; // events read from the trace (-1: no trace this analysis)
            public long CountedWithoutSort;
            public long SpillBytes;
            public int SpillRuns;
            public bool EventsJsonl;
            public long MaxAnalyzerMb;
            public long RetainedOverflow, MarkersOverflow;

            public double EventsPerSec => WallS > 0 ? (TraceEvents >= 0 ? TraceEvents : Events) / WallS : 0;

            public static Metrics Take(EventData ev, double wallS, AnalyzerOptions opts)
            {
                var m = new Metrics { WallS = wallS, Events = ev.Count, MaxAnalyzerMb = opts.MaxAnalyzerMb, EventsJsonl = opts.ExportEvents,
                    RetainedOverflow = ev.RetainedOverflow, MarkersOverflow = ev.MarkersOverflow };
                try
                {
                    using var p = Process.GetCurrentProcess();
                    p.Refresh();
                    m.PeakWsBytes = p.PeakWorkingSet64;
                    m.PeakPrivateBytes = p.PeakPagedMemorySize64;
                }
                catch (Exception) { }
                if (ev.Step1 != null)
                {
                    m.TraceEvents = ev.Step1.TraceEvents;
                    m.CountedWithoutSort = ev.Step1.Bypassed;
                    m.SpillBytes = ev.Step1.SpillBytes;
                    m.SpillRuns = ev.Step1.SpillRuns;
                }
                return m;
            }

            static string Mb(long b) => (b / 1048576.0).ToString("F0", CultureInfo.InvariantCulture);

            public string OneLine() =>
                "analyzer peak_ws_mb=" + Mb(PeakWsBytes) + " events=" + Events + " trace_events=" + TraceEvents
                + " events_per_s=" + EventsPerSec.ToString("F0", CultureInfo.InvariantCulture) + " wall_s=" + WallS.ToString("F1", CultureInfo.InvariantCulture)
                + (MaxAnalyzerMb > 0 ? " limit_mb=" + MaxAnalyzerMb : "");

            public string Markdown()
            {
                var sb = new StringBuilder();
                sb.AppendLine("## Analyzer").AppendLine();
                sb.AppendLine("- peak working set: " + Mb(PeakWsBytes) + " MB (peak pagefile-backed " + Mb(PeakPrivateBytes) + " MB)"
                    + (MaxAnalyzerMb > 0 ? "; ceiling " + MaxAnalyzerMb + " MB" : "; no ceiling"));
                sb.AppendLine("- events processed: " + Txt.I(Events) + (TraceEvents >= 0 ? " (read from the trace: " + Txt.I(TraceEvents) + ")" : " (from events.jsonl)")
                    + "; " + EventsPerSec.ToString("F0", CultureInfo.InvariantCulture) + " events/s; wall " + WallS.ToString("F1", CultureInfo.InvariantCulture) + " s");
                if (TraceEvents >= 0)
                    sb.AppendLine("- step 1: " + Txt.I(CountedWithoutSort) + " exception/loader events counted without sorting; temporary sorted runs "
                        + SpillRuns + " files, " + Mb(SpillBytes) + " MB (deleted); events.jsonl " + (EventsJsonl ? "written (--events-jsonl)" : "not written (opt-in: --events-jsonl)"));
                if (RetainedOverflow > 0 || MarkersOverflow > 0)
                    sb.AppendLine("- CAPPED: " + Txt.I(RetainedOverflow) + " column.load spans folded into the totals past " + Txt.I(EventData.RetainedLoadCap)
                        + " individual spans; " + Txt.I(MarkersOverflow) + " marker events dropped past " + Txt.I(EventData.MarkerCap) + " (their spans are missing)");
                sb.AppendLine();
                return sb.ToString();
            }

            public string Json()
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteNumber("peak_working_set_bytes", PeakWsBytes);
                    w.WriteNumber("peak_pagefile_bytes", PeakPrivateBytes);
                    w.WriteNumber("max_analyzer_mb", MaxAnalyzerMb);
                    w.WriteNumber("events_processed", Events);
                    w.WriteNumber("trace_events_read", TraceEvents);
                    w.WriteNumber("events_per_sec", Math.Round(EventsPerSec, 1));
                    w.WriteNumber("wall_s", Math.Round(WallS, 3));
                    w.WriteNumber("counted_without_sort", CountedWithoutSort);
                    w.WriteNumber("spill_bytes", SpillBytes);
                    w.WriteNumber("spill_runs", SpillRuns);
                    w.WriteBoolean("events_jsonl_written", EventsJsonl && TraceEvents >= 0);
                    w.WriteNumber("retained_load_overflow", RetainedOverflow);
                    w.WriteNumber("markers_overflow", MarkersOverflow);
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            }
        }
    }
}
