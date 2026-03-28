using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for all async methods:
    /// ListTablesAsync, ReadTableAsync, GetStatisticsAsync, ReadAllTablesAsync, ReadAllTablesAsStringsAsync.
    /// </summary>
    public class AccessReaderAsyncTests
    {
        // ── ListTablesAsync ───────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task ListTablesAsync_ReturnsNonEmptyList(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> tables = await reader.ListTablesAsync();

            tables.Should().NotBeNullOrEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task ListTablesAsync_MatchesSyncListTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> sync  = reader.ListTables();
            List<string> async_ = await reader.ListTablesAsync();

            async_.Should().BeEquivalentTo(sync);
        }

        // ── ReadTableAsync ────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task ReadTableAsync_ReturnsNonNullDataTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt = await reader.ReadTableAsync(table);

            dt.Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task ReadTableAsync_ColumnTypes_AreTyped(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            var meta = reader.GetColumnMetadata(table);

            DataTable dt = await reader.ReadTableAsync(table);

            for (int i = 0; i < meta.Count; i++)
                dt.Columns[i].DataType.Should().Be(meta[i].ClrType);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task ReadTableAsync_RowCount_MatchesSyncReadTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable syncDt  = reader.ReadTable(table);
            DataTable asyncDt = await reader.ReadTableAsync(table);

            asyncDt.Rows.Count.Should().Be(syncDt.Rows.Count);
        }

        // ── GetStatisticsAsync ────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public async Task GetStatisticsAsync_MatchesSyncGetStatistics(string path)
        {
            using var reader = TestDatabases.Open(path);

            DatabaseStatistics sync  = reader.GetStatistics();
            DatabaseStatistics async_ = await reader.GetStatisticsAsync();

            async_.TotalPages.Should().Be(sync.TotalPages);
            async_.TableCount.Should().Be(sync.TableCount);
            async_.Version.Should().Be(sync.Version);
        }

        // ── ReadAllTablesAsync ────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadAllTablesAsync_ContainsAllTableNames(string path)
        {
            using var reader = TestDatabases.Open(path);
            List<string> expected = reader.ListTables();

            Dictionary<string, DataTable> all = await reader.ReadAllTablesAsync();

            all.Keys.Should().BeEquivalentTo(expected);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadAllTablesAsync_RowCounts_MatchSyncReadAllTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> sync  = reader.ReadAllTables();
            Dictionary<string, DataTable> async_ = await reader.ReadAllTablesAsync();

            foreach (string name in sync.Keys)
                async_[name].Rows.Count.Should().Be(sync[name].Rows.Count,
                    because: $"table '{name}' row count should match");
        }

        // ── ReadAllTablesAsStringsAsync ────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadAllTablesAsStringsAsync_AllColumns_AreStringType(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> all = await reader.ReadAllTablesAsStringsAsync();

            foreach (var (_, dt) in all)
            foreach (DataColumn col in dt.Columns)
                col.DataType.Should().Be(typeof(string));
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public async Task ReadAllTablesAsStringsAsync_RowCounts_MatchReadAllTablesAsync(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> typed   = await reader.ReadAllTablesAsync();
            Dictionary<string, DataTable> strings = await reader.ReadAllTablesAsStringsAsync();

            foreach (string name in typed.Keys)
                strings[name].Rows.Count.Should().Be(typed[name].Rows.Count);
        }
    }
}
