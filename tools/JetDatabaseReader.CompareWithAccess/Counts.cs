using System;
using System.Data.OleDb;
using System.IO;
using JetDatabaseReader;

// Emits the row count ACE reports for every table of the committed fixtures, as C# InlineData.
internal static class Counts
{
    public static void Run(params string[] dbs)
    {
        foreach (string db in dbs)
        {
            using var reader = AccessReader.Open(db);
            using var cn = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db};");
            cn.Open();

            foreach (string table in reader.ListTables())
            {
                using var cmd = new OleDbCommand($"SELECT COUNT(*) FROM [{table.Replace("]", "]]")}]", cn);
                object n = cmd.ExecuteScalar();
                Console.WriteLine($"        [InlineData(\"{Path.GetFileName(db)}\", \"{table}\", {n})]");
            }
        }
    }
}
