using System;

namespace JetDatabaseReader
{
    /// <summary>
    /// Makes sense of the <c>Connect</c> string Access stores for a linked table.
    ///
    /// The shape is a semicolon-separated list whose first element names the provider, empty for a
    /// link to another Access database:
    ///
    ///   <c>;DATABASE=C:\data\other.accdb</c>
    ///   <c>Excel 12.0 Xml;HDR=YES;IMEX=2;DATABASE=C:\data\book.xlsx</c>
    ///   <c>Text;FMT=Delimited;HDR=YES;DATABASE=C:\data</c>
    ///   <c>ODBC;DSN=Sales;UID=app;DATABASE=Sales</c>
    /// </summary>
    internal static class LinkedTableParser
    {
        public static LinkedTable Parse(string name, string foreignName, string connect, bool odbcType)
        {
            var table = new LinkedTable
            {
                Name = name,
                ForeignName = string.IsNullOrEmpty(foreignName) ? name : foreignName,
                ConnectionString = connect ?? string.Empty,
                Kind = LinkedTableKind.Unknown
            };

            string provider = ProviderPrefix(table.ConnectionString);

            if (odbcType || provider.StartsWith("ODBC", StringComparison.OrdinalIgnoreCase))
                table.Kind = LinkedTableKind.Odbc;
            else if (provider.Length == 0)
                table.Kind = LinkedTableKind.Access;
            else if (provider.StartsWith("Excel", StringComparison.OrdinalIgnoreCase))
                table.Kind = LinkedTableKind.Excel;
            else if (provider.StartsWith("Text", StringComparison.OrdinalIgnoreCase))
                table.Kind = LinkedTableKind.Text;

            // For ODBC, DATABASE= names a database on the server rather than a file, so exposing it
            // as a path would invite callers to try opening something that is not there.
            if (table.Kind != LinkedTableKind.Odbc)
                table.SourcePath = Clause(table.ConnectionString, "DATABASE=");

            return table;
        }

        /// <summary>Everything before the first semicolon — empty for an Access link.</summary>
        private static string ProviderPrefix(string connect)
        {
            if (string.IsNullOrEmpty(connect)) return string.Empty;

            int semi = connect.IndexOf(';');
            return (semi < 0 ? connect : connect.Substring(0, semi)).Trim();
        }

        /// <summary>Value of a <c>KEY=</c> clause, or null when absent.</summary>
        private static string Clause(string connect, string key)
        {
            if (string.IsNullOrEmpty(connect)) return null;

            int at = connect.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            int start = at + key.Length;
            int end = connect.IndexOf(';', start);
            string value = (end < 0 ? connect.Substring(start) : connect.Substring(start, end - start)).Trim();

            return value.Length == 0 ? null : value;
        }
    }
}
