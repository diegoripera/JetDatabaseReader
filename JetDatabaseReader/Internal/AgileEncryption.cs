using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace JetDatabaseReader
{
    /// <summary>
    /// ECMA-376 agile encryption, as used by ACE ("Encrypt with Password" in Access 2010+).
    ///
    /// Unlike a Jet4 database password — which encrypts nothing — this really does encrypt every
    /// page. Page 0 stays readable and carries an XML descriptor naming the cipher, the hash, the
    /// salts and the iteration count; everything else is ciphertext.
    ///
    /// The scheme is MS-OFFCRYPTO §2.3.4.10-15: iterate a hash over the password to get a key,
    /// use it to unwrap the verifier (which proves the password) and the package key, then decrypt
    /// the payload in fixed-size segments, each with an IV derived from its index.
    /// </summary>
    internal sealed class AgileEncryption
    {
        // Fixed block keys from the specification.
        private static readonly byte[] BlockKeyVerifierInput = { 0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79 };
        private static readonly byte[] BlockKeyVerifierValue = { 0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E };
        private static readonly byte[] BlockKeySecretKey     = { 0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6 };

        /// <summary>
        /// Offset of the database's encoding key, and the fixed value the header masks it with.
        ///
        /// Access departs from the specification here. MS-OFFCRYPTO says a segment's blockKey is
        /// its zero-based index; Access instead uses <c>encodingKey XOR pageNumber</c>. The
        /// encoding key sits at 0x3E, XOR-masked like the rest of the Jet header — an unencrypted
        /// database stores zero there, so the mask is simply what such a file contains. Two
        /// unrelated unencrypted databases (one .mdb, one .accdb) both hold FB 8A BC 4E, which is
        /// how this constant was obtained.
        /// </summary>
        private const int EncodingKeyOffset = 0x3E;
        private static readonly byte[] EncodingKeyMask = { 0xFB, 0x8A, 0xBC, 0x4E };

        private readonly byte[] _packageKey;   // decrypts the data
        private readonly byte[] _dataSalt;     // seeds each page's IV
        private readonly string _dataHash;
        private readonly int _dataBlockSize;
        private readonly uint _encodingKey;

        private AgileEncryption(byte[] packageKey, byte[] dataSalt, string dataHash,
                                int dataBlockSize, uint encodingKey)
        {
            _packageKey = packageKey;
            _dataSalt = dataSalt;
            _dataHash = dataHash;
            _dataBlockSize = dataBlockSize;
            _encodingKey = encodingKey;
        }

        /// <summary>Reads the database's encoding key out of page 0, undoing the header mask.</summary>
        public static uint ReadEncodingKey(byte[] page0)
        {
            if (page0 == null || page0.Length < EncodingKeyOffset + 4) return 0;

            uint key = 0;
            for (int i = 3; i >= 0; i--)
                key = (key << 8) | (uint)(page0[EncodingKeyOffset + i] ^ EncodingKeyMask[i]);

            return key;
        }

        /// <summary>Finds the descriptor in page 0, or null when the page holds no agile descriptor.</summary>
        public static string FindDescriptor(byte[] page0)
        {
            if (page0 == null) return null;

            int start = IndexOf(page0, Encoding.ASCII.GetBytes("<?xml"));
            if (start < 0) return null;

            byte[] closing = Encoding.ASCII.GetBytes("</encryption>");
            int end = IndexOf(page0, closing, start);
            if (end < 0) return null;

            return Encoding.UTF8.GetString(page0, start, end - start + closing.Length);
        }

        /// <summary>
        /// Derives the package key from a password. Returns null when the password is wrong —
        /// the format carries a verifier, so that is a definite answer rather than a guess.
        /// </summary>
        /// <exception cref="NotSupportedException">The descriptor asks for an unsupported algorithm.</exception>
        /// <summary>
        /// Access limits a database password to 20 characters and silently truncates anything
        /// longer when it is set, so the key is derived from the truncation, not from what the
        /// user typed. Both forms are tried — the verifier makes that a definite test, not a guess.
        /// </summary>
        private const int AccessPasswordLimit = 20;

        public static AgileEncryption Create(string descriptorXml, string password, uint encodingKey)
        {
            AgileEncryption result = CreateWith(descriptorXml, password ?? string.Empty, encodingKey);
            if (result != null) return result;

            if (password != null && password.Length > AccessPasswordLimit)
                return CreateWith(descriptorXml, password.Substring(0, AccessPasswordLimit), encodingKey);

            return null;
        }

        private static AgileEncryption CreateWith(string descriptorXml, string password, uint encodingKey)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(descriptorXml);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/office/2006/encryption");
            ns.AddNamespace("p", "http://schemas.microsoft.com/office/2006/keyEncryptor/password");

            XmlNode keyData = doc.SelectSingleNode("//e:keyData", ns);
            XmlNode encKey = doc.SelectSingleNode("//p:encryptedKey", ns);
            if (keyData == null || encKey == null)
                throw new NotSupportedException("The encryption descriptor has no password key encryptor.");

            string dataCipher = Attr(keyData, "cipherAlgorithm");
            string dataChain  = Attr(keyData, "cipherChaining");
            string dataHash   = Attr(keyData, "hashAlgorithm");
            int dataBlockSize = AttrInt(keyData, "blockSize");
            byte[] dataSalt   = Convert.FromBase64String(Attr(keyData, "saltValue"));

            RequireSupported(dataCipher, dataChain, dataHash);

            string keyCipher = Attr(encKey, "cipherAlgorithm");
            string keyChain  = Attr(encKey, "cipherChaining");
            string keyHash   = Attr(encKey, "hashAlgorithm");
            RequireSupported(keyCipher, keyChain, keyHash);

            byte[] keySalt = Convert.FromBase64String(Attr(encKey, "saltValue"));
            int spinCount  = AttrInt(encKey, "spinCount");
            int keyBytes   = AttrInt(encKey, "keyBits") / 8;

            byte[] verifierInput = Convert.FromBase64String(Attr(encKey, "encryptedVerifierHashInput"));
            byte[] verifierValue = Convert.FromBase64String(Attr(encKey, "encryptedVerifierHashValue"));
            byte[] encryptedKey  = Convert.FromBase64String(Attr(encKey, "encryptedKeyValue"));

            // H_0 = H(salt + password); H_n = H(LE32(n-1) + H_n-1), spinCount times.
            byte[] spun = SpinHash(keyHash, keySalt, password ?? string.Empty, spinCount);

            byte[] inputKey = DeriveKey(keyHash, spun, BlockKeyVerifierInput, keyBytes);
            byte[] valueKey = DeriveKey(keyHash, spun, BlockKeyVerifierValue, keyBytes);

            byte[] verifier = Decrypt(verifierInput, inputKey, keySalt);
            byte[] expected = Decrypt(verifierValue, valueKey, keySalt);

            using (HashAlgorithm h = CreateHash(keyHash))
            {
                byte[] actual = h.ComputeHash(verifier);

                // expected is padded up to the cipher block size; compare only the hash itself.
                if (actual.Length > expected.Length) return null;
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] != expected[i]) return null;
            }

            byte[] secretKey = DeriveKey(keyHash, spun, BlockKeySecretKey, keyBytes);
            byte[] packageKey = Decrypt(encryptedKey, secretKey, keySalt);

            // The unwrapped key can come back padded to the cipher block size.
            int wanted = AttrInt(keyData, "keyBits") / 8;
            if (packageKey.Length > wanted) Array.Resize(ref packageKey, wanted);

            return new AgileEncryption(packageKey, dataSalt, dataHash, dataBlockSize, encodingKey);
        }

        /// <summary>
        /// Decrypts one page in place. Every page is its own CBC unit with its own IV, so pages
        /// stay independently readable and the reader can keep seeking freely.
        /// </summary>
        public void DecryptPage(byte[] buffer, int length, long pageNumber)
        {
            if (length <= 0) return;

            // IV = H(dataSalt + LE32(blockKey)) truncated to the block size, where blockKey is
            // encodingKey XOR pageNumber rather than the plain segment index the specification
            // describes.
            uint blockKey = _encodingKey ^ (uint)pageNumber;

            byte[] iv;
            using (HashAlgorithm h = CreateHash(_dataHash))
            {
                var seed = new byte[_dataSalt.Length + 4];
                Buffer.BlockCopy(_dataSalt, 0, seed, 0, _dataSalt.Length);
                WriteLe32(seed, _dataSalt.Length, blockKey);

                iv = h.ComputeHash(seed);
            }
            if (iv.Length != _dataBlockSize) Array.Resize(ref iv, _dataBlockSize);

            // CBC needs whole blocks; a trailing partial block cannot occur for a full page.
            int whole = length - (length % _dataBlockSize);
            if (whole == 0) return;

            byte[] plain = Decrypt(buffer, 0, whole, _packageKey, iv);
            Buffer.BlockCopy(plain, 0, buffer, 0, whole);
        }

        // ── Key derivation ────────────────────────────────────────────────

        private static byte[] SpinHash(string hashName, byte[] salt, string password, int spinCount)
        {
            using (HashAlgorithm h = CreateHash(hashName))
            {
                byte[] pwd = Encoding.Unicode.GetBytes(password);

                var seed = new byte[salt.Length + pwd.Length];
                Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
                Buffer.BlockCopy(pwd, 0, seed, salt.Length, pwd.Length);

                byte[] hash = h.ComputeHash(seed);

                var round = new byte[4 + hash.Length];
                for (int i = 0; i < spinCount; i++)
                {
                    WriteLe32(round, 0, (uint)i);
                    Buffer.BlockCopy(hash, 0, round, 4, hash.Length);
                    hash = h.ComputeHash(round);
                }
                return hash;
            }
        }

        private static byte[] DeriveKey(string hashName, byte[] spun, byte[] blockKey, int keyBytes)
        {
            using (HashAlgorithm h = CreateHash(hashName))
            {
                var seed = new byte[spun.Length + blockKey.Length];
                Buffer.BlockCopy(spun, 0, seed, 0, spun.Length);
                Buffer.BlockCopy(blockKey, 0, seed, spun.Length, blockKey.Length);

                byte[] hash = h.ComputeHash(seed);

                var key = new byte[keyBytes];
                if (hash.Length >= keyBytes)
                {
                    Buffer.BlockCopy(hash, 0, key, 0, keyBytes);
                }
                else
                {
                    // Short hash is padded with 0x36, per the specification.
                    Buffer.BlockCopy(hash, 0, key, 0, hash.Length);
                    for (int i = hash.Length; i < keyBytes; i++) key[i] = 0x36;
                }
                return key;
            }
        }

        // ── Primitives ────────────────────────────────────────────────────

        private static void RequireSupported(string cipher, string chaining, string hash)
        {
            if (!string.Equals(cipher, "AES", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Unsupported cipher '{cipher}'; only AES is implemented.");

            if (!string.Equals(chaining, "ChainingModeCBC", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Unsupported chaining mode '{chaining}'; only CBC is implemented.");

            if (CreateHashOrNull(hash) == null)
                throw new NotSupportedException($"Unsupported hash algorithm '{hash}'.");
        }

        private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv) =>
            Decrypt(data, 0, data.Length, key, iv);

        private static byte[] Decrypt(byte[] data, int offset, int count, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = key.Length * 8;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = key;

                // The IV must be exactly one block; salts are sized to match but be explicit.
                var blockIv = new byte[aes.BlockSize / 8];
                Buffer.BlockCopy(iv, 0, blockIv, 0, Math.Min(iv.Length, blockIv.Length));
                aes.IV = blockIv;

                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(data, offset, count);
            }
        }

        private static HashAlgorithm CreateHash(string name)
        {
            HashAlgorithm h = CreateHashOrNull(name);
            if (h == null) throw new NotSupportedException($"Unsupported hash algorithm '{name}'.");
            return h;
        }

        private static HashAlgorithm CreateHashOrNull(string name)
        {
            if (name == null) return null;
            switch (name.ToUpperInvariant())
            {
                case "SHA512": return SHA512.Create();
                case "SHA384": return SHA384.Create();
                case "SHA256": return SHA256.Create();
                case "SHA1":   return SHA1.Create();
                default:       return null;
            }
        }

        private static void WriteLe32(byte[] b, int offset, uint value)
        {
            b[offset]     = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        private static string Attr(XmlNode node, string name) => node.Attributes?[name]?.Value;

        private static int AttrInt(XmlNode node, string name) =>
            int.Parse(Attr(node, name), CultureInfo.InvariantCulture);

        private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
        {
            for (int i = from; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
