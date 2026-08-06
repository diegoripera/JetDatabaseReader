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
        // Local-only fixtures, kept out of the repository (see .gitignore). The password is a
        // throwaway for these test files; it protects nothing of value.
        private static readonly string JetPasswordDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "AdventureLT2008_encrypted.mdb");

        private static readonly string AceEncryptedDb =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "NorthwindTraders_encrypted.accdb");

        private const string Password = "This_Pwd_IsReally_dificult_to_guess_123!";

        // Access truncates a Jet4 database password to 20 characters when it is set.
        private const string StoredPassword = "This_Pwd_IsReally_di";

        // ── Jet4 database password (.mdb) ─────────────────────────────────

        [Fact]
        public void Jet4_WithCorrectPassword_Opens()
        {
            if (!File.Exists(JetPasswordDb)) return;

            using var reader = AccessReader.Open(JetPasswordDb, new AccessReaderOptions { Password = Password });

            reader.IsPasswordProtected.Should().BeTrue();
            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact]
        public void Jet4_WithTruncatedPassword_Opens()
        {
            if (!File.Exists(JetPasswordDb)) return;

            // Supplying exactly what Access stored must work too.
            using var reader = AccessReader.Open(JetPasswordDb,
                new AccessReaderOptions { Password = StoredPassword });

            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact]
        public void Jet4_WithWrongPassword_Throws()
        {
            if (!File.Exists(JetPasswordDb)) return;

            Action act = () => AccessReader.Open(JetPasswordDb,
                new AccessReaderOptions { Password = "not the password" });

            act.Should().Throw<InvalidOperationException>().WithMessage("*does not match*");
        }

        [Fact]
        public void Jet4_WithNoPassword_ThrowsAskingForOne()
        {
            if (!File.Exists(JetPasswordDb)) return;

            Action act = () => AccessReader.Open(JetPasswordDb);

            act.Should().Throw<InvalidOperationException>().WithMessage("*database password*");
        }

        [Fact]
        public void Jet4_ProtectedData_MatchesTheUnprotectedTwin()
        {
            if (!File.Exists(JetPasswordDb) || !File.Exists(TestDatabases.AdventureWorks)) return;

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
        public void AceEncrypted_CorrectPassword_PassesTheFormatsOwnVerifier()
        {
            if (!File.Exists(AceEncryptedDb)) return;

            // Agile encryption stores a verifier, so getting past it is proof the key derivation
            // is right — a wrong key cannot produce a matching verifier hash. Decryption then
            // fails later, on the page IV, which is a different and clearly-named failure.
            Action act = () => AccessReader.Open(AceEncryptedDb, new AccessReaderOptions { Password = Password });

            act.Should().Throw<NotSupportedException>()
               .WithMessage("*password was accepted*",
                   because: "the key is correct; only the per-page IV derivation is unresolved");
        }

        [Fact(Skip = "ACE page IV derivation unresolved — see CHANGELOG. Key derivation is verified.")]
        public void AceEncrypted_WithCorrectPassword_Opens()
        {
            using var reader = AccessReader.Open(AceEncryptedDb, new AccessReaderOptions { Password = Password });

            reader.IsEncrypted.Should().BeTrue();
            reader.ListTables().Should().NotBeEmpty();
        }

        [Fact(Skip = "ACE page IV derivation unresolved — see CHANGELOG. Key derivation is verified.")]
        public void AceEncrypted_DecryptsToTheSameDataAsThePlainTwin()
        {
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
            if (!File.Exists(AceEncryptedDb)) return;

            // Agile encryption stores a verifier, so a wrong password is a definite answer rather
            // than garbage that happens not to parse.
            Action act = () => AccessReader.Open(AceEncryptedDb,
                new AccessReaderOptions { Password = "not the password" });

            act.Should().Throw<InvalidOperationException>().WithMessage("*does not match*");
        }

        [Fact]
        public void AceEncrypted_WithNoPassword_ThrowsAskingForOne()
        {
            if (!File.Exists(AceEncryptedDb)) return;

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
