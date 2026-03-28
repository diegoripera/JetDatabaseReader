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

        // Local-only large file — not added to the project or repository.
        public const string Matrix = @"C:\temp\large_access_file.accdb";


        // ── MemberData sets ───────────────────────────────────────────────

        /// <summary>Returns true when the file exists and can be opened by the reader (not encrypted, not corrupt).</summary>
        internal static bool IsReadable(string path)
        {
            if (!File.Exists(path)) return false;
            try { using var r = AccessReader.Open(path); return true; }
            catch { return false; }
        }

        /// <summary>All databases (skips any that don't exist or can't be opened).</summary>
        public static IEnumerable<object[]> All =>
            new[] { Matrix, NorthwindTraders, AdventureWorks }
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>The smaller databases (skips any that can't be opened).</summary>
        public static IEnumerable<object[]> Small =>
            new[] { NorthwindTraders, AdventureWorks }
                .Where(IsReadable)
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
