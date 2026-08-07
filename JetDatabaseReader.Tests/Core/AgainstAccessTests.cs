using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Five defects that only surfaced when the reader was compared against the Access engine
    /// itself rather than against its own second read path. Each expectation below is a value ACE
    /// OLEDB returned for the same cell — they are not the reader's own output frozen in place,
    /// which is exactly how these survived for so long.
    /// </summary>
    public class AgainstAccessTests
    {
        // ── Rows dropped from all-fixed-column tables ─────────────────────

        [Fact]
        public void AllFixedColumnTable_KeepsEveryRow()
        {
            // A table with no variable-length columns has no var_len field in its rows. Reading one
            // anyway takes the tail of the last fixed column, so rows whose final column happened
            // to be non-zero were silently dropped.
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            using var reader = TestDatabases.Open(TestDatabases.Jet4NoPassword);

            // Sample's columns are Id/Name/Amount/Created — Name is variable, so build the
            // guarantee on the invariant instead: the count the scanner reports and the count the
            // read path yields must never disagree.
            foreach (string table in reader.ListTables())
                reader.StreamRows(table).Count().Should().Be((int)reader.GetRealRowCount(table),
                    because: $"'{table}' must yield every row the scanner counted");
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void RowsYielded_MatchRowsCounted(string path)
        {
            // The general form of the bug above: GetRealRowCount and StreamRows walk the same
            // spans, so any gap between them is a row the decoder threw away.
            using var reader = TestDatabases.Open(path);

            foreach (string table in reader.ListTables())
            {
                long counted = reader.GetRealRowCount(table);
                long yielded = reader.StreamRows(table).LongCount();

                yielded.Should().Be(counted, because: $"'{table}' must not drop rows while decoding");
            }
        }

        // ── Decimal (T_NUMERIC) ───────────────────────────────────────────

        [Fact]
        public void DecimalColumns_DecodeToTheStoredValue()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("Product");
            int weight = meta.FindIndex(m => m.Name == "Weight");
            int id = meta.FindIndex(m => m.Name == "ProductID");

            meta[weight].ClrType.Should().Be(typeof(decimal));

            // ProductID 680's weight per Access. Before the fix every Decimal came back as a
            // 24-digit integer, because the scale was read from the magnitude and the magnitude
            // from the wrong nine bytes.
            object[] row = reader.StreamRows("Product").First(r => Convert.ToInt32(r[id]) == 680);

            row[weight].Should().BeOfType<decimal>();
            ((decimal)row[weight]).Should().Be(1016.04m);
        }

        [Fact]
        public void DecimalColumns_KeepTheirScale()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("Product");
            int weight = meta.FindIndex(m => m.Name == "Weight");

            // The scale lives in the column descriptor, not in the row — decimal(8,2) here.
            foreach (object[] row in reader.StreamRows("Product").Where(r => r[weight] != DBNull.Value).Take(20))
                decimal.GetBits((decimal)row[weight])[3].Should().Be(2 << 16,
                    because: "the descriptor says scale 2");
        }

        // ── GUID stored in the variable area ──────────────────────────────

        [Fact]
        public void GuidColumn_StoredVariably_ReadsTheStoredGuid()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("ProductModel");
            int id = meta.FindIndex(m => m.Name == "ProductModelID");
            int guid = meta.FindIndex(m => m.Name == "rowguid");

            // These rowguid columns have the FIXED flag clear, so Access keeps them in the
            // variable area. Deciding by type instead read them from offset zero and returned a
            // GUID whose first four bytes were the row's primary key.
            var expected = new Dictionary<int, string>
            {
                [1] = "29321d47-1e4c-4aac-887c-19634328c25e",
                [2] = "474fb654-3c96-4cb9-82df-2152eeffbdb0",
                [3] = "a75483fe-3c47-4aa4-93cf-664b51192987",
            };

            foreach (object[] row in reader.StreamRows("ProductModel"))
            {
                int key = Convert.ToInt32(row[id]);
                if (!expected.TryGetValue(key, out string want)) continue;

                row[guid].Should().BeOfType<Guid>();
                ((Guid)row[guid]).ToString("D").Should().Be(want);
            }
        }

        [Fact]
        public void GuidColumns_AreNotAllDerivedFromThePrimaryKey()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("Employee");
            int guid = meta.FindIndex(m => m.Name == "rowguid");
            if (guid < 0) return;

            // The old failure produced GUIDs that were mostly zeroes with a small counter in them.
            List<Guid> guids = reader.StreamRows("Employee")
                .Where(r => r[guid] is Guid)
                .Select(r => (Guid)r[guid])
                .ToList();

            guids.Should().NotBeEmpty();
            guids.Distinct().Should().HaveCount(guids.Count, because: "row GUIDs are unique");
            guids.Should().OnlyContain(g => g.ToByteArray().Count(b => b != 0) > 8,
                because: "a real GUID is not mostly zero bytes");
        }

        // ── Compressed text mode toggling ─────────────────────────────────

        [Fact]
        public void CompressedText_SurvivesNonAsciiCharacters()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("Learn");
            int text = meta.FindIndex(m => m.Name == "SectionText");
            if (text < 0) return;

            // JET's compressed encoding toggles modes on a single NUL in both directions. Getting
            // the return toggle wrong cost a byte of alignment, so everything after the first
            // smart quote became CJK-looking mojibake.
            List<string> values = reader.StreamRows("Learn")
                .Select(r => r[text] as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            values.Should().NotBeEmpty();
            values.Should().Contain(s => s.Contains("“Welcome”"),
                because: "the smart-quoted words must survive the mode switch");
            values.Should().OnlyContain(s => !s.Any(IsMojibake),
                because: "no value should contain CJK from a misaligned UCS-2 read");
        }

        private static bool IsMojibake(char c) => c >= '　' && c <= '鿿';

        // ── Multi-page LVAL chains ────────────────────────────────────────

        [Fact]
        public void LongMemo_SpanningLvalPages_LosesNothingAtTheBoundaries()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            List<ColumnMetadata> meta = reader.GetColumnMetadata("ProductModel");
            int desc = meta.FindIndex(m => m.Name == "CatalogDescription");

            // These XML descriptions are ~2 400 characters, which spans more than one LVAL page.
            // Skipping eight bytes per chunk instead of four dropped two characters at every
            // boundary, so the document no longer closed its root element.
            List<string> docs = reader.StreamRows("ProductModel")
                .Select(r => r[desc] as string)
                .Where(s => !string.IsNullOrEmpty(s) && s.Length > 2000)
                .ToList();

            docs.Should().NotBeEmpty(because: "the fixture has multi-page catalogue descriptions");

            foreach (string doc in docs)
            {
                doc.Should().Contain("<?xml-stylesheet", because: "the declaration must be intact");
                doc.TrimEnd().Should().EndWith("</p1:ProductDescription>",
                    because: "the closing tag is the last thing in the final chunk");
            }
        }
    }
}
