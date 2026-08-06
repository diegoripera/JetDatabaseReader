using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Overflow rows: a row-offset entry with bit 0x4000 is not a row, it is a pointer to the page
    /// and row actually holding the data. These used to be skipped, which silently dropped rows.
    ///
    /// It mattered most in MSysObjects: 40 of NorthwindTraders' catalog rows are overflow rows, so
    /// five user tables — including Employees, Orders, and Products — were invisible entirely.
    /// </summary>
    public class OverflowRowTests
    {
        /// <summary>Tables in NorthwindTraders whose catalog entry lives behind an overflow row.</summary>
        private static readonly string[] PreviouslyInvisible =
        {
            "Employees", "Orders", "Products", "PurchaseOrderStatus", "Welcome"
        };

        [Fact]
        public void Northwind_ListsTablesWhoseCatalogRowIsOverflow()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);
            List<string> tables = reader.ListTables();

            foreach (string expected in PreviouslyInvisible)
                tables.Should().Contain(expected,
                    because: "its MSysObjects row is an overflow row and must be followed");

            tables.Should().HaveCount(28);
        }

        [Fact]
        public void Northwind_TablesBehindOverflowRows_AreReadable()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);

            foreach (string table in PreviouslyInvisible)
            {
                reader.GetColumnNames(table).Should().NotBeEmpty(because: $"'{table}' should have a schema");

                // Listing a table is not enough — a misread pointer would surface as a table that
                // exists but yields garbage or throws.
                Action read = () => reader.StreamRows(table).Take(5).ToList();
                read.Should().NotThrow(because: $"'{table}' should be readable");
            }
        }

        [Fact]
        public void Northwind_OrdersTable_HasExpectedShape()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = TestDatabases.Open(TestDatabases.NorthwindTraders);

            // A pointer resolved to the wrong page would still "work" but produce nonsense, so
            // assert on content: Orders must have an Order ID-ish key column and real rows.
            List<string> columns = reader.GetColumnNames("Orders");
            columns.Should().Contain(c => c.IndexOf("Order", StringComparison.OrdinalIgnoreCase) >= 0);

            List<object[]> rows = reader.StreamRows("Orders").ToList();
            rows.Should().NotBeEmpty();
            rows.Should().OnlyContain(r => r.Length == columns.Count);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void StreamRows_MatchesGetRealRowCount_WithOverflowRowsCounted(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (TableStat stat in reader.GetTableStats().Where(s => s.ColumnCount > 0).Take(5))
            {
                long counted = reader.GetRealRowCount(stat.Name);
                int streamed = reader.StreamRows(stat.Name).Count();

                streamed.Should().Be((int)counted,
                    because: $"'{stat.Name}' must stream exactly the rows GetRealRowCount counts");
            }
        }
    }
}
