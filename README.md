# JetDatabaseReader

[![NuGet](https://img.shields.io/nuget/v/JetDatabaseReader.svg)](https://www.nuget.org/packages/JetDatabaseReader/)
[![Downloads](https://img.shields.io/nuget/dt/JetDatabaseReader.svg)](https://www.nuget.org/packages/JetDatabaseReader/)
[![.NET Standard](https://img.shields.io/badge/.NET%20Standard-2.0-blue)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Pure-managed .NET library for reading Microsoft Access JET databases — no OleDB, ODBC, or ACE/Jet driver installation required.

> **v2.0** introduced typed DataTables and typed streaming by default. **v2.1** adds structured schema types (`ColumnSize`, `TableStat`, `FirstTableResult`). **v2.2** cleans up the `TableResult` API (`ReadTableAsStrings`, `ToDataTable`, ACCDB encryption fix). See [CHANGELOG.md](CHANGELOG.md) and the [migration guide](#migration-from-v1) for breaking changes.

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

### Table preview with schema — typed

```csharp
TableResult preview = reader.ReadTable("Products", maxRows: 20);
foreach (TableColumn col in preview.Schema)
{
    Type   clrType = col.Type;            // e.g. typeof(int), typeof(string)
    string display = col.Size.ToString(); // e.g. "4 bytes", "255 chars", "LVAL"
    Console.WriteLine($"{col.Name}: {clrType.Name} ({col.Size})");
}

// Convert to DataTable with CLR-typed columns
DataTable dt = preview.ToDataTable();
// dt.Columns["UnitPrice"].DataType == typeof(decimal)
```

### Table preview with schema — strings

```csharp
StringTableResult preview = reader.ReadTableAsStrings("Products", maxRows: 20);
string firstCell = preview.Rows[0][0];  // always a string

// Convert to DataTable — all columns typeof(string)
DataTable dt = preview.ToDataTable();
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

### Column projection — read only what you need

Unselected columns are never decoded. For MEMO and OLE columns that also means their LVAL pages
are never read, so projecting away blob columns cuts both time and memory:

```csharp
foreach (object[] row in reader.StreamRows("BigTable", new[] { "Id", "Total" }, null))
{
    int     id    = (int)row[0];   // indexes follow the projection, not the table
    decimal total = (decimal)row[1];
}
```

Also available on `ReadTable`, `ReadTableAsStringDataTable`, and the fluent `Query(...).Select(...)`.

### Constant-memory export — `IDataReader`

`ReadTable` materialises the whole table: reading a 77 MB database costs about 165 MB of retained
heap. When you only need to move the data somewhere else, use the cursor — it holds one row at a
time, so memory stays flat no matter how large the table is:

```csharp
using var cursor = reader.CreateDataReader("BigTable");

using var bulk = new SqlBulkCopy(connectionString) { DestinationTableName = "dbo.BigTable" };
bulk.WriteToServer(cursor);          // streams; never materialises the table
```

It works anywhere `IDataReader` is accepted — `DataTable.Load(cursor)`, CSV writers, and so on.
Per the `IDataReader` contract, values are valid only until the next `Read()`.

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
TableResult                   typed  = await reader.ReadTableAsync("Orders", 50);
StringTableResult             str    = await reader.ReadTableAsStringsAsync("Orders", 50);
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

// Table-level stats (single catalog scan)
foreach (TableStat s in reader.GetTableStats())
    Console.WriteLine($"{s.Name}: {s.RowCount:N0} rows, {s.ColumnCount} cols");

// First table preview + total table count
FirstTableResult first = reader.ReadFirstTable();
Console.WriteLine($"First: {first.TableName} ({first.TableCount} tables total)");

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
    PageCacheSize      = 512,    // pages in LRU cache (default: 256)
    FileBufferSize     = 64*1024,// FileStream buffer (default: 65536)
    DiagnosticsEnabled = false,  // verbose logging (default: false)
    ValidateOnOpen     = true,   // format check on open (default: true)
    OleObjectMode      = OleObjectMode.Placeholder,  // skip OLE payloads (default: DataUri)
    FileAccess         = FileAccess.Read,       // default
    FileShare          = FileShare.ReadWrite,   // default: another app may hold the file open
};
using var reader = AccessReader.Open("database.mdb", options);
```

`OleObjectMode.Placeholder` makes OLE columns read as the literal `"(OLE)"` without decoding the
payload — the blob's LVAL pages are never read and no base64 string is built. Use it when scanning
a table whose attachments you do not need; `DataUri` (the default) returns a `data:` URI and costs
the blob plus a string about 1.33x its size.

> `ParallelPageReadsEnabled` exists on the options and the reader but currently has no effect —
> nothing reads it. It is kept for binary compatibility.

---

## Concurrency & hosting (IIS, Azure App Service)

### What is safe

| Scenario | Safe | Notes |
|----------|------|-------|
| One reader shared across threads, independent operations | ✅ | Reads against the shared file handle are serialised internally |
| Several readers in one process, same file | ✅ | Each owns its own file handle |
| Several **processes** reading the same file | ✅ | IIS web gardens, multiple App Service instances |
| Opening while Microsoft Access holds the file | ✅ | Default `FileShare.ReadWrite` |
| One `IEnumerable` from `StreamRows` enumerated by several threads | ❌ | Enumerate it on one thread, like any `IEnumerable` |
| One `AccessDataReader` used by several threads | ❌ | One cursor per thread — its row buffer is reused |

### Caching a reader

Opening a database scans the catalog once, so keeping a reader alive is worth it — and it is cheap:
a reader over a 2 GB database costs about **140 KB** resident, and about **85 KB** over a 77 MB one
(most of it the 64 KB `FileBufferSize`, which you can lower). Registering one per database as a
singleton and serving concurrent requests from it is a supported pattern.

```csharp
services.AddSingleton(_ => AccessReader.Open(@"D:\data\catalog.accdb"));
```

### Staleness

The catalog, page index, and page cache are read once and never re-validated. Pages **appended**
by another process are picked up automatically, but pages **rewritten in place** are not — a
long-lived reader would keep serving the old contents. Call `Refresh()` when you know the file
changed:

```csharp
reader.Refresh();   // drops catalog, page index, and page cache
```

If the database is rewritten frequently, prefer opening a reader per request over caching one.

### Memory

`ReadTable` and `ReadAllTables` materialise everything: a 77 MB database retains about 165 MB as a
`DataTable`. On a memory-constrained plan use `StreamRows` or `CreateDataReader`, which hold one
row at a time, and project away columns you do not need.

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
| ✅ Jet4 database password (`.mdb`) | Supply it via `AccessReaderOptions.Password` |
| ❌ ACE encryption (`.accdb`) | "Encrypt with Password" really encrypts the pages — decrypt in Access first |
| ❌ Attachment fields (0x11) | Rare type added in Access 2007 |
| ❌ Linked tables | Only local tables are listed |
| ❌ Write operations | Read-only library |

### Password-protected databases

The two kinds of protection are not the same thing:

```csharp
using var reader = AccessReader.Open("secured.mdb",
    new AccessReaderOptions { Password = "..." });

reader.IsPasswordProtected;   // true
```

A **Jet4 database password** (Access 2000–2003, `.mdb`) is access control, not encryption: Access
refuses to open the file, but the page bodies sit on disk in plain text. This library verifies the
password and then reads normally — it is not decrypting anything, and any tool reading the file
directly sees the same data. Treat such a file as unprotected at rest. Access also truncates these
passwords to 20 characters when they are set, so a longer one is compared on the same terms.

**ACE encryption** (Access 2007+, `.accdb`, "Encrypt with Password") genuinely encrypts the pages
and is not supported; opening one throws a `NotSupportedException` saying so.

> **Overflow rows are now supported.** A row-offset entry with bit `0x4000` is a pointer to the
> page and row actually holding the data; these used to be skipped. It mattered most in
> `MSysObjects` — 40 of NorthwindTraders' catalog rows are overflow rows, so `Employees`, `Orders`,
> `Products`, `PurchaseOrderStatus`, and `Welcome` were invisible to `ListTables()` entirely.

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

// Preview — typed rows (v2.2: Rows is now List<object[]>, was List<List<string>>)
TablePreviewResult t = r.ReadTable("T", 10);           // v2.0.0 ❌
TableResult        t = r.ReadTable("T", 10);           // v2.0.1–v2.1 ✅ (Rows was List<List<string>>)
TableResult        t = r.ReadTable("T", 10);           // v2.2 ✅ Rows is now List<object[]>

// Preview — string rows (v2.2: new dedicated API)
StringTableResult  s = r.ReadTableAsStrings("T", 10); // v2.2 ✅
string val = s.Rows[0][2];                             // always string

// bool overload removed (v2.2)
TableResult t = r.ReadTable("T", 10, typedValues: true);  // v2.1 ❌ removed
TableResult t = r.ReadTable("T", 10);                     // v2.2 ✅

// ToDataTable (v2.2: new on both result types)
DataTable dtTyped = r.ReadTable("T", 100).ToDataTable();         // CLR-typed columns
DataTable dtStr   = r.ReadTableAsStrings("T", 100).ToDataTable(); // string columns

// Schema properties (v2.0.1 → v2.1.0)
col.TypeName  // v2.0.1 ❌ — string e.g. "Long Integer"
col.Type      // v2.1.0 ✅ — System.Type e.g. typeof(int)
col.SizeDesc  // v2.0.1 ❌ — string e.g. "4 bytes"
col.Size      // v2.1.0 ✅ — ColumnSize struct (.Value, .Unit, .ToString())

// Table stats (v2.1.0)
foreach (var (n, r, c) in reader.GetTableStats())       // v2.0 ❌ tuple
foreach (TableStat s in reader.GetTableStats())         // v2.1 ✅ named type

// First table (v2.1.0 → v2.2.0: base class changed)
TableResult      r = reader.ReadFirstTable();           // v2.0 ❌
FirstTableResult r = reader.ReadFirstTable();           // v2.1+ ✅ + r.TableCount
// Note: FirstTableResult now extends StringTableResult (v2.2)

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
