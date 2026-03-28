using System.Collections.Generic;

namespace JetDatabaseReader
{
    /// <summary>
    /// Schema entry for a single column in a <see cref="TablePreviewResult"/>.
    /// </summary>
    public sealed class TablePreviewColumn
    {
        /// <summary>Column name.</summary>
        public string Name { get; set; }

        /// <summary>Access-friendly type name (e.g., "Text", "Long Integer", "Date/Time").</summary>
        public string TypeName { get; set; }

        /// <summary>Human-readable size description (e.g., "255 chars", "8 bytes", "LVAL").</summary>
        public string SizeDesc { get; set; }
    }

    /// <summary>
    /// Result returned by <see cref="IAccessReader.ReadTablePreview"/>.
    /// Contains column headers, sampled rows (as strings), and per-column schema information.
    /// </summary>
    public sealed class TablePreviewResult
    {
        /// <summary>Ordered list of column names.</summary>
        public List<string> Headers { get; set; }

        /// <summary>Up to <c>maxRows</c> rows, each row a list of string values (one per column).</summary>
        public List<List<string>> Rows { get; set; }

        /// <summary>Per-column schema information in the same order as <see cref="Headers"/>.</summary>
        public List<TablePreviewColumn> Schema { get; set; }
    }
}
