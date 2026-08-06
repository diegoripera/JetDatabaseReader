using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace JetDatabaseReader.Benchmarks
{
    /// <summary>Result of running one scenario N times.</summary>
    internal sealed class Measurement
    {
        public string Scenario = "";
        public string Database = "";
        public double MinMs;
        public double MedianMs;
        public double MeanMs;

        /// <summary>Bytes allocated per iteration — GC throughput, not footprint.</summary>
        public long AllocatedBytes;

        /// <summary>Highest managed heap size observed while the scenario ran, over baseline.</summary>
        public long PeakHeapBytes;

        /// <summary>Heap still held after a full collect, with the result alive. This is the
        /// number that decides whether a 512 MB App Service survives the call.</summary>
        public long RetainedBytes;

        public string Note = "";
        public bool Failed;

        public string Key => $"{Database}|{Scenario}";
    }

    /// <summary>
    /// Minimal stopwatch harness. These operations run in the 10 ms – 60 s range and are
    /// dominated by I/O and page decoding, so per-invocation isolation (BenchmarkDotNet)
    /// buys nothing here — warmup plus a median over N runs is the honest measurement.
    ///
    /// All runs are WARM: a warmup pass pulls the file into the OS page cache first, so the
    /// numbers reflect CPU + syscall cost, not cold-disk seek time. That is the conservative
    /// direction — on a cold cache the page-scanning overhead is strictly worse.
    ///
    /// Timing and memory are measured in separate passes so the heap sampler cannot skew times.
    /// </summary>
    internal static class Harness
    {
        public static Measurement Run(string database, string scenario, int iterations, Action<Result> action)
        {
            var m = new Measurement { Scenario = scenario, Database = System.IO.Path.GetFileName(database) };

            // Warmup — also primes the OS file cache.
            try
            {
                var warm = new Result();
                action(warm);
                m.Note = warm.Note;
            }
            catch (Exception ex)
            {
                m.Failed = true;
                m.Note = ex.GetType().Name + ": " + Truncate(ex.Message, 60);
                return m;
            }

            // ── Pass 1: timing ────────────────────────────────────────────
            var times = new List<double>(iterations);
            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);

            for (int i = 0; i < iterations; i++)
            {
                var r = new Result();
                var sw = Stopwatch.StartNew();
                action(r);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                m.Note = r.Note;
            }

            long allocAfter = GC.GetTotalAllocatedBytes(precise: false);
            m.AllocatedBytes = (allocAfter - allocBefore) / iterations;

            times.Sort();
            m.MinMs = times[0];
            m.MedianMs = times[times.Count / 2];
            m.MeanMs = times.Average();

            // ── Pass 2: memory footprint ──────────────────────────────────
            MeasureMemory(m, action);
            return m;
        }

        private static void MeasureMemory(Measurement m, Action<Result> action)
        {
            Collect();
            long baseline = GC.GetTotalMemory(forceFullCollection: true);

            long peak = baseline;
            using (var stop = new ManualResetEventSlim(false))
            {
                var sampler = new Thread(() =>
                {
                    while (!stop.IsSet)
                    {
                        long now = GC.GetTotalMemory(forceFullCollection: false);
                        if (now > peak) peak = now;
                        Thread.Sleep(1);
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.AboveNormal };

                var r = new Result();
                sampler.Start();
                try
                {
                    action(r);
                }
                finally
                {
                    stop.Set();
                    sampler.Join();
                }

                // Still holding r.Retained: whatever survives a full collect is the real footprint
                // the caller is left carrying.
                long retained = GC.GetTotalMemory(forceFullCollection: true);
                m.RetainedBytes = Math.Max(0, retained - baseline);
                GC.KeepAlive(r.Retained);
            }

            m.PeakHeapBytes = Math.Max(0, peak - baseline);
            Collect();
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>Lets a scenario report what it actually did, so the output is verifiable.</summary>
    internal sealed class Result
    {
        public string Note = "";

        /// <summary>
        /// The scenario's output. Assign it so the harness can measure what the caller is left
        /// holding — a streaming API that yields 200K rows should retain nothing, a DataTable
        /// read of the same table retains all of it.
        /// </summary>
        public object? Retained;
    }
}
