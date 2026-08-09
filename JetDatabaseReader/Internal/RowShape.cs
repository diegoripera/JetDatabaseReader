using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// The output shape of a single read operation: which of the table's columns get decoded,
    /// in what order, and what they map to.
    ///
    /// Computed once per operation rather than once per cell — the previous code called
    /// <c>TypeCodeToClrType()</c> for every cell of every row. It is also what makes column
    /// projection possible: a column that is not in <see cref="Columns"/> is never decoded, so a
    /// MEMO or OLE column nobody asked for costs no LVAL page reads at all.
    /// </summary>
    internal sealed class RowShape
    {
        public TableDef Table;

        /// <summary>Output position → index into <see cref="TableDef.Columns"/>.</summary>
        public int[] Source;

        /// <summary>The selected columns, in output order.</summary>
        public ColumnInfo[] Columns;

        public string[] Names;
        public Type[] ClrTypes;

        /// <summary>True when every column is decoded in table order — the unprojected case.</summary>
        public bool IsIdentity;

        public int Width => Source.Length;
    }
}
