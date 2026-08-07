using System.Collections.Generic;

namespace JetDatabaseReader
{
    internal sealed class ColumnInfo
    {
        public byte   Type;
        public int    ColNum;    // col_num: absolute column number (includes deleted cols)
        public int    VarIdx;    // offset_V: 0-based index in var_table
        public int    FixedOff;  // offset_F: byte offset within the fixed area
        public int    Size;      // col_len (0 for MEMO/OLE/variable)
        public byte   Flags;
        public byte   Precision; // col_prec:  total digits, T_NUMERIC only
        public byte   Scale;     // col_scale: digits after the point, T_NUMERIC only
        public string Name = string.Empty;

        /// <summary>
        /// Whether the column's bytes live in the row's fixed area or in its variable area.
        ///
        /// The descriptor's FLAG_FIXED bit decides this, and only that bit. A column's *type* does
        /// not: JET is free to put a fixed-size type in the variable area, and Access does — the
        /// <c>rowguid</c> columns in AdventureWorksLT are GUIDs stored variably, with the flag
        /// clear and a var_table index assigned.
        ///
        /// Deciding by type instead read those GUIDs from the fixed area, at whatever offset the
        /// unused offset_F field happened to hold — zero, which is the first column. Every
        /// <c>rowguid</c> in that database came back built out of the row's primary key.
        /// </summary>
        public bool IsFixed => (Flags & 0x01) != 0;   // FLAG_FIXED
    }
}
