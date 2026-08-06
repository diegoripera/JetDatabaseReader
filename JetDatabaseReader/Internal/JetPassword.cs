using System;
using System.Text;

namespace JetDatabaseReader
{
    /// <summary>
    /// The Jet4 "database password" stored in the header.
    ///
    /// This is access control enforced by the Jet engine, not encryption: with a database password
    /// set, Access refuses to open the file, but the page bodies stay in plain text on disk. Any
    /// tool reading the file directly — this library, mdbtools, a hex editor — sees the data. Real
    /// page encryption only arrived with the ACE format's "Encrypt with Password".
    ///
    /// Layout: 40 bytes at 0x42, the password as UTF-16LE, XOR'd with a fixed mask. 40 bytes is
    /// 20 characters, which is also Access's limit for a Jet4 database password — a longer one is
    /// silently truncated when set.
    /// </summary>
    internal static class JetPassword
    {
        public const int Offset = 0x42;
        public const int Length = 40;

        /// <summary>
        /// The mask the password is XOR'd with.
        ///
        /// Recovered empirically rather than taken on faith: a Jet4 database with no password
        /// stores the bare mask in this field, so reading it out of a known-passwordless file
        /// yields the constant directly. It was then confirmed by decoding a file whose password
        /// was known — the 40 bytes produced exactly the expected 20 characters, with no residue.
        /// </summary>
        private static readonly byte[] Mask =
        {
            0xE2, 0x65, 0xEC, 0x37, 0x39, 0xDA, 0x9C, 0xFA, 0xA2, 0xC0,
            0x28, 0xE6, 0x77, 0x28, 0x8A, 0x60, 0x30, 0x0A, 0x7B, 0x36,
            0x91, 0xEC, 0xDF, 0xB1, 0x13, 0x6A, 0x13, 0x43, 0xAB, 0x31,
            0xB1, 0x33, 0x50, 0xFF, 0x79, 0x5B, 0xF6, 0x2B, 0x7C, 0x2A
        };

        /// <summary>
        /// Reads the password out of a database header, or an empty string when none is set.
        /// Returns null when the header is too short to contain the field.
        /// </summary>
        public static string Decode(byte[] header)
        {
            if (header == null || header.Length < Offset + Length) return null;

            var raw = new byte[Length];
            bool anyDifference = false;

            for (int i = 0; i < Length; i++)
            {
                raw[i] = (byte)(header[Offset + i] ^ Mask[i]);
                if (raw[i] != 0) anyDifference = true;
            }

            if (!anyDifference) return string.Empty;

            // Trailing garbage past the terminator is possible; stop at the first NUL character.
            string text = Encoding.Unicode.GetString(raw);
            int end = text.IndexOf('\0');
            return end >= 0 ? text.Substring(0, end) : text;
        }

        /// <summary>
        /// True when the header carries a database password.
        ///
        /// The previous check read a single byte at 0x62 and tested two bits. That byte is inside
        /// this field — it is the low half of the seventeenth character — so it only looked like a
        /// flag by coincidence, and would misreport for passwords whose seventeenth character
        /// happened to clear those bits.
        /// </summary>
        public static bool IsProtected(byte[] header) => !string.IsNullOrEmpty(Decode(header));

        /// <summary>
        /// Compares a supplied password with the stored one. Access truncates a Jet4 database
        /// password to 20 characters when setting it, so a longer supplied password is compared on
        /// the same terms rather than rejected outright.
        /// </summary>
        public static bool Matches(byte[] header, string supplied)
        {
            string stored = Decode(header);
            if (string.IsNullOrEmpty(stored)) return true;
            if (supplied == null) return false;

            int max = Length / 2;
            if (supplied.Length > max) supplied = supplied.Substring(0, max);

            return string.Equals(stored, supplied, StringComparison.Ordinal);
        }
    }
}
