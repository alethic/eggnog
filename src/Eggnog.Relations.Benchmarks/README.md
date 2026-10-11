# Eggnog.Relations.Benchmarks

BenchmarkDotNet ports of egglog's divan benchmarks for the relational engine, so the C# engine can be measured against
the Rust it reimplements on the same workloads.

## Running

Always run in Release, from the repository root:

```
dotnet run -c Release --project src/Eggnog.Relations.Benchmarks -- --filter *
```

Narrow the run with a filter on `Namespace.Class.Method`, for example `--filter *WritesBenchmarks*` or
`--filter *UnionFindConcurrentBenchmarks.Concurrent`. Other useful BenchmarkDotNet options:

- `--list flat` lists the benchmarks without running them.
- `--job short` runs fewer iterations; `--job dry` runs each case once, which only proves that it works.
- `--iterationCount 25` matches divan's `sample_count = 25` of the `writes` benches (divan's default is 100 and the
  union-find groups use 50).
- `--artifacts <dir>` puts the reports somewhere other than `BenchmarkDotNet.Artifacts`.

Results are only meaningful on an otherwise idle machine. The configuration keeps the machine's current power plan
(BenchmarkDotNet would otherwise switch Windows to High performance for the run); select that plan yourself before
measuring if you want it.

Every summary has two extra columns: `Items`, the number of items one invocation processes (rows staged, union-find
operations applied, identifiers found), and `Items/s`, that count divided by the mean time. This is what divan prints
for a bench with an `ItemsCount` counter, so `Items/s` compares directly with divan's throughput figures. The memory
diagnoser is on for every class.

Each benchmark processes its whole workload in one invocation: a fresh table or union-find is built before every
invocation (`[IterationSetup]`, `[InvocationCount(1)]`), as divan's `with_inputs` builds one per sample. Each workload
is generated once per parameter set from a fixed seed, where egglog draws a new one from an OS-seeded generator; the
algorithms and distributions are egglog's. After each iteration the result is checked against a reference model (the
table's live row count; every identifier's root), so a broken engine fails the run instead of producing a number.

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
