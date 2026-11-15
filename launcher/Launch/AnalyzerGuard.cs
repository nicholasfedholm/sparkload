using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SparkLoad.Launch
{
    /// <summary>
    /// Memory ceiling for "sparkload analyze". Two layers: the process puts itself in a job object with a process
    /// memory limit (allocations past it fail, so the analysis stops with OutOfMemoryException), and a monitor thread
    /// ends the process with a clear message and exit code 7 when private bytes pass 90% of the ceiling, before the
    /// hard limit can fail an allocation somewhere less tidy. Nothing is written after the ceiling is hit.
    /// </summary>
    internal static class AnalyzerGuard
    {
        public const int ExitCeiling = 7;
        public const long DefaultMb = 4096;

        private static Thread? _monitor;
        private static volatile bool _stop;

        /// <summary>Returns a short status line for the log.</summary>
        public static string Arm(long maxMb)
        {
            if (maxMb <= 0) return "analyzer memory ceiling: off";
            ulong bytes = (ulong)maxMb << 20;
            string job;
            IntPtr h = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (h == IntPtr.Zero) job = "job object not created";
            else if (!Launcher.SetJobMemoryLimit(h, bytes)) job = "job limit not set";
            else if (!Native.AssignProcessToJobObject(h, Process.GetCurrentProcess().Handle)) job = "job not assigned (monitor only)";
            else job = "job limit set";
            long trip = (long)(bytes * 0.9);
            _stop = false;
            _monitor = new Thread(() => Watch(trip, maxMb)) { IsBackground = true, Name = "sparkload-analyzer-guard" };
            _monitor.Start();
            return "analyzer memory ceiling: " + maxMb.ToString(CultureInfo.InvariantCulture) + " MB (" + job + ", monitor at 90%)";
        }

        public static void Disarm() { _stop = true; }

        private static void Watch(long tripBytes, long maxMb)
        {
            using var p = Process.GetCurrentProcess();
            while (!_stop)
            {
                try
                {
                    p.Refresh();
                    long priv = p.PrivateMemorySize64;
                    if (priv > tripBytes)
                    {
                        Fail("private bytes " + (priv >> 20).ToString(CultureInfo.InvariantCulture) + " MB passed 90% of --max-analyzer-mb " + maxMb.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception) { }
                Thread.Sleep(250);
            }
        }

        public static void Fail(string why)
        {
            try
            {
                Console.Error.WriteLine("sparkload analyze: MEMORY CEILING HIT: " + why + ". Analysis aborted, no report written. Rerun with a higher --max-analyzer-mb only when RAM allows.");
                Console.Error.Flush();
            }
            catch { }
            Environment.Exit(ExitCeiling);
        }
    }
}
