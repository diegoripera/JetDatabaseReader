using System.Collections.Generic;
using System.Linq;

namespace JetDatabaseReader
{
    internal sealed class TableDef
    {
        public List<ColumnInfo> Columns = new List<ColumnInfo>();
        public long RowCount;           // num_rows from TDEF page offset 16
        public bool HasDeletedColumns;  // true if ColNum sequence has gaps

        /// <summary>
        /// Pointer to the table's usage map — the bitmap of pages it owns — as
        /// (page &lt;&lt; 8) | row. Zero when the format or the file does not carry one.
        /// </summary>
        public uint UsedPagesDp;

        /// <summary>
        /// The pages the usage map names, ascending; null when it could not be read. Resolved
        /// once per table and cached here, because every read path asks for it.
        /// </summary>
        public long[] UsagePages;
        public bool UsagePagesResolved;

        private bool? _hasVariableColumns;

        /// <summary>
        /// Whether rows of this table carry the variable-length section at all.
        ///
        /// When every column is fixed-length, JET writes no var_len field, no var_table and no
        /// end-of-data offset — the row is num_cols, the fixed data, and the null mask, and
        /// nothing else. Reading a var_len that is not there takes the last two bytes of the final
        /// fixed column instead, which happens to be zero often enough to look correct.
        /// </summary>
        public bool HasVariableColumns =>
            (_hasVariableColumns ?? (_hasVariableColumns = Columns.Any(c => !c.IsFixed))).Value;
    }
}
