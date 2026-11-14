using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparkLoad.AnalyzerTests
{
    // Gate-fix regressions (2026-11-14): stale Stats, frame-only hits, G6 Shutdown pairing and unmatched text, seq-gap loss.
    internal static partial class Program
    {
        private static int MarkerId(string name) => Array.IndexOf(Synth.MarkerNames, name) + 1;

        private static string D3(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string BeginEnd(string kind, double t, string marker, int tid, long seq, long a) =>
            "{\"t_ms\":" + D3(t) + ",\"src\":\"sparkload\",\"name\":\"" + kind + "\",\"fields\":{\"markerId\":" + MarkerId(marker) + ",\"tid\":" + tid
            + ",\"seq\":" + seq + ",\"qpc\":0,\"a\":" + a + ",\"b\":0,\"c\":0,\"s\":\"\"}}";

        // Rewrites events.jsonl: drops lines matching `drop`, appends `add`, re-sorts by t_ms (stable).
        private static void EditEvents(string dir, Func<string, bool> drop, IEnumerable<string> add)
        {
            string p = Path.Combine(dir, "events.jsonl");
            var all = File.ReadAllLines(p).Where(l => !drop(l)).Concat(add).ToList();
            double T(string l) { using var d = JsonDocument.Parse(l); return d.RootElement.GetProperty("t_ms").GetDouble(); }
            var sorted = all.Select((l, i) => (l, i, t: T(l))).OrderBy(x => x.t).ThenBy(x => x.i).Select(x => x.l);
            File.WriteAllLines(p, sorted, new UTF8Encoding(false));
        }

        private static string StatsLine(string marker, long hits) =>
            "{\"t_ms\":124400,\"src\":\"sparkload\",\"name\":\"Stats\",\"fields\":{\"marker\":\"" + marker + "\",\"hits\":" + hits + "}}";

        private static void AppendLogRow(string dir, double t, string file, string text)
        {
            string p = Path.Combine(dir, "logtail.csv");
            File.AppendAllText(p, D3(t) + "," + file + ",999999," + text.Length + ",\"" + text.Replace("\"", "\"\"") + "\"\n", new UTF8Encoding(false));
        }

        private static string Fresh(string name)
        {
            var sc = new Scenario { Name = name };
            string dir = Dir(name);
            new Synth(sc).Write(dir);
            return dir;
        }

        private static string RunMd(string dir, string label)
        {
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, label + ": exit 0", log);
            return File.ReadAllText(Path.Combine(dir, "loading.md"));
        }

        private static void TestP2()
        {
            // (p2a) stale Stats snapshot (hits=0) but 125 Begins observed: not ZERO-HIT.
            {
                const string mk = "client.finalize.handle";
                string dir = Fresh("p2a");
                var add = new List<string>();
                for (int k = 0; k < 125; k++)
                {
                    add.Add(BeginEnd("Begin", 1000 + k * 10, mk, 910, 2 * k + 1, 0));
                    add.Add(BeginEnd("End", 1005 + k * 10, mk, 910, 2 * k + 2, 0));
                }
                EditEvents(dir, l => l.Contains("\"name\":\"Stats\"") && l.Contains("\"" + mk + "\""), add.Append(StatsLine(mk, 0)));
                string md = RunMd(dir, "p2a");
                Check(!md.Contains(mk + " [ZERO-HIT]") && Gate(md, "G3").Contains("zero_hit_targets=none"), "p2a: stale Stats hits=0 with 125 Begins is not ZERO-HIT", Gate(md, "G3"));
            }

            // (p2b) frame marker with an ok patch row and Stats hits=0, seen only through Frame/FrameAgg events: not ZERO-HIT.
            {
                string dir = Fresh("p2b");
                string patch = "{\"t_ms\":3003,\"src\":\"sparkload\",\"name\":\"Patch\",\"fields\":{\"marker\":\"frame\",\"target\":\"Vintagestory.Synth::frame\",\"status\":\"ok\",\"detail\":\"\"}}";
                EditEvents(dir, l => false, new[] { patch, StatsLine("frame", 0) });
                string md = RunMd(dir, "p2b");
                Check(!md.Contains("frame [ZERO-HIT]") && Gate(md, "G3").Contains("zero_hit_targets=none"), "p2b: frame hit via Frame/FrameAgg only is not ZERO-HIT", Gate(md, "G3"));
            }

            // (p2c) marker phase 9 and log Shutdown 20 ms apart: G6 pairs them and passes.
            {
                string dir = Fresh("p2c");
                EditEvents(dir, l => false, new[] { BeginEnd("Begin", 125500, "server.phase", 911, 1, 9), BeginEnd("End", 125501, "server.phase", 911, 2, 9) });
                AppendLogRow(dir, 125520, "server-main", "12:02:05 [Notification] Entering runphase Shutdown");
                string md = RunMd(dir, "p2c");
                string g6 = Gate(md, "G6");
                Check(g6.EndsWith("PASS", StringComparison.Ordinal) && g6.Contains("phases=9") && !g6.Contains("unmatched="), "p2c: G6 pairs phase 9 with log Shutdown", g6);
            }

            // (p2d) marker phase 9 without a log line: G6 FAIL and the gate text names the unmatched row.
            {
                string dir = Fresh("p2d");
                EditEvents(dir, l => false, new[] { BeginEnd("Begin", 125500, "server.phase", 911, 1, 9), BeginEnd("End", 125501, "server.phase", 911, 2, 9) });
                string md = RunMd(dir, "p2d");
                string g6 = Gate(md, "G6");
                Check(g6.EndsWith("FAIL", StringComparison.Ordinal) && g6.Contains("unmatched=phase9(no log line)"), "p2d: G6 unmatched row fails and is printed", g6);
            }

            // (p2e) wd trace shape: one tid, 23 contiguous seq gaps totalling 81,740 -> lost=81740 and G3 FAIL.
            {
                string dir = Fresh("p2e");
                var add = new List<string>();
                long seq = 0;
                int gaps = 0;
                long gapTotal = 0;
                for (int seg = 0; seg < 24; seg++)
                {
                    for (int k = 0; k < 5; k++)
                    {
                        double t = 2000 + (seg * 5 + k) * 20;
                        add.Add(BeginEnd("Begin", t, "client.shapes.item", 912, ++seq, 0));
                        add.Add(BeginEnd("End", t + 5, "client.shapes.item", 912, ++seq, 0));
                    }
                    if (seg < 23)
                    {
                        long g = seg < 22 ? 3554 : 3552;
                        seq += g; gapTotal += g; gaps++;
                    }
                }
                Check(gaps == 23 && gapTotal == 81740, "p2e: generator makes 23 gaps totalling 81740", gaps + " " + gapTotal);
                EditEvents(dir, l => false, add);
                string md = RunMd(dir, "p2e");
                string g3 = Gate(md, "G3");
                Check(g3.Contains("lost=81740") && g3.EndsWith("FAIL", StringComparison.Ordinal), "p2e: 23 contiguous gaps give lost=81740 and G3 FAIL", g3);
            }
        }
    }
}
