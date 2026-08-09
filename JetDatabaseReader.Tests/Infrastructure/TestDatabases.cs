using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Paths to the local test databases and shared MemberData helpers.
    /// Tests are skipped automatically when the file does not exist on the machine.
    /// </summary>
    internal static class TestDatabases
    {
        // ── Paths ─────────────────────────────────────────────────────────

        public static readonly string NorthwindTraders =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NorthwindTraders.accdb");

        public static readonly string AdventureWorks =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdventureLT2008.mdb");

        /// <summary>
        /// Single-table database with an autonumber Id column and two Short Text columns
        /// (Number1, Number2) whose values look like integers but must stay as strings.
        /// Row 8 of Number1 contains "78/465", which is an intentional non-numeric value
        /// that proves the columns are Text, not Numeric.
        /// </summary>
        public static readonly string AutonumberDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Autonumber.accdb");

        /// <summary>
        /// Holds one local table plus a linked table (MSysObjects type 6) pointing at
        /// AdventureLT2008.mdb. The link stores the absolute path it had when it was created, so
        /// the source resolves only on the machine that made it — tests assert on the link's
        /// metadata always, and follow it only when the target happens to exist.
        /// </summary>
        public static readonly string LinkedDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Autonumber_linked.accdb");

        /// <summary>
        /// A matched pair of Jet4 databases holding the same three rows, one with a database
        /// password set and one without. Both are in the repository, unlike the older
        /// password fixtures — the stored password is masked with the database's creation date,
        /// so a single passwordless file proves nothing about any other file's date.
        /// </summary>
        public static readonly string Jet4NoPassword =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Jet4_NoPassword.mdb");

        /// <summary>Twin of <see cref="Jet4NoPassword"/> with <see cref="Jet4StoredPassword"/> set.</summary>
        public static readonly string Jet4WithPassword =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Jet4_Password.mdb");

        /// <summary>
        /// Exactly 20 characters — Access's limit for a Jet4 database password, which is also the
        /// case where the stored field is full and carries no NUL terminator.
        /// </summary>
        public const string Jet4StoredPassword = "JetPwd_Test_20Chars!";

        /// <summary>
        /// Extra databases to test against, from the <c>JETDATABASEREADER_TEST_DBS</c> environment
        /// variable: a directory, or a list of paths separated by <see cref="Path.PathSeparator"/>.
        ///
        /// Large real-world files catch things the small fixtures cannot — multi-page long values,
        /// tables spanning thousands of pages, released pages a compact never reclaimed — but they
        /// belong to whoever is running the tests and cannot live in the repository. Point the
        /// variable at your own; when it is unset these sets are simply empty and the theories
        /// that use them do not run.
        /// </summary>
        private static readonly Lazy<string[]> ExternalDbs = new Lazy<string[]>(() =>
        {
            string spec = Environment.GetEnvironmentVariable("JETDATABASEREADER_TEST_DBS");
            if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<string>();

            try
            {
                if (Directory.Exists(spec))
                    return Directory.GetFiles(spec, "*.*")
                        .Where(f => f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                return spec.Split(Path.PathSeparator)
                           .Select(p => p.Trim())
                           .Where(p => p.Length > 0)
                           .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        });

        // ── MemberData sets ───────────────────────────────────────────────

        /// <summary>Returns true when the file exists and can be opened by the reader (not encrypted, not corrupt).</summary>
        internal static bool IsReadable(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var r = AccessReader.Open(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The fixtures in the repository plus any external databases configured.</summary>
        public static IEnumerable<object[]> All =>
            new[] { NorthwindTraders, AdventureWorks }
                .Concat(ExternalDbs.Value)
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>The fixtures in the repository (skips any that can't be opened).</summary>
        public static IEnumerable<object[]> Small =>
            new[] { NorthwindTraders, AdventureWorks }
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>
        /// The external databases, if any were configured. Not exposed as MemberData: xUnit fails
        /// a theory whose data set is empty, and empty is the normal case here.
        /// </summary>
        public static IEnumerable<string> ExternalPaths => ExternalDbs.Value.Where(IsReadable);

        /// <summary>
        /// All known database files that exist on disk, without an IsReadable check.
        /// Use this when you need to assert something about files that may fail to open
        /// (e.g., verifying they are not password-protected).
        /// </summary>
        public static IEnumerable<object[]> AllExisting =>
            new[] { NorthwindTraders, AdventureWorks }
                .Concat(ExternalDbs.Value)
                .Where(File.Exists)
                .Select(p => new object[] { p });

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns a skip reason string when the file is missing, or null when it exists.
        /// Use with <c>Skip = SkipIfMissing(path)</c> on [Fact].
        /// </summary>
        public static string? SkipIfMissing(string path) =>
            File.Exists(path) ? null : $"Test database not found: {path}";

        public static AccessReader Open(string path, AccessReaderOptions? options = null) =>
            AccessReader.Open(path, options);
    }
}
