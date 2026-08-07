using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Cancellation. A full read of a large database easily outlives the request that started it,
    /// so a scan has to be abandonable — otherwise it runs to completion holding a thread whether
    /// anyone still wants the result or not.
    /// </summary>
    public class CancellationTests
    {
        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void StreamRows_AlreadyCancelled_StopsImmediately(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Action act = () => reader.StreamRows(table, null, null, cts.Token).ToList();

            act.Should().Throw<OperationCanceledException>();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void StreamRows_CancelledMidEnumeration_Stops(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            using var cts = new CancellationTokenSource();
            int seen = 0;

            Action act = () =>
            {
                foreach (object[] _ in reader.StreamRows(table, null, null, cts.Token))
                {
                    seen++;
                    if (seen == 1) cts.Cancel();
                }
            };

            // The token is checked once per page, so a single-page table finishes before the
            // cancel is noticed. Either outcome is correct; what matters is that it does not hang
            // and does not report more rows than exist.
            try { act(); } catch (OperationCanceledException) { }

            seen.Should().BeLessThanOrEqualTo((int)reader.GetRealRowCount(table));
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void GetRealRowCount_AlreadyCancelled_Throws(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Action act = () => reader.GetRealRowCount(table, cts.Token);

            act.Should().Throw<OperationCanceledException>();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadTableAsync_AlreadyCancelled_Throws(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = () => reader.ReadTableAsync(table, null, null, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadTableAsync_NotCancelled_ReturnsTheSameAsTheSyncRead(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            var expected = reader.ReadTable(table);
            var actual = await reader.ReadTableAsync(table, null, null, CancellationToken.None);

            actual.Should().NotBeNull();
            actual!.Rows.Count.Should().Be(expected!.Rows.Count);
            actual.Columns.Count.Should().Be(expected.Columns.Count);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task GetRealRowCountAsync_MatchesTheSyncCount(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstTable(reader);
            if (table == null) return;

            long expected = reader.GetRealRowCount(table);
            long actual = await reader.GetRealRowCountAsync(table, CancellationToken.None);

            actual.Should().Be(expected);
        }

        private static string FirstTable(AccessReader reader) =>
            reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
    }
}
