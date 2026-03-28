# Changelog

All notable changes to `JetDatabaseReader` are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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
| `ReadTable(string, int)` | Sampled-rows overload — returns `TableResult` (headers, rows, schema) |
| `ReadTableAsync()` | Async typed `DataTable` |
| `ReadTableAsync(string, int)` | Async sampled-rows overload — returns `Task<TableResult>` |
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
| `TableResult` | Result type for sampled-rows overload — `Headers`, `Rows`, `Schema` |
| `TableColumn` | Schema entry — `Name`, `TypeName`, `SizeDesc` |

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
TableResult p = r.ReadTable("Orders", 10);
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
