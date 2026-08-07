using System.Collections.Generic;
using System.Linq;

namespace JetDatabaseReader
{
    internal sealed class TableDef
    {
        public List<ColumnInfo> Columns = new List<ColumnInfo>();
        public long RowCount;           // num_rows from TDEF page offset 16
        public bool HasDeletedColumns;  // true if ColNum sequence has gaps

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
