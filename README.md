# JetDatabaseReader

[![NuGet](https://img.shields.io/nuget/v/JetDatabaseReader.svg)](https://www.nuget.org/packages/JetDatabaseReader/)
[![Downloads](https://img.shields.io/nuget/dt/JetDatabaseReader.svg)](https://www.nuget.org/packages/JetDatabaseReader/)
[![.NET Standard](https://img.shields.io/badge/.NET%20Standard-2.0-blue)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Pure-managed .NET library for reading Microsoft Access JET databases — no OleDB, ODBC, or ACE/Jet driver installation required.

> **v2.0** introduced typed DataTables and typed streaming by default. See [CHANGELOG.md](CHANGELOG.md) and the [migration guide](#migration-from-v1) for breaking changes.

---

## Features

| | |
|---|---|
| ✅ **No native dependencies** | Pure C# — runs anywhere .NET runs |
| ✅ **Jet3 & Jet4 / ACE** | Access 97 through Access 2019 (`.mdb` / `.accdb`) |
| ✅ **Typed by default** | `int`, `DateTime`, `decimal`, `Guid` — not just strings |
| ✅ **All column types** | Text, Integer, Currency, Date/Time, GUID, MEMO, OLE Object, Decimal |
| ✅ **Streaming API** | Process millions of rows without loading the whole file |
| ✅ **Async support** | Full `Task<T>`-based async for all major operations |
| ✅ **Page cache** | 256-page LRU cache (~1 MB, configurable) |
| ✅ **Fluent query** | `Query().Where().Take().Execute()` — typed and string chains |
| ✅ **Progress reporting** | `IProgress<int>` callbacks on all long operations |
| ✅ **Non-Western text** | Code page auto-detected from the database header |
| ✅ **OLE Objects** | Detects embedded JPEG, PNG, PDF, ZIP, DOC, RTF |

---

## Installation

```bash
dotnet add package JetDatabaseReader
```

```powershell
Install-Package JetDatabaseReader
```

### NuGet target compatibility

`JetDatabaseReader` targets **`netstandard2.0`**, which is consumed by every current .NET surface:

| Consumer | Minimum version |
|----------|----------------|
| .NET Framework | 4.6.1 |
| .NET Core | 2.0 |
| .NET | 5 / 6 / 7 / 8 / 9 |
| Mono / Xamarin | All |
| Unity | 2018.1+ |
| UWP | 10.0.16299+ |

---

## Quick Start

```csharp
using JetDatabaseReader;

using var reader = AccessReader.Open("database.mdb");

List<string> tables = reader.ListTables();
Console.WriteLine($"Found {tables.Count} tables: {string.Join(", ", tables)}");

DataTable dt = reader.ReadTable("Orders");
foreach (DataRow row in dt.Rows)
{
    int     id   = (int)row["OrderID"];
    var     date = (DateTime)row["OrderDate"];
    decimal amt  = (decimal)row["Freight"];
    Console.WriteLine($"#{id}  {date:yyyy-MM-dd}  {amt:C}");
}
```

---

## Reading Data

### Typed DataTable — recommended

```csharp
DataTable dt = reader.ReadTable("Products");
// dt.Columns["ProductID"].DataType    == typeof(int)
// dt.Columns["UnitPrice"].DataType    == typeof(decimal)
// dt.Columns["Discontinued"].DataType == typeof(bool)
```

### String DataTable — compatibility

```csharp
DataTable dt = reader.ReadTableAsStringDataTable("Products");
// every column is typeof(string)
```

### Table preview with schema

```csharp
TablePreviewResult preview = reader.ReadTable("Products", maxRows: 20);
foreach (TablePreviewColumn col in preview.Schema)
    Console.WriteLine($"{col.Name}: {col.TypeName} ({col.SizeDesc})");
```

---

## Streaming Large Tables

### Typed streaming — recommended

```csharp
var progress = new Progress<int>(n => Console.Write($"\r{n:N0} rows"));

foreach (object[] row in reader.StreamRows("BigTable", progress))
{
    int     id  = (int)row[0];
    decimal val = row[2] == DBNull.Value ? 0m : (decimal)row[2];
}
```

### String streaming — compatibility

```csharp
foreach (string[] row in reader.StreamRowsAsStrings("BigTable"))
    Console.WriteLine(string.Join(", ", row));
```

Null values in typed rows surface as `DBNull.Value`.

---

## Fluent Query API

```csharp
// Typed chain
object[] order = reader.Query("Orders")
    .Where(row => row[2] is DateTime d && d.Year == 2024)
    .Take(10)
    .FirstOrDefault();

int count = reader.Query("OrderDetails")
    .Where(row => row[3] is decimal p && p > 100m)
    .Count();

// String chain
IEnumerable<string[]> recent = reader.Query("Orders")
    .WhereAsStrings(row => row[2].StartsWith("2024"))
    .Take(50)
    .ExecuteAsStrings();
```

---

## Async Operations

```csharp
List<string>                  tables = await reader.ListTablesAsync();
DataTable                     dt     = await reader.ReadTableAsync("Orders");
DatabaseStatistics            stats  = await reader.GetStatisticsAsync();
Dictionary<string, DataTable> all    = await reader.ReadAllTablesAsync();
Dictionary<string, DataTable> allStr = await reader.ReadAllTablesAsStringsAsync();
```

---

## Bulk Operations

```csharp
// Typed columns
Dictionary<string, DataTable> all = reader.ReadAllTables(
    new Progress<string>(t => Console.WriteLine($"Reading {t}...")));

// String columns (compatibility)
Dictionary<string, DataTable> allStr = reader.ReadAllTablesAsStrings();
```

---

## Statistics & Metadata

```csharp
foreach (ColumnMetadata col in reader.GetColumnMetadata("Orders"))
    Console.WriteLine($"{col.Ordinal}. {col.Name} — {col.TypeName} ({col.ClrType.Name})");

DatabaseStatistics s = reader.GetStatistics();
Console.WriteLine($"Version:   {s.Version}");
Console.WriteLine($"Size:      {s.DatabaseSizeBytes / 1024 / 1024} MB");
Console.WriteLine($"Tables:    {s.TableCount}  Rows: {s.TotalRows:N0}");
Console.WriteLine($"Cache hit: {s.PageCacheHitRate}%");
```

---

## Configuration

```csharp
var options = new AccessReaderOptions
{
    PageCacheSize            = 512,    // pages in LRU cache (default: 256)
    ParallelPageReadsEnabled = true,   // parallel I/O (default: false)
    DiagnosticsEnabled       = false,  // verbose logging (default: false)
    ValidateOnOpen           = true,   // format check on open (default: true)
    FileAccess               = FileAccess.Read,   // default
    FileShare                = FileShare.Read,    // default: others may read, writes blocked
    // FileShare             = FileShare.ReadWrite // use when Access has the file open
};
using var reader = AccessReader.Open("database.mdb", options);
```

---

## Error Handling

```csharp
try { var dt = await reader.ReadTableAsync("Orders"); }
catch (FileNotFoundException)   { /* file missing */ }
catch (NotSupportedException)   { /* encrypted / password-protected */ }
catch (InvalidDataException)    { /* corrupt or non-JET file */ }
catch (JetLimitationException)  { /* deleted-column gap, numeric overflow */ }
catch (ObjectDisposedException) { /* reader already disposed */ }
```

---

## Limitations

| | |
|---|---|
| ❌ Encrypted databases | Remove password in Access (File › Info › Encrypt with Password) |
| ❌ Attachment fields (0x11) | Rare type added in Access 2007 |
| ❌ Linked tables | Only local tables are listed |
| ❌ Overflow rows | Rows spanning multiple pages are skipped |
| ❌ Write operations | Read-only library |

---

## Migration from v1

```csharp
// Open
var r = new JetDatabaseReader("db.mdb");              // v1 ❌
var r = AccessReader.Open("db.mdb");                   // v2 ✅

// Typed DataTable
var dt = r.ReadTableAsDataTable("Orders");             // v1 — string columns
var dt = r.ReadTable("Orders");                        // v2 ✅ typed
var dt = r.ReadTableAsStringDataTable("Orders");       // v2 compat

// Preview
var t = r.ReadTable("T", maxRows: 10);                 // v1 ❌ — returned a tuple
TablePreviewResult t = r.ReadTable("T", 10);           // v2 ✅ typed result

// Streaming
foreach (string[] row in r.StreamRows("T"))            // v1
foreach (object[] row in r.StreamRows("T"))            // v2 ✅ typed
foreach (string[] row in r.StreamRowsAsStrings("T"))   // v2 compat

// Bulk
var all = r.ReadAllTables();                           // v1 — string cols / v2 ✅ typed
var all = r.ReadAllTablesAsStrings();                  // v2 compat
```

Full details in [CHANGELOG.md](CHANGELOG.md).

---

## How It Works

Based on the [mdbtools format specification](https://github.com/mdbtools/mdbtools/blob/master/HACKING.md). The library parses JET pages directly:

1. **Page 0** — header: Jet3/Jet4 detection, code page, encryption flag
2. **Page 2** — `MSysObjects` catalog: table names → TDEF page numbers
3. **TDEF pages** — table definition chains: column descriptors + names
4. **Data pages** — row slot arrays → null mask + fixed/variable fields
5. **LVAL pages** — long-value chains for MEMO and OLE fields

---

## Contributing

Issues and pull requests welcome at [github.com/diegoripera/JetDatabaseReader](https://github.com/diegoripera/JetDatabaseReader).

## License

MIT — see [LICENSE](LICENSE) for details.
