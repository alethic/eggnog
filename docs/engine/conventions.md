# Eggnog.Relations engineering conventions

`Eggnog.Relations` is a C# reimplementation of egglog's relational engine: the Rust crates `core-relations`,
`union-find`, `numeric-id` and `concurrency`, the bridge `egglog-bridge` and the extractor (sources in the submodule
`ext/egglog`), plus colored contexts, which egglog does not have. It must be as fast as the Rust and faithful to its
semantics. These conventions bind every engine file; the library's files (`src/Eggnog` outside `Relations/`) follow the
layout and style rules below as well. How the engine is put together is in [`../architecture.md`](../architecture.md).

## Fidelity

- Port the algorithm, not just the idea. Read the Rust source for the module you port, in full, and keep its
  invariants, its fast paths and its complexity. Where egglog has a bug, fix it and say so in a comment citing the
  Rust file and line. Known defects are listed in the defect list ("egglog defects: what the port does instead" in
  [`../history/egglog-engine-map.md`](../history/egglog-engine-map.md)).
- Port the Rust unit tests for your module (`tests.rs`, `#[cfg(test)]` blocks) into xunit tests, and add property tests
  against a simple reference implementation where the module has nontrivial invariants.
- Do not invent features the Rust doesn't have, except colored contexts and what the library needs of them.

## Layout

- Engine code: `src/Eggnog/Relations/<Folder>/<Type>.cs`. Tests: `src/Eggnog.Tests/Relations/<Folder>/<Type>Tests.cs`.
- One namespace for the engine: `Eggnog.Relations`. Tests: `Eggnog.Relations.Tests`, in the library's test project
  (`Eggnog.Tests`); benchmarks: `Eggnog.Relations.Benchmarks`, in `src/Eggnog.Benchmarks/Relations/`. The test and
  benchmark folders keep the engine's namespace rather than their project's, so engine types resolve as they do in the
  engine and the library tests' `Relations.X` keeps naming the engine. The library's own benchmarks are in
  `src/Eggnog.Benchmarks/Library/`, namespace `Eggnog.Benchmarks.Library`, and use the public API, except the engine
  baseline of `LibraryRunOverheadBenchmarks`.
- **One type per file.** No nested types (including nested enumerators, comparers and private helper structs), no
  multi-type files. Partial classes across files are fine for large types.
- The engine is compiled into the `Eggnog` assembly (see "Packaging" in [`decisions.md`](decisions.md)). Every engine
  type is `internal`; only the library's own types in the namespace `Eggnog` are public. `InternalsVisibleTo` covers
  `Eggnog.Tests` and `Eggnog.Benchmarks`. Library code names engine types through the child namespace
  (`Relations.EGraph`) and never writes `using Eggnog.Relations;`: several engine and library types share simple names
  (`EGraph`, `CaseSet`, `Extractor`, `CostModel`), and the prefix shows at every use which one is meant.
- XML doc comments on every public and internal type and member: say what it is for, its invariants and its cost.

## Representation

- A table cell is a `uint` (`Value.Rep`). `Value.StaleRep` (`uint.MaxValue`) in a row's first cell marks the row
  deleted. Hot paths use raw `uint`; the `Value` struct is for API edges.
- A row id (egglog's `RowId`/`Offset`) is an `int` index into a table's row buffer.
- Identifiers are the record structs in `src/Eggnog/Relations/Ids` (`TableId`, `Variable`, `AtomId`, ...), all
  implementing `INumericId<T>`. Use `DenseIdMap<TKey,TValue>`, `IdVec<TKey,TValue>` and `FixedBitSet` from
  `Collections`.
- Hashing: `FxHasher` (egglog's rustc-hash 2.x), and `ShardData` for shard selection (bits below the top seven).
- Row-major buffers are `uint[]` with an arity: row `r` occupies `[r*arity, (r+1)*arity)`.

## Performance rules

- No allocation per row, per match or per probe on hot paths. Pool buffers (`ArrayPool<T>.Shared` for arrays, the
  engine's own pools where the Rust uses `Pooled<...>`), reuse scratch arrays, prefer structs.
- No interfaces, virtual calls or delegates in per-row loops. Use sealed classes, and generic methods constrained to
  struct type parameters (`where TVisitor : struct, IRowVisitor`) so the JIT specializes them, the way Rust
  monomorphizes closures. Interfaces are fine on cold paths (planning, setup).
- No `try`/`catch`/`finally` and no `foreach` over interfaces in hot methods. Keep hot methods small and mark tiny ones
  `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Hot loops may use `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add` to drop bounds checks, after one explicit
  range check, as egglog does with `get_unchecked`. Keep such code obviously correct and commented.
- SIMD (`Vector128/256`) where the Rust relies on hashbrown/SwissTable group probing or bulk compares.
- Spans, refs and ref structs are the default on hot paths. An overload over `uint[]`/`int[]` plus offsets exists only
  where an engine caller holds the data in an array (row storage read in place, the cells gathered for a lane, the
  join's dense bindings), and an overload nothing in the engine calls is deleted, not kept for later. A plain struct
  stands in for a ref struct only where it must be stored in fields (`SubsetRef`, the cursors).
- The interpreter is the only executor: nothing is generated or compiled at run time (no `System.Linq.Expressions`,
  no `Reflection.Emit`, no generic instantiation by reflection), so the engine needs no dynamic code. Specialization
  is closed-world C#: a fast path for a bounded set of shapes, written as a struct-constrained generic method or a
  dedicated kernel and chosen by one of the interpreter's switches (the stage kind in `JoinRun.RunPlan`, the
  instruction kind in `ExecutionState.RunInstr`, the probe `VectorizedLookup` picks once per batch). Every fast path
  is tested against the general path it shortcuts.
- Parallelism uses `Parallel.For`/the thread pool in place of rayon, with egglog's thresholds (table ops > 20000 rows,
  rebuild > 10000, database-level > 10000) and a configurable degree of parallelism instead of rayon's pool size.

## Style

- C# 13, nullable enabled, implicit usings disabled (write the `using`s), net8.0 and net10.0.
- 4-space indent, braces on new lines, a blank line after an opening type brace and before the closing one (see
  `src/Eggnog/Relations/Collections/DenseIdMap.cs`), `== false` rather than `!`, sealed classes, readonly structs.
- Exceptions: `ArgumentException`/`ArgumentOutOfRangeException` for bad input at API edges,
  `InvalidOperationException` for misuse; `Debug.Assert` for internal invariants the Rust `debug_assert`s.

## Building and testing

1. Build with `dotnet build src/Eggnog.Tests` (both target frameworks), or add `-f net10.0` for a quick check. The
   build must have no errors and no warnings.
2. Run the tests by executing `src/Eggnog.Tests/bin/Debug/<tfm>/Eggnog.Tests.exe`: with `-class <FullName>` for one
   class, `-namespace Eggnog.Relations.Tests` for the engine's, `-namespace "Eggnog.Tests.*"` for the library's, or no
   filter for all (10-15 minutes). Every test must pass on both net8.0 and net10.0.
3. The suite turns the engine's executor counters on for itself; set `EGGNOG_COUNTERS=0` to run it as production runs.
4. Work done in parallel happens in separate clones, so one change's half-finished state never breaks another's build.
   Keep a change within its own files; if it needs a change to a shared type, make that change on its own and say so.
5. Soaks are opt-in and skipped by default. `EGGNOG_CONCURRENT_SOAK=<seconds>` soaks the concurrency tests
   (`ConcurrentChildRulesTests.Soak`, `ConcurrentChildrenStorageTests.Soak` and the library's `ConcurrencyTests.Soak`):
   each iteration draws a seed, a child or query count, a thread count and the programs, and a failure prints the seed
   and its draws. `EGGNOG_CONCURRENT_SEED=<seed>` with a soak of one second replays that seed first; a race may need
   several replays to recur. The differential soaks have their own variables (`EGGNOG_DIFF_SOAK`,
   `EGGNOG_BRIDGE_DIFF_SOAK`, `EGGNOG_CONTEXT_SOAK`), named in each class's documentation. `EGGNOG_CHURN_SOAK`, set to
   any value, runs the 100,000-query churn soak of `ScopeChurnHeapTests`.

### Checking a change to a parallel kernel

The engine takes its parallel paths only above egglog's size thresholds (`ParallelHeuristics`): 10,000 rows for
database-level operations and rebuilds, 20,000 for table operations and index builds, more than 1,000 containers for an
operation within one container memo table, and more than one memo table for an operation across them. Small test
programs therefore run the serial table, index, rebuild and database-level code even at several threads; only the
container paths go parallel at small sizes, as soon as a program has two container memo tables. Each parallel path has
targeted tests above its threshold. A change that rewrites a parallel kernel (the table insert, delete or rehash, the
rebuild chunks, the index build, the rule-set executor's tasks) reruns the comparison below. If the new kernel lacks
targeted tests above its threshold, or if a mutation is caught only in eager mode, the eager switch is built
permanently, with a CI combination of its own.

1. **The eager switch**, in a private clone: one `static readonly bool` read from an environment variable, as
   `EGGNOG_COUNTERS` is read, and every `Parallelize*` method of `ParallelHeuristics` becomes
   `(eager || size > Threshold) && DegreeOfParallelism > 1`. `ParallelHeuristics.Serial` and every degree-1 path stay
   as they are: layer tables, a child's union-find table and temporary indexes rely on `Serial` meaning one shard.
2. **The test adjustments** under the switch. Merges that are not associative fold differently on the parallel path,
   as in egglog (`MergeFunction`): `DiffGenerator.RandomTable` keeps every `KeepIncoming` table single-sharded (it
   already does so under big loads), and `SortedWritesTableModelTests.MatchesTheFoldModel` skips its `keep-incoming`
   cases. The tests that assert the thresholds (`ConstraintTests.ParallelHeuristicsUseEgglogsThresholds`,
   `ContainerEnvTests.TheFullRebuildGoesParallelOnlyAboveTheIntraContainerThreshold`) are excluded, and so is
   `ScopeTreeTests.ScopeOptionsReachTheContext` if graphs default to more than one thread.
3. **The mutations**, each in the parallel branch only and switched on by an environment variable of its own:

   | Mutation | Site |
   |---|---|
   | Keep the incoming row and drop the merge output (egglog defect 3) | `SortedWritesTable.MergeAppendedRows` |
   | Report a change for every staged row (egglog defect 4) | `SortedWritesTable.MergeAppendedRows` |
   | Merge a round's tables as one stratum (egglog defect 11) | `Database.MergeRound` |
   | Skip the cross-shard sort check | `SortedWritesTable.CheckParallelSortValues` |
   | Do not notify a table that the operation changed | `Database.RunOnTablesParallel` |
   | Drop the last row of the last batch of a parallel tuple-index build | `TupleIndex.SplitBatch` |
   | Skip the last row of the last chunk of a parallel full rebuild | `SortedWritesTable.RebuildFullParallel` |
   | Use the serial incremental-rebuild ratio on the parallel path | the rebuild decision in `SortedWritesTable.Rebuild.cs` |

   Add mutations of the same kinds for the rewritten kernel.
4. **The runs**: the whole suite twice per mutation, with `-stopOnFail` so that the first failing test is recorded,
   once with default settings and once with the eager switch on. With no mutation the eager run must pass.

Against the current kernels, both runs catch each of the first seven mutations, and eager mode catches none that the
default suite misses; for two of them (the missed notification and the skipped rebuild chunk) it fails on small
programs where the default suite needs a bulk program. Neither catches the last one, which only chooses between two
paths that stage the same writes: a performance choice, which only a unit test of the decision itself could catch.

The concurrent soaks have mutations of their own: removing the lock in `ContextTables.CreateLayer`, so that the tasks
of one child's run can create a table's layer twice, and removing the second read of the root's frozen flag in
`Store.AddTable`, so that a table defined while the root freezes can stay unfrozen in a frozen root. The default suite
catches both, in `LayerCreationConcurrencyTests` and `StoreTests.ATableAddedWhileTheRootFreezesIsFrozenOrRefused`. The
soaks of `ConcurrentChildRulesTests` and `ConcurrencyTests` catch the first within a few dozen iterations. Only the soak
of `ConcurrentChildRulesTests` reaches the second, which needs the defining thread to be preempted for the whole of a
freeze, so a one-minute soak catches it in some runs and not in others; soak longer to rely on it. The rules soak
creates layers concurrently only in children of two or more threads whose rule sets are forced onto the parallel
executor; `ChildParallelRuleSetTests.AForcedChildRunsItsRulesOnTwoThreadsAtOnce` fails if a child ignores the force,
which would otherwise leave every concurrent test green and the soak without that reach.
