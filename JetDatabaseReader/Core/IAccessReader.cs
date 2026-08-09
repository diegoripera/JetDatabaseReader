using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace JetDatabaseReader
{
    /// <summary>
    /// Interface for reading Microsoft Access JET databases (.mdb / .accdb).
    /// Provides methods for listing tables, reading data, and streaming large datasets.
    /// </summary>
    public interface IAccessReader : IDisposable
    {
        /// <summary>When true, GetUserTables logs verbose hex dumps for debugging. Default: false.</summary>
        bool DiagnosticsEnabled { get; set; }

        /// <summary>Maximum number of pages to keep in cache. 0 = unlimited, -1 = disabled. Default: 256 (1 MB for 4K pages).</summary>
        int PageCacheSize { get; set; }

        /// <summary>Has no effect. Kept so existing code keeps compiling.</summary>
        [Obsolete("Has no effect — page reads are serialised on the shared file handle.")]
        bool ParallelPageReadsEnabled { get; set; }

        /// <summary>Diagnostic output populated after each call to <see cref="ListTables"/>.</summary>
        string LastDiagnostics { get; }

        /// <summary>
        /// Returns the column headers and up to <paramref name="maxRows"/> rows
        /// from the first user table, plus the table name and total table count.
        /// </summary>
        FirstTableResult ReadFirstTable(int maxRows = 100);

        /// <summary>Returns the names of all user tables in the database.</summary>
        List<string> ListTables();

        /// <summary>
        /// Returns name, stored row-count, and column-count for every user table.
        /// Calling this instead of <see cref="ListTables"/> avoids a duplicate catalog scan.
        /// </summary>
        List<TableStat> GetTableStats();

        /// <summary>
        /// Returns table metadata as a DataTable with columns: TableName, RowCount, ColumnCount.
        /// Ideal for binding to data grids or exporting to CSV/Excel.
        /// </summary>
        DataTable GetTablesAsDataTable();

        /// <summary>
        /// Scans all data pages to count live (non-deleted, non-overflow) rows for the specified table.
        /// This is slower than reading the TDEF RowCount (which may be stale), but always accurate.
        /// Use this after many deletes/imports when Compact & Repair hasn't been run.
        /// </summary>
        long GetRealRowCount(string tableName);

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with native CLR types.
        /// Rows are in <see cref="TableResult.Rows"/>.
        /// Use <see cref="ReadTableAsStrings"/> when raw string values are needed.
        /// </summary>
        TableResult ReadTable(string tableName, int maxRows);

        /// <summary>
        /// Reads up to <paramref name="maxRows"/> rows from the table named
        /// <paramref name="tableName"/> (case-insensitive) with all values as strings.
        /// Rows are in <see cref="StringTableResult.Rows"/>.
        /// </summary>
        StringTableResult ReadTableAsStrings(string tableName, int maxRows);

        /// <summary>
        /// Async overload of <see cref="ReadTable(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows with native CLR types asynchronously.
        /// </summary>
        Task<TableResult> ReadTableAsync(string tableName, int maxRows);

        /// <summary>
        /// Async overload of <see cref="ReadTableAsStrings(string, int)"/>.
        /// Reads up to <paramref name="maxRows"/> rows as strings asynchronously.
        /// </summary>
        Task<StringTableResult> ReadTableAsStringsAsync(string tableName, int maxRows);

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as properly typed object arrays without collecting them all in memory.
        /// Each element in the array is the native CLR type (int, DateTime, decimal, etc.).
        /// Ideal for large tables — use foreach to process one row at a time.
        /// This is the recommended method for streaming data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        IEnumerable<object[]> StreamRows(string tableName, IProgress<int> progress = null);

        /// <summary>
        /// Yields rows from <paramref name="tableName"/> as string arrays without collecting them all in memory.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="StreamRows"/> which returns properly typed data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive).</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        IEnumerable<string[]> StreamRowsAsStrings(string tableName, IProgress<int> progress = null);

        /// <summary>
        /// Reads the entire table into a DataTable with all columns typed as strings.
        /// Use this for compatibility scenarios or when you need raw string data.
        /// For most use cases, prefer <see cref="ReadTable"/> which returns properly typed columns.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        DataTable ReadTableAsStringDataTable(string tableName = null, IProgress<int> progress = null);

        /// <summary>
        /// True when the database carries a Jet4 database password. That password is access
        /// control rather than encryption — the page data is stored in plain text either way.
        /// </summary>
        bool IsPasswordProtected { get; }

        /// <summary>True when the pages are encrypted and are being decrypted as they are read.</summary>
        bool IsEncrypted { get; }

        /// <summary>
        /// Returns the tables that are linked rather than stored here, with the connection string
        /// and the name each has in its source. They are deliberately absent from
        /// <see cref="ListTables"/> because their rows are not in this file.
        /// </summary>
        List<LinkedTable> GetLinkedTables();

        /// <summary>
        /// Opens the Access database a linked table points at. The returned reader is a separate
        /// instance and the caller owns it — dispose it when finished.
        /// </summary>
        AccessReader OpenLinkedTableSource(LinkedTable link, AccessReaderOptions options = null);

        /// <summary>
        /// Drops the catalog, page index, and page cache so the next call re-reads from disk.
        /// Use when another process may have modified the database under a long-lived reader.
        /// </summary>
        void Refresh();

        /// <summary>Returns the column names of the specified table, in table order.</summary>
        List<string> GetColumnNames(string tableName);

        /// <summary>
        /// Returns rich metadata for all columns in the specified table.
        /// </summary>
        List<ColumnMetadata> GetColumnMetadata(string tableName);

        /// <summary>
        /// Yields only <paramref name="columns"/>, in the order given, as typed object arrays.
        /// Unselected columns are never decoded — for MEMO and OLE columns that also means their
        /// LVAL pages are never read.
        /// </summary>
        IEnumerable<object[]> StreamRows(string tableName, IReadOnlyList<string> columns, IProgress<int> progress);

        /// <summary>
        /// Yields only <paramref name="columns"/>, in the order given, as string arrays.
        /// </summary>
        IEnumerable<string[]> StreamRowsAsStrings(string tableName, IReadOnlyList<string> columns, IProgress<int> progress);

        /// <summary>
        /// Reads only <paramref name="columns"/> into a DataTable with native CLR column types.
        /// </summary>
        DataTable ReadTable(string tableName, IReadOnlyList<string> columns, IProgress<int> progress);

        /// <summary>
        /// Reads only <paramref name="columns"/> into a DataTable of string columns.
        /// </summary>
        DataTable ReadTableAsStringDataTable(string tableName, IReadOnlyList<string> columns, IProgress<int> progress);

        /// <summary>
        /// Opens a forward-only cursor over the table — the constant-memory path for feeding
        /// <c>SqlBulkCopy</c>, <c>DataTable.Load</c>, or a streaming exporter.
        /// </summary>
        AccessDataReader CreateDataReader(string tableName, IReadOnlyList<string> columns = null);

        /// <summary>Counts live rows, stopping when the token is signalled.</summary>
        long GetRealRowCount(string tableName, CancellationToken cancellationToken);

        /// <summary>Streams typed rows, stopping when the token is signalled.</summary>
        IEnumerable<object[]> StreamRows(string tableName, IReadOnlyList<string> columns,
                                         IProgress<int> progress, CancellationToken cancellationToken);

        /// <summary>Streams string rows, stopping when the token is signalled.</summary>
        IEnumerable<string[]> StreamRowsAsStrings(string tableName, IReadOnlyList<string> columns,
                                                  IProgress<int> progress, CancellationToken cancellationToken);

        /// <summary>Reads a table into a typed DataTable asynchronously, honouring cancellation.</summary>
        Task<DataTable> ReadTableAsync(string tableName, IReadOnlyList<string> columns,
                                       IProgress<int> progress, CancellationToken cancellationToken);

        /// <summary>Reads a table into a string DataTable asynchronously, honouring cancellation.</summary>
        Task<DataTable> ReadTableAsStringDataTableAsync(string tableName, IReadOnlyList<string> columns,
                                                        IProgress<int> progress, CancellationToken cancellationToken);

        /// <summary>Counts live rows asynchronously, honouring cancellation.</summary>
        Task<long> GetRealRowCountAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Returns statistical information about the database.
        /// </summary>
        DatabaseStatistics GetStatistics();

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// This is the recommended method for bulk reading.
        /// </summary>
        Dictionary<string, DataTable> ReadAllTables(IProgress<string> progress = null);

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings.
        /// Use this for compatibility scenarios.
        /// </summary>
        Dictionary<string, DataTable> ReadAllTablesAsStrings(IProgress<string> progress = null);

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// This is the recommended method for reading table data.
        /// </summary>
        /// <param name="tableName">Table name (case-insensitive). If null or empty, reads the first table.</param>
        /// <param name="progress">Optional progress reporter — receives row count after each page.</param>
        DataTable ReadTable(string tableName = null, IProgress<int> progress = null);

        // ── Async Methods ──────────────────────────────────────────────────

        /// <summary>Returns the names of all user tables in the database asynchronously.</summary>
        Task<List<string>> ListTablesAsync();

        /// <summary>
        /// Reads the entire table into a DataTable with properly typed columns asynchronously.
        /// Each column uses its native CLR type (int, DateTime, decimal, etc.).
        /// </summary>
        Task<DataTable> ReadTableAsync(string tableName = null, IProgress<int> progress = null);

        /// <summary>
        /// Returns statistical information about the database asynchronously.
        /// </summary>
        Task<DatabaseStatistics> GetStatisticsAsync();

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with properly typed columns asynchronously.
        /// Each table's columns use their native CLR types (int, DateTime, decimal, etc.).
        /// </summary>
        Task<Dictionary<string, DataTable>> ReadAllTablesAsync(IProgress<string> progress = null);

        /// <summary>
        /// Reads all tables into a dictionary of DataTables with all columns typed as strings asynchronously.
        /// Use this for compatibility scenarios.
        /// </summary>
        Task<Dictionary<string, DataTable>> ReadAllTablesAsStringsAsync(IProgress<string> progress = null);

        /// <summary>
        /// Creates a fluent query interface for the specified table.
        /// Supports both typed and string row access:
        /// <list type="bullet">
        ///   <item>Typed chain:  <c>Where(obj => ...)</c>          → <c>Execute()</c>          / <c>FirstOrDefault()</c>          / <c>Count()</c></item>
        ///   <item>String chain: <c>WhereAsStrings(str => ...)</c> → <c>ExecuteAsStrings()</c> / <c>FirstOrDefaultAsStrings()</c> / <c>CountAsStrings()</c></item>
        /// </list>
        /// </summary>
        TableQuery Query(string tableName);
    }
}
