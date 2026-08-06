using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for Test_Autonumber.accdb — a single-table database with:
    ///   Id        Autonumber (Long Integer)
    ///   Number1   Short Text  — values look like integers but the column type is Text
    ///   Number2   Short Text  — values look like integers but the column type is Text
    ///
    /// Row 8 of Number1 contains "78/465", an intentional non-numeric string that
    /// would be lost if the column were mis-read as Numeric instead of Text.
    /// </summary>
    public class AutonumberStringColumnsTests
    {
        private static readonly string DbPath = TestDatabases.AutonumberDb;
        private const string Table = "TableTest";

        // ── Schema ────────────────────────────────────────────────────────

        [Fact]
        public void Id_ColumnType_IsInt()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            var meta = reader.GetColumnMetadata(Table);
            var idCol = meta.Single(m => m.Name == "Id");

            idCol.ClrType.Should().Be(typeof(int),
                because: "Id is an Autonumber (Long Integer) column");
        }

        [Fact]
        public void Number1_ColumnType_IsString_NotNumeric()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            var meta = reader.GetColumnMetadata(Table);
            var col = meta.Single(m => m.Name == "Number1");

            col.ClrType.Should().Be(typeof(string),
                because: "Number1 is a Short Text column even though its values look like numbers");
        }

        [Fact]
        public void Number2_ColumnType_IsString_NotNumeric()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            var meta = reader.GetColumnMetadata(Table);
            var col = meta.Single(m => m.Name == "Number2");

            col.ClrType.Should().Be(typeof(string),
                because: "Number2 is a Short Text column even though its values look like numbers");
        }

        // ── Row count ─────────────────────────────────────────────────────

        [Fact]
        public void TableTest_RowCount_IsTen()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            DataTable dt = reader.ReadTable(Table);

            dt.Rows.Count.Should().Be(10);
        }

        // ── Non-numeric string preservation ───────────────────────────────

        /// <summary>
        /// Row 8 has Number1 = "78/465" — a value that cannot be parsed as a number.
        /// If the column were incorrectly read as Numeric the value would be empty or corrupt.
        /// </summary>
        [Fact]
        public void Number1_NonNumericValue_IsPreservedAsString()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            var rows = reader.ReadTableAsStrings(Table, int.MaxValue).Rows;
            var number1Values = rows.Select(r => r[1]).ToList();

            number1Values.Should().Contain("78/465",
                because: "row 8 stores the literal text '78/465' which only survives if Number1 is read as Text");
        }

        // ── Known row values ──────────────────────────────────────────────

        [Fact]
        public void FirstRow_Values_MatchKnownData()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            DataTable dt = reader.ReadTable(Table);
            DataRow first = dt.Rows[0];

            first["Id"].Should().Be(1,        because: "Id is an autonumber starting at 1");
            first["Number1"].Should().Be("124", because: "Number1 in row 1 is the text '124'");
            first["Number2"].Should().Be("124", because: "Number2 in row 1 is the text '124'");
        }

        [Fact]
        public void Number1AndNumber2_AllValues_AreNonEmpty()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            var rows = reader.ReadTableAsStrings(Table, int.MaxValue).Rows;

            rows.Should().AllSatisfy(r =>
            {
                r[1].Should().NotBeNullOrEmpty(because: "Number1 should have a value in every row");
                r[2].Should().NotBeNullOrEmpty(because: "Number2 should have a value in every row");
            });
        }
    }
}
