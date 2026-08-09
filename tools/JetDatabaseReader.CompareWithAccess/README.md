# compare-with-access

Reads every table of a database twice — once through JetDatabaseReader, once through the Access
engine (ACE OLEDB) — and reports every place they disagree.

## Why this exists

The test suite compares the library's typed path against its own string path. That is a real check,
but it cannot catch a row the library never sees, or a field it decodes consistently wrongly: both
paths fail together and agree perfectly while doing it. Seven defects lived through hundreds of
passing tests for exactly that reason, including a table that silently lost 39% of its rows and a
`Decimal` column that returned a 24-digit integer for `0.99`.

Access is the only independent oracle available, and every one of those defects showed up the first
time it was consulted. Keep using it when touching the decoder.

## Requirements

The **ACE OLEDB provider**, x64 — the same one Access itself installs. Available standalone as the
Microsoft Access Database Engine Redistributable. Without it every database reports
`SKIP … ACE: provider is not registered`, which is the tool telling you it cannot do its job, not
that the library is fine.

This is why it is a tool and not a test: a test that silently passes on machines without the
provider is worse than no test.

## Use

```
dotnet run --project tools/JetDatabaseReader.CompareWithAccess -- <db> [<db>…]
```

Exit code 0 means every table matched on row count and on every cell. Anything else prints what
differed and where.

The remaining subcommands are for narrowing a difference down once the sweep has found one:

| | |
|---|---|
| `counts <db>…` | the row count ACE reports per table, as C# `InlineData` |
| `extra <db> <table>` | are the reader's extra rows duplicates, or rows Access does not have? |
| `memo <db> <table> <key> <column>` | pair rows by key, find the first differing character |
| `desc <db> <table>…` | the parsed column descriptors |
| `reject <db> <table>` | which of the decoder's bail-outs is dropping rows, and how many |
| `usage <db> <table>` | the table's usage map against what a page sweep would accept |

## Reading the output

Columns are compared as **multisets**, not row by row: page order is not Access's order, and pairing
rows up would blame one wrong column for every column after it. The cost is that a single wrong
value shifts the sorted lists and inflates the count of differing positions — 6 bad values can
report as 12. Use `memo` or `extra` to get the true figure once a column is implicated.
