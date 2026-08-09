using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Sharing one database file across independent readers and OS handles — the IIS web-garden
    /// and multi-instance App Service case. Each AccessReader owns its own FileStream, so several
    /// readers in one process exercise the same OS file-sharing path several processes would.
    /// </summary>
    public class MultiProcessTests
    {
        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void IndependentReaders_OnSameFile_AllSucceed(string path)
        {
            string table;
            long expected;
            using (var probe = TestDatabases.Open(path))
            {
                table = probe.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
                if (table == null) return;
                expected = probe.GetRealRowCount(table);
            }

            var failures = new ConcurrentQueue<string>();

            // Eight separate readers, so eight separate OS file handles, all reading at once.
            Parallel.For(0, 8, i =>
            {
                try
                {
                    using var reader = TestDatabases.Open(path);
                    long n = reader.GetRealRowCount(table);
                    if (n != expected) failures.Enqueue($"reader {i}: {n} rows, expected {expected}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"reader {i}: {ex.GetType().Name}: {ex.Message}");
                }
            });

            failures.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Open_WhileAnotherHandleHasWriteAccess_Succeeds(string path)
        {
            // Mimics Microsoft Access holding the file open for writing. The reader's default
            // FileShare.ReadWrite is what makes this work; FileShare.Read would throw here.
            using var foreignWriter = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            using var reader = TestDatabases.Open(path);

            reader.ListTables().Should().NotBeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Open_DoesNotBlockOtherReaders(string path)
        {
            using var reader = TestDatabases.Open(path);
            reader.ListTables();

            // While we hold the database open, another handle must still be able to read it.
            Action openAgain = () =>
            {
                using var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                other.ReadByte();
            };

            openAgain.Should().NotThrow();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Refresh_ClearsCachesAndStillReadsCorrectly(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
            if (table == null) return;

            var before = reader.StreamRows(table).ToList();

            reader.Refresh();

            var after = reader.StreamRows(table).ToList();

            after.Should().HaveCount(before.Count);
            for (int r = 0; r < before.Count; r++)
                after[r].Should().Equal(before[r]);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Refresh_IsSafeWhileOtherThreadsRead(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
            if (table == null) return;

            long expected = reader.GetRealRowCount(table);
            var failures = new ConcurrentQueue<string>();

            Parallel.For(0, 8, i =>
            {
                try
                {
                    if (i % 4 == 0) reader.Refresh();

                    long n = reader.GetRealRowCount(table);
                    if (n != expected) failures.Enqueue($"worker {i}: {n} rows, expected {expected}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"worker {i}: {ex.GetType().Name}: {ex.Message}");
                }
            });

            failures.Should().BeEmpty();
        }
    }
}
