using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JetDatabaseReader
{
    /// <summary>
    /// Pure-managed reader for Microsoft Access JET databases (.mdb / .accdb).
    /// No OleDB, ODBC, or ACE/Jet driver installation required.
    ///
    /// Supports:
    ///   Jet4+ – Access 2000-2019 (.mdb / .accdb)
    ///   Jet3  – Access 97 (.mdb): the page layout is implemented, but no Jet3 file has been
    ///           available to test it against, and the ACE engine can no longer create one.
    ///
    /// Features:
    ///   ✓ All standard data types (Text, Integer, Date, GUID, Currency, etc.)
    ///   ✓ MEMO fields (inline + single-page + multi-page LVAL chains)
    ///   ✓ OLE Object fields — auto-detects images (JPEG/PNG/GIF/BMP), documents (PDF/DOC/RTF), archives (ZIP)
    ///   ✓ Overflow rows — rows whose payload lives on another page are followed, not skipped
    ///   ✓ Password-protected and encrypted databases — Jet4 database passwords and ACE agile
    ///     encryption (AES); supply the password via AccessReaderOptions.Password
    ///   ✓ Linked tables — reported with their source, and readable when that source is another
    ///     Access file
    ///   ✓ Streaming API — process millions of rows without OOM (StreamRows, ReadTableAsDataTable)
    ///   ✓ Progress reporting — IProgress&lt;int&gt; callbacks for long operations
    ///   ✓ Page cache — 256-page LRU cache (default 1 MB) for 50%+ performance boost
    ///   ✓ Catalog caching — single MSysObjects scan, reused across calls
    ///   ✓ Non-Western text — auto-detects code page from database header (Cyrillic, Japanese, etc.)
    ///
    /// Limitations:
    ///   ✗ Complex columns (Type 0x12 — Attachment, Multi-Value, append-only Memo history):
    ///     the 4-byte id stored in the row is surfaced, not the values it points at
    ///   ✗ Linked tables whose source is not an Access file (ODBC, Excel, text) — listed, not read
    ///   ✗ Write operations — this is a read-only library
    ///
    /// Based on the mdbtools format specification:
    ///   https://github.com/mdbtools/mdbtools/blob/master/HACKING.md
    /// </summary>
    public sealed class AccessReader : IAccessReader
    {
        // ── Column type codes (mdbtools HACKING.md) ──────────────────────
        private const byte T_BOOL    = 0x01; // 1 bit  – stored in null_mask
        private const byte T_BYTE    = 0x02; // 1 byte
        private const byte T_INT     = 0x03; // 2 bytes (signed)
        private const byte T_LONG    = 0x04; // 4 bytes (signed)
        private const byte T_MONEY   = 0x05; // 8 bytes (int64 / 10000)
        private const byte T_FLOAT   = 0x06; // 4 bytes (IEEE 754)
        private const byte T_DOUBLE  = 0x07; // 8 bytes (IEEE 754)
        private const byte T_DATETIME= 0x08; // 8 bytes (OA date)
        private const byte T_BINARY  = 0x09; // variable (≤ 255 bytes)
        private const byte T_TEXT    = 0x0A; // variable (UCS-2 in Jet4, ANSI in Jet3)
        private const byte T_OLE     = 0x0B; // LVAL
        private const byte T_MEMO    = 0x0C; // LVAL or inline
        private const byte T_GUID    = 0x0F; // 16 bytes
        private const byte T_NUMERIC = 0x10; // 17 bytes scaled decimal

        /// <summary>
        /// A "complex" column — Attachment, Multi-Value, or append-only Memo history, all added in
        /// Access 2007. The row itself holds only a 4-byte id; the values live in hidden system
        /// tables keyed by it. Not resolved: the id is surfaced as-is rather than followed.
        /// </summary>
        private const byte T_COMPLEX = 0x12;

        // Catalog (MSysObjects) constants
        private const int  OBJ_TABLE        = 1;  // local table
        private const int  OBJ_LINKED_ODBC  = 4;  // linked through an ODBC driver
        private const int  OBJ_LINKED       = 6;  // linked to a file: Access, Excel, text, ...
        private const uint SYSTABLE_MASK    = 0x80000002U;

        // ── Format-specific offsets ───────────────────────────────────────

        // Data page
        private readonly int _dpTDefOff;    // offset of tdef_pg (4 bytes)
        private readonly int _dpNumRows;    // offset of num_rows (2 bytes)
        private readonly int _dpRowsStart;  // offset of first row-offset entry

        // TDEF page (absolute offsets within the TDEF byte array)
        private readonly int _tdNumCols;    // offset of num_cols    (2 bytes)
        private readonly int _tdNumRealIdx; // offset of num_real_idx (4 bytes)
        private readonly int _tdBlockEnd;   // first byte after table-definition block
        private readonly int _tdUsedPages;  // offset of used_pages  (4 bytes), -1 when absent

        // Column descriptor (per-column, fixed-size block)
        private readonly int _colDescSz;
        private readonly int _colTypeOff;
        private readonly int _colVarOff;    // offset_V – var-col index
        private readonly int _colFixedOff;  // offset_F – byte offset in fixed area
        private readonly int _colSzOff;     // col_len
        private readonly int _colFlagsOff;  // bitmask
        private readonly int _colNumOff;    // col_num (includes deleted)
        private readonly int _colPrecOff;   // col_prec  – T_NUMERIC total digits
        private readonly int _colScaleOff;  // col_scale – T_NUMERIC digits after the point

        // Per-real-index entry size (skipped during column parsing)
        private readonly int _realIdxEntrySz;

        // Row field sizes (differ between Jet3 and Jet4)
        private readonly int _numColsFldSz;  // 1 or 2
        private readonly int _varEntrySz;    // 1 or 2  (var_table entry)
        private readonly int _eodFldSz;      // 1 or 2
        private readonly int _varLenFldSz;   // 1 or 2

        private readonly int  _pgSz;
        private readonly bool _jet4;
        private readonly FileStream _fs;
        private readonly Encoding _ansiEncoding;
        private readonly int _codePage;
        private readonly object _cacheLock = new object();
        private readonly object _catalogLock = new object();
        private readonly object _indexLock = new object();

        /// <summary>Guards the non-atomic Seek+Read pair against the shared <see cref="_fs"/>.</summary>
        private readonly object _ioLock = new object();
        private volatile List<CatalogEntry> _catalogCache;

        /// <summary>Linked tables found in the same catalog scan. Published before the catalog.</summary>
        private volatile List<LinkedTable> _linkedCache;
        private volatile LruCache<long, byte[]> _pageCache;

        /// <summary>
        /// Maps a table's TDEF page number to the ascending runs of data pages owned by it.
        /// Built for free during the single catalog scan in <see cref="GetUserTables"/>, which
        /// already reads every page header. Without it, every read method has to walk the whole
        /// file to find its own pages — so reading N tables costs N whole-file scans.
        /// </summary>
        private volatile Dictionary<long, PageRun[]> _pageIndex;

        /// <summary>Number of pages covered by <see cref="_pageIndex"/>. Read via Interlocked.</summary>
        private long _indexedPages;

        private bool _disposed;
        private long _cacheHits;
        private long _cacheMisses;
        private readonly bool _isPasswordProtected;

        /// <summary>Page decryptor for an encrypted ACE database; null when pages are plain text.</summary>
        private AgileEncryption _crypto;

        /// <summary>When true, GetUserTables logs verbose hex dumps for debugging. Default: false.</summary>
        public bool DiagnosticsEnabled { get; set; }

        /// <summary>Maximum number of pages to keep in cache. 0 = unlimited, -1 = disabled. Default: 256 (1 MB for 4K pages).</summary>
        public int PageCacheSize { get; set; } = 256;

        /// <summary>
        /// Has no effect. Nothing reads this; page reads are serialised on the shared file handle.
        /// Kept so existing code keeps compiling.
        /// </summary>
        [Obsolete("Has no effect — page reads are serialised on the shared file handle.")]
        public bool ParallelPageReadsEnabled { get; set; }

        /// <summary>
        /// How OLE Object columns are rendered. Default: <see cref="OleObjectMode.DataUri"/>.
        /// </summary>
        public OleObjectMode OleObjectMode { get; set; } = OleObjectMode.DataUri;

        // ── Constructor ───────────────────────────────────────────────────

        static AccessReader()
        {
            // On .NET Core / .NET 5+ code-page encodings (e.g. Windows-1252) are not
            // available by default. Register them once so GetEncoding() works for any
            // ANSI code page stored in the JET database header.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>Opens <paramref name="path"/> and detects the JET version.</summary>
        private AccessReader(string path, AccessReaderOptions options)
        {
            Guard.NotNullOrEmpty(path, nameof(path));
            Guard.NotNull(options, nameof(options));

            DiagnosticsEnabled = options.DiagnosticsEnabled;
            PageCacheSize = options.PageCacheSize;
#pragma warning disable CS0618 // carrying an obsolete option through is deliberate
            ParallelPageReadsEnabled = options.ParallelPageReadsEnabled;
#pragma warning restore CS0618
            OleObjectMode = options.OleObjectMode;

            // Two deliberate choices here:
            //
            // bufferSize — every read is exactly one page at a seeked offset. With the default
            // 4 KB buffer FileStream bypasses buffering entirely and issues one syscall per page.
            // A larger buffer lets a front-to-back scan serve most pages from memory, because
            // FileStream keeps its read buffer across a Seek that lands inside it. At 64 KB that
            // is one syscall per 16 pages instead of one per page.
            //
            // SequentialScan — tells the OS cache manager to read ahead and drop pages behind us,
            // which is what a full-file scan wants.
            _fs = new FileStream(path, FileMode.Open, options.FileAccess, options.FileShare,
                                 options.FileBufferSize, FileOptions.SequentialScan);

            // Everything past this point can reject the file — wrong password, encrypted pages,
            // not a JET database at all. Without this the handle stays open until a collection,
            // so a caller retrying with a different password leaks one per attempt.
            try
            {

            // Page 0 whole, not just the 0x80-byte header: an encrypted ACE database keeps this
            // page in plain text and stores its encryption descriptor further in, past 0x2A0.
            var hdr = new byte[Math.Min(4096, Math.Max(0x80, _fs.Length))];
            ReadFully(hdr, hdr.Length);

            // Offset 0x14: 0 = Jet3, ≥ 1 = Jet4+
            byte ver = hdr[0x14];
            _jet4 = (ver >= 1);
            _pgSz = _jet4 ? 4096 : 2048;

            // Offset 0x3C (Jet4) or 0x3A (Jet3): sort order / code page ID
            // Common: 1033=en-US(1252), 1049=ru(1251), 1041=ja(932)
            int cpOffset = _jet4 ? 0x3C : 0x3A;
            int sortOrder = (hdr.Length > cpOffset + 1) ? Ru16(hdr, cpOffset) : 0;
            _codePage = (sortOrder >> 8) & 0xFF;
            if (_codePage == 0) _codePage = 1252;  // default to Windows-1252 if unknown
            try { _ansiEncoding = Encoding.GetEncoding(_codePage); }
            catch { _ansiEncoding = Encoding.UTF8; _codePage = 65001; }

            // A Jet4 database password does not encrypt anything — the pages stay in plain text
            // and only the Jet engine refuses to open the file. So the password is verified when
            // one is stored, and reading then proceeds normally.
            //
            // The old check read one byte at 0x62 and tested two bits. That byte sits inside the
            // 40-byte password field at 0x42, so it was never a flag: it is the low half of the
            // seventeenth character, and only behaved like one because an unset password leaves
            // the mask's own value there.
            //
            // Decoding that field needs the database's creation date as well as the fixed mask —
            // see JetPassword. Getting that wrong does not merely misreport: it refuses to open
            // databases that were never protected.
            //
            // Jet4 only (ver == 1, Access 2000-2003). Jet3 uses a different field, and ACE always
            // encrypts when a password is set, so its 0x42 bytes are not a Jet4 password field at
            // all — decoding them with the Jet4 mask yields noise that reads as "wrong password".
            _isPasswordProtected = ver == 1 && JetPassword.IsProtected(hdr);

            if (_isPasswordProtected && !JetPassword.Matches(hdr, options.Password))
            {
                throw new InvalidOperationException(options.Password == null
                    ? "This database has a database password. Supply it via AccessReaderOptions.Password."
                    : "The supplied password does not match this database's password.");
            }

            if (_jet4)
            {
                // ── Jet4 / ACE (Access 2000 – 2019, .mdb + .accdb) ──────
                // Data page
                _dpTDefOff    = 4;
                _dpNumRows    = 12;   // extra 4-byte field after tdef_pg
                _dpRowsStart  = 14;

                // TDEF: 8-byte header + 55-byte Jet4 block = 63 total
                //   num_cols    at 8 + 37 = 45
                //   num_real_idx at 8 + 43 = 51
                _tdNumCols    = 45;
                _tdNumRealIdx = 51;
                _tdBlockEnd   = 63;
                _tdUsedPages  = 55;

                // Column descriptor (25 bytes)
                _colDescSz    = 25;
                _colTypeOff   =  0;   // col_type  (1)
                _colVarOff    =  7;   // offset_V  (2): 1+4+2
                _colFixedOff  = 21;   // offset_F  (2): 1+4+2+2+2+2+2+1+1+4
                _colSzOff     = 23;   // col_len   (2)
                _colFlagsOff  = 15;   // bitmask   (1): 1+4+2+2+2+2+2
                _colNumOff    =  5;   // col_num   (2)
                _colPrecOff   = 11;   // col_prec  (1)
                _colScaleOff  = 12;   // col_scale (1)

                _realIdxEntrySz = 12;
                _numColsFldSz   =  2;
                _varEntrySz     =  2;
                _eodFldSz       =  2;
                _varLenFldSz    =  2;
            }
            else
            {
                // ── Jet3 (Access 97, .mdb) ────────────────────────────
                // Data page
                _dpTDefOff    = 4;
                _dpNumRows    =  8;
                _dpRowsStart  = 10;

                // TDEF: 8-byte header + 35-byte Jet3 block = 43 total
                //   num_cols    at 8 + 17 = 25
                //   num_real_idx at 8 + 23 = 31
                _tdNumCols    = 25;
                _tdNumRealIdx = 31;
                _tdBlockEnd   = 43;
                _tdUsedPages  = 35;

                // Column descriptor (18 bytes)
                _colDescSz    = 18;
                _colTypeOff   =  0;   // col_type  (1)
                _colVarOff    =  3;   // offset_V  (2): 1+2
                _colFixedOff  = 14;   // offset_F  (2): 1+2+2+2+2+2+2+1
                _colSzOff     = 16;   // col_len   (2)
                _colFlagsOff  = 13;   // bitmask   (1)
                _colNumOff    =  1;   // col_num   (2)
                _colPrecOff   =  9;   // col_prec  (1)
                _colScaleOff  = 10;   // col_scale (1)

                _realIdxEntrySz =  8;
                _numColsFldSz   =  1;
                _varEntrySz     =  1;
                _eodFldSz       =  1;
                _varLenFldSz    =  1;
            }

            // Format validation comes first: a file that is not a JET database at all should say
            // so, rather than fall through to the encryption check and be reported as encrypted
            // because its page 2 happens not to look like a TDEF.
            if (options.ValidateOnOpen)
            {
                ValidateDatabaseFormat();
            }

            // Page 2 always holds the MSysObjects table definition, so it doubles as the check
            // that pages are readable at all. When it is not a TDEF the pages are encrypted.
            if (!IsCatalogPageReadable())
            {
                SetUpDecryption(hdr, options.Password);

                if (!IsCatalogPageReadable())
                {
                    throw new NotSupportedException(
                        "This database's pages are encrypted and could not be decrypted. " +
                        "Supply the password via AccessReaderOptions.Password, or remove the " +
                        "encryption in Microsoft Access (File > Info > Decrypt Database).");
                }
            }
            }
            catch
            {
                _crypto?.Dispose();
                _fs.Dispose();
                throw;
            }
        }

        /// <summary>
        /// True when the database carries a Jet4 database password. That password is access
        /// control rather than encryption — the page data is stored in plain text either way.
        /// </summary>
        public bool IsPasswordProtected => _isPasswordProtected;

        /// <summary>True when the pages are encrypted and are being decrypted as they are read.</summary>
        public bool IsEncrypted => _crypto != null;

        /// <summary>Reads <paramref name="count"/> bytes from the current position, tolerating short reads.</summary>
        private void ReadFully(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = _fs.Read(buffer, read, count - read);
                if (got == 0) break;
                read += got;
            }
        }

        /// <summary>True when page 2 decodes to a TDEF, which is what a readable database gives.</summary>
        private bool IsCatalogPageReadable()
        {
            var page = new byte[_pgSz];
            _fs.Seek(2L * _pgSz, SeekOrigin.Begin);
            ReadFully(page, _pgSz);

            _crypto?.DecryptPage(page, _pgSz, 2);
            return page[0] == 0x02;
        }

        /// <summary>Derives the page key from the password and confirms it decrypts the catalog.</summary>
        private void SetUpDecryption(byte[] page0, string password)
        {
            string descriptor = AgileEncryption.FindDescriptor(page0);
            if (descriptor == null) return;   // encrypted by some other scheme, or not at all

            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException(
                    "This database is encrypted. Supply the password via AccessReaderOptions.Password.");

            AgileEncryption crypto = AgileEncryption.Create(
                descriptor, password, AgileEncryption.ReadEncodingKey(page0));

            if (crypto == null)
                throw new InvalidOperationException("The supplied password does not match this database's password.");

            _crypto = crypto;
        }

        /// <summary>
        /// Opens a JET database file and returns a new AccessReader instance.
        /// </summary>
        /// <param name="path">Path to the .mdb or .accdb file.</param>
        /// <param name="options">Optional configuration options.</param>
        public static AccessReader Open(string path, AccessReaderOptions options = null)
        {
            Guard.NotNullOrEmpty(path, nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException($"Database file not found: {path}", path);

            options = options ?? new AccessReaderOptions();

            return new AccessReader(path, options);
        }

        private void ValidateDatabaseFormat()
        {
            if (_fs.Length < 128)
                throw new InvalidDataException("File too small to be a valid JET database");

            // Verify the JET magic signature at offset 0: 00 01 00 00
            _fs.Seek(0, SeekOrigin.Begin);
            var magic = new byte[4];
            int read = _fs.Read(magic, 0, 4);
            if (read < 4 || magic[0] != 0x00 || magic[1] != 0x01 || magic[2] != 0x00 || magic[3] != 0x00)
                throw new InvalidDataException(
                    $"File does not have a valid JET magic signature " +
                    $"(expected 00 01 00 00, got {magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}).");
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _fs?.Dispose();
                _crypto?.Dispose();
                lock (_cacheLock)
                {
                    _pageCache?.Clear();
                }
                lock (_catalogLock)
                {
                    _catalogCache?.Clear();
                }
                lock (_indexLock)
                {
                    _pageIndex = null;
                    _indexedPages = 0;
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Drops the catalog, page index, and page cache so the next call re-reads them from disk.
        ///
        /// The reader caches aggressively and never re-validates, which is right for a file nobody
        /// else is writing. The default <see cref="FileShare.ReadWrite"/> does allow another
        /// process — Microsoft Access, an import job — to modify the database underneath a
        /// long-lived reader, and those caches would then serve stale data indefinitely. Call this
        /// when you know the file changed, or drop the reader and open a new one.
        ///
        /// Appended pages are picked up automatically and do not need a refresh; this is for
        /// pages that were rewritten in place.
        /// </summary>
        public void Refresh()
        {
            ThrowIfDisposed();

            lock (_catalogLock)
            {
                _catalogCache = null;
                _linkedCache = null;
                LastDiagnostics = string.Empty;
            }
            lock (_indexLock)
            {
                _pageIndex = null;
                Interlocked.Exchange(ref _indexedPages, 0);
            }
            lock (_cacheLock)
            {
                _pageCache?.Clear();
            }
            lock (_tdefLock)
            {
                _tdefCache.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AccessReader));
        }

        /// <summary>Diagnostic output populated after each call to <see cref="ListTables"/>.</summary>
        public string LastDiagnostics { get; private set; } = string.Empty;

        // ── Low-level helpers

        private static ushort Ru16(byte[] b, int o) =>
            (ushort)(b[o] | (b[o + 1] << 8));

        private static int Ri32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        private static uint Ru32(byte[] b, int o) => (uint)Ri32(b, o);

        private byte[] ReadPage(long n)
        {
            var buf = new byte[_pgSz];
            ReadPageInto(n, buf);
            return buf;
        }

        /// <summary>
        /// Reads page <paramref name="n"/> into <paramref name="buf"/>, which must be exactly
        /// <see cref="_pgSz"/> bytes — MEMO/OLE decoding uses <c>buf.Length</c> as a bound, so an
        /// oversized buffer would widen those checks and let stale bytes through.
        /// </summary>
        private void ReadPageInto(long n, byte[] buf)
        {
            int read = 0;

            // Seek and Read are two calls against one shared file position. Without this lock two
            // threads using the same reader interleave them and each gets bytes from the other's
            // page — silently, as plausible-looking garbage. Caching one open reader and serving
            // concurrent requests from it is the obvious thing to do in a web app, so the pair has
            // to be atomic. An uncontended lock costs far less than the read it guards.
            lock (_ioLock)
            {
                _fs.Seek(n * _pgSz, SeekOrigin.Begin);
                // FileStream.Read is not guaranteed to return all bytes in one call
                while (read < _pgSz)
                {
                    int got = _fs.Read(buf, read, _pgSz - read);
                    if (got == 0) break;
                    read += got;
                }

                // A freshly allocated array was zero-filled; a reused one is not. Clear the tail
                // so a short read at end-of-file cannot expose the previous page's bytes.
                if (read < _pgSz) Array.Clear(buf, read, _pgSz - read);

                // Decryption reuses its cipher and buffers, so it runs under the same lock rather
                // than paying for per-page state. Page 0 carries the header and the encryption
                // descriptor and is never encrypted.
                if (_crypto != null && n > 0) _crypto.DecryptPage(buf, _pgSz, n);
            }
        }

        /// <summary>
        /// Reads a page during a front-to-back scan. Returns the cached copy when one exists,
        /// otherwise fills <paramref name="scratch"/> and returns that.
        ///
        /// Deliberately does NOT populate the cache: a sequential scan touches every page once,
        /// so caching them evicts the LVAL and TDEF pages that do get reused, and yields nothing
        /// in return. The caller must not retain the returned array beyond the current page.
        /// </summary>
        private byte[] ReadPageForScan(long n, byte[] scratch)
        {
            ThrowIfDisposed();

            LruCache<long, byte[]> cache = _pageCache;
            if (cache != null && cache.TryGetValue(n, out byte[] cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            Interlocked.Increment(ref _cacheMisses);
            ReadPageInto(n, scratch);
            return scratch;
        }

        /// <summary>Allocates one page-sized scratch buffer for the duration of a scan.</summary>
        private byte[] NewScanBuffer() => new byte[_pgSz];

        /// <summary>Pages read at a time when sweeping the file for the catalog. 256 pages = 1 MB.</summary>
        private const int ScanBlockPages = 256;

        /// <summary>Reads <paramref name="bytes"/> bytes starting at <paramref name="firstPage"/>.</summary>
        private void ReadBlock(long firstPage, byte[] buffer, int bytes)
        {
            int read = 0;
            lock (_ioLock)
            {
                _fs.Seek(firstPage * _pgSz, SeekOrigin.Begin);
                while (read < bytes)
                {
                    int got = _fs.Read(buffer, read, bytes - read);
                    if (got == 0) break;
                    read += got;
                }
            }

            if (read < bytes) Array.Clear(buffer, read, bytes - read);
        }

        /// <summary>Reads a page through the cache if enabled (PageCacheSize > 0).</summary>
        private byte[] ReadPageCached(long n)
        {
            ThrowIfDisposed();

            if (PageCacheSize < 0) return ReadPage(n);  // cache disabled

            // Lazy-init: only one thread creates the cache; LruCache is internally thread-safe
            // so subsequent TryGetValue/Add calls need no outer lock.
            if (_pageCache == null && PageCacheSize > 0)
            {
                lock (_cacheLock)
                {
                    if (_pageCache == null)
                        _pageCache = new LruCache<long, byte[]>(PageCacheSize);
                }
            }

            if (_pageCache != null && _pageCache.TryGetValue(n, out byte[] cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            Interlocked.Increment(ref _cacheMisses);
            byte[] page = ReadPage(n);
            _pageCache?.Add(n, page);
            return page;
        }

        // ── TDEF reading ──────────────────────────────────────────────────

        /// <summary>
        /// Concatenates the TDEF page chain starting at <paramref name="startPage"/>
        /// into a single byte array.  Pages after the first have their 8-byte
        /// TDEF header stripped before appending.
        /// </summary>
        private byte[] ReadTDefBytes(long startPage)
        {
            var parts = new List<byte[]>();
            var seen  = new HashSet<long>();
            long pg   = startPage;

            while (pg != 0 && !seen.Contains(pg))
            {
                seen.Add(pg);
                byte[] p = ReadPage(pg);
                if (p[0] != 0x02) break;   // not a TDEF page
                parts.Add(p);
                pg = Ru32(p, 4);           // next_pg (0 = end of chain)
            }

            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];

            // Concatenate: full first page, then continuation pages minus 8-byte TDEF header
            int total = parts[0].Length;
            for (int i = 1; i < parts.Count; i++)
                total += parts[i].Length - 8;

            var result = new byte[total];
            Buffer.BlockCopy(parts[0], 0, result, 0, parts[0].Length);
            int pos = parts[0].Length;
            for (int i = 1; i < parts.Count; i++)
            {
                int len = parts[i].Length - 8;
                Buffer.BlockCopy(parts[i], 8, result, pos, len);
                pos += len;
            }
            return result;
        }

        /// <summary>
        /// Parsed table definitions, keyed by TDEF page. Every read, every <c>GetTableStats</c>,
        /// and every <c>GetStatistics</c> re-read and re-parsed the TDEF page chain; the result
        /// only changes if the schema does, which needs a <see cref="Refresh"/> anyway.
        /// </summary>
        private readonly Dictionary<long, TableDef> _tdefCache = new Dictionary<long, TableDef>();
        private readonly object _tdefLock = new object();

        private TableDef ReadTableDef(long tdefPage)
        {
            lock (_tdefLock)
            {
                if (_tdefCache.TryGetValue(tdefPage, out TableDef cached)) return cached;
            }

            TableDef parsed = ReadTableDefUncached(tdefPage);

            lock (_tdefLock)
            {
                _tdefCache[tdefPage] = parsed;
            }
            return parsed;
        }

        private TableDef ReadTableDefUncached(long tdefPage)
        {
            byte[] td = ReadTDefBytes(tdefPage);
            if (td == null || td.Length < _tdBlockEnd) return null;

            int numCols    = Ru16(td, _tdNumCols);
            int numRealIdx = Ri32(td, _tdNumRealIdx);
            uint usedPages = _tdUsedPages >= 0 && td.Length >= _tdUsedPages + 4
                             ? Ru32(td, _tdUsedPages) : 0u;

            // Safety: corrupt or unusual TDEFs can report absurd index counts
            if (numRealIdx < 0 || numRealIdx > 1000) numRealIdx = 0;
            if (numCols    < 0 || numCols    > 4096) return null;

            // Column descriptors follow immediately after block + first real-idx entries
            int colStart = _tdBlockEnd + numRealIdx * _realIdxEntrySz;
            int namePos  = colStart + numCols * _colDescSz;

            if (namePos > td.Length) return null;

            var cols = new List<ColumnInfo>(numCols);
            for (int i = 0; i < numCols; i++)
            {
                int o = colStart + i * _colDescSz;
                if (o + _colDescSz > td.Length) break;
                cols.Add(new ColumnInfo
                {
                    Type     = td[o + _colTypeOff],
                    ColNum   = Ru16(td, o + _colNumOff),
                    VarIdx   = Ru16(td, o + _colVarOff),
                    FixedOff = Ru16(td, o + _colFixedOff),
                    Size     = Ru16(td, o + _colSzOff),
                    Flags    = td[o + _colFlagsOff],
                    // T_NUMERIC keeps its scale here, not in the row — the 17 stored bytes are a
                    // sign and a 128-bit magnitude with nothing to say where the point goes.
                    Precision = td[o + _colPrecOff],
                    Scale     = td[o + _colScaleOff]
                });
            }

            // Column names follow directly after all descriptors (in TDEF / descriptor order).
            // Names MUST be read before sorting so each name maps to the correct descriptor.
            for (int i = 0; i < cols.Count; i++)
            {
                if (namePos >= td.Length) break;

                if (_jet4)
                {
                    if (namePos + 2 > td.Length) break;
                    int len = Ru16(td, namePos); namePos += 2;
                    if (namePos + len > td.Length) break;
                    cols[i].Name = Encoding.Unicode.GetString(td, namePos, len);
                    namePos += len;
                }
                else
                {
                    int len = td[namePos++];
                    if (namePos + len > td.Length) break;
                    cols[i].Name = _ansiEncoding.GetString(td, namePos, len);
                    namePos += len;
                }
            }

            // Sort by col_num AFTER names are assigned.
            // Row data (null_mask bits, numCols check) is indexed by col_num,
            // not by TDEF position.  mdbtools does the same sort (mdb_col_comparer).
            cols.Sort((a, b) => a.ColNum.CompareTo(b.ColNum));

            // Detect deleted-column gaps: if ColNum sequence has gaps, flag it
            bool hasDeletedColumns = false;
            for (int i = 1; i < cols.Count; i++)
            {
                if (cols[i].ColNum != cols[i - 1].ColNum + 1)
                {
                    hasDeletedColumns = true;
                    break;
                }
            }

            return new TableDef 
            { 
                Columns = cols,
                RowCount = td.Length > 20 ? (long)Ru32(td, 16) : 0,
                HasDeletedColumns = hasDeletedColumns,
                UsedPagesDp = usedPages
            };
        }

        // ── Catalog ───────────────────────────────────────────────────────

        private sealed class CatalogEntry
        {
            public string Name;
            public long   TDefPage;
        }

        /// <summary>Returns all user-visible table names and their TDEF page numbers.</summary>
        private List<CatalogEntry> GetUserTables()
        {
            if (_catalogCache != null) return _catalogCache;

            lock (_catalogLock)
            {
                if (_catalogCache != null) return _catalogCache;

                var diag = new System.Text.StringBuilder();
                diag.AppendLine($"JET: {(_jet4 ? "Jet4/ACE" : "Jet3")}  PageSize: {_pgSz}  TotalPages: {_fs.Length / _pgSz}");

                // MSysObjects TDEF is hard-coded at page 2 by the Jet engine
                TableDef msys = ReadTableDef(2);
                if (msys == null)
                {
                    diag.AppendLine("ERROR: Page 2 is not a valid TDEF page (null returned).");
                    LastDiagnostics = diag.ToString();
                    _catalogCache = new List<CatalogEntry>();
                    return _catalogCache;
                }

                diag.AppendLine($"MSysObjects cols ({msys.Columns.Count}): " +
                    string.Join(", ", msys.Columns.ConvertAll(c => $"{c.Name}[0x{c.Type:X2}]")));

                // Case-insensitive column lookup — column names vary slightly across Access versions
                int idxId      = msys.Columns.FindIndex(c => string.Equals(c.Name, "Id",          StringComparison.OrdinalIgnoreCase));
                int idxName    = msys.Columns.FindIndex(c => string.Equals(c.Name, "Name",        StringComparison.OrdinalIgnoreCase));
                int idxType    = msys.Columns.FindIndex(c => string.Equals(c.Name, "Type",        StringComparison.OrdinalIgnoreCase));
                int idxFlags   = msys.Columns.FindIndex(c => string.Equals(c.Name, "Flags",       StringComparison.OrdinalIgnoreCase));
                int idxConnect = msys.Columns.FindIndex(c => string.Equals(c.Name, "Connect",     StringComparison.OrdinalIgnoreCase));
                int idxForeign = msys.Columns.FindIndex(c => string.Equals(c.Name, "ForeignName", StringComparison.OrdinalIgnoreCase));
                int idxDatabase = msys.Columns.FindIndex(c => string.Equals(c.Name, "Database",   StringComparison.OrdinalIgnoreCase));

                if (idxName < 0 || idxType < 0)
                {
                    diag.AppendLine("ERROR: Required catalog columns not found. Column name mismatch?");
                    LastDiagnostics = diag.ToString();
                    _catalogCache = new List<CatalogEntry>();
                    return _catalogCache;
                }

                var result       = new List<CatalogEntry>();
                var linked       = new List<LinkedTable>();
                var objectTypes  = new Dictionary<int, int>();
                long totPages    = _fs.Length / _pgSz;
                int  catPages    = 0;
                int  allRows     = 0;

                // This pass already touches every page header, so recording each data page's
                // owning TDEF costs nothing extra and saves every later read a whole-file scan.
                var index = new Dictionary<long, List<PageRun>>();

                RowShape msysShape = BuildShape(msys, null);
                var catScanner = new RowScanner();
                var catRow = new string[msysShape.Width];

                // This scan only needs eight bytes out of every page — the type and the owning
                // TDEF — but it has to visit them all. Reading a page at a time means one copy
                // per page out of the FileStream buffer; reading in blocks and inspecting the
                // headers where they land removes that copy for all but the catalog's own pages,
                // which are the only ones whose body is actually used here.
                byte[] scan = NewScanBuffer();
                byte[] block = _crypto == null ? new byte[ScanBlockPages * _pgSz] : null;

                for (long first = 3; first < totPages; first += ScanBlockPages)
                {
                    int inBlock = (int)Math.Min(ScanBlockPages, totPages - first);
                    if (block != null) ReadBlock(first, block, inBlock * _pgSz);

                    for (int i = 0; i < inBlock; i++)
                    {
                        long p = first + i;

                        // Encrypted databases decrypt page by page, so they keep the simple path.
                        byte[] page;
                        int at;
                        if (block != null) { page = block; at = i * _pgSz; }
                        else { page = ReadPageForScan(p, scan); at = 0; }

                        if (page[at] != 0x01) continue;         // data pages only

                        // Same expression the read loops compare against, so the index is an exact
                        // memoization of their filter — not a heuristic that could miss pages.
                        long owner = Ri32(page, at + _dpTDefOff);
                        if (!index.TryGetValue(owner, out List<PageRun> runs))
                        {
                            runs = new List<PageRun>();
                            index[owner] = runs;
                        }
                        AppendPage(runs, p);

                        if (owner != 2) continue;               // must belong to MSysObjects
                        catPages++;

                        // Row decoding requires a buffer that is exactly one page: MEMO, NUMERIC
                        // and GUID decoding clamp against buffer.Length, and handing them the
                        // whole block would widen those bounds into the following pages. Always
                        // lift the page out — there are only a handful of catalog pages.
                        byte[] catalogPage;
                        if (block != null)
                        {
                            Buffer.BlockCopy(block, at, scan, 0, _pgSz);
                            catalogPage = scan;
                        }
                        else
                        {
                            catalogPage = page;
                        }

                    foreach (RowSpan span in EnumerateRowSpans(catalogPage, catScanner))
                    {
                        if (!CrackRow(span.Page, span.Start, span.Size, msysShape, catRow, null)) continue;

                        allRows++;
                        string typeStr  = SafeGet(catRow, idxType);
                        string nameStr  = SafeGet(catRow, idxName);
                        string flagsStr = SafeGet(catRow, idxFlags);

                        // Invariant parsing, to match the invariant text these values were
                        // written as. Sixty-six cultures use a negative sign that is not '-',
                        // and under those the parse fails: a system table's flags have the high
                        // bit set, so they format negative, and a failed parse leaves zero —
                        // which passes the mask below and lists the system table as a user one.
                        if (!int.TryParse(typeStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                          out int objType)) continue;

                        objectTypes.TryGetValue(objType, out int seen);
                        objectTypes[objType] = seen + 1;

                        bool isLinked = objType == OBJ_LINKED || objType == OBJ_LINKED_ODBC;
                        if (objType != OBJ_TABLE && !isLinked) continue;

                        long.TryParse(flagsStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                      out long flagsLong);
                        if (((uint)flagsLong & SYSTABLE_MASK) != 0)
                            continue;

                        if (string.IsNullOrEmpty(nameStr)) continue;

                        if (isLinked)
                        {
                            // The rows live elsewhere, so there is no TDEF page to record — only
                            // where to find them.
                            linked.Add(LinkedTableParser.Parse(
                                nameStr,
                                SafeGet(catRow, idxForeign),
                                SafeGet(catRow, idxConnect),
                                SafeGet(catRow, idxDatabase),
                                objType == OBJ_LINKED_ODBC));
                            continue;
                        }

                        long tdefPage = 0;
                        if (idxId >= 0)
                        {
                            long.TryParse(SafeGet(catRow, idxId), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out long id);
                            tdefPage = id & 0x00FFFFFFL;
                        }

                        if (tdefPage > 0)
                            result.Add(new CatalogEntry { Name = nameStr, TDefPage = tdefPage });
                    }
                    }
                }

                diag.AppendLine($"Catalog pages: {catPages}  Total rows scanned: {allRows}  " +
                                $"User tables: {result.Count}  Linked tables: {linked.Count}");

                var types = new List<int>(objectTypes.Keys);
                types.Sort();
                var typeParts = new List<string>(types.Count);
                foreach (int t in types) typeParts.Add($"{t}:{objectTypes[t]}");
                diag.AppendLine($"Catalog object types (type:count): {string.Join(", ", typeParts)}");

                if (DiagnosticsEnabled)
                {
                    foreach (LinkedTable l in linked)
                        diag.AppendLine($"  LINK {l}  connect='{l.ConnectionString}'");
                }
                int totalRuns = 0;
                foreach (var kv in index) totalRuns += kv.Value.Count;
                diag.AppendLine($"Page index: {index.Count} distinct owners, {totalRuns} runs over {totPages} pages");
                if (DiagnosticsEnabled)
                {
                    foreach (var e in result)
                        diag.AppendLine($"  [{e.Name}] TDEF page {e.TDefPage}");
                }

                // Trim the growth slack off each list before publishing — List<T> over-allocates
                // by up to 2x, and this structure outlives the scan.
                var trimmed = new Dictionary<long, PageRun[]>(index.Count);
                foreach (var kv in index)
                    trimmed[kv.Key] = kv.Value.ToArray();

                LastDiagnostics = diag.ToString();

                // Publish the index BEFORE the catalog: _catalogCache != null is the fast-path
                // exit for concurrent callers, and they consult the index right after.
                Interlocked.Exchange(ref _indexedPages, totPages);
                _pageIndex = trimmed;
                _linkedCache = linked;
                _catalogCache = result;
                return _catalogCache;
            }
        }

        private static string SafeGet(string[] row, int idx) =>
            (idx >= 0 && idx < row.Length) ? row[idx] : string.Empty;

        /// <summary>Finds a catalog entry by name (case-insensitive) without re-scanning the catalog.</summary>
        private CatalogEntry GetCatalogEntry(string tableName)
        {
            return GetUserTables().Find(e =>
                string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
        }

        // ── Page index ────────────────────────────────────────────────────

        /// <summary>
        /// Yields, in ascending order, the data pages belonging to <paramref name="tdefPage"/>.
        /// Falls back to a whole-file scan when the catalog (and therefore the index) could not
        /// be read, so behaviour is never worse than before the index existed.
        /// </summary>
        private IEnumerable<long> EnumerateTablePages(long tdefPage)
        {
            // The usage map is what Access itself consults, and it is the only thing that knows a
            // page has been released. Sweeping for data pages whose tdef_pg still names the table
            // finds those too: on a 2 GB sample database that was 94 657 extra pages carrying
            // 66 164 rows Access does not have — rows that look like data and are not.
            long[] owned = GetUsagePages(tdefPage);
            if (owned != null)
            {
                for (int i = 0; i < owned.Length; i++) yield return owned[i];
                yield break;
            }

            GetUserTables();   // builds the index on first call; cached afterwards

            Dictionary<long, PageRun[]> index = _pageIndex;
            if (index == null)
            {
                // Unreadable catalog — the legacy behaviour is the safe fallback.
                long total = _fs.Length / _pgSz;
                for (long p = 3; p < total; p++)
                    yield return p;
                yield break;
            }

            // The file may have grown since the index was built: FileShare.ReadWrite is the
            // default, so another process (e.g. Access) can append pages while we read.
            index = ExtendIndexIfFileGrew(index);

            if (index.TryGetValue(tdefPage, out PageRun[] runs))
            {
                for (int i = 0; i < runs.Length; i++)
                {
                    long end = runs[i].End;
                    for (long p = runs[i].Start; p <= end; p++)
                        yield return p;
                }
            }
        }

        /// <summary>
        /// The pages a table owns, ascending, from its usage map — or null when the map cannot be
        /// read, in which case the caller falls back to the page sweep.
        ///
        /// Two encodings, both a bitmap of "page N is mine":
        ///
        ///   inline    [0x00][first page(4)][bits…]        — one row, one contiguous range
        ///   reference [0x01][map page(4)]…                — each pointer names a page whose body
        ///                                                   is a bitmap for the next slice
        ///
        /// Resolved once per table. Every read path calls this, and on a 2 GB database the
        /// reference form costs seventeen page reads against a whole-file sweep.
        /// </summary>
        private long[] GetUsagePages(long tdefPage)
        {
            TableDef td = ReadTableDef(tdefPage);
            if (td == null) return null;

            lock (_indexLock)
            {
                if (td.UsagePagesResolved) return td.UsagePages;
                td.UsagePagesResolved = true;
                td.UsagePages = ReadUsageMap(td.UsedPagesDp);
                return td.UsagePages;
            }
        }

        private long[] ReadUsageMap(uint dp)
        {
            if (dp == 0) return null;

            try
            {
                byte[] row = ReadUsageMapRow(dp);
                if (row == null || row.Length < 5) return null;

                var pages = new List<long>();

                if (row[0] == 0x00)
                {
                    long first = Ru32(row, 1);
                    AddSetBits(pages, row, 5, row.Length - 5, first);
                }
                else if (row[0] == 0x01)
                {
                    // Each pointer covers the same number of pages, whether or not it is present:
                    // a zero pointer means "none of that slice", not "skip it", so the slice index
                    // has to come from the position rather than from a running total.
                    int perPage = (_pgSz - 4) * 8;
                    int entries = (row.Length - 1) / 4;

                    for (int i = 0; i < entries; i++)
                    {
                        uint mapPage = Ru32(row, 1 + i * 4);
                        if (mapPage == 0) continue;

                        byte[] page = ReadPageCached(mapPage);
                        if (page == null) continue;

                        AddSetBits(pages, page, 4, _pgSz - 4, (long)i * perPage);
                    }
                }
                else return null;

                if (pages.Count == 0) return null;   // nothing usable — let the sweep answer

                pages.Sort();
                return pages.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static void AddSetBits(List<long> into, byte[] b, int offset, int len, long firstPage)
        {
            for (int i = 0; i < len; i++)
            {
                byte v = b[offset + i];
                if (v == 0) continue;
                for (int bit = 0; bit < 8; bit++)
                    if ((v & (1 << bit)) != 0) into.Add(firstPage + (long)i * 8 + bit);
            }
        }

        /// <summary>
        /// Reads the row a (page &lt;&lt; 8 | row) pointer names. Same shape as an LVAL row: the
        /// payload runs from the row's offset to the start of whichever row sits above it.
        /// </summary>
        private byte[] ReadUsageMapRow(uint dp)
        {
            long pageNo = dp >> 8;
            int rowIdx  = (int)(dp & 0xFF);
            if (pageNo <= 0) return null;

            byte[] page = ReadPageCached(pageNo);
            if (page == null || page[0] != 0x01) return null;

            int numRows = PageRowCount(page);
            if (rowIdx >= numRows) return null;

            int rawOff = Ru16(page, _dpRowsStart + rowIdx * 2);
            if ((rawOff & 0xC000) != 0) return null;

            int rowStart = rawOff & 0x1FFF;
            if (rowStart <= 0 || rowStart >= _pgSz) return null;

            int rowEnd = _pgSz - 1;
            for (int r = 0; r < numRows; r++)
            {
                int ofs = Ru16(page, _dpRowsStart + r * 2) & 0x1FFF;
                if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
            }

            int size = rowEnd - rowStart + 1;
            if (size <= 0) return null;

            var data = new byte[size];
            Buffer.BlockCopy(page, rowStart, data, 0, size);
            return data;
        }

        /// <summary>
        /// Appends <paramref name="page"/> to <paramref name="runs"/>, extending the last run when
        /// the page continues it. Pages are always appended in ascending order.
        /// </summary>
        private static void AppendPage(List<PageRun> runs, long page)
        {
            int n = runs.Count;
            if (n > 0)
            {
                PageRun last = runs[n - 1];
                if (last.End + 1 == page)
                {
                    last.Count++;
                    runs[n - 1] = last;
                    return;
                }
            }
            runs.Add(new PageRun { Start = page, Count = 1 });
        }

        /// <summary>
        /// Indexes any pages appended since the last scan. Normally a single length comparison.
        /// </summary>
        private Dictionary<long, PageRun[]> ExtendIndexIfFileGrew(Dictionary<long, PageRun[]> current)
        {
            long total = _fs.Length / _pgSz;
            if (total <= Interlocked.Read(ref _indexedPages)) return current;

            lock (_indexLock)
            {
                if (_pageIndex == null) return current;

                long from = Interlocked.Read(ref _indexedPages);
                total = _fs.Length / _pgSz;
                if (total <= from) return _pageIndex;

                var added = new Dictionary<long, List<PageRun>>();
                byte[] scan = NewScanBuffer();
                for (long p = from; p < total; p++)
                {
                    byte[] page = ReadPageForScan(p, scan);
                    if (page[0] != 0x01) continue;

                    long owner = Ri32(page, _dpTDefOff);
                    if (!added.TryGetValue(owner, out List<PageRun> list))
                    {
                        list = new List<PageRun>();
                        added[owner] = list;
                    }
                    AppendPage(list, p);
                }

                // Appended pages have higher numbers than every indexed page, so concatenating
                // preserves the ascending order the read loops depend on for row ordering.
                var merged = new Dictionary<long, PageRun[]>(_pageIndex);
                foreach (var kv in added)
                {
                    merged.TryGetValue(kv.Key, out PageRun[] existing);
                    var combined = new List<PageRun>(existing ?? new PageRun[0]);

                    // Re-append run by run so a new run that continues the last indexed one merges
                    // instead of leaving an artificial split.
                    foreach (PageRun run in kv.Value)
                    {
                        long end = run.End;
                        for (long p = run.Start; p <= end; p++) AppendPage(combined, p);
                    }

                    merged[kv.Key] = combined.ToArray();
                }

                Interlocked.Exchange(ref _indexedPages, total);
                _pageIndex = merged;
                return merged;
            }
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the column headers and up to <paramref name="maxRows"/> rows
        /// from the first user table, plus the table name and total table count.
        /// </summary>
        public FirstTableResult ReadFirstTable(int maxRows = 100)
        {
            ThrowIfDisposed();

            var empty = new FirstTableResult
            {
                Headers    = new List<string> { "Info" },
                Rows       = new List<List<string>> { new List<string> { "No user tables found" } },
                Schema     = new List<TableColumn>(),
                TableName  = string.Empty,
                TableCount = 0
            };

            List<CatalogEntry> tables = GetUserTables();
            if (tables.Count == 0) return empty;

            CatalogEntry entry = tables[0];
            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new FirstTableResult
                {
                    Headers    = new List<string> { "Info" },
                    Rows       = new List<List<string>> { new List<string> { $"Cannot read TDEF for '{entry.Name}'" } },
                    Schema     = new List<TableColumn>(),
                    TableName  = entry.Name,
                    TableCount = tables.Count
                };

            var headers = td.Columns.Select(c => c.Name).ToList();

            // Populated from the TDEF like every other read path. This used to be handed back
            // empty, so callers could see the headers but never the column types.
            var schema = td.Columns.ConvertAll(c => new TableColumn
            {
                Name = c.Name,
                Type = TypeCodeToClrType(c.Type),
                Size = SizeForColumn(c)
            });

            var rows    = new List<List<string>>();

            RowShape shape = BuildShape(td, null);
            var scanner = new RowScanner();
            var buf = new string[shape.Width];
            byte[] scan = NewScanBuffer();

            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                if (rows.Count >= maxRows) break;

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                {
                    if (!CrackRow(span.Page, span.Start, span.Size, shape, buf, null)) continue;
                    rows.Add(new List<string>(buf));
                    if (rows.Count >= maxRows) break;
                }
            }

            return new FirstTableResult
            {
                Headers    = headers,
                Rows       = rows,
                Schema     = schema,
                TableName  = entry.Name,
                TableCount = tables.Count
            };
        }

        /// <summary>
        /// Returns the tables that are linked rather than stored here, with the connection string
        /// and the name each has in its source.
        ///
        /// These are deliberately absent from <see cref="ListTables"/>: their rows are not in this
        /// file, so asking to read one would either fail or silently return nothing. Use
        /// <see cref="OpenLinkedTableSource"/> for a link that points at another Access database.
        /// </summary>
        public List<LinkedTable> GetLinkedTables()
        {
            ThrowIfDisposed();
            GetUserTables();   // the catalog scan finds both in one pass
            return new List<LinkedTable>(_linkedCache ?? new List<LinkedTable>());
        }

        /// <summary>
        /// Opens the Access database a linked table points at, so its rows can be read with
        /// <c>reader.StreamRows(link.ForeignName)</c>.
        ///
        /// Only for links to another Access file. An ODBC link needs a driver, which is the
        /// dependency this library exists to avoid, and Excel or text sources are not JET
        /// databases — for those, <see cref="LinkedTable.ConnectionString"/> tells you what to open.
        /// </summary>
        /// <param name="link">A link obtained from <see cref="GetLinkedTables"/>.</param>
        /// <param name="options">Options for the source database. Its own password, if any, goes here.</param>
        /// <exception cref="NotSupportedException">The link does not point at an Access database.</exception>
        /// <exception cref="FileNotFoundException">The source file is missing or unreachable.</exception>
        public AccessReader OpenLinkedTableSource(LinkedTable link, AccessReaderOptions options = null)
        {
            ThrowIfDisposed();
            Guard.NotNull(link, nameof(link));

            if (!link.IsAccessDatabase)
                throw new NotSupportedException(
                    $"'{link.Name}' is a {link.Kind} link, not a link to an Access database. " +
                    $"Its connection string is '{link.ConnectionString}'.");

            // Access stores the path as it was when the link was made, so it is regularly a UNC
            // path or a drive letter that no longer resolves on this machine.
            if (!File.Exists(link.SourcePath))
                throw new FileNotFoundException(
                    $"The database '{link.Name}' is linked to was not found at '{link.SourcePath}'. " +
                    $"Linked paths are stored as they were when the link was created.",
                    link.SourcePath);

            return Open(link.SourcePath, options);
        }

        /// <summary>Returns the names of all user tables in the database.</summary>
        public List<string> ListTables()
        {
            ThrowIfDisposed();
            return GetUserTables().ConvertAll(e => e.Name);
        }

        /// <summary>
        /// Returns name, stored row-count, and column-count for every user table.
        /// Calling this instead of <see cref="ListTables"/> avoids a duplicate catalog scan.
        /// </summary>
        public List<TableStat> GetTableStats()
        {
            ThrowIfDisposed();
            var entries = GetUserTables();
            var result  = new List<TableStat>(entries.Count);
            foreach (var e in entries)
            {
                TableDef td = ReadTableDef(e.TDefPage);
                result.Add(new TableStat
                {
                    Name        = e.Name,
                    RowCount    = td?.RowCount ?? 0L,
                    ColumnCount = td?.Columns.Count ?? 0
                });
            }
            return result;
        }

        /// <summary>
        /// Returns table metadata as a DataTable with columns: TableName, RowCount, ColumnCount.
        /// Ideal for binding to data grids or exporting to CSV/Excel.
        /// </summary>
        public DataTable GetTablesAsDataTable()
        {
            ThrowIfDisposed();
            var dt = new DataTable("Tables");
            dt.Columns.Add("TableName", typeof(string));
            dt.Columns.Add("RowCount", typeof(long));
            dt.Columns.Add("ColumnCount", typeof(int));

            var stats = GetTableStats();
            foreach (TableStat s in stats)
            {
                dt.Rows.Add(s.Name, s.RowCount, s.ColumnCount);
            }

            return dt;
        }

        /// <summary>
        /// Scans all data pages to count live (non-deleted) rows for the specified table,
        /// including rows reached through an overflow pointer.
        /// This is slower than reading the TDEF RowCount (which may be stale), but always accurate.
        /// Use this after many deletes/imports when Compact &amp; Repair hasn't been run.
        /// </summary>
        public long GetRealRowCount(string tableName) => GetRealRowCount(tableName, default);

        /// <summary>
        /// Counts live rows, stopping when <paramref name="cancellationToken"/> is signalled.
        /// A full count walks every page the table owns, which on a large database is long enough
        /// to outlive the request that asked for it.
        /// </summary>
        public long GetRealRowCount(string tableName, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return 0;

            long count = 0;

            // Shares EnumerateRowSpans with the read paths so the count cannot drift from what
            // StreamRows actually yields — the two used to disagree about overflow rows.
            var scanner = new RowScanner();
            byte[] scan = NewScanBuffer();

            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan _ in EnumerateRowSpans(page, scanner)) count++;
            }
            return count;
        }

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive).
        /// Returns column headers, rows with native CLR types (int, DateTime, decimal, etc.) in <see cref="TableResult.Rows"/>, and per-column schema.
        /// Use <see cref="ReadTableAsStrings"/> when raw string values are needed instead.
        /// </summary>
        public TableResult ReadTable(string tableName, int maxRows)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);

            if (entry == null)
                return new TableResult
                {
                    Headers   = new List<string>(),
                    Rows = new List<object[]>(),
                    Schema    = new List<TableColumn>()
                };

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new TableResult
                {
                    Headers   = new List<string>(),
                    Rows = new List<object[]>(),
                    Schema    = new List<TableColumn>()
                };

            var headers   = td.Columns.ConvertAll(c => c.Name);
            var schema    = td.Columns.ConvertAll(c => new TableColumn
            {
                Name = c.Name,
                Type = TypeCodeToClrType(c.Type),
                Size = SizeForColumn(c)
            });
            var typedRows = new List<object[]>();

            RowShape shape = BuildShape(td, null);
            var scanner = new RowScanner();
            var buf = new object[shape.Width];
            byte[] scan = NewScanBuffer();

            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                if (typedRows.Count >= maxRows) break;

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                {
                    if (!CrackRow(span.Page, span.Start, span.Size, shape, null, buf)) continue;
                    typedRows.Add((object[])buf.Clone());
                    if (typedRows.Count >= maxRows) break;
                }
            }

            return new TableResult { Headers = headers, Rows = typedRows, Schema = schema, TableName = tableName };
        }

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with all values as strings.
        /// Returns column headers, string rows in <see cref="StringTableResult.Rows"/>, and per-column schema.
        /// Use <see cref="ReadTable(string, int)"/> when native CLR types are preferred.
        /// </summary>
        public StringTableResult ReadTableAsStrings(string tableName, int maxRows)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            CatalogEntry entry = GetCatalogEntry(tableName);

            if (entry == null)
                return new StringTableResult
                {
                    Headers = new List<string>(),
                    Rows    = new List<List<string>>(),
                    Schema  = new List<TableColumn>()
                };

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                return new StringTableResult
                {
                    Headers = new List<string>(),
                    Rows    = new List<List<string>>(),
                    Schema  = new List<TableColumn>()
                };

            var headers = td.Columns.ConvertAll(c => c.Name);
            var schema  = td.Columns.ConvertAll(c => new TableColumn
            {
                Name = c.Name,
                Type = TypeCodeToClrType(c.Type),
                Size = SizeForColumn(c)
            });
            var rows  = new List<List<string>>();

            RowShape shape = BuildShape(td, null);
            var scanner = new RowScanner();
            var buf = new string[shape.Width];
            byte[] scan = NewScanBuffer();

            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                if (rows.Count >= maxRows) break;

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                {
                    if (!CrackRow(span.Page, span.Start, span.Size, shape, buf, null)) continue;
                    rows.Add(new List<string>(buf));
                    if (rows.Count >= maxRows) break;
                }
            }

            return new StringTableResult { Headers = headers, Rows = rows, Schema = schema, TableName = tableName };
        }

        private static string TypeCodeToName(byte t)
        {
            switch (t)
            {
                case T_BOOL:     return "Yes/No";
                case T_BYTE:     return "Byte";
                case T_INT:      return "Integer";
                case T_LONG:     return "Long Integer";
                case T_MONEY:    return "Currency";
                case T_FLOAT:    return "Single";
                case T_DOUBLE:   return "Double";
                case T_DATETIME: return "Date/Time";
                case T_BINARY:   return "Binary";
                case T_TEXT:     return "Text";
                case T_OLE:      return "OLE Object";
                case T_MEMO:     return "Memo";
                case T_GUID:     return "GUID";
                case T_NUMERIC:  return "Decimal";
                case T_COMPLEX:  return "Complex";
                default:         return $"0x{t:X2}";
            }
        }

        private static ColumnSize SizeForColumn(ColumnInfo col)
        {
            switch (col.Type)
            {
                case T_BOOL:     return ColumnSize.FromBits(1);
                case T_BYTE:     return ColumnSize.FromBytes(1);
                case T_INT:      return ColumnSize.FromBytes(2);
                case T_LONG:     return ColumnSize.FromBytes(4);
                case T_MONEY:    return ColumnSize.FromBytes(8);
                case T_FLOAT:    return ColumnSize.FromBytes(4);
                case T_DOUBLE:   return ColumnSize.FromBytes(8);
                case T_DATETIME: return ColumnSize.FromBytes(8);
                case T_GUID:     return ColumnSize.FromBytes(16);
                case T_NUMERIC:  return ColumnSize.FromBytes(17);
                case T_TEXT:     return ColumnSize.FromChars(col.Size > 0 ? col.Size / 2 : 255);
                case T_BINARY:   return col.Size > 0 ? ColumnSize.FromBytes(col.Size) : ColumnSize.Variable;
                case T_MEMO:
                case T_OLE:      return ColumnSize.Lval;
                default:         return col.Size > 0 ? ColumnSize.FromBytes(col.Size) : ColumnSize.Variable;
            }
        }

        // Used by ColumnMetadata.SizeDescription which keeps a plain string.
        private static string SizeDescForColumn(ColumnInfo col) => SizeForColumn(col).ToString();

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as properly typed object arrays without collecting them all in memory.
        /// Each element in the array is the native CLR type (int, DateTime, decimal, etc.).
        /// Ideal for large tables — use foreach to process one row at a time.
        /// This is the recommended method for streaming data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public IEnumerable<object[]> StreamRows(string tableName, IProgress<int> progress = null)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsCore(tableName, null, progress);
        }

        /// <summary>
        /// Yields only <paramref name="columns"/>, in the order given, as typed object arrays.
        /// Columns that are not selected are never decoded — for a MEMO or OLE column that means
        /// its LVAL pages are never even read, so projecting away blob columns can cut both time
        /// and memory by an order of magnitude.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="columns">Column names (case-insensitive). Null or empty selects all columns.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        /// <exception cref="ArgumentException">A requested column does not exist in the table.</exception>
        public IEnumerable<object[]> StreamRows(string tableName, IReadOnlyList<string> columns,
                                                IProgress<int> progress)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsCore(tableName, columns, progress);
        }

        /// <summary>
        /// Streams rows without copying each one out. The yielded array is overwritten on the next
        /// iteration, so this is only safe for consumers that read a row before advancing —
        /// exactly the contract <see cref="System.Data.IDataReader"/> already imposes.
        /// </summary>
        internal IEnumerable<object[]> StreamRowsShared(string tableName, IReadOnlyList<string> columns)
            => StreamRowsCore(tableName, columns, null, copyRows: false);

        private IEnumerable<object[]> StreamRowsCore(string tableName, IReadOnlyList<string> columns,
                                                     IProgress<int> progress, bool copyRows = true,
                                                     CancellationToken cancellationToken = default)
        {
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) yield break;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) yield break;

            RowShape shape = BuildShape(td, columns);
            var scanner = new RowScanner();
            var buf = new object[shape.Width];
            byte[] scan = NewScanBuffer();

            int rowCount = 0;
            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                {
                    if (!CrackRow(span.Page, span.Start, span.Size, shape, null, buf)) continue;

                    // Copy out: the consumer may keep the row, and buf is reused for the next one.
                    yield return copyRows ? (object[])buf.Clone() : buf;
                    rowCount++;
                }
                progress?.Report(rowCount);
            }
        }

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as string arrays without collecting them all in memory.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="StreamRows"/> which returns properly typed data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public IEnumerable<string[]> StreamRowsAsStrings(string tableName, IProgress<int> progress = null)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsAsStringsCore(tableName, null, progress);
        }

        /// <summary>
        /// Yields only <paramref name="columns"/>, in the order given, as string arrays.
        /// Unselected columns are never decoded. See <see cref="StreamRows(string, IReadOnlyList{string}, IProgress{int})"/>.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="columns">Column names (case-insensitive). Null or empty selects all columns.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        /// <exception cref="ArgumentException">A requested column does not exist in the table.</exception>
        public IEnumerable<string[]> StreamRowsAsStrings(string tableName, IReadOnlyList<string> columns,
                                                         IProgress<int> progress)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsAsStringsCore(tableName, columns, progress);
        }

        private IEnumerable<string[]> StreamRowsAsStringsCore(string tableName, IReadOnlyList<string> columns,
                                                              IProgress<int> progress,
                                                              CancellationToken cancellationToken = default)
        {
            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) yield break;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) yield break;

            RowShape shape = BuildShape(td, columns);
            var scanner = new RowScanner();
            var buf = new string[shape.Width];
            byte[] scan = NewScanBuffer();

            int rowCount = 0;
            foreach (long p in EnumerateTablePages(entry.TDefPage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] page = ReadPageForScan(p, scan);
                if (page[0] != 0x01) continue;
                if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                {
                    if (!CrackRow(span.Page, span.Start, span.Size, shape, buf, null)) continue;

                    // Copy out: the consumer may keep the row, and buf is reused for the next one.
                    yield return (string[])buf.Clone();
                    rowCount++;
                }
                progress?.Report(rowCount);
            }
        }

        /// <summary>
        /// Reads the entire table into a DataTable with all columns typed as strings.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="ReadTable"/> which returns properly typed columns.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public DataTable ReadTableAsStringDataTable(string tableName = null, IProgress<int> progress = null)
            => ReadTableAsStringDataTableCore(tableName, null, progress, default);

        /// <summary>
        /// Reads only <paramref name="columns"/> into a DataTable of string columns.
        /// Unselected columns are never decoded.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="columns">Column names (case-insensitive). Null or empty selects all columns.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        /// <exception cref="ArgumentException">A requested column does not exist in the table.</exception>
        public DataTable ReadTableAsStringDataTable(string tableName, IReadOnlyList<string> columns,
                                                    IProgress<int> progress)
            => ReadTableAsStringDataTableCore(tableName, columns, progress, default);

        private DataTable ReadTableAsStringDataTableCore(string tableName, IReadOnlyList<string> columns,
                                                         IProgress<int> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            // If no table name specified, use the first table
            if (string.IsNullOrEmpty(tableName))
            {
                var tables = GetUserTables();
                if (tables.Count == 0) return null;
                tableName = tables[0].Name;
            }

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return null;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) return null;

            RowShape shape = BuildShape(td, columns);

            var dt = new DataTable(tableName);
            for (int i = 0; i < shape.Width; i++)
                dt.Columns.Add(shape.Names[i], typeof(string));

            SetCapacityHint(dt, td);

            var scanner = new RowScanner();
            var buf = new string[shape.Width];
            byte[] scan = NewScanBuffer();

            // Suspends index maintenance and constraint checking for the duration of the load.
            dt.BeginLoadData();
            try
            {
                foreach (long p in EnumerateTablePages(entry.TDefPage))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] page = ReadPageForScan(p, scan);
                    if (page[0] != 0x01) continue;
                    if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                    foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                    {
                        // DataRowCollection.Add copies the values, so buf can be reused.
                        if (CrackRow(span.Page, span.Start, span.Size, shape, buf, null))
                            dt.Rows.Add(buf);
                    }
                    progress?.Report(dt.Rows.Count);
                }
            }
            finally
            {
                dt.EndLoadData();
            }

            return dt;
        }

        /// <summary>
        /// Returns the column names of the specified table, in table order.
        /// </summary>
        public List<string> GetColumnNames(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return new List<string>();

            TableDef td = ReadTableDef(entry.TDefPage);
            return td == null ? new List<string>() : td.Columns.ConvertAll(c => c.Name);
        }

        /// <summary>
        /// Returns rich metadata for all columns in the specified table.
        /// </summary>
        public List<ColumnMetadata> GetColumnMetadata(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return new List<ColumnMetadata>();

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null) return new List<ColumnMetadata>();

            return td.Columns.Select((col, index) => new ColumnMetadata
            {
                Name = col.Name,
                TypeName = TypeCodeToName(col.Type),
                ClrType = TypeCodeToClrType(col.Type),
                MaxLength = col.Size > 0 ? (int?)col.Size : null,
                IsNullable = true,
                IsFixedLength = col.IsFixed,
                Ordinal = index,
                SizeDescription = SizeDescForColumn(col)
            }).ToList();
        }

        /// <summary>
        /// Returns statistical information about the database.
        /// </summary>
        public DatabaseStatistics GetStatistics()
        {
            ThrowIfDisposed();

            var stats = new DatabaseStatistics
            {
                TotalPages = _fs.Length / _pgSz,
                DatabaseSizeBytes = _fs.Length,
                PageSize = _pgSz,
                Version = _jet4 ? "Jet4/ACE" : "Jet3",
                CodePage = _codePage
            };

            var tables = GetUserTables();
            stats.TableCount = tables.Count;
            stats.TableRowCounts = new Dictionary<string, long>();

            foreach (var table in tables)
            {
                var td = ReadTableDef(table.TDefPage);
                if (td != null)
                {
                    stats.TableRowCounts[table.Name] = td.RowCount;
                    stats.TotalRows += td.RowCount;
                }
            }

            long totalAccess = _cacheHits + _cacheMisses;
            stats.PageCacheHitRate = totalAccess > 0 ? (int)((_cacheHits * 100) / totalAccess) : 0;

            return stats;
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// This is the recommended method for bulk reading.
        /// </summary>
        public Dictionary<string, DataTable> ReadAllTables(IProgress<string> progress = null)
        {
            ThrowIfDisposed();

            var result = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var tables = GetUserTables();

            foreach (var table in tables)
            {
                progress?.Report($"Reading {table.Name}...");
                result[table.Name] = ReadTable(table.Name);
            }

            return result;
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings.
        /// Use this for compatibility scenarios.
        /// </summary>
        public Dictionary<string, DataTable> ReadAllTablesAsStrings(IProgress<string> progress = null)
        {
            ThrowIfDisposed();

            var result = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var tables = GetUserTables();

            foreach (var table in tables)
            {
                progress?.Report($"Reading {table.Name}...");
                result[table.Name] = ReadTableAsStringDataTable(table.Name);
            }

            return result;
        }

        /// <summary>
        /// Opens a forward-only cursor over the table. This is the constant-memory path for moving
        /// a large table elsewhere — <c>SqlBulkCopy.WriteToServer(reader)</c> or
        /// <c>DataTable.Load(reader)</c> stream row by row instead of materialising the table.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="columns">Column names (case-insensitive). Null or empty selects all columns.</param>
        /// <exception cref="ArgumentException">The table, or a requested column, does not exist.</exception>
        public AccessDataReader CreateDataReader(string tableName, IReadOnlyList<string> columns = null)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null)
                throw new ArgumentException($"Table '{tableName}' does not exist in this database.", nameof(tableName));

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0)
                throw new InvalidDataException($"Cannot read the table definition for '{tableName}'.");

            RowShape shape = BuildShape(td, columns);

            // StreamRowsShared hands back the same array each row; AccessDataReader never retains
            // it past the next Read(), which is exactly what IDataReader promises its callers.
            return new AccessDataReader(tableName, shape.Names, shape.ClrTypes,
                                        StreamRowsShared(tableName, columns).GetEnumerator());
        }

        /// <summary>
        /// Creates a fluent query interface for the specified table.
        /// </summary>
        public TableQuery Query(string tableName)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return new TableQuery(this, tableName);
        }

        private static Type TypeCodeToClrType(byte typeCode)
        {
            switch (typeCode)
            {
                case T_BOOL: return typeof(bool);
                case T_BYTE: return typeof(byte);
                case T_INT: return typeof(short);
                case T_LONG: return typeof(int);
                case T_MONEY: return typeof(decimal);
                case T_FLOAT: return typeof(float);
                case T_DOUBLE: return typeof(double);
                case T_DATETIME: return typeof(DateTime);
                case T_GUID: return typeof(Guid);
                case T_NUMERIC: return typeof(decimal);
                default: return typeof(string);
            }
        }

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// This is the recommended method for reading table data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        public DataTable ReadTable(string tableName = null, IProgress<int> progress = null)
            => ReadTableCore(tableName, null, progress, default);

        /// <summary>
        /// Reads only <paramref name="columns"/> into a DataTable with native CLR column types.
        /// Unselected columns are never decoded — see
        /// <see cref="StreamRows(string, IReadOnlyList{string}, IProgress{int})"/>.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="columns">Column names (case-insensitive). Null or empty selects all columns.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        /// <exception cref="ArgumentException">A requested column does not exist in the table.</exception>
        public DataTable ReadTable(string tableName, IReadOnlyList<string> columns, IProgress<int> progress)
            => ReadTableCore(tableName, columns, progress, default);

        private DataTable ReadTableCore(string tableName, IReadOnlyList<string> columns, IProgress<int> progress,
                                        CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(tableName))
            {
                var tables = GetUserTables();
                if (tables.Count == 0) return null;
                tableName = tables[0].Name;
            }

            CatalogEntry entry = GetCatalogEntry(tableName);
            if (entry == null) return null;

            TableDef td = ReadTableDef(entry.TDefPage);
            if (td == null || td.Columns.Count == 0) return null;

            RowShape shape = BuildShape(td, columns);

            var dt = new DataTable(tableName);

            // Create columns with proper CLR types
            for (int i = 0; i < shape.Width; i++)
                dt.Columns.Add(shape.Names[i], shape.ClrTypes[i]);

            SetCapacityHint(dt, td);

            var scanner = new RowScanner();
            var buf = new object[shape.Width];
            byte[] scan = NewScanBuffer();

            // Suspends index maintenance and constraint checking for the duration of the load.
            dt.BeginLoadData();
            try
            {
                foreach (long p in EnumerateTablePages(entry.TDefPage))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] page = ReadPageForScan(p, scan);
                    if (page[0] != 0x01) continue;
                    if ((long)Ri32(page, _dpTDefOff) != entry.TDefPage) continue;

                    foreach (RowSpan span in EnumerateRowSpans(page, scanner))
                    {
                        // DataRowCollection.Add copies the values, so buf can be reused.
                        if (CrackRow(span.Page, span.Start, span.Size, shape, null, buf))
                            dt.Rows.Add(buf);
                    }
                    progress?.Report(dt.Rows.Count);
                }
            }
            finally
            {
                dt.EndLoadData();
            }

            return dt;
        }

        // ── Async Methods ──────────────────────────────────────────────────

        // ── Cancellable overloads ─────────────────────────────────────────
        //
        // These run the synchronous reader on a pool thread — the work is CPU and file I/O, and
        // the pages are already served largely from the OS cache, so there is no true async I/O
        // to be had. What the token buys is the ability to abandon a scan: a full read of a large
        // database easily outlives the request that started it, and without this it would run to
        // completion holding a thread regardless.

        /// <summary>
        /// Streams typed rows, stopping when <paramref name="cancellationToken"/> is signalled.
        /// The token is checked once per page.
        /// </summary>
        public IEnumerable<object[]> StreamRows(string tableName, IReadOnlyList<string> columns,
                                                IProgress<int> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsCore(tableName, columns, progress, true, cancellationToken);
        }

        /// <summary>
        /// Streams string rows, stopping when <paramref name="cancellationToken"/> is signalled.
        /// </summary>
        public IEnumerable<string[]> StreamRowsAsStrings(string tableName, IReadOnlyList<string> columns,
                                                         IProgress<int> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Guard.NotNullOrEmpty(tableName, nameof(tableName));
            return StreamRowsAsStringsCore(tableName, columns, progress, cancellationToken);
        }

        /// <summary>
        /// Reads a table into a typed DataTable asynchronously, honouring cancellation.
        /// </summary>
        public Task<DataTable> ReadTableAsync(string tableName, IReadOnlyList<string> columns,
                                              IProgress<int> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() => ReadTableCore(tableName, columns, progress, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Reads a table into a string DataTable asynchronously, honouring cancellation.
        /// </summary>
        public Task<DataTable> ReadTableAsStringDataTableAsync(string tableName, IReadOnlyList<string> columns,
                                                               IProgress<int> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() => ReadTableAsStringDataTableCore(tableName, columns, progress, cancellationToken),
                            cancellationToken);
        }

        /// <summary>Counts live rows asynchronously, honouring cancellation.</summary>
        public Task<long> GetRealRowCountAsync(string tableName, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() => GetRealRowCount(tableName, cancellationToken), cancellationToken);
        }

        /// <summary>Returns the names of all user tables in the database asynchronously.</summary>
        public Task<List<string>> ListTablesAsync()
        {
            return Task.Run(() => ListTables());
        }

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns asynchronously.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// </summary>
        public Task<DataTable> ReadTableAsync(string tableName = null, IProgress<int> progress = null)
        {
            return Task.Run(() => ReadTable(tableName, progress));
        }

        /// <summary>
        /// Async overload of <see cref="ReadTable(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows with native CLR types asynchronously.
        /// </summary>
        public Task<TableResult> ReadTableAsync(string tableName, int maxRows)
        {
            return Task.Run(() => ReadTable(tableName, maxRows));
        }

        /// <summary>
        /// Async overload of <see cref="ReadTableAsStrings(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows as strings asynchronously.
        /// </summary>
        public Task<StringTableResult> ReadTableAsStringsAsync(string tableName, int maxRows)
        {
            return Task.Run(() => ReadTableAsStrings(tableName, maxRows));
        }

        /// <summary>
        /// Returns statistical information about the database asynchronously.
        /// </summary>
        public Task<DatabaseStatistics> GetStatisticsAsync()
        {
            return Task.Run(() => GetStatistics());
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns asynchronously.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// </summary>
        public Task<Dictionary<string, DataTable>> ReadAllTablesAsync(IProgress<string> progress = null)
        {
            return Task.Run(() => ReadAllTables(progress));
        }

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings asynchronously.
        /// Use this for compatibility scenarios.
        /// </summary>
        public Task<Dictionary<string, DataTable>> ReadAllTablesAsStringsAsync(IProgress<string> progress = null)
        {
            return Task.Run(() => ReadAllTablesAsStrings(progress));
        }

        /// <summary>
        /// Pre-sizes a DataTable's row storage from the TDEF's row count. Without a hint the
        /// collection grows by doubling, which on a large table means repeatedly reallocating and
        /// copying it; supplying one cut allocations and peak heap by about a sixth on a
        /// 228 000-row read.
        ///
        /// The stored count can be stale — it drifts after deletes until a Compact &amp; Repair —
        /// so this is only ever a hint, and one that cannot affect correctness. It is ignored when
        /// implausible for the file's size, so a corrupt count cannot provoke a huge reservation.
        /// </summary>
        private void SetCapacityHint(DataTable dt, TableDef td)
        {
            long rows = td.RowCount;
            if (rows <= 0) return;

            // No table can hold more rows than the file has room for at a few bytes each.
            long ceiling = _fs.Length / 8;
            if (rows > ceiling || rows > int.MaxValue) return;

            dt.MinimumCapacity = (int)rows;
        }

        // ── Row enumeration ───────────────────────────────────────────────

        /// <summary>
        /// Bounds of one live row, plus the page holding it. An overflow row's bytes live on a
        /// different page than the one being scanned, so the page travels with the bounds.
        /// </summary>
        private struct RowSpan
        {
            public byte[] Page;
            public int Start;
            public int Size;
        }

        /// <summary>
        /// Resolves which columns a read decodes. <paramref name="columns"/> null or empty means
        /// every column in table order; otherwise the named columns, in the order given.
        /// </summary>
        private RowShape BuildShape(TableDef td, IReadOnlyList<string> columns)
        {
            int[] source;
            bool identity = columns == null || columns.Count == 0;

            if (identity)
            {
                source = new int[td.Columns.Count];
                for (int i = 0; i < source.Length; i++) source[i] = i;
            }
            else
            {
                source = new int[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    string want = columns[i];
                    int idx = td.Columns.FindIndex(c =>
                        string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));

                    if (idx < 0)
                        throw new ArgumentException(
                            $"Column '{want}' does not exist in this table. Available: " +
                            string.Join(", ", td.Columns.ConvertAll(c => c.Name)),
                            nameof(columns));

                    source[i] = idx;
                }
            }

            var shape = new RowShape
            {
                Table      = td,
                Source     = source,
                Columns    = new ColumnInfo[source.Length],
                Names      = new string[source.Length],
                ClrTypes   = new Type[source.Length],
                IsIdentity = identity
            };

            for (int o = 0; o < source.Length; o++)
            {
                ColumnInfo c = td.Columns[source[o]];
                shape.Columns[o]  = c;
                shape.Names[o]    = c.Name;
                shape.ClrTypes[o] = TypeCodeToClrType(c.Type);
            }

            return shape;
        }

        /// <summary>
        /// Row-offset entries on a data page, clamped to what fits. The field is 16 bits, so a
        /// corrupt page can claim 65535 rows and send the offset table reading past the buffer.
        /// </summary>
        private int PageRowCount(byte[] page)
        {
            int numRows = Ru16(page, _dpNumRows);
            int maxRows = (_pgSz - _dpRowsStart) / 2;
            return numRows > maxRows ? maxRows : numRows;
        }

        /// <summary>
        /// Reads every row-offset entry on the page into <paramref name="s"/> and sorts the
        /// physical positions so each row's end can be found by binary search.
        /// </summary>
        private int LoadRowOffsets(byte[] page, RowScanner s)
        {
            int numRows = PageRowCount(page);
            s.SortedCount = 0;
            if (numRows == 0) return 0;

            s.EnsureCapacity(numRows);

            int sorted = 0;
            for (int r = 0; r < numRows; r++)
            {
                int raw = Ru16(page, _dpRowsStart + r * 2);
                s.Raw[r] = raw;

                int pos = raw & 0x1FFF;
                if (pos > 0 && pos < _pgSz) s.Sorted[sorted++] = pos;
            }

            Array.Sort(s.Sorted, 0, sorted);
            s.SortedCount = sorted;
            return numRows;
        }

        /// <summary>
        /// A row ends just before the next higher row start, or at the end of the page.
        /// Binary search over the sorted positions, replacing a linear probe per row.
        /// </summary>
        private int FindRowEnd(RowScanner s, int rowStart)
        {
            int lo = 0, hi = s.SortedCount;
            while (lo < hi)
            {
                int mid = (int)(((uint)(lo + hi)) >> 1);
                if (s.Sorted[mid] > rowStart) hi = mid; else lo = mid + 1;
            }
            return lo < s.SortedCount ? s.Sorted[lo] - 1 : _pgSz - 1;
        }

        /// <summary>Yields the bounds of every live row on a page, following overflow pointers.</summary>
        private IEnumerable<RowSpan> EnumerateRowSpans(byte[] page, RowScanner scanner)
        {
            int numRows = LoadRowOffsets(page, scanner);
            RowScanner overflowScanner = null;

            for (int r = 0; r < numRows; r++)
            {
                int raw = scanner.Raw[r];
                if ((raw & 0x8000) != 0) continue; // deleted

                if ((raw & 0x4000) != 0)
                {
                    // Overflow: the entry is a pointer, not a row. Same encoding as an LVAL
                    // pointer — upper 24 bits the page, low byte the row index on it.
                    if (overflowScanner == null) overflowScanner = new RowScanner();

                    RowSpan? resolved = ResolveOverflowRow(page, raw & 0x1FFF, overflowScanner);
                    if (resolved.HasValue) yield return resolved.Value;
                    continue;
                }

                int rowStart = raw & 0x1FFF;
                int rowEnd   = FindRowEnd(scanner, rowStart);
                int rowSize  = rowEnd - rowStart + 1;
                if (rowSize < _numColsFldSz) continue;

                yield return new RowSpan { Page = page, Start = rowStart, Size = rowSize };
            }
        }

        /// <summary>
        /// Follows an overflow pointer to the page and row actually holding the data.
        /// Returns null when the pointer does not resolve to a usable row.
        /// </summary>
        private RowSpan? ResolveOverflowRow(byte[] page, int pointerAt, RowScanner scanner)
        {
            if (pointerAt < 0 || pointerAt + 4 > _pgSz) return null;

            uint pointer = Ru32(page, pointerAt);
            long targetPage = pointer >> 8;
            int  targetRow  = (int)(pointer & 0xFF);
            if (targetPage <= 0) return null;

            byte[] target;
            try { target = ReadPageCached(targetPage); }
            catch { return null; }

            if (target[0] != 0x01) return null;

            int targetRows = LoadRowOffsets(target, scanner);
            if (targetRow >= targetRows) return null;

            // The target's own offset entry carries the 0x8000 bit, which on an ordinary data page
            // would mean "deleted". On an overflow target it does not: the row is live and only
            // reachable through the pointer. Only the position bits are meaningful here.
            int rowStart = scanner.Raw[targetRow] & 0x1FFF;
            if (rowStart <= 0 || rowStart >= _pgSz) return null;

            int rowEnd  = FindRowEnd(scanner, rowStart);
            int rowSize = rowEnd - rowStart + 1;
            if (rowSize < _numColsFldSz) return null;

            return new RowSpan { Page = target, Start = rowStart, Size = rowSize };
        }

        // ── Row decoding ──────────────────────────────────────────────────

        // Boxing a bool allocates; there are only two possible values, so box them once.
        private static readonly object BoxedTrue  = true;
        private static readonly object BoxedFalse = false;

        /// <summary>
        /// Decodes one row into the caller's buffer. Exactly one of <paramref name="stringOut"/>
        /// and <paramref name="typedOut"/> must be non-null; the typed path builds CLR values
        /// straight from the row bytes instead of formatting a string and re-parsing it.
        /// Returns false when the row is malformed and should be skipped.
        /// </summary>
        private bool CrackRow(byte[] page, int rowStart, int rowSize, RowShape shape,
                              string[] stringOut, object[] typedOut)
        {
            if (rowSize < _numColsFldSz) return false;

            // Number of columns stored in THIS row (may be less than the table's column count
            // if columns were added after this row was written)
            int numCols = _jet4 ? Ru16(page, rowStart) : page[rowStart];
            if (numCols == 0) return false;

            // Check for deleted-column schema mismatch
            // If the table has deleted columns AND this row has MORE columns than current schema,
            // it was written before the deletion and data alignment is ambiguous
            if (shape.Table.HasDeletedColumns && numCols > shape.Table.Columns.Count)
            {
                throw new JetLimitationException(
                    $"Row has {numCols} columns but current schema has {shape.Table.Columns.Count} with deleted-column gaps. " +
                    $"This row predates schema changes and data may be misaligned. " +
                    $"Solution: Compact & Repair the database in Microsoft Access to rebuild all rows.");
            }

            int nullMaskSz  = (numCols + 7) / 8;
            int nullMaskPos = rowSize - nullMaskSz;  // relative to rowStart
            if (nullMaskPos < _numColsFldSz) return false;

            // ── Tail section layout (high→low addresses, reading from end) ──
            //  Jet4: [null_mask][var_len(2)][var_table(varLen*2)][eod(2)]
            //  Jet3: [null_mask][var_len(1)][jump_table(n*1)][var_table(varLen*1)][eod(1)]

            // A table whose columns are all fixed-length has no variable section in its rows:
            // no var_len, no var_table, no eod. Reading them anyway lands on the tail of the last
            // fixed column — which is zero often enough to pass, and garbage the rest of the time.
            // In Nwind's all-fixed "Order Details" that was the Discount float: every row with a
            // discount of 0 parsed, and all 838 rows with a real discount were dropped without a
            // word, 39% of the table.
            int varLen = 0, varTableStart = 0, eod = 0;

            if (shape.Table.HasVariableColumns)
            {
                int varLenPos = nullMaskPos - _varLenFldSz;  // relative
                if (varLenPos < _numColsFldSz) return false;

                varLen = _jet4 ? Ru16(page, rowStart + varLenPos) : page[rowStart + varLenPos];

                // Jet3 jump table: floor(rowSize / 256) entries of 1 byte each
                int jumpSz = _jet4 ? 0 : (rowSize / 256);

                varTableStart = varLenPos - jumpSz - varLen * _varEntrySz;  // relative
                int eodPos    = varTableStart - _eodFldSz;                  // relative
                if (eodPos < _numColsFldSz) return false;

                eod = _jet4 ? Ru16(page, rowStart + eodPos) : page[rowStart + eodPos];
            }

            // ── Decode each selected column ───────────────────────────────
            ColumnInfo[] cols = shape.Columns;
            bool typed = typedOut != null;

            for (int o = 0; o < cols.Length; o++)
            {
                ColumnInfo col = cols[o];

                // null_mask bit index = col.ColNum (the descriptor's col_num field),
                // NOT the output position.  JET rows index the mask by col_num,
                // while the TDEF may store columns in a different order (e.g. alphabetically).
                bool nullBit = false;
                if (col.ColNum < numCols)
                {
                    int mByte = nullMaskPos + (col.ColNum / 8);  // relative
                    int mBit  = col.ColNum % 8;
                    if (mByte < rowSize)
                        nullBit = (page[rowStart + mByte] & (1 << mBit)) != 0;
                }

                // BOOL: null_mask bit IS the value; no bytes stored in the row.
                // In JET: bit SET (1) = TRUE for BOOL.
                if (col.Type == T_BOOL)
                {
                    if (typed) typedOut[o] = nullBit ? BoxedTrue : BoxedFalse;
                    else       stringOut[o] = nullBit ? "True" : "False";
                    continue;
                }

                // For all other types: bit SET (1) = column HAS a value (not null).
                // bit CLEAR (0) = column IS null.
                // Column also has no value when it was added after this row was written.
                if (col.ColNum >= numCols || !nullBit)
                {
                    if (typed) typedOut[o] = DBNull.Value;
                    else       stringOut[o] = string.Empty;
                    continue;
                }

                if (col.IsFixed)
                {
                    int start = _numColsFldSz + col.FixedOff;  // relative
                    int sz    = FixedSize(col.Type, col.Size);
                    if (sz == 0 || start + sz > rowSize)
                    {
                        if (typed) typedOut[o] = DBNull.Value;
                        else       stringOut[o] = string.Empty;
                        continue;
                    }

                    if (typed) typedOut[o] = ReadFixedTyped(page, rowStart + start, col, sz);
                    else       stringOut[o] = ReadFixed(page, rowStart + start, col, sz);
                }
                else
                {
                    // Variable column — look up its offset in the reversed var_table.
                    // var_table is stored in reverse column order:
                    //   entry for VarIdx=k  →  varTableStart + (varLen-1-k)*varEntrySz
                    int dataStart = 0, dataLen = -1;

                    if (col.VarIdx < varLen)
                    {
                        int entryPos = varTableStart + (varLen - 1 - col.VarIdx) * _varEntrySz;  // relative
                        if (entryPos >= 0 && entryPos + _varEntrySz <= rowSize)
                        {
                            int varOff = _jet4 ? Ru16(page, rowStart + entryPos) : page[rowStart + entryPos];

                            // End of this variable column's data
                            int varEnd;
                            if (col.VarIdx + 1 < varLen)
                            {
                                int nextEntry = varTableStart + (varLen - 2 - col.VarIdx) * _varEntrySz;  // relative
                                varEnd = (_jet4 ? Ru16(page, rowStart + nextEntry) : page[rowStart + nextEntry]);
                            }
                            else
                            {
                                varEnd = eod;
                            }

                            // var_table entries are ROW offsets (from row[0]), not data-area offsets.
                            // FixedOff is a data-area offset (requires + _numColsFldSz), but var_table
                            // entries already include the num_cols header bytes.
                            dataStart = varOff;
                            dataLen   = varEnd - varOff;
                            if (dataLen < 0 || dataStart < 0 || dataStart + dataLen > rowSize)
                                dataLen = -1;
                        }
                    }

                    if (dataLen < 0)
                    {
                        if (typed) typedOut[o] = DBNull.Value;
                        else       stringOut[o] = string.Empty;
                        continue;
                    }

                    if (typed) typedOut[o] = ReadVarTyped(page, rowStart + dataStart, dataLen, col);
                    else       stringOut[o] = ReadVar(page, rowStart + dataStart, dataLen, col);
                }
            }

            return true;
        }

        // ── Typed value readers ───────────────────────────────────────────
        //
        // These build the CLR value directly from the row bytes. The previous typed path went
        // bytes → formatted string → Parse → box, which allocated a throwaway string per cell and
        // lost precision on the way: DateTime was rendered as "yyyy-MM-dd HH:mm:ss" (dropping
        // sub-second precision) and float/double used "G", which is lossy on .NET Framework.
        //
        // An empty string maps to DBNull.Value because that is what the old
        // TypedValueParser.ParseValue("") produced — an empty TEXT column reads as null, not "".

        private object ReadFixedTyped(byte[] row, int start, ColumnInfo col, int sz)
        {
            try
            {
                switch (col.Type)
                {
                    case T_BYTE:     return row[start];
                    case T_INT:      return (short)Ru16(row, start);
                    case T_LONG:     return Ri32(row, start);
                    case T_FLOAT:    return BitConverter.ToSingle(row, start);
                    case T_DOUBLE:   return BitConverter.ToDouble(row, start);
                    case T_DATETIME: return OaDateToValue(BitConverter.ToDouble(row, start));
                    case T_MONEY:    return MoneyToDecimal(BitConverter.ToInt64(row, start));
                    case T_NUMERIC:  return ReadNumericValue(row, start, col);
                    case T_GUID:     return ReadGuidValue(row, start);
                    default:         return NullIfEmpty(BitConverter.ToString(row, start, Math.Min(sz, 8)));
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return DBNull.Value;
            }
        }

        private object ReadVarTyped(byte[] row, int start, int len, ColumnInfo col)
        {
            if (len < 0) return DBNull.Value;
            try
            {
                switch (col.Type)
                {
                    // A stored zero-length string is not a null. The row's null mask has already
                    // spoken — a column whose bit is clear never reaches here — so an empty value
                    // at this point is a value, and Access keeps the two apart. Collapsing them
                    // used to turn 280 468 zero-length cells across the sample databases into
                    // nulls, which is a different fact about the data.
                    case T_TEXT:
                        return len == 0
                            ? string.Empty
                            : (object)(_jet4 ? DecodeJet4Text(row, start, len)
                                             : _ansiEncoding.GetString(row, start, len));

                    case T_BINARY:
                        return len == 0 ? string.Empty : (object)BitConverter.ToString(row, start, len);

                    case T_MEMO:
                    case T_OLE:
                        return len == 0 ? string.Empty : (object)ReadLongValue(row, start, len, col);

                    default:
                        // A fixed-size type stored in the variable area — JET allows it and Access
                        // uses it for GUIDs. The bytes are laid out exactly as they would be in
                        // the fixed area, so the same reader applies; only where they live differs.
                        int need = FixedSize(col.Type, col.Size);
                        if (need > 0 && len >= need) return ReadFixedTyped(row, start, col, need);
                        return DBNull.Value;
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return DBNull.Value;
            }
        }

        /// <summary>
        /// JET currency is an int64 of ten-thousandths. Building the decimal with an explicit
        /// scale of 4 rather than dividing keeps the trailing zeros the old string round-trip
        /// produced ("1.0000", not "1") — the numeric value is the same either way, but the scale
        /// is visible through ToString() and in a DataGridView.
        /// </summary>
        private static decimal MoneyToDecimal(long raw)
        {
            bool negative = raw < 0;

            // long.MinValue has no positive counterpart; the unchecked cast yields its magnitude.
            ulong magnitude = negative ? unchecked((ulong)(-raw)) : (ulong)raw;

            return new decimal((int)(magnitude & 0xFFFFFFFF), (int)(magnitude >> 32), 0, negative, 4);
        }

        private static object NullIfEmpty(string s) =>
            string.IsNullOrEmpty(s) ? (object)DBNull.Value : s;

        private static object OaDateToValue(double oaDate)
        {
            try   { return DateTime.FromOADate(oaDate); }
            catch { return DBNull.Value; }
        }

        private static Guid ReadGuidValue(byte[] b, int start)
        {
            // First three groups are stored little-endian in the Jet format
            return new Guid(
                Ri32(b, start),
                (short)Ru16(b, start + 4),
                (short)Ru16(b, start + 6),
                b[start + 8],  b[start + 9],  b[start + 10], b[start + 11],
                b[start + 12], b[start + 13], b[start + 14], b[start + 15]);
        }

        // ── Column size helpers ───────────────────────────────────────────

        /// <summary>Returns the expected byte size for a fixed-length column type.</summary>
        private static int FixedSize(byte type, int declaredSize)
        {
            switch (type)
            {
                case T_BYTE:    return 1;
                case T_INT:     return 2;
                case T_LONG:    return 4;
                case T_MONEY:   return 8;
                case T_FLOAT:   return 4;
                case T_DOUBLE:  return 8;
                case T_DATETIME:return 8;
                case T_GUID:    return 16;
                case T_NUMERIC: return 17;
                default:        return declaredSize > 0 ? declaredSize : 0;
            }
        }

        // ── Fixed-column value reader ─────────────────────────────────────

        private string ReadFixed(byte[] row, int start, ColumnInfo col, int sz)
        {
            try
            {
                switch (col.Type)
                {
                    // "R" rather than "G": on .NET Framework "G" gives 15 significant digits
                    // and silently loses the value — 0.1+0.2 renders as "0.3", which does not
                    // parse back to what was stored. "R" round-trips on every runtime and leaves
                    // ordinary values alone (1059.31 stays "1059.31"). The exact digits of a value
                    // that needs more than 15 still differ between .NET Framework and .NET Core,
                    // but the value itself survives, which is what the string path is for.
                    case T_BYTE:
                        return row[start].ToString(CultureInfo.InvariantCulture);
                    case T_INT:
                        return ((short)Ru16(row, start)).ToString(CultureInfo.InvariantCulture);
                    case T_LONG:
                        return Ri32(row, start).ToString(CultureInfo.InvariantCulture);
                    case T_FLOAT:
                        return BitConverter.ToSingle(row, start).ToString("R", CultureInfo.InvariantCulture);
                    case T_DOUBLE:
                        return BitConverter.ToDouble(row, start).ToString("R", CultureInfo.InvariantCulture);
                    case T_DATETIME:
                        return OaDateToString(BitConverter.ToDouble(row, start));
                    case T_MONEY:
                        return MoneyToDecimal(BitConverter.ToInt64(row, start)).ToString("F4", CultureInfo.InvariantCulture);
                    case T_NUMERIC:
                        return ReadNumeric(row, start, col);
                    case T_GUID:
                        return ReadGuid(row, start);
                    default:
                        return BitConverter.ToString(row, start, Math.Min(sz, 8));
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string ReadVar(byte[] row, int start, int len, ColumnInfo col)
        {
            if (len <= 0) return string.Empty;
            try
            {
                switch (col.Type)
                {
                    case T_TEXT:
                        return _jet4 ? DecodeJet4Text(row, start, len)
                                     : _ansiEncoding.GetString(row, start, len);

                    case T_BINARY:
                        return BitConverter.ToString(row, start, len);

                    case T_MEMO:
                    case T_OLE:
                        return ReadLongValue(row, start, len, col);

                    default:
                        // See ReadVarTyped: a fixed-size type can live in the variable area.
                        int need = FixedSize(col.Type, col.Size);
                        if (need > 0 && len >= need) return ReadFixed(row, start, col, need);
                        return string.Empty;
                }
            }
            catch (JetLimitationException)
            {
                throw;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
        //
        // MEMO/OLE field header (12 bytes):
        //   [memo_len: 3 bytes][bitmask: 1 byte][lval_dp: 4 bytes][unknown: 4 bytes]
        //
        // bitmask:
        //   0x80 = inline data immediately after the 12-byte header
        //   0x40 = single LVAL page:  lval_dp = (page << 8) | row_index
        //   0x00 = chained LVAL pages (not decoded; placeholder returned)

        /// <summary>
        /// Reads <paramref name="maxLen"/> bytes from a single LVAL data page / row.
        /// lval_dp encoding: upper 24 bits = page number, lower 8 bits = row index.
        /// </summary>
        /// <summary>Bytes of pointer at the head of every chunk in an LVAL chain.</summary>
        private const int LvalChainHeader = 4;

        private byte[] ReadLvalBytes(uint lvalDp, int maxLen)
        {
            try
            {
                int lvalPage = (int)(lvalDp >> 8);
                int lvalRow  = (int)(lvalDp & 0xFF);
                if (lvalPage <= 0) return null;

                byte[] page = ReadPageCached(lvalPage);
                if (page[0] != 0x01) return null;  // must be a data page

                int numRows = PageRowCount(page);
                if (lvalRow >= numRows) return null;

                int rawOff = Ru16(page, _dpRowsStart + lvalRow * 2);
                if ((rawOff & 0xC000) != 0) return null;  // deleted or overflow

                int rowStart = rawOff & 0x1FFF;
                if (rowStart == 0 || rowStart >= _pgSz) return null;

                int rowEnd = _pgSz - 1;
                for (int r = 0; r < numRows; r++)
                {
                    int ofs = Ru16(page, _dpRowsStart + r * 2) & 0x1FFF;
                    if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
                }

                int rowSize = Math.Min(rowEnd - rowStart + 1, maxLen);
                if (rowSize <= 0) return null;

                var data = new byte[rowSize];
                Buffer.BlockCopy(page, rowStart, data, 0, rowSize);
                return data;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads multi-page LVAL chains (bitmask 0x00). Follows LVAL page links until
        /// the entire memo is reconstructed or maxLen is reached.
        /// LVAL chunk format: [next_page(4)][data...] — the rest of the row is payload.
        /// </summary>
        private LvalChainResult ReadLvalChain(uint firstLvalDp, int maxLen)
        {
            if (maxLen <= 0) return LvalChainResult.Failure("no chunks read");

            try
            {
                // Assemble the chain into one buffer instead of collecting chunks into a
                // List<byte[]> and copying everything again, which peaked at twice the memo size.
                //
                // maxLen comes from a 3-byte header field, so a corrupt row can claim 16 MB while
                // the chain holds a single page. Start small and grow towards maxLen rather than
                // trusting the header up front — otherwise one bad row costs 16 MB.
                var result = new byte[Math.Min(maxLen, 64 * 1024)];
                int totalLen = 0;
                uint currentDp = firstLvalDp;
                var seen = new HashSet<uint>();

                while (currentDp != 0 && totalLen < maxLen && !seen.Contains(currentDp))
                {
                    seen.Add(currentDp);

                    int lvalPage = (int)(currentDp >> 8);
                    int lvalRow  = (int)(currentDp & 0xFF);
                    if (lvalPage <= 0) return LvalChainResult.Failure($"invalid page {lvalPage}");

                    byte[] page = ReadPageCached(lvalPage);
                    if (page[0] != 0x01) return LvalChainResult.Failure($"page {lvalPage} not data page");

                    int numRows = PageRowCount(page);
                    if (lvalRow >= numRows) return LvalChainResult.Failure($"row {lvalRow} >= numRows {numRows}");

                    int rawOff = Ru16(page, _dpRowsStart + lvalRow * 2);
                    if ((rawOff & 0xC000) != 0) return LvalChainResult.Failure("deleted/overflow row");

                    int rowStart = rawOff & 0x1FFF;
                    if (rowStart == 0 || rowStart >= _pgSz) return LvalChainResult.Failure($"invalid rowStart {rowStart}");

                    int rowEnd = _pgSz - 1;
                    for (int r = 0; r < numRows; r++)
                    {
                        int ofs = Ru16(page, _dpRowsStart + r * 2) & 0x1FFF;
                        if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
                    }

                    int rowSize = rowEnd - rowStart + 1;
                    if (rowSize < LvalChainHeader) return LvalChainResult.Failure($"rowSize {rowSize} < {LvalChainHeader}");

                    // LVAL chain chunk: [next_dp(4)][data...]. Nothing else — the chunk carries no
                    // length of its own. The total is already in the memo header that sent us
                    // here, and the chunk's own length is its row length.
                    //
                    // This used to skip eight bytes, taking the first four bytes of payload as a
                    // length field. That cost four bytes at the head of every chunk, so a memo
                    // spanning three LVAL pages came back missing two characters in three places.
                    // The bogus length was harmless only because it was clamped to the row.
                    currentDp = Ru32(page, rowStart);
                    int dataStart = rowStart + LvalChainHeader;
                    int availableData = Math.Min(rowSize - LvalChainHeader, maxLen - totalLen);

                    if (availableData > 0 && dataStart + availableData <= page.Length)
                    {
                        if (totalLen + availableData > result.Length)
                        {
                            int grown = Math.Min(maxLen, Math.Max(result.Length * 2, totalLen + availableData));
                            Array.Resize(ref result, grown);
                        }

                        Buffer.BlockCopy(page, dataStart, result, totalLen, availableData);
                        totalLen += availableData;
                    }
                }

                if (totalLen == 0) return LvalChainResult.Failure("no chunks read");
                return LvalChainResult.Success(result, totalLen);
            }
            catch (Exception ex) { return LvalChainResult.Failure(ex.Message); }
        }

        /// <summary>
        /// Scans the first 512 bytes for known file magic numbers (images, PDFs, Office docs, archives).
        /// Typical Access OLE fields wrap files in an OLE container (~78-byte header),
        /// so this scans beyond the OLE envelope to find the real file bytes.
        /// Returns a data-URI with appropriate MIME type, or null if no known format is found.
        /// </summary>
        private static string TryDecodeOleObject(byte[] b, int start, int len)
        {
            if (b == null || len < 4) return null;

            int scanEnd = Math.Min(start + len, start + 512);
            for (int i = start; i < scanEnd - 3; i++)
            {
                // ── Images ──
                // JPEG: FF D8 FF
                if (b[i] == 0xFF && b[i+1] == 0xD8 && b[i+2] == 0xFF)
                {
                    int fileLen = start + len - i;
                    return "data:image/jpeg;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // PNG: 89 50 4E 47
                if (b[i] == 0x89 && b[i+1] == 0x50 && b[i+2] == 0x4E && b[i+3] == 0x47)
                {
                    int fileLen = start + len - i;
                    return "data:image/png;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // GIF: 47 49 46
                if (b[i] == 0x47 && b[i+1] == 0x49 && b[i+2] == 0x46)
                {
                    int fileLen = start + len - i;
                    return "data:image/gif;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // BMP: 42 4D. Two bytes is far too weak on its own — "BM" occurs constantly in
                // ordinary binary data — so also require the header to be self-consistent: the
                // reserved fields must be zero and the declared size must match what is left.
                if (b[i] == 0x42 && b[i+1] == 0x4D && i + 14 <= start + len)
                {
                    int declared = Ri32(b, i + 2);
                    int reserved = Ri32(b, i + 6);
                    int pixelOffset = Ri32(b, i + 10);
                    int remaining = start + len - i;

                    if (reserved == 0 && declared == remaining && pixelOffset > 0 && pixelOffset < remaining)
                        return "data:image/bmp;base64," + Convert.ToBase64String(b, i, remaining);
                }

                // ── Documents ──
                // PDF: 25 50 44 46 (%PDF)
                if (b[i] == 0x25 && b[i+1] == 0x50 && b[i+2] == 0x44 && b[i+3] == 0x46)
                {
                    int fileLen = start + len - i;
                    return "data:application/pdf;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // ZIP (also DOCX/XLSX/PPTX): 50 4B 03 04 (PK..)
                if (i + 3 < scanEnd && b[i] == 0x50 && b[i+1] == 0x4B && b[i+2] == 0x03 && b[i+3] == 0x04)
                {
                    int fileLen = start + len - i;
                    // Check if it's an Office Open XML file by looking for [Content_Types].xml signature
                    // For simplicity, return generic zip MIME
                    return "data:application/zip;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // DOC (Word 97-2003): D0 CF 11 E0 (OLE compound file)
                if (i + 3 < scanEnd && b[i] == 0xD0 && b[i+1] == 0xCF && b[i+2] == 0x11 && b[i+3] == 0xE0)
                {
                    int fileLen = start + len - i;
                    return "data:application/msword;base64," + Convert.ToBase64String(b, i, fileLen);
                }
                // RTF: 7B 5C 72 74 ({\rt)
                if (i + 3 < scanEnd && b[i] == 0x7B && b[i+1] == 0x5C && b[i+2] == 0x72 && b[i+3] == 0x74)
                {
                    int fileLen = start + len - i;
                    return "data:application/rtf;base64," + Convert.ToBase64String(b, i, fileLen);
                }
            }
            return null;
        }

        private string ReadLongValue(byte[] row, int start, int len, ColumnInfo col)
        {
            bool isOle = col.Type == T_OLE;
            if (len < 12) return isOle ? "(OLE)" : "(memo)";

            // Base64-encoding an OLE blob costs the blob itself plus a string 1.33x its size. When
            // the caller does not want the payload, bail out before any of it happens — including
            // the LVAL page reads, which is where the real cost of a table full of images lives.
            if (isOle && OleObjectMode == OleObjectMode.Placeholder) return "(OLE)";

            byte bitmask = row[start + 3];
            int  memoLen  = row[start] | (row[start + 1] << 8) | (row[start + 2] << 16);

            if ((bitmask & 0x80) != 0)
            {
                // Inline: data follows the 12-byte header
                int memoStart = start + 12;
                if (memoStart + memoLen > row.Length) memoLen = row.Length - memoStart;
                if (memoLen <= 0) return string.Empty;

                if (isOle) return TryDecodeOleObject(row, memoStart, memoLen) ?? "(OLE)";

                return _jet4 ? DecodeJet4Text(row, memoStart, memoLen)
                             : _ansiEncoding.GetString(row, memoStart, memoLen);
            }

            if ((bitmask & 0x40) != 0)
            {
                // Single LVAL page — lval_dp = (pageNumber << 8) | rowIndex
                uint   lvalDp   = Ru32(row, start + 4);
                byte[] lvalData = ReadLvalBytes(lvalDp, memoLen);

                if (lvalData != null)
                {
                    if (isOle) return TryDecodeOleObject(lvalData, 0, lvalData.Length) ?? "(OLE)";

                    return _jet4 ? DecodeJet4Text(lvalData, 0, lvalData.Length)
                                 : _ansiEncoding.GetString(lvalData);
                }

                return isOle ? "(OLE)" : "(memo on LVAL page)";
            }

            // Multi-page LVAL (0x00) — follow the chain
            uint chainDp = Ru32(row, start + 4);
            LvalChainResult chain = ReadLvalChain(chainDp, memoLen);

            if (chain.Data != null)
            {
                if (isOle) return TryDecodeOleObject(chain.Data, 0, chain.Length) ?? "(OLE)";

                return _jet4 ? DecodeJet4Text(chain.Data, 0, chain.Length)
                             : _ansiEncoding.GetString(chain.Data, 0, chain.Length);
            }

            return isOle ? $"(OLE chain error: {chain.Error})" : $"(memo chain error: {chain.Error})";
        }

        // ── Jet4 text decoding ────────────────────────────────────────────

        /// <summary>
        /// Decodes JET4 text, which is either compressed or plain UCS-2. Compressed text opens
        /// with the two bytes FF FE.
        ///
        /// Those same two bytes are also how plain UCS-2 encodes a leading U+FEFF byte-order mark,
        /// and nothing distinguishes the two cases: the column descriptor carries no usable
        /// "compressed" flag — databases that store compressed text and databases that store an
        /// uncompressed BOM have identical descriptor bytes — and the payload after FF FE parses
        /// either way. A value whose first character really is a BOM therefore loses it. mdbtools
        /// and jackcess read this the same way, and the alternative — gating on the flag that
        /// looked like it meant compression — turned every compressed memo in a real database into
        /// mojibake, which is a far worse trade than one lost BOM.
        /// </summary>
        private static string DecodeJet4Text(byte[] b, int start, int len)
        {
            if (len < 2) return string.Empty;
            if (b[start] == 0xFF && b[start + 1] == 0xFE)
                return DecompressJet4(b, start + 2, len - 2);
            // Plain UCS-2 LE — length must be even
            int evenLen = len & ~1;
            return evenLen > 0 ? Encoding.Unicode.GetString(b, start, evenLen) : string.Empty;
        }

        /// <summary>
        /// Decodes the JET4 "compressed unicode" encoding.
        /// A 0x00 byte toggles between 1-byte compressed (ASCII) and 2-byte
        /// uncompressed (UCS-2) mode.
        /// </summary>
        private static string DecompressJet4(byte[] b, int start, int len)
        {
            // At most one char per input byte, so a single exactly-bounded buffer beats a
            // StringBuilder: no chunk list, no growth, and one copy into the final string.
            var chars = new char[len];
            int n = 0;

            bool compressed = true;
            int i = start, end = start + len;

            while (i < end)
            {
                // One NUL toggles the mode, and it does so in both directions. Leaving
                // uncompressed mode used to require a 0x00 0x00 pair here, which is not what JET
                // writes — and the cost was not a missing character but a lost byte of alignment.
                // Every UCS-2 pair after the first non-Latin-1 character was then read one byte
                // late, so the rest of the value came back as mojibake: "this “Welcome” panel"
                // turned into "this “圀汥潣敭". Text without accents or smart quotes never leaves
                // compressed mode, which is why most values looked fine.
                if (b[i] == 0x00) { compressed = !compressed; i++; continue; }

                if (compressed)
                {
                    chars[n++] = (char)b[i++];
                }
                else
                {
                    if (i + 1 >= end) break;
                    chars[n++] = (char)(b[i] | (b[i + 1] << 8));
                    i += 2;
                }
            }

            return n == 0 ? string.Empty : new string(chars, 0, n);
        }

        // ── Numeric / GUID helpers ────────────────────────────────────────

        private static string OaDateToString(double oaDate)
        {
            // InvariantCulture is not cosmetic here: a culture whose default calendar is not
            // Gregorian renders a different date entirely. Under th-TH this same value came out
            // as 2541-06-01 instead of 1998-06-01, and under ar-SA as 1419-02-07.
            try
            {
                DateTime value = DateTime.FromOADate(oaDate);

                // A fixed "HH:mm:ss" silently dropped the fractional second, which JET does store:
                // 220 of 1226 date cells in the test databases lost theirs. The fraction is only
                // appended when there is one, so whole-second dates render exactly as before.
                string format = value.Ticks % TimeSpan.TicksPerSecond == 0
                    ? "yyyy-MM-dd HH:mm:ss"
                    : "yyyy-MM-dd HH:mm:ss.FFFFFFF";

                return value.ToString(format, CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Reads a Jet NUMERIC (17 bytes).
        ///
        /// Layout, which is not the one this used to assume:
        ///
        ///   [sign(1)][w3(4)][w2(4)][w1(4)][w0(4)]
        ///
        /// A sign byte — zero for positive — then a 128-bit magnitude as four 32-bit
        /// little-endian words stored **most significant first**, so the low word sits at the very
        /// end of the field. The scale is not in the row at all: it comes from the column
        /// descriptor, because the stored bytes say nothing about where the point goes.
        ///
        /// The previous reading took the scale from byte 1 and the magnitude from bytes 4..15,
        /// which is the layout of nothing. Chinook's <c>Track.UnitPrice</c> — 0.99 in every one of
        /// its 3 503 rows — came back as 467514281804094876155904: the 0x63 that is 99 sits nine
        /// bytes away from where it was looked for, and the scale of 2 was never read at all.
        /// </summary>
        private static string ReadNumeric(byte[] b, int start, ColumnInfo col)
        {
            object v = ReadNumericValue(b, start, col);
            return v is decimal d ? d.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        /// <summary>
        /// Decimal form of <see cref="ReadNumeric"/>. Returns <see cref="DBNull.Value"/> when the
        /// field is truncated, matching the empty string the string path yields for that case.
        /// </summary>
        private static object ReadNumericValue(byte[] b, int start, ColumnInfo col)
        {
            if (start + 17 > b.Length) return DBNull.Value;

            bool neg   = b[start] != 0;
            uint w3    = Ru32(b, start + 1);    // most significant
            uint w2    = Ru32(b, start + 5);
            uint w1    = Ru32(b, start + 9);
            uint w0    = Ru32(b, start + 13);   // least significant
            byte scale = col?.Scale ?? 0;

            // decimal is a 96-bit mantissa with a scale of at most 28. JET's field is 128-bit, so
            // a value that uses the top word cannot be represented — say so rather than truncate.
            if (w3 != 0)
                throw new JetLimitationException(
                    $"T_NUMERIC value needs more than the 96 bits a .NET decimal holds " +
                    $"(w3=0x{w3:X8}, w2=0x{w2:X8}, w1=0x{w1:X8}, w0=0x{w0:X8}).");

            if (scale > 28)
                throw new JetLimitationException(
                    $"T_NUMERIC scale {scale} exceeds the .NET decimal maximum of 28.");

            try
            {
                // Returns the decimal itself. Formatting belongs to ReadNumeric, which is the
                // string path; returning text from here made every DECIMAL column read as an
                // empty string and put a boxed string into a typeof(decimal) DataTable column.
                return new decimal((int)w0, (int)w1, (int)w2, neg, scale);
            }
            catch (OverflowException ex)
            {
                throw new JetLimitationException(
                    $"T_NUMERIC value overflow (w2=0x{w2:X8}, w1=0x{w1:X8}, w0=0x{w0:X8}, scale={scale})", ex);
            }
        }

        private static string ReadGuid(byte[] b, int start)
        {
            if (start + 16 > b.Length) return string.Empty;
            // First three groups are stored little-endian in the Jet format
            return string.Format(CultureInfo.InvariantCulture,
                "{{{0:X2}{1:X2}{2:X2}{3:X2}-{4:X2}{5:X2}-{6:X2}{7:X2}" +
                "-{8:X2}{9:X2}-{10:X2}{11:X2}{12:X2}{13:X2}{14:X2}{15:X2}}}",
                b[start+3], b[start+2], b[start+1], b[start],
                b[start+5], b[start+4],
                b[start+7], b[start+6],
                b[start+8], b[start+9],
                b[start+10], b[start+11], b[start+12],
                b[start+13], b[start+14], b[start+15]);
        }
    }
}
