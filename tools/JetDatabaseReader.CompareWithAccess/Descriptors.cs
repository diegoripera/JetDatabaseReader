using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using JetDatabaseReader;

// Reads the parsed column descriptors out of a live reader via reflection. The question is
// narrow: does a GUID column's FixedOff say what it should, or is the reader looking in the
// wrong place?
internal static class Descriptors
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(string db, params string[] tables)
    {
        using var reader = AccessReader.Open(db);
        Console.WriteLine($"── {System.IO.Path.GetFileName(db)}");

        // Force the catalog to load, then walk the tdef cache.
        foreach (string table in tables)
        {
            reader.GetColumnMetadata(table);

            // Resolve the table's TDEF page through the catalog and ask the reader for that exact
            // definition. Searching the tdef cache by name does not work — TableDef carries no
            // name, and the "first entry" fallback silently hands back MSysObjects.
            object catalog = typeof(AccessReader).GetField("_catalogCache", Any).GetValue(reader);
            long page = -1;
            foreach (object entry in (IEnumerable)catalog)
            {
                Type et = entry.GetType();
                object nm = Get(et, entry, "Name");
                if (nm is string s && string.Equals(s, table, StringComparison.OrdinalIgnoreCase))
                {
                    page = Convert.ToInt64(Get(et, entry, "TDefPage"));
                    break;
                }
            }
            if (page < 0) { Console.WriteLine($"   {table}: not in catalog"); continue; }

            object td = typeof(AccessReader)
                .GetMethod("ReadTableDef", Any)
                .Invoke(reader, new object[] { page });
            Console.WriteLine($"   (tdef page {page})");

            Console.WriteLine($"   {table}");
            var colsProp = td.GetType().GetField("Columns", Any) ?? (MemberInfo)null as FieldInfo;
            object colsObj = colsProp != null
                ? ((FieldInfo)colsProp).GetValue(td)
                : td.GetType().GetProperty("Columns", Any).GetValue(td);

            foreach (object col in (IEnumerable)colsObj)
            {
                Type t = col.GetType();
                string name = (string)Get(t, col, "Name");
                byte type = (byte)Get(t, col, "Type");
                int colNum = (int)Get(t, col, "ColNum");
                int varIdx = (int)Get(t, col, "VarIdx");
                int fixedOff = (int)Get(t, col, "FixedOff");
                int size = (int)Get(t, col, "Size");
                byte flags = (byte)Get(t, col, "Flags");
                bool isFixed = (bool)t.GetProperty("IsFixed", Any).GetValue(col);

                byte ext = (byte)Get(t, col, "ExtFlags");
                byte scale = (byte)Get(t, col, "Scale");
                Console.WriteLine($"      {name,-22} type=0x{type:X2} colNum={colNum,2} fixed={isFixed,-5} " +
                                  $"fixedOff={fixedOff,3} varIdx={varIdx,3} size={size,3} flags=0x{flags:X2} " +
                                  $"ext=0x{ext:X2} scale={scale}");
            }
        }
    }

    private static object Get(Type t, object o, string field)
    {
        FieldInfo f = t.GetField(field, Any);
        if (f != null) return f.GetValue(o);
        return t.GetProperty(field, Any).GetValue(o);
    }

    private static object FindTableDef(IDictionary cache, string table)
    {
        foreach (DictionaryEntry e in cache)
        {
            object td = e.Value;
            object nm = td.GetType().GetField("Name", Any)?.GetValue(td)
                     ?? td.GetType().GetProperty("Name", Any)?.GetValue(td);
            if (nm is string s && string.Equals(s, table, StringComparison.OrdinalIgnoreCase)) return td;
        }
        // Fall back to the single entry when the def carries no name
        foreach (DictionaryEntry e in cache) return e.Value;
        return null;
    }
}
