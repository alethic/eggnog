# Eggnog.Relations engineering conventions

`Eggnog.Relations` is a C# reimplementation of egglog's relational engine: the Rust crates `core-relations`,
`union-find`, `numeric-id` and `concurrency` (sources in `D:\eggnog\ext\egglog`), plus colored contexts, which egglog
does not have. It must be as fast as the Rust and faithful to its semantics. These conventions bind every file.

## Fidelity

- Port the algorithm, not just the idea. Read the Rust source for the module you port, in full, and keep its
  invariants, its fast paths and its complexity. Where egglog has a bug that the analysis notes call out, fix it and
  say so in a comment citing the Rust file and line.
- Port the Rust unit tests for your module (`tests.rs`, `#[cfg(test)]` blocks) into xunit tests, and add property tests
  against a simple reference implementation where the module has nontrivial invariants.
- Do not invent features the Rust doesn't have, except where these conventions or the design say so (colors).

## Layout

- Engine code: `src/Eggnog.Relations/<Folder>/<Type>.cs`. Tests: `src/Eggnog.Relations.Tests/<Folder>/<Type>Tests.cs`.
- One namespace for the engine: `Eggnog.Relations`. Tests: `Eggnog.Relations.Tests`.
- **One type per file.** No nested types (including nested enumerators, comparers and private helper structs), no
  multi-type files. Partial classes across files are fine for large types.
- Default to `internal`. Make a type `public` only when a consumer of the engine needs it. `InternalsVisibleTo` covers
  the test, benchmark and `Eggnog` projects.
- XML doc comments on every public and internal type and member: say what it is for, its invariants and its cost.

## Representation

- A table cell is a `uint` (`Value.Rep`). `Value.StaleRep` (`uint.MaxValue`) in a row's first cell marks the row
  deleted. Hot paths use raw `uint`; the `Value` struct is for API edges.
- A row id (egglog's `RowId`/`Offset`) is an `int` index into a table's row buffer.
- Identifiers are the record structs in `src/Eggnog.Relations/Ids` (`TableId`, `Variable`, `AtomId`, ...), all implementing
  `INumericId<T>`. Use `DenseIdMap<TKey,TValue>`, `IdVec<TKey,TValue>` and `FixedBitSet` from `Collections`.
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
- Plans will later be compiled to delegates with System.Linq.Expressions, which cannot use `Span<T>` or `ref struct`s.
  So every primitive a plan calls on a hot path also needs an overload over `uint[]`/`int[]` plus offsets, and must
  be a non-virtual method on a sealed type.
- Parallelism uses `Parallel.For`/the thread pool in place of rayon, with egglog's thresholds (table ops > 20000 rows,
  rebuild > 10000, database-level > 10000) and a configurable degree of parallelism instead of rayon's pool size.

## Style

- C# 13, nullable enabled, implicit usings disabled (write the `using`s), net8.0 and net10.0.
- 4-space indent, braces on new lines, a blank line after an opening type brace and before the closing one (see
  `src/Eggnog.Relations/Collections/DenseIdMap.cs`), `== false` rather than `!`, sealed classes, readonly structs.
- Exceptions: `ArgumentException`/`ArgumentOutOfRangeException` for bad input at API edges,
  `InvalidOperationException` for misuse; `Debug.Assert` for internal invariants the Rust `debug_assert`s.

## Building and testing in isolation

Agents work in a private sandbox so parallel work never breaks each other's build:

1. Copy the repository's build files and both engine projects into your sandbox (see the workflow prompt for the
   exact command).
2. Add only new files, in your module's folders. Never edit a file another module owns. If you need a change to a
   shared type, implement around it and report the change you need.
3. Build with `dotnet build src/Eggnog.Relations.Tests -f net10.0` and run the tests by executing
   `src/Eggnog.Relations.Tests/bin/Debug/net10.0/Eggnog.Relations.Tests.exe` (add `-class <FullName>` to filter).
   The build must have no errors and no warnings in your files, and every test must pass.
4. Report the list of files you created, relative to the repository root.
