# JetDatabaseReader.Benchmarks

Stopwatch harness for measuring JetDatabaseReader performance changes.

## Running

```bash
# Capture a baseline before making a change
dotnet run -c Release -- --out baseline.tsv

# ...make the change, then compare
dotnet run -c Release -- --out after.tsv --compare baseline.tsv
```

| Option | Description |
|--------|-------------|
| `--out <file>` | Write results as TSV for later comparison |
| `--compare <file>` | Print a speedup table against a previous `--out` file |
| `--db <path>` | Benchmark only this database (repeatable) |
| `--iterations <n>` | Iteration count (default 5) |
| `--huge` | Include the 2 GB database — slow |

## Databases

Picked up automatically when present:

- `AdventureLT2008.mdb` (1 MB, 3 tables) and `NorthwindTraders.accdb` (11 MB, 23 tables), from the
  test project's output directory — build `JetDatabaseReader.Tests` first so they are copied there.
- Anything named by the `JETDATABASEREADER_TEST_DBS` environment variable: a directory, or a list
  of paths separated by the platform path separator. Files over a gigabyte are skipped unless
  `--huge` is passed. Pass `--db` for a one-off.

Large real-world databases are worth configuring. The repository fixtures are small enough that a
whole-file scan looks free on them, which is exactly the class of regression that matters most.

Multi-table databases are the interesting case for anything touching page scanning — a
single-table database gives every table the whole file, which hides that class of regression.

## Reading the results

Each scenario reports what it actually did (row/table counts) in the `Note` column. Diffing that
column between two runs is a cheap equivalence check: a performance change that alters row counts
is a bug, not a speedup.

**Always run A/B back-to-back on a quiet machine.** All runs are warm — a warmup pass pulls the
file into the OS page cache first — so the numbers measure CPU and syscall cost rather than
cold-disk seek time. That is the conservative direction: on a cold cache, reading pages you do not
need is strictly more expensive. Numbers taken minutes apart, or right after other heavy I/O, drift
by 30–60% and will invent regressions that do not exist.
