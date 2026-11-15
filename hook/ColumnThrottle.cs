using System;
using System.Diagnostics;
using System.Threading;

namespace SparkLoad.Hook
{
    // column.load throttle and source-side aggregation. The server chunk thread calls
    // loadOrGenerateChunkColumn_OnChunkThread tens of thousands of times per column (59.4M spans on 3,024 columns
    // in one Earth run). Per (x,z) column the first KeepFirst calls always emit a span, and so does any later call
    // that takes at least one millisecond (its Begin is written late, from the postfix, with the QPC taken in the
    // prefix). The rest are aggregated per column: count, total ticks and max ticks of the calls that were not
    // emitted. The summary leaves as an Agg event (id 11) keyed "column.load <x> <z>": at each power-of-two call
    // count past KeepFirst, and for every column again in Core.StatsAll. The last Agg per column holds the totals.
    // Older hooks sent only the count, as Stats "column.load.suppressed <x> <z>"; the analyzer reads both. The suppressed path allocates nothing.
    internal static class ColumnThrottle
    {
        public const int KeepFirst = 4;
        public const string AggPrefix = "column.load";

        private const int Cap = 1 << 16;
        private const int Mask = Cap - 1;
        private const int MaxProbe = 64;
        private const long Empty = long.MinValue;

        private static readonly long[] Keys = NewKeys();
        private static readonly int[] Seen = new int[Cap];
        private static readonly int[] Emitted = new int[Cap];
        private static readonly long[] SumTicks = new long[Cap];
        private static readonly long[] MaxTicks = new long[Cap];

        // Spans at least this long are always emitted (1 ms).
        public static readonly long SlowTicks = Math.Max(1, Stopwatch.Frequency / 1000);

        private static long[] NewKeys()
        {
            var k = new long[Cap];
            for (int i = 0; i < k.Length; i++) k[i] = Empty;
            return k;
        }

        // Slot of the column, claiming one when new. -1 when the table is crowded: that column is never throttled.
        public static int Slot(int x, int z)
        {
            long key = ((long)x << 32) | (uint)z;
            if (key == Empty) return -1;
            int i = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 48);
            for (int p = 0; p < MaxProbe; p++)
            {
                int s = (i + p) & Mask;
                long k = Volatile.Read(ref Keys[s]);
                if (k == key) return s;
                if (k == Empty)
                {
                    long prev = Interlocked.CompareExchange(ref Keys[s], key, Empty);
                    if (prev == Empty || prev == key) return s;
                }
            }
            return -1;
        }

        // Called from the prefix for every call. True = emit the Begin now.
        public static bool Admit(int slot, int x, int z)
        {
            int n = Interlocked.Increment(ref Seen[slot]);
            if (n <= KeepFirst)
            {
                Interlocked.Increment(ref Emitted[slot]);
                return true;
            }
            if ((n & (n - 1)) == 0) Report(slot, x, z);
            return false;
        }

        // A suppressed call that turned out slow: its span is emitted after all.
        public static void LateEmitted(int slot)
        {
            if (slot >= 0) Interlocked.Increment(ref Emitted[slot]);
        }

        // A suppressed call that stayed fast: its duration goes into the column's totals.
        public static void Fast(int slot, long ticks)
        {
            if (slot < 0) return;
            Interlocked.Add(ref SumTicks[slot], ticks);
            long m = Volatile.Read(ref MaxTicks[slot]);
            while (ticks > m)
            {
                long prev = Interlocked.CompareExchange(ref MaxTicks[slot], ticks, m);
                if (prev == m) break;
                m = prev;
            }
        }

        public static long Suppressed(int slot)
        {
            long d = (long)Volatile.Read(ref Seen[slot]) - Volatile.Read(ref Emitted[slot]);
            return d < 0 ? 0 : d;
        }

        private static void Report(int slot, int x, int z)
        {
            try
            {
                SparkLoadSource s = Core.Src;
                if (s != null && s.IsEnabled())
                    s.Agg(AggPrefix + " " + x.ToString() + " " + z.ToString(), Suppressed(slot), Volatile.Read(ref SumTicks[slot]), Volatile.Read(ref MaxTicks[slot]));
            }
            catch { Core.Fault(); }
        }

        // One Agg event per column called more than KeepFirst times (the last value per column is the total; a
        // mid-run report can count a still-running call as suppressed, without its ticks). Low rate: StatsAll runs a
        // few times per run.
        public static void FlushStats(SparkLoadSource s)
        {
            for (int i = 0; i < Cap; i++)
            {
                long key = Volatile.Read(ref Keys[i]);
                if (key == Empty) continue;
                if (Volatile.Read(ref Seen[i]) <= KeepFirst) continue;
                s.Agg(AggPrefix + " " + ((int)(key >> 32)).ToString() + " " + ((int)(key & 0xFFFFFFFFL)).ToString(),
                    Suppressed(i), Volatile.Read(ref SumTicks[i]), Volatile.Read(ref MaxTicks[i]));
            }
        }
    }
}
