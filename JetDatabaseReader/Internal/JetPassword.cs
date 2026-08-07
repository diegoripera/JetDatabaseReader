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
    /// Layout: 40 bytes at 0x42, the password as UTF-16LE, obfuscated twice. First with the fixed
    /// header mask that covers the whole 0x18..0x95 region, then — and this is the part that is
    /// easy to miss — with a four-byte value derived from the database's creation date, repeated
    /// down the field. 40 bytes is 20 characters, which is also Access's limit for a Jet4 database
    /// password; a longer one is silently truncated when set.
    /// </summary>
    internal static class JetPassword
    {
        public const int Offset = 0x42;
        public const int Length = 40;

        /// <summary>
        /// The header mask over the password field.
        ///
        /// Recovered empirically: a Jet4 database with no password leaves this field holding
        /// nothing but the two masks, so reading it out of a known-passwordless file whose
        /// creation date is known yields the constant. Confirmed against databases created on
        /// purpose with known passwords of 8, 18 and 20 characters — each decoded to exactly the
        /// password that was set, with no residue.
        /// </summary>
        private static readonly byte[] Mask =
        {
            0x86, 0xFB, 0xEC, 0x37, 0x5D, 0x44, 0x9C, 0xFA, 0xC6, 0x5E,
            0x28, 0xE6, 0x13, 0xB6, 0x8A, 0x60, 0x54, 0x94, 0x7B, 0x36,
            0xF5, 0x72, 0xDF, 0xB1, 0x77, 0xF4, 0x13, 0x43, 0xCF, 0xAF,
            0xB1, 0x33, 0x34, 0x61, 0x79, 0x5B, 0x92, 0xB5, 0x7C, 0x2A
        };

        /// <summary>
        /// The creation date lives at 0x72 as an OLE Automation double, under the same header
        /// mask. Only its top four bytes are needed — everything below them is the time of day,
        /// which the day-count truncation throws away — so only those four mask bytes are kept.
        /// </summary>
        private const int DateOffset = 0x72;
        private const byte DateMask4 = 0x60;   // 0x76: only the top three bits are ever read
        private const byte DateMask5 = 0x3E;   // 0x77
        private const byte DateMask6 = 0x60;   // 0x78
        private const byte DateMask7 = 0x26;   // 0x79

        /// <summary>
        /// The four-byte value the password field is additionally XOR'd with: the creation date's
        /// whole-day count, little-endian.
        ///
        /// Returns false when the header does not hold a date this can read. That happens for a
        /// date outside 1989-09-19 .. 2079-06-06, where zeroing the low mantissa bytes would no
        /// longer be free — and, more usefully, for any file whose 0x72 field is not a date at
        /// all. Refusing to guess there is the point: a wrong date mask turns an unprotected
        /// database into a password-protected one, and the caller gets locked out of a file that
        /// was never locked.
        /// </summary>
        private static bool TryGetDateMask(byte[] header, out uint mask)
        {
            mask = 0;
            if (header == null || header.Length < DateOffset + 8) return false;

            int b4 = header[DateOffset + 4] ^ DateMask4;
            int b5 = header[DateOffset + 5] ^ DateMask5;
            int b6 = header[DateOffset + 6] ^ DateMask6;
            int b7 = header[DateOffset + 7] ^ DateMask7;

            // Exponent 0x40E — the double is in [2^15, 2^16), so its unit place sits at mantissa
            // bit 37 and every bit below is a fraction of a day. Zeroing the four low bytes is
            // then exact for the truncation to an integer, which is all the mask uses.
            if (b7 != 0x40 || (b6 & 0xF0) != 0xE0) return false;

            long bits = ((long)b7 << 56) | ((long)b6 << 48) | ((long)b5 << 40) | ((long)(b4 & 0xE0) << 32);
            int days = (int)BitConverter.Int64BitsToDouble(bits);

            mask = unchecked((uint)days);
            return true;
        }

        /// <summary>
        /// Reads the password out of a database header, or an empty string when none is set.
        /// Returns null when the header is too short, or when the creation date it would need is
        /// not readable.
        /// </summary>
        public static string Decode(byte[] header)
        {
            if (header == null || header.Length < Offset + Length) return null;
            if (!TryGetDateMask(header, out uint dateMask)) return null;

            var raw = new byte[Length];
            bool anyDifference = false;

            for (int i = 0; i < Length; i++)
            {
                byte date = (byte)(dateMask >> ((i & 3) * 8));
                raw[i] = (byte)(header[Offset + i] ^ Mask[i] ^ date);
                if (raw[i] != 0) anyDifference = true;
            }

            if (!anyDifference) return string.Empty;

            // A 20-character password fills the field exactly and has no terminator; anything
            // shorter is NUL-padded, and trailing garbage past the terminator is possible.
            string text = Encoding.Unicode.GetString(raw);
            int end = text.IndexOf('\0');
            return end >= 0 ? text.Substring(0, end) : text;
        }

        /// <summary>
        /// True when the header carries a database password.
        ///
        /// The check before this one read a single byte at 0x62 and tested two bits. That byte
        /// sits inside this field — it is the low half of the seventeenth character — so it only
        /// looked like a flag by coincidence, and would misreport for passwords whose seventeenth
        /// character happened to clear those bits.
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
