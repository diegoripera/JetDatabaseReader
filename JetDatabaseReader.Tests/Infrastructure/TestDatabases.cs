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

        // Local-only large file — not added to the project or repository.
        public const string LargeFile = @"D:\Diego\Downloads\DB Matrix.accdb";

        // User's local downloaded .mdb files — not added to the project or repository.
        // Note: JetDatabaseReader reads these with FileStream; Windows MOTW macro blocking
        // (Zone.Identifier ADS) does not affect raw file reads — only Access VBA execution.
        // To unblock for Access: Unblock-File -Path "<path>" in PowerShell.
        public const string R3188_W_PO  = @"D:\Diego\Downloads\R3188_20260321-20260327_W_PO.mdb";
        public const string R419_TR_TPI = @"D:\Diego\Downloads\R419_20260213_D_TR_TPI.mdb";

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

        /// <summary>All databases (skips any that don't exist or can't be opened).</summary>
        public static IEnumerable<object[]> All =>
            new[] { LargeFile, NorthwindTraders, AdventureWorks, R3188_W_PO, R419_TR_TPI }
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>The smaller databases (skips any that can't be opened).</summary>
        public static IEnumerable<object[]> Small =>
            new[] { NorthwindTraders, AdventureWorks }
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>The user's local downloaded .mdb files (skips any that can't be opened).</summary>
        public static IEnumerable<object[]> Downloads =>
            new[] { R3188_W_PO, R419_TR_TPI }
                .Where(IsReadable)
                .Select(p => new object[] { p });

        /// <summary>
        /// All known database files that exist on disk, without an IsReadable check.
        /// Use this when you need to assert something about files that may fail to open
        /// (e.g., verifying they are not password-protected).
        /// </summary>
        public static IEnumerable<object[]> AllExisting =>
            new[] { LargeFile, NorthwindTraders, AdventureWorks, R3188_W_PO, R419_TR_TPI }
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
