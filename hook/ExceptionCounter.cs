using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace SparkLoad.Hook
{
    // First-chance exception counts per exception type, aggregated in the game process. The runtime's Exception
    // keyword wrote one event per throw (1.3M to 1.9M per trace, 97.6% of one capture's events); the launcher now
    // leaves that keyword off outside --deep, and this counter reports the same numbers as Agg events keyed
    // "exception <full type name>" (count only; sum and max are 0). A type is reported at each power-of-two count
    // and every type again in Core.StatsAll, so the last Agg per type holds the total. The handler never throws,
    // ignores exceptions raised while it runs, and allocates only when a type is first seen or reported.
    internal static class ExceptionCounter
    {
        public const string AggPrefix = "exception";

        private sealed class Box { public long N; }

        private static readonly ConcurrentDictionary<Type, Box> Counts = new ConcurrentDictionary<Type, Box>();
        [ThreadStatic] private static bool inHandler;
        private static int installed;

        public static void Install()
        {
            if (Interlocked.Exchange(ref installed, 1) != 0) return;
            try { AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; }
            catch { Core.Fault(); }
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (inHandler) return;
            inHandler = true;
            try
            {
                Exception ex = e?.Exception;
                if (ex == null) return;
                Type t = ex.GetType();
                if (!Counts.TryGetValue(t, out Box b)) b = Counts.GetOrAdd(t, _ => new Box());
                long n = Interlocked.Increment(ref b.N);
                if ((n & (n - 1)) == 0) Report(t, n);
            }
            catch { Core.Fault(); }
            finally { inHandler = false; }
        }

        private static void Report(Type t, long n)
        {
            SparkLoadSource s = Core.Src;
            if (s != null && s.IsEnabled()) s.Agg(AggPrefix + " " + (t.FullName ?? t.Name), n, 0, 0);
        }

        // Every type seen so far, with its current total. Called from Core.StatsAll (a few times per run).
        public static void FlushStats(SparkLoadSource s)
        {
            bool was = inHandler;
            inHandler = true; // exceptions inside the flush are not counted
            try
            {
                foreach (var kv in Counts) s.Agg(AggPrefix + " " + (kv.Key.FullName ?? kv.Key.Name), Interlocked.Read(ref kv.Value.N), 0, 0);
            }
            finally { inHandler = was; }
        }
    }
}
