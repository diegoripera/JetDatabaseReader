using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Access 2007 added Attachment, Multi-Value and append-only Memo history columns. All three
    /// share type code 0x12 and keep their values in hidden system tables, with only a 4-byte id
    /// in the row itself.
    ///
    /// The reader does not follow that id. What these tests pin down is that the gap is *visible*:
    /// the column has to announce itself as complex, because the id looks like an ordinary value
    /// and a caller who cannot tell the difference will publish "01-00-00-00" as an attachment.
    /// </summary>
    public class ComplexColumnTests
    {
        [Theory]
        [InlineData("Employees", "Attachments")]
        [InlineData("ProductCategories", "ProductCategoryImage")]
        public void ComplexColumns_AreNamedAsComplex(string table, string column)
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);

            ColumnMetadata col = reader.GetColumnMetadata(table).Single(c => c.Name == column);

            // Before this it read "0x12", which tells a caller nothing.
            col.TypeName.Should().Be("Complex");
        }

        [Fact]
        public void ComplexColumnValues_AreTheStoredIdAndNothingMore()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);

            List<ColumnMetadata> meta = reader.GetColumnMetadata("Employees");
            int idx = meta.FindIndex(c => c.Name == "Attachments");

            meta[idx].MaxLength.Should().Be(4, because: "the row holds a 4-byte id, not the attachment");

            // Four bytes rendered as hex pairs. Asserting the shape rather than the exact string
            // keeps this a statement about the gap — the id is opaque and its values are not the
            // library's to promise.
            foreach (object[] row in reader.StreamRows("Employees").Take(5))
            {
                if (row[idx] == DBNull.Value) continue;
                row[idx].Should().BeOfType<string>().Which
                    .Should().MatchRegex("^[0-9A-F]{2}(-[0-9A-F]{2}){3}$");
            }
        }

        [Fact]
        public void OleObjectColumns_AreNotConfusedWithComplexOnes()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);

            // An OLE Object stores its bytes in the row's LVAL chain and is read in full. The two
            // are easy to conflate — both are "a file" as far as the Access UI is concerned — and
            // conflating them would either break OLE reads or hide the complex-column gap.
            List<ColumnMetadata> meta = reader.GetColumnMetadata("Product");
            ColumnMetadata photo = meta.Single(c => c.Name == "ThumbNailPhoto");

            photo.TypeName.Should().Be("OLE Object");

            // Whatever an OLE column yields, it is not a four-byte id: these really are the bytes.
            string biggest = reader.StreamRowsAsStrings("Product")
                .Take(50)
                .Select(r => r[meta.IndexOf(photo)])
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderByDescending(s => s.Length)
                .FirstOrDefault();

            biggest.Should().NotBeNull(because: "the fixture has thumbnails stored");
            biggest!.Length.Should().BeGreaterThan(100, because: "an OLE payload is the file, not a 4-byte pointer");
        }
    }
}
