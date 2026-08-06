# Changelog

All notable changes to `JetDatabaseReader` are documented here.
This project follows [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

### ⚡ Performance: page→table index

Every read method used to walk the **entire file** to find the pages belonging to its table,
re-testing each page's owning TDEF. Reading N tables therefore cost N whole-file scans, and
reading one small table out of a large database read the whole database.

`GetUserTables()` already scans every page once to locate the `MSysObjects` catalog, so it now
records each data page's owning TDEF while it is there — the index is built for free. All eight
read paths (`ReadTable` ×3, `ReadTableAsStrings`, `ReadTableAsStringDataTable`, `ReadFirstTable`,
`StreamRows`, `StreamRowsAsStrings`, `GetRealRowCount`) then visit only their own pages.

Measured with `JetDatabaseReader.Benchmarks` (median of 7 warm runs, back-to-back A/B):

| Database | Operation | Before | After | Speedup |
|----------|-----------|--------|-------|---------|
| Northwind (11 MB, 23 tables) | `ReadAllTables` | 318.8 ms | 18.3 ms | **17.5×** |
| Northwind | `GetRealRowCount` (warm) | 12.8 ms | ~0 ms | **>1000×** |
| Northwind | `StreamRows` (warm) | 14.1 ms | 0.6 ms | **23.7×** |
| Northwind | `Open` + `ReadTable` | 27.6 ms | 14.3 ms | 1.9× |
| AdventureWorks (1 MB, 3 tables) | `ReadAllTables` | 16.9 ms | 10.9 ms | 1.6× |
| 77 MB, 1 table | any | — | — | ~1.0× (neutral) |

Allocations for `ReadAllTables` on Northwind drop from 290 MB to 14 MB (**−95%**), because pages
belonging to other tables are no longer read and copied.

Single-table databases are unchanged, as expected: one table owns nearly every page, so there is
nothing to skip. The index costs one `long` per data page (~4 MB for a 2 GB database) and adds
+0.8% to the catalog scan's allocations.

**Behaviour is unchanged.** The index stores the result of the exact same predicate the read loops
applied (`page[0] == 0x01 && Ri32(page, tdefOff) == tdefPage`), in the same ascending page order,
so row content and row order are identical — verified across 4 real databases × 6 operations, all
returning byte-identical results, plus the full 365-test suite. The loops still re-verify each
page after reading it, and pages appended by another process (the default `FileShare.ReadWrite`
allows it) are picked up by an incremental tail scan.

One visible side effect: `IProgress<int>` callbacks now fire once per *table* page instead of once
per *file* page, so there are far fewer of them. The reported row counts are unchanged.

### ⚡ Performance: constant-memory reading

Follow-up work aimed at hosts with little RAM (small services, Azure App Service). Measured
against v2.2.0 with `JetDatabaseReader.Benchmarks`, median of 5 warm runs, back-to-back A/B:

| Database | Operation | Time | Allocations | Retained |
|----------|-----------|------|-------------|----------|
| Northwind (11 MB, 23 tables) | `ReadAllTables` | 335 ms → 16 ms (**20.5×**) | 291 MB → 2 MB (−99.3%) | — |
| Northwind | `StreamRows` | 15.4 ms → 0.2 ms (**70.9×**) | −99.3% | — |
| 77 MB, 228 K rows | `StreamRows` | 683 ms → 300 ms (**2.3×**) | 346 MB → 167 MB (−51.7%) | 256 B |
| 77 MB | `ListTables` | 115 ms → 78 ms (1.5×) | 79 MB → 80 KB (**−99.9%**) | — |
| 77 MB | `GetRealRowCount` | 77 ms → 66 ms | 79 MB → ~0 (**−100%**) | — |
| 77 MB | idle reader resident | — | −99.9% | 1.0 MB → 23 KB (**−97.8%**) |
| 40 MB | `ReadTable` | 919 ms → 456 ms (**2.0×**) | −45.3% | — |

Every scenario improved on time and allocations. Row counts are byte-identical across 28
scenarios on four real databases, and the suite is green at 383 tests.

**Reusable page buffer.** `ReadPage` allocated a fresh 4 KB array per page, so allocations scaled
with file size rather than with the data requested — counting rows in a 77 MB database produced
79 MB of garbage. Scans now fill one page-sized buffer per operation. Scanned pages no longer
enter the LRU cache either: a front-to-back scan touches each page once, so caching them only
evicted the LVAL and TDEF pages that do get reused.

**Typed decoding without the string round-trip.** The typed path formatted every cell as a string
and parsed it back. Values are now built straight from the row bytes. Besides the allocations,
this fixes two silent precision losses — see *Behaviour changes* below.

**Column projection.** `StreamRows`, `StreamRowsAsStrings`, `ReadTable`, and
`ReadTableAsStringDataTable` gained an overload taking column names, and the fluent API gained
`Query(t).Select("A", "B")`. Unselected columns are never decoded; for MEMO and OLE columns their
LVAL pages are never even read.

**`IDataReader` cursor.** `CreateDataReader(table, columns?)` returns an `AccessDataReader` that
holds one row at a time, for `SqlBulkCopy.WriteToServer`, `DataTable.Load`, or a streaming
exporter. `ReadTable` on the 77 MB database retains 165 MB by construction; the cursor retains
kilobytes.

**LVAL and OLE.** Multi-page memo chains were assembled into a `List<byte[]>` and then copied
again, peaking at twice the memo's size; they now fill a single correctly sized buffer. New
`AccessReaderOptions.OleObjectMode.Placeholder` skips OLE payloads entirely — no LVAL page reads
and no base64 string, which otherwise costs the blob plus about 1.33× its size.

**Run-length page index.** The index introduced above stored one entry per page. Table pages are
allocated in extents, so it now stores runs: the 2 GB database's 524 288 pages collapse to 333
runs, and a reader costs 81 KB to keep open.

**Faster page decoding.** Per-page row-boundary work dropped a four-stage LINQ chain and an
O(rows²) probe for each row's end offset, in favour of one reusable scratch and a binary search.
`DataTable` loads are wrapped in `BeginLoadData`/`EndLoadData`.

### 🐛 Concurrency fix: torn reads across threads

`ReadPage` did `_fs.Seek(...)` followed by `_fs.Read(...)` — two calls against one shared file
position. Two threads using the same reader interleaved them, and each got bytes belonging to the
other's page. It surfaced as **wrong data, not an exception**: a table would come back with 298
rows instead of 295, or 110 instead of 128, or a valid TDEF would fail to parse.

Caching one open reader and serving concurrent requests from it is the obvious pattern under IIS
or App Service, so the Seek+Read pair is now atomic. The regression tests fail without the lock
and pass with it. An uncontended lock costs far less than the read it guards; benchmark timings
and allocations are unchanged.

This also makes the following safe on a single shared reader: independent operations from many
threads, several readers over the same file, and several processes reading the same file. A single
`IEnumerable` or `AccessDataReader` still belongs to one thread. See the README's
*Concurrency & hosting* section.

### ✨ New: `Refresh()`

The catalog, page index, and page cache were read once and never re-validated, so a long-lived
reader kept serving stale data after another process rewrote the database. `Refresh()` drops all
three. Appended pages were already picked up automatically; this covers pages rewritten in place.

### 🛡️ Robustness

- **Corrupt row counts** — the row-count field on a data page is 16 bits, so a corrupt page could
  claim 65 535 rows and send the offset table reading past the end of the page buffer. Now clamped
  to what fits, in row enumeration and in both LVAL readers.
- **Corrupt memo lengths** — the LVAL chain reader sized its buffer from the memo header, a 3-byte
  field. A corrupt row claiming 16 MB allocated 16 MB even when the chain held one page. It now
  starts at 64 KB and grows towards the declared length.
- **Currency scale preserved** — building the decimal with an explicit scale of 4 rather than
  dividing by `10000.0m` keeps the trailing zeros the old string round-trip produced (`1.0000`,
  not `1`). Same numeric value, but the scale is visible through `ToString()` and in a grid.
- **Dead code removed** — `TypedValueParser` had no remaining callers after typed decoding stopped
  going through strings.

### 🐛 Overflow rows were skipped — five Northwind tables were invisible

A row-offset entry with bit `0x4000` set is not a row: it is a pointer to the page and row that
actually hold the data, encoded like an LVAL pointer (`page << 8 | rowIndex`). These entries were
skipped, so those rows were silently dropped.

It mattered most in `MSysObjects`. Forty of NorthwindTraders' catalog rows are overflow rows, and
following them takes the database from **23 to 28 user tables** — the ones that were invisible are
`Employees`, `Orders`, `Products`, `PurchaseOrderStatus`, and `Welcome`.

The pointer format was confirmed against real bytes before implementing: in all 18 overflow
entries across the test databases, the target resolves to a page whose owning TDEF matches the
source page's. One wrinkle the raw dump exposed — the target row's own offset entry carries the
`0x8000` bit, which on an ordinary data page means "deleted". On an overflow target it does not;
the row is live and only reachable through the pointer, so only the position bits are read there.

`GetRealRowCount` now shares the same row enumeration as the read paths, so it can no longer
disagree with `StreamRows` about which rows exist.

### ⚡ Performance: I/O and parse caching

- **`FileStream` buffering** — reads are one page at a seeked offset, and with the default 4 KB
  buffer `FileStream` bypassed buffering entirely, issuing one syscall per page. The buffer is now
  64 KB (`AccessReaderOptions.FileBufferSize`), so a front-to-back scan serves most pages from
  memory; `FileOptions.SequentialScan` also asks the OS to read ahead. Catalog scans got **3.7×–4.8×**
  faster, `GetRealRowCount` about **2×**, `ReadAllTables` on Northwind **2.5×**. Cost: 64 KB per
  open reader.
- **`TableDef` cache** — the TDEF page chain was re-read and re-parsed on every read, every
  `GetTableStats`, and every `GetStatistics`. Now parsed once per table and dropped by `Refresh()`.
- **`DecompressJet4`** — builds the string in one exactly-bounded `char[]` instead of appending to
  a `StringBuilder` one character at a time.

### 🐛 `ReadFirstTable` returned an empty schema

`FirstTableResult.Schema` was always `new List<TableColumn>()`, so callers got headers and rows but
never column types. It is now populated from the TDEF like every other read path.

### ⚠️ Behaviour changes

- **`DateTime` keeps sub-second precision.** The typed path used to render dates as
  `"yyyy-MM-dd HH:mm:ss"` and re-parse them, truncating milliseconds. Values now come from the
  OLE Automation date directly. Code comparing typed `DateTime`s for exact equality against
  second-truncated values will see a difference.
- **`float`/`double` round-trip exactly.** These went through `ToString("G")`, which is lossy on
  .NET Framework.
- **`IAccessReader` gained members** (`GetColumnNames`, the projection overloads,
  `CreateDataReader`). Breaking only for code that implements the interface itself, such as a
  hand-written test double.
- `IProgress<int>` callbacks fire per table page rather than per file page, so there are far
  fewer of them. Reported row counts are unchanged.

### 📝 Documentation fixes

- The README showed `FileShare.Read` as the default; it is and always was `FileShare.ReadWrite`.
- `ParallelPageReadsEnabled` is documented as having no effect. It is settable on the options and
  the reader, but nothing reads it. Kept for binary compatibility.

### 🧪 New: `JetDatabaseReader.Benchmarks`

Stopwatch harness for before/after comparison, reporting time, allocations, peak heap, and
retained memory. Writes TSV so runs can be diffed:

```
dotnet run -c Release -- --out baseline.tsv
dotnet run -c Release -- --out after.tsv --compare baseline.tsv
dotnet run -c Release -- --diag --huge     # per-database index and resident footprint
```

### 🔧 Fixes

- **Flaky progress test** — `ReadTable_WithProgress_ReportsIncreasingRowCounts` collected
  `Progress<int>` callbacks into a `List<int>` and enumerated it while thread-pool callbacks could
  still be adding to it. Now uses a `ConcurrentQueue<int>` and asserts over a snapshot.
- **Flaky memory test** — `StreamRows_Matrix_DoesNotExceedReasonableMemory` compared
  `GC.GetTotalMemory` before and after a whole enumeration. That counter is process-wide and xUnit
  runs test classes in parallel, so the delta also counted memory held by other tests reading the
  same 2 GB file. It now measures heap *growth* between two points inside one enumeration, which
  is the invariant it was written to check and is unaffected by concurrent tests.

---

## [2.2.0] — 2026-04-01

### ⚠️ Breaking Changes

| Area | Before | After |
|------|--------|-------|
| **`TableResult.Rows`** | `List<List<string>>` — string rows | `List<object[]>` — typed CLR rows |
| **`ReadTable(string, int)`** | Returned string rows in `Rows` | Now returns typed rows in `Rows` |
| **`ReadTable(string, int, bool)`** | Bool flag selecting typed vs string mode | **Removed** — use `ReadTable` (typed) or `ReadTableAsStrings` (strings) |
| **`ReadTableAsync(string, int, bool)`** | Async counterpart of removed overload | **Removed** |
| **`FirstTableResult`** | Extended `TableResult` | Now extends `StringTableResult` |

### ✨ New Type: `StringTableResult`

Dedicated result class for string-mode reads, returned by `ReadTableAsStrings`. Mirrors `TableResult` but `Rows` is `List<List<string>>`.

| Property | Type | Description |
|----------|------|-------------|
| `Headers` | `List<string>` | Column names |
| `Rows` | `List<List<string>>` | String rows |
| `Schema` | `List<TableColumn>` | Per-column schema |
| `TableName` | `string` | Source table name |
| `RowCount` | `int` | Computed row count |
| `ToDataTable()` | `DataTable` | All columns `typeof(string)` |

### ✨ New Methods

| Method | Returns | Description |
|--------|---------|-------------|
| `ReadTableAsStrings(string, int)` | `StringTableResult` | Sampled string-mode read |
| `ReadTableAsStringsAsync(string, int)` | `Task<StringTableResult>` | Async variant |
| `TableResult.ToDataTable()` | `DataTable` | Convert typed result — column types from `Schema` |
| `StringTableResult.ToDataTable()` | `DataTable` | Convert string result — all columns `typeof(string)` |

### 🔧 Improvements & Bug Fixes

- **ACCDB encryption false positive fixed** — The Jet4 encryption flag at offset `0x62` is now only checked for `ver == 1` (Access 2000–2003 `.mdb`). Access 2007+ format databases (`ver >= 2`) set unrelated bits at that offset and were incorrectly rejected with `NotSupportedException`. All `.accdb` files and `.mdb` files saved in Access 2007–2019 format are now readable.
- **Model files split** — `ColumnSizeUnit`, `ColumnSize`, and `TableColumn` each have their own `.cs` file.

### 📦 Migration Guide

```csharp
// ── ReadTable — Rows is now typed ─────────────────────────────────────
// Before (v2.1): string rows in Rows
TableResult r = reader.ReadTable("Orders", 10);
string val = r.Rows[0][2];                 // List<List<string>>

// After (v2.2): typed rows in Rows
TableResult r = reader.ReadTable("Orders", 10);
object val = r.Rows[0][2];                 // List<object[]>

// ── ReadTableAsStrings — dedicated string API ─────────────────────────
StringTableResult sr = reader.ReadTableAsStrings("Orders", 10);
string val = sr.Rows[0][2];                // List<List<string>>

// ── bool overload removed ─────────────────────────────────────────────
// Before ❌
TableResult r = reader.ReadTable("Orders", 10, typedValues: true);
// After ✅
TableResult        r  = reader.ReadTable("Orders", 10);
StringTableResult  sr = reader.ReadTableAsStrings("Orders", 10);

// ── ToDataTable ───────────────────────────────────────────────────────
DataTable typed   = reader.ReadTable("Orders", 100).ToDataTable();
DataTable strings = reader.ReadTableAsStrings("Orders", 100).ToDataTable();
```

---

## [2.1.0] — 2026-03-30

### ⚠️ Breaking Changes

| Area | Before | After |
|------|--------|-------|
| **`TableColumn.TypeName`** | `string` — e.g. `"Long Integer"` | **Removed** — replaced by `Type` (`System.Type`) |
| **`TableColumn.SizeDesc`** | `string` — e.g. `"4 bytes"` | **Removed** — replaced by `Size` (`ColumnSize` struct) |
| **`ReadFirstTable()`** | Returned `TableResult` | Now returns `FirstTableResult` (subclass of `TableResult`) |
| **`GetTableStats()`** | Returned `List<(string Name, long RowCount, int ColumnCount)>` | Now returns `List<TableStat>` |

### ✨ New Types

| Type | Description |
|------|-------------|
| `ColumnSizeUnit` | Enum: `Bits`, `Bytes`, `Chars`, `Variable`, `Lval` |
| `ColumnSize` | Readonly struct — `Value` (`int?`) + `Unit` (`ColumnSizeUnit`). Factory methods: `FromBits`, `FromBytes`, `FromChars`. Sentinels: `Variable`, `Lval`. `ToString()` produces a human-readable description. |
| `FirstTableResult` | Extends `TableResult` with `TableCount` (`int`) — the total number of user tables in the database. Returned by `ReadFirstTable()`. |
| `TableStat` | Named class with `Name` (`string`), `RowCount` (`long`), `ColumnCount` (`int`). Returned as element of `List<TableStat>` from `GetTableStats()`. |

### 🔧 Improvements

- **`TableResult`** gains `TableName` (`string`) — the table this result was read from — and `RowCount` (`int`) computed property.
- **`TableColumn.Type`** (`System.Type`) — exact CLR type, consistent with `ColumnMetadata.ClrType`.
- **`TableColumn.Size`** (`ColumnSize`) — structured size with programmatic access to numeric value and unit; `ToString()` preserves the previous human-readable output.

### 📦 Migration Guide

```csharp
// ── TableColumn schema properties ────────────────────────────────────
// Before
string typeName = col.TypeName;   // "Long Integer"
string sizeDesc = col.SizeDesc;   // "4 bytes"
// After
Type   clrType  = col.Type;                    // typeof(int)
int?   bytes    = col.Size.Value;              // 4
string display  = col.Size.ToString();         // "4 bytes"
bool   isVar    = col.Size.Unit == ColumnSizeUnit.Variable;

// ── ReadFirstTable ────────────────────────────────────────────────────
// Before
TableResult      r = reader.ReadFirstTable();
// After
FirstTableResult r = reader.ReadFirstTable();
int total = r.TableCount;   // new property on FirstTableResult

// ── GetTableStats ─────────────────────────────────────────────────────
// Before — tuple list
foreach (var (name, rows, cols) in reader.GetTableStats()) { ... }
// After — named class
foreach (TableStat s in reader.GetTableStats())
    Console.WriteLine($"{s.Name}: {s.RowCount} rows, {s.ColumnCount} cols");
```

---

## [2.0.1] — 2026-03-29

### ⚠️ Breaking Changes

| Before | After | Notes |
|--------|-------|-------|
| `TablePreviewResult` | `TableResult` | Renamed for clarity — remove the `Preview` prefix |
| `TablePreviewColumn` | `TableColumn` | Renamed for clarity — remove the `Preview` prefix |

### 📦 Migration Guide

```csharp
// Before
TablePreviewResult p = r.ReadTable("Orders", 10);
foreach (TablePreviewColumn col in p.Schema)
    Console.WriteLine($"{col.Name}: {col.TypeName} ({col.SizeDesc})");

// After
TableResult p = r.ReadTable("Orders", 10);
foreach (TableColumn col in p.Schema)
    Console.WriteLine($"{col.Name}: {col.TypeName} ({col.SizeDesc})");
```

---

## [2.0.0] — 2026-03-28

### ⚠️ Breaking Changes

| Area | v1 behaviour | v2 behaviour |
|------|-------------|-------------|
| **Constructor** | `new JetDatabaseReader(path)` | `AccessReader.Open(path)` — factory method required |
| **`ReadTable()`** | Returned `(headers, rows, schema)` tuple | Now an overload — `ReadTable(string, int)` returns `TableResult` |
| **`ReadTableAsDataTable()`** | Returned `DataTable` with `string` columns | **Renamed** to `ReadTableAsStringDataTable()` |
| **`StreamRows()`** | Returned `IEnumerable<string[]>` | Now returns `IEnumerable<object[]>` with native CLR types |
| **`ReadAllTables()`** | Returned `DataTable` with `string` columns | Now returns `DataTable` with typed CLR columns |
| **`ReadAllTablesAsync()`** | Same string behaviour | Now returns typed CLR columns |

### ✨ New Methods

| Method | Description |
|--------|-------------|
| `ReadTable()` | Primary read method — typed `DataTable` (replaces `ReadTableAsDataTableTyped`) |
| `ReadTable(string, int)` | Sampled-rows overload — returns `TablePreviewResult` (headers, rows, schema) |
| `ReadTableAsync()` | Async typed `DataTable` |
| `ReadTableAsync(string, int)` | Async sampled-rows overload — returns `Task<TablePreviewResult>` |
| `StreamRowsAsStrings()` | Compatibility streaming — `IEnumerable<string[]>` |
| `ReadAllTablesAsStrings()` | Bulk read with string columns |
| `ReadAllTablesAsStringsAsync()` | Async bulk read with string columns |
| `TableQuery.Where(Func<object[], bool>)` | Typed row predicate |
| `TableQuery.WhereAsStrings(Func<string[], bool>)` | String row predicate |
| `TableQuery.Execute()` | Returns `IEnumerable<object[]>` |
| `TableQuery.ExecuteAsStrings()` | Returns `IEnumerable<string[]>` |
| `TableQuery.FirstOrDefault()` | Returns first `object[]` or null |
| `TableQuery.FirstOrDefaultAsStrings()` | Returns first `string[]` or null |
| `TableQuery.Count()` / `CountAsStrings()` | Count per chain |
| `GetColumnMetadata()` | Rich per-column metadata with CLR type |
| `GetStatistics()` / `GetStatisticsAsync()` | Database-level statistics + cache hit rate |
| `TablePreviewResult` | Result type for sampled-rows overload — `Headers`, `Rows`, `Schema` |
| `TablePreviewColumn` | Schema entry — `Name`, `TypeName` (`string`), `SizeDesc` (`string`) |

### 🔧 Improvements

- **`FileShare` default changed to `FileShare.Read`** — other processes may read but not write while the database is open; pass `FileShare.ReadWrite` explicitly when Microsoft Access has the file open
- LRU page cache (256-page default, ~1 MB for Jet4 pages)
- Parallel page reads option (`ParallelPageReadsEnabled`)
- `AccessReaderOptions` configuration object (`PageCacheSize`, `FileAccess`, `FileShare`, `ValidateOnOpen`)
- `DatabaseStatistics` and `ColumnMetadata` types
- `IAccessReader` interface — fully testable and mockable
- Full XML documentation on all public members

### 📦 Migration Guide

```csharp
// ── Open ────────────────────────────────────────────────────────────
// v1
using var r = new JetDatabaseReader("db.mdb");
// v2
using var r = AccessReader.Open("db.mdb");

// ── Read typed DataTable ─────────────────────────────────────────────
// v1 — no equivalent (all columns were strings)
// v2
DataTable dt = r.ReadTable("Orders");
int id = (int)dt.Rows[0]["OrderID"];

// ── Read string DataTable (compatibility) ────────────────────────────
// v1
DataTable dt = r.ReadTableAsDataTable("Orders");
// v2
DataTable dt = r.ReadTableAsStringDataTable("Orders");

// ── Sample with schema ──────────────────────────────────────────────
// v1
var (h, rows, schema) = r.ReadTable("Orders", maxRows: 10);
// v2
TablePreviewResult p = r.ReadTable("Orders", 10);
// p.Headers / p.Rows / p.Schema[i].Name, .TypeName, .SizeDesc

// ── Stream rows (typed) ──────────────────────────────────────────────
// v1
foreach (string[] row in r.StreamRows("Orders")) { ... }
// v2 — typed
foreach (object[] row in r.StreamRows("Orders")) { int id = (int)row[0]; }
// v2 — compat
foreach (string[] row in r.StreamRowsAsStrings("Orders")) { ... }

// ── Bulk read ────────────────────────────────────────────────────────
// v1 — returned string columns
var tables = r.ReadAllTables();
// v2 — returns typed columns
var tables = r.ReadAllTables();
// v2 — compat strings
var tables = r.ReadAllTablesAsStrings();
```

---

## [1.0.0] — 2026-03-27

- Pure-managed JET3/Jet4 reader (no OleDB/ODBC/ACE)
- All standard column types (Text, Integer, Currency, GUID, MEMO, OLE)
- Multi-page LVAL chain support
- OLE Object magic-byte detection (JPEG, PNG, PDF, ZIP, DOC, RTF)
- Compressed Unicode (Jet4) decoding
- Code page auto-detection (non-Western text)
- Encryption detection (`NotSupportedException`)
- Streaming API (`StreamRows`)
- `IProgress<int>` callbacks
- 256-page LRU cache
