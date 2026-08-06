using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace JetDatabaseReader
{
    /// <summary>
    /// Forward-only, read-only cursor over a JET table.
    ///
    /// This is the constant-memory way to move a large table somewhere else: the reader holds one
    /// row at a time, so <c>SqlBulkCopy.WriteToServer(reader)</c>, <c>DataTable.Load(reader)</c>,
    /// or a hand-written CSV loop all run without materialising the table. Reading a 200K-row
    /// table this way costs kilobytes instead of hundreds of megabytes.
    ///
    /// Values are valid only until the next <see cref="Read"/>, per the
    /// <see cref="IDataReader"/> contract — copy anything you intend to keep.
    /// </summary>
    public sealed class AccessDataReader : IDataReader
    {
        private readonly string _tableName;
        private readonly string[] _names;
        private readonly Type[] _types;
        private readonly IEnumerator<object[]> _rows;
        private Dictionary<string, int> _ordinals;
        private object[] _current;
        private bool _closed;

        internal AccessDataReader(string tableName, string[] names, Type[] types, IEnumerator<object[]> rows)
        {
            _tableName = tableName;
            _names = names;
            _types = types;
            _rows = rows;
        }

        // ── IDataReader ───────────────────────────────────────────────────

        /// <summary>Always 0 — JET tables are not hierarchical.</summary>
        public int Depth => 0;

        /// <inheritdoc />
        public bool IsClosed => _closed;

        /// <summary>Always -1 — this reader never modifies the database.</summary>
        public int RecordsAffected => -1;

        /// <summary>Advances to the next row. Returns false at end of table.</summary>
        public bool Read()
        {
            if (_closed) throw new InvalidOperationException("The data reader is closed.");

            if (_rows.MoveNext())
            {
                _current = _rows.Current;
                return true;
            }

            _current = null;
            return false;
        }

        /// <summary>Always false — a JET table read produces a single result set.</summary>
        public bool NextResult() => false;

        /// <inheritdoc />
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _current = null;
            _rows.Dispose();
        }

        /// <inheritdoc />
        public void Dispose() => Close();

        /// <summary>Describes the result columns, for consumers such as <see cref="DataTable.Load(IDataReader)"/>.</summary>
        public DataTable GetSchemaTable()
        {
            var schema = new DataTable("SchemaTable");
            schema.Columns.Add("ColumnName", typeof(string));
            schema.Columns.Add("ColumnOrdinal", typeof(int));
            schema.Columns.Add("ColumnSize", typeof(int));
            schema.Columns.Add("DataType", typeof(Type));
            schema.Columns.Add("AllowDBNull", typeof(bool));
            schema.Columns.Add("IsKey", typeof(bool));
            schema.Columns.Add("IsUnique", typeof(bool));
            schema.Columns.Add("IsReadOnly", typeof(bool));
            schema.Columns.Add("BaseTableName", typeof(string));
            schema.Columns.Add("BaseColumnName", typeof(string));

            for (int i = 0; i < _names.Length; i++)
            {
                schema.Rows.Add(_names[i], i, -1, _types[i], true, false, false, true, _tableName, _names[i]);
            }

            return schema;
        }

        // ── IDataRecord ───────────────────────────────────────────────────

        /// <inheritdoc />
        public int FieldCount => _names.Length;

        /// <inheritdoc />
        public object this[int i] => GetValue(i);

        /// <inheritdoc />
        public object this[string name] => GetValue(GetOrdinal(name));

        /// <inheritdoc />
        public string GetName(int i) => _names[i];

        /// <inheritdoc />
        public Type GetFieldType(int i) => _types[i];

        /// <inheritdoc />
        public string GetDataTypeName(int i) => _types[i].Name;

        /// <inheritdoc />
        public int GetOrdinal(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            if (_ordinals == null)
            {
                _ordinals = new Dictionary<string, int>(_names.Length, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _names.Length; i++)
                    if (!_ordinals.ContainsKey(_names[i])) _ordinals[_names[i]] = i;
            }

            if (_ordinals.TryGetValue(name, out int ordinal)) return ordinal;
            throw new IndexOutOfRangeException($"Column '{name}' does not exist in this result.");
        }

        /// <inheritdoc />
        public object GetValue(int i)
        {
            if (_current == null)
                throw new InvalidOperationException("No current row. Call Read() first.");
            return _current[i];
        }

        /// <inheritdoc />
        public int GetValues(object[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (_current == null)
                throw new InvalidOperationException("No current row. Call Read() first.");

            int n = Math.Min(values.Length, _current.Length);
            Array.Copy(_current, values, n);
            return n;
        }

        /// <inheritdoc />
        public bool IsDBNull(int i) => GetValue(i) is DBNull;

        /// <inheritdoc />
        public bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public byte GetByte(int i) => Convert.ToByte(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public short GetInt16(int i) => Convert.ToInt16(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public int GetInt32(int i) => Convert.ToInt32(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public long GetInt64(int i) => Convert.ToInt64(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public float GetFloat(int i) => Convert.ToSingle(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public double GetDouble(int i) => Convert.ToDouble(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public decimal GetDecimal(int i) => Convert.ToDecimal(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public DateTime GetDateTime(int i) => Convert.ToDateTime(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public string GetString(int i) => Convert.ToString(GetValue(i), CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public char GetChar(int i)
        {
            string s = GetString(i);
            if (string.IsNullOrEmpty(s))
                throw new InvalidCastException($"Column {i} is empty and cannot be read as a char.");
            return s[0];
        }

        /// <inheritdoc />
        public Guid GetGuid(int i)
        {
            object v = GetValue(i);
            if (v is Guid g) return g;
            return Guid.Parse(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        /// <inheritdoc />
        public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length)
        {
            if (!(GetValue(i) is byte[] source))
                throw new InvalidCastException($"Column {i} is not a byte array.");

            if (buffer == null) return source.LongLength;

            long available = source.LongLength - fieldOffset;
            if (available <= 0) return 0;

            int n = (int)Math.Min(length, Math.Min(available, buffer.Length - bufferoffset));
            Array.Copy(source, fieldOffset, buffer, bufferoffset, n);
            return n;
        }

        /// <inheritdoc />
        public long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length)
        {
            string source = GetString(i) ?? string.Empty;
            if (buffer == null) return source.Length;

            long available = source.Length - fieldoffset;
            if (available <= 0) return 0;

            int n = (int)Math.Min(length, Math.Min(available, buffer.Length - bufferoffset));
            source.CopyTo((int)fieldoffset, buffer, bufferoffset, n);
            return n;
        }

        /// <summary>Not supported — JET tables have no nested result sets.</summary>
        public IDataReader GetData(int i) =>
            throw new NotSupportedException("Nested data readers are not supported.");
    }
}
