using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Regression cover for defects a review pass found in this release's own changes.
    /// Each of these shipped green against the existing suite, which is the point of keeping them.
    /// </summary>
    public class ReviewFindingsTests
    {
        // ── DECIMAL / NUMERIC columns ─────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void DecimalColumns_AreNotEmpty_AndAreActuallyDecimal(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (TableStat stat in reader.GetTableStats().Where(s => s.ColumnCount > 0).Take(8))
            {
                List<ColumnMetadata> meta = reader.GetColumnMetadata(stat.Name);
                List<int> decimals = meta.Select((m, i) => new { m, i })
                                         .Where(x => x.m.ClrType == typeof(decimal))
                                         .Select(x => x.i).ToList();
                if (decimals.Count == 0) continue;

                List<object[]> typed = reader.StreamRows(stat.Name).Take(200).ToList();
                List<string[]> text = reader.StreamRowsAsStrings(stat.Name).Take(200).ToList();

                for (int r = 0; r < typed.Count; r++)
                {
                    foreach (int c in decimals)
                    {
                        object value = typed[r][c];
                        if (value == DBNull.Value)
                        {
                            // A null decimal reads as an empty string, not as text.
                            text[r][c].Should().BeEmpty();
                            continue;
                        }

                        // Returning the formatted text from the decimal reader made every value
                        // arrive as a boxed string in a decimal column, and blanked the string path.
                        value.Should().BeOfType<decimal>(
                            because: $"'{stat.Name}'.{meta[c].Name} is a decimal column");

                        text[r][c].Should().NotBeEmpty(
                            because: $"'{stat.Name}'.{meta[c].Name} has a value, so its text must not be blank");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DecimalColumns_SurviveTheDataTablePath(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (TableStat stat in reader.GetTableStats().Where(s => s.ColumnCount > 0).Take(8))
            {
                if (!reader.GetColumnMetadata(stat.Name).Any(m => m.ClrType == typeof(decimal))) continue;

                // A boxed string in a typeof(decimal) column is exactly what DataTable rejects.
                Action load = () => reader.ReadTable(stat.Name);
                load.Should().NotThrow(because: $"'{stat.Name}' has a decimal column");
            }
        }

        // ── Cancellation actually stops a running read ────────────────────

        // Progress fires once per page and the token is checked at the top of the next iteration,
        // so a single-page table can never observe a cancellation raised from the callback.
        // AdventureWorks' Product spans nineteen pages, which makes this deterministic.
        private const string MultiPageTable = "Product";

        [Fact]
        public async Task ReadTableAsync_CancelledWhileRunning_Stops()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            using var cts = new CancellationTokenSource();

            // Cancelling from the callback signals the token while the read is in flight, which
            // Task.Run's pre-start check cannot account for.
            var progress = new SyncProgress<int>(_ => cts.Cancel());

            Func<Task> act = () => reader.ReadTableAsync(MultiPageTable, null, progress, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>(
                because: "the token must be observed inside the page loop, not only before it");
        }

        [Fact]
        public async Task ReadTableAsStringDataTableAsync_CancelledWhileRunning_Stops()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress<int>(_ => cts.Cancel());

            Func<Task> act = () =>
                reader.ReadTableAsStringDataTableAsync(MultiPageTable, null, progress, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void RejectedFile_DoesNotLeaveTheHandleOpen()
        {
            string junk = Path.Combine(Path.GetTempPath(), $"jdr_leak_{Guid.NewGuid():N}.mdb");
            File.WriteAllBytes(junk, Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray());

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { AccessReader.Open(junk); } catch { /* expected */ }
                }

                // The constructor opens the file before it can decide to reject it. If it does not
                // close it on the way out, every rejected open leaks a handle — and deleting the
                // file here fails, which is how this was found.
                Action delete = () => File.Delete(junk);
                delete.Should().NotThrow(because: "a rejected open must not keep the file locked");
            }
            finally
            {
                try { File.Delete(junk); } catch { }
            }
        }

        // ── A file that is not a database says so ─────────────────────────

        [Fact]
        public void NonJetFile_ReportsAnInvalidFormat_NotEncryption()
        {
            string junk = Path.Combine(Path.GetTempPath(), $"jdr_junk_{Guid.NewGuid():N}.mdb");
            File.WriteAllBytes(junk, Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray());

            try
            {
                Action act = () => AccessReader.Open(junk);

                // Page 2 of junk is not a TDEF, so the encryption check would otherwise claim the
                // file is encrypted — a misleading answer for a file that is not a database.
                act.Should().Throw<InvalidDataException>()
                   .WithMessage("*JET magic signature*");
            }
            finally
            {
                File.Delete(junk);
            }
        }
    }
}
