using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JetDatabaseReader.Benchmarks
{
    /// <summary>
    /// Benchmark harness for JetDatabaseReader.
    ///
    ///   dotnet run -c Release -- --out baseline.tsv
    ///   dotnet run -c Release -- --out after.tsv --compare baseline.tsv
    ///
    /// Options:
    ///   --out &lt;file&gt;      write results as TSV (for before/after comparison)
    ///   --compare &lt;file&gt;  print a delta table against a previous --out file
    ///   --db &lt;path&gt;       benchmark only this database (repeatable)
    ///   --huge            include the 2 GB database (very slow before the page-index fix)
    ///   --iterations &lt;n&gt;  override the iteration count
    /// </summary>
    internal static class Program
    {
        private static int _iterations = 5;

        private static int Main(string[] args)
        {
            string? outFile = null, compareFile = null;
            bool huge = false, diag = false;
            var explicitDbs = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out":        outFile = args[++i]; break;
                    case "--compare":    compareFile = args[++i]; break;
                    case "--db":         explicitDbs.Add(args[++i]); break;
                    case "--huge":       huge = true; break;
                    case "--diag":       diag = true; break;
                    case "--iterations": _iterations = int.Parse(args[++i]); break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return 1;
                }
            }

            List<string> databases = explicitDbs.Count > 0
                ? explicitDbs.Where(File.Exists).ToList()
                : DiscoverDatabases(huge);

            if (databases.Count == 0)
            {
                Console.Error.WriteLine("No benchmark databases found.");
                return 1;
            }

            Console.WriteLine($"JetDatabaseReader benchmark — {_iterations} iterations, warm OS cache");
            Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} cores\n");

            if (diag)
            {
                foreach (string db in databases) PrintDiagnostics(db);
                return 0;
            }

            var results = new List<Measurement>();
            foreach (string db in databases)
                results.AddRange(BenchmarkDatabase(db));

            PrintTable(results);

            if (outFile != null)
            {
                WriteTsv(outFile, results);
                Console.WriteLine($"\nWrote {outFile}");
            }

            if (compareFile != null && File.Exists(compareFile))
                PrintComparison(ReadTsv(compareFile), results);

            return 0;
        }

        // ── Database discovery ────────────────────────────────────────────

        private static List<string> DiscoverDatabases(bool huge)
        {
            var candidates = new List<string>();

            // Test databases copied to the test project's output directory.
            string testBin = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "JetDatabaseReader.Tests", "bin", "Debug", "net8.0"));

            if (Directory.Exists(testBin))
            {
                candidates.Add(Path.Combine(testBin, "AdventureLT2008.mdb"));      // 1.2 MB
                candidates.Add(Path.Combine(testBin, "NorthwindTraders.accdb"));   //  12 MB
            }

            // Local-only large files (not in the repository).
            candidates.Add(@"D:\Diego\Downloads\R3188_20260321-20260327_W_PO.mdb");  //  43 MB
            candidates.Add(@"D:\Diego\Downloads\R419_20260213_D_TR_TPI.mdb");        //  80 MB
            if (huge)
                candidates.Add(@"D:\Diego\Downloads\DB Matrix.accdb");               // 2 GB

            return candidates.Where(File.Exists).ToList();
        }

        /// <summary>
        /// Opens each database, builds the catalog and page index, and reports what the reader
        /// costs to keep alive. This is the per-open-database resident footprint a long-running
        /// service pays, independent of any read.
        /// </summary>
        private static void PrintDiagnostics(string db)
        {
            long sizeMb = new FileInfo(db).Length / (1024 * 1024);
            Console.WriteLine($"── {Path.GetFileName(db)} ({sizeMb} MB)");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalMemory(forceFullCollection: true);

            try
            {
                using var reader = AccessReader.Open(db);
                var tables = reader.ListTables();
                long after = GC.GetTotalMemory(forceFullCollection: true);

                foreach (string line in reader.LastDiagnostics.Split('\n'))
                    if (line.Trim().Length > 0) Console.WriteLine($"   {line.Trim()}");

                Console.WriteLine($"   Tables: {tables.Count}");
                Console.WriteLine($"   Reader resident: {Bytes(after - before)}");

                foreach (LinkedTable link in reader.GetLinkedTables())
                {
                    Console.WriteLine($"      LINK {link.Name,-30} kind={link.Kind} " +
                                      $"foreign='{link.ForeignName}' path='{link.SourcePath}'");
                    Console.WriteLine($"           connect='{link.ConnectionString}'");

                    if (!link.IsAccessDatabase) continue;
                    try
                    {
                        using AccessReader src = reader.OpenLinkedTableSource(link);
                        int n = src.StreamRows(link.ForeignName).Take(5).Count();
                        Console.WriteLine($"           followed OK, read {n} row(s) from the source");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"           follow FAILED: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Read every table so a table that only appears via an overflow catalog row is
                // proven readable, not just listed.
                foreach (TableStat s in reader.GetTableStats())
                {
                    string status;
                    try
                    {
                        int read = reader.StreamRows(s.Name).Take(5).Count();
                        int cols = reader.GetColumnNames(s.Name).Count;
                        status = $"{cols} cols, tdef rowcount {s.RowCount}, read {read} row(s) OK";
                    }
                    catch (Exception ex)
                    {
                        status = $"FAILED: {ex.GetType().Name}: {ex.Message}";
                    }
                    Console.WriteLine($"      {s.Name,-40} {status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   cannot open — {ex.Message}");
            }
            Console.WriteLine();
        }

        // ── Scenarios ─────────────────────────────────────────────────────

        private static List<Measurement> BenchmarkDatabase(string db)
        {
            var results = new List<Measurement>();
            long sizeMb = new FileInfo(db).Length / (1024 * 1024);

            string smallTable, bigTable;
            int tableCount;
            try
            {
                using var probe = AccessReader.Open(db);
                var stats = probe.GetTableStats()
                                 .Where(s => s.ColumnCount > 0)
                                 .OrderBy(s => s.RowCount)
                                 .ToList();
                if (stats.Count == 0)
                {
                    Console.WriteLine($"── {Path.GetFileName(db)} ({sizeMb} MB): no readable tables, skipped");
                    return results;
                }
                tableCount = stats.Count;
                smallTable = stats.First().Name;
                bigTable = stats.Last().Name;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"── {Path.GetFileName(db)} ({sizeMb} MB): cannot open — {ex.Message}");
                return results;
            }

            Console.WriteLine($"── {Path.GetFileName(db)} ({sizeMb} MB, {tableCount} tables; " +
                              $"small='{smallTable}', big='{bigTable}')");

            // 1. Catalog scan only. The page index is built during this pass, so this
            //    scenario is the regression guard: it must NOT get slower.
            results.Add(Harness.Run(db, "Open + ListTables", _iterations, r =>
            {
                using var reader = AccessReader.Open(db);
                var tables = reader.ListTables();
                r.Note = $"{tables.Count} tables";
                r.Retained = tables;
            }));

            // 1b. What a long-lived reader costs to just SIT THERE: page index + LRU cache.
            //     This is the per-open-database resident footprint an Azure App Service carries.
            results.Add(Harness.Run(db, "Idle reader footprint", 1, r =>
            {
                var reader = AccessReader.Open(db);
                var tables = reader.ListTables();
                r.Note = $"{tables.Count} tables, reader held";
                r.Retained = reader;
            }));

            // 2. Full read of the SMALLEST table from a fresh reader.
            //    Before the fix this costs two whole-file scans (catalog + table).
            results.Add(Harness.Run(db, "Open + ReadTable(small)", _iterations, r =>
            {
                using var reader = AccessReader.Open(db);
                var dt = reader.ReadTable(smallTable);
                r.Note = $"{dt?.Rows.Count ?? 0} rows";
                r.Retained = dt;
            }));

            // 3. Same read on an already-warm reader (catalog cached). This isolates the
            //    per-table scan cost — the purest measure of the page-index fix.
            {
                using var reader = AccessReader.Open(db);
                reader.ListTables(); // force the catalog scan out of the measurement
                results.Add(Harness.Run(db, "ReadTable(small) warm", _iterations, r =>
                {
                    var dt = reader.ReadTable(smallTable);
                    r.Note = $"{dt?.Rows.Count ?? 0} rows";
                    r.Retained = dt;
                }));
            }

            // 4. Streaming the largest table on a warm reader. Retains nothing by design —
            //    the contrast against ReadTable is the whole low-memory argument.
            {
                using var reader = AccessReader.Open(db);
                reader.ListTables();
                results.Add(Harness.Run(db, "StreamRows(big) warm", _iterations, r =>
                {
                    int n = 0;
                    foreach (var _ in reader.StreamRows(bigTable)) n++;
                    r.Note = $"{n} rows";
                }));
            }

            // 5. Row counting — pure page scan, no row decoding.
            {
                using var reader = AccessReader.Open(db);
                reader.ListTables();
                results.Add(Harness.Run(db, "GetRealRowCount(big) warm", _iterations, r =>
                {
                    r.Note = $"{reader.GetRealRowCount(bigTable)} rows";
                }));
            }

            // 6. The quadratic case: one whole-file scan per table.
            int allTablesIters = Math.Max(1, _iterations / 2);
            results.Add(Harness.Run(db, "ReadAllTables", allTablesIters, r =>
            {
                using var reader = AccessReader.Open(db);
                var all = reader.ReadAllTables();
                r.Note = $"{all.Count} tables, {all.Values.Sum(t => t?.Rows.Count ?? 0)} rows";
                r.Retained = all;
            }));

            return results;
        }

        // ── Reporting ─────────────────────────────────────────────────────

        private static void PrintTable(List<Measurement> results)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Database",-30} {"Scenario",-25} {"Median",10} {"Alloc",10} {"PeakHeap",10} {"Retained",10}  Note");
            Console.WriteLine(new string('─', 130));

            foreach (var m in results)
            {
                if (m.Failed)
                {
                    Console.WriteLine($"{m.Database,-30} {m.Scenario,-25} {"FAILED",10} {"",10} {"",10} {"",10}  {m.Note}");
                    continue;
                }
                Console.WriteLine($"{m.Database,-30} {m.Scenario,-25} {Ms(m.MedianMs),10} " +
                                  $"{Bytes(m.AllocatedBytes),10} {Bytes(m.PeakHeapBytes),10} " +
                                  $"{Bytes(m.RetainedBytes),10}  {m.Note}");
            }
        }

        private static void PrintComparison(List<Measurement> before, List<Measurement> after)
        {
            var baseline = before.ToDictionary(m => m.Key, m => m);

            Console.WriteLine();
            Console.WriteLine("Comparison vs baseline");
            Console.WriteLine($"{"Database",-30} {"Scenario",-25} {"Before",9} {"After",9} {"Speedup",8} " +
                              $"{"Alloc Δ",9} {"Peak Δ",9} {"Retain Δ",9}");
            Console.WriteLine(new string('─', 122));

            foreach (var m in after)
            {
                if (m.Failed || !baseline.TryGetValue(m.Key, out var b) || b.Failed) continue;

                double speedup = m.MedianMs > 0 ? b.MedianMs / m.MedianMs : 0;

                Console.WriteLine($"{m.Database,-30} {m.Scenario,-25} {Ms(b.MedianMs),9} {Ms(m.MedianMs),9} " +
                                  $"{speedup.ToString("F2", CultureInfo.InvariantCulture) + "x",8} " +
                                  $"{Pct(b.AllocatedBytes, m.AllocatedBytes),9} " +
                                  $"{Pct(b.PeakHeapBytes, m.PeakHeapBytes),9} " +
                                  $"{Pct(b.RetainedBytes, m.RetainedBytes),9}");
            }
        }

        private static string Pct(long before, long after)
        {
            if (before == 0) return after == 0 ? "0%" : "n/a";
            double d = (after - before) * 100.0 / before;
            return d.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Ms(double ms) =>
            ms >= 1000 ? (ms / 1000).ToString("F2", CultureInfo.InvariantCulture) + " s"
                       : ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";

        private static string Bytes(long b) =>
            b >= 1024L * 1024 * 1024 ? (b / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GB"
          : b >= 1024 * 1024        ? (b / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB"
          : b >= 1024               ? (b / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB"
                                    : b + " B";

        // ── TSV persistence ───────────────────────────────────────────────

        private static void WriteTsv(string path, List<Measurement> results)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("Database\tScenario\tMedianMs\tMinMs\tMeanMs\tAllocatedBytes\tPeakHeapBytes\tRetainedBytes\tFailed\tNote");
            foreach (var m in results)
                w.WriteLine($"{m.Database}\t{m.Scenario}\t" +
                            $"{m.MedianMs.ToString("R", CultureInfo.InvariantCulture)}\t" +
                            $"{m.MinMs.ToString("R", CultureInfo.InvariantCulture)}\t" +
                            $"{m.MeanMs.ToString("R", CultureInfo.InvariantCulture)}\t" +
                            $"{m.AllocatedBytes}\t{m.PeakHeapBytes}\t{m.RetainedBytes}\t{m.Failed}\t{m.Note}");
        }

        private static List<Measurement> ReadTsv(string path)
        {
            var list = new List<Measurement>();
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                string[] f = line.Split('\t');
                if (f.Length < 10) continue;
                list.Add(new Measurement
                {
                    Database = f[0],
                    Scenario = f[1],
                    MedianMs = double.Parse(f[2], CultureInfo.InvariantCulture),
                    MinMs = double.Parse(f[3], CultureInfo.InvariantCulture),
                    MeanMs = double.Parse(f[4], CultureInfo.InvariantCulture),
                    AllocatedBytes = long.Parse(f[5]),
                    PeakHeapBytes = long.Parse(f[6]),
                    RetainedBytes = long.Parse(f[7]),
                    Failed = bool.Parse(f[8]),
                    Note = f[9]
                });
            }
            return list;
        }
    }
}
