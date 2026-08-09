using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using JetDatabaseReader;

// The reader yields more rows than Access for one table. Are the extras duplicates of rows that
// are already there, or rows Access does not have at all?
internal static class Extra
{
    public static void Run(string db, string table)
    {
        var mine = new Dictionary<long, int>();
        long mineTotal = 0;

        using (var reader = AccessReader.Open(db))
        {
            var meta = reader.GetColumnMetadata(table);
            foreach (object[] r in reader.StreamRows(table))
            {
                mineTotal++;
                long h = Hash(r);
                mine.TryGetValue(h, out int n);
                mine[h] = n + 1;
            }
            Console.WriteLine($"reader: {mineTotal:N0} rows, {mine.Count:N0} distinct");
        }

        var theirs = new Dictionary<long, int>();
        long theirTotal = 0;
        using (var cn = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db};"))
        {
            cn.Open();
            using var cmd = new OleDbCommand($"SELECT * FROM [{table}]", cn);
            cmd.CommandTimeout = 0;
            using var rd = cmd.ExecuteReader();
            var buf = new object[rd.FieldCount];
            while (rd.Read())
            {
                theirTotal++;
                rd.GetValues(buf);
                long h = Hash(buf);
                theirs.TryGetValue(h, out int n);
                theirs[h] = n + 1;
            }
        }
        Console.WriteLine($"ACE:    {theirTotal:N0} rows, {theirs.Count:N0} distinct");

        long onlyMineKinds = 0, onlyMineRows = 0, dupExcess = 0;
        foreach (var kv in mine)
        {
            if (!theirs.TryGetValue(kv.Key, out int t)) { onlyMineKinds++; onlyMineRows += kv.Value; }
            else if (kv.Value > t) dupExcess += kv.Value - t;
        }
        long onlyTheirsRows = theirs.Where(kv => !mine.ContainsKey(kv.Key)).Sum(kv => (long)kv.Value);

        Console.WriteLine();
        Console.WriteLine($"rows the reader has and ACE does not : {onlyMineRows:N0} ({onlyMineKinds:N0} distinct)");
        Console.WriteLine($"extra copies of rows both have       : {dupExcess:N0}");
        Console.WriteLine($"rows ACE has and the reader does not : {onlyTheirsRows:N0}");
    }

    private static long Hash(object[] row)
    {
        unchecked
        {
            long h = unchecked((long)14695981039346656037UL);
            foreach (object v in row)
            {
                string s = v == null || v == DBNull.Value ? "\0"
                         : v is byte[] b ? "b" + b.Length
                         : v is double d ? d.ToString("R", CultureInfo.InvariantCulture)
                         : v is DateTime t ? t.Ticks.ToString(CultureInfo.InvariantCulture)
                         : Convert.ToString(v, CultureInfo.InvariantCulture);
                foreach (char c in s) { h ^= c; h *= 1099511628211; }
                h ^= '|'; h *= 1099511628211;
            }
            return h;
        }
    }
}
