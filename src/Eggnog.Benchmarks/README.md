# Eggnog.Benchmarks

BenchmarkDotNet benchmarks for Eggnog's engine (`Relations/`, namespace `Eggnog.Relations.Benchmarks`). They measure:

- the engine against the Rust it reimplements, on the same workloads: ports of egglog's divan benchmarks for tables
  and the union-find (`Relations/Writes/`, `Relations/UnionFind/`), and ports of egglog-bridge's example programs
  `math` and `ac` (`Relations/Bridge/`), which the app can also run the way their Rust `main`s do (see
  [The egglog-bridge examples](#the-egglog-bridge-examples));
- the root engine's hot paths (`Relations/Root/`): scans, index builds, the executor, actions and rebuilds, the
  baseline that changes to those paths are compared against (see [The root benchmark set](#the-root-benchmark-set));
- what a colored context costs (`Relations/Contexts/`): per child as the root grows, per case of a split, per union,
  and per structure (see [The context benchmarks](#the-context-benchmarks)).

Benchmarks with a result to check check it, against a reference model or against known counts, so a broken engine
fails the run instead of producing a number. The project references the library too, so benchmarks of the library's own paths can live beside these.

## Running

This is a plain console app. BenchmarkDotNet is used as a library with its in-process toolchain: every benchmark runs
inside this process, against the Eggnog assembly (engine included) the app was built with. Nothing generates, builds
or launches a separate project per benchmark, so MSBuild is involved only when you build the app itself.

Build in Release and run the executable, from the repository root:

```
dotnet build -c Release src/Eggnog.Benchmarks
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --filter *WritesBenchmarks*
```

With no arguments it runs every benchmark. `dotnet run -c Release --project src/Eggnog.Benchmarks -- <args>`
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

## The egglog-bridge examples

The classes in `Relations/Bridge/` port egglog-bridge's two example programs, `egglog-bridge/examples/math.rs` and
`egglog-bridge/examples/ac.rs`: whole programs written against the bridge API (`EGraph`, `FunctionConfig`,
`define_rule!`, `add_expressions!`, `run_rules`), each measured over exactly what its Rust `main` times. The console app
can also run either program the way its Rust `main` does, printing the same lines, so the two can be compared side by
side.

| Class | Program | Rust example |
| --- | --- | --- |
| `BridgeMathBenchmarks.Math` | `BridgeMathProgram` | `egglog-bridge/examples/math.rs`, the bridge form of `examples/math.egg` |
| `BridgeAcBenchmarks.Ac` | `BridgeAcProgram` | `egglog-bridge/examples/ac.rs`, the bridge form of the program `examples/make-ac-bench.py` prints |

- `math`: the 13 constructors of `math.rs` with its schemas (`diff`, `integral`, `add`, `sub`, `mul`, `div` and `pow`
  over two ids; `ln`, `sqrt`, `sin` and `cos` over one; `rat` over a `Rational64`; `var` over a string), each with the
  fresh-id default, the union-id merge and no subsumption; its 24 rules and its seven seed expressions, in its order;
  then at most `N = 12` iterations, stopping at the first that changes nothing (none does). Rust's `Rational64` is
  `Rational`, interned (`InternedCodec<Rational>`) as egglog interns it; `&'static str` is `StringCodec`.
- `ac`: the constructors `num(i64)` and `add(id, id)`, the commutativity and associativity rules, a run on the empty
  database that must change nothing, the numbers `0 .. 12` under fresh ids, then `(0 + ... + 12)` associated to the left
  and `(12 + ... + 0)` associated to the right, each group loaded with one `add_values`; then iterations until one
  changes nothing, after which the two sums must be in one class (`N = 13`).

Both e-graphs are `new EGraph()`, which uses every logical processor, as `EGraph::default()` uses rayon's global pool.

### What is measured

One invocation is one repetition of the Rust `main`'s loop body, timed from before the e-graph is created to where the
Rust prints its total (`Total time`, `Time elapsed`): creating the e-graph, registering the base types, declaring the
tables, building the rules, loading the seed data, the iterations, and then reading the table sizes (`math`, which prints
them inside its timer) or the equality assertion (`ac`, inside its timer). The benchmarks print nothing, where the Rust
prints a line per iteration. The Rust leaves dropping the e-graph out of its time ("We don't include dropping the egraph
in the time"); here the e-graph stays referenced until `[IterationCleanup]`, and BenchmarkDotNet's forced collection
before the next iteration collects it. One invocation per iteration (`[InvocationCount(1)]`): an invocation takes
seconds; `math` builds about 16 million rows and allocates about 10 GB, `ac` builds about 1.6 million `add` rows and
allocates about 15 GB.

Each invocation checks its result, so a broken engine fails the run instead of producing a number:

- `math`: 12 iterations without saturating, and every table at the size `math.exe` prints: `diff` 100,395, `integral`
  224,150, `add` 11,396,002, `sub` 78,586, `mul` 4,188,378, `div` 3, `pow` 2, `ln`, `sqrt`, `sin` and `cos` 1, `rat` 5,
  `var` 3.
- `ac`: the two sums in one class (`ac.rs`'s `assert_eq!`), 10 iterations (see the differences below), 13 `num` rows,
  and 1,577,940 `add` rows: the saturated e-graph has one `add` row per ordered split of each subset of at least two of
  the 13 numbers into two non-empty parts, `3^13 - 2^14 + 1`. egglog does not print that count; it follows from the
  program.

With the default job each benchmark runs many iterations of seconds each; `--job short` or `--iterationCount 5` keeps a
run short, and `--job dry` runs each once.

### Running both sides

Build the app in Release (see [Running](#running)), then, from the repository root, run the BenchmarkDotNet classes:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --filter *Bridge*
```

or the example mode, which runs a program twice, as both Rust `main`s do (`for _ in 0..2`), without BenchmarkDotNet:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --bridge-example math
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --bridge-example ac
```

The example mode prints the Rust's lines: `math` prints `Iteration {i} finished, duration={:?} (saturated={})` per
iteration, `Function {name} has size {n}` per table and `Total time: {:?}`; `ac` prints
`Finished iteration {i} after {:?}` per iteration and `Time elapsed: {:?}`. Durations are formatted as Rust's `{:?}`
formats a `Duration` (`9.0456298s`, `279.7217ms`, `908.1µs`), and the lines are written to standard output as UTF-8
with `\n` line ends, as `println!` writes them. A repetition that fails a check (the checks above, or, for `ac`, the
run on the empty database changing something) is reported on standard error and ends the run with exit code 1 (`math`
is checked after its `Total time` line, outside the time; `ac` before its `Time elapsed` line, inside the time, where
`ac.rs` asserts both). Between repetitions the e-graph is released and the heap collected, outside the time.

The Rust examples, from `ext/egglog` (the workspace pins toolchain 1.91.0):

```
cargo +1.91.0 build --release -p egglog-bridge --examples
target/release/examples/math.exe
target/release/examples/ac.exe
```

`cargo +1.91.0 run --release -p egglog-bridge --example math` (or `--example ac`) builds and runs in one step. From the
repository root the binaries are `ext/egglog/target/release/examples/math.exe` and
`ext/egglog/target/release/examples/ac.exe`. `RAYON_NUM_THREADS` sets the Rust's thread count; the C# side always uses
every logical processor.

### Differences from the Rust examples

- `ac` runs 10 iterations here (0 to 9) and 11 in egglog (0 to 10). Its iteration 9 re-derives the saturated e-graph:
  about 5.2 million matches, no new row and no new union, so the engine reports no change and the loop stops. In egglog
  the more than 20,000 rows that iteration stages into `add` are merged by `parallel_insert`, which reports a change for
  every staged row before deciding whether its merge changes anything (`core-relations/src/table/mod.rs:871`), so egglog
  runs an iteration 10, which finds no new row and takes microseconds. The engine reports real changes only, fixing that
  defect (row 4 of the defect list, "egglog defects: what the port does instead" in
  [`docs/history/egglog-engine-map.md`](../../docs/history/egglog-engine-map.md)); with one rayon thread egglog would
  take its serial insert,
  which reports real changes, and stop after 10 iterations too. The results agree otherwise: the sums are equal on both
  sides, and every table size `math` prints is the same.
- The example mode's first repetition includes JIT compilation, which the Rust does not have.
- egglog allocates with mimalloc. Here the server garbage collector runs (see [Running](#running)), and the collections
  during a run are part of its time.
- Fresh ids, and so the number of redundant unions the parallel paths stage, can vary from run to run; the table sizes,
  the iteration counts and the checked results do not.
- `env_logger::init()` has no counterpart.

## The root benchmark set

The classes in `Relations/Root/`, together with `WritesBenchmarks` and the `Relations/UnionFind/` classes, are the root
benchmark set: they measure the engine paths that every program takes at the root, and a change to those paths is
compared against a baseline of the whole set, recorded on the same idle machine before the change. The tolerance used
so far is 2%. Which classes exercise which paths:

| Path | Classes |
|---|---|
| Scans and their constraints | `ConstrainedScanBenchmarks`, `RuleSetRunBenchmarks` |
| Index builds and refreshes | `IndexRefreshBenchmarks`, `ConstrainedScanBenchmarks` |
| The executor and actions | `RuleSetRunBenchmarks`, `ActionLookupBenchmarks` |
| Table writes and merges | `WritesBenchmarks`, `ActionLookupBenchmarks`, `RuleSetRunBenchmarks` |
| The union-find and rebuilds | `DisplacedRebuildBenchmarks`, the `UnionFind/` classes |

To record the whole set's baseline in one session, keeping the exported reports (the gates compare against all of
these, `WritesBenchmarks` and the `Relations/UnionFind/` classes included):

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --filter *RuleSetRunBenchmarks* *ConstrainedScanBenchmarks* *IndexRefreshBenchmarks* *DisplacedRebuildBenchmarks* *ActionLookupBenchmarks* *WritesBenchmarks* *UnionFind* --artifacts <dir>
```

Unlike the divan ports, these have no Rust counterpart: they measure scans, index builds, the executor, actions, the
union-find and the rebuild, on programs taken from the executor tests
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
action interpreter and `MergeAll`. Both run on `MathEgraph`, a port of the test fixture `Relations/Execution/ExecutionMathEgraph.cs`
(egglog's `basic_math_egraph`: a union-find, `num(value, id, ts)` and `add(x, y, id, ts)` merged with
`UnionTakeNewMerge`).

- `AcSaturation` is `AcSaturationWorkload`, a port of `Relations/Execution/AcSaturation.cs` (egglog's `ac_test_inner`): it builds
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

The app's executor-counts mode runs the `BasicMath` workload three times at each size and mode, and prints the
executor's counters for each run's rule execution and its merge: the execution states cloned, by site, the mutation
buffers they made, and the first allocations and the growths of their buffer maps. It also prints the bytes of one
clone, alone and with the buffer of its first write. The counters are off unless `EGGNOG_COUNTERS` is set:

```
set EGGNOG_COUNTERS=1
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --executor-counts
```

Counts and bytes do not depend on the machine's load, so they need no idle machine.

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

## The context benchmarks

The classes in `Relations/Contexts/` measure what a colored context costs. They have no Rust counterpart and no
baseline to stay within, but a shape to keep: a child must cost in proportion to what it touches, never to the size
of its ancestors. The root workload (`RootWorkload`: `f(x) → v` and `r(x, x+1)` for every id, so a child's work over
a fixed set of ids touches a fixed number of root rows; or `CaseGraph` for the bridge workloads) is built from the
fixed seed `0x5EED`, frozen except where a class says otherwise; the classes check their results in the setup or in every invocation.

| Class | What it measures | The shape to keep |
| --- | --- | --- |
| `PerChildCostBenchmarks` | One child of a root of 20,000, 200,000 or 2,000,000 rows, frozen or not (`FrozenRoot`): creation, 100 unions in one batch, a rebuild, disposal; and the full workload on the bridge, which also flushes and runs one rule whose watermark the child inherits from the root. A root that is not frozen brings its rebuild indexes up to date when it creates a child; the setup's first child absorbs that root work. | Time and allocated bytes flat within 1.5× from 20k to 2M rows; the setup checks that the child examines and copies exactly the 300 root rows naming its losers, and that the rule matches only the child's 200 rewritten rows. |
| `ManyChildrenBenchmarks` | A thousand single-union children of a 200,000-row frozen root, in parallel and serially; one child with no layer and with one empty layer. | `ChildWithOneEmptyLayer` minus `ChildOnly` in the allocated column is the bytes per empty layer (about 1 KB). |
| `ChildFindBenchmarks` | `find` at a frozen root, in a child at depth 1 and 3 over an explicit chain (raw and through `Context.Find`), and under a frozen, flattened chain. | One probe per non-empty level over the root's find; a flattened chain costs one map read whatever its depth. |
| `ContextExtractionBenchmarks` | On-demand extraction of 4,000 classes from a fresh extractor: through the root API (the baseline), through the context form at the root, and in a child of a 40,000-term frozen root that made 4,000 terms and 400 unions of its own, frozen or not. | `RootContext` within noise of `RootApi` (one branch per discovery round, none per node); `Child` pays one index probe per present layer per producing function on top. |
| `CaseSplitMemoryBenchmarks` | The bytes one split allocates per case (a case set under a fresh scope, each case one union and one fact, optionally lifted), against a full clone of the root per case with the same writes, over roots of 20,000 and 200,000 ids. | Bytes per case flat in the root's size for contexts; clones grow linearly. |
| `CopyVolumeBenchmarks` | How many ancestor rows a child copies for one union, as a histogram (printed by the setup) over unions of random ids on a root where a few ids are named by thousands of rows. | Most unions copy two or three rows; only a union whose loser is a heavily used id copies many. |
| `LateParentMintBenchmarks` | A writable parent that lifts terms after its cases built them, so each lifted term's parent id loses to the cases' local ids: the parent-id losers, the rows they copy per lift round, and the growth of the root's id array. | Counters linear in cases times terms per round, independent of the root's size. |

Run them like any other class, for example `--filter *PerChildCostBenchmarks*`; the 2M-row root takes some seconds to
build in the setup.

The app's footprint mode prints the steady-state bytes of the context structures (a bare child, an empty layer, a
bridge child, a child with one union) and the counters of the case-split workloads, measured in process after a
warm-up and averaged over many repetitions, without the cold-start allocations a dry run includes:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --context-footprint
```

Allocation counts and counters do not depend on the machine's load, so these figures need no idle machine; times do.
