using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// Password-protected databases.
    ///
    /// The two fixtures are opposite cases, and the distinction is the whole point:
    ///
    ///   .mdb (Jet4)   — a "database password". Access refuses to open the file, but the page
    ///                   bodies are plain text on disk. Supported: the password is verified and
    ///                   the data reads normally.
    ///   .accdb (ACE)  — "Encrypt with Password". The pages really are encrypted. Not supported,
    ///                   and it must say so clearly rather than report an empty database.
    /// </summary>
    public class PasswordTests
    {
        // Local-only fixtures, kept out of the repository (see .gitignore). They are skipped when
        // absent; the pair in the repository covers the same ground for the Jet4 case.
        private static readonly string JetPasswordDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "AdventureLT2008_encrypted.mdb");

        private static readonly string AceEncryptedDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "NorthwindTraders_encrypted.accdb");

        /// <summary>
        /// The password on those two fixtures. A throwaway invented for them: the files hold
        /// sample data, they are not in the repository, and it unlocks nothing else.
        /// </summary>
        private const string Password = "This_Pwd_IsReally_dificult_to_guess_123!";

        /// <summary>Access truncates a Jet4 database password to 20 characters when it is set.</summary>
        private const string StoredPassword = "This_Pwd_IsReally_di";

        private static bool HaveLocalFixture(string path) => File.Exists(path);

        // ── Jet4 database password, on fixtures that are in the repository ────

        // Everything below this heading used to depend on local-only files, so a clean clone ran
        // none of it. That is how the bug these tests pin down survived: the stored password is
        // masked twice, once with a fixed constant and once with a four-byte value derived from
        // the database's creation date. Only the fixed mask was applied, so the creation-date
        // residue read as a password on every database created on a different day than the one
        // the constant had been recovered from — and the reader refused to open files that had
        // never been protected at all.

        [Fact]
        public void Jet4_WithoutAPassword_IsNotReportedAsProtected()
        {
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            using var reader = AccessReader.Open(TestDatabases.Jet4NoPassword);

            reader.IsPasswordProtected.Should().BeFalse();
            reader.ListTables().Should().Contain("Sample");
        }

        [Fact]
        public void Jet4_WithoutAPassword_OpensWithoutOne()
        {
            if (!File.Exists(TestDatabases.Jet4NoPassword)) return;

            // The regression in one line: this threw "This database has a database password".
            Action act = () => AccessReader.Open(TestDatabases.Jet4NoPassword).Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public void Jet4_TwentyCharacterPassword_RoundTrips()
        {
            if (!File.Exists(TestDatabases.Jet4WithPassword)) return;

            // 20 characters fills the 40-byte field exactly, so there is no NUL terminator to
            // stop at — the decoder has to handle the field running to its end.
            using var reader = AccessReader.Open(TestDatabases.Jet4WithPassword,
                new AccessReaderOptions { Password = TestDatabases.Jet4StoredPassword });

            reader.IsPasswordProtected.Should().BeTrue();
            reader.ListTables().Should().Contain("Sample");
        }

        [Fact]
        public void Jet4_ProtectedAndUnprotectedTwins_ReadTheSameRows()
        {
            if (!File.Exists(TestDatabases.Jet4WithPassword) || !File.Exists(TestDatabases.Jet4NoPassword)) return;

            using var plain = AccessReader.Open(TestDatabases.Jet4NoPassword);
            using var locked = AccessReader.Open(TestDatabases.Jet4WithPassword,
                new AccessReaderOptions { Password = TestDatabases.Jet4StoredPassword });

            List<object[]> a = plain.StreamRows("Sample").ToList();
            List<object[]> b = locked.StreamRows("Sample").ToList();

            a.Should().HaveCount(3);
            b.Should().HaveCount(a.Count);
            for (int r = 0; r < a.Count; r++) b[r].Should().Equal(a[r]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("wrong")]
        [InlineData("JetPwd_Test_20Chars")]   // one character short
        [InlineData("jetpwd_test_20chars!")]  // case differs
        public void Jet4_WithoutTheRightPassword_IsRefused(string? supplied)
        {
            if (!File.Exists(TestDatabases.Jet4WithPassword)) return;

            Action act = () => AccessReader.Open(TestDatabases.Jet4WithPassword,
                new AccessReaderOptions { Password = supplied }).Dispose();

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Jet4_LongerPassword_IsComparedOnAccessesTerms()
        {
            if (!File.Exists(TestDatabases.Jet4WithPassword)) return;

            // Access truncates to 20 characters when setting a password, so a caller who types
            // the untruncated password must still get in — that is what Access itself does.
            using var reader = AccessReader.Open(TestDatabases.Jet4WithPassword,
                new AccessReaderOptions { Password = TestDatabases.Jet4StoredPassword + "ignored" });

            reader.ListTables().Should().Contain("Sample");
        }

        [Fact]
        public void EveryFixtureThatHasNoPassword_OpensWithoutOne()
        {
            // A sweep rather than another named case: the failure being guarded against was not
            // specific to one file, it was one constant being wrong for every file created on a
            // different day. Any fixture added later is covered for free.
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            var refused = new List<string>();

            foreach (string file in Directory.EnumerateFiles(dir)
                         .Where(f => f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
                         .Where(f => !string.Equals(Path.GetFileName(f), "Jet4_Password.mdb", StringComparison.OrdinalIgnoreCase))
                         .Where(f => !Path.GetFileName(f).Contains("encrypted", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var reader = AccessReader.Open(file); }
                catch (Exception ex) { refused.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }

            refused.Should().BeEmpty(because: "no unprotected fixture may be refused");
        }

        // ── Jet4 database password (.mdb), local-only fixtures ────────────

        [Fact]
        public void Jet4_WithCorrectPassword_Opens()
        {
            if (!HaveLocalFixture(JetPasswordDb)) return;

            using var reader = AccessReader.Open(JetPasswordDb, new AccessReaderOptions { Password = Password });

            reader.IsPasswordProtected.Should().BeTrue();
            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact]
        public void Jet4_WithTruncatedPassword_Opens()
        {
            if (!HaveLocalFixture(JetPasswordDb)) return;

            // Supplying exactly what Access stored must work too.
            using var reader = AccessReader.Open(JetPasswordDb,
                new AccessReaderOptions { Password = StoredPassword });

            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact]
        public void Jet4_WithWrongPassword_Throws()
        {
            if (!HaveLocalFixture(JetPasswordDb)) return;

            Action act = () => AccessReader.Open(JetPasswordDb,
                new AccessReaderOptions { Password = "not the password" });

            act.Should().Throw<InvalidOperationException>().WithMessage("*does not match*");
        }

        [Fact]
        public void Jet4_WithNoPassword_ThrowsAskingForOne()
        {
            if (!HaveLocalFixture(JetPasswordDb)) return;

            Action act = () => AccessReader.Open(JetPasswordDb);

            act.Should().Throw<InvalidOperationException>().WithMessage("*database password*");
        }

        [Fact]
        public void Jet4_ProtectedData_MatchesTheUnprotectedTwin()
        {
            if (!HaveLocalFixture(JetPasswordDb) || !File.Exists(TestDatabases.AdventureWorks)) return;

            using var plain = AccessReader.Open(TestDatabases.AdventureWorks);
            using var locked = AccessReader.Open(JetPasswordDb, new AccessReaderOptions { Password = Password });

            // Same database, one with a password set. The contents must be identical — proving the
            // password really is access control and the page data was never transformed.
            List<string> plainTables = plain.ListTables().OrderBy(t => t).ToList();
            locked.ListTables().OrderBy(t => t).Should().Equal(plainTables);

            foreach (string table in plainTables)
            {
                List<object[]> a = plain.StreamRows(table).ToList();
                List<object[]> b = locked.StreamRows(table).ToList();

                b.Should().HaveCount(a.Count, because: $"'{table}' should have the same rows");
                for (int r = 0; r < a.Count; r++)
                    b[r].Should().Equal(a[r], because: $"'{table}' row {r} should be identical");
            }
        }

        [Fact]
        public void UnprotectedDatabase_ReportsNotProtected()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = AccessReader.Open(TestDatabases.AdventureWorks);

            reader.IsPasswordProtected.Should().BeFalse();
        }

        [Fact]
        public void UnprotectedDatabase_IgnoresASuppliedPassword()
        {
            if (!TestDatabases.IsReadable(TestDatabases.AdventureWorks)) return;

            using var reader = AccessReader.Open(TestDatabases.AdventureWorks,
                new AccessReaderOptions { Password = "irrelevant" });

            reader.ListTables().Should().NotBeEmpty();
        }

        // ── ACE encryption (.accdb) ───────────────────────────────────────

        [Fact]
        public void AceEncrypted_WithCorrectPassword_Opens()
        {
            if (!HaveLocalFixture(AceEncryptedDb)) return;

            using var reader = AccessReader.Open(AceEncryptedDb, new AccessReaderOptions { Password = Password });

            reader.IsEncrypted.Should().BeTrue();
            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact]
        public void AceEncrypted_DecryptsToTheSameDataAsThePlainTwin()
        {
            if (!HaveLocalFixture(AceEncryptedDb) || !TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var plain = AccessReader.Open(TestDatabases.NorthwindTraders);
            using var encrypted = AccessReader.Open(AceEncryptedDb, new AccessReaderOptions { Password = Password });

            // Decryption is only correct if it reproduces the original byte for byte. Comparing
            // against the unencrypted twin is the only assertion that actually proves that.
            List<string> tables = plain.ListTables().OrderBy(t => t).ToList();
            encrypted.ListTables().OrderBy(t => t).Should().Equal(tables);

            foreach (string table in tables)
            {
                List<object[]> a = plain.StreamRows(table).ToList();
                List<object[]> b = encrypted.StreamRows(table).ToList();

                b.Should().HaveCount(a.Count, because: $"'{table}' should decrypt to the same rows");
                for (int r = 0; r < a.Count; r++)
                    b[r].Should().Equal(a[r], because: $"'{table}' row {r} should decrypt identically");
            }
        }

        [Fact]
        public void AceEncrypted_WithWrongPassword_Throws()
        {
            if (!HaveLocalFixture(AceEncryptedDb)) return;

            // Agile encryption stores a verifier, so a wrong password is a definite answer rather
            // than garbage that happens not to parse.
            Action act = () => AccessReader.Open(AceEncryptedDb,
                new AccessReaderOptions { Password = "not the password" });

            act.Should().Throw<InvalidOperationException>().WithMessage("*does not match*");
        }

        [Fact]
        public void AceEncrypted_WithNoPassword_ThrowsAskingForOne()
        {
            if (!HaveLocalFixture(AceEncryptedDb)) return;

            // The failure mode that mattered: before, this opened fine and then reported zero
            // tables, which looks like an empty database rather than an unreadable one.
            Action act = () => AccessReader.Open(AceEncryptedDb);

            act.Should().Throw<InvalidOperationException>().WithMessage("*encrypted*");
        }

        [Fact]
        public void UnencryptedDatabase_ReportsNotEncrypted()
        {
            if (!TestDatabases.IsReadable(TestDatabases.NorthwindTraders)) return;

            using var reader = AccessReader.Open(TestDatabases.NorthwindTraders);

            reader.IsEncrypted.Should().BeFalse();
        }
    }
}
