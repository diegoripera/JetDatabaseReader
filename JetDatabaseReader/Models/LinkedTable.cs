namespace JetDatabaseReader
{
    /// <summary>Where a linked table's data actually lives.</summary>
    public enum LinkedTableKind
    {
        /// <summary>Unrecognised connection string.</summary>
        Unknown = 0,

        /// <summary>Another Access database. Its connection string is just <c>;DATABASE=path</c>.</summary>
        Access = 1,

        /// <summary>An ODBC data source — <c>ODBC;DSN=...</c> or a full driver connection string.</summary>
        Odbc = 2,

        /// <summary>An Excel workbook.</summary>
        Excel = 3,

        /// <summary>A delimited or fixed-width text file.</summary>
        Text = 4
    }

    /// <summary>
    /// A table that appears in this database but whose rows live somewhere else.
    ///
    /// Access stores the link, not the data: the catalog row carries a connection string and the
    /// name the table has in the source. Reading the rows means opening that source, which for an
    /// ODBC link would need a driver — the thing this library exists to avoid.
    /// </summary>
    public sealed class LinkedTable
    {
        /// <summary>Name the table has in this database.</summary>
        public string Name { get; set; }

        /// <summary>Name the table has in the source, which may differ from <see cref="Name"/>.</summary>
        public string ForeignName { get; set; }

        /// <summary>The raw connection string from the catalog's <c>Connect</c> column.</summary>
        public string ConnectionString { get; set; }

        /// <summary>What kind of source the connection string points at.</summary>
        public LinkedTableKind Kind { get; set; }

        /// <summary>
        /// File path parsed out of the connection string's <c>DATABASE=</c> clause, when there is
        /// one. Null for links that do not name a file, such as an ODBC DSN.
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// True when the source is another Access database that this library could open itself.
        /// </summary>
        public bool IsAccessDatabase => Kind == LinkedTableKind.Access && !string.IsNullOrEmpty(SourcePath);

        /// <inheritdoc />
        public override string ToString() =>
            $"{Name} -> {Kind}:{SourcePath ?? ConnectionString}" +
            (string.IsNullOrEmpty(ForeignName) || ForeignName == Name ? "" : $" [{ForeignName}]");
    }
}
