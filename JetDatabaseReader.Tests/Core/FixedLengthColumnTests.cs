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
    /// Fixed-length Text (DDL <c>CHAR(n)</c>) and Binary columns, which JET stores in the row's
    /// fixed area rather than its variable area. Both used to come back as a hex rendering of
    /// their first 8 bytes — "ABC12" read as "41-00-42-00-43-00-31-00" — in every API.
    ///
    /// Every expectation below is the value ACE OLEDB 16.0 returned for the same cell. ACE
    /// returns fixed-length text padded with spaces to the declared length, because that is how
    /// it is stored, so the padding is part of the expected value.
    /// </summary>
    public class FixedLengthColumnTests
    {
        private static readonly string DbPath = TestDatabases.FixedLengthDb;

        private const int Width = 20;

        /// <summary>Id → the Code/Packed value ACE returns (null for a null cell).</summary>
        private static readonly Dictionary<int, string?> ExpectedText = new Dictionary<int, string?>
        {
            [1] = "ABC12".PadRight(Width),
            [2] = "12345678901234567890",
            [3] = null,
            [4] = new string(' ', Width),
            [5] = "Café “q”".PadRight(Width),
            [6] = "  lead".PadRight(Width),
            [7] = "trail".PadRight(Width),
        };

        /// <summary>Id → the Bytes value ACE returns, rendered the way the library renders binary.</summary>
        private static readonly Dictionary<int, string?> ExpectedBinary = new Dictionary<int, string?>
        {
            [1] = "01-02-03-04-05-06-07-08-09-0A-0B-0C-0D-0E-0F-10",
            [2] = "AB-CD-00-00-00-00-00-00-00-00-00-00-00-00-00-00",
            [3] = null,
        };

        // ── Schema ────────────────────────────────────────────────────────

        [Fact]
        public void FixedText_IsReportedAsFixedLengthText()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            foreach (string name in new[] { "Code", "Packed" })
            {
                ColumnMetadata col = reader.GetColumnMetadata("FixedText").Single(m => m.Name == name);
                col.ClrType.Should().Be(typeof(string));
                col.IsFixedLength.Should().BeTrue(because: $"{name} is CHAR({Width})");
            }
        }

        // ── Text ──────────────────────────────────────────────────────────

        [Fact]
        public void FixedText_TypedPath_MatchesAccess()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            List<string> names = reader.GetColumnNames("FixedText");
            int id = names.IndexOf("Id"), code = names.IndexOf("Code"), packed = names.IndexOf("Packed");

            List<object[]> rows = reader.StreamRows("FixedText").ToList();
            rows.Should().HaveCount(ExpectedText.Count);

            foreach (object[] row in rows)
            {
                object expected = (object?)ExpectedText[(int)row[id]] ?? DBNull.Value;
                row[code].Should().Be(expected, because: $"Code of row {row[id]}");
                row[packed].Should().Be(expected, because: $"Packed of row {row[id]}");
            }
        }

        [Fact]
        public void FixedText_StringPath_MatchesAccess()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            List<string> names = reader.GetColumnNames("FixedText");
            int id = names.IndexOf("Id"), code = names.IndexOf("Code");

            foreach (string[] row in reader.StreamRowsAsStrings("FixedText"))
                row[code].Should().Be(ExpectedText[int.Parse(row[id])] ?? string.Empty,
                    because: $"Code of row {row[id]}");
        }

        [Fact]
        public void FixedText_DataReader_MatchesAccess()
        {
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            using IDataReader rd = reader.CreateDataReader("FixedText", new[] { "Id", "Code" });
            int seen = 0;
            while (rd.Read())
            {
                string? expected = ExpectedText[rd.GetInt32(0)];
                if (expected == null) rd.IsDBNull(1).Should().BeTrue();
                else rd.GetString(1).Should().Be(expected);
                seen++;
            }

            seen.Should().Be(ExpectedText.Count);
        }

        // ── Binary ────────────────────────────────────────────────────────

        [Fact]
        public void FixedBinary_ReadsTheWholeDeclaredLength()
        {
            // BINARY(16): the old path stopped after 8 bytes.
            if (!File.Exists(DbPath)) return;
            using var reader = TestDatabases.Open(DbPath);

            List<string> names = reader.GetColumnNames("FixedBinary");
            int id = names.IndexOf("Id"), bytes = names.IndexOf("Bytes");

            List<object[]> typed = reader.StreamRows("FixedBinary").ToList();
            typed.Should().HaveCount(ExpectedBinary.Count);
            foreach (object[] row in typed)
                row[bytes].Should().Be((object?)ExpectedBinary[(int)row[id]] ?? DBNull.Value);

            foreach (string[] row in reader.StreamRowsAsStrings("FixedBinary"))
                row[bytes].Should().Be(ExpectedBinary[int.Parse(row[id])] ?? string.Empty);
        }
    }
}
