using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetDatabaseReader;

// Probe, not an implementation: does the TDEF really carry a usable used_pages pointer at the
// offset the format documents, and does the map it names account for exactly the pages Access
// considers the table's? Answering that before writing any of it.
internal static class UsageMap
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(string db, string table)
    {
        using var reader = AccessReader.Open(db);
        Type T = typeof(AccessReader);

        bool jet4 = (bool)T.GetField("_jet4", Any).GetValue(reader);
        int pgSz = (int)T.GetField("_pgSz", Any).GetValue(reader);
        int dpTDefOff = (int)T.GetField("_dpTDefOff", Any).GetValue(reader);

        object entry = T.GetMethod("GetCatalogEntry", Any).Invoke(reader, new object[] { table });
        long tdefPage = Convert.ToInt64(entry.GetType().GetField("TDefPage", Any).GetValue(entry));

        byte[] td = (byte[])T.GetMethod("ReadTDefBytes", Any).Invoke(reader, new object[] { tdefPage });
        Console.WriteLine($"── {System.IO.Path.GetFileName(db)} / {table}  tdef page {tdefPage}, tdef bytes {td.Length}, jet4={jet4}");

        int usedOff = jet4 ? 55 : 35;
        uint usedDp = BitConverter.ToUInt32(td, usedOff);
        Console.WriteLine($"   used_pages @0x{usedOff:X2} = 0x{usedDp:X8}  → page {usedDp >> 8}, row {usedDp & 0xFF}");

        byte[] mapRow = ReadRow(reader, T, pgSz, usedDp);
        if (mapRow == null) { Console.WriteLine("   could not read the map row"); return; }

        Console.WriteLine($"   map row: {mapRow.Length} bytes, type byte 0x{mapRow[0]:X2}");
        Console.WriteLine($"   first bytes: {string.Join(" ", mapRow.Take(24).Select(b => b.ToString("X2")))}");

        var pages = new SortedSet<long>();
        if (mapRow[0] == 0x00)
        {
            long startPage = BitConverter.ToUInt32(mapRow, 1);
            Console.WriteLine($"   inline map, start page {startPage}, {(mapRow.Length - 5) * 8} bits");
            AddBits(pages, mapRow, 5, mapRow.Length - 5, startPage);
        }
        else if (mapRow[0] == 0x01)
        {
            int entries = (mapRow.Length - 1) / 4;
            Console.WriteLine($"   reference map, {entries} page pointers");
            int bitsPerPage = (pgSz - 4) * 8;
            for (int i = 0; i < entries; i++)
            {
                uint mp = BitConverter.ToUInt32(mapRow, 1 + i * 4);
                if (mp == 0) continue;
                byte[] mapPage = ReadPage(reader, T, (long)mp);
                if (mapPage == null) continue;
                AddBits(pages, mapPage, 4, pgSz - 4, (long)i * bitsPerPage);
            }
        }
        else Console.WriteLine("   unknown map type");

        Console.WriteLine($"   pages named by the map: {pages.Count:N0}");

        // How many of those actually carry rows for this table, and how many pages does the sweep
        // find that the map does not?
        var sweep = new SortedSet<long>();
        var pagesEnum = (IEnumerable)T.GetMethod("EnumerateTablePages", Any).Invoke(reader, new object[] { tdefPage });
        byte[] scan = (byte[])T.GetMethod("NewScanBuffer", Any).Invoke(reader, null);
        MethodInfo readScan = T.GetMethod("ReadPageForScan", Any);
        foreach (object po in pagesEnum)
        {
            long p = Convert.ToInt64(po);
            byte[] pg = (byte[])readScan.Invoke(reader, new object[] { p, scan });
            if (pg[0] != 0x01) continue;
            if (BitConverter.ToInt32(pg, dpTDefOff) != tdefPage) continue;
            sweep.Add(p);
        }

        Console.WriteLine($"   data pages the sweep accepts: {sweep.Count:N0}");
        Console.WriteLine($"   in sweep but not in map     : {sweep.Except(pages).Count():N0}");
        Console.WriteLine($"   in map but not in sweep     : {pages.Except(sweep).Count():N0}");

        // The decisive number: how many rows survive if only the map's pages are read?
        Type scannerType = typeof(AccessReader).Assembly.GetType("JetDatabaseReader.RowScanner");
        object scanner = Activator.CreateInstance(scannerType, nonPublic: true);
        MethodInfo enumSpans = T.GetMethod("EnumerateRowSpans", Any);

        long rowsInMap = 0, rowsSweptOnly = 0;
        foreach (long p in sweep)
        {
            byte[] pg = (byte[])readScan.Invoke(reader, new object[] { p, scan });
            long n = 0;
            foreach (object _ in (IEnumerable)enumSpans.Invoke(reader, new[] { pg, scanner })) n++;
            if (pages.Contains(p)) rowsInMap += n; else rowsSweptOnly += n;
        }

        Console.WriteLine($"   rows on map pages           : {rowsInMap:N0}");
        Console.WriteLine($"   rows only the sweep finds   : {rowsSweptOnly:N0}");
    }

    private static void AddBits(SortedSet<long> into, byte[] b, int offset, int len, long firstPage)
    {
        for (int i = 0; i < len; i++)
        {
            byte v = b[offset + i];
            if (v == 0) continue;
            for (int bit = 0; bit < 8; bit++)
                if ((v & (1 << bit)) != 0) into.Add(firstPage + (long)i * 8 + bit);
        }
    }

    private static byte[] ReadPage(AccessReader reader, Type T, long page)
    {
        try { return (byte[])T.GetMethod("ReadPageCached", Any).Invoke(reader, new object[] { page }); }
        catch { return null; }
    }

    /// <summary>Resolves a (page &lt;&lt; 8 | row) pointer to that row's bytes.</summary>
    private static byte[] ReadRow(AccessReader reader, Type T, int pgSz, uint dp)
    {
        long page = dp >> 8;
        int rowIdx = (int)(dp & 0xFF);
        byte[] pg = ReadPage(reader, T, page);
        if (pg == null || pg[0] != 0x01) return null;

        int rowsStart = (int)T.GetField("_dpRowsStart", Any).GetValue(reader);
        int numRows = (int)T.GetMethod("PageRowCount", Any).Invoke(reader, new object[] { pg });
        if (rowIdx >= numRows) return null;

        int rawOff = BitConverter.ToUInt16(pg, rowsStart + rowIdx * 2);
        int rowStart = rawOff & 0x1FFF;
        int rowEnd = pgSz - 1;
        for (int r = 0; r < numRows; r++)
        {
            int ofs = BitConverter.ToUInt16(pg, rowsStart + r * 2) & 0x1FFF;
            if (ofs > rowStart && ofs < rowEnd) rowEnd = ofs - 1;
        }
        int size = rowEnd - rowStart + 1;
        if (size <= 0) return null;
        var data = new byte[size];
        Buffer.BlockCopy(pg, rowStart, data, 0, size);
        return data;
    }
}
