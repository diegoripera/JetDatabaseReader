using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using JetDatabaseReader;

// Pairs rows by their first column so a memo can be compared against the same row on both sides,
// then reports length and the first character position that differs.
internal static class Memo
{
    public static void Run(string db, string table, string key, string column)
    {
        using var reader = AccessReader.Open(db);
        var meta = reader.GetColumnMetadata(table);
        int k = meta.FindIndex(m => m.Name == key);
        int c = meta.FindIndex(m => m.Name == column);

        var mine = new Dictionary<string, string>();
        foreach (object[] r in reader.StreamRows(table))
            mine[Convert.ToString(r[k])] = r[c] == DBNull.Value ? null : Convert.ToString(r[c]);

        using var cn = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db};");
        cn.Open();
        using var cmd = new OleDbCommand($"SELECT [{key}], [{column}] FROM [{table}]", cn);
        using var rd = cmd.ExecuteReader();

        int shown = 0;
        while (rd.Read())
        {
            string id = Convert.ToString(rd.GetValue(0));
            string theirs = rd.IsDBNull(1) ? null : Convert.ToString(rd.GetValue(1));
            mine.TryGetValue(id, out string ours);

            if (ours == theirs) continue;
            if (shown++ >= 4) break;

            Console.WriteLine($"   {key}={id}  readerLen={ours?.Length.ToString() ?? "null"}  aceLen={theirs?.Length.ToString() ?? "null"}");
            if (ours != null && theirs != null)
            {
                int i = 0;
                while (i < ours.Length && i < theirs.Length && ours[i] == theirs[i]) i++;
                Console.WriteLine($"      first difference at {i}");
                Console.WriteLine($"      reader …{Esc(Slice(ours, i - 20, 40))}");
                Console.WriteLine($"      ACE    …{Esc(Slice(theirs, i - 20, 40))}");
                // Is the reader's value simply the tail of ACE's?
                if (theirs.Length > ours.Length && theirs.EndsWith(ours, StringComparison.Ordinal))
                    Console.WriteLine($"      → reader lost the first {theirs.Length - ours.Length} characters");

                // Where else do they diverge? Walk both, resyncing on the longest common run.
                var gaps = new List<string>();
                int ai = 0, bi = 0;
                while (ai < ours.Length && bi < theirs.Length && gaps.Count < 6)
                {
                    if (ours[ai] == theirs[bi]) { ai++; bi++; continue; }
                    int skip = 1;
                    while (skip < 8 && bi + skip < theirs.Length && theirs[bi + skip] != ours[ai]) skip++;
                    gaps.Add($"at reader[{ai}] ACE has {skip} extra: '{Esc(Slice(theirs, bi, skip))}' " +
                             $"(context '{Esc(Slice(theirs, bi - 8, 8))}')");
                    bi += skip;
                }
                foreach (string g in gaps) Console.WriteLine("      " + g);
            }
        }
        if (shown == 0) Console.WriteLine("   no differences");
    }

    private static string Slice(string s, int from, int len)
    {
        if (from < 0) from = 0;
        if (from >= s.Length) return "";
        return s.Substring(from, Math.Min(len, s.Length - from));
    }

    private static string Esc(string s) =>
        string.Concat(s.Select(ch => ch < 32 || ch > 126 ? $"\\u{(int)ch:X4}" : ch.ToString()));
}
