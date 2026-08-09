using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetDatabaseReader;

// EnumerateRowSpans yields exactly the number of rows Access reports, and StreamRows yields far
// fewer, so rows are being dropped by CrackRow's early bail-outs. This replays those checks over
// the same spans and tallies which one fires.
internal static class Reject
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(string db, string table)
    {
        using var reader = AccessReader.Open(db);
        Type T = typeof(AccessReader);

        int numColsFldSz = (int)F(T, reader, "_numColsFldSz");
        int varLenFldSz  = (int)F(T, reader, "_varLenFldSz");
        int varEntrySz   = (int)F(T, reader, "_varEntrySz");
        int eodFldSz     = (int)F(T, reader, "_eodFldSz");
        bool jet4        = (bool)F(T, reader, "_jet4");
        int dpTDefOff    = (int)F(T, reader, "_dpTDefOff");

        object entry = T.GetMethod("GetCatalogEntry", Any).Invoke(reader, new object[] { table });
        long tdefPage = Convert.ToInt64(entry.GetType().GetField("TDefPage", Any).GetValue(entry));

        Type scannerType = typeof(AccessReader).Assembly.GetType("JetDatabaseReader.RowScanner");
        object scanner = Activator.CreateInstance(scannerType, nonPublic: true);
        byte[] scan = (byte[])T.GetMethod("NewScanBuffer", Any).Invoke(reader, null);

        var pages = (IEnumerable)T.GetMethod("EnumerateTablePages", Any).Invoke(reader, new object[] { tdefPage });
        MethodInfo readPage = T.GetMethod("ReadPageForScan", Any);
        MethodInfo enumSpans = T.GetMethod("EnumerateRowSpans", Any);

        var tally = new Dictionary<string, int>();
        int total = 0, oks = 0;
        var sizes = new List<int>();

        foreach (object po in pages)
        {
            long p = Convert.ToInt64(po);
            byte[] page = (byte[])readPage.Invoke(reader, new object[] { p, scan });
            if (page[0] != 0x01) continue;
            if (BitConverter.ToInt32(page, dpTDefOff) != tdefPage) continue;

            foreach (object span in (IEnumerable)enumSpans.Invoke(reader, new[] { page, scanner }))
            {
                Type st = span.GetType();
                byte[] sp = (byte[])st.GetField("Page", Any).GetValue(span);
                int rowStart = (int)st.GetField("Start", Any).GetValue(span);
                int rowSize = (int)st.GetField("Size", Any).GetValue(span);
                total++;

                string why = Why(sp, rowStart, rowSize, jet4, numColsFldSz, varLenFldSz, varEntrySz, eodFldSz);
                bool resolved = !ReferenceEquals(sp, page);   // came from an overflow pointer
                string key = why == "ok" ? $"ok (size {rowSize}{(resolved ? ", overflow" : "")})"
                                         : $"{why} (size {rowSize}{(resolved ? ", overflow" : "")})";
                tally.TryGetValue(key, out int n);
                tally[key] = n + 1;

                if (why != "ok" && sizes.Count < 3)
                {
                    sizes.Add(rowSize);
                    Console.WriteLine($"   rejected bytes @{rowStart}: " +
                        string.Join(" ", Enumerable.Range(0, Math.Min(rowSize, 40)).Select(k => sp[rowStart + k].ToString("X2"))));
                }
                if (why == "ok" && oks < 3)
                {
                    oks++;
                    Console.WriteLine($"   ok       bytes @{rowStart}: " +
                        string.Join(" ", Enumerable.Range(0, Math.Min(rowSize, 40)).Select(k => sp[rowStart + k].ToString("X2"))));
                }
            }
        }

        Console.WriteLine($"── {System.IO.Path.GetFileName(db)} / {table}: {total} spans");
        foreach (var kv in tally.OrderByDescending(k => k.Value))
            Console.WriteLine($"   {kv.Key,-28} {kv.Value}");
        if (sizes.Count > 0) Console.WriteLine($"   rejected row sizes: {string.Join(", ", sizes)}");
    }

    private static string Why(byte[] page, int rowStart, int rowSize, bool jet4,
                              int numColsFldSz, int varLenFldSz, int varEntrySz, int eodFldSz)
    {
        int numCols = jet4 ? BitConverter.ToUInt16(page, rowStart) : page[rowStart];
        if (numCols == 0) return "numCols == 0";

        int nullMaskSz = (numCols + 7) / 8;
        int nullMaskPos = rowSize - nullMaskSz;
        if (nullMaskPos < numColsFldSz) return $"nullMaskPos<hdr (numCols={numCols})";

        int varLenPos = nullMaskPos - varLenFldSz;
        if (varLenPos < numColsFldSz) return "varLenPos<hdr";

        int varLen = jet4 ? BitConverter.ToUInt16(page, rowStart + varLenPos) : page[rowStart + varLenPos];
        int jumpSz = jet4 ? 0 : (rowSize / 256);
        int varTableStart = varLenPos - jumpSz - varLen * varEntrySz;
        int eodPos = varTableStart - eodFldSz;
        if (eodPos < numColsFldSz) return $"eodPos<hdr (varLen={varLen})";

        return "ok";
    }

    private static object F(Type t, object o, string name) => t.GetField(name, Any).GetValue(o);
}
