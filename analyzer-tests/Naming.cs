using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SparkLoad.Analyzer;

namespace SparkLoad.AnalyzerTests
{
    internal static partial class Program
    {
        private static string Di(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// Hand-made run with six silences, one per naming path and cause (game pid 1234, child process pid 777):
        /// S1 1.5-5 s thread of another process, S2 5-9 s unnamed thread with no events, S3 9-13 s unnamed thread inside a marker span,
        /// S4 13-17 s named thread with a low cpu share, S5 17-21 s no cpu but io, S6 21-25 s main thread that has no name.
        /// </summary>
        private static void WriteNamingRun(string dir, bool withSamples = true)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "run.json"),
                "{\"run_id\":\"naming\",\"arm\":\"O\",\"mode\":\"default\",\"timing_void\":false,\"exe\":\"C:\\\\synthetic\\\\game.exe\",\"args\":\"\",\"data_path\":\"C:\\\\synthetic\\\\data\",\"launcher_version\":\"0.1.0\","
                + "\"t0_qpc\":1000000000,\"qpc_freq\":10000000,\"t0_utc_filetime\":134000000000000000,\"child_pid\":1234,\"g1_delta_ms\":1.0,\"end_ms\":26000,\"exit_reason\":\"finalize\"}",
                new UTF8Encoding(false));

            var ev = new List<string>();
            long seq100 = 0, seq920 = 0;
            void Add(double t, string name, string fields) => ev.Add("{\"t_ms\":" + Di(t) + ",\"src\":\"sparkload\",\"name\":\"" + name + "\",\"fields\":{" + fields + "}}");
            Add(100, "Hello", "\"pid\":1234,\"qpc\":0,\"qpcFreq\":10000000,\"hookVersion\":\"0.1.0-synth\",\"runtime\":\".NET 10 synthetic\"");
            Add(110, "MarkerDef", "\"markerId\":1,\"name\":\"client.boot.main\"");
            Add(111, "MarkerDef", "\"markerId\":2,\"name\":\"client.shapes.entity\"");
            Add(200, "Thread", "\"tid\":920,\"managedId\":53,\"name\":\"\"");
            Add(201, "Thread", "\"tid\":930,\"managedId\":54,\"name\":\"Worker\"");
            Add(500, "Begin", "\"markerId\":1,\"tid\":100,\"seq\":" + (++seq100) + ",\"qpc\":0,\"a\":0,\"b\":0,\"c\":0,\"s\":\"\"");
            Add(9000, "Begin", "\"markerId\":2,\"tid\":920,\"seq\":" + (++seq920) + ",\"qpc\":0,\"a\":0,\"b\":0,\"c\":0,\"s\":\"\"");
            Add(13000, "End", "\"markerId\":2,\"tid\":920,\"seq\":" + (++seq920) + ",\"qpc\":0,\"a\":0,\"b\":0,\"c\":0,\"s\":\"\"");
            Add(26000, "End", "\"markerId\":1,\"tid\":100,\"seq\":" + (++seq100) + ",\"qpc\":0,\"a\":0,\"b\":0,\"c\":0,\"s\":\"\"");
            File.WriteAllLines(Path.Combine(dir, "events.jsonl"), ev, new UTF8Encoding(false));

            var logs = new[] { 500.0, 1500, 5000, 17000, 21000, 25000, 26000 };
            var sbl = new StringBuilder("t_ms,file,offset,len,text\n");
            foreach (var t in logs) sbl.Append(Di(t)).Append(",client-main,0,5,\"line at ").Append(Di(t)).Append("\"\n");
            File.WriteAllText(Path.Combine(dir, "logtail.csv"), sbl.ToString(), new UTF8Encoding(false));

            if (!withSamples) return;
            // Cumulative cpu ms per thread, linear inside its window. tid, name, pid, from, to, rate.
            var th = new (int Tid, string Name, int Pid, double From, double To, double Rate)[]
            {
                (900, "", 777, 1500, 5000, 0.9),
                (910, "", 1234, 5000, 9000, 0.8),
                (920, "", 1234, 9000, 13000, 0.8),
                (930, "Worker", 1234, 13000, 17000, 0.125),
                (100, "", 1234, 21000, 25000, 0.8),
            };
            var sb = new StringBuilder("t_ms,pid,kind,tid,name,cpu_ms,state,wait_reason,ctx,read_bytes,write_bytes,other_bytes,working_set,private_bytes,threads\n");
            for (double t = 0; t <= 26000; t += 500)
            {
                // Process io: 1 GB spread over 17-21 s, a trickle otherwise.
                double rd = t <= 17000 ? t * 10 : (t <= 21000 ? 170000 + (t - 17000) * 250000 : 1000170000 + (t - 21000) * 10);
                sb.Append(Di(t)).Append(",1234,P,,game.exe,").Append(Di(t * 0.1)).Append(",,,,").Append(Di(rd)).Append(",0,0,1,1,5\n");
                sb.Append(Di(t)).Append(",777,P,,dotnet.exe,0,,,,0,0,0,1,1,3\n");
                foreach (var x in th)
                {
                    double cum = Math.Max(0, Math.Min(t, x.To) - x.From) * x.Rate;
                    sb.Append(Di(t)).Append(',').Append(x.Pid).Append(",T,").Append(x.Tid).Append(',').Append(x.Name).Append(',').Append(Di(cum)).Append(",Wait,,0,,,,,,\n");
                }
            }
            File.WriteAllText(Path.Combine(dir, "samples.csv"), sb.ToString(), new UTF8Encoding(false));
        }

        private static void TestSilenceNaming()
        {
            string dir = Dir("naming");
            WriteNamingRun(dir);
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "naming: exit 0", log);
            string md = File.ReadAllText(Path.Combine(dir, "loading.md"));

            Check(Gate(md, "G2") == "G2 uncovered_pct=" + Gate(md, "G2").Split(' ')[1].Substring("uncovered_pct=".Length)
                  + " silences_over_2s=6 named=3 unnamed=3 no_thread_name=1 low_cpu_share=1 idle_or_io=1 FAIL", "naming: G2 line with named, unnamed and split", Gate(md, "G2"));

            // Table rows, one per silence, in order.
            var rows = md.Split('\n').Where(l => l.StartsWith("| 1.50 |", StringComparison.Ordinal) || l.StartsWith("| 5.00 |", StringComparison.Ordinal)
                || l.StartsWith("| 9.00 |", StringComparison.Ordinal) || l.StartsWith("| 13.00 |", StringComparison.Ordinal)
                || l.StartsWith("| 17.00 |", StringComparison.Ordinal) || l.StartsWith("| 21.00 |", StringComparison.Ordinal)).Select(l => l.TrimEnd()).ToList();
            Check(rows.Count == 6, "naming: six silence rows, none dropped", rows.Count.ToString());
            if (rows.Count == 6)
            {
                Check(rows[0].Contains("dotnet.exe pid 777 (other process) tid 900") && rows[0].EndsWith("| process | named |"), "naming: S1 named from its process", rows[0]);
                Check(rows[1].Contains("(unnamed) tid 910") && rows[1].EndsWith("| none | UNNAMED (no_thread_name) |"), "naming: S2 unnamed, no thread name", rows[1]);
                Check(rows[2].Contains("managed thread 53 running client.shapes.entity tid 920") && rows[2].EndsWith("| marker_span | named |"), "naming: S3 named from the marker span", rows[2]);
                Check(rows[3].Contains("Worker tid 930") && rows[3].EndsWith("| samples | UNNAMED (low_cpu_share) |"), "naming: S4 low cpu share", rows[3]);
                Check(rows[4].EndsWith("| UNNAMED (idle_or_io) |") && rows[4].Contains("| io |") && rows[4].Contains("(no cpu in gap)"), "naming: S5 io only", rows[4]);
                Check(rows[5].Contains("main thread (client.boot.main) tid 100") && rows[5].EndsWith("| main_thread | named |"), "naming: S6 main thread named", rows[5]);
            }

            // Unnamed section.
            int u = md.IndexOf("## Unnamed silences", StringComparison.Ordinal);
            Check(u > 0 && u > md.IndexOf("## Silences", StringComparison.Ordinal) && u < md.IndexOf("## GC", StringComparison.Ordinal), "naming: Unnamed silences section sits between Silences and GC");
            string sec = u > 0 ? md.Substring(u, md.IndexOf("## GC", u, StringComparison.Ordinal) - u) : "";
            Check(sec.Split('\n').Count(l => l.StartsWith("### ", StringComparison.Ordinal)) == 3, "naming: three unnamed entries");
            Check(!sec.Contains("### 1.50 s") && !sec.Contains("### 9.00 s") && !sec.Contains("### 21.00 s"), "naming: named silences are not listed as unnamed");
            int s2 = sec.IndexOf("### 5.00 s to 9.00 s", StringComparison.Ordinal);
            string e2 = s2 >= 0 ? sec.Substring(s2, sec.IndexOf("### 13.00 s", s2, StringComparison.Ordinal) - s2) : "";
            Check(e2.Contains("- cause: no_thread_name"), "naming: S2 entry cause", e2);
            Check(e2.Contains("- thread: tid 910, no name known"), "naming: S2 entry thread", e2);
            Check(e2.Contains("- nearest event on thread before: none on this thread") && e2.Contains("- nearest event on thread after: none on this thread"), "naming: S2 no events on thread", e2);
            Check(e2.Contains("- nearest event global before: log client-main: line at 5000 at 5.00 s"), "naming: S2 global before", e2);
            Check(e2.Contains("- nearest event global after: Begin client.shapes.entity (tid 920) at 9.00 s"), "naming: S2 global after", e2);
            int s4 = sec.IndexOf("### 13.00 s to 17.00 s", StringComparison.Ordinal);
            string e4 = s4 >= 0 ? sec.Substring(s4, sec.IndexOf("### 17.00 s", s4, StringComparison.Ordinal) - s4) : "";
            Check(e4.Contains("- cause: low_cpu_share") && e4.Contains("- thread: tid 930, name Worker (samples)") && e4.Contains("(12.5%)"), "naming: S4 entry", e4);
            Check(e4.Contains("- nearest event global before: End client.shapes.entity (tid 920) at 13.00 s") && e4.Contains("- nearest event global after: log client-main: line at 17000 at 17.00 s"), "naming: S4 global events", e4);
            int s5 = sec.IndexOf("### 17.00 s to 21.00 s", StringComparison.Ordinal);
            string e5 = s5 >= 0 ? sec.Substring(s5) : "";
            Check(e5.Contains("- cause: idle_or_io") && e5.Contains("- thread: none (no thread used cpu in the gap)"), "naming: S5 entry cause", e5);
            Check(e5.Contains("- marker spans open in the gap: client.boot.main 4.00 s"), "naming: S5 open marker span", e5);

            // CSV: old columns intact, three appended.
            var csv = CsvReader.Rows(Path.Combine(dir, "loading.csv")).ToList();
            Check(string.Join(",", csv[0].Take(17)) == "run_id,arm,t_start_ms,t_end_ms,dur_ms,side,thread,phase,subphase,mod,x,z,pass,count,bytes,source,lost" && string.Join(",", csv[0].Skip(17)) == "name_source,named,cause", "naming: csv header keeps the 17 old columns and appends three", string.Join(",", csv[0]));
            Check(csv.All(r => r.Count == 20), "naming: every csv row has 20 columns");
            var sil = csv.Skip(1).Where(r => r[7] == "silence").ToList();
            Check(sil.Count == 6, "naming: six silence csv rows", sil.Count.ToString());
            if (sil.Count == 6)
            {
                Check(sil.Select(r => r[17] + "/" + r[18] + "/" + r[19]).SequenceEqual(
                    new[] { "process/1/", "none/0/no_thread_name", "marker_span/1/", "samples/0/low_cpu_share", "none/0/idle_or_io", "main_thread/1/" }),
                    "naming: silence csv name_source, named, cause", string.Join(" ", sil.Select(r => r[17] + "/" + r[18] + "/" + r[19])));
            }
            Check(csv.Skip(1).Where(r => r[7] != "silence").All(r => r[17] == "" && r[18] == "" && r[19] == ""), "naming: non-silence rows leave the new columns empty");

            NoDashes("naming loading.md", Path.Combine(dir, "loading.md"));
            NoDashes("naming loading.csv", Path.Combine(dir, "loading.csv"));

            // Without samples.csv: gate n/a, entries say no_samples, rows still kept.
            string dir2 = Dir("naming-nosamples");
            WriteNamingRun(dir2, false);
            var (rc2, log2) = RunAnalyzer(dir2);
            Check(rc2 == 0, "naming nosamples: exit 0", log2);
            string md2 = File.ReadAllText(Path.Combine(dir2, "loading.md"));
            Check(Gate(md2, "G2").Contains("silences_over_2s=6 named=n/a unnamed=n/a no_thread_name=n/a low_cpu_share=n/a idle_or_io=n/a FAIL"), "naming nosamples: G2 n/a split", Gate(md2, "G2"));
            Check(md2.Contains("- cause: no_samples") && md2.Contains("- nearest event global before:"), "naming nosamples: entries list global events", "");
        }
    }
}
