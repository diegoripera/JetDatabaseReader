using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// The same database must read the same way on every machine.
    ///
    /// String-valued reads used to format numbers and dates with the ambient culture, so the
    /// result depended on where the code ran. That is not only a formatting nuisance: under a
    /// culture whose default calendar is not Gregorian the date was simply wrong — 1998-06-01 came
    /// back as 2541-06-01 under th-TH and 1419-02-07 under ar-SA — and ar-SA rendered the decimal
    /// separator as U+066B, which no invariant parser accepts.
    /// </summary>
    public class CultureInvarianceTests : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public void Dispose() => Thread.CurrentThread.CurrentCulture = _original;

        private static readonly string[] Cultures =
        {
            "en-US",   // Gregorian, dot separator
            "es-ES",   // comma separator
            "de-DE",   // comma separator
            "th-TH",   // Buddhist calendar — years differ by 543
            "ar-SA",   // Umm al-Qura calendar and an Arabic decimal separator
        };

        // Only this thread's culture is changed. CultureInfo.DefaultThreadCurrentCulture is
        // process-wide, and setting it here made concurrency tests running in parallel fail —
        // formatting happens on the calling thread, so the thread-local setting is enough.
        private static void Use(string culture) =>
            Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void StringRows_AreIdenticalAcrossCultures(string path)
        {
            List<string[]> reference = null;
            string table = null;

            foreach (string culture in Cultures)
            {
                Use(culture);

                using var reader = TestDatabases.Open(path);
                table = table ?? reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
                if (table == null) return;

                List<string[]> rows = reader.StreamRowsAsStrings(table).Take(200).ToList();

                if (reference == null) { reference = rows; continue; }

                rows.Should().HaveCount(reference.Count, because: $"culture {culture} must not change the row count");
                for (int r = 0; r < rows.Count; r++)
                    rows[r].Should().Equal(reference[r],
                        because: $"row {r} must read identically under {culture}");
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void TheTableList_IsIdenticalAcrossCultures(string path)
        {
            List<string> reference = null;

            foreach (string culture in Cultures)
            {
                Use(culture);

                using var reader = TestDatabases.Open(path);
                List<string> tables = reader.ListTables().OrderBy(t => t, StringComparer.Ordinal).ToList();

                if (reference == null) { reference = tables; continue; }

                // The catalog decides what is a user table by parsing the object type and flags
                // out of text. Sixty-six cultures use a negative sign that is not '-', and a
                // system table's flags are negative — so a culture-sensitive parse silently
                // failed there, left zero, and let the system table through the mask.
                tables.Should().Equal(reference,
                    because: $"the set of user tables must not depend on the culture ({culture})");
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.Small), MemberType = typeof(TestDatabases))]
        public void DatesAndNumbers_RoundTripThroughInvariantParsing(string path)
        {
            foreach (string culture in Cultures)
            {
                Use(culture);

                using var reader = TestDatabases.Open(path);
                string table = reader.GetTableStats().FirstOrDefault(s => s.ColumnCount > 0)?.Name;
                if (table == null) return;

                List<ColumnMetadata> meta = reader.GetColumnMetadata(table);
                List<string[]> rows = reader.StreamRowsAsStrings(table).Take(200).ToList();

                for (int c = 0; c < meta.Count; c++)
                {
                    Type type = meta[c].ClrType;
                    foreach (string[] row in rows)
                    {
                        string value = row[c];
                        if (string.IsNullOrEmpty(value)) continue;

                        // Whatever the ambient culture, the text a caller receives has to be
                        // parseable by the invariant reader on the other side of a CSV or an API.
                        if (type == typeof(DateTime))
                            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out _)
                                .Should().BeTrue(because: $"'{value}' under {culture} should parse invariantly");
                        else if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
                            decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                                .Should().BeTrue(because: $"'{value}' under {culture} should parse invariantly");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
        public void FloatingPointText_RoundTripsToTheTypedValue(string path)
        {
            using var reader = TestDatabases.Open(path);

            foreach (TableStat stat in reader.GetTableStats().Where(s => s.ColumnCount > 0).Take(6))
            {
                List<ColumnMetadata> meta = reader.GetColumnMetadata(stat.Name);
                List<int> reals = meta.Select((m, i) => new { m, i })
                                      .Where(x => x.m.ClrType == typeof(double) || x.m.ClrType == typeof(float))
                                      .Select(x => x.i).ToList();
                if (reals.Count == 0) continue;

                List<object[]> typed = reader.StreamRows(stat.Name).Take(200).ToList();
                List<string[]> text = reader.StreamRowsAsStrings(stat.Name).Take(200).ToList();

                for (int r = 0; r < typed.Count; r++)
                {
                    foreach (int c in reals)
                    {
                        if (typed[r][c] == DBNull.Value) continue;

                        // The text must recover the value exactly. "G" did not on .NET Framework,
                        // where it emits 15 significant digits: a stored 0.1+0.2 came back as
                        // "0.3", which parses to a different number than the one in the file.
                        double expected = Convert.ToDouble(typed[r][c], CultureInfo.InvariantCulture);
                        double parsed = double.Parse(text[r][c], NumberStyles.Float, CultureInfo.InvariantCulture);

                        parsed.Should().Be(expected,
                            because: $"'{stat.Name}'.{meta[c].Name} text must recover the stored value");
                    }
                }
            }
        }

        [Fact]
        public void Dates_KeepTheGregorianYear_UnderANonGregorianCalendar()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            string gregorian;
            Use("en-US");
            using (var reader = TestDatabases.Open(TestDatabases.AdventureWorks))
                gregorian = FirstDate(reader);

            if (gregorian == null) return;

            Use("th-TH");
            using (var reader = TestDatabases.Open(TestDatabases.AdventureWorks))
                FirstDate(reader).Should().Be(gregorian,
                    because: "the Buddhist calendar would otherwise shift the year by 543");

            Use("ar-SA");
            using (var reader = TestDatabases.Open(TestDatabases.AdventureWorks))
                FirstDate(reader).Should().Be(gregorian,
                    because: "the Umm al-Qura calendar would otherwise report a different date entirely");
        }

        private static string FirstDate(AccessReader reader)
        {
            const string table = "Product";
            int col = reader.GetColumnMetadata(table).FindIndex(m => m.ClrType == typeof(DateTime));
            if (col < 0) return null;

            return reader.StreamRowsAsStrings(table)
                         .Select(r => r[col])
                         .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        }
    }
}
