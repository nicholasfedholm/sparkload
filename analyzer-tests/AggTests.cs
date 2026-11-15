using System;
using System.Linq;

namespace SparkLoad.AnalyzerTests
{
    // Source-side aggregation (2026-11-15): hook event 11 Agg for suppressed column.load calls and first-chance
    // exception counts. Old traces (Stats column.load.suppressed, runtime ExceptionThrown) must report as before.
    internal static partial class Program
    {
        private static string AggLine(double t, string key, long count, double sumMs, double maxMs) =>
            "{\"t_ms\":" + D3(t) + ",\"src\":\"sparkload\",\"name\":\"Agg\",\"fields\":{\"key\":\"" + key + "\",\"count\":" + count
            + ",\"sumTicks\":0,\"maxTicks\":0,\"sum_ms\":" + D3(sumMs) + ",\"max_ms\":" + D3(maxMs) + "}}";

        private static string Line(string md, string prefix) =>
            md.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? "";

        private static void TestAgg()
        {
            // (agg-a) new hook: column Agg (last value per column wins) and exception Agg with no runtime exception events.
            {
                string dir = Fresh("agg-a");
                EditEvents(dir, l => l.Contains("\"name\":\"ExceptionThrown\""), new[]
                {
                    AggLine(30000, "column.load 3 4", 16, 2.5, 0.4),
                    AggLine(124400, "column.load 3 4", 50, 7.25, 0.9),
                    AggLine(124400, "column.load -1 7", 8, 1.0, 0.2),
                    AggLine(4000, "exception System.FormatException", 4, 0, 0),
                    AggLine(124400, "exception System.FormatException", 9, 0, 0),
                    AggLine(124400, "exception System.IO.IOException", 2, 0, 0),
                });
                string md = RunMd(dir, "agg-a");
                string col = Line(md, "- column.load detail:");
                Check(col.Contains("left out 58 calls over 2 columns (hook Agg; they took 0.01 s in total, longest 0.9 ms)"),
                    "agg-a: Columns line counts the last Agg per column with total and longest ms", col);
                string ex = Line(md, "- assembly loads:");
                Check(ex.EndsWith("exceptions thrown: 11 (first-chance, counted by the hook)", StringComparison.Ordinal),
                    "agg-a: exceptions come from the hook Agg when the trace has no runtime exception events", ex);
            }

            // (agg-b) runtime ExceptionThrown events present (deep or an older capture): they win over the hook counts.
            {
                string dir = Fresh("agg-b");
                EditEvents(dir, l => false, new[] { AggLine(124400, "exception System.FormatException", 9, 0, 0) });
                string md = RunMd(dir, "agg-b");
                string ex = Line(md, "- assembly loads:");
                Check(ex.EndsWith("exceptions thrown: 3", StringComparison.Ordinal), "agg-b: runtime exception events win, no hook mark", ex);
            }

            // (agg-c) old hook: Stats column.load.suppressed keeps the old wording.
            {
                string dir = Fresh("agg-c");
                EditEvents(dir, l => false, new[] { StatsLine("column.load.suppressed 3 4", 40) });
                string md = RunMd(dir, "agg-c");
                string col = Line(md, "- column.load detail:");
                Check(col.Contains("left out 40 calls over 1 columns (Stats column.load.suppressed). Gap figures"),
                    "agg-c: old Stats column.load.suppressed report is unchanged", col);
            }
        }
    }
}
