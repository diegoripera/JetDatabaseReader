using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Linked tables — tables that appear in a database but whose rows live somewhere else.
    ///
    /// The connection-string parsing is covered directly, because none of the available test
    /// databases contains a linked table: the catalog-level detection (object types 4 and 6) has
    /// no fixture to prove it against yet. The formats below are the ones Access writes.
    /// </summary>
    public class LinkedTableTests
    {
        // ── Connection string parsing ─────────────────────────────────────

        [Fact]
        public void AccessLink_HasEmptyProviderAndAFilePath()
        {
            LinkedTable link = Parse("Orders", "Orders", @";DATABASE=C:\data\backend.accdb");

            link.Kind.Should().Be(LinkedTableKind.Access);
            link.SourcePath.Should().Be(@"C:\data\backend.accdb");
            link.IsAccessDatabase.Should().BeTrue();
        }

        [Fact]
        public void AccessLink_ToAUncPath_IsStillAnAccessDatabase()
        {
            LinkedTable link = Parse("Orders", "Orders", @";DATABASE=\\server\share\backend.mdb");

            link.Kind.Should().Be(LinkedTableKind.Access);
            link.SourcePath.Should().Be(@"\\server\share\backend.mdb");
            link.IsAccessDatabase.Should().BeTrue();
        }

        [Fact]
        public void ExcelLink_IsRecognisedAndNotOpenable()
        {
            LinkedTable link = Parse("Sheet1", "Sheet1$",
                @"Excel 12.0 Xml;HDR=YES;IMEX=2;DATABASE=C:\data\book.xlsx");

            link.Kind.Should().Be(LinkedTableKind.Excel);
            link.SourcePath.Should().Be(@"C:\data\book.xlsx");
            link.IsAccessDatabase.Should().BeFalse();
        }

        [Fact]
        public void TextLink_IsRecognised()
        {
            LinkedTable link = Parse("Import", "Import#csv",
                @"Text;FMT=Delimited;HDR=YES;DATABASE=C:\data");

            link.Kind.Should().Be(LinkedTableKind.Text);
            link.SourcePath.Should().Be(@"C:\data");
        }

        [Fact]
        public void OdbcLink_DoesNotExposeDatabaseAsAPath()
        {
            // DATABASE= names a database on the server, not a file. Surfacing it as SourcePath
            // would invite callers to try opening something that does not exist on disk.
            LinkedTable link = Parse("Customers", "dbo.Customers",
                "ODBC;DRIVER={SQL Server};SERVER=sql01;DATABASE=Sales;UID=app");

            link.Kind.Should().Be(LinkedTableKind.Odbc);
            link.SourcePath.Should().BeNull();
            link.IsAccessDatabase.Should().BeFalse();
        }

        [Fact]
        public void OdbcTypedRow_IsOdbcEvenWithoutThePrefix()
        {
            LinkedTable link = LinkedTableParser.Parse("T", "T", "DSN=Sales", odbcType: true);

            link.Kind.Should().Be(LinkedTableKind.Odbc);
        }

        [Fact]
        public void ForeignName_DefaultsToTheLocalName()
        {
            LinkedTable link = Parse("Orders", "", @";DATABASE=C:\data\backend.accdb");

            link.ForeignName.Should().Be("Orders");
        }

        [Fact]
        public void ForeignName_IsKeptWhenItDiffers()
        {
            LinkedTable link = Parse("RemoteOrders", "Orders", @";DATABASE=C:\data\backend.accdb");

            link.ForeignName.Should().Be("Orders");
            link.Name.Should().Be("RemoteOrders");
        }

        [Fact]
        public void MissingDatabaseClause_LeavesPathNull()
        {
            LinkedTable link = Parse("T", "T", ";");

            link.SourcePath.Should().BeNull();
            link.IsAccessDatabase.Should().BeFalse(because: "there is nothing to open");
        }

        [Fact]
        public void EmptyConnectionString_IsUnknownRatherThanAccess()
        {
            LinkedTable link = Parse("T", "T", "");

            link.SourcePath.Should().BeNull();
            link.IsAccessDatabase.Should().BeFalse();
        }

        // ── Catalog integration ───────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void GetLinkedTables_NeverReturnsNull(string path)
        {
            using var reader = TestDatabases.Open(path);

            reader.GetLinkedTables().Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void LinkedTables_AreNotListedAsLocalTables(string path)
        {
            using var reader = TestDatabases.Open(path);

            List<string> local = reader.ListTables();
            foreach (LinkedTable link in reader.GetLinkedTables())
                local.Should().NotContain(link.Name,
                    because: "a linked table's rows are not in this file, so reading it would return nothing");
        }

        [Fact]
        public void OpenLinkedTableSource_RejectsANonAccessLink()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            LinkedTable odbc = Parse("T", "T", "ODBC;DSN=Sales");

            Action act = () => reader.OpenLinkedTableSource(odbc);

            act.Should().Throw<NotSupportedException>().WithMessage("*Odbc*");
        }

        [Fact]
        public void OpenLinkedTableSource_ReportsAMissingSourceFile()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);
            LinkedTable dangling = Parse("T", "T", @";DATABASE=Z:\gone\missing.accdb");

            Action act = () => reader.OpenLinkedTableSource(dangling);

            act.Should().Throw<FileNotFoundException>().WithMessage("*was not found*");
        }

        [Fact]
        public void OpenLinkedTableSource_OpensARealAccessSource()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = TestDatabases.Open(TestDatabases.AdventureWorks);

            // Point a synthetic link at a database that does exist, so the open path itself is
            // exercised even without a database that contains a genuine link.
            LinkedTable link = Parse("Elsewhere", "Product", $";DATABASE={TestDatabases.AdventureWorks}");

            using AccessReader source = reader.OpenLinkedTableSource(link);

            source.ListTables().Should().Contain(link.ForeignName);
            source.StreamRows(link.ForeignName).Should().NotBeEmpty();
        }

        private static LinkedTable Parse(string name, string foreignName, string connect) =>
            LinkedTableParser.Parse(name, foreignName, connect, odbcType: false);
    }
}
