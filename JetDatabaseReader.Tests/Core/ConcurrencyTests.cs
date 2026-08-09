using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Concurrent use of a single reader. Caching one open AccessReader and serving requests from
    /// it is the natural pattern in a web app, so independent operations on one instance must be
    /// safe. Seek and Read against the shared FileStream are two calls, and without a lock two
    /// threads interleave them and each receives bytes belonging to the other's page — which
    /// surfaces as wrong values or malformed rows rather than an exception.
    /// </summary>
    public class ConcurrencyTests
    {
        private const int Threads = 8;

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ConcurrentStreamRows_OnOneReader_AllThreadsAgree(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
            if (table == null) return;

            // Single-threaded reference result.
            List<object[]> expected = reader.StreamRows(table).ToList();

            var failures = new ConcurrentQueue<string>();

            Parallel.For(0, Threads, t =>
            {
                try
                {
                    List<object[]> actual = reader.StreamRows(table).ToList();

                    if (actual.Count != expected.Count)
                    {
                        failures.Enqueue($"thread {t}: {actual.Count} rows, expected {expected.Count}");
                        return;
                    }

                    for (int r = 0; r < expected.Count; r++)
                        for (int c = 0; c < expected[r].Length; c++)
                            if (!Equals(actual[r][c], expected[r][c]))
                            {
                                failures.Enqueue(
                                    $"thread {t}: row {r} col {c} = '{actual[r][c]}', expected '{expected[r][c]}'");
                                return;
                            }
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"thread {t}: {ex.GetType().Name}: {ex.Message}");
                }
            });

            failures.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ConcurrentMixedOperations_OnOneReader_DoNotCorruptEachOther(string path)
        {
            using var reader = TestDatabases.Open(path);
            List<string> tables = reader.ListTables();
            if (tables.Count == 0) return;

            int expectedTables = tables.Count;
            var byTable = tables.ToDictionary(t => t, t => reader.GetRealRowCount(t));

            var failures = new ConcurrentQueue<string>();

            // Different operations against different tables, all sharing one FileStream.
            Parallel.For(0, Threads, t =>
            {
                try
                {
                    string table = tables[t % tables.Count];

                    switch (t % 4)
                    {
                        case 0:
                            reader.ListTables().Count.Should().Be(expectedTables);
                            break;
                        case 1:
                            reader.GetRealRowCount(table).Should().Be(byTable[table]);
                            break;
                        case 2:
                            DataTable dt = reader.ReadTable(table);
                            dt!.Rows.Count.Should().Be((int)byTable[table]);
                            break;
                        default:
                            reader.StreamRows(table).Count().Should().Be((int)byTable[table]);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"thread {t}: {ex.GetType().Name}: {ex.Message}");
                }
            });

            failures.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ConcurrentDataReaders_OnOneReader_EachSeesFullTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
            if (table == null) return;

            int expected = (int)reader.GetRealRowCount(table);
            var failures = new ConcurrentQueue<string>();

            // Each cursor has its own reusable row buffer; only the FileStream is shared.
            Parallel.For(0, Threads, t =>
            {
                try
                {
                    int n = 0;
                    using AccessDataReader cursor = reader.CreateDataReader(table);
                    while (cursor.Read()) n++;

                    if (n != expected) failures.Enqueue($"thread {t}: {n} rows, expected {expected}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"thread {t}: {ex.GetType().Name}: {ex.Message}");
                }
            });

            failures.Should().BeEmpty();
        }
    }
}
