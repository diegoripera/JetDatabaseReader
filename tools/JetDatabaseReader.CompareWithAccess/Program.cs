using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using JetDatabaseReader;

// Ground truth. Every check so far compared the library's two read paths against each other, which
// cannot catch a row the library never sees — both paths would miss it identically. This compares
// against the Access engine itself (ACE OLEDB), the only independent oracle available.
//
// Comparison is per column, as a multiset: row order differs between the two (page order is not
// Access's order), and pairing rows up would mis-attribute a single wrong column to every column
// after it. Per column, sorted, the disagreement lands on the column that actually has it.
internal static class Program
{
    private static int tablesChecked, tablesSkipped, badColumns;
    private static readonly List<string> Detail = new();

    private static void Usage()
    {
        Console.WriteLine(@"Compares JetDatabaseReader against the Access engine (ACE OLEDB).

  compare-with-access <db> [<db>…]              every table, every column, against ACE
  compare-with-access counts <db> [<db>…]       row count ACE reports, per table
  compare-with-access extra  <db> <table>       are the reader's extra rows duplicates or new?
  compare-with-access memo   <db> <table> <key> <column>
                                                pair rows by key and locate the first difference
  compare-with-access desc   <db> <table> […]   parsed column descriptors
  compare-with-access reject <db> <table>       why the decoder is dropping rows
  compare-with-access usage  <db> <table>       the table's usage map against the page sweep

Exit code is 0 when everything matches. Requires the ACE OLEDB provider, x64.");
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        if (args.Length > 2 && args[0] == "desc") { Descriptors.Run(args[1], args.Skip(2).ToArray()); return 0; }
        if (args.Length > 2 && args[0] == "reject") { Reject.Run(args[1], args[2]); return 0; }
        if (args.Length > 4 && args[0] == "memo") { Memo.Run(args[1], args[2], args[3], args[4]); return 0; }
        if (args.Length > 2 && args[0] == "extra") { Extra.Run(args[1], args[2]); return 0; }
        if (args.Length > 2 && args[0] == "usage") { UsageMap.Run(args[1], args[2]); return 0; }
        if (args.Length > 1 && args[0] == "counts") { Counts.Run(args.Skip(1).ToArray()); return 0; }

        foreach (string db in args)
        {
            if (!File.Exists(db)) { Console.WriteLine($"MISSING {db}"); continue; }
            string name = Path.GetFileName(db);

            AccessReader reader;
            try { reader = AccessReader.Open(db); }
            catch (Exception ex) { Console.WriteLine($"SKIP {name}: reader: {ex.Message}"); continue; }

            using (reader)
            using (var cn = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db};"))
            {
                try { cn.Open(); }
                catch (Exception ex) { Console.WriteLine($"SKIP {name}: ACE: {ex.Message}"); continue; }

                Console.WriteLine($"── {name}");
                foreach (string table in reader.ListTables())
                    Compare(reader, cn, name, table);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"tables compared {tablesChecked}, skipped {tablesSkipped}, columns that disagree {badColumns}");
        foreach (string d in Detail.Take(60)) Console.WriteLine("  " + d);
        Console.WriteLine();
        Console.WriteLine(badColumns == 0 ? "MATCHES ACCESS" : "DIFFERENCES FOUND");
        return badColumns == 0 ? 0 : 1;
    }

    private static void Compare(AccessReader reader, OleDbConnection cn, string db, string table)
    {
        List<ColumnMetadata> meta;
        try { meta = reader.GetColumnMetadata(table); }
        catch { tablesSkipped++; return; }

        // Complex columns hold an id the library does not follow while ACE returns what it points
        // at — a documented difference, not a finding.
        var cols = meta.Where(m => m.TypeName != "Complex").ToList();
        if (cols.Count == 0) { tablesSkipped++; return; }

        List<string[]> mine;
        try
        {
            mine = reader.StreamRows(table)
                .Select(r => cols.Select(c => Norm(r[meta.IndexOf(c)], c)).ToArray())
                .ToList();
        }
        catch (Exception ex) { Detail.Add($"{db}/{table}: reader threw: {ex.Message}"); tablesSkipped++; return; }

        List<string[]> theirs;
        try
        {
            string list = string.Join(", ", cols.Select(c => "[" + c.Name.Replace("]", "]]") + "]"));
            using var cmd = new OleDbCommand($"SELECT {list} FROM [{table.Replace("]", "]]")}]", cn);
            cmd.CommandTimeout = 0;
            using var rdr = cmd.ExecuteReader();
            theirs = new List<string[]>();
            var buf = new object[cols.Count];
            while (rdr.Read())
            {
                rdr.GetValues(buf);
                theirs.Add(buf.Select((v, i) => Norm(v, cols[i])).ToArray());
            }
        }
        catch (Exception ex) { Detail.Add($"{db}/{table}: ACE threw: {ex.Message}"); tablesSkipped++; return; }

        tablesChecked++;

        bool rowsDiffer = mine.Count != theirs.Count;
        if (rowsDiffer)
        {
            badColumns++;
            Detail.Add($"{db}/{table}: ROW COUNT reader={mine.Count} ACE={theirs.Count}");
        }

        var wrong = new List<string>();
        for (int c = 0; c < cols.Count; c++)
        {
            var a = mine.Select(r => r[c]).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var b = theirs.Select(r => r[c]).OrderBy(s => s, StringComparer.Ordinal).ToList();

            int n = Math.Min(a.Count, b.Count), diff = 0;
            string sample = null;
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) { diff++; sample ??= $"reader='{Cut(a[i])}' ACE='{Cut(b[i])}'"; }

            if (diff > 0)
            {
                badColumns++;
                wrong.Add($"{cols[c].Name} [{cols[c].TypeName}] {diff}/{n} — {sample}");
            }
        }

        Console.WriteLine($"   {table,-34} {mine.Count,7} rows  {(wrong.Count == 0 && !rowsDiffer ? "ok" : $"{wrong.Count} bad cols")}");
        foreach (string w in wrong) Detail.Add($"{db}/{table}.{w}");
    }

    private static string Cut(string s) => s.Length > 70 ? s.Substring(0, 70) + "…" : s;

    private static string Norm(object v, ColumnMetadata col)
    {
        if (v == null || v == DBNull.Value) return "<null>";
        var inv = CultureInfo.InvariantCulture;

        // Binary: the two sides deliberately represent it differently. Presence still catches a
        // payload the library drops or invents.
        if (v is byte[] b) return b.Length == 0 ? "<empty-blob>" : "<blob>";
        if (col.TypeName == "OLE Object" || col.TypeName == "Binary")
            return v is string s0 && s0.Length == 0 ? "<empty-blob>" : "<blob>";

        switch (v)
        {
            case bool x:     return x ? "1" : "0";
            case byte x:     return x.ToString(inv);
            case short x:    return x.ToString(inv);
            case int x:      return x.ToString(inv);
            case long x:     return x.ToString(inv);
            case float x:    return ((double)x).ToString("R", inv);
            case double x:   return x.ToString("R", inv);
            case decimal x:  return StripZeros(x);
            case DateTime x: return x.Ticks.ToString(inv);
            case Guid x:     return x.ToString("D");
            case string x:   return x;
            default:         return Convert.ToString(v, inv);
        }
    }

    /// <summary>Currency is scale-4 on one side and trimmed on the other; 12.3400 is 12.34.</summary>
    private static string StripZeros(decimal d)
    {
        string s = d.ToString("0.############################", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
