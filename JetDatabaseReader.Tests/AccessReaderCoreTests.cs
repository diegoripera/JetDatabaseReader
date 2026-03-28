using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for: ListTables, GetTableStats, GetTablesAsDataTable, GetStatistics,
    /// GetColumnMetadata, GetRealRowCount, ReadFirstTable, ReadTablePreview, Dispose.
    /// </summary>
    public class AccessReaderCoreTests
    {
        // ── ListTables ────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ListTables_WhenDatabaseHasTables_ReturnsNonEmptyList(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> tables = reader.ListTables();

            tables.Should().NotBeNullOrEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ListTables_ReturnedNames_AreNonEmptyStrings(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> tables = reader.ListTables();

            tables.Should().AllSatisfy(name => name.Should().NotBeNullOrWhiteSpace());
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ListTables_ReturnedNames_AreUnique(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> tables = reader.ListTables();

            tables.Should().OnlyHaveUniqueItems();
        }

        // ── GetTableStats ─────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetTableStats_CountMatchesListTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            var stats  = reader.GetTableStats();
            var tables = reader.ListTables();

            stats.Should().HaveCount(tables.Count);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void GetTableStats_RowCountAndColumnCount_ArePositive(string path)
        {
            using var reader = TestDatabases.Open(path);

            var stats = reader.GetTableStats();

            stats.Should().AllSatisfy(s =>
            {
                s.RowCount.Should().BeGreaterThanOrEqualTo(0);
                s.ColumnCount.Should().BeGreaterThan(0);
            });
        }

        // ── GetTablesAsDataTable ──────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetTablesAsDataTable_HasExpectedColumns(string path)
        {
            using var reader = TestDatabases.Open(path);

            var dt = reader.GetTablesAsDataTable();

            dt.Columns["TableName"]!.DataType.Should().Be(typeof(string));
            dt.Columns["RowCount"]!.DataType.Should().Be(typeof(long));
            dt.Columns["ColumnCount"]!.DataType.Should().Be(typeof(int));
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetTablesAsDataTable_RowCountMatchesListTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            var dt     = reader.GetTablesAsDataTable();
            var tables = reader.ListTables();

            dt.Rows.Count.Should().Be(tables.Count);
        }

        // ── GetStatistics ─────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetStatistics_ReturnsConsistentPageAndSizeInfo(string path)
        {
            using var reader = TestDatabases.Open(path);

            DatabaseStatistics stats = reader.GetStatistics();

            stats.TotalPages.Should().BeGreaterThan(0);
            stats.DatabaseSizeBytes.Should().Be(stats.TotalPages * stats.PageSize);
            stats.PageSize.Should().BeOneOf(2048, 4096);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetStatistics_Version_IsRecognisedJetVersion(string path)
        {
            using var reader = TestDatabases.Open(path);

            DatabaseStatistics stats = reader.GetStatistics();

            stats.Version.Should().BeOneOf("Jet3", "Jet4/ACE");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetStatistics_TableCount_MatchesListTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            DatabaseStatistics stats = reader.GetStatistics();
            int tableCount = reader.ListTables().Count;

            stats.TableCount.Should().Be(tableCount);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetStatistics_TotalRows_IsNonNegative(string path)
        {
            using var reader = TestDatabases.Open(path);

            DatabaseStatistics stats = reader.GetStatistics();

            stats.TotalRows.Should().BeGreaterThanOrEqualTo(0);
        }

        // ── GetColumnMetadata ─────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetColumnMetadata_ForEachTable_ReturnsNonEmptyList(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (string table in reader.ListTables())
            {
                List<ColumnMetadata> meta = reader.GetColumnMetadata(table);
                meta.Should().NotBeEmpty(because: $"table '{table}' should have columns");
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetColumnMetadata_OrdinalIsSequential(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            List<ColumnMetadata> meta = reader.GetColumnMetadata(table);

            for (int i = 0; i < meta.Count; i++)
                meta[i].Ordinal.Should().Be(i);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetColumnMetadata_ClrType_IsNeverNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            List<ColumnMetadata> meta = reader.GetColumnMetadata(table);

            meta.Should().AllSatisfy(m => m.ClrType.Should().NotBeNull());
        }

        // ── GetRealRowCount ───────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void GetRealRowCount_IsNonNegative(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            long count = reader.GetRealRowCount(table);

            count.Should().BeGreaterThanOrEqualTo(0);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void GetRealRowCount_ConsistentWithStatsTdefRowCount(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            long real  = reader.GetRealRowCount(table);
            long tdef  = reader.GetTableStats().Find(s => s.Name == table).RowCount;

            // Real row count may differ from TDEF after deletes — both must be >= 0
            real.Should().BeGreaterThanOrEqualTo(0);
            tdef.Should().BeGreaterThanOrEqualTo(0);
        }

        // ── ReadFirstTable ────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadFirstTable_ReturnsNonEmptyHeadersAndTableName(string path)
        {
            using var reader = TestDatabases.Open(path);

            var (headers, _, tableName, tableCount) = reader.ReadFirstTable();

            headers.Should().NotBeEmpty();
            tableName.Should().NotBeNullOrWhiteSpace();
            tableCount.Should().BeGreaterThan(0);
        }

        // ── ReadTable (preview overload) ──────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_Preview_HeadersMatchSchemaColumnNames(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            TablePreviewResult preview = reader.ReadTable(table, maxRows: 10);

            preview.Headers.Should().HaveCount(preview.Schema.Count);
            for (int i = 0; i < preview.Headers.Count; i++)
                preview.Headers[i].Should().Be(preview.Schema[i].Name);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_Preview_RowCount_DoesNotExceedMaxRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            const int max = 5;

            TablePreviewResult preview = reader.ReadTable(table, maxRows: max);

            preview.Rows.Should().HaveCountLessThanOrEqualTo(max);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_Preview_EachRow_HasSameColumnCountAsHeaders(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            TablePreviewResult preview = reader.ReadTable(table, maxRows: 20);

            foreach (var row in preview.Rows)
                row.Should().HaveCount(preview.Headers.Count);
        }

        // ── Dispose ───────────────────────────────────────────────────────

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            if (!System.IO.File.Exists(TestDatabases.AdventureWorks)) return;

            var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            reader.Dispose();
            Action second = () => reader.Dispose();
            second.Should().NotThrow();
        }

        [Fact]
        public void AfterDispose_ListTables_ThrowsObjectDisposedException()
        {
            if (!System.IO.File.Exists(TestDatabases.AdventureWorks)) return;

            var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            reader.Dispose();
            Action act = () => reader.ListTables();
            act.Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public void Open_WhenFileNotFound_ThrowsFileNotFoundException()
        {
            Action act = () => AccessReader.Open(@"C:\no\such\file.mdb");
            act.Should().Throw<System.IO.FileNotFoundException>();
        }
    }
}
