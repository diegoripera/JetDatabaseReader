using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Tests for TableQuery — both the typed chain and the string chain.
    ///
    /// Typed chain:  Where → Execute / FirstOrDefault / Count
    /// String chain: WhereAsStrings → ExecuteAsStrings / FirstOrDefaultAsStrings / CountAsStrings
    /// </summary>
    public class AccessReaderQueryTests
    {
        // ── Typed chain: Execute ──────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Execute_WithoutFilter_ReturnsAllRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            int expected = reader.StreamRows(table).Count();

            IEnumerable<object[]> result = reader.Query(table).Execute();

            result.Should().HaveCount(expected);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Execute_WithTake_LimitsResults(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            IEnumerable<object[]> result = reader.Query(table).Take(3).Execute();

            result.Should().HaveCountLessThanOrEqualTo(3);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Execute_WithWhere_FiltersRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            // Always-false filter should return zero rows
            IEnumerable<object[]> none = reader.Query(table)
                .Where(_ => false)
                .Execute();

            none.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Execute_WithWhere_AlwaysTrue_ReturnsAllRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            int expected = reader.StreamRows(table).Count();

            IEnumerable<object[]> all = reader.Query(table)
                .Where(_ => true)
                .Execute();

            all.Should().HaveCount(expected);
        }

        // ── Typed chain: FirstOrDefault ───────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void FirstOrDefault_WithoutFilter_ReturnsNonNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.RowCount > 0).Name;
            if (table == null) return; // all tables are empty — nothing to assert

            object[]? first = reader.Query(table).FirstOrDefault();

            first.Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void FirstOrDefault_WhenNoRowMatches_ReturnsNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            object[]? result = reader.Query(table)
                .Where(_ => false)
                .FirstOrDefault();

            result.Should().BeNull();
        }

        // ── Typed chain: Count ────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Count_WithoutFilter_MatchesStreamRowsCount(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            int expected = reader.StreamRows(table).Count();

            int count = reader.Query(table).Count();

            count.Should().Be(expected);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Count_WithAlwaysFalseFilter_ReturnsZero(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            int count = reader.Query(table).Where(_ => false).Count();

            count.Should().Be(0);
        }

        // ── String chain: ExecuteAsStrings ────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ExecuteAsStrings_WithoutFilter_ReturnsAllRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            int expected = reader.StreamRowsAsStrings(table).Count();

            IEnumerable<string[]> result = reader.Query(table).ExecuteAsStrings();

            result.Should().HaveCount(expected);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ExecuteAsStrings_WithTake_LimitsResults(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            IEnumerable<string[]> result = reader.Query(table).Take(3).ExecuteAsStrings();

            result.Should().HaveCountLessThanOrEqualTo(3);
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ExecuteAsStrings_WithWhereAsStrings_FiltersRows(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            // Always-false filter should return zero rows
            IEnumerable<string[]> none = reader.Query(table)
                .WhereAsStrings(_ => false)
                .ExecuteAsStrings();

            none.Should().BeEmpty();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void ExecuteAsStrings_AllCells_AreStringOrNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            foreach (string[] row in reader.Query(table).Take(20).ExecuteAsStrings())
            foreach (string cell in row)
                (cell == null || cell is string).Should().BeTrue();
        }

        // ── String chain: FirstOrDefaultAsStrings ─────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void FirstOrDefaultAsStrings_WithoutFilter_ReturnsNonNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.GetTableStats().FirstOrDefault(s => s.RowCount > 0).Name;
            if (table == null) return; // all tables are empty — nothing to assert

            string[]? first = reader.Query(table).FirstOrDefaultAsStrings();

            first.Should().NotBeNull();
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void FirstOrDefaultAsStrings_WhenNoRowMatches_ReturnsNull(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            string[]? result = reader.Query(table)
                .WhereAsStrings(_ => false)
                .FirstOrDefaultAsStrings();

            result.Should().BeNull();
        }

        // ── String chain: CountAsStrings ──────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void CountAsStrings_WithoutFilter_MatchesCount(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];

            int typed  = reader.Query(table).Count();
            int string_ = reader.Query(table).CountAsStrings();

            string_.Should().Be(typed);
        }

        // ── Take affects both chains ──────────────────────────────────────

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void Take_AppliedOnce_AffectsBothExecuteAndExecuteAsStrings(string path)
        {
            using var reader = TestDatabases.Open(path);
            string table = reader.ListTables()[0];
            const int limit = 2;

            TableQuery query  = reader.Query(table).Take(limit);

            query.Execute().Should().HaveCountLessThanOrEqualTo(limit);
            query.ExecuteAsStrings().Should().HaveCountLessThanOrEqualTo(limit);
        }
    }
}
