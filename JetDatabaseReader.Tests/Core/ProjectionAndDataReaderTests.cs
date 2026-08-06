using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for column projection and the IDataReader cursor — the two constant-memory
    /// additions. Projection must return exactly the same values as a full read, and the
    /// data reader must agree with StreamRows row for row.
    /// </summary>
    public class ProjectionAndDataReaderTests
    {
        // ── Projection ────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Projection_ReturnsSameValuesAsFullRead(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            List<string> all = reader.GetColumnNames(table);
            if (all.Count < 2) return;

            // Pick the last two columns, reversed, so both projection and ordering are exercised.
            var picked = new[] { all[all.Count - 1], all[all.Count - 2] };
            int lastIdx = all.Count - 1, secondLastIdx = all.Count - 2;

            List<object[]> full = reader.StreamRows(table).Take(200).ToList();
            List<object[]> projected = reader.StreamRows(table, picked, null).Take(200).ToList();

            projected.Should().HaveCount(full.Count);

            for (int r = 0; r < full.Count; r++)
            {
                projected[r].Should().HaveCount(2, "the projection selected two columns");
                projected[r][0].Should().Be(full[r][lastIdx]);
                projected[r][1].Should().Be(full[r][secondLastIdx]);
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Projection_UnknownColumn_Throws(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            Action act = () => reader.StreamRows(table, new[] { "NoSuchColumn" }, null).First();

            act.Should().Throw<ArgumentException>()
               .WithMessage("*NoSuchColumn*");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void Query_Select_ProjectsAndReportsColumns(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            List<string> all = reader.GetColumnNames(table);
            if (all.Count == 0) return;

            var query = reader.Query(table).Select(all[0]).Take(10);

            query.Columns.Should().ContainSingle().Which.Should().Be(all[0]);
            foreach (object[] row in query.Execute())
                row.Should().HaveCount(1);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void ReadTable_WithProjection_HasOnlySelectedColumns(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            List<string> all = reader.GetColumnNames(table);
            if (all.Count == 0) return;

            DataTable dt = reader.ReadTable(table, new[] { all[0] }, null);

            dt.Should().NotBeNull();
            dt!.Columns.Count.Should().Be(1);
            dt.Columns[0].ColumnName.Should().Be(all[0]);
        }

        // ── IDataReader ───────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DataReader_AgreesWithStreamRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            List<object[]> expected = reader.StreamRows(table).Take(100).ToList();

            using AccessDataReader cursor = reader.CreateDataReader(table);
            cursor.FieldCount.Should().Be(reader.GetColumnNames(table).Count);

            int r = 0;
            while (r < expected.Count && cursor.Read())
            {
                for (int c = 0; c < cursor.FieldCount; c++)
                    cursor.GetValue(c).Should().Be(expected[r][c]);
                r++;
            }

            r.Should().Be(expected.Count, "the cursor should yield at least as many rows");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DataReader_LoadsIntoDataTable(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            var dt = new DataTable();
            using (AccessDataReader cursor = reader.CreateDataReader(table))
                dt.Load(cursor);

            dt.Columns.Count.Should().Be(reader.GetColumnNames(table).Count);
            dt.Rows.Count.Should().Be((int)reader.GetRealRowCount(table));
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DataReader_GetOrdinal_IsCaseInsensitive(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            string first = reader.GetColumnNames(table)[0];

            using AccessDataReader cursor = reader.CreateDataReader(table);
            cursor.GetOrdinal(first.ToUpperInvariant()).Should().Be(0);
            cursor.GetOrdinal(first.ToLowerInvariant()).Should().Be(0);

            Action act = () => cursor.GetOrdinal("NoSuchColumn");
            act.Should().Throw<IndexOutOfRangeException>();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DataReader_ReadingBeforeRead_Throws(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = FirstNonEmptyTable(reader);
            if (table == null) return;

            using AccessDataReader cursor = reader.CreateDataReader(table);

            Action act = () => cursor.GetValue(0);
            act.Should().Throw<InvalidOperationException>();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DataReader_UnknownTable_Throws(string path)
        {
            using var reader = TestDatabases.Open(path);

            Action act = () => reader.CreateDataReader("NoSuchTable");
            act.Should().Throw<ArgumentException>();
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static string? FirstNonEmptyTable(AccessReader reader) =>
            reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
    }
}
