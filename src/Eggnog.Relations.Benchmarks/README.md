# Eggnog.Relations.Benchmarks

BenchmarkDotNet ports of egglog's divan benchmarks for the relational engine, so the C# engine can be measured against
the Rust it reimplements on the same workloads; and the root benchmark set (`Root/`), which records the root engine's
performance before colored contexts change it (see [The root benchmark set](#the-root-benchmark-set)).

## Running

This is a plain console app. BenchmarkDotNet is used as a library with its in-process toolchain: every benchmark runs
inside this process, against the engine assembly the app was built with. Nothing generates, builds or launches a
separate project per benchmark, so MSBuild is involved only when you build the app itself.

Build in Release and run the executable, from the repository root:

```
dotnet build -c Release src/Eggnog.Relations.Benchmarks
src/Eggnog.Relations.Benchmarks/bin/Release/net10.0/Eggnog.Relations.Benchmarks.exe --filter *WritesBenchmarks*
```

With no arguments it runs every benchmark. `dotnet run -c Release --project src/Eggnog.Relations.Benchmarks -- <args>`
does the same build and run in one step.

Narrow the run with a filter on `Namespace.Class.Method`, for example `--filter *WritesBenchmarks*` or
`--filter *UnionFindConcurrentBenchmarks.Concurrent`. Other useful BenchmarkDotNet options:

- `--list flat` lists the benchmarks without running them.
- `--job short` runs fewer iterations; `--job dry` runs each case once, which only proves that it works.
- `--iterationCount 25` matches divan's `sample_count = 25` of the `writes` benches (divan's default is 100 and the
  union-find groups use 50).
- `--artifacts <dir>` puts the reports somewhere other than `BenchmarkDotNet.Artifacts` in the current directory.

Because the benchmarks share one process, the runtime, garbage collector and JIT settings are the app's own (set them in
the project file); options that need a separate process, such as another runtime or another GC mode, are rejected. One
case's heap and JIT state carry over to the next, which BenchmarkDotNet's warmup absorbs for these workloads.

The app runs the server garbage collector. The engine's parallel paths allocate from every worker, and the workstation
collector stops them all for each collection: on a 4-core/8-thread i7-7700K, `WritesBenchmarks` at 8 threads took 263 ms
under the workstation collector and 205 ms under the server collector (egglog: 177 ms on the same machine). To measure
the workstation collector, set the environment variable `DOTNET_gcServer=0` before running.

Results are only meaningful on an otherwise idle machine. The configuration keeps the machine's current power plan
(BenchmarkDotNet would otherwise switch Windows to High performance for the run); select that plan yourself before
measuring if you want it.

Every summary has two extra columns: `Items`, the number of items one invocation processes (rows staged, union-find
operations applied, identifiers found), and `Items/s`, that count divided by the mean time. This is what divan prints
for a bench with an `ItemsCount` counter, so `Items/s` compares directly with divan's throughput figures. The memory
diagnoser is on for every class.

Each divan port processes its whole workload in one invocation: a fresh table or union-find is built before every
invocation (`[IterationSetup]`, `[InvocationCount(1)]`), as divan's `with_inputs` builds one per sample. Each workload
is generated once per parameter set from a fixed seed, where egglog draws a new one from an OS-seeded generator; the
algorithms and distributions are egglog's. After each iteration the result is checked against a reference model (the
table's live row count; every identifier's root), so a broken engine fails the run instead of producing a number. The
root benchmark set follows the same pattern where it changes state (see its section).

## What each class mirrors

| Class | Rust bench | Rust command |
| --- | --- | --- |
| `WritesBenchmarks` | `core-relations/benches/writes.rs`: `parallel_insert`, `parallel_insert_merge2`, `parallel_insert_remove_with_collisions` | `cargo +1.91.0 bench -p egglog-core-relations --bench writes` |
| `UnionFindSerialBenchmarks` | `union-find/benches/union-find.rs`: `random::serial`, `local::serial` | `cargo +1.91.0 bench -p egglog-union-find -- ::serial` |
| `UnionFindConcurrentBenchmarks` | `union-find/benches/union-find.rs`: `random::concurrent_{1,2,4,8}threads`, `local::concurrent_{1,2,4,8}threads`, type `concurrent::UnionFind<usize>` | `cargo +1.91.0 bench -p egglog-union-find -- concurrent_` |
| `UnionFindWorkStealingBenchmarks` | `union-find/benches/union-find.rs`: `rayon_work_stealing::random`, `rayon_work_stealing::local` | `cargo +1.91.0 bench -p egglog-union-find -- rayon_work_stealing` |
| `UnionFindFindOnlyBenchmarks` | `union-find/benches/union-find.rs`: `find_only::parallel_naive`, types `concurrent::UnionFind<usize>` and `Arc<ReadOptimizedLock<UnionFind<usize>>>` | `cargo +1.91.0 bench -p egglog-union-find -- parallel_naive` |
| `UnionFindFindOnlySerialBenchmarks` | `union-find/benches/union-find.rs`: `find_only::best_serial` | `cargo +1.91.0 bench -p egglog-union-find -- best_serial` |

The arguments after `--` are divan name filters (regular expressions); they also select the lock-based baseline types
that are not ported here. `cargo +1.91.0 bench -p egglog-union-find` alone runs every union-find bench. Run the Rust
benches in `ext/egglog`.

### Parameters

- `WritesBenchmarks.Threads` is divan's const `N` (1, 2, 4, 8; egglog also runs 16, beyond this machine's 8 logical
  cores). It plays the size of the rayon pool the bench `install`s: it caps both the parallel staging workers and the
  table's own parallel merge, through one `ParallelHeuristics`. Workload sizes are egglog's: 4 << 20 operations for
  `ParallelInsert` and `ParallelInsertMerge2`, 1 << 20 for `ParallelInsertRemoveWithCollisions`.
- The union-find classes take `Workload` (egglog's `random` or `local` group) and `N` (egglog's `LENGTHS`: 1000,
  100000, 1000000, 4000000); `UnionFindConcurrentBenchmarks` adds `Threads` (1, 2, 4, 8; egglog also runs 16).
- `UnionFindFindOnlyBenchmarks.BatchSize` is divan's const `N` of `parallel_naive` (32, 128, 256, 1024) over egglog's
  1,000,000 identifiers.

### Differences from the Rust

- rayon becomes `Parallel.For`: one iteration per batch where egglog spawns or bridges one task per batch. Where egglog
  uses the global rayon pool (`rayon_work_stealing`, `find_only`), the loop is capped at one thread per logical core.
- egglog builds the `writes` table outside `pool.install`, so its hash index has twice as many shards as the machine has
  threads whatever `N` is; here the table's shard count follows `Threads` (twice it, or one shard at one thread),
  because `ParallelHeuristics` fixes both.
- `find_only` folds each task's results into one value kept alive, in place of a `black_box` per find.
- Not ported: egglog's lock baselines `Arc<Mutex<UnionFind>>` (in the `random` and `local` groups) and
  `Arc<RwLock<UnionFind>>` (in `find_only`), which measure standard-library locks rather than the engine.

## The root benchmark set

The classes in `Root/` record the root engine's performance before colored contexts change it: milestone M9.0 of
`docs/design/colored-contexts-spec.md` (§17, X33). Together with `WritesBenchmarks` and the `UnionFind/` classes they
are the root benchmark set: every later colors milestone must keep the classes that exercise its code within 2% of the
baseline these classes record on the unchanged engine. Record the baseline, and every later comparison, on the same
idle machine.

| Milestone | Classes it is gated on |
| --- | --- |
| M9.1 | `ActionLookupBenchmarks`, `DisplacedRebuildBenchmarks`, `WritesBenchmarks`, `RuleSetRunBenchmarks` |
| M9.2 | `DisplacedRebuildBenchmarks`, `UnionFind/*` |
| M9.3 | `WritesBenchmarks`, `ActionLookupBenchmarks`, `RuleSetRunBenchmarks` |
| M9.4 | `DisplacedRebuildBenchmarks`, `RuleSetRunBenchmarks` |
| M10.1 | `ConstrainedScanBenchmarks`, `IndexRefreshBenchmarks` |
| M10.2 | `RuleSetRunBenchmarks`, `ConstrainedScanBenchmarks` |
| M10.3 | `RuleSetRunBenchmarks` (plus the bridge workloads once they exist) |

To record the whole set's baseline in one session, keeping the exported reports (the gates compare against all of
these, `WritesBenchmarks` and the `UnionFind/` classes included):

```
src/Eggnog.Relations.Benchmarks/bin/Release/net10.0/Eggnog.Relations.Benchmarks.exe --filter *RuleSetRunBenchmarks* *ConstrainedScanBenchmarks* *IndexRefreshBenchmarks* *DisplacedRebuildBenchmarks* *ActionLookupBenchmarks* *WritesBenchmarks* *UnionFind* --artifacts <dir>
```

Unlike the divan ports, these have no Rust counterpart: they measure the engine paths that colored contexts touch
(scans, index builds, the executor, actions, the union-find and the rebuild), on programs taken from the executor tests
or shaped as egglog's bridge lays out an e-graph. What they share:

- `Rows` (20,000 and 200,000) sizes the workload, as each class describes.
- `Mode` (`Serial`, `Parallel`) picks the engine's paths (`RunMode`, `RootParallelism`). `Serial` builds the database,
  its tables and indexes with `ParallelHeuristics.Serial`. `Parallel` builds them with the machine's logical cores,
  clamped to between 2 and 8, and forces the parallel rule-set executor (`Database.ParallelRuleSetsOverride`), as the
  executor tests' parallel modes do; merges, rebuilds and index builds then go parallel wherever egglog's size
  thresholds say so.
- Every workload is generated from the fixed seed `0x5EED`.
- Every class checks its result against a reference model, so a broken engine fails the run instead of producing a
  number. The classes that change the engine's state build it fresh before every invocation (`[IterationSetup]`,
  `[InvocationCount(1)]`) and check after each iteration. `ConstrainedScanBenchmarks` only reads, so it builds its table
  once, lets BenchmarkDotNet choose the invocation count, and has each invocation check its own result.

### RuleSetRunBenchmarks

Whole programs on `Database.RunRuleSet`, exercising header evaluation, the trie, the probers, the join kernels, the
action interpreter and `MergeAll`. Both run on `MathEgraph`, a port of the test fixture `Execution/ExecutionMathEgraph.cs`
(egglog's `basic_math_egraph`: a union-find, `num(value, id, ts)` and `add(x, y, id, ts)` merged with
`UnionTakeNewMerge`).

- `AcSaturation` is `AcSaturationWorkload`, a port of `Execution/AcSaturation.cs` (egglog's `ac_test_inner`): it builds
  the left- and right-associated sums of `n` leaves before the measurement, then measures the whole saturation loop
  (associativity and commutativity over the recent timestamp range, rule-based rebuilds, repeated until nothing
  changes), building every rule set inside the measurement as the program does. The AC e-graph has
  `3^n - 2^(n+1) + 1` `add` rows when saturated, so `Rows` picks the nearest size: 9 leaves (18,660 rows) for 20,000 and
  11 leaves (173,052 rows) for 200,000. `Items` is the saturated row count. The check: the two sums are equal, and the
  `add` table holds exactly the saturated rows, all canonical, in `2^n - n - 1` classes.
- `BasicMath` is `MathWorkload`: egglog's `basic_query` rule `Add(x, y, z), Num(a, x), Num(b, y) => Num(a + b, z)` over
  `Rows / 2` `num` rows (the values `0 .. Rows/2 - 1`) and `Rows / 2` `add` rows over distinct random pairs of their ids.
  The rule set is built before the measurement; one run, merge included, is measured. About half the sums collide with
  existing values, so the merge stages unions into the union-find. `Items` is the matches. The check: the matches, the
  live `num` rows and the union count equal the reference model's, which does not depend on merge order.

Differences from the test programs: commutativity matches the whole recent range (the test's `wholeRecentRange: true`)
so the e-graph saturates and both modes do the same work up to renaming; each rule is planned as egglog's bridge plans
it (`Gj` for multi-atom rules, `MinCover` for single-atom and rebuild rules) where the test takes one strategy; and the
fixture's tables take the mode's parallelism where the test fixture's are always serial.

### ConstrainedScanBenchmarks

`Table.ScanGenericBounded` and `Table.RefineLive` on one `SortedWritesTable`, exercising the scan loops and the
`TableKind` dispatch. The table (`TableRowsWorkload`) is `f(x, y) = v` with a timestamp: `Rows` rows keyed by `(x, y)`,
`x` in `[0, Rows/8)`, `v` in `[0, 256)`, stamped in 64 epochs. With `Tombstones`, every row whose index is 1 mod 4 is
then deleted (a quarter, below the half that would compact). The table is held as a `Table`, so each scan goes through
the kind dispatch, and scans resume every 256 positions (the executor's default morsel size).

| Method | Scans |
| --- | --- |
| `Unconstrained` | every row, no constraint (the fast loop, or the tombstone loop) |
| `EqConst` | every row, `v == 7` |
| `TimestampRangeFast` | timestamps `[16, 32)` as the executor applies them to the sort column: a fast subset (`SplitFastSlow`), scanned without slow constraints |
| `TimestampRangeScan` | every row, `ts >= 16` and `ts < 32` evaluated per row by the scan's constraint loop (its `GeConst` and `LtConst` arms) |
| `SlowConstraints` | every row, `x > Rows/16` and `v <= 63`, evaluated per row |
| `SparseSubset` | a sparse subset (every third row), `v < 128` |
| `RefineLive` | `RefineLive` of every row, which builds a sparse subset |

`Items` is the positions scanned (tombstones included). Each invocation compares the emitted rows' count and the sum of
their `y` cells with the reference model.

### IndexRefreshBenchmarks

`Index<TIndex>.Refresh` of a `ColumnIndex` (keyed by `x`, about eight rows per key) and a `TupleIndex` (keyed by
`(x, v)`) over a table of `Rows` rows of `TableRowsWorkload`, exercising the index builds.

- `ColumnFull` and `TupleFull` refresh an index that has seen nothing. The table is in its second generation (a first
  row was loaded and the table cleared), so the refresh clears the index and rebuilds it from every row: the bulk build,
  or the parallel build in `Parallel` above egglog's 20,000-row threshold (so only at 200,000 rows).
- `ColumnIncremental` and `TupleIncremental` refresh a clone of an index refreshed over the first three quarters of the
  rows: only the last quarter is indexed (in parallel above 20,000 rows, so only at 200,000 rows).

A refresh changes its index, so each invocation refreshes a batch of 16 fresh indexes made before it, and
BenchmarkDotNet reports the time of one refresh (`OperationsPerInvoke`). `Items` is the rows one refresh indexes. After
each iteration every refreshed index (`IndexContentChecker`) must be at the table's version, have the expected number of
distinct keys, and list as many rows as the table holds; every listed row must be live with key cells equal to the key it
is listed under; and the order-independent checksum of the listed (row, key) entries must equal the table's, so an index
that lists a wrong row under the right key fails too.

### DisplacedRebuildBenchmarks

A batch of unions merged into the `DisplacedTable` (its merge and path compression), then the bridge's native rebuild
loop, `Database.Rebuild`, over the function tables until congruence closes (`ApplyRebuild`: incremental through the
rebuild index where the delta is small, full scans elsewhere, and the congruence merges they cause). The e-graph
(`TermGraph`) is laid out as egglog's bridge lays one out: `pair(id, id) -> id` and `wrap(id) -> id` merged with
`UnionIdMerge`, and the lattice `low(id) -> value` merged by minimum, each sorted by its timestamp. `TermGraphWorkload`
generates `Rows` rows (three fifths nested `pair` terms, a fifth `wrap` terms, a fifth `low` values, over a fifth as many
leaves) and `Rows / 50` unions of leaves. `Items` is the unions. After each iteration the e-graph is checked against a
naive congruence closure of the same input: every id's canonical id, the union-find's size, every table's live rows
(all canonical) and `low`'s minimum per class.

### ActionLookupBenchmarks

One rule run whose work is action batches dominated by `LookupOrInsertDefault` and `Lookup`, writing a function whose
merge calls `ExecutionState.PredictRow`, exercising the action dispatch, the vectorized lookups, the predicted-value
cache and prediction from merge functions. `ActionLookupProgram` holds the bridge-shaped tables: constructors
`leaf(i64) -> id` and `combine(id, id) -> id`, the function `weight(id) -> i64`, the function `best(i64) -> id` with
`:merge (combine old new)` (`FunctionMerge`, a port of egglog-bridge's `ResolvedMergeFn::Function`, which predicts the
`combine` row with a fresh id and the clock's timestamp), and the relation `driver(i64, i64)`. `ActionLookupWorkload`
loads `leaf(k) = weight(k) = best(k) = k` for `k < Rows` and `Rows` `driver(a, b)` rows (distinct `a` from
`[0, 2 Rows)`, repeating `b` from the same range) and runs, per `driver` row, as the bridge would lower
`(set (best (weight (leaf a))) (leaf b))`:

```
ts = ReadCounter(clock)
x = LookupOrInsertDefault(leaf, [a], [fresh id, ts])   // hits, and new leaves for a >= Rows
y = LookupOrInsertDefault(leaf, [b], [fresh id, ts])   // hits, predicted misses, and repeated misses that hit the prediction
w = Lookup(weight, [x])                                // drops the lanes of new leaves
Insert(best, [w, y, ts])                               // collides with best(a): the merge predicts combine(old, new)
```

The rule set is built before the measurement; the run, merge included, is measured. `Items` is the `driver` rows (the
join's matches). After each iteration the run is checked against the reference model, which does not depend on the order
lanes, batches or merges run in: the reported matches (the lanes that survive the `Lookup`) and every table's live row
count; the loaded `leaf` and `weight` rows unchanged; every new leaf holding a fresh id that the union-find keeps
canonical; one `combine` row per merged `best` key, whose second key is in the class of the driver's `leaf(b)`; and every
`best(a)` holding the output of its merge's `combine` row (or still `a` where no merge changes it). So an engine that
drops or misstores a merge function's output fails the run.
