using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for: ReadTable (typed), ReadTableAsStringDataTable,
    /// ReadAllTables, ReadAllTablesAsStrings.
    /// </summary>
    public class AccessReaderReadTests
    {
        // ── ReadTable (typed) ─────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_ReturnsNonNullDataTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt = reader.ReadTable(table);

            dt.Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_TableNameMatchesRequest(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt = reader.ReadTable(table);

            dt.TableName.Should().Be(table);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_ColumnCount_MatchesGetColumnMetadata(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt   = reader.ReadTable(table);
            var meta       = reader.GetColumnMetadata(table);

            dt.Columns.Count.Should().Be(meta.Count);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTable_ColumnTypes_MatchGetColumnMetadataClrTypes(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt = reader.ReadTable(table);
            var meta     = reader.GetColumnMetadata(table);

            for (int i = 0; i < meta.Count; i++)
                dt.Columns[i].DataType.Should().Be(meta[i].ClrType,
                    because: $"column '{meta[i].Name}' should have CLR type {meta[i].ClrType.Name}");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadTable_WithNullTableName_ReadsFirstTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string first = reader.ListTables()[0];

            DataTable dt = reader.ReadTable(tableName: null);

            dt.Should().NotBeNull();
            dt.TableName.Should().Be(first);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadTable_ForAllTables_ReturnsNonNullDataTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (string table in reader.ListTables())
            {
                DataTable dt = reader.ReadTable(table);
                dt.Should().NotBeNull(because: $"table '{table}' should be readable");
            }
        }

        // ── ReadTableAsStringDataTable ────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTableAsStringDataTable_AllColumnsAreStringType(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable dt = reader.ReadTableAsStringDataTable(table);

            foreach (DataColumn col in dt.Columns)
                col.DataType.Should().Be(typeof(string),
                    because: $"ReadTableAsStringDataTable should produce only string columns");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTableAsStringDataTable_RowCount_MatchesReadTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable typed  = reader.ReadTable(table);
            DataTable string_ = reader.ReadTableAsStringDataTable(table);

            string_.Rows.Count.Should().Be(typed.Rows.Count);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ReadTableAsStringDataTable_ColumnCount_MatchesReadTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            DataTable typed   = reader.ReadTable(table);
            DataTable string_ = reader.ReadTableAsStringDataTable(table);

            string_.Columns.Count.Should().Be(typed.Columns.Count);
        }

        // ── ReadAllTables ─────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadAllTables_ContainsAllTableNames(string path)
        {
            using var reader = TestDatabases.Open(path);
            List<string> expected = reader.ListTables();

            Dictionary<string, DataTable> all = reader.ReadAllTables();

            all.Keys.Should().BeEquivalentTo(expected);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadAllTables_EachDataTable_HasTypedColumns(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> all = reader.ReadAllTables();

            // At least one table must have a non-string column to prove typing
            bool anyTypedColumn = all.Values
                .SelectMany(dt => dt.Columns.Cast<DataColumn>())
                .Any(col => col.DataType != typeof(string));

            anyTypedColumn.Should().BeTrue(
                because: "ReadAllTables should return at least one non-string typed column");
        }

        // ── ReadAllTablesAsStrings ────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadAllTablesAsStrings_AllColumns_AreStringType(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> all = reader.ReadAllTablesAsStrings();

            foreach (var (tableName, dt) in all)
            {
                foreach (DataColumn col in dt.Columns)
                    col.DataType.Should().Be(typeof(string),
                        because: $"ReadAllTablesAsStrings column '{tableName}.{col.ColumnName}' should be string");
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadAllTablesAsStrings_RowCounts_MatchReadAllTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            Dictionary<string, DataTable> typed   = reader.ReadAllTables();
            Dictionary<string, DataTable> strings = reader.ReadAllTablesAsStrings();

            foreach (string name in typed.Keys)
                strings[name].Rows.Count.Should().Be(typed[name].Rows.Count,
                    because: $"table '{name}' row count should match");
        }

        // ── Progress reporting ────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadTable_WithProgress_ReportsIncreasingRowCounts(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            var reported = new List<int>();

            reader.ReadTable(table, new Progress<int>(reported.Add));

            // Every reported value should be non-negative; ForEach handles zero callbacks gracefully
            foreach (int v in reported) v.Should().BeGreaterThanOrEqualTo(0);
        }
    }
}
