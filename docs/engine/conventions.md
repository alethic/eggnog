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
  engine and the library tests' `Relations.X` keeps naming the engine.
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
