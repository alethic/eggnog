# Eggnog.Benchmarks

BenchmarkDotNet benchmarks for Eggnog's engine (`Relations/`, namespace `Eggnog.Relations.Benchmarks`) and for the
library through its public API (`Library/`, namespace `Eggnog.Benchmarks.Library`). They measure:

- the engine against the Rust it reimplements, on the same workloads: ports of egglog's divan benchmarks for tables
  and the union-find (`Relations/Writes/`, `Relations/UnionFind/`), and ports of egglog-bridge's example programs
  `math` and `ac` (`Relations/Bridge/`), which the app can also run the way their Rust `main`s do (see
  [The egglog-bridge examples](#the-egglog-bridge-examples));
- the root engine's hot paths (`Relations/Root/`): scans, index builds, the executor, actions and rebuilds, the
  baseline that changes to those paths are compared against (see [The root benchmark set](#the-root-benchmark-set));
- what a colored context costs (`Relations/Contexts/`): per child as the root grows, per case of a split, per union,
  per structure, and per row a child catches up with under a busy parent (see
  [The context benchmarks](#the-context-benchmarks));
- the library against the egglog command line, on a port of egglog's `math-microbenchmark.egg`, and the library's own
  paths: transient query scopes and idle local rules, and the library's work per run at the root. They use the public
  API, except the engine baseline of `LibraryRunOverheadBenchmarks` (see
  [The library benchmarks](#the-library-benchmarks)).

Benchmarks with a result to check check it, against a reference model or against known counts, so a broken engine
fails the run instead of producing a number.

The Rust side of every comparison is egglog at commit `5e655cae`, the version the engine follows, and the commands,
counts and sizes below are for it. The submodule `ext/egglog` holds egglog 3.0.0, so check out that commit in it
before building the Rust (`git -C ext/egglog checkout 5e655cae`), and the submodule's own commit afterwards
(`git submodule update ext/egglog`). egglog 3.0.0 replaced Rayon's global pool with a thread pool per e-graph: the
bridge examples' `EGraph::default()` runs on one thread, and `RAYON_NUM_THREADS` no longer sets their thread count
([`docs/egglog-3.0.0-todo.md`](../../docs/egglog-3.0.0-todo.md)).

Besides running benchmarks, the app has modes that count allocations by type
([The allocation probe](#the-allocation-probe)) and compare two or more builds from their exported reports under the
measurement campaign's rules ([Comparing two builds](#comparing-two-builds)).

## Running

This is a plain console app. BenchmarkDotNet is used as a library with its in-process toolchain: every benchmark runs
inside this process, against the Eggnog assembly (engine included) the app was built with. Nothing generates, builds
or launches a separate project per benchmark, so MSBuild is involved only when you build the app itself.

Build in Release and run the executable, from the repository root:

```
dotnet build -c Release src/Eggnog.Benchmarks
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --filter *WritesBenchmarks*
```

With no arguments it runs every benchmark. `dotnet run -c Release -f net10.0 --project src/Eggnog.Benchmarks -- <args>`
does the same build and run in one step.

The app targets net10.0 and net8.0, as the library does, and the build writes one executable per target framework
(`bin/Release/net8.0/Eggnog.Benchmarks.exe` runs every benchmark on .NET 8's runtime and JIT). The runtime is the
process's own, so each target framework is its own launch.

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
- `WritesBenchmarks.Shards` is the table's shard count: 0 takes twice `Threads` (one shard at one thread), as the
  engine derives it; 16 fixes 16 shards at every thread count, which is what egglog's bench gets on an eight-thread
  machine (see the differences below). The setup checks that the table has the shard count the two parameters call
  for. At 8 threads the default is already 16 shards, so the class has no case of 16 there: 21 cases in all. Builds
  from before this parameter ran the cases of 0; compare them with `--absent Shards=0`
  ([Comparing two builds](#comparing-two-builds)).
- The union-find classes take `Workload` (egglog's `random` or `local` group) and `N` (egglog's `LENGTHS`: 1000,
  100000, 1000000, 4000000); `UnionFindConcurrentBenchmarks` adds `Threads` (1, 2, 4, 8; egglog also runs 16).
- `UnionFindFindOnlyBenchmarks.BatchSize` is divan's const `N` of `parallel_naive` (32, 128, 256, 1024) over egglog's
  1,000,000 identifiers.

### Differences from the Rust

- rayon becomes `Parallel.For`: one iteration per batch where egglog spawns or bridges one task per batch. Where egglog
  uses the global rayon pool (`rayon_work_stealing`, `find_only`), the loop is capped at one thread per logical core.
- egglog builds the `writes` table outside `pool.install`, so its hash index has twice as many shards as the machine has
  threads whatever `N` is; here the table's shard count follows `Threads` (twice it, or one shard at one thread) when
  `Shards` is 0, and `Shards = 16` reproduces egglog's layout on an eight-thread machine.
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
tables, building the rules, loading the seed data, the iterations, and then reading the table sizes (`math`, which
prints them inside its timer) or the equality assertion (`ac`, inside its timer). The benchmarks print nothing, where
the Rust prints a line per iteration. The Rust leaves dropping the e-graph out of its time ("We don't include dropping
the egraph in the time"); here the e-graph stays referenced until `[IterationCleanup]`, and BenchmarkDotNet's forced
collection before the next iteration collects it. One invocation per iteration (`[InvocationCount(1)]`): an invocation
takes seconds; `math` builds about 16 million rows and allocates about 10 GB, `ac` builds about 1.6 million `add` rows
and allocates about 15 GB.

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

The example mode's e-graph uses every logical processor, as `EGraph::default()` uses rayon's global pool.
`--threads <n>` sets its degree of parallelism instead, the counterpart of `RAYON_NUM_THREADS=<n>` on the Rust side:
`--bridge-example math --threads 1` against `RAYON_NUM_THREADS=1 math.exe` compares the two at one thread. One thread
makes every operation serial and gives each table one shard, as one rayon thread does. The BenchmarkDotNet classes keep
every processor, so that their cases match those of earlier builds.

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
`ext/egglog/target/release/examples/ac.exe`. `RAYON_NUM_THREADS` sets the Rust's thread count, and `--threads` the
example mode's.

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
compared against a baseline of the whole set, recorded on the same idle machine before the change, with a 2% gate (see
[Comparing two builds](#comparing-two-builds)). Which classes exercise which paths:

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
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --filter *RuleSetRunBenchmarks* *ConstrainedScanBenchmarks* *IndexRefreshBenchmarks* *DisplacedRebuildBenchmarks* *ActionLookupBenchmarks* *WritesBenchmarks* *UnionFind* --allStats --artifacts <dir>
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
action interpreter and `MergeAll`. Both run on `MathEgraph`, a port of the test fixture
`Relations/Execution/ExecutionMathEgraph.cs` (egglog's `basic_math_egraph`: a union-find, `num(value, id, ts)` and
`add(x, y, id, ts)` merged with `UnionTakeNewMerge`).

- `AcSaturation` is `AcSaturationWorkload`, a port of `Relations/Execution/AcSaturation.cs` (egglog's `ac_test_inner`):
  it builds the left- and right-associated sums of `n` leaves before the measurement, then measures the whole saturation
  loop (associativity and commutativity over the recent timestamp range, rule-based rebuilds, repeated until nothing
  changes), building every rule set inside the measurement as the program does. The AC e-graph has `3^n - 2^(n+1) + 1`
  `add` rows when saturated, so `Rows` picks the nearest size: 9 leaves (18,660 rows) for 20,000 and 11 leaves (173,052
  rows) for 200,000. `Items` is the saturated row count. The check: the two sums are equal, and the `add` table holds
  exactly the saturated rows, all canonical, in `2^n - n - 1` classes.
- `BasicMath` is `MathWorkload`: egglog's `basic_query` rule `Add(x, y, z), Num(a, x), Num(b, y) => Num(a + b, z)` over
  `Rows / 2` `num` rows (the values `0 .. Rows/2 - 1`) and `Rows / 2` `add` rows over distinct random pairs of their
  ids. The rule set is built before the measurement; one run, merge included, is measured. About half the sums collide
  with existing values, so the merge stages unions into the union-find. `Items` is the matches. The check: the matches,
  the live `num` rows and the union count equal the reference model's, which does not depend on merge order.

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
distinct keys, and list as many rows as the table holds; every listed row must be live with key cells equal to the key
it is listed under; and the order-independent checksum of the listed (row, key) entries must equal the table's, so an
index that lists a wrong row under the right key fails too.

### DisplacedRebuildBenchmarks

A batch of unions merged into the `DisplacedTable` (its merge and path compression), then the bridge's native rebuild
loop, `Database.Rebuild`, over the function tables until congruence closes (`ApplyRebuild`: incremental through the
rebuild index where the delta is small, full scans elsewhere, and the congruence merges they cause). The e-graph
(`TermGraph`) is laid out as egglog's bridge lays one out: `pair(id, id) -> id` and `wrap(id) -> id` merged with
`UnionIdMerge`, and the lattice `low(id) -> value` merged by minimum, each sorted by its timestamp. `TermGraphWorkload`
generates `Rows` rows (three fifths nested `pair` terms, a fifth `wrap` terms, a fifth `low` values, over a fifth as
many leaves) and `Rows / 50` unions of leaves. `Items` is the unions. After each iteration the e-graph is checked
against a naive congruence closure of the same input: every id's canonical id, the union-find's size, every table's live
rows (all canonical) and `low`'s minimum per class.

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
canonical; one `combine` row per merged `best` key, whose second key is in the class of the driver's `leaf(b)`; and
every `best(a)` holding the output of its merge's `combine` row (or still `a` where no merge changes it). So an engine
that drops or misstores a merge function's output fails the run.

## The context benchmarks

The classes in `Relations/Contexts/` measure what a colored context costs. They have no Rust counterpart and no
baseline to stay within, but a shape to keep: a child must cost in proportion to what it touches, never to the size
of its ancestors. The root workload (`RootWorkload`: `f(x) → v` and `r(x, x+1)` for every id, so a child's work over
a fixed set of ids touches a fixed number of root rows; or `CaseGraph` for the bridge workloads) is built from the
fixed seed `0x5EED`, frozen except where a class says otherwise; the classes check their results in the setup or in
every invocation.

| Class | What it measures | The shape to keep |
| --- | --- | --- |
| `PerChildCostBenchmarks` | One child of a root of 20,000, 200,000 or 2,000,000 rows, frozen or not (`FrozenRoot`): creation, 100 unions in one batch, a rebuild, disposal; and the full workload on the bridge, which also flushes and runs one rule whose watermark the child inherits from the root. A root that is not frozen brings its rebuild indexes up to date when it creates a child; the setup's first child absorbs that root work. | Time and allocated bytes flat within 1.5× from 20k to 2M rows; the setup checks that the child examines and copies exactly the 300 root rows naming its losers, and that the rule matches only the child's 200 rewritten rows. |
| `ManyChildrenBenchmarks` | A thousand single-union children of a 200,000-row frozen root, in parallel and serially; one child with no layer and with one empty layer. | `ChildWithOneEmptyLayer` minus `ChildOnly` in the allocated column is the bytes per empty layer (about 1 KB). |
| `ChildFindBenchmarks` | `find` at a frozen root, in a child at depth 1 and 3 over an explicit chain (raw and through `Context.Find`), and under a frozen, flattened chain. | One probe per non-empty level over the root's find; a flattened chain costs one map read whatever its depth. |
| `ContextExtractionBenchmarks` | On-demand extraction of 4,000 classes from a fresh extractor: through the root API (the baseline), through the context form at the root, and in a child of a 40,000-term frozen root that made 4,000 terms and 400 unions of its own, frozen or not. | `RootContext` within noise of `RootApi` (one branch per discovery round, none per node); `Child` pays one index probe per present layer per producing function on top. |
| `CaseSplitMemoryBenchmarks` | The bytes one split allocates per case (a case set under a fresh scope, each case one union and one fact, optionally lifted), against a full clone of the root per case with the same writes, over roots of 20,000 and 200,000 ids. | Bytes per case flat in the root's size for contexts; clones grow linearly. |
| `CopyVolumeBenchmarks` | How many ancestor rows a child copies for one union, as a histogram (printed by the setup) over unions of random ids on a root where a few ids are named by thousands of rows. | Most unions copy two or three rows; only a union whose loser is a heavily used id copies many. |
| `LateParentMintBenchmarks` | A writable parent that lifts terms after its cases built them, so each lifted term's parent id loses to the cases' local ids: the parent-id losers, the rows they copy per lift round, and the growth of the root's id array. | Counters linear in cases times terms per round, independent of the root's size. |
| `BusyParentCatchUpBenchmarks` | A writable 20,000-row root with 1, 8 or 64 live children, each with one union of its own, appends 100 or 10,000 rows to a shared table (one in ten naming a child's loser), and every child rebuilds. Only the rebuilds are measured; `Items` is the rows examined, so time over `Items` is the time per examined row. Each iteration is one invocation on a fresh root, below BenchmarkDotNet's recommended 100 ms, so read the time per row from the cases with 10,000 appended rows; the others are there for their counters. | Each child examines every appended row once (`Children × Appended` in all, checked after each iteration), and only the rows naming its own loser are copied (`Appended / 10` in all); after the children are disposed the root has no live child, and each iteration's setup checks that the previous iteration was checked. |

Run them like any other class, for example `--filter *PerChildCostBenchmarks*`; the 2M-row root takes some seconds to
build in the setup.

The app's footprint mode prints the steady-state bytes of the context structures (a bare child, an empty layer, a
bridge child, a child with one union) and the counters of the case-split workloads, measured in process after a
warm-up and averaged over many repetitions, without the cold-start allocations a dry run includes:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --context-footprint
```

Allocation counts and counters do not depend on the machine's load, so these figures need no idle machine; times do.

Two more modes print counts and bytes for the questions colored contexts leave open:

- `--stale-probe <iterations> none|held|caughtup` (`Relations/Bridge/StaleProbe.cs`) runs the bridge `math` program
  at its root with no child, with a child of the root held unused from the start, or with one caught up after every
  iteration. A table does not compact while its scope has live descendants, so the probe prints, per iteration, the
  function tables' live rows, physical rows, tombstones, the compactions the gate blocked, the tombstones at or above
  the child's cursor and those above egglog's compaction bound. At the end it prints the tables' row storage,
  compaction scratch buffers and key indexes, and the managed heap after forced collections taken before the program,
  with the graph live and after it is released, which separates the graph's own memory from what the process keeps
  in pooled arrays. Every iteration checks its counts (no tombstone column above the tombstones, nothing blocked
  without a child, no compaction while the child lives), and with all 12 iterations the result is checked against
  egglog's; a failed check ends the run with exit code 1.
- `--plan-footprint [rules...]` (`Relations/Contexts/PlanFootprint.cs`) prints the bytes a sibling scope's freeze
  allocates and retains for planning, per rule, on a synthetic theory of 100 and 1,000 global rules (or the counts
  given): what a plan per frozen scope would cost.

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --stale-probe 12 held
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --plan-footprint
```

The share of a split's work that goes to catch-up is counted in the tests, not here: `SplitCatchUpCountTests` (in
`src/Eggnog.Tests/Cases/`) counts, per split step of a domain split on the test fixture `PredicateTheory` under a
writable query scope, at 3 and 8 cases, the rows the cases examined in catch-ups against those their rule runs scanned
and indexed. The query's totals are pinned to the recorded figures, so a change to them fails the test.

## The library benchmarks

The classes in `Library/` measure the library through its public API, as an application uses it; only the baseline of
`LibraryRunOverheadBenchmarks`, with its setup and checks, calls the engine directly. They are not part of the root
benchmark set.

### math-microbenchmark.egg

| Class | Program | Counterpart |
| --- | --- | --- |
| `MathMicrobenchmarkBenchmarks.Math` | `MathMicrobenchmarkProgram` | `egglog -j <n> tests/math-microbenchmark.egg` |

`MathMicrobenchmarkProgram` is egglog's `tests/math-microbenchmark.egg` written against the library: the datatype
`Math` with its 13 constructors (`Const` over `i64`, `Var` over `String`), its 24 rewrites, its seven seed expressions
in the file's order, and `(run 11)` as `Schedule.Repeat(11, Schedule.Run(rules))`, which stops early after a pass that
changes nothing, as egglog's `run` does. One invocation declares the theory, adds the seeds, runs and reads the table
sizes. `DegreeOfParallelism` (1, 8) is `EGraphOptions.DegreeOfParallelism`, egglog's `-j`: 1 is the default of both,
and 8 is every logical processor of the four-core, eight-thread campaign machine.

Each invocation checks its result: eleven passes without saturating, and every table at the size egglog's
`(print-size)` prints: `Add` 641,743, `Const` 5, `Cos` 1, `Diff` 13,504, `Div` 3, `Integral` 32,434, `Ln` 1, `Mul`
345,075, `Pow` 2, `Sin` 1, `Sqrt` 1, `Sub` 15,123, `Var` 3. These are the sizes `egglog -j 1` and `egglog -j 8` print
for the file at commit `5e655cae`, built with `cargo +1.91.0 build --release --bin egglog`.

The app also runs the program the way the egglog command line runs the file, printing the sizes as `(print-size)`
does, the passes, and `Total time: {:?}`, timed from before the graph's creation to after the sizes are read:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --math-microbenchmark --threads 1 --repetitions 5
```

and on the Rust side, from `ext/egglog`:

```
cargo +1.91.0 build --release --bin egglog
target/release/egglog.exe -j 1 tests/math-microbenchmark.egg
```

egglog's process time includes starting the process and parsing the file, and its `(print-stats)` prints the time of
search, merge and rebuild per ruleset; the C# mode's first repetition includes JIT compilation. Compare the C# mode's
warm repetitions with egglog's search, merge and rebuild total, or the BenchmarkDotNet class with egglog's process
time less its start-up, and say which.

### Query scopes and run overhead

`QueryScopeBenchmarks` and `LibraryRunOverheadBenchmarks` share a theory (`LongTables`): `T` relations `p0 … p(T−1)`
over `long`, each holding 1,000 rows at the root (`p0` lacks the value 999), one head relation `q_i` per `p_i`, and a
relation `idle` that no rule reads. Rule `r` of `A` body atoms reads `p_h(x)` with `h = r mod T` and `A − 1` other `p`
tables (all `A` distinct, checked when the rule is defined), and derives `q_h(x)`; semi-naive evaluation gives it one
variant per body atom, so the cost of an idle rule grows with `A`.

| Class | What it measures | Checks |
| --- | --- | --- |
| `QueryScopeBenchmarks` | One query of a frozen schema scope: a child of the schema scope, one assumed fact, a saturating run, one check, disposal. The schema scope is a child of the frozen root that defines `Rules` local rules (0, 10, 100, 1,000) of `Atoms` body atoms (2, 4) over `Tables` relations (10, 30, 50), saturates and freezes. In `Idle` mode the query asserts `idle(1)`, so every rule is idle; in `Active` mode it asserts `p0(999)`, so every rule reading `p0` fires once. | The schema scope's `q` tables against the model; per query, the run's iterations (0 without rules, 1 idle, 2 active), saturation and the checked fact (`q0(999)` when active); no query scope left alive, after the setup's query and at cleanup. |
| `LibraryRunOverheadBenchmarks` | One iteration at the root of `Rules` global rules (10, 100) of `Atoms` body atoms (2, 4), after they saturated, so every rule is idle: through `Scope.Run` (`LibraryRun`), against the same lowered variants run through the engine context's `RunRules` (`EngineRun`, the baseline). The ratio is the library's per-run overhead. | The saturated tables against the model; one run of each path that reports no change and no match; the engine path given the lowered variant of every rule; one call of each benchmark method that runs every variant (each variant's last run moves to the call's epoch); per invocation, no change, and for the library one iteration of every rule. |

`Items` is the rules a query or iteration runs, so time over `Items` is the time per rule; the allocated bytes against
`Rules` give the bytes per idle rule. Run each library class in its own process, once as built (server collector) and
once with `DOTNET_gcServer=0`, since a service may run either collector.

Whether sibling schema scopes compiled in either order run different plans is checked in the tests, not here:
`SiblingScopePlanOrderTests` (in `src/Eggnog.Tests/Scopes/`) compiles the fixed corpus of `ScopedQueryCases.CompileAll`
on `PredicateTheory` in both orders, with recorded plans, checks that each order plans every global rule once and
shares that plan with every scope, and prints the plan of every global rule. Timing those
queries here needs the fixture in this app.

## The allocation probe

The probe runs one benchmark case outside BenchmarkDotNet, with the same life cycle (parameters, global setup, then per
invocation the iteration setup, the method and the iteration cleanup), and reports the bytes each invocation allocates
and where they go by type:

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --allocation-probe RuleSetRunBenchmarks.BasicMath Rows=200000 Mode=Serial
```

- The case is `Class.Method`, or `Class` when the class has one benchmark method. Parameters are `Name=value`; a
  parameter not given takes its first value, which the first output line shows.
- `--warmup <n>` (default 1) invocations run first and are not counted; `--repetitions <n>` (default 5) are.
- The bytes per invocation come from `GC.GetTotalAllocatedBytes(true)` around the method alone, so the setups are not
  in them; they count every thread, so a parallel case's workers are included, and so is any other thread of the
  process: now and then an invocation gains a few hundred bytes the case did not allocate (672 B in one of eight
  invocations of `UnionFindFindOnlySerialBenchmarks.BestSerial`, which allocates nothing). For a case of a few bytes,
  read the minimum beside the mean.
- The types come from the runtime's `GCAllocationTick` events, read in process by an `EventListener` over the measured
  invocations: the runtime raises one about every 100 KB a thread allocates, naming the type that crossed the
  threshold. The bytes by type are a sample; their shares estimate where the bytes go. `--types <n>` (default 15) sets
  how many types are listed. The listener's own event dispatch allocates too (about 1.4 KB per tick, 0.4% of
  `BasicMath`'s bytes at 200,000 rows, shown as `MoreEventInfo`); `--types 0` runs without it, for the bytes alone.
- The method is called through a delegate bound once, not through reflection's invoker, so a call allocates nothing
  beyond what the method does (and the boxing of a value-type result). The first call compiles the method, which the
  warm-up absorbs.
- The process's CPU time per measured invocation (every thread's, the listener's included) is printed too. For a
  serial case it is a signal that a busy machine moves less than it moves wall-clock time, though cache and frequency
  effects still reach it: an early hint before an idle window, never a gate. Its resolution is the system's clock
  tick (15.6 ms on Windows), so it suits cases of a second or more. Instruction counts (BenchmarkDotNet's
  `--counters InstructionRetired`) would be steadier, but need the `BenchmarkDotNet.Diagnostics.Windows` package and
  administrator rights; the app does not reference the package, and whether the counters work with the in-process
  toolchain is untested.

Byte counts do not depend on the machine's load, so the probe needs no idle machine. They do depend on the runtime and
the collector, which are the process's own: run the net8.0 executable for .NET 8, and set `DOTNET_gcServer=0` for the
workstation collector.

## Comparing two builds

A change to the root's paths is gated by comparing the root benchmark set on the build with the change against a
baseline build, on the same idle machine; the bridge programs are compared the same way across the builds before and
after the store-backed root. This is the procedure, and the app's `--compare` mode applies its rules to the exported
reports.

### The builds

| Build | What it is | Project | Executable |
| --- | --- | --- | --- |
| `3a6e0b7` | The root baseline, from before colored contexts | `src/Eggnog.Relations.Benchmarks` | `Eggnog.Relations.Benchmarks.exe` |
| `673495d` | The bridge programs as first ported | `src/Eggnog.Relations.Benchmarks` | `Eggnog.Relations.Benchmarks.exe` |
| `2fd4a18` | The last build before the store-backed root | `src/Eggnog.Relations.Benchmarks` | `Eggnog.Relations.Benchmarks.exe` |
| `1.0.0` | The released build (tag `1.0.0`, `eda0c61`): a fixed baseline for later candidates | `src/Eggnog.Benchmarks` | `Eggnog.Benchmarks.exe` |
| `f2bfea2` and later, `main` | The candidate | `src/Eggnog.Benchmarks` | `Eggnog.Benchmarks.exe` |

The three older builds and `1.0.0` target net10.0 only, and none has `WritesBenchmarks.Shards` or the library
classes: compare them with `--absent Shards=0`. `3a6e0b7` has the root classes; `673495d` and `2fd4a18` add
`BridgeMathBenchmarks` and `BridgeAcBenchmarks`. Their root and bridge workloads match `main`'s at the defaults. The
sources differ only in comments, the bridge example's usage line, the bridge programs' constructor that takes a degree
of parallelism (every processor by default) and `WritesBenchmarks.Shards` (0, the older builds' layout, by default).

Build each in its own clone, in Release. The older trees have test paths longer than Windows' 260 characters, so let git
write them:

```
git -c core.longpaths=true clone --no-checkout <repository> ../bench-3a6e0b7
git -C ../bench-3a6e0b7 config core.longpaths true
git -C ../bench-3a6e0b7 checkout 3a6e0b7
dotnet build -c Release ../bench-3a6e0b7/src/Eggnog.Relations.Benchmarks
```

The released build checks out by its tag (`git checkout 1.0.0`) and builds `src/Eggnog.Benchmarks`.

### The machine

- Every build rolls forward to the newest installed .NET 10 runtime. Record `dotnet --list-runtimes` with the results,
  and install no update during the campaign.
- Close other applications (chat clients, browsers), and let no update, index or scan run. Select the power plan
  yourself: the app keeps the current one.
- Run every build with the same job options, and always with `--allStats`: BenchmarkDotNet writes the `Median` column,
  which the comparison reads, only when it judges it worth showing, and always under `--allStats`.
- BenchmarkDotNet separates the fields of its CSV reports with the current culture's list separator, a semicolon in
  many cultures, while its numbers stay in the invariant culture. The comparison reads either separator from each
  report's header.

### The method

- **One class per process.** Cases in one process share its heap, JIT and dynamic PGO state, so each class runs in its
  own launch.
- **Interleaved builds.** For each class, two builds run in the order baseline, candidate, candidate, baseline; three
  builds A, B, C run A B C C B A. That is one round. Run two rounds.
- **Layout.** Each launch writes its reports with `--artifacts <campaign>/<build>/r<round>-<slot>`, where the slot tells
  a round's runs of one build apart (`r1-0` and `r1-3` for the baseline, `r1-1` and `r1-2` for the candidate). The
  launches of every class in one slot can share the directory.
- **Launch matrix.** The root and bridge classes run on net10.0 under the server collector, as the baselines were built.
  The library classes run four ways: the net10.0 and net8.0 executables, each with and without `DOTNET_gcServer=0`.
- **An A/A control.** Before any candidate is gated, `main` runs against itself on `RuleSetRunBenchmarks` and the warm
  `math` example, for the spread of each configuration. No threshold is applied below twice that spread: a row whose
  threshold is not above it is reported as unresolved, within the four-round limit of the rules below.
- **An alignment control.** Every candidate that passes is run once more from a second build with an unrelated change.
  An effect that the rebuild alone reproduces or erases is reported as code alignment, not as a gain.
- **Profiles.** ETW kernel sampling, which needs administrator rights, not EventPipe.

In PowerShell, from the repository root, one round per pass of the outer loop:

```powershell
$exe = @{
    '3a6e0b7' = '..\bench-3a6e0b7\src\Eggnog.Relations.Benchmarks\bin\Release\net10.0\Eggnog.Relations.Benchmarks.exe'
    'main'    = 'src\Eggnog.Benchmarks\bin\Release\net10.0\Eggnog.Benchmarks.exe'
}
$order = '3a6e0b7', 'main', 'main', '3a6e0b7'
$classes = 'RuleSetRunBenchmarks', 'ConstrainedScanBenchmarks', 'IndexRefreshBenchmarks', 'DisplacedRebuildBenchmarks',
    'ActionLookupBenchmarks', 'WritesBenchmarks', 'UnionFindSerialBenchmarks', 'UnionFindConcurrentBenchmarks',
    'UnionFindWorkStealingBenchmarks', 'UnionFindFindOnlyBenchmarks', 'UnionFindFindOnlySerialBenchmarks'
foreach ($round in 1, 2) {
    foreach ($class in $classes) {
        for ($slot = 0; $slot -lt $order.Count; $slot++) {
            $build = $order[$slot]
            & $exe[$build] --filter "*.$class.*" --allStats --artifacts "campaign\$build\r$round-$slot"
        }
    }
}
```

### The rules

- **The statistic.** In each round, each build's figure for a case is the median of its runs' medians, and the ratio
  is the candidate's figure over the baseline's. The spread of a build in a round is its largest run median over its
  smallest, minus one; the round's spread is the larger of the two builds'.
- **Dropped rounds.** A round whose spread is above 1% is dropped: its same-build runs disagree, so its ratio says
  nothing. So is a round whose spread is unknown, because a build has one run in it or a run has no median for the
  case. The outcome is decided on the rounds left.
- **The gate.** A ratio of at most 1.02, on cases whose baseline median is 100 µs or more, over every run of the
  rounds in which every run has a median. Shorter cases are reported, not gated.
- **The outcomes.**

  | Outcome | When | Action |
  | --- | --- | --- |
  | `Pass` | At least two rounds are left, and the ratio is at most 1.02 in every one. | The candidate becomes the baseline for the case. |
  | `Failure` | At least two rounds are left, and the ratio is above 1.02 in every one. | A confirmed failure: a roadmap item naming the case. |
  | `Inconclusive` | Anything else, in fewer than four rounds: rounds left on both sides of the gate, or fewer than two rounds left because rounds were dropped for disagreeing runs. | Run another round. |
  | `Ungated` | Anything else, after four rounds, whatever was dropped. | Report it with its spread, leave it ungated, and run it again in the next window. |
  | `Reported` | The baseline median is under 100 µs. | Report the ratio; no gate. |
  | `NoMedian` | Fewer than two rounds left in fewer than four rounds, and a run has no median for the case: it failed, or its report has no `Median` column. | Run again, with `--allStats`. |
  | `NeedsRuns` | Fewer than two rounds left in fewer than four rounds, and only one round run, or a build with fewer than two runs in a round. | Run the rest of the method. |

### The comparison

```
src/Eggnog.Benchmarks/bin/Release/net10.0/Eggnog.Benchmarks.exe --compare campaign --baseline 3a6e0b7 --absent Shards=0
```

The mode reads every `*-report.csv` under each `<campaign>/<build>/r<round>-<slot>` directory. A case is its class
(without the namespace), its method and its parameter values; the job's columns are not part of it, so builds run with
the same options match whatever their toolchain or runtime columns say. For each build other than the baseline (or each
named with `--candidate <build>`), it prints:

- the case lists: the cases matched per class, every baseline case the candidate does not run (missing), every case the
  candidate adds to a class the baseline has (new), and the classes only the candidate has, which are not compared;
- one line per matched case: the baseline's and the candidate's medians over every run, the outcome, and each round's
  ratio and spread, as `r1 1.012 (0.4%)`, or `r1 1.012 (2.1%, dropped)` for a dropped round;
- the count of each outcome, and the rules applied.

`--absent <parameter>=<value>` reads a report that has no column for the parameter as if every case had that value:
`--absent Shards=0` matches the builds from before `WritesBenchmarks.Shards` with its cases of 0, and lists its cases
of 16 as new. `--gate <ratio>`, `--spread <fraction>`, `--min-time <time>` (for example `100us`) and `--rounds <n>` (the
most rounds) change the rules, for example to performance parity's thresholds. `--cases` stops after the case lists. The
exit code is 0 when every baseline case has a counterpart in every candidate, 1 when one has none, and 2 for a bad
command line or a report that does not parse.

### Checking the procedure without an idle machine

A dry run of every build shows that the builds run and that their case lists match, before an idle night is spent on
them:

```
..\bench-3a6e0b7\src\Eggnog.Relations.Benchmarks\bin\Release\net10.0\Eggnog.Relations.Benchmarks.exe --filter <the root classes> --job dry --allStats --artifacts dry\3a6e0b7\r1
src\Eggnog.Benchmarks\bin\Release\net10.0\Eggnog.Benchmarks.exe --filter * --job dry --allStats --artifacts dry\main\r1
src\Eggnog.Benchmarks\bin\Release\net10.0\Eggnog.Benchmarks.exe --compare dry --baseline 3a6e0b7 --absent Shards=0 --cases
```

`--compare` exits with 1 if a baseline case has no counterpart. Dry runs of `3a6e0b7`, `673495d`, `2fd4a18` and
`main` match: the 129 root cases at every build, and the two bridge cases from `673495d` on; `main` adds the nine cases
of `WritesBenchmarks.Shards = 16`.

### Selecting cases by parameter

BenchmarkDotNet's `--filter` also matches a case's full name with its parameters, as
`Namespace.Class.Method(Name: value, ...)`. On a build that has `WritesBenchmarks.Shards`, this runs only the writes
cases the baselines have, twelve of the class's 21:

```
--filter "*.WritesBenchmarks.*Shards: 0)"
```

A baseline has no such parameter, so the same filter selects nothing there: give it the class filter.

### The order and length of a campaign

The campaign has two quiet nights, which the owner schedules. The first measures what has no other source; the second,
the A/A control, one candidate and the newer benchmarks, in order of what their results unblock. The lengths are
estimates from the case counts and the dry runs, scaled to BenchmarkDotNet's default job (about 25 iterations for a case
of one invocation per iteration, about 15 s for a case that chooses its own invocation count) and checked against
earlier default-job runs of the writes and union-find classes. The dry runs were made on a busy machine, so the lengths
are rough.

| Night | Step | Comparison | Estimate |
| --- | --- | --- | --- |
| 1 | 1 | The root set, `3a6e0b7` against `main`: eleven classes, two rounds of baseline, candidate, candidate, baseline. One pass of the set is about 17-21 minutes. | 2.5-3 h |
| 1 | 2 | `BridgeMathBenchmarks` and `BridgeAcBenchmarks` at `673495d`, `2fd4a18` and `main`, A B C C B A, two rounds, `--warmupCount 2 --iterationCount 15`: about 2-2.6 minutes per `math` launch and 4.5-6.5 per `ac` launch. | 1.3-1.9 h |
| 1 | 3 | The example mode against the Rust examples, five pairs each, at every processor and at one thread (`--threads 1` against `RAYON_NUM_THREADS=1`); the library's `math-microbenchmark.egg` against `egglog -j 1`. | 0.5-0.8 h |
| 2 | 1 | An A/A control: `main` against itself on `RuleSetRunBenchmarks` and the warm `math` example, for the spread of each configuration. | 0.5-1 h |
| 2 | 2 | Each root-path candidate against `main` on the root set, as in step 1 of night 1. | 2.5-3 h each |
| 2 | 3 | The context classes (`ContextExtractionBenchmarks`, `PerChildCostBenchmarks`, `CaseSplitMemoryBenchmarks`, `ManyChildrenBenchmarks`, `ChildFindBenchmarks`), whose ratios are within one build, and `BusyParentCatchUpBenchmarks`, for its time per examined row: about 10 minutes a launch. | 0.5 h |
| 2 | 4 | The library classes on the launch matrix: four launches a class. | 0.5-1 h |
| 2 | 5 | `WritesBenchmarks --iterationCount 25` with both shard counts, against `cargo bench --bench writes`, two rounds. | 1-1.5 h |
| 2 | 6 | ETW profiles of `BasicMath`, `math` and a query child, as listed under "What the campaign times". | 0.5 h |

Night 1 is about 4.5-5.5 hours. Night 2 is about 5.5-7.5 hours with one candidate, so it holds one of the three
campaign branches. The other two, among them the three-build comparison of `campaign/basicmath-port`, the per-plan
profiles for kernel specialization, which wait until the staging-buffer share is settled, and any work left
inconclusive move to a later window, which the owner schedules too.

### What the campaign times

The gate each result decides is in the [roadmap](../../docs/roadmap.md).

- The root set against `3a6e0b7`, the baseline from before colored contexts. After the campaign the new baseline is
  `main`, exported and kept, and named in [The root benchmark set](#the-root-benchmark-set).
- `BridgeMathBenchmarks` and `BridgeAcBenchmarks` at `673495d`, `2fd4a18` and `main`; the bridge examples against the
  Rust's, five pairs each, at every processor and at one thread; `math-microbenchmark.egg` against `egglog -j 1`; and
  `WritesBenchmarks` with both shard counts against `cargo bench --bench writes`.
- The three campaign branches against `main` on the root set: `campaign/idle-rule-fix`; `campaign/basicmath-port`, as
  a three-build run with `3a6e0b7`, which also times `BridgeMathBenchmarks`, `BridgeAcBenchmarks`,
  `ManyChildrenBenchmarks`, `PerChildCostBenchmarks` and the transient children of `QueryScopeBenchmarks`, because every
  multi-layer child atom takes the port's layered paths; and `campaign/write-alloc`, under both collectors. Each is
  rebased onto `main` and its full suite rerun on both frameworks before it lands.
- The context classes and `BusyParentCatchUpBenchmarks`, for the time per child and per examined row.
- The library classes (`QueryScopeBenchmarks`, `LibraryRunOverheadBenchmarks`, `MathMicrobenchmarkBenchmarks`) on the
  launch matrix.
- ETW profiles of `BasicMath` at 200,000 rows on `3a6e0b7` and `main`, of `math`, and of a query child at 1,000 idle
  local rules.
- For kernel specialization, once the staging-buffer share is settled, warm per-plan ETW profiles, each giving every
  plan's share of run time: the bridge `math` and `ac`, `RuleSetRunBenchmarks.AcSaturation` and `BasicMath` at 200,000
  rows, `ActionLookupBenchmarks`, a library program heavy in integer primitives, and a loop of `PredicateTheory` query
  children under a frozen scope.
