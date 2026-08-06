using System.IO;

namespace JetDatabaseReader
{
    /// <summary>
    /// Configuration options for opening a JET database with <see cref="AccessReader"/>.
    /// </summary>
    public sealed class AccessReaderOptions
    {
        /// <summary>Maximum number of pages to keep in cache. 0 = unlimited, -1 = disabled. Default: 256 (1 MB for 4K pages).</summary>
        public int PageCacheSize { get; set; } = 256;

        /// <summary>When true, logs verbose diagnostic information. Default: false.</summary>
        public bool DiagnosticsEnabled { get; set; }

        /// <summary>When true, uses parallel processing for reading multiple pages. Can improve performance for large tables. Default: false.</summary>
        public bool ParallelPageReadsEnabled { get; set; }

        /// <summary>When true, validates the database format on open. Default: true.</summary>
        public bool ValidateOnOpen { get; set; } = true;

        /// <summary>
        /// How OLE Object columns are rendered. Default: <see cref="OleObjectMode.DataUri"/>.
        /// Set to <see cref="OleObjectMode.Placeholder"/> when the payloads are not needed —
        /// it skips both the base64 encoding and the LVAL page reads behind it.
        /// </summary>
        public OleObjectMode OleObjectMode { get; set; } = OleObjectMode.DataUri;

        /// <summary>
        /// FileStream buffer size in bytes. Default: 65536.
        /// Reads are one page at a seeked offset, so a buffer larger than the page size lets a
        /// front-to-back scan serve most pages without a syscall. Lower it to trade scan speed
        /// for a smaller per-reader footprint.
        /// </summary>
        public int FileBufferSize { get; set; } = 64 * 1024;

        /// <summary>File access mode. Default: Read.</summary>
        public FileAccess FileAccess { get; set; } = FileAccess.Read;

        /// <summary>
        /// File sharing mode. Default: Read (other processes may read but not write while the database is open).
        /// Set to <see cref="FileShare.ReadWrite"/> when another application (e.g. Microsoft Access) holds a write lock on the file.
        /// </summary>
        public FileShare FileShare { get; set; } = FileShare.ReadWrite;
    }
}
