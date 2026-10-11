# Colored contexts in Eggnog.Relations: integrated implementation spec (M9–M10)

**Code baseline:** worktree `project-overview-1f1e82` at `e65f4f4`. The bridge (M8) is being ported into
`src/Eggnog.Relations/Bridge` and `Extraction`; §10 names its types as egglog-bridge's.

**Paths.** Engine paths are relative to `src/Eggnog.Relations/`. Test paths are relative to `src/Eggnog.Relations.Tests/` and benchmark paths to `src/Eggnog.Relations.Benchmarks/`.

**Binding inputs:**
- `docs/design/egglog-engine-map.md`: §0, §1.2–1.3, §4 (C1–C14), §6 (M9, M10) and §8.2;
- `docs/engine/decisions.md`: decisions 1–12 and L1–L7;
- `docs/design/colored-egraph-literature.md`, including its review corrections;
- `docs/engine/conventions.md`;
- the spike: `src/Eggnog.Storage`, plus its `Reference/`, `Harness/` and `DifferentialTests.cs`.

`docs/design/rule-layer-spec.md` is the consumer of this work. It is cited where its decisions (D3, D4, D6, D11, D16, D18, D20, D21) and its invariant C constrain the engine.

**Conventions** (`conventions.md`): one type per file, no nested types, `internal` by default, namespace `Eggnog.Relations`, XML docs, `== false`, sealed classes, and struct-constrained generics on hot paths.

**Labels used in this document:**
- Conflict resolutions are numbered **X1–X43** (§2).
- Invariants are numbered **I1–I35** (§12).
- Milestones are **M9.0–M9.4** and **M10.1–M10.4** (§17).

**Revision.** This revision answers two design reviews. §20 maps every finding to the section that fixes it.

---

## 1. The design in brief

1. **Each context owns one `Database`.**
   - The root's `Database` is egglog's, unchanged.
   - A child's `Database` has its own catalog, notification list, pending queues, merge, index catalogs and size estimate.
   - It shares the store-wide `CounterSet`, `ExternalFunctionRegistry`, `DependencyGraph` and `ContainerValues` by reference.
   - Staging, `UnionIdMerge` and `Database.MergeAll` therefore bind to the right context through the catalog. This is C4's "ExecutionState bound to the child", and C9's ownership list.
   - A child's `Database` answers schema questions from the store schema and planning and header questions from its `ContextView`, so rules are built, planned and run in a child exactly as at the root (X39).
2. **Clock and ids (C1).**
   - The store has one id counter (`CounterId 0`) and one clock (`CounterId 1`, starting at 1).
   - A context captures an epoch at the start of each rule run, each rebuild pass and each write batch.
   - Every write of that run, pass or batch is stamped with it. `ExecutionState.ReadCounter(clock)` returns the state's `Timestamp`.
   - The clock is incremented at the end of the run, pass or batch.
   - Children mint ids from blocks reserved on the shared counter, which makes `IsLocalId` exact.
3. **Union-find (C2).**
   - The root keeps `DisplacedTable` and compresses at each merge and at freeze.
   - A child keeps:
     - a `UnionFindOverlay`, a class table over parent-canonical ids that records each class's member list (L2, B7);
     - a `DisplacedJournal`: its own losers, re-key losers and imported parent displacements.
   - `find_C = overlay_C ∘ find_P`.
   - Catch-up re-keys eagerly, through one cursor into the parent's journal.
4. **Tables (C3, C4, C14).**
   - A child creates its layer of a table lazily, on its first write or hide. The layer is an ordinary single-shard `SortedWritesTable` in the child's catalog.
   - Layer creation may happen on a rule task. It runs under a per-context lock and publishes copy-on-write. An empty new layer changes no view, so nothing is invalidated during a run (X37).
   - Ancestor rows are hidden through per-(context, table, ancestor layer) `HiddenSet`s. Every layered `MergeAll` seals the sets it touched before returning (X40).
   - Lookups probe the layers from the top down, and the first layer that holds the key decides (Lemma F).
   - `SortedWritesTable.Merge` dispatches to a layered merge, so a child reuses `Database.MergeAll` unchanged. The layered merge canonicalizes each incoming row's id cells and stamps its sort cell with the merge's epoch (X38).
5. **Rebuild and catch-up (C5, C8).** `Context.Rebuild()` is the single loop at every level. In a child, each pass:
   1. re-keys the union-find (first pass only);
   2. absorbs ancestor appends since its row cursors (first pass only);
   3. rewrites its own rows with egglog's incremental `ApplyRebuild` against the child's union-find table, through `Database.RunWithTakenOut`, which notifies every rewritten layer;
   4. hides and copies the ancestor rows that mention its *own* new losers, found through the ancestors' rebuild indexes;
   5. merges.

   Hides are only ever added, and only for losers (decision 3). Before the root is shared (a child created from it, or a freeze), it settles: rows written since its last rebuild are canonicalized (X38).
6. **Freeze, dispose and threads (C9).**
   - `Freeze` requires a frozen parent. It rebuilds (and settles at the root), compacts (gated), builds rebuild indexes and plan indexes, seals staging, compresses or flattens the union-find, builds every view, and publishes.
   - A context that is not frozen shares one `ThreadAffinityGuard` "thread domain" with the descendants created under it. Each child of a frozen context gets its own domain.
7. **Retraction and compaction (C10, C12).**
   - Decision 1 applies, plus refinement **R1**: in a child, a point delete or clear of a shared table needs a frozen parent.
   - A shared layer never compacts while it has live descendants.
8. **Query execution (C3, C6).**
   - A run sees `TableView`s: one `LayerView` per present layer, each holding the layer's own `TableInfo` catalogs and at most two hidden sets. A `RuleSet` snapshots the views it reads when it is built, and the search reads only that snapshot.
   - Subsets become `LayeredSubset`s. The enumerating sides of the kernels iterate per layer, and the probed sides join their hits across layers.
   - Visibility is checked on every path that yields rows.
   - A root run touches exactly one layer and allocates nothing more than today.
9. **Rules (C7).**
   - Watermarks are kept per (rule, context), with the executed Id constants, in the bridge's `EGraphContext`.
   - Every Id constant of a rule, in its query or its actions, runs in its canonical form in the context that runs it, the root included (X19).
   - Watermarks are seeded lazily as `min(W_a, CreatedAt(a's child on the path))`, refused when the constants differ (D3, D4), and refused for rules whose actions read tables (X41).
   - A watermark commits after a successful run (D18).
10. **Lift (L1, L2, L4, L5).** The storage primitives are `ScanOwn`, `ScanMerges`, `IsLocalId` and `ScanAncestorsMentioning` (the L1 winner probe). On top of them sit `MemberMap`, the partition meet, `Lifter`, `CaseSet` (which drops and disposes vacuous cases) and `SplitDriver` (demand filter and lift-before-deepening).
11. **What the root pays.**
    - A handful of per-call, per-batch, per-probe and per-refinement branches, all listed in §14.1. The only one a hot loop can reach per row is the call-level branch in `PredictRow`/`PredictColumn`, which bridge merge functions call once per merged row.
    - Freeze-time and child-creation work (settling, §7.6).
    - One cheap compression per merge that no result can observe.

    Nothing is added per scanned row, per staged row or per find.

---

## 2. Conflicts between the area specs, and how they are resolved

Abbreviations: UF = unionfind-ids, TB = tables-merge-rebuild, QE = query-execution, BR = bridge-concurrency-lift.

| # | Conflict | Decision | Why |
|---|---|---|---|
| X1 | **Where child state lives.** UF: a `Database` per context. TB: `ContextTables` plus `LayeredSchema`, staging through a resolver. BR: `ContextTable : Table` wrappers. QE: `ILayerSource` and `ContextRunState`. | One `Database` per context (UF). Its catalog holds own-layer `SortedWritesTable`s, created lazily by `TableCatalog.Resolver` → `ContextTables.ResolveForStaging` under the context's layer lock (X37), plus the union-find layer at `TableId 0`. `ContextTables` (TB) is the child's layering engine, reachable as `Database.Layers`. No `ContextTable` wrapper. `ContextRunState` is dropped: the view lives on `Database.View` and watermarks live in the bridge (X17). | A wrapper would have to re-implement every abstract `Table` member (`RefineOne`, `TryFastSubset`, scans and so on) over layers, while the executor works per layer anyway (C3). A database per context reuses `MergeAll`, the take-out guard, failure recording and index resets as they are. |
| X2 | **Who merges a child.** TB: its own `ContextTables.MergeAll`. BR: `database.MergeAll`. | `Database.MergeAll`, unchanged in structure. `SortedWritesTable.Merge` calls `MergeLayered` when its `layer` field is set (one null test per table merge). A layered database never takes the parallel-stratum path. `TakeRound` first adopts layers created during a search (X37). The round loop stops when `Owner.IsContradicted` (L4). The `finally` epilogue seals hidden sets and invalidates the views whose structure changed (X40). | No duplicated fixpoint; stratum order is preserved (§8.1 row 11). The same-stratum read guard comes from take-out: TB's `Merging` flag is replaced by `Database.IsTakenOut`. |
| X3 | **Epoch pin.** UF: a pin on `Database` (`PinClock`, `ReadCounterInView`). BR: `ExecutionState.ReadCounter` returns `Timestamp`. TB: `IEpochClock.Advance(e)` = `max(e+1)`. | BR's stateless pin: `ExecutionState.ReadCounter(c)` returns `Timestamp` when `c` is the store clock. `Context.BeginEpoch()` reads the clock; `Context.EndEpoch()` increments it (egglog's `inc_ts`). `IEpochClock` is dropped. Database write entry points take `uint?` and resolve `null` to the live clock. | Every state of a run, pass or batch already carries the timestamp (`RunRuleSet`, `MergeAll`, `MergeTable` and `CreateExecutionState` take it, and `Clone` copies it). This needs no shared mutable pin, and increment keeps the root's numbering identical to egglog's. |
| X4 | **Run → rebuild epoch.** UF: `EndEpoch` before `Rebuild`, so the first pass uses T+1. BR: the first pass reads the clock again, which gives T at the root. | BR's: after a run that grew the union-find, the clock is not advanced; `Rebuild`'s passes do their own begin/end. Per-context epochs are **nondecreasing**, not strictly increasing (I3). | This is egglog parity (`eb/lib.rs:592-624`, `667-676`). The sort columns and the journal only need nondecreasing stamps. |
| X5 | **Local ids.** UF: `IdBlockAllocator`. BR: `LocalIdLog`. | `IdBlockAllocator`. | Exact `Contains` in O(log blocks), no per-id memory, a lock-free fast path, and bounded waste. |
| X6 | **The child union-find API** assumed by TB (`ContextUnionFind`) and BR (`ContextUnionFindTable`). | UF's types, with this mapping: `FindNaive`/`Find` → `Context.UnionFind.Find`; `IsDisplacedHere(x)` (x P-canonical) → `UnionFind.Find(x) != x`; `Children`/`DisplacedAt` → `DisplacedJournal`; `LogTable` → `OverlayDisplacedTable`; `GetRebuilder` → `OverlayCanonicalizer`; `Rekey` → `Context.CatchUpUnionFind`; `ForEachOverlayEntry`/`FindInOverlay` → `UnionFindOverlay` slots; `OverlayVersion` → new `UnionFindOverlay.Version`; `PrepareForSharing` → `Table.Freeze` + `FlattenedUnionFind`. | A class table is one probe per level, never writes on `find`, and keeps member lists (L2). |
| X7 | **Union-find catch-up cursors.** TB: one per ancestor log. UF: one into the parent's journal. | One cursor into the parent's journal (`unionFindCursor`). `AncestorCursor` keeps row cursors only. | The parent's journal re-exports every displacement that is visible at the parent, its imports included. Reading ancestors' logs directly would revisit ids that are already non-canonical in P. This narrows C8 for the union-find only. |
| X8 | **Delta ids for a pass.** TB: `pendingAncestorDisplaced ∪ ownLosers`. UF: journal rows plus `IsOwnDisplacement`. | Changed ids = journal rows `[journalMark, Count)`; own losers = those with `IsOwnDisplacement`. TB's `pendingAncestorDisplaced` is dropped. | The journal already contains imports and re-key losers. Step 4 needs own losers only, because ancestor rows that mention an import are already invisible at P (I13). |
| X9 | **Step 3 (own rows).** TB: a new `RebuildRowsMentioning`. UF: the existing `ApplyRebuild` against the journal table. | The existing `SortedWritesTable.ApplyRebuild`, run through `Database.RunWithTakenOut(TableId 0, own layers, new RebuildTableOperation(TableId 0, overlayTable, e))` (`Database/Database.Rebuild.cs`, `Database/RebuildTableOperation.cs`). `RunOnTablesCore` notifies each layer whose rebuild staged something and records failures. A layer takes the incremental path whenever its delta is no larger than its row count (§7.3). | It is egglog's incremental path verbatim: `SubsetTracker.RecentUpdates` reads the journal suffix, `RebuildIncremental` probes through hint column 0, and it includes the rows-since-mark fix (`FirstRowSinceRebuild`). Calling `ApplyRebuild` directly would stage through the table's own `NewBuffer()` without notifying the catalog, so `MergeAll` would never merge the rewrites. |
| X10 | **Three rebuild loops** (UF, TB, BR). | One: `Context.Rebuild()` (§7.1), which covers catch-up, container refresh and the eq guard. `EGraphContext.Rebuild` wraps it. | A single code path for root and child gives one place to keep root parity. |
| X11 | **Contradiction.** TB: an unconditional sticky flag. BR: `ContextOptions.StopOnContradiction`, an early-stop external and a `Stopped` state. | The storage flag `Context.IsContradicted` is set only when `Options.StopOnContradiction` is true (cases, D11). BR's `StopOnContradictionFunction` is kept to cut the search short. BR's `contradictionStaged` is dropped, because the merge sets the flag. | D11: contradiction is an ordinary fact for host runs. The early stop is still worth having in cases (L4, G4). |
| X12 | **Exceptions in a child.** TB: the context becomes faulted. BR: "the next Rebuild recovers". | A child is **faulted** when an exception escapes a `Write`, rule run, `Rebuild` or `Freeze`; only `Dispose` is then allowed. The root keeps `Database`'s existing guarantee. | A hide whose copy was never merged cannot be repaired: the re-run's dedupe skips the copy. This refines the guidance in rule-layer D18 for child scopes: dispose them (§19). |
| X13 | **Freeze hooks.** UF: `DisplacedTable.Freeze`. BR: `PrepareForSharing`/`Seal`. QE: `PrebuildIndexes`. BR: `PlanTemplate.ForEachIndexKey`. | One virtual `Table.Freeze()` (compact if ungated, build the rebuild index when the table has rebuild columns, compress the union-find, seal pending). `Database.Freeze(FreezeWarmup?)` warms plan indexes collected by `PlanIndexKeys.Collect`. | One mechanism, one place. |
| X14 | **Lazy index builds on frozen catalogs.** QE: parallel is fine. BR: serial. | Serial (`IndexCatalog.SetSerialRefresh`). | Concurrent children are serial by default (C9 budget). Map §5.5 forbids builds under a once-cell that block on work the waiter would have to run. Prebuilding at freeze makes these builds rare. |
| X15 | **Thread guard.** UF: `busyThread`. BR: `ThreadAffinityGuard` per domain. | `ThreadAffinityGuard`, shared by a thread domain, owned by `Context.Domain`. | It expresses "a non-frozen context and its descendants are one thread" directly, and freezing reassigns domains (§8.3). |
| X16 | **Schema after children.** UF: sealed at the first child. TB: arrays grow on demand. BR: throws while anything runs. The spike and rule-layer D6 allow definitions at any time. | `Store.AddTable` is allowed at any time, under `Store.DefinitionLock`, published copy-on-write. `DependencyGraph.LevelOf` reads a volatile snapshot. Arrays of non-frozen contexts grow on their owner's thread (copy-on-write under the layer lock); frozen contexts never grow (X43). `Store.AddCounter` throws once any child has been created. `ExternalFunctionRegistry` becomes copy-on-write. | The spike's `DefineTable` and D6 require it. A new counter would reallocate `CounterSet.values` under concurrent `Interlocked` operations, and nothing needs one later. |
| X17 | **Watermark state.** QE: `SemiNaiveProgress`, `WatermarkInheritance` and a static `Disabled`. BR: `RuleProgress` with generations. | BR's `RuleProgress` in `EGraphContext`, plus `ContextOptions.InheritWatermarks` (per context, not static). QE's proof is kept as I31. | Rules are a bridge concept: `RuleId` reuse needs generations, and a static switch breaks parallel tests. |
| X18 | **Commit point.** BR: at instantiation (egglog). QE and D18: after success. | Progress is staged at instantiation and committed when `RunRuleSet` returns with no recorded failure (`Database.TakeFailure() == null`). It is not delayed until after the rebuild. | Every row the run writes has `ts ≥ T`, so committing before the rebuild is sound (I31). This differs from egglog only on the panic path, where egglog's results are partial anyway. |
| X19 | **Id constants** (revised). QE: the bridge passes a canonicalizer. BR and the `Query/IdConstant.cs` remarks: as written at the root. | **One semantics at every level.** A Store-backed context, the root included, runs every Id constant of a rule (query constants, `IdConstant`, and action constants, `ActionIdConstant`) in its canonical form there: K = `Find_V(c)`. A bare `Database` (no `Store`) has no designated union-find and runs them as written, as egglog does. | Context = copy: a child that asserts nothing must see what its parent sees (§20, R1-3). Rule-layer invariant C canonicalizes constants per run. The root differs from egglog only when a constant has lost a union, where egglog's cached plan silently stops matching; this is proposed as a new §8.1 defect row (§19). |
| X20 | **Two `TableView`s (TB `Layers/`, QE `Execution/`), `HiddenFilter` vs `HiddenRows`, `HiddenSet` location and API, `VisibleRowFilter` vs `VisibleFilter`, `HiddenSkippingSink` vs `HiddenFilterSink`.** | One `Layers/TableView` holding `LayerView?[]` (QE's shape) plus TB's C4 lookup partial. `HiddenRows(Frozen, Live)`: a cleared level makes the layers below it absent (QE), not `HidesAll`. One `Layers/HiddenSet` (TB's representation plus QE's M12 accessors). `VisibleRowFilter` (TB, with merge-walk cursor). `HiddenSkippingSink` (TB). | Removes duplication. Two probes per row at most. A frozen prefix collapses into one set (§6.3). |
| X21 | **View source and invalidation** (revised). QE: polls `GetViewVersion(d)`. TB: `InvalidateViews`. | Storage builds and caches `TableView`s (`ContextTables.GetView`). Only the `MergeAll` epilogue, `Rebuild`, `ClearTable` and `Freeze` invalidate views and bump `ViewGeneration`; layer creation never does (X37). `RuleSetBuilder.Build` snapshots the views of every table its rules read (`ViewSnapshot`), records `ViewGeneration`, and `RunRuleSet` asserts it unchanged. QE's `ILayerSource` shrinks to `ILayerViewSource` (§9.1). | One owner of view state; the search never reads the cache, and synthetic layers stay testable. |
| X22 | **QE's `RunRuleSetCore`** vs a database per context. | `Database.RunRuleSet` keeps its shape. `JoinRun` and `RuleWork` take the rule set's `ViewSnapshot`. The idle `JoinRun` stays per `Database`. | Each context already has its own `Database`. |
| X23 | **`$uf` as an atom in a child.** QE supports it; UF: never. | Unsupported: `ContextView.GetTableView($uf)` and `IQuerySchema.SplitConstraints($uf)` throw `NotSupportedException` in a child. | Decision 10 drops the rule-based rebuild. Only `lookup_uf` reaches a child's `$uf`. |
| X24 | **BR's long-lived host buffers** (`database.NewBuffer` notifies before flush; HostEpoch stamped at staging). | `HostWrites` keeps logical rows bridge-side. `FlushUpdates` replays them into a state stamped with the flush epoch, after catch-up. | A `MergeAll` between `NewBuffer` and flush would consume the notification. Stamping at flush gives S3 by construction (I21). At the root the values are equal to egglog's, because `AddValues` and `AddTerm` flush immediately. |
| X25 | **Lookup producers** (BR `ContextKey*`, TB `LayeredKey*`) and new table kinds. | TB's `LayeredKey1/2/3/NLookup` over `TableView`, plus UF's `OverlayDisplacedLookup`. `TableKind` gains only `OverlayDisplaced`. | Follows X1. |
| X26 | **Child work measure.** BR: virtual `Table.WorkSize`. | Dropped. A child `Database.TotalSizeEstimate` sums its own layers only. | C9's "measure by delta" comes free from X1. |
| X27 | **Compaction gate.** C12: "cursors behind or hidden sets". TB: never with live descendants. | TB's stricter gate; the deviation is documented. | Every compacting merge also appends, so descendants are behind anyway. The strict gate avoids translating cursors and hidden sets. `remap` stays the upgrade path. |
| X28 | **Retraction under a writable parent.** | R1 is adopted: a child's point delete or clear of a shared table needs a frozen parent. It is reported to the decisions owner (§19). | Counterexample (TB): a parent re-asserts `f(x)` after the child deleted it. Replay semantics put the parent's assertion first, but per-row hides cannot express that. |
| X29 | **Member lists.** UF: maintained in the overlay. BR: derived from overlay keys and cached. | Maintained in `UnionFindOverlay` (UF, B7). `MemberMap` (BR) is a sorted, local-filtered snapshot of the overlay's slots, cached by `UnionFindOverlay.Version`. | Exact, O(group), with no derivation pass. |
| X30 | **Change stamp.** TB: `ChangeStamp`. BR: `version`. D16: `Version`. | `Database.ChangeStamp` is the single counter. It is bumped on `TableChange.Added \|\| Removed` in `MergeOne`/`MergeTable`, on effective `ClearTable`, on hides and on the stop flag. `Context.Version` and `EGraphContext.Version` read it. | D16's semantics: no-ops and redundant unions do not move it. |
| X31 | **Containers.** BR: dirty log on the root `Database`, eq guard over every displaced id. UF: `NewestUnionTimestamp` arm. | Container ids are minted with `IncrementSharedCounter`. `ContainerDirtyLog` lives on `Store`. The eq guard checks a pass's **own** displacements only, inside `Context.Rebuild`. UF's `NewestUnionTimestamp` arm is dropped. | Imports were already renamed by the root's container rebuild. Children never call `RebuildContainers`. |
| X32 | **Duplicate tests and harnesses.** `ContextFacade`/`SpikeContextFacade`, `ContextPair`/`Pair`, `ReferenceContextChain`, `CanonicalViewText`/`TermNamer`, two classes named `ContextCopyPropertyTests`, and name clashes with `Eggnog.Storage` and with `Eggnog.Relations.Tests.ReferenceView` (`Database/ReferenceView.cs`). | Consolidated in §17 under `Contexts/Spike/`: `SpikeContextFacade`, `SpikePair`, `SpikeTestSchema`, `SpikeReferenceContext` (with ordered `SpikeReferenceOp`), `SpikeReferenceView`; plus `Colors/ReferenceRuleContext`, `Colors/TermNamer`, `Contexts/ContextCopyTests` (storage) and `Colors/ContextRuleCopyTests` (rules). Files that touch the spike's own types write `using Spike = Eggnog.Storage;` and never `using Eggnog.Storage;`. | One type per file; no CS0101/CS0104 clashes with the internal `Eggnog.Relations.Store`, `Context`, `Table` and `MergeFunction`. |
| X33 | **Root performance gate** (revised). UF: noise. TB: 1%. QE: 2%. | ≤ 2% against the root benchmark set recorded in M9.0, before any engine change. Each milestone is gated on the benchmark classes that exercise the code it changes (§17, M9.0 table). Plus `ExecutorAllocationProbeTests` unchanged. | BenchmarkDotNet noise makes 1% unreliable. The allocation probe is exact. The benchmarks that exist today (`Writes/`, `UnionFind/`) cover none of scans, indexes, the executor or `DisplacedTable` rebuilds. |
| X34 | **BR's `MarkFrozen`** stops overlay path compression. | Not needed. | The class table never writes on `find`. |
| X35 | **TB's `ContextTables.ClearTable` / `ScanOwn` etc.** vs BR's `EGraphContext` copies. | Storage exposes them on `Context`; the bridge wraps them by `FunctionId`. | One implementation. |
| X36 | **Depth.** QE: ≤ 31. Others: unbounded. | `CreateChild` throws `InvalidOperationException` at depth 32. | `LayeredSubset` uses a `uint` layer mask. |
| X37 | **Layer creation off the domain thread** (R1-2, R2-2). Lazy creation from `ExecutionState.CreateBuffer` ran on rule tasks and action-batch clones (`Execution/ActionBuffer.cs`), mutated `DenseIdMap`s without a lock, and bumped `ViewGeneration` mid-run. Reviewers offered (a) pre-creating every write-set layer before the search, or (b) lock-protected copy-on-write creation that leaves views alone. | (b). `GetOrCreateLayer` double-checks under `ContextTables.layerLock`, publishes the layer to the catalog with `TableCatalog.Publish` (a new map published with `Volatile.Write`) and to the layer slots, and changes no view and no `ViewGeneration`. During a search (`ContextTables.InSearch`) the layer's `TableInfo` waits in a deferred list, and the next `TakeRound` adopts it into `Database.tables` on the domain thread. `RunLookupOrInsertDefault` creates its buffer at the first miss in a child, so a lookup that hits creates nothing. | An empty layer adds no visible row and hides nothing, so a view taken before it exists stays correct for the rest of the run. (a) would allocate an empty layer (about 1–2 KB: hash table, two row buffers, pending queues, `TableInfo`) for every table any rule of a set could write, in every case child, against C14. |
| X38 | **Canonical and stamped writes** (R2-4, R1-6). Incoming rows were not canonicalized, a clean child could hold stale ids, and I19/I21 relied on callers stamping the sort cell. | **Children:** `InsertLayered` canonicalizes every incoming row's rebuild columns through `Owner.UnionFind.Find` and sets its sort cell to `state.Timestamp` before it probes (child-only code; zero root cost). **Root:** egglog's semantics for raw writes (written as given, rewritten by the next rebuild). Before the root is shared, `Context.Settle()` canonicalizes the rows written since its last rebuild (§7.6), and a non-frozen root's later stale appends are copied and hidden by each child's catch-up branch (a). DEBUG builds assert stamps in `Context.Write` at the root. | Children stay canonical whenever clean, at the cost of one `find` per incoming id cell. The root keeps egglog's numbering and contents for every program that creates no child. |
| X39 | **Schema and planning in a child** (R2-1). Rule building and planning read `Database.tables`, which in a child holds only own layers and `$uf`. | A layered `Database` answers schema questions (`GetTableSpec`, `GetTableName`, `GetIdColumns`, `IQuerySchema`) from `LayeredSchema`, and statistics, header evaluation and size estimates from its `ContextView`. `RuleBuilder.GetSpec`, `RuleQueryBuilder.AddAtom`, `SymbolMap.Create`, `RuleSetBuilder` and `PlanQuery` switch to these members. The full member list is in §3.4. | Rules are then built and planned in any context with that context's statistics (§9.7). |
| X40 | **Unsealed hides** (R1-1, critical). New hides went into a set's unsorted tail, readers used the sealed state only, and only `Rebuild` sealed. | Every layered `MergeAll` and `MergeTable` seals, in its `finally` epilogue, every set that gained a hide since the last seal (`ContextTables.AfterMerge`). `Rebuild`, `ClearTable` and `Freeze` seal too. A context is never readable with an unsealed set (I18), which `ThrowIfNotReadable` asserts in DEBUG. `HiddenSet.Union` and `HiddenRows` require sealed inputs. | Hides are added only inside `MergeAll` (`InsertLayered`, `DeleteLayered`) or right before the `MergeAll` of the same pass (catch-up, step 4), so the epilogue covers every path. |
| X41 | **Which rules may inherit, and which oracle governs** (R1-4, R1-5). | `RuleInfo.AllowInheritance` is computed from the rule's compiled actions (`ActionReadAnalysis`): false when any instruction reads a table whose contents can change (`Lookup`, `LookupWithDefault`, `LookupWithFallback`, `LookupOrInsertDefault` on a table whose merge is not `UnionIdMerge`, or an external function flagged `ReadsTables`), and for naive, split and callback rules. Oracles: seeded == unseeded governs every rule; the operational `ReferenceRuleContext` governs every rule; the fresh-root copy property governs monotone programs only; egglog push/pop governs programs whose rules all inherit. | I31's precondition "actions are query-determined" is then enforced, not assumed. A fresh-root replay is not an oracle for non-monotone bodies or for deletes, because facts derived earlier in an ancestor persist (correct operational semantics). |
| X42 | **Root cleanliness and egglog parity** (R2 minor). `EnsureClean` rebuilt the root whenever writes were staged, shifting every timestamp, and root reads threw on pending writes. | At the root, `IsClean` ignores pending notifications (staged writes are invisible until merged, as in egglog), and the bridge's `EnsureClean` rebuilds the root only when its journal mark is behind. `Context.Rebuild()` at the root runs passes when the journal mark is behind or writes are pending. | Root host writes staged through `WithExecutionState` merge inside the run's `MergeAll` at T, as in egglog. |
| X43 | **Frozen contexts and tables added later** (R1-7, R2 minor). | A frozen context never grows or writes its arrays. For a table id beyond them, `GetView` returns the parent's view (or `RootView`) without caching, and `TryGetLayer` returns `null`. `Store.AddTable` publishes the new table's `RootView`. | A frozen context has no layer and no hides for a table added after its freeze, so its view of that table is its parent's. No shared array is resized by concurrent readers. |

---

## 3. Objects and the interfaces between areas

### 3.1 Ownership

| Object (file) | Owns | Shares by reference |
|---|---|---|
| `Store` (`Contexts/Store.cs`) | The root `Context`, `LayeredSchema`, `UnionFindTable` (`TableId 0`), `IdCounter` (`CounterId 0`), `ClockCounter` (`CounterId 1`), `ContainerDirtyLog`, `DefinitionLock`, and the "has had children" flag | — |
| Root `Context` | The root `Database` (an egglog `Database`; `$uf` is `DisplacedTable`), its `LayeredUnionFind`, `Gate`, `Domain` and children | — |
| Child `Context` | Its `Database` (catalog, notifications, pending queues, own-layer `TableInfo`s, `ChangeStamp`, idle `JoinRun`, `ContextView`), its `ContextTables`, its `OverlayDisplacedTable` (`UnionFindOverlay` plus `DisplacedJournal`), `IdBlockAllocator`, union-find cursor, semi-naive floor, `Gate`, `Domain`, and, once frozen, a `FlattenedUnionFind` | `CounterSet`, `ExternalFunctionRegistry`, `DependencyGraph` and `ContainerValues` (root's); frozen ancestors' layers, overlays, journals and indexes (read-only); plan templates |
| `ContextTables` (`Layers/ContextTables*.cs`) | Lazy `TableLayer`s and their slots, the layer lock and deferred-adoption list, the `TableView` cache and dirty flags, the unsealed-set list, `AncestorCursor`s, `LayerStatistics`, `RetractionGuard` | `LayeredSchema` |
| `EGraph` (`Bridge/`, M8, modified) | Definitions: functions, `RuleTable`, panic functions, context-local slots, `ParallelismBudget` | The `Store` |
| `EGraphContext` (`Bridge/EGraphContext*.cs`) | Rule progress, `HostWrites`, locals, split records, member-map cache | Wraps exactly one `Context` (`Context.Host` points back) |

### 3.2 `Store` (`Contexts/Store.cs`)

```csharp
internal sealed class Store
{
    public Store(ParallelHeuristics? parallelism = null);
    public Context Root { get; }
    public LayeredSchema Schema { get; }
    public TableId UnionFindTable { get; }            // TableId 0 (egglog-bridge lib.rs:143-148)
    public DisplacedTable RootUnionFind { get; }
    public CounterId IdCounter { get; }               // CounterId 0 (lib.rs:149)
    public CounterId ClockCounter { get; }            // CounterId 1, incremented to 1 (lib.rs:150-152)
    public ContainerDirtyLog ContainerDirtyLog { get; }
    public uint ReadClock();                          // checked: OverflowException at uint.MaxValue
    public void AdvanceClock();                       // Counters.Increment(ClockCounter)
    public TableId AddTable(Table table, ReadOnlySpan<TableId> readDependencies = default,
                            ReadOnlySpan<TableId> writeDependencies = default, string? name = null,
                            bool contextPrivate = false);   // §4.3
    public CounterId AddCounter();                    // throws once any child has been created (X16)
    public void DesignateContradiction(TableId table);// a nullary relation; once
    internal CounterSet Counters { get; }
    internal object DefinitionLock { get; }
    internal bool HasHadChildren { get; }             // volatile
}
```

The constructor:
1. creates `new Database(parallelism)`;
2. adds `$uf` as `TableId 0`;
3. adds the two counters and increments the clock to 1;
4. creates `Schema = new LayeredSchema(this)`;
5. creates `Root = new Context(this, db)`, which calls `db.AttachRoot(root, ClockCounter)` and sets `db.RetractionGuard = Schema.RootGuard`.

The bridge's `EGraph` on a `Store` uses these members and does not add its own counters or `$uf`. A bare `Database` without a `Store` behaves exactly as today.

### 3.3 `Context` (`Contexts/Context*.cs`, partial)

```csharp
internal sealed partial class Context : IDisposable
{
    // Context.cs
    public Store Store { get; }  public Context? Parent { get; }  public int Depth { get; }
    public ReadOnlySpan<Context> Ancestors { get; }       // index = depth; [Depth] == this
    public ContextOptions Options { get; }
    public Database Database { get; }
    public ContextTables? Tables { get; }                 // null at the root
    public DescendantGate Gate { get; }                   // live descendants (C10, C12)
    public ThreadAffinityGuard Domain { get; }            // §8.3
    public uint CreatedAt { get; }                        // clock read after cursors are initialized; root 0
    public long Version => Database.ChangeStamp;          // D16
    public bool IsRoot { get; }  public bool IsFrozen { get; }  public bool IsDisposed { get; }
    public bool IsFaulted { get; }  public bool IsContradicted { get; }  public bool IsClean { get; }   // §7.5
    public object? Host { get; set; }                     // the bridge's EGraphContext; set once
    // Context.Lifecycle.cs (§7, §8)
    public Context CreateChild(ContextOptions? options = null);
    public bool Rebuild();
    public void Freeze(FreezeWarmup? warmup = null);
    public void Dispose();
    internal void PrepareForWrite();
    internal void Settle();                               // root only, §7.6
    // Context.Epochs.cs (§4.1)
    internal uint BeginEpoch();  internal void EndEpoch();
    internal void NoteRuleRun(uint epoch);                // raises SemiNaiveFloor
    internal uint SemiNaiveFloor { get; }                 // max(CreatedAt, largest NoteRuleRun epoch), §7.2
    // Context.UnionFind.cs (§5.7)
    public Table UnionFindTable { get; }  public LayeredUnionFind UnionFind { get; }
    public int DisplacedCount { get; }    public ReadOnlySpan<uint> DisplacedIds { get; }
    public bool IsUnionFindCurrent { get; }
    public uint Find(uint id);  public bool AreEqual(uint a, uint b);
    public uint NewId();        public bool IsLocalId(uint id);  public bool Union(uint a, uint b);
    internal bool IsOwnDisplacement(uint id);
    internal void ScanMerges<TVisitor>(ref TVisitor visitor) where TVisitor : struct, IMergeVisitor;
    public UnionFindStatistics UnionFindStatistics { get; }
    // Context.CatchUp.cs (§5.6)
    // int CatchUpUnionFind(uint epoch)
    // Context.Writes.cs (§6.6)
    public TResult Write<TResult>(Func<ExecutionState, TResult> action);
    public void ClearTable(TableId table);
    // Context.Reads.cs (§6.5, §6.10)
    public TableView GetView(TableId table);
    public bool TryGetRow(TableId table, ReadOnlySpan<uint> key, Span<uint> destination);
    public bool IsContradictory { get; }
    public void ScanOwn<TSink>(TableId table, ref TSink sink) where TSink : struct, IRowSink;
    public void ScanAncestorsMentioning<TSink>(TableId table, uint id, ref TSink sink) where TSink : struct, IRowSink;
    // Context.Invariants.cs (§12.7)
    internal void CheckInvariants();
}
```

`ContextOptions` (`Contexts/ContextOptions.cs`) is a sealed class with init-only properties:
- `bool StopOnContradiction` (default `false`);
- `ParallelHeuristics? Parallelism` (`null` means the C9 default of §10.9);
- `bool IsCase`;
- `bool InheritWatermarks` (default `true`; read by the bridge).

It has two static instances: `Default` and `Case` (`IsCase = true`, `StopOnContradiction = true`, per D11).

### 3.4 `Database` per context (`Contexts/Database.Contexts.cs`, partial)

```csharp
Context? owner;  ContextTables? layers;  IdBlockAllocator? idBlocks;  int clockRep = -1;
long changeStamp;  volatile bool frozen;
internal Database(Database root, Context owner, Table unionFind,
                  IdBlockAllocator ids, ParallelHeuristics parallelism);   // child
internal void AttachRoot(Context root, CounterId clock);
internal void AttachLayers(ContextTables layers);
internal Context? Owner { get; }  internal ContextTables? Layers { get; }
internal int ClockCounterRep => clockRep;   internal long ChangeStamp { get; }
internal RetractionGuard? RetractionGuard { get; set; }
internal RetractionGuard? GuardForNewState();                    // null at a root without live descendants (§6.7)
public bool IsFrozen { get; }
internal void AdoptLayer(TableLayer layer);                      // tables.Insert(layer.Id, layer.Info); domain thread, outside a search (I34)
internal bool IsTakenOut(TableId table);                         // the layer is adopted and the table map does not hold it
internal long IncrementCounterInView(CounterId c);               // idBlocks is {} b && c == b.Counter ? b.Next() : counters.Increment(c)
internal long IncrementSharedCounter(CounterId c);               // never redirected
internal TableSpec GetTableSpec(TableId table);                  // X39
internal string? GetTableName(TableId table);                    // X39
```

**The child constructor:**
- copies the root's `counters`, `externalFunctions`, `deps` and `containers` (`root.Containers`, which forces creation) by reference;
- builds `catalog = new TableCatalog { Resolver = id => layers!.ResolveForStaging(id) }`;
- places `TableInfo("$uf", unionFind, ParallelHeuristics.Serial)` at `TableId 0` (the `OverlayDisplacedTable` from M9.2 on; a placeholder `DisplacedTable` that is never written in M9.1, §17);
- sets `parallelism` to the C9 default (§10.9).

`Database.View` (§9.1) is assigned for children and created lazily at the root.

**Members of a layered (child) `Database` (X39).** Every member of `Database/Database.cs`, `Database/Database.Constraints.cs`, `Database/Database.Rebuild.cs`, `Containers/Database.Containers.cs` and `Query/Database.Query.cs` that reads the table map behaves as follows in a child; the root and a bare `Database` are unchanged.

| Member | In a child |
|---|---|
| `GetTableSpec(t)` (new) | `LayeredSchema.GetSpec(t)`: `DisplacedTable.DisplacedSpec` for `$uf`, else `schema[t].RootInfo.Spec`. Root: `GetTableInfo(t).Spec`. |
| `GetTableName(t)` (new) | `LayeredSchema.GetName(t)`. Root: `GetTableInfo(t).Name`. |
| `GetIdColumns(t)` (`Query/Database.Query.cs:45`) | `schema[t]?.RebuildColumns`, empty for `$uf` |
| `IQuerySchema.GetArity` / `GetKeyCount` | `GetTableSpec(t)` (both levels) |
| `IQuerySchema.SplitConstraints` | `ConstraintProcessor.Split(schema[t].RootInfo, …)`: the split depends only on the table's kind and cacheable columns, which every layer shares; `$uf` throws `NotSupportedException` (X23) |
| `IPlanStatistics` | `View` (`ContextView`) |
| `IHeaderEvaluator.Evaluate` | Throws `InvalidOperationException`; every caller instantiates through `View` (`ILayeredHeaderEvaluator`, §9.4) |
| `PlanQuery(q)` | `QueryPlanner.PlanQuery(q, View)` |
| `EstimateSize(t, c)` | `View.EstimateSize(t, c)` (sum over present layers) |
| `GetTable`, `GetTableInfo`, `TryGetTableInfo` | The own-layer and `$uf` map only, for the database's own merge, rebuild and index work; `MissingTable` says "Table t has no layer in this context; read it through Context.GetView" |
| `NewBuffer(t)` | `catalog.GetForStaging(t).NewBuffer()`, then notify |
| `ClearTable(t)` | `Owner.ClearTable(t)` (§6.7) |
| `AddTable`, `AddCounter`, `AddExternalFunction`, `FreeExternalFunction`, `NextTableId` | Throw `InvalidOperationException("Definitions are added through the Store.")` |
| `TableCount` | `LayeredSchema.TableCount` |
| `VisibleTableIds` (behind `DatabaseView.TableIds` and `ExecutionState.TableIds`) | `LayeredSchema.Ids` |
| `ApplyRebuild`, `RefreshRowsForValues`, `RebuildContainers`, `RebuildPass`, `Rebuild` (public) | Throw `InvalidOperationException`; a child rebuilds through `Context.Rebuild`, whose `ContextTables` calls the internal `RunWithTakenOut` and `RunOnTables` |
| `Clone` | Throws (§4.3) |
| `NewRuleSet`, `RunRuleSet`, `MergeAll`, `MergeTable`, `CreateExecutionState`, `WithExecutionState` | Unchanged in shape (layered behaviour per X2, X21, X37, X40) |

Call sites switched to the new members: `Query/RuleBuilder.cs:389` (`GetSpec` → `owner.Database.GetTableSpec`), `Query/RuleQueryBuilder.cs:128` (`AddAtom` arity → `GetTableSpec(table).Arity`), `Query/RuleQueryBuilder.cs:190` (`GetIdColumns`, layered as above), `Query/SymbolMap.cs:58` (`GetTableName`), `Query/RuleSetBuilder.cs` (`EstimateSize`; `AddBuiltRule` and `GetRuleWithExtraConstraints` instantiate through `db.View`), and `Database/ColumnCardEstimator.cs` (a static per-`TableInfo` overload used by `ContextView`). `DatabaseView.GetTableName` forwards to `GetTableName`, and `DatabaseView.GetTable` throws in a child.

### 3.5 Who calls whom

| Caller | Callee | Contract |
|---|---|---|
| Executor (`JoinRun`, `RuleWork`) | The rule set's `ViewSnapshot` → `TableView` / `LayerView` / `HiddenRows` | Taken at `RuleSetBuilder.Build` while the context is clean. Views are immutable for the run; hidden sets are sealed (I18, I29). |
| Actions (`ExecutionState`) | Search phase: the state's `ViewSnapshot`, else `ContextTables.GetView(t)`; write phase: `ContextTables.TryGetVisibleRow` / `TryGetVisibleColumn` | C4. `layers` is resolved from `view.Owner.Layers`. |
| Staging (`ExecutionState.CreateBuffer`) | `TableCatalog.GetForStaging` → `ContextTables.ResolveForStaging` | Thread-safe; may create an empty layer (X37, I34). |
| Tables (`ContextTables`) | `Context.UnionFind.Find`, `Context.IsOwnDisplacement`, `OverlayDisplacedTable` (rebuilder table), journal rows `[journalMark, Count)` | The union-find is current before any row work (§7.1). |
| `Context.Rebuild` | `CatchUpUnionFind(e)` → `ContextTables.CatchUpRows(e)` → `ContextTables.RebuildPass(e)` | In this order, first pass only for the first two (§7.1). |
| Bridge (`EGraphContext`) | `Context.BeginEpoch/EndEpoch/NoteRuleRun/Rebuild/Write/Freeze/CreateChild/Dispose`, `Database.RunRuleSet(rs, level, T)` | The bridge flushes its own `HostWrites` up the chain before calling storage (§10.4). |
| Lift (`Cases/`) | `Context.ScanOwn`, `ScanMerges`, `IsLocalId`, `ScanAncestorsMentioning`, `UnionFindOverlay` (via `MemberMap`) | The context is clean. |

---

## 4. Identity, time and schema (C1)

### 4.1 Clock and epochs

- **Capture.** `Context.BeginEpoch()` requires that the context is not frozen, not disposed and not faulted, then returns `Store.ReadClock()`. `Context.EndEpoch()` calls `Store.AdvanceClock()`.
- **Bracketing.** Every rule run, rebuild pass and `Write` batch is bracketed by these calls. Exception (X4): a rule run that grew the union-find ends its epoch through the first rebuild pass.
- **The pin.** `Actions/ExecutionState.Database.cs` changes as follows:

  ```csharp
  public long ReadCounter(CounterId counter)
  {
      var view = RequireView();
      return counter.Rep == view.ClockCounterRep ? Timestamp : view.ReadCounter(counter);   // C1 pin
  }
  ```

  `DatabaseView.ClockCounterRep` forwards `Database.ClockCounterRep`, which is −1 for a bare database.

  So `ReadCounterInstr` (`next_ts`), `UnionContainerMerge` (`TimestampCounter`), `UnionIdMerge` (`state.Timestamp`) and the bridge's `TableAction`/`UnionAction` all stamp with the captured epoch, even when a concurrent sibling advances the clock.
- **Timestamp parameters.** `Database.MergeAll`, `MergeTable`, `RunRuleSet`, `CreateExecutionState`, `WithExecutionState` and `RebuildPass` change their timestamp parameter to `uint? timestamp = null`. `null` resolves to `ClockCounterRep < 0 ? 0 : checked((uint)ReadCounter(clock))`. This is source-compatible, and a bare `Database` behaves as before.
- **Per-context order.** Epochs of one context never decrease (I3). After a context's `EndEpoch` the clock is at least e+1, and nothing decreases it.
- **Root equivalence.** A root that is not frozen runs sequentially with all its descendants, and nothing else increments the clock during its run. The pinned value therefore equals the live value at every read. With X4 and X42, `run → (no advance) → pass(T) → advance` reproduces `run_rules_inner` exactly. The root departs from egglog only (a) when a rule's Id constant has lost a union (X19), and (b) in operations egglog does not have (`CreateChild`, `Freeze`, which settle, §7.6).

### 4.2 Id blocks and `IsLocalId`

**`IdBlockAllocator`** (`Contexts/IdBlockAllocator.cs`) and **`IdBlock`** (`Contexts/IdBlock.cs`, a sealed class with fields `long Next` and `readonly long End`):

```csharp
internal sealed class IdBlockAllocator
{
    public IdBlockAllocator(CounterSet counters, CounterId counter);
    public CounterId Counter { get; }
    public uint Next();                // thread-safe
    public bool Contains(uint id);     // binary search over the published [start, end) arrays
    public long Minted { get; }
}
```

**`Next()`:**
1. `b = Volatile.Read(ref current)`; `v = Interlocked.Increment(ref b.Next) - 1`.
2. If `v < b.End`, return it.
3. Otherwise, under `lock (sync)`, if `current == b`:
   - choose `size = clamp(2 × Minted, 16, 4096)`;
   - compute `start = counters.IncrementBy(Counter, size)` (new: `Interlocked.Add(...) - size`);
   - throw `OverflowException` when `start + size - 1 > UnionFind.MaxId` (`UnionFind/UnionFind.cs:72`, `Array.MaxLength - 1`), the largest id a union-find array can hold;
   - append the block to the copy-on-write arrays;
   - publish the new `IdBlock` with `Volatile.Write`.
4. Retry.

**Where it is used:**
- `Database.IncrementCounter` and `DatabaseView.IncrementCounter` forward to `IncrementCounterInView`. That is one null test per minted id at the root.
- Every term mint in a child goes through it: `WriteValKind.IncrementCounter`, `PredictRow`'s `MergeVal.IsCounter`, and `Context.NewId`.
- `ContainerEnv.MintId` (`Containers/ContainerEnv.cs:409`) changes to `state.IncrementSharedCounter(counter)` (new `ExecutionState` member, `Layers/ExecutionState.Layers.cs`). Globally interned container ids are therefore never local (L3, C13).
- `Context.IsLocalId(id)` returns `idBlocks.Contains(id)` and throws `InvalidOperationException` on the root.
- `Context.NewId` at the root checks the minted id against `UnionFind.MaxId` and throws `OverflowException` past it.
- An id inside a reserved block that was never minted reports `true`, which is harmless because it appears nowhere.

**Memory at the root.** Reserved blocks spread ids. The root's dense arrays (`UnionFind.parents` through `Reserve`, and `DisplacedUnionFind.rowOf` through `GrowLookup`) grow to the largest id the root *unions*, at 8 bytes per id below it; minting or finding a large id grows nothing. With 16-id first blocks, 10⁵ single-mint cases reserve 1.6M ids, so a root that later unions an id above them pays about 13 MB. `LateParentMintBenchmarks` (M10.4) reports it.

**Defect to fix in passing:** `ExecutionState.ValueFromCounter` (`Actions/ExecutionState.Predicted.cs:184`) accepts `uint.MaxValue == Value.StaleRep`. Reject ids `>= StaleRep`.

### 4.3 Schema changes (X16, X43)

**`Store.AddTable`:**
1. Takes `lock (DefinitionLock)`. If the root is not frozen, it also enters `Root.Domain`, so it cannot run concurrently with root work.
2. Calls `root.Database.AddTable(...)`.
3. Calls `Schema.Register(id, contextPrivate)`.
4. If the root is frozen, freezes the new (empty) table at once: `Table.Freeze()`, `TableInfo.FreezeIndexes()`, and publishes its `RootView` (§8.1).

It touches no other context. A frozen context answers for the new id functionally (X43); a non-frozen context grows its arrays on its own thread when it first sees the id.

**What children read, and how it stays safe:**
- Children reach root tables only through `LayeredSchema` (a volatile array swap), never through the root `Database`'s `tables` map or `catalog`.
- `DependencyGraph` gains `int[] levelSnapshot`, republished with `Volatile.Write` on `AddTable`. `LevelOf` reads the snapshot. Existing strata never change, because a new table lands at or above existing levels.
- `ContextTables` arrays of a non-frozen context grow on demand, copy-on-write under the layer lock, because its own rule tasks may read the layer slots. `AncestorCursor.Rows` treats an index beyond its length as `(0,0)`, so a new root table's rows count as new.

**Counters and external functions:**
- `Store.AddCounter`, and `Database.AddCounter` on any database of the store, throw `InvalidOperationException` once `HasHadChildren` is set.
- `ExternalFunctionRegistry` publishes a `DenseIdMap` snapshot with `Volatile`. `Add` and `Remove` lock, clone and publish. Lookups are lock-free.

**Clone:** `Database.Clone()` throws `InvalidOperationException` when `owner` is a child or `owner.Gate.HasLiveDescendants`. egglog's push/pop clone stays root-only.

---

## 5. The layered union-find (C2)

Notation: `P` is the parent; `find_V` is canonical in view V; an id is **P-canonical** iff `find_P(x) == x`; C is **uf-current** iff `unionFindCursor == P.DisplacedCount`.

### 5.1 Root changes

**`UnionFind/UnionFind.cs`** gains:
- `CompressPath(uint id)`: a read-only walk to the root, then every node on the path points at the root. O(path).
- `CompressAll()`: `p[i] = p[p[i]]` for ascending i. This is correct because `p[i] ≤ i` (the class invariant): when i is processed, a smaller `p[i]` already points at a root. O(IdCount).

**`UnionFind/DisplacedUnionFind.cs`** gains `CompressRows(int firstRow)` (`CompressPath` of each child since `firstRow`), `CompressAll()` and `LastTimestamp`. `Search` becomes `internal static`.

**`Tables/DisplacedTable.cs`:**
- `DisplacedSpec` becomes `internal static readonly`.
- `Merge` records `first = uf.Count` and calls `uf.CompressRows(first)` after the loop (C2, "compress at the end of each merge"). This is invisible to every result: roots are unchanged and `parents[i] ≤ i` still holds.
- Adds `HasPendingUnions`.
- `Freeze()` override (X13): throws if pending, calls `uf.CompressAll()`, seals pending.
- After freezing, `Merge` and `Clear` throw `InvalidOperationException("The union-find of a frozen context cannot be written.")`.

The existing `DisplacedUnionFindTests` parent-parity tests exercise `DisplacedUnionFind` directly and stay green.

### 5.2 `UnionFindOverlay` (`UnionFind/UnionFindOverlay.cs`)

The overlay is a partition of the P-canonical ids that C merged, as a class table. Each slot holds:
- `canonical` (the minimum member);
- a member list (including the canonical);
- a count (0 means a free slot).

`classOf` is a `RawHashTable<IdEntry>` mapping member → slot. It uses new `IdEntry(uint Id, uint Payload)` (`Hashing/IdEntry.cs`), `IdEntryHasher` (`FxHasher.Hash(Id)`) and `IdEntryMatch`, and is allocated on first insert.

```csharp
internal sealed class UnionFindOverlay
{
    public bool IsEmpty { get; }  public int MemberCount { get; }  public int ClassCount { get; }  public int SlotCount { get; }
    public int Version { get; }                                   // +1 per effective Union/Rekey (X6, X29)
    [MethodImpl(AggressiveInlining)] public uint Map(uint parentCanonical);   // hit ? canonical[slot] : id
    public bool TryGetClass(uint id, out int slot);
    public uint CanonicalOf(int slot);  public ReadOnlySpan<uint> MembersOf(int slot);  public int CountOf(int slot);
    public bool Union(uint a, uint b, out uint winner, out uint loser);        // a, b P-canonical
    public int Rekey(uint displaced, uint parentCanonical, Span<uint> lost);   // §5.6; lost.Length ≥ 2
    public void Freeze();  internal void Release();
}
```

**Private helpers.** Each keeps `canonical = min(members)`.

| Helper | What it does | Cost |
|---|---|---|
| `NewClass(a, b)` | Creates a class with members `[a, b]` | O(1) |
| `AddMember(slot, x)` | Appends x to the class | O(1) amortized |
| `MergeClasses(s, t)` | Merges the smaller class into the larger, relinking the smaller's members | O(smaller); O(n log n) in total |
| `RemoveMember(slot, x, out dissolved, out remaining)` | Removes x. A class left with one member dissolves; a class that loses its canonical recomputes the minimum. | O(class size) |

**`Union(a, b)`:**
1. `ca = class(a)?.canonical ?? a`, and likewise `cb`.
2. If they are equal, return false.
3. Otherwise `winner = min(ca, cb)`, `loser = max(ca, cb)`, then `NewClass`, `AddMember` or `MergeClasses` as the cases require.
4. Increment `Version`.

**Why it replaces the spike's dictionary** (`Context.Views.cs:37-78`): the spike's `LocalFind` compresses paths on read, so a context that is not frozen cannot be read by its own rule tasks. Re-keying needs member lists anyway (B7).

### 5.3 `DisplacedJournal` (`UnionFind/DisplacedJournal.cs`)

```csharp
internal sealed class DisplacedJournal
{
    public int Count { get; }  public ReadOnlySpan<uint> Ids { get; }  public ReadOnlySpan<uint> Timestamps { get; }
    public uint LastTimestamp { get; }                       // 0 when empty
    public void Append(uint id, uint timestamp);             // InvalidOperationException if timestamp < LastTimestamp (DisplacedUnionFind's message); Debug.Assert(!Contains(id))
    public bool Contains(uint id);  public bool TryGetRow(uint id, out int row);
    public uint GetId(int row);  public uint GetTimestamp(int row);
    public bool TimestampBounds(uint ts, out int start, out int end);   // DisplacedUnionFind.Search
    internal void Release();
}
```

**Definition (I8).** C's journal holds exactly the ids x with `find_C(x) ≠ x` that were not displaced in P's view when C was created. Each appears once, in nondecreasing ts. The ids come from three sources:
- losers of C's own unions;
- canonical ids that lost during re-keying;
- **imports**: parent displacements of ids that were C-canonical.

`Context.DisplacedIds` and `Context.DisplacedCount` expose the root's `DisplacedUnionFind.Children` or a child's `Journal.Ids`.

`Context.IsOwnDisplacement(id)` is `Parent is not null && parentFinder.Find(id) == id`. It is computed at use time, which is always correct. If the parent later displaces an own loser, that loser's ancestor rows are invisible at the parent level, so skipping them is right.

### 5.4 `LayeredUnionFind` and `FlattenedUnionFind`

```csharp
internal sealed class LayeredUnionFind        // UnionFind/LayeredUnionFind.cs
{
    public LayeredUnionFind(DisplacedUnionFind root);
    public LayeredUnionFind Extend(UnionFindOverlay own);
    public static LayeredUnionFind Flattened(DisplacedUnionFind root, FlattenedUnionFind? prefix);
    [MethodImpl(AggressiveInlining)]
    public uint Find(uint id)
    {
        var y = root.FindNaive(id);
        if (prefix is { } f) y = f.Map(y);
        var l = levels;
        for (var i = 0; i < l.Length; i++) { var o = l[i]; if (o.IsEmpty == false) y = o.Map(y); }
        return y;
    }
    public uint MapAboveRoot(uint rootCanonical);
}
```

**`FlattenedUnionFind`** (`UnionFind/FlattenedUnionFind.cs`) is immutable.
- **Domain:** the prefix's keys plus every member of every level. All of these are root-canonical.
- **Content:** it stores `m → MapAboveRoot(m)` wherever that differs from m. Outside the domain, `Map` is the identity.
- **Representation:**
  - `null` when nothing is stored;
  - a dense identity-filled `uint[]` over `[minKey, maxKey]` when the range is at most 8 × the entries;
  - otherwise a `RawHashTable<IdEntry>`.
- `internal void CollectKeys(List<uint>)` supports building a deeper prefix.

**Finders.** `Context.FinderForChildren` is the context's own chain until it freezes, and `LayeredUnionFind.Flattened(...)` afterwards (§8.1). Children created before a freeze keep their explicit chain, which is correct at one probe per level.

**Why the alternating chase is not used.** Nothing reads a child's `find` between a parent change and the child's re-key:
- reads require `IsClean`;
- writes and runs start with `PrepareForWrite`;
- `Rebuild` re-keys first;
- a frozen context's parent is frozen.

Keys that the parent displaced break the chase anyway. Example: overlay `9→4`, then the parent unions 9 and 3; `find_C(4)` stays wrong until re-keyed.

### 5.5 `OverlayDisplacedTable` and the rebuilders

**`OverlayDisplacedTable : Table`** (`Tables/OverlayDisplacedTable.cs`, `TableKind.OverlayDisplaced`, arity 3):
- **Constructor:** `(LayeredUnionFind parentFinder, UnionFindOverlay overlay, DisplacedJournal journal, Table parentUnionFind)`.
  - `Finder = parentFinder.Extend(overlay)`.
  - `pending = new PendingState(new ShardData(1))`.
- **Members:**

| Member | Behaviour |
|---|---|
| `Spec` | `DisplacedTable.DisplacedSpec` |
| `Count` / `Version` | `journal.Count` / `new(0, journal.Count)` |
| `All` / `UpdatesSince` | Over the journal, which is layer-local |
| `HasStaleRows` | `false` |
| `FindNaive` | `Finder.Find`, non-virtual |
| `GetCell` | Column 1 is `Finder.Find(id)` |
| `ScanGenericBounded` / `ScanGeneric` (`new`) | Copies of `DisplacedTable`'s versions |
| `RefineOne` | Uses `OverlayDisplacedConstraintFilter` (`Tables/`, mirroring `DisplacedConstraintFilter`) |
| `TryFastSubset` | `EqConst` on column 0 → `journal.TryGetRow`; ts ranges → `journal.TimestampBounds` |
| `TryGetRow` | **View-level.** A journal hit gives that row id. An ancestor hit through `parentUnionFind.TryGetRow` gives `rowId = -1` with `dest[1] = Finder.Find(key)`. |
| `TryGetRowColumn` / `TryGetColumn(key, 1)` | `Finder.Find(key)` for any key |
| `NewBuffer` | `new MutationBuffer(pending, 3, 1, false)` |
| `Merge` | Below |
| `Clear` | Throws (decision 1) |
| `Clone` | Throws `NotSupportedException` |
| `GetRebuilder(cols)` | `new OverlayCanonicalizer(Finder, cols)` |
| `Freeze` | Throws if pending; `overlay.Freeze()`; seals pending |

**`Merge(state)`.** Precondition: uf-current, which `PrepareForWrite` and `Rebuild` guarantee. For each staged `[l, r, ts]`, in queue and staging order:
1. `a = parentFinder.Find(l)`, `b = parentFinder.Find(r)`.
2. If `journal.Count > 0 && ts < journal.LastTimestamp && overlay.Map(a) != overlay.Map(b)`, throw `InvalidOperationException` before any mutation. This is `DisplacedUnionFind.Union`'s order check; a redundant union may carry an old ts.
3. If `overlay.Union(a, b, out _, out loser)`, then `journal.Append(loser, ts)` and increment `stats.OwnUnions`.
4. Return `new TableChange(changed, changed)`.

**Rebuilders** (UF §4.8):
- `IIdFinder` (`UnionFind/IIdFinder.cs`: `uint Find(uint)`), with `RootIdFinder` and `LayeredIdFinder` readonly structs.
- `CanonicalizeLoops` (`Tables/CanonicalizeLoops.cs`) holds today's `Canonicalizer.RebuildOne/Two/Three/Many` bodies and `RebuildRow`, moved verbatim with `uf.FindNaive` replaced by `finder.Find`, under `where TF : struct, IIdFinder`.
- `Canonicalizer` keeps its public surface and forwards with `new RootIdFinder(uf)`, which the JIT specializes, so root machine code is unchanged.
- `OverlayCanonicalizer` (`Tables/OverlayCanonicalizer.cs`) uses `LayeredIdFinder`, with `HintColumn = 0`.

**Lookup lane.** `OverlayDisplacedLookup` (`Actions/OverlayDisplacedLookup.cs`) is a struct `ILaneProducer` over `TryGetColumn`, used by a new arm in `VectorizedLookup.Run`. It serves the bridge's `lookup_uf`.

### 5.6 Catch-up step 1: re-keying (`Contexts/Context.CatchUp.cs`)

```text
int CatchUpUnionFind(uint e):                       // child; returns journal.Count before
  require parent frozen or clean; throw if overlayTable.HasPendingUnions
  ("Unions staged in a context must be merged before it catches up with its parent.")
  from = journal.Count; end = parent.DisplacedCount
  if unionFindCursor == end: return from
  ids = parent.DisplacedIds; Span<uint> lost = stackalloc uint[2]
  for i in [unionFindCursor, end):
      d = ids[i]; Debug.Assert(!localIds.Contains(d))
      p = parentFinder.Find(d); Debug.Assert(p < d)          // the FINAL P-canonical of d
      n = overlay.Rekey(d, p, lost)
      for j < n: if !journal.Contains(lost[j]): journal.Append(lost[j], e); stats.ImportedDisplacements++ or RekeyedMembers++
  unionFindCursor = end
  return from
```

**`UnionFindOverlay.Rekey(d, p, lost)`.** It merges d's C-class with p's class and replaces member d by p. It returns the ids that were C-canonical before and are not after.

```text
kd = TryGetClass(d); kp = TryGetClass(p)
if kd < 0: lost[0] = d; return 1                       // the caller skips it if already journaled
cdOld = canonical[kd]; cpOld = kp ≥ 0 ? canonical[kp] : p
if kd == kp: RemoveMember(kd, d, out _, out _); return 0
RemoveMember(kd, d, out dissolved, out remaining)
k = dissolved ? (kp ≥ 0 ? AddMember(kp, remaining) : NewClass(p, remaining))
              : (kp ≥ 0 ? MergeClasses(kd, kp)     : AddMember(kd, p))
cNew = canonical[k]; n = 0
if cdOld != cNew: lost[n++] = cdOld
if cpOld != cNew && cpOld != cdOld: lost[n++] = cpOld
return n
```

**Statistics.** `UnionFindStatistics` (`Contexts/UnionFindStatistics.cs`, a readonly record struct) has `OwnUnions`, `ImportedDisplacements`, `RekeyedMembers` and `ParentIdLosers`. The last counts own losers that are parent ids beaten by a local id: the G2/L7 measure.

**Cost.** O(1) expected per parent displacement, plus class sizes. Zero when current, which is always the case under a frozen parent after the first catch-up.

### 5.7 The context's union-find API

- `Find`, `AreEqual`: `ThrowIfNotReadable()`, then `UnionFind.Find`. `Debug.Assert(IsUnionFindCurrent)`.
- `NewId`: `ThrowIfNotWritable()`. A child uses `idBlocks.Next()`; the root uses `checked((uint)Counters.Increment(IdCounter))`, bounded by `UnionFind.MaxId` (§4.2).
- `Union(a, b)`: `Write(s => s.StageInsert(uf, [a, b, s.Timestamp]))`. Returns whether `DisplacedCount` grew. Rows are rewritten at the next `Rebuild`.
- `ScanMerges<TVisitor>(ref visitor)`:
  - throws on the root, and requires `IsClean`;
  - for every slot with count ≥ 2, visits `(m, canonical)` for each member m with `IsLocalId(m) == false`, in slot order then insertion order (deterministic, decision 4);
  - reads, including `Find`, are allowed inside the visitor.

  `IMergeVisitor` (`Contexts/IMergeVisitor.cs`) is `void Visit(uint member, uint canonical)`. This is rule-layer §2.1's contract: a merged class's canonical id is reported paired with itself when it is a parent id (L2, G1(a)).

---

## 6. Layered tables (C3, C4, C10, C11, C12, C14)

### 6.1 `LayeredSchema` and `LayeredTableSchema` (`Layers/`)

```csharp
internal sealed class LayeredSchema
{
    public LayeredSchema(Store store);
    public TableId UnionFind { get; }  public TableId? Contradiction { get; }   // set by Store.DesignateContradiction
    public int TableCount { get; }
    public LayeredTableSchema? this[TableId id] { get; }    // null for $uf or unregistered
    public ReadOnlySpan<TableId> FunctionTables { get; }    // every registered id, for the root's RebuildPass
    public IEnumerable<TableId> Ids { get; }                // $uf and every registered id, ascending
    public TableSpec GetSpec(TableId id);                   // $uf → DisplacedTable.DisplacedSpec (X39)
    public string? GetName(TableId id);                     // X39
    public RetractionGuard RootGuard { get; }
    public void Register(TableId id, bool contextPrivate);  // volatile array swap
    public TableView RootView(TableId id);                  // built once, published with CompareExchange
    internal void FreezeRoot();                             // builds every RootView (§8.1)
}
internal sealed class LayeredTableSchema
{
    public TableId Id; public string? Name; public SortedWritesTable Root; public TableInfo RootInfo;
    public bool IsContextPrivate; public bool IsContradiction; public int Stratum;
    public int KeyCount; public int Arity; public int SortColumn; public ReadOnlySpan<ColumnId> RebuildColumns;
    public bool IsLattice => Root.MergeFunction.IsLattice;
    public bool IsConstructor => Root.MergeFunction is UnionIdMerge;   // ActionReadAnalysis (§10.2)
}
```

`Register` requires `Root.Catalog[id]` to be a `SortedWritesTable`. For a table that is not private, it sets `root.CompactionGate = store.Root.Gate` (§6.8). `RootInfo` is the root `Database`'s `TableInfo` object, held by reference, so it stays valid while the root takes the table out for a merge.

### 6.2 `ContextTables`, `TableLayer` and layer creation (X37)

**`ContextTables`** (`Layers/ContextTables.cs`, with partials `.Lookup.cs`, `.Merge.cs`, `.CatchUp.cs`, `.Rebuild.cs`, `.Retraction.cs`, `.Views.cs`, `.Lift.cs`, `.Freeze.cs`, `.Invariants.cs`) implements `ILayerViewSource`.

```csharp
internal sealed partial class ContextTables : ILayerViewSource
{
    public ContextTables(LayeredSchema schema, Context owner);       // chain from owner.Ancestors
    public int Depth { get; }  public Context Owner { get; }
    public RetractionGuard Guard { get; }  public LayerStatistics Statistics { get; }
    public bool NeedsCatchUp { get; }                                // §7.2, O(unsettled ancestors)
    public long ViewGeneration { get; }
    internal bool InSearch { get; set; }                             // set by Database.RunRuleSet around the search
    internal int UnsealedCount { get; }                              // X40
    internal Table ResolveForStaging(TableId t);                    // $uf → OverlayDisplacedTable, else GetOrCreateLayer(t).Own
    internal TableLayer? TryGetLayer(TableId t);  internal TableLayer GetOrCreateLayer(TableId t);
    internal void AdoptDeferred();                                   // Database.TakeRound prologue
    internal void NoteUnsealed(HiddenSet set);
    internal SortedWritesTable? AncestorLayer(int j, TableId t);    // j == 0: schema[t].Root
    internal TableInfo? AncestorInfo(int j, TableId t);
    internal bool TryFindVisible(TableId t, uint[] key, int offset, ulong keyHash, int top,
                                 out SortedWritesTable? layer, out int j, out int row);           // §6.5
    public bool TryGetVisibleRow(TableId t, ReadOnlySpan<uint> key, Span<uint> destination);
    public bool TryGetVisibleColumn(TableId t, ReadOnlySpan<uint> key, ColumnId column, out uint value);
    internal bool CatchUpRows(uint epoch);  internal bool RebuildPass(uint epoch);   // §7
    internal void CommitCursors();  internal void SealHidden();
    internal void AfterMerge();                                      // SealHidden + InvalidateViews(dirty), X40
    internal void InvalidateViews(bool all);
    public TableView GetView(TableId t);                             // §6.3
    internal void ClearTable(TableId t);                             // §6.7
    internal void ScanOwn<TSink>(TableId t, ref TSink sink) where TSink : struct, IRowSink;
    internal void ScanAncestorsMentioning<TSink>(TableId t, uint id, ref TSink sink) where TSink : struct, IRowSink;
    internal void Freeze();  internal void Release();
    internal void CheckInvariants(List<string> violations);
}
```

**`GetOrCreateLayer(t)`** (C14, decision 2). It may be called from any task of the owner's run (an action-batch clone staging, `Execution/ActionBuffer.cs:93,105`):

```text
if t < slots.Length && Volatile.Read(ref slots[t]) is {} L: return L          // fast path, no lock
lock (layerLock):
    re-read the slot; return it if set
    throw if the owner is frozen, disposed, faulted or contradicted (volatile flags)
    s = schema[t] ?? throw
    if s.IsLattice == false && s.IsContextPrivate == false:
        throw InvalidOperationException naming the table and decision 2       // copy-on-touch counts as a write
    L = new TableLayer(this, s, Depth)                                         // Own = s.Root.CreateLayer(L); Info = new TableInfo(s.Name, Own, Serial)
    owner.Database.Catalog.Publish(t, L.Own)                                   // copy-on-write map, Volatile.Write
    publish L in slots[t] (Volatile.Write; a grown array is copied and published whole)
    if InSearch: deferred.Add(L) else owner.Database.AdoptLayer(L)             // tables.Insert(t, L.Info)
    dirty[t] = true; Statistics.LayersCreated++
return L
```

It never clears a cached view and never bumps `ViewGeneration`: an empty layer adds no visible row and hides nothing, so every view taken before it exists stays correct until the next `MergeAll` epilogue, which rebuilds the views of dirty tables.

**`TableCatalog`** (`Tables/TableCatalog.cs`) changes:

```csharp
DenseIdMap<TableId, Table> tables;                     // no longer readonly; this[], TryGetTable and GetForStaging read Volatile.Read(ref tables)
public Func<TableId, Table>? Resolver { get; init; }   // layered catalogs only
public Table GetForStaging(TableId id);                // TryGetTable, else Resolver, else KeyNotFoundException
internal void Publish(TableId id, Table table);        // clone the map, insert, Volatile.Write; the caller holds the layer lock
```

`Add` and `Set` are unchanged (exclusive access at the root). `ExecutionState.CreateBuffer` (`Tables/ExecutionState.cs:198`) calls `catalog.GetForStaging(table).NewBuffer()`. The cost at the root is one volatile load per (state, table) buffer creation.

**Adoption.** `Database.TakeRound` (`Database/Database.Merge.cs`) starts with `layers?.AdoptDeferred()`, which moves deferred layers into `Database.tables` on the domain thread. Every layer is therefore adopted before `MergeAll` takes a round, and `IsTakenOut(t)` (`TryGetLayer(t) is { IsAdopted: true } && tables.ContainsKey(t) == false`) is exact whenever the write phase consults it.

`SortedWritesTable.CreateLayer(TableLayer layer)` (new, the only signature) returns:

```csharp
new SortedWritesTable(keyCount, Arity, sortBy, RebuildColumns, merge, ParallelHeuristics.Serial)
```

That is one shard, never parallel. It sets the new table's `layer` field and `CompactionGate` (the owner's gate, or null if private).

**`TableLayer`** (`Layers/TableLayer.cs`):

```csharp
internal sealed class TableLayer
{
    public ContextTables Owner; public LayeredTableSchema Schema; public TableId Id; public int Depth;
    public SortedWritesTable Own { get; }  public TableInfo Info { get; }  public bool IsAdopted { get; }
    public bool IsCleared { get; }  public bool HasRetractions { get; }  public int Version { get; }
    public HiddenSet? HiddenOver(int layer);
    public bool IsHiddenHere(int layer, int row);                 // any state
    public bool Hide(int layer, int row, HideReason reason);      // false if already hidden at this level
    internal void SetCleared();  internal void SealHidden();
}
```

`Hide(j, r, reason)` runs on the domain thread only (inside `MergeAll` or right before it):
1. Lazily creates `hidden[j] = new HiddenSet(A.Version.Major)` for ancestor layer `A = Owner.AncestorLayer(j, Id)`, and registers it with `Owner.NoteUnsealed`.
2. Returns false if `set.Add(r, A.Rows.Count)` reports the row was already present.
3. If the set was sealed before the add, registers it with `Owner.NoteUnsealed` (a sealed set reopens its tail).
4. Bumps `Version`, `Owner.Owner.Database.ChangeStamp` and `dirty[Id]`, increments `Statistics.RowsHidden`, and in DEBUG records the reason.

**`AncestorCursor`** (`Layers/AncestorCursor.cs`) holds, per ancestor depth:
- `TableVersion[] Rows` (by table index; `(0,0)` means "layer not seen yet");
- `long Stamp` (the ancestor's `ChangeStamp`);
- `PendingRows` and `PendingStamp`, committed after the pass loop.

Only depths in `[settled, Depth)` have cursors. Frozen ancestors that have been fully read are *settled*, and their cursors are dropped.

### 6.3 Hidden sets, filters and views (X20, X40)

**`HiddenSet`** (`Layers/HiddenSet.cs`):

```csharp
internal sealed class HiddenSet
{
    public const int MinBitsetCount = 64;  public const int BitsetDivisor = 32;
    public HiddenSet(int generation);
    public int Generation { get; }  public int Count { get; }  public int Version { get; }
    public bool IsSealed { get; }  public bool IsBitset { get; }
    public bool Add(int row, int layerRowCount);                    // a sealed set reopens: the row goes to the tail
    public bool Contains(int row);                                  // any state
    [MethodImpl(AggressiveInlining)] public bool ContainsSealed(int row);   // Debug.Assert(IsSealed)
    public void Seal(int layerRowCount);                            // merges the tail into a new sorted array or the bits
    internal int[]? SortedRows { get; }  internal int SortedCount { get; }  internal ulong[]? Bits { get; }   // M12, no ref returns
    public static HiddenSet Union(ReadOnlySpan<HiddenSet> sets, int layerRowCount);   // inputs sealed; result new and sealed
    public void CopyTo(List<int> rows);
}
```

**Representation and operations:**
- A sorted `int[]` plus an unsorted `HashSet<int>` tail, until `Count ≥ 64 && Count > n/32`. Then it switches to a `ulong[]` bitset.
- `Seal` merges the tail and publishes the new sorted array (or bits) by reference.
- Sets never shrink while their owner is alive.
- All rows are below the layer's physical row count.

**Sealing discipline (X40).** Hides happen only on the owner's domain thread: inside `MergeAll` (`InsertLayered`'s `KeyHeld`, `DeleteLayered`'s `Retracted`) or right before the `MergeAll` of the same pass (catch-up's `ApplyHides`, step 4). `Database.MergeAll` and `Database.MergeTable` call `layers?.AfterMerge()` in their `finally` blocks, after `ResetIndexesAndEstimateSize`. `AfterMerge` seals every set registered with `NoteUnsealed` since the last seal, then invalidates the views of dirty tables. `Rebuild`, `ClearTable` and `Freeze` call `SealHidden` as well. So outside a `MergeAll` of its owner, every set is sealed (I18), and every reader (`HiddenRows.Contains`, `HiddenCursor`, `HiddenSet.Union`, index builds, headers, point lookups) agrees with the write-phase `IsHiddenHere`.

**The filter and walker types:**

| Type (file) | What it does |
|---|---|
| `HiddenRows` (`Layers/HiddenRows.cs`, readonly struct) | Fields `HiddenSet? Frozen` and `HiddenSet? Live` (both sealed). `IsNone`. `[AggressiveInlining] bool Contains(int row)` uses `ContainsSealed` on each, so at most two probes. `HiddenCursor Cursor()`. |
| `HiddenCursor` (`Layers/HiddenCursor.cs`, struct) | Answers `IsHidden(row)` for nondecreasing rows by a merge walk with galloping, or a bit test. |
| `VisibleRowFilter` (`Layers/VisibleRowFilter.cs`, struct `IOffsetFilter`) | Keeps rows that are not tombstoned and not hidden; used with `Subset.Retain`. |
| `HiddenOnlyFilter` (`Layers/HiddenOnlyFilter.cs`) | The same for tables without tombstones (`DisplacedTable`). |
| `HiddenSkippingSink<TSink>` (`Layers/HiddenSkippingSink.cs`) | Drops hidden rows before the inner `IRowSink`. Used on the `DisplacedTable` and projecting paths. |
| `HideReason` (`Layers/HideReason.cs`) | `byte` enum: `Mentions = 1`, `KeyHeld`, `Retracted`, `Restamped`. |

**`LayerView`** (`Layers/LayerView.cs`, sealed, immutable):
- fields `int Depth`, `TableInfo Info`, `Table Table`, `HiddenRows Hidden`, `bool IsFrozen`;
- `bool HasInvisible => Table.HasStaleRows || Hidden.IsNone == false`, which replaces `HasStaleRows`;
- `Subset RefineVisible(Subset s)`: `Hidden.IsNone ? Table.RefineLive(s) : Table.RefineVisible(s, Hidden)`.

**`TableView`** (`Layers/TableView.cs`, `Layers/TableView.Lookup.cs`, sealed, immutable):

```csharp
internal sealed partial class TableView
{
    internal TableView(TableId id, TableSpec spec, LayerView?[] layers);
    public TableId Id; public TableSpec Spec; public LayerView?[] Layers; public int LayerCount; public uint PresentMask;
    public LayeredSubset AllRows(SubsetArrayPool? pool);       // present layers with rows: Dense(0, n_l)
    public bool AnyVisible(int layer, SubsetRef subset);        // zero-variable atoms (map §8.1 row 2)
    public long ApproximateCount { get; }  public uint LatestStamp { get; }   // D16
    public long VisibleCount { get; }                           // exact: Σ live rows − live hidden rows; computed once per view, O(hidden)
    // Lookup partial (C4): FindRow(k0 | k0,k1 | k0,k1,k2 | uint[] key, int offset | ReadOnlySpan<uint> key, out int layer),
    // TryGetColumn (1-, 2-, 3-, N-key forms), TryGetRow(ReadOnlySpan<uint> key, Span<uint> dest, out int layer, out int row)
}
```

**`ContextTables.GetView(t)`.** Cached in `TableView?[] views`, published with `Interlocked.CompareExchange`. When building a view:
- **Private table:** only the own layer (index `Depth`) is present.
- **No own layer:** returns the parent's view instance (or `schema.RootView(t)`). A context hides nothing for a table it never touched.
- **Otherwise,** for each ancestor layer j:
  - the layer is absent if any level in `(j, Depth]` cleared t;
  - else `Frozen` = the frozen-prefix part, taken from the deepest frozen ancestor f > j: its own frozen view's `Layers[j].Hidden.Frozen`, a single set precomputed at its freeze;
  - `Live` = the sets of the non-frozen levels in `(max(j, f), Depth]`: none → null, one → that object, more → `HiddenSet.Union` (`Statistics.FilterMerges++`).

  The own layer is present with `HiddenRows.None`.
- **A frozen context's views** have `Live == null`: everything is folded into `Frozen` at freeze. A frozen context never caches beyond its arrays: for a table id at or beyond them, `GetView` returns the parent's view (or `RootView`) without writing, and `TryGetLayer` returns null (X43).

**Invalidation.** `InvalidateViews(all: false)` (the `MergeAll` epilogue) drops the cached views of tables whose structure changed since the last invalidation (a layer created, a hide added, a clear) and bumps `ViewGeneration` if any did. `InvalidateViews(all: true)` (the end of a `Rebuild` that caught up with an ancestor, `ClearTable`, `Freeze`) drops every cached view. A view references live layer tables, so rows appended to a present layer need no invalidation. A descendant's cached views may point at this context's sets or view instances, but the descendant is not clean until its next catch-up (`ChangeStamp` moved), which invalidates its whole cache.

### 6.4 Visibility and Lemma F

Visibility, for a table that is not private:

```
visible_V(j, r) ⇔ live_j(r) ∧ ∀ i ∈ (j, d_V]: ¬cleared_i(t) ∧ r ∉ H_i[t][j]
```

**Lemma F (first hit decides).** Let V and its ancestors be clean. If j is the topmost layer whose key hash holds key k, with row r, then no row with key k in a lower layer is visible to V. So the answer for k is r if it is visible, and "absent" otherwise.

*Proof.* I12 for context j says no other row with key k is visible to j. Visibility for V checks a superset of j's levels. ∎

Lemma F also holds during V's own merge, for the ancestor layers: they are clean and fixed.

### 6.5 Point lookups and action wiring (C4)

**Write phase** (merges, merge functions, catch-up, `PredictRow`): `ContextTables.TryFindVisible`.

```text
s = schema[t]; floor = ClearedFloor(t)          // max cleared level in (0, Depth], else 0
for j = top down to 0:
    if j < Depth && s.IsContextPrivate: break
    if j < floor: break
    A = j == Depth ? TryGetLayer(t)?.Own : AncestorLayer(j, t); if A is null: continue
    if j == Depth && owner.Database.IsTakenOut(t):
        throw InvalidOperationException (same-stratum read; the root throws KeyNotFoundException)   // X2
    row = A.FindRowHashed(key, offset, keyHash); Statistics.LookupProbes++
    if row < 0: continue
    if j < Depth && HiddenBetween(t, j, row): return false        // Lemma F
    layer = A; return true
return false
HiddenBetween(t, j, row) = ∃ i ∈ (j, Depth]: chain[i].TryGetLayer(t)?.IsHiddenHere(j, row)
```

`TryGetVisibleRow` and `TryGetVisibleColumn` copy span keys into a `[ThreadStatic] uint[]` and hash them once with `TableKeyHash.Hash`. `SortedWritesTable.FindRowHashed(uint[] key, int offset, ulong keyHash)` is new: the body of `FindRowCore` (`Tables/SortedWritesTable.Lookup.cs:198`) without the hash.

**Read phase** (rules, the lift, harness reads): `TableView.FindRow`.

```text
for j = LayerCount-1 down to 0:
    lv = Layers[j]; if lv is null: continue      // absent, or below a cleared level (all lower ones are absent too)
    r = lv.Table.FindRow(k...); if r < 0: continue
    if lv.Hidden.Contains(r): break             // Lemma F
    layer = j; return r
return -1
```

**Action wiring** (`Layers/ExecutionState.Layers.cs`, a new partial of `ExecutionState`):
- `ExecutionState` gains `ContextTables? layers`, `RetractionGuard? guard` and `ViewSnapshot? views`, plus `bool InChildContext => layers is not null`.
  - The `DatabaseView` constructor (`Actions/ExecutionState.Database.cs`) sets `layers = view.Owner.Layers` and `guard = view.Owner.GuardForNewState()` (§6.7).
  - `Database.RunRuleSet` sets `views = ruleSet.Views` on the run's root state.
  - The clone constructor (`Tables/ExecutionState.cs`) copies `view`, `viewResolved`, `layers`, `guard` and `views`, so action-batch clones never fall back to `DatabaseView.TryGetForCatalog`.
- **`ReadTable`** (`Actions/ExecutionState.Predicted.cs:175`) becomes the single dispatch point for committed-table reads: it throws `InvalidOperationException` when `layers` is set. Every caller branches first:

  | Call site | Root path (unchanged) | Layered path |
  |---|---|---|
  | `Actions/ExecutionState.Actions.cs:137` `LookupWithDefault` | `VectorizedLookup.LookupWithDefault(ReadTable(t), …)` | `VectorizedLookup.LookupWithDefault(ViewFor(t), …)` |
  | `:143` `Lookup` | `LookupRow(ReadTable(t), …)` | `LookupRow(ViewFor(t), …)` |
  | `:208` `LookupOrInsertDefault` | buffer first, then `LookupRow(ReadTable(t), …)` (egglog's order, `:205`) | `LookupRow(ViewFor(t), …)`; the buffer is created at the first miss, so a lookup that hits creates no layer; the arity check uses `layers.Schema` |
  | `:290` `LookupWithFallback` | `LookupRow(ReadTable(t), …)` | `LookupRow(ViewFor(t), …)` |
  | `Predicted.cs:54`, `:78` `PredictRow` (both forms) | `ReadTable(t).TryGetRow` | `layers.TryGetVisibleRow`; arity and spec from `layers.Schema` |
  | `Predicted.cs:94`, `:120` `PredictColumn` (both forms) | `ReadTable(t).TryGetRowColumn` | `layers.TryGetVisibleColumn` |

  `ViewFor(t)` is `views?.Get(t) ?? layers.GetView(t)`. For `t == $uf` the lookups use the `OverlayDisplaced` arm. The new `VectorizedLookup` overloads over `TableView` run `LayeredKey1Lookup`, `LayeredKey2Lookup`, `LayeredKey3Lookup` or `LayeredKeyNLookup` (`Actions/`, structs mirroring `SortedKey*Lookup` and calling `TableView.TryGetColumn`).
- `ExecutionState.GetTable` (`Actions/ExecutionState.Database.cs:116`) throws when `layers` is set (it reads `DatabaseView.GetTable`, which throws in a child).
- `ExecutionState.TableIds` lists `LayeredSchema.Ids` in a child (§3.4).
- `ExecutionState.Containers` (`Containers/ExecutionState.Containers.cs:16`) reads `RequireView().Owner.Containers`, which in a child is the root's instance (the child constructor copies the reference).
- New `ExecutionState.VisibleRowCount(TableId t)`: `ReadTable(t).Count` at the root, `ViewFor(t).VisibleCount` in a child. The bridge's `TableAction.row_count` (`eb/lib.rs:1293-1294`, `state.get_table(t).len()`) uses it.

### 6.6 Inserts, merges and deletes in a child (X38)

**`Tables/SortedWritesTable.Layered.cs`** (new partial):

```csharp
TableLayer? layer;  DescendantGate? compactionGate;  uint[]? layerScratch;
internal TableLayer? Layer => layer;  internal DescendantGate? CompactionGate { get; set; }
internal SortedWritesTable CreateLayer(TableLayer layer);
internal int FindRowHashed(uint[] key, int offset, ulong keyHash);
internal TableChange MergeLayered(ExecutionState state, TableLayer layer);
bool DeleteLayered(TableLayer layer);  bool InsertLayered(ExecutionState state, TableLayer layer);
internal Index<ColumnIndex>? SharedRebuildIndex();  // null when RebuildColumns is empty; frozen: assert current, return; else RefreshRebuildIndex()
internal bool IsShared { get; }                     // set by Freeze
```

`SortedWritesTable.Merge` (`Tables/SortedWritesTable.cs`) becomes:

```csharp
layer is { } l ? MergeLayered(state, l) : /* today's DoDelete; DoInsert; MaybeRehash */
```

**`MergeLayered`:**
1. `removed = DeleteLayered(l)`.
2. `added = InsertLayered(state, l)`.
3. `MaybeRehash()`, which is gated (§6.8).
4. If `added && l.Schema.IsContradiction && l.Owner.Owner.Options.StopOnContradiction`, call `SetContradicted()`.

**`DeleteLayered`.** For each key in `pending.RemovalQueue(0)`, as given:
- an own hit is tombstoned (`SetStaleShared`);
- an own miss with `TryFindVisible(top: Depth-1)` hides the ancestor row (`Retracted`) and sets `HasRetractions`;
- otherwise it is a no-op.

Keys are not canonicalized: visible rows are canonical in the context (I13), so a key that is not canonical matches nothing, which is also what egglog's root does with a stale key once rebuilt. Legality was checked at staging (§6.7). Rebuild-staged removals always hit own rows.

**`InsertLayered`.** Over `pending.RowQueue(0)`, in queue and staging order. Each incoming row q is first copied into `layerScratch` and prepared:

```text
for c in RebuildColumns: q[c] = Owner.Owner.UnionFind.Find(q[c])          // canonical here (I13), X38
if SortColumn >= 0: q[SortColumn] = state.Timestamp                         // the merge's epoch (I19, I21), X38
own hit:          today's InsertSerial hit branch (merge into the own row)
elif TryFindVisible(top: Depth-1, out A, out j, out r):
    if merge.Merge(state, A.Rows.GetRow(r), q, output) == false: continue     // store nothing; r stays visible
    AssertMergeOutput; CheckSortValue; append output; insert hash entry
    layer.Hide(j, r, KeyHeld)                                                  // hide AFTER the output exists
else:             today's new-key branch
```

Properties:
- Rows of one key fold left from the visible row.
- `UnionIdMerge` stages into the child's `OverlayDisplacedTable` through the catalog (C4).
- Outputs keep q's key and timestamp.
- Every staging path of the engine already stages canonical cells stamped with `state.Timestamp` (rule actions bind canonical values and run canonical constants, X19; catch-up and rebuild copies are canonicalized and re-stamped; `HostWrites` stamps at flush). The preparation step changes only raw writes, and costs one `find` per id cell of an incoming row, in the child only (`Statistics.RowsCanonicalized` counts rows it changed).
- The union-find may change later in the same `MergeAll` (`$uf` merges in its own stratum). A row canonical when appended that becomes stale through such a union mentions a journal id appended at or after it, which the next pass's step 3 rewrites (I13).

**`Context.Write`** (`Contexts/Context.Writes.cs`):

```text
using Domain.Enter(); ThrowIfNotWritable(); PrepareForWrite()
e = BeginEpoch()
try   { st = Database.CreateExecutionState(e); res = action(st); st.Flush(); Database.RecordFailure(st)
        if IsRoot: Debug-only AssertPendingSortCells(e) on every registered table      // X38
        Database.MergeAll(e) }                                                        // epilogue seals and invalidates (X40)
catch when !IsRoot { faulted = true; throw }
finally { EndEpoch() }
```

It leaves rebuild work pending (spike semantics): a union makes the context not clean until `Rebuild`. In a child, incoming rows are canonical when stored, so a write with a stale id lands on the canonical key and merges with the visible row there. At the root, `Context.Write` is egglog's raw write: rows are stored as given, the sort cell is the caller's contract (asserted in DEBUG by `SortedWritesTable.AssertPendingSortCells`, which walks the pending row queues without dequeuing), and a stale row is rewritten by the next rebuild or settle.

**`PrepareForWrite()`:**
- if the parent is not frozen, call `Parent.Rebuild()`;
- if `!IsUnionFindCurrent || Tables!.NeedsCatchUp`, call `Rebuild()`.

### 6.7 Retraction (C10, decision 1, R1)

**`RetractionGuard`** (`Layers/RetractionGuard.cs`): `CheckRemove(TableId)`, `CheckClear(TableId)` and `CheckSubsume(TableId)`. A context-private table is always allowed. Throws are `InvalidOperationException`s naming the table and the rule.

| Operation | Root, no live descendants | Root, live descendants | Child, no live descendants, parent frozen | Child, no live descendants, parent writable | Child, live descendants |
|---|---|---|---|---|---|
| `CheckRemove` | ok | throw | ok | **throw (R1)** | throw |
| `CheckClear` | ok | throw | ok | **throw (R1)** | throw |
| `CheckSubsume` | ok | throw | ok | ok | throw |

**Enforcement points:**
- Both `ExecutionState.StageRemove` overloads (`Tables/ExecutionState.cs:131`, `:145`) call `guard?.CheckRemove(table)`. `Database.GuardForNewState()` returns `null` at a root whose gate has no live descendants and the store's guard otherwise; it is snapshotted when the state is created, which is sound because a child cannot be created while a state of the root is in use (structural calls are refused re-entrantly, §8.3). A root with no descendants therefore pays one null test per staged removal call. Rebuild and refresh stage through `MutationBuffer.StageRemove` and are never treated as retractions.
- `Database.ClearTable` calls `RetractionGuard?.CheckClear(table)`. `$uf` is never private, so clearing it throws while descendants live.
- The bridge's subsume action calls `CheckSubsume`.

**`ContextTables.ClearTable(t)`:**
1. `Guard.CheckClear(t)`.
2. `L = GetOrCreateLayer(t)`.
3. `L.Own.Clear()`, which bumps only the own generation (no live descendants).
4. If t is not private, `L.SetCleared()`.
5. Reset `L`'s indexes, bump `ChangeStamp`, `SealHidden()`, `InvalidateViews(all: true)`.

### 6.8 Compaction gate (C12, X27)

- `SortedWritesTable.MaybeRehash` (`Tables/SortedWritesTable.Rehash.cs:17`) starts with:

  ```csharp
  if (compactionGate is { } g && g.HasLiveDescendants) { compactionsBlocked++; return; }
  ```

- New `ForceRehash()` compacts when `staleRows > 0` and the gate allows it (`Freeze` uses it).
- New `internal int CompactionsBlocked`.
- Gates:
  - root tables that are not private: the root `Context.Gate`;
  - child layers that are not private: the owner's `Gate`;
  - private tables: none.
- **`DescendantGate`** (`Layers/DescendantGate.cs`): `HasLiveDescendants` (volatile read), `LiveDescendants`, `Enter()`, `Exit()` (interlocked). Every `Context` owns one.

### 6.9 Contradiction stop flag (L4, X11)

`ContextTables.SetContradicted()`:
- sets `owner.IsContradicted` (sticky) and bumps `ChangeStamp`;
- applies only when `Options.StopOnContradiction` is set.

It is triggered by:
1. `MergeLayered` adding a contradiction row;
2. catch-up seeing a visible ancestor contradiction row;
3. the constructor finding a visible row in the parent's view: `GetView(c).FindRow([], out _) >= 0`.

Once it is set:
- `Database.MergeAll` stops after the current round: the round loop checks `layers?.Owner.IsContradicted == true`, and the remaining notifications stay pending. The epilogue still seals (X40).
- `RebuildPass` returns false and `Rebuild` returns at once.
- `GetOrCreateLayer` and writes throw.
- Reads throw except `IsContradictory`.

`IsContradictory` is `IsContradicted || (Schema.Contradiction is { } c && GetView(c).FindRow([], out _) >= 0)`.

### 6.10 Lift support reads (L1)

Both require `IsClean` and throw on the root.

- **`Context.ScanOwn<TSink>(t, ref sink)`** scans `TryGetLayer(t)?.Own.All()`, skipping tombstones. These rows are canonical here (I13). O(own rows).
- **`Context.ScanAncestorsMentioning<TSink>(t, id, ref sink)`**:
  - returns at once for a table without rebuild columns (no id can be mentioned), and for a local id (I10);
  - for each `j ∈ [ClearedFloor(t), Depth)`, takes `AncestorLayer(j, t).SharedRebuildIndex()!.Data.TryGetSubset(id, out rows)`;
  - delivers each live row with `!HiddenBetween(t, j, r)`;
  - id must be canonical here.

  Cost: O(uses of id); nothing is scanned.

---

## 7. Rebuild and catch-up (C5, C8)

### 7.1 `Context.Rebuild()` (`Contexts/Context.Lifecycle.cs`): the only loop (X10)

```text
if frozen: return false
ThrowIfDisposed(); using Domain.Enter(); ThrowIfFaulted()
if Parent is not null && !Parent.IsFrozen: Parent.Rebuild()
if IsContradicted: return false
if IsRoot ? (journalMark == DisplacedCount && notifications empty) : IsClean: return false   // no clock churn (X42)
first = true; changed = false; caughtUp = false
try:
  loop:
    e = BeginEpoch()
    try:
      pass = false
      if first && !IsRoot:
          pass |= CatchUpUnionFind(e) != Journal.Count            // C8 step 1
          caughtUp = Tables.CatchUpRows(e) | caughtUp             // C8 step 2 (§7.2)
          pass |= caughtUp
          pass |= RefreshDirtyContainerIds(e)                     // C13 interim (§7.4)
      pass |= IsRoot ? Database.RebuildPass(Store.UnionFindTable, Store.Schema.FunctionTables, e)   // egglog body
                     : Tables.RebuildPass(e)                      // C8 steps 3-5 (§7.3)
      if !IsRoot: EqContainerGuard.Check(this)                    // §7.4
    finally: EndEpoch()
    changed |= pass; first = false
    if !pass || IsContradicted: break
catch when !IsRoot: faulted = true; throw                         // X12
journalMark = DisplacedCount
Tables?.CommitCursors(); Tables?.SealHidden(); Tables?.InvalidateViews(all: caughtUp || cursors moved)
#if DEBUG: CheckInvariants()
return changed
```

At the root this is `Database.Rebuild(uf, tables, clock)` (`Containers/Database.Containers.cs:122`), with identical numbering: read the clock, run the pass, increment, until nothing changes. The root `journalMark` is `RootUnionFind.Count` at the end of the loop. `Database.RebuildPass` merges any pending root writes inside `ApplyRebuild`'s `MergeAll`.

### 7.2 Catch-up of ancestor rows (C8 step 2)

`ContextTables.CatchUpRows(uint e)`:

```text
if settled == Depth: return false
for a in [settled, Depth): generation check: every seen ancestor layer has Version.Major == cursor's, else Fault (I16)
st = Database.CreateExecutionState(e); hides = []; floor = Owner.SemiNaiveFloor
for a in [settled, Depth), for each registered t not private with a layer at depth a:
    A = AncestorLayer(a, t); v = A.Version; from = cursors[a].RowsOrDefault(t).Minor; cursors[a].PendingRows[t] = v
    if from == v.Minor || a < ClearedFloor(t): continue
    for r in [from, v.Minor):
        cells = A.Rows.GetRow(r); Statistics.CatchUpRowsExamined++
        if cells[0] == Value.StaleRep || HiddenBetween(t, a, r): continue
        if s.IsContradiction: SetContradicted(); if Owner.IsContradicted: return true   // staged copies are dropped unflushed; the context now answers only IsContradictory
        if any rebuild column x has Owner.UnionFind.Find(x) != x:  StageCopy(t, a, r, Mentions, canonicalize: true)   // (a)
        elif TryGetLayer(t)?.Own.FindRow(cells[..k]) >= 0:         StageCopy(t, a, r, KeyHeld, false)              // (b)
        elif s.SortColumn >= 0 && cells[s.SortColumn] < floor:     StageCopy(t, a, r, Restamped, false); RestampedCopies++  // (c′)
        // (c) otherwise visible as is, canonical here
    cursors[a].PendingStamp = Stamp(a)
st.Flush(); ApplyHides(hides)                                     // hides after the copies are pending (X12)
return hides.Count > 0
```

Notes:
- **`StageCopy`** stages the row through `st`, rewritten with the child's rebuilder when `canonicalize` is set, and with `buf[sort] = e` (re-stamped). It records `(j, r, reason)` in `hides`. `ApplyHides` calls `GetOrCreateLayer` (the decision 2 check) and `Hide`. A failed `Hide` means the row was already hidden, and its staged copy is dropped before the flush. The hides are sealed by the `MergeAll` of the same pass (X40), or by `Rebuild`'s epilogue when the pass ends early.
- **Branch (a)** also catches a root row that was stale *at the root* when appended (a raw root write, X38): its id is non-canonical in this context too, since `find_C = overlay ∘ find_P`.
- **`NeedsCatchUp`:** some a in `[settled, Depth)` has `Stamp(a) != cursors[a].Stamp`, where `Stamp(0)` is the root's `Database.ChangeStamp`.
- **The semi-naive floor of (c′).** `Context.SemiNaiveFloor` is `max(CreatedAt, the largest epoch this context passed to NoteRuleRun)`. `NoteRuleRun(T)` (called by the bridge at the start of each run, §10.3) raises it; nothing else writes it, and only this context's catch-up reads it. Every watermark this context can use is at most its floor: its own watermarks are epochs it noted, and an inherited watermark is at most its `CreatedAt` (§10.2). A descendant checks ancestor appends against its own floor in its own catch-up, because it holds its own cursor into every unsettled ancestor, so no floor needs to travel up or down. Frozen contexts never catch up and never read it.
- **(c′)** is the C7 safeguard. It never fires when writers stamp with the state's timestamp (I21): every engine and bridge path does, and children enforce it at the merge (X38). Tests assert `RestampedCopies == 0`.
- Ancestor tombstones need no work: a replaced row reappears as an append.

### 7.3 One rebuild pass in a child (C5)

`ContextTables.RebuildPass(uint e)`:

```text
if Owner.IsContradicted: return false
from = Owner.journalMark; end = Owner.Journal.Count; Owner.journalMark = end
ownLosers = { Journal.Ids[i] | i ∈ [from, end), Owner.IsOwnDisplacement(id) }                    // X8
own = adopted own layers with nonempty RebuildColumns, ascending
if own nonempty:                                                                                  // step 3 (X9)
    Database.RunWithTakenOut(schema.UnionFind, own, new RebuildTableOperation(schema.UnionFind, Owner.UnionFindTable, e))
    // RunOnTablesCore notifies every layer whose ApplyRebuild staged a rewrite and records failures
st = Database.CreateExecutionState(e); hides = []
if ownLosers nonempty:                                                                            // step 4
    for t in registered tables, not private, with s.RebuildColumns nonempty, ascending:
        for j in [ClearedFloor(t), Depth): A = AncestorLayer(j, t); if A is null: continue
            gather = new ColumnIndexGatherSink(A.SharedRebuildIndex()!.Data)
            foreach x in ownLosers: gather.AddValue(x)          // new ColumnIndexGatherSink.AddValue
            n = gather.SortUnique(); Statistics.AncestorRowsExamined += n
            for r in gather.Rows[..n]: if live && !HiddenBetween(t, j, r): StageCopy(t, j, r, Mentions, canonicalize: true)
            gather.Release()
st.Flush(); Database.RecordFailure(st); staged = ApplyHides(hides)
return Database.MergeAll(e) || staged                                                            // step 5
```

Notes:
- Step 3 uses `SubsetTracker.RecentUpdates(uf, overlayTable)` over the journal suffix, plus `FirstRowSinceRebuild`. `RebuildTableOperation.Run` creates a state over the child's view, calls `ApplyRebuild`, flushes and records failures (`Database/RebuildTableOperation.cs`), and `RunOnTablesCore` (`Database/Database.Rebuild.cs`) notifies the table when it returns true, so `MergeAll` merges the rewrites. `$uf` is taken out meanwhile, as at the root.
- **Incremental path for layers.** `SortedWritesTable.RebuildWith` (`Tables/SortedWritesTable.Rebuild.cs`) takes the incremental branch for a layered table whenever `delta ≤ max(rows, 64)`, instead of egglog's `ShouldRebuildIncrementally` (which requires more than 10 000 rows); `RefreshRebuildIndex` on a small own layer is cheap. Otherwise it takes `RebuildFull` over the own rows. The root keeps egglog's rule.
- Imports are rewritten in own rows by step 3. Their ancestor rows are already invisible at P (I13), so step 4 needs own losers only.
- **Worked trace.**
  - The root holds `pair(1,2)=5` and `pair(3,2)=6`. C unions 3 with 1, and 3 loses.
  - Step 4 finds `pair(3,2)=6`, hides it (`Mentions`) and stages `pair(1,2)=6`.
  - `InsertLayered` hits the root's `pair(1,2)=5`, which is visible. `UnionIdMerge(5,6)` stages `union(5,6)` into C and returns false, so nothing is stored.
  - The next pass has `ownLosers = {6}`.
  - Rows mentioning the winners 1 or 5 are never copied.
- **Worked trace (own rows).** C holds own row `f(3)=7`; C unions 3 with 1 and rebuilds. Step 3's `ApplyRebuild` on `f`'s layer stages `remove f(3)`, `insert f(1)=7`, returns true, and `RunOnTablesCore` notifies `f`; `MergeAll` merges both; C is clean with `f(1)=7` only.

### 7.4 Containers in the loop (C13 interim; decision 9)

- **Dirty log.** `Containers/ContainerDirtyLog.cs` lives on `Store`, with `End`, `Append`, `Since(ref int cursor)` and `Clear`.
  - The root's `Database.RebuildPass` appends `containerRebuild.DirtyIds` while `owner.Gate.HasLiveDescendants`.
  - A child's `RefreshDirtyContainerIds(e)` consumes `Since(ref dirtyCursor)` and, when it is nonempty, calls `Database.RunOnTables(own layers with rebuild columns, new RefreshTableOperation(ids, e))` (`Database/RefreshTableOperation.cs`), which notifies every layer that re-staged rows. Root rows are re-stamped by the root itself and arrive as appends.
  - `Context.Dispose` clears the log when the root gate reaches zero.
- **Eq guard.** `EqContainerGuard.Check(ctx)` (`Contexts/EqContainerGuard.cs`): if `ContainerValues.HasEqContainers`, then for the journal rows of this pass with `IsOwnDisplacement`, it throws `NotSupportedException` when `AnyEqContainerMentions(id)` (G-b). The context is then faulted.
- **Child databases never rebuild containers.** `RebuildContainers`, `RebuildPass` and `Rebuild` throw `InvalidOperationException` when `owner` is a child (§3.4).

### 7.5 `IsClean`

```text
IsClean (root)  = !disposed && !faulted && (frozen || journalMark == DisplacedCount)                       // X42
IsClean (child) = !disposed && !faulted && (frozen ||
                  (Database.Catalog.Notifications.IsEmpty && journalMark == DisplacedCount &&
                   IsUnionFindCurrent && !Tables.NeedsCatchUp && Parent.IsClean))
```

- At the root, pending notifications do not make it unclean: staged writes are invisible until merged, as in egglog. A child's view never includes them either; when the root merges them, its `ChangeStamp` moves and the child catches up.
- The recursion stops at the first frozen ancestor, so the cost is O(unfrozen ancestors), as D16 requires.
- `NotificationList.IsEmpty` (`Concurrency/NotificationList.cs`) is new: `lock` plus a count of 0.
- Reads call `ThrowIfNotReadable()`:
  - disposed → `ObjectDisposedException`;
  - contradicted → `InvalidOperationException("…only IsContradictory may be read")`;
  - not clean → `InvalidOperationException` naming `Rebuild()`;
  - DEBUG: `Debug.Assert(Tables is null || Tables.UnsealedCount == 0)` (I18, X40).

### 7.6 Root settling (X38)

`Context.Settle()` (root only, `Contexts/Context.Lifecycle.cs`) runs where egglog has no counterpart: in `CreateChild` and in `Freeze`, after `Rebuild()`.

```text
if !IsRoot: return
if no registered table has SortedWritesTable.HasRowsSinceRebuild(uf): return       // O(tables)
e = BeginEpoch()
try:   Database.RunWithTakenOut(uf, Schema.FunctionTables, new SettleTableOperation(uf, RootUnionFind, e)); Database.MergeAll(e)
finally: EndEpoch()
Rebuild()                                                                           // when the merge staged unions
```

- **`SortedWritesTable.SettleAppends(TableId uf, Table ufTable, uint e, ExecutionState st)`** (new, `Tables/SortedWritesTable.Rebuild.cs`): returns false for a table without rebuild columns; otherwise gathers the live rows in `[FirstRowSinceRebuild(uf), Rows.Count)`, rewrites the stale ones with `RebuildRows` (the egglog rewrite: `remove` plus canonical `insert` stamped e), and calls `MarkRebuilt(uf)`. `HasRowsSinceRebuild(uf)` is `FirstRowSinceRebuild(uf) < data.Count` (`Tables/SortedWritesTable.RebuildWatermark.cs`).
- **`SettleTableOperation`** (`Database/SettleTableOperation.cs`, `ITableOperation`) mirrors `RebuildTableOperation`, so `RunOnTablesCore` notifies settled tables.
- **Why it suffices.** Rows before a table's rebuild mark were canonical at the mark and are kept canonical by the journal-driven rebuilds since (egglog's rebuild). Rows after it are exactly the rows a raw write could have left stale. Once settled, a child's view starts canonical (I35); later root appends are checked by the child's catch-up branch (a).
- **Cost.** O(rows written since the last rebuild) per `CreateChild` of a non-frozen root, and once at the root's freeze. Root programs that never create a child never settle.

---

## 8. Freeze, dispose and threading (C9)

### 8.1 `Context.Freeze(FreezeWarmup? warmup = null)`

1. Return if already frozen. Throw if called re-entrantly (§8.3). Enter `Domain`. Throw if disposed, faulted or contradicted.
2. If the parent is not frozen, throw `InvalidOperationException("A context can be frozen only once its parent is.")`.
3. `Rebuild()`, then, at the root, `Settle()` (§7.6). At the root, pending writes merge in the first pass (§7.1). The context is now clean: pending queues are empty and the rebuild is complete (C9 step 1).
4. `Database.Freeze(warmup)` (`Contexts/Database.Freeze.cs`):
   1. Throw if notifications are pending.
   2. For each `TableInfo`, call `info.Table.Freeze()` (X13). This is a new virtual on `Table`; the default seals the pending state.
      - `SortedWritesTable`: `ForceRehash()` (gated); `RefreshRebuildIndex()` only when `RebuildColumns` is nonempty (`Index<T>`'s constructor rejects an empty key, `Indexes/Index.cs:48`, so the nullary contradiction table and value-only tables have no rebuild index); `IsShared = true`; seal.
      - `DisplacedTable`: `CompressAll()`, seal.
      - `OverlayDisplacedTable`: `overlay.Freeze()`, seal.
   3. Warm every `(table, columns)` in `warmup` through `GetColumnIndex` / `GetIndex` on the tables this database owns.
   4. `info.FreezeIndexes()` → `IndexCatalog.SetSerialRefresh()` (X14).
   5. `Volatile.Write(ref frozen, true)`.
5. Views:
   - child: `Tables.Freeze()` seals every hidden set and builds every `TableView` for the tables that exist now, eagerly (frozen ones fold `Live` into `Frozen`); tables added later are answered functionally (X43);
   - root: `Store.Schema.FreezeRoot()` builds every `RootView`.
6. Union-find:
   - child: `flattened = FlattenedUnionFind.Build(UnionFind)` and `FinderForChildren = LayeredUnionFind.Flattened(root, flattened)`;
   - root: compressed in step 4.
7. `lock (children)`: give each live child subtree a new `ThreadAffinityGuard` (§8.3).
8. `Volatile.Write(ref frozenFlag, true)`, the last write.

**Supporting changes:**
- `PendingState.Seal()` (`Tables/PendingState.cs`): `EnqueueRows` and `EnqueueRemovals` then throw `InvalidOperationException("The context is frozen.")`. That is one volatile read per buffer flush.
- `Database.ThrowIfFrozen()` guards `MergeAll`, `MergeTable`, `NewBuffer`, `ClearTable`, `RunRuleSet`, `ApplyRebuild`, `RefreshRowsForValues`, `RebuildContainers`, `RebuildPass` and `Rebuild`.
- `Index<T>.Refresh(Table, bool allowParallel = true)`; `IndexCatalog.GetOrRefresh` passes `!serialRefresh`.

**`FreezeWarmup`** (`Database/FreezeWarmup.cs`) holds a list of `CachedIndexKey(TableId, ColumnId[])` (`Planning/CachedIndexKey.cs`). `FreezeWarmup.From(IEnumerable<PlanTemplate>)` uses `PlanIndexKeys.Collect` (§9.10). The bridge passes every cached plan (§10.8).

### 8.2 `Context.CreateChild(ContextOptions? options = null)` and `Dispose()`

**`CreateChild`:**
```text
ThrowIfDisposed; throw if faulted or contradicted; throw if called re-entrantly (§8.3)
if !frozen: using Domain.Enter(); Rebuild(); if IsRoot: Settle()     // the parent is clean and canonical when cursors are initialized
else: Debug.Assert(IsClean)
if Depth + 1 > 31: throw InvalidOperationException       // X36
child = new Context(store, this, options ?? Default)
  // parentFinder = FinderForChildren; overlay, journal, OverlayDisplacedTable; IdBlockAllocator;
  // Database(child ctor); ContextTables (cursors at current versions; settled = leading frozen ancestors);
  // unionFindCursor = Parent.DisplacedCount; CreatedAt = Store.ReadClock() (after the cursors);
  // SemiNaiveFloor = CreatedAt; Domain = frozen ? new ThreadAffinityGuard() : this.Domain; contradiction check (§6.9)
lock (children) children.Add(child); foreach ancestor a (this included): a.Gate.Enter()
Store.HasHadChildren = true
```

**`Dispose`:**
- A second call does nothing. The root throws. A re-entrant call throws (§8.3).
- `lock (children)`: throws if any child is live.
- Enters `Domain`, which throws if it is busy on another thread.
- Sets `disposed = true` (volatile), then removes itself from `Parent.children`.
- `Gate.Exit()` on every ancestor, which releases compaction and retraction there.
- Clears `ContainerDirtyLog` if the root gate reaches zero.
- Releases `Tables`, the overlay, the journal and `Host`.
- Its ids leak, which is harmless. Journals need no reader tracking (unlike the spike's `readers`/`Tidy`).

### 8.3 Thread domains and publication

**`ThreadAffinityGuard`** (`Concurrency/ThreadAffinityGuard.cs`):
- `Enter()` is re-entrant on the owning thread. Otherwise it CASes the owner from 0, or throws `InvalidOperationException("…used from two threads at once; only children of a frozen context may run concurrently")`.
- `Exit()` releases at depth 0.
- `int Depth` (owner-thread only).

**Re-entrant structural calls.** `CreateChild`, `Freeze` and `Dispose` throw `InvalidOperationException` when the calling thread already holds the context's domain (`Domain.Depth > 0` at entry): from inside a `Write` action, a rule's action or external, a `ScanMerges` visitor or a sink. Reads inside visitors stay allowed. This keeps every structure a running operation captured (states, guards, snapshots) valid until it ends.

**Domains:**
- A context that is not frozen shares its domain with every descendant created under it.
- Freezing gives each child subtree a new guard. Reassignment happens on the domain's thread, so no descendant is in use.
- Reads of a frozen context take no guard.

**Publication:**
- The volatile `frozen` write is last.
- `CreateChild` takes the parent's `lock (children)`.
- Children's threads start after creation.

Together these give happens-before for every ancestor structure a child reads.

**Shared mutable state:**
- `CounterSet` (`Interlocked`);
- `IdBlockAllocator` (lock on refill);
- `DescendantGate`;
- `LayeredSchema` arrays and root views (volatile swap or CAS);
- `DependencyGraph.levelSnapshot`;
- the `ExternalFunctionRegistry` snapshot;
- `ContainerValues` (thread-safe interning);
- frozen ancestors' once-cells (serial builds);
- `children` lists (locked).

**Per-context state** is touched by the owning domain only, with one exception: the tasks of the context's own rule run may create layers. During a search, rule tasks stage writes into their states' buffers, read the rule set's view snapshot, read ancestors' immutable structures, and may call `GetOrCreateLayer`, which publishes an empty layer to the catalog and the layer slots under `ContextTables.layerLock` (I34). The table map, the view cache, `ViewGeneration` and the hidden sets do not change until the search ends.

### 8.4 Exceptions

- **Root:** `Database`'s guarantee (`Database/Database.Merge.cs` catch): every table is back, the unmerged round stays notified, caches are reset.
- **Child:** any exception escaping `Write`, a rule run, `Rebuild` or `Freeze` sets `IsFaulted` (X12). Every later call except `Dispose` throws.
- Panics recorded through `ExecutionState.ReportFailure` are not exceptions and do not fault.

---

## 9. Layered query execution (C3, C6, C7)

### 9.1 `ContextView`, `ViewSnapshot` and `ILayerViewSource` (X21, X22, X39)

```csharp
internal interface ILayerViewSource         // Layers/ILayerViewSource.cs; implemented by ContextTables and the test SyntheticLayers
{
    int Depth { get; }  long ViewGeneration { get; }  TableView GetView(TableId table);  uint Find(uint id);
}
internal sealed class ContextView : ILayeredHeaderEvaluator, IPlanStatistics      // Execution/ContextView.cs
{
    public static ContextView ForRoot(Database root);          // one LayerView per table, HiddenRows.None; cached per table
    public ContextView(Database database, ILayerViewSource source);
    public Database Database { get; }  public int Depth { get; }  public int LayerCount => Depth + 1;
    public bool IsLayered => Depth > 0;
    public ParallelHeuristics Parallelism => Database.Parallelism;
    public long SizeEstimate => Database.TotalSizeEstimate;     // child: own rows only (C9, X26)
    public long Generation { get; }
    public TableView GetTableView(TableId table);               // child: $uf throws NotSupportedException (X23)
    public ViewSnapshot Snapshot(ReadOnlySpan<TableId> tables); // the cached instances, pinned for one run
    public bool TryCanonicalize(uint id, out uint canonical);   // false only for a bare Database (X19)
    public LayeredSubset Evaluate(TableId t, ReadOnlySpan<Constraint> cs, List<Constraint> fast, List<Constraint> slow);   // §9.4
    public int EstimateSize(TableId table, Constraint? constraint);
    long IPlanStatistics.GetApproximateSize(AtomId atom, QueryAtom info);
    ColumnUniqueness IPlanStatistics.GetColumnUniqueness(TableId t, ColumnId c);
}
internal sealed class ViewSnapshot          // Execution/ViewSnapshot.cs: TableView?[] by table index; immutable
{
    public TableView? Get(TableId table);
}
```

`TryCanonicalize` is `Store.RootUnionFind.FindNaive` at a Store-backed root, `Owner.UnionFind.Find` in a child, and false for a bare `Database`, which has no designated union-find.

**Wiring:**
- `Database.View` is created lazily at the root and assigned at child construction.
- `RuleSetBuilder(Database db)` evaluates headers through `db.View` (`InstantiateLayered`, §9.4).
- `RuleSetBuilder.Build()` collects the tables of every rule's atoms and of its actions' lookup instructions (`Lookup`, `LookupWithDefault`, `LookupWithFallback`, `LookupOrInsertDefault`), and stores `View.Snapshot(tables)` and `View.Generation` in the `RuleSet` (`RuleSet.Views`, `RuleSet.ViewGeneration`). Header evaluation and the snapshot read the same cached instances, because nothing invalidates views between them.
- `Database.RunRuleSet` throws `ArgumentException` unless the rule set was built on this database's view, asserts the generation is unchanged, sets `layers.InSearch = true` around `RunRulesSerial`/`RunRulesParallel`, and gives the root state `views = ruleSet.Views`.
- `JoinRun(Database db, ViewSnapshot views, ExecutionSignals signals, bool parallel, FrameUpdatesPool?)` and `RuleWork` read tables from the snapshot (today's `db.GetTableInfo(info.Table)` at `Execution/JoinRun.cs:178` becomes `views.Get(info.Table)`). `idleSerialJoin` stays per `Database`.
- At the root the snapshot holds the root's cached single-layer views; building it allocates one array per rule set, outside the run that `ExecutorAllocationProbeTests` measures.

### 9.2 Layered subsets (`Offsets/`)

**`LayeredSubset`** (`Offsets/LayeredSubset.cs`) is an owned, move-only struct, with `MaxLayers = 32`.

**Fields:**
- `Subset single`, `int singleLayer`: the rows when they lie in one layer;
- `Subset[]? layers` with `uint mask`: per-layer subsets when two or more layers are non-empty.

**Members:**
- `Empty`, `Single(int, Subset)`, `FromLayers(Subset[], SubsetArrayPool?)` (collapses to `Single` when possible);
- `Size`, `IsEmpty`, `IsMultiLayer`, `LayerMask`, `SingleLayer`, `SingleRows`;
- `GetLayer(int) → SubsetRef`;
- `Intersect(in LayeredSubset, SubsetArrayPool?)`: two singles in the same layer use today's `Subset.Intersect`, which is the root path; otherwise it works per layer and collapses;
- `Clone`, `Take`, `Dispose(pool)`.

**`SubsetArrayPool`** (`Offsets/SubsetArrayPool.cs`) is a non-thread-safe stack of `Subset[LayerCount]`, owned by `TrieNodePool`.

### 9.3 Visibility in scans and index builds (decisions' engine-wide rule)

**`Tables/Table.cs`:**
- `abstract Subset RefineVisible(Subset s, HiddenRows hidden)`.
- Hidden-aware overloads of `ScanGenericBounded`, `ScanGeneric`, `ScanProject` (sink and `TaggedRowBuffer` forms) and `ForEachColumn`.
- The existing overloads forward `HiddenRows.None`. Dispatch is on `Kind`, with new `OverlayDisplaced` arms.

**`Tables/SortedWritesTable.Scan.cs`:**
- When `hidden.IsNone`, it runs today's code byte for byte.
- Otherwise it uses separate loops with `EmitIfMatchesHidden`: tombstone test, then `hidden.Contains` with a cursor, then constraints.
- `RefineVisible` uses `VisibleRowFilter` through `Subset.Retain`.

**`Tables/DisplacedTable.cs` / `OverlayDisplacedTable`:** hidden overloads through `HiddenSkippingSink` and `HiddenOnlyFilter`.

**Index builds:**
- `Indexes/ColumnIndex.cs` gains `BuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)`, `RebuildForSubset(...)` and internal `MergeSubsetVisible(...)`.
- `ColumnIndex.Build.cs` gains `RebuildFull(..., HiddenRows)`.
- `TupleIndex.BuildForSubset(..., HiddenRows)`.
- `TableIndexing.GroupByColumn/GroupByKey(..., HiddenRows)`.
- `Execution/SparseColumnIndex.Build(Table, SubsetRef, ColumnId, HiddenRows)`.
- Catalog content (`Index.Refresh`, `MergeSubset`) stays hidden-agnostic (C6: hides are not index content).

**`Tables/RowOps.cs`:** `static RowOps.IsVisible(uint[] data, int rowBase, int row, HiddenSet? frozen, HiddenSet? live)`, `AggressiveInlining`, for M12.

### 9.4 Per-layer header evaluation

**`Database/ConstraintProcessor.cs`** (static) holds today's body of `Database.ProcessConstraints` (`Database/Database.Constraints.cs:52`), working on a `TableInfo`:
- `Process(TableInfo, cs, fast, slow) → Subset`;
- `Split(TableInfo, cs, fast, slow)`, which classifies without building an index.

`Database.ProcessConstraints(TableId, …)` delegates to it. At the root, `IQuerySchema.SplitConstraints` keeps today's behaviour (it builds and drops the subset, egglog's `add_atom`); in a child it uses `Split` on the root `TableInfo` (§3.4).

**`ContextView.Evaluate`:**
```text
tv = GetTableView(t)
if !IsLayered: return Single(0, ConstraintProcessor.Process(tv.Layers[0].Info, cs, fast, slow))
if tv.PresentMask == 0: ConstraintProcessor.Split(...); return Empty
for each present l: s_l = Process(lv_l.Info, cs, lf, ls)    // ts ranges: one binary search per layer
a constraint is fast overall iff fast in some present layer; layers where it was slow refine with lv_l.Table.RefineOne
return FromLayers(s_0..s_D)
```

**Supporting types and changes:**
- `Planning/ILayeredHeaderEvaluator.cs`: `LayeredSubset Evaluate(...)`.
- `Planning/SingleLayerEvaluator.cs`: a struct wrapping an `IHeaderEvaluator` as an `ILayeredHeaderEvaluator`.
- `PlanTemplate.InstantiateLayered<TEvaluator>(ref TEvaluator, extras, action) where TEvaluator : ILayeredHeaderEvaluator` (new). A header is empty iff it is empty in every layer; the variant is then dropped before an action id is taken.
- `PlanTemplate.Instantiate<TEvaluator>(ref TEvaluator, extras, action) where TEvaluator : IHeaderEvaluator` (`Planning/PlanTemplate.cs:96`) is kept and forwards through `SingleLayerEvaluator`, so `Planning/PlanInstantiationTests`, `Execution/DecomposedExecutionTests` (with `Planning/PlanTestDatabase : IHeaderEvaluator`) and `Query/DatabaseQueryTests` compile unchanged. C# cannot overload on constraints alone, hence the second name.
- `PlanInstance.HeaderSubsets` becomes `LayeredSubset[]`.

### 9.5 Trie nodes, probers and kernels

**`Execution/TrieNode.cs`:**
- `Subset subset` becomes `LayeredSubset rows`.
- New members: `Size`, `IsEmpty`, `LayerMask`, `IsMultiLayer`, `SingleLayer`, `GetLayerRows(int)`, `Initialize(LayeredSubset, bool)`, `Intersect(in LayeredSubset, SubsetArrayPool?)`, `GetCachedIndex(ColumnId, int layer, LayerView)`, `ResetDense(int layer, OffsetRange, TrieNodePool)`.
- Children: `TryGetChild(int slot, ...)`, `AddChild(int slot, ...)`, `static ChildSlot(int column, int selector, int arity)`.
- Index slot: `IsMultiLayer ? layer * arity + column : column`. Child-map slot: `selector * arity + column`, where selector 0 means all layers and `l+1` means layer l only. A single-layer node, which includes every root node, uses today's slots after one `IsMultiLayer` test.

**`TrieNodePool`:** `SubsetArrayPool Layers`, `Rent(LayeredSubset)`, `Rent(int layer, Subset)`; `Rent(Subset)` means layer 0.

**Probers:**
- **`Execution/ProberPiece.cs`** (struct): `Kind`, `byte Layer`, `HasOuter`, `MayHoldInvisible`, `Outer`, `Column`, `Tuple`, `Sparse`, `LayerView View`, `Count`, and `TryGetSubset(uint)` / `TryGetSubset(uint[])`.
- **`Execution/Prober.cs`** (changed): `TrieNode Node`, `ProberPiece First`, `ProberPiece[]? Rest`, `RestCount`, `PieceCount`, `Count` (the sum), `GetPiece(ref p, i)`.
- **`ProberCursor.MoveNext(in ProberPiece)`.**
- **`Execution/LayerHits.cs`:** per-position scratch (`SubsetRef[] Rows`, `int[] Pieces`, `Count`, `Total`).
- **`FrameUpdate`** gains `byte Layer`; the struct stays 24 bytes (`FrameUpdateKind` is a byte enum).
- **`FrameUpdates`:** `RefineAtomDense(AtomId, int layer, OffsetRange)`, `RefineAtomSubset(AtomId, int layer, Subset, TrieNodePool)`, `RefineAtomLayered(AtomId, LayeredSubset, TrieNodePool)`.

**Index choice per piece (C6)** — `JoinRun.Index.cs`, `BuildPiece`:
- a small single-column piece gets `Sparse` (built with `lv.Hidden`);
- a cacheable, dense piece covering more than half of its layer uses `lv.Info` cached indexes, shared and lock-free when `lv.IsFrozen`, with the clamp. It sets `MayHoldInvisible = lv.HasInvisible`;
- a multi-column piece gets `GroupByKey(rows, cols, lv.Hidden)` (`DynamicTuple`);
- otherwise `node.GetCachedIndex(col, l, lv)`.

`MoveBack` releases the pieces. At the root there is one piece, built with today's heuristics, giving the same objects.

**Kernels** (`JoinRun.Intersect.cs`, `JoinRun.Fused.cs`, `JoinRun.Materialized.cs`):
- **Drivers split by layer:** one frame per (piece, key).
- **Probed sides joined across layers:** a probe with `PieceCount == 1` takes today's path. Otherwise `ProbeAll` gathers the hits and `RefineHits` builds a `LayeredSubset`, cached under selector 0.
- `RefineVisible(rows, cs, lv, mayHoldInvisible)` refines visibility only when the flag is set: rows from a visibility-aware build are already visible.
- FusedScan, FusedIntersect and Mat stages loop over the cover node's layers, using the hidden-aware scan overloads.
- `Replay` passes `e.Layer`; `CloneForMorsel` copies `views`.

**Existence checks** (`JoinRun.BeginRule`) scan each layer in `node.LayerMask` with the hidden-aware `ScanGenericBounded`. This fixes map §8.1 row 2.

### 9.6 Child caches

A (node, slot, key) always names one well-defined row set (I28):

| Refinement | Slot | Content |
|---|---|---|
| Driver refinement of a single-layer node, and every root refinement | `column` | Today's content |
| Driver refinement of a multi-layer node, piece in layer l | `(l+1) * arity + column` | Rows of layer l only |
| Probed refinement | `column` | Rows of all layers |

### 9.7 Planner statistics per context

- `GetApproximateSize(atom, info)` is `Evaluate(info.Table, info.FastConstraints, …).Size`. It counts slots, invisible ones included, as egglog's `approx_size` does.
- `GetColumnUniqueness(t, c)` sums the per-layer counts through a new static `ColumnCardEstimator.GetColumnUniqueness(TableInfo, ColumnId)`.
- A plan is made once per rule, on the statistics of the context it first runs in, or of the frozen context that pre-plans it (§10.8). Building and planning a rule in a child goes through the layered members of §3.4, so a child that never wrote the rule's tables plans with read-through statistics.
- Replanning stays open (§8.3 #13).

### 9.8 Id constants per context (X19)

**What is recorded.**
- Query constants: `IdConstant` (`Query/IdConstant.cs`), recorded by `RuleQueryBuilder.RecordIdConstants` (`Query/RuleQueryBuilder.cs:188`) for `EqConst` constraints and constant entries on Id columns, as today.
- Action constants (new): `ActionIdConstant(int Instr, int Operand, Value Value)` (`Query/ActionIdConstant.cs`, a readonly record struct). `RuleBuilder` (`Query/RuleBuilder.cs`) records one for every constant operand that:
  - lands in an Id column of the instruction's table: `Insert` and `InsertIfEq` values, `Remove` keys, `Lookup`/`LookupWithDefault`/`LookupWithFallback` keys, `LookupOrInsert` keys and `WriteVal` entry defaults (`GetIdColumns(table)`), or either id column of the store's union-find table; or
  - is flagged as an Id by the caller: new `QueryEntry.IdConst(Value)` (`Planning/QueryEntry.cs`) sets a new `IsIdConstant` flag. This covers `AssertEq`, `AssertNe`, `AssertAnyNe`, `InsertIfEq`'s comparands and external-function arguments, which have no table column. The bridge lowers every Id-typed constant through `IdConst`.

  `Operand` indexes the instruction's operands in the order `ActionSubstituter` defines per `InstrKind`. `ActionInfo` (`Query/ActionInfo.cs`) gains `ActionIdConstant[] IdConstants`; `CachedPlan` carries it through `Actions`.

**Types:**

| Type (file) | Role |
|---|---|
| `CanonicalConstants` (`Query/CanonicalConstants.cs`) | `static uint[]? Compute(ContextView view, IdConstant[] query, ActionIdConstant[] action)`: `null` iff there are none; otherwise, for each constant in that order, `view.TryCanonicalize(c) ? canonical : c.Value.Rep`. This is **K**. |
| `ConstantSubstitution` (`Query/ConstantSubstitution.cs`) | The query entries `(atom, column, written, canonical)` and action entries `(instr, operand, written, canonical)` that differ. `Create` returns `null` when nothing differs. |
| `TemplateSubstituter` (`Query/TemplateSubstituter.cs`) | `Substitute(PlanTemplate, ConstantSubstitution)` rewrites only the matching `EqConst`s in atoms, headers, existence checks, stages and blocks. It shares unchanged sub-objects; new stage arrays get their own `PreparedBlock`. |
| `ActionSubstituter` (`Query/ActionSubstituter.cs`) | `Substitute(ActionInfo, ConstantSubstitution)` builds a new `ActionInfo` whose changed instructions are new instances with the canonical operands; unchanged instructions are shared. |
| `ConstantVariant` (`Query/ConstantVariant.cs`) | `(uint[] Key, PlanTemplate Template, ActionInfo Actions)`. |
| `CachedPlan.GetVariantFor(uint[]? K)` (`Query/CachedPlan.cs`) | Returns `(Plan, Actions)` when K is null or equals the written values. Otherwise it consults a copy-on-write `ConstantVariant[]` (at most 16 entries; insert under a lock, then double-check). |

**Call sites:**
- `RuleSetBuilder.AddRuleFromCachedPlan(cached, extras, uint[]? canonical = null)` computes K if it is not given, takes `GetVariantFor(K)`, instantiates the variant's template and pushes the variant's actions.
- `RuleSetBuilder.AddBuiltRule` (rules built directly in a database) computes K the same way and substitutes before instantiating.
- `RulePlan` (`Query/RulePlan.cs`) gains `PlanTemplate WrittenTemplate`, `ActionInfo WrittenActions` and `uint[]? CanonicalConstants`. `RuleSet.BuildCachedPlan` uses the written ones, so a cached plan never bakes one context's canonical forms.
- The `Query/IdConstant.cs` remarks change from "a root run uses Value as written" to "a Store-backed context canonicalizes at every level; a bare Database uses Value as written".

At a Store-backed root, K differs from the written values only for a constant that has lost a union. egglog then matches the stale constant against canonical rows and finds nothing; this design matches the class instead.

### 9.9 What M12 needs

- One compiled delegate per (plan, stage, piece-kind pattern) for the per-piece inner loop, with a `hiddenIsNone` specialization bit. The interpreter keeps the outer layer loop, `Drain`, re-sorting and recursion (decision 8).
- Array-friendly, non-virtual accessors: `LayeredSubset.GetLayer(int)`, the `LayerView` fields, the `HiddenSet` arrays (§6.3), and `RowOps.IsVisible`.
- `ProberPiece.TryGetSubset` overloads.
- Frames carry `Layer`.

### 9.10 Prebuilding at freeze

`Planning/PlanIndexKeys.cs` provides `static void Collect(PlanTemplate t, IQuerySchema schema, List<CachedIndexKey> into)`. It lists, for cacheable columns only:
- each `SingleScanSpec` column;
- each `Probe.Scan.ToIndex`: a column index for one column, a tuple index otherwise;
- each `EqConst` header column.

`FreezeWarmup.From` feeds this list to `Database.Freeze` (§8.1).

---

## 10. Per-context rules in the bridge (M8 port + this spec)

This section names M8 types as egglog-bridge's: `EGraph`, `RuleInfo`, `FunctionInfo`, `BridgeQuery.AddRulesFromCached`, `PanicFunction`, `TableAction`, `UnionAction`, `IterationReport` and `PanicException` (`Bridge/`). If the port names them differently, map them one to one.

### 10.1 Split of state

**`EGraph`** keeps the definitions:
- `funcs`, a copy-on-write `functionTables` snapshot, and `RuleTable rules`;
- `panicFunctions` (a `ConcurrentDictionary`, with registration under `registrationGate`);
- `StopOnContradiction` (an `ExternalFunctionId`) and `Contradiction` (a `FunctionId?` → `Store.DesignateContradiction`);
- `ContextLocalSlot<object>[]` and the `ParallelismBudget`;
- `Root` (an `EGraphContext`).

Its constructor builds a `Store`; `$uf` and the counters come from it. The root-only egglog API (`RunRules`, `FlushUpdates`, `AddValues`, `AddTerm`, `LookupId`, `TableSize`, `ForEach`, `WithExecutionState`, `FreshId`, `ClearTable`, `Rebuild`) delegates to `Root`, so every M8 test keeps its meaning.

**`EGraphContext`** (`Bridge/EGraphContext.cs`, partials `.Run.cs`, `.Watermarks.cs`, `.Writes.cs`, `.Reads.cs`, `.Freeze.cs`, `.Lift.cs`, `.Locals.cs`, `.Invariants.cs`) holds:
- `owner`, `parent` and `context` (the storage `Context`);
- `RuleProgress[] progress`, `HostWrites hostWrites` (`Bridge/HostWrites.cs`) and `object?[] locals`;
- `List<SplitRecord>? splitRecords`, and `memberMap` / `memberMapVersion`.

Its properties forward to storage: `Version`, `CreatedAt`, `IsFrozen`, `IsStopped` (= `context.IsContradicted`) and `IsDisposed`.

**Other bridge types:**
- `ContextState` is dropped (storage flags suffice).
- `ContextLocalSlot<T>` (`Bridge/ContextLocalSlot.cs`): `GetLocal<T>(slot)` installs with `CompareExchange`. Scheduler collect-matches buffers live there (map §8.1 row 10).

### 10.2 Watermarks (C7, D3, D4, D18, X41)

```csharp
internal readonly struct RuleProgress { public RuleProgress(uint lastRunAt, uint[]? constants, int generation); uint LastRunAt; uint[]? Constants; int Generation; }   // Bridge/RuleProgress.cs
internal sealed class RuleTable { RuleId Add(Func<RuleId,int,RuleInfo> create); void Remove(RuleId id); bool TryGet(RuleId id, out RuleInfo info); }   // copy-on-write, Bridge/RuleTable.cs
```

**`RuleInfo` changes:**
- drop `LastRunAt`;
- add `int Generation`, `planGate`, a volatile `cachedPlan`, and `GetOrBuildCachedPlan(Database db)`, double-checked under the gate;
- add `bool AllowInheritance`, computed once with the cached plan: false for naive, split and callback rules (D4, D21), and false unless `ActionReadAnalysis.IsQueryDetermined(cached.Actions, store.Schema, externals)` holds.

**`ActionReadAnalysis`** (`Query/ActionReadAnalysis.cs`, static). An action program is *query-determined* when what it writes depends only on the match and the context's equalities, not on table contents that can change in a descendant. It is query-determined iff every instruction is one of:
- `Insert`, `InsertIfEq`, `Remove`, `AssertEq`, `AssertNe`, `AssertAnyNe`, `ReadCounter`;
- `LookupOrInsertDefault` on a table whose merge function is `UnionIdMerge` (a constructor: the row exists after the ancestor's run, and its value changes only by unions, which descendants see through `find`);
- any lookup on the union-find table (`lookup_uf` is canonicalization);
- `External` or `ExternalWithFallback` whose function has `ReadsTables == false`.

`Lookup`, `LookupWithDefault` and `LookupWithFallback` on any other table, `LookupOrInsertDefault` on a table with any other merge, and an external with `ReadsTables` make it not query-determined: a descendant can change the row read (a lattice value raised, a row added), and an inherited watermark would never re-fire the old match. `ExternalFunction` (`Database/ExternalFunction.cs`) gains `virtual bool ReadsTables => false`; `DelegateExternalFunction` takes it as a constructor argument, and the bridge sets it for primitives that read tables.

**Computing the watermark:**

```text
GetWatermark(r, info, K):
  if TryOwn(r, info.Generation, out p): return SameConstants(p.Constants, K) ? p.LastRunAt : 0
  if Parent is null || !info.AllowInheritance || !context.Options.InheritWatermarks: return 0
  bound = uint.MaxValue
  for c = this; c.Parent is not null; c = c.Parent:
      bound = min(bound, c.CreatedAt)              // = CreatedAt of the ancestor's child on the path
      if c.Parent.TryOwn(r, info.Generation, out p): return SameConstants(p.Constants, K) ? min(p.LastRunAt, bound) : 0
  return 0
```

- `TryOwn` returns false when the slot is beyond `progress.Length` or the generation differs.
- Reading an ancestor's `progress` is safe: the ancestor is frozen (immutable) or shares this domain.
- **Commit (X18):** `(T, K, generation)` is staged per rule at instantiation and committed when `RunRuleSet` returns and `Database.TakeFailure()` is `null`.
- An inherited watermark is at most `this.CreatedAt` (the first `bound`), which `SemiNaiveFloor` relies on (§7.2).

### 10.3 `RunRules(rules) → IterationReport`

```text
using context.Domain.Enter(); ThrowIfNotWritable()
if IsStopped: return IterationReport.Stopped
EnsureClean()
if IsStopped: return Stopped
T = context.BeginEpoch(); context.NoteRuleRun(T); ufBefore = context.DisplacedCount
rsb = database.NewRuleSet(); staged = []
for r: info = rules.TryGet(r); cached = info.GetOrBuildCachedPlan(database)
       K = CanonicalConstants.Compute(database.View, cached.IdConstants, cached.Actions.IdConstants)
       W = GetWatermark(r, info, K); info.Query.AddRulesFromCached(rsb, W, cached, K); staged.Add((r, T, K))
using rs = rsb.Build()                                  // snapshots the views (§9.1)
rep = database.RunRuleSet(rs, level, T)                 // merges at T; a child fault is X12
if database.TakeFailure() is { } m: throw new PanicException(m)   // egglog: after the merge, no rebuild, no clock advance
commit staged progress
if IsStopped: context.EndEpoch(); return Stopped report
if context.DisplacedCount == ufBefore: context.EndEpoch(); return (rep, RebuildChanged: false)
rb = context.Rebuild()                                   // the first pass reads the clock again (X4)
return (rep, RebuildChanged: rb, Stopped: IsStopped)
```

**`EnsureClean()`** (X42):
```text
if parent is not null && !parent.IsFrozen: parent.EnsureClean()
if hostWrites nonempty: FlushUpdates()
if IsRoot: if context.DisplacedCount != context.journalMark: context.Rebuild()   // pending staged writes merge inside the run, as in egglog
else: context.Rebuild()
```

- At the root, writes staged through `WithExecutionState` or `Database.NewBuffer` and not flushed stay pending into the run and merge in `RunRuleSet`'s `MergeAll` at T, exactly as in egglog's `run_rule_set`. At rest the root's journal mark is always current (every root operation that grows the union-find rebuilds), so `EnsureClean` adds no pass and no epoch at the root.
- `IterationReport` gains `bool RebuildChanged`, `bool Stopped` and `int RebuildPasses`. `Changed` stays the rule application's own change (decision 11).
- `Saturate(rules, max)` loops until `Changed`, `RebuildChanged` and `Stopped` are all false, or the limit is reached. It returns `SaturateReport { Iterations, Stopped, HitLimit }` (`Bridge/SaturateReport.cs`).

### 10.4 Host writes (X24, X38)

- **`HostWrites`** keeps logical rows per table (`uint[]` arenas without the ts and subsume cells), union pairs, and per row whether it came through the context API or the egglog-compatible root API.
- **Staging:**
  - `StageRow(f, row)` appends the row;
  - `StageTerm(f, key)` mints `id = (uint)database.IncrementCounter(IdCounter)`, which comes from a block in a child, then stages the row and returns id;
  - `StageUnion(a, b)` stages the pair;
  - `FreshId()` mints directly.
- **`FlushUpdates()`:**
  ```text
  using Domain.Enter(); ThrowIfNotWritable(); if IsStopped: return false
  if Parent is not null && !Parent.IsFrozen: Parent.EnsureClean()
  context.PrepareForWrite()                 // catch-up BEFORE anything is staged
  T = context.BeginEpoch(); ufBefore = DisplacedCount
  st = database.CreateExecutionState(T); hostWrites.ReplayInto(st, T, context.UnionFind)   // ts cell = T, subsume cell = NOT_SUBSUMED
  st.Flush(); database.RecordFailure(st); changed = database.MergeAll(T); context.EndEpoch()   // egglog flush_updates
  if DisplacedCount != ufBefore: context.Rebuild()
  ThrowIfFailed(); return changed
  ```
- `ReplayInto` canonicalizes the Id columns of rows staged through the `EGraphContext` API at every level (`UnionFind.Find` is current after `PrepareForWrite`); this gives the rule layer the spike's canonicalizing `Insert` (rule-layer invariant C). Rows staged through the egglog-compatible `EGraph` root API are replayed as given, so the root matches egglog; in a child they are canonicalized at the merge anyway (X38).
- `AddValues(rows)` stages each row, then `FlushUpdates()`. `AddTerm(f, inputs)` is `id = StageTerm(...)`, `FlushUpdates()`, then returns `Find(id)`.

### 10.5 Reads

`Find`, `TryLookup(f, key, out ret)` (Id key columns canonicalized with `Find`), `ContainsKey`, `LookupId`, `TableSize`, `ForEach` and `IsContradictory` all go through `ThrowIfNotReadable`:
- in a child they use `context.GetView(t)` / `TryGetRow`;
- at the root, the root path. Root reads do not throw for writes staged and not yet flushed; they see the committed rows, as egglog does (X42).

Bridge `IsClean` is `context.IsClean && hostWrites.IsEmpty && (Parent?.IsClean ?? true)`, where the recursion stops at frozen ancestors.

### 10.6 Contradiction early stop and panics

- **`StopOnContradictionFunction`** (`Bridge/StopOnContradictionFunction.cs`): if `state.View?.Owner.Owner is { Options.StopOnContradiction: true }`, it calls `state.TriggerEarlyStop()`.
- The bridge rule builder appends `External(StopOnContradiction, [])` after every head insert into the contradiction function. The storage flag is set when that row merges (§6.9).
- `PanicFunction` calls `state.ReportFailure(message)`, never a field closed over the `EGraph`, so failures stay per context.
- `EGraph.NewPanic` is thread-safe.

### 10.7 Containers (decision 9, C13)

- **Registration:** `ContainerValues.RegisterType<T>(CounterId, ContainerMergeFunction, bool holdsIds = true)`. `ContainerEnvBase.HoldsIds`. Containers over base values register with `holdsIds: false`; the rule layer's built-in `ValueSet<T>` (rule-layer D20) is one, registered by that layer.
- **New members:** `ContainerValues.HasEqContainers`, `ContainerValues.AnyEqContainerMentions(uint)`, `ContainerEnvBase.AnyContainerMentions(uint)` and `ContainerValueIndex.ContainsElement(uint)`.
- **G-a:** `ContainerEnv.GetOrInsert` throws `InvalidOperationException` when `HoldsIds && state.InChildContext` (§6.5). `TryGetId` stays allowed.
- **G-b, G-c:** §7.4.

### 10.8 Freeze (bridge part)

`EGraphContext.Freeze()`:
1. `FlushUpdates()`.
2. `Rebuild()`.
3. Throw if `database.Failure` is not null.
4. Plan every live rule without a cached plan, on this context's statistics (§9.7).
5. `context.Freeze(FreezeWarmup.From(cached plans))`.

### 10.9 Concurrent children and the parallelism budget (C9, M11 parts the colors need)

- **Default parallelism:** a child of a frozen context uses `ParallelHeuristics.Serial`. A child of a non-frozen context inherits its parent's heuristics, since they never run concurrently. `ContextOptions.Parallelism` overrides both. Under inherited heuristics a child's rule set can go parallel once its own rows pass `DbLevelThreshold`; layer creation from those tasks is safe (X37).
- **`ParallelismBudget`** (`Concurrency/ParallelismBudget.cs`): `Total`, `Available`, `Lease(int)` (blocks), `TryLease`, `Release`.
- **`ParallelismLease`** (`Concurrency/ParallelismLease.cs`): `IDisposable` struct.
- **`ConcurrentChildren.Run(children, body)`** (`Bridge/ConcurrentChildren.cs`):
  - requires every child's parent to be frozen and no child to be an ancestor of another;
  - runs `Parallel.For` with `degree = max(1, Total / max child DOP)`, each body under a lease;
  - rethrows a single inner exception unwrapped.

---

## 11. Lift primitives and split hooks (L1, L2, L4, L5)

All files are in `Cases/`, one type each.

### 11.1 Member maps (L2, G1(a), X29)

```csharp
internal sealed class MemberMap
{
    public static MemberMap Build(EGraphContext k);      // from UnionFindOverlay slots: members not local, ascending, per canonical
    public int GroupCount { get; }  public ReadOnlySpan<uint> GroupRoots { get; }
    public ReadOnlySpan<uint> Members(uint caseCanonical);   // group; else [c] if c is not local; else empty
}
```

- `GetMemberMap()` is cached while `UnionFindOverlay.Version` is unchanged.
- `ClassPartition.Meet(MemberMap seed, IReadOnlyList<EGraphContext> live, int seedIndex, List<uint[]> blocks)`:
  - for each seed group, builds each member's signature `(K_j.Find(m))` over the cases j ≠ seed;
  - sorts the members by signature;
  - emits every run of length ≥ 2 as a block.

### 11.2 The lift

**Types:**

| Type | Shape |
|---|---|
| `LiftTableKind` | `Relation`, `Constructor`, `Function` |
| `LiftTable` | `(FunctionId, LiftTableKind, Func<uint,uint,uint>? caseJoin)` |
| `LiftSpec` | A list of `LiftTable` |
| `LiftOptions` | `RoundLimit = 16`, `CandidateLimit = 64` |
| `LiftReport` | `Rounds`, `LiveCases`, `VacuousCases`, `ParentContradicted`, `Unions`, `Rows`, `Terms`, `Values`, `Skipped`, `HitRoundLimit`, `Changed` |
| `LiftSignature` | Versions of P, of P's non-frozen ancestors, and of each case |
| `LiftCandidates` | A deduplicating key arena |
| `LiftKeySink` | `IRowSink`, with an optional winner filter |
| `LiftWrites` | Pending unions, rows and terms |
| `Lifter` | `Lifter.cs`, `Lifter.Equalities.cs`, `Lifter.Facts.cs` |

**`Lifter.Lift(P, cases, spec, options, ref memo)`:**

```text
throw if cases empty; P frozen, stopped or disposed; or a case's parent is not P
loop:
  P.Rebuild(); each case not stopped: Rebuild()
  sig = LiftSignature.Of(P, cases); if first round && sig == memo: return (Changed = false)
  live = cases not IsContradictory; VacuousCases = |cases| - |live|                    // L4
  if live empty: if P has a contradiction function and !P.IsContradictory: P.StageRow(c, []); P.FlushUpdates()
                 ParentContradicted = true; break
  maps = live.GetMemberMap(); r = argmin GroupCount
  ClassPartition.Meet(maps[r], live, r, blocks); for each block: union block[0] with each other member   // equalities
  for h in spec (skipping the contradiction function):
     sources: live[r].ScanOwn(h) keys                                                         // (a)
            ∪ for each group root c of maps[r], not local: live[r].ScanAncestorsMentioning(h, c) keys mentioning c   // (b) L1
     candidates: expand the Id key cells through maps[r].Members (skip a source if any list is empty or the product > CandidateLimit)
     per kind: Relation/Constructor → present in every live case and absent in P → Row/Term
               Function → every live case has a value: an Id output must have a common parent member
                          (first y with K.Find(y) == v_K for every K); a value output folds with caseJoin, or requires all values equal
  if writes empty: break
  apply: StageUnion, then StageRow (NOT_SUBSUMED), then StageTerm; v0 = P.Version; P.FlushUpdates(); rounds++
  if P.Version == v0 or rounds == RoundLimit (HitRoundLimit): break
P.Rebuild(); cases.Rebuild(); memo = LiftSignature.Of(P, cases)
```

**`CaseSet`** (`Cases/CaseSet.cs`):
- `AddCase()` calls `parent.CreateChild(ContextOptions.Case)`.
- `RunEach(ICaseSchedule)` rebuilds each case. A contradictory case is disposed at once and counted as vacuous (L4); other cases run their schedule.
- `Lift(...)` calls `Lifter.Lift`.
- `Dispose()` disposes in reverse creation order, each case's split records first.

### 11.3 Split hooks (L5, B6)

**Types:** `SplitKey`, `SplitRequest`, `ISplitSource`, `ISplitDemand`, `ICaseSchedule`, `SplitOptions` (`DepthLimit = 4`, `CaseLimit = 16`, `Lift`), `SplitRecord` and `SplitReport`.

**`SplitDriver.Step(scope, depth, report)`:**
1. Find the split requests.
2. Skip a request whose key is covered by a live record in the scope **or any ancestor** (B6).
3. Skip a request over the limits.
4. Skip a request that `ISplitDemand` rejects. Demand is checked before any case exists.
5. Open the cases.
6. **Phase A:** for every record, `RunEach` (nested splits disabled), then lift. This is lift before deepening (TheSy).
7. **Phase B:** deepen into live cases while `depth + 1 < DepthLimit`, and lift again when deeper steps changed something.

`DisposeRecords(scope)` disposes depth-first, in reverse.

---

## 12. Invariants and proof sketches

The checker named with each invariant is the part of `Context.CheckInvariants()` / `EGraphContext.CheckInvariants()` that verifies it (§12.7).

### 12.1 Identity and time

**I1. Ids are unique across the store.** The root's direct mints and every child block are drawn atomically from one `CounterSet` slot (`Interlocked.Increment`, `Interlocked.Add`), so ranges are disjoint. Container ids use the same counter. *Checker:* `IdBlockAllocatorTests`.

**I2. `IsLocalId` is exact.** Every term mint in C goes through `IncrementCounterInView` (§4.2, the only path from `ExecutionState`, `PredictRow`, `NewId` and `StageTerm`) and lands in C's blocks. Container ids bypass it, and blocks of other contexts are disjoint (I1). *Checker:* differential check 3 (§17, M9.2).

**I3. Epoch discipline.**
- Every row, union and merge output of one run, pass or batch of C carries one T: states are created with T and the clock is pinned through `Timestamp`; in a child, `InsertLayered` also stamps incoming rows with the merge's T (X38).
- C's Ts never decrease: captures read a monotone counter, and C's operations are sequential in its domain.
- After a frozen context's last `EndEpoch`, every epoch captured by any descendant is larger: freezing follows its last epoch, and children capture later.
- Consequences: sort columns stay nondecreasing (`CheckSortValue` never throws), and parallel merges carry one value. *Checker:* `ContextEpochTests`, I19.

### 12.2 Union-find

**I4. Root array.** `parents[i] ≤ i`; there is one row per displaced id; rows are ordered by ts. This is unchanged egglog code. `CompressPath` and `CompressAll` replace a parent only by an ancestor, which is smaller.

**I5. Overlay partition.** Classes partition `classOf`'s keys, every class has at least two members, and `canonical = min(members) ∈ members`. Each helper in §5.2 maintains this: dissolve at size 1, recompute on removal of the minimum. *Checker:* union-find check 1.

**I6. When uf-current, every overlay member is P-canonical** (review correction 18: only once catch-up is done). `Union` inserts only `parentFinder.Find` results. `Rekey` processes the parent journal suffix, which is complete by I8 at P: every member that went stale is in the batch and is removed, and it inserts only final P-canonicals. *Checker:* union-find check 1.

**I7. `find_C(x) = min [x]_C`, and coarsening `find_C(find_P(x)) = find_C(x)`.** By induction on depth:
- `find_P` is the minimum of the P-class (I4, or induction).
- Overlay classes are C's classes restricted to P-canonical ids (I5, I6).
- Every P-class has its minimum as its P-canonical, so the minimum of the C-class is `canonical[slot]`, or `find_P(x)` for a trivial class.

*Checker:* union-find check 2; differential check 1 (exact canonical equality).

**I8. Journal.**
- Ids are unique, ts is nondecreasing, and every id is C-non-canonical.
- **J:** every non-canonical overlay member is journaled.
- **Completeness:** journal ids = `{x : find_C x ≠ x} \ {x : find_{P@creation} x ≠ x}`.

*Proof.* Union appends its loser. Re-key appends every canonical id it removes, with the dedupe check. Imports cover parent displacements of C-canonical ids. Classes only coarsen, so canonicity is lost once and never regained. Order holds by I3 and the order check in §5.5. *Checker:* union-find check 3; differential check 4.

**I9. Cursor bounds.** `parentDisplacedAtCreation ≤ unionFindCursor ≤ P.DisplacedCount`, with equality after catch-up. Cursors only advance, and P's count only grows: `$uf` cannot be cleared while descendants live (§6.7, `OverlayDisplacedTable.Clear` throws). *Checker:* union-find check 4.

**I10. Local ids stay local.** No ancestor overlay member, journal id, root row or ancestor table row holds an id of C's blocks.
- Ancestors never read a child's state.
- The lift filters local ids: `Members` returns empty and the candidate is skipped. A term lift mints in the parent.
- Blocks are disjoint (I1).

*Checker:* union-find check 5, and the tables check through ancestors' rebuild indexes (`TryGetSubset(localId)` empty, O(local ids × depth)).

**I11. Isolation.** No child operation writes ancestor state:
- C's states stage through `C.Database.Catalog`, whose entries are C's own;
- `find` is read-only at every level;
- compression happens only in the owner's merge or freeze;
- an ancestor's rebuild index is refreshed from a child only when the ancestor is idle in the same domain;
- frozen tables are sealed.

*Checker:* `StressFrozenRoot` (root array bit-identical before and after).

### 12.3 Tables and views (for a clean V; the ancestors are then clean)

**I12. Key uniqueness (S6).** At most one row with key k is visible to V.
- The own layer gets key k only by: merging with the visible ancestor row (hidden at once, `KeyHeld`); having no ancestor row visible; or a catch-up copy (the source is hidden).
- Incoming keys are canonical when probed (X38), so a write whose key names the same class as a visible row lands on that row's key.
- Later ancestor appends with key k are caught by 2(b), or by 2(a) when their canonical key changes.
- Ancestors are unique in their own views (induction).

*Checker:* collect `(key, layer, row)` over the visible rows.

**I13. View canonicity (S5).** Every id cell of every visible row is canonical in V. For the root this holds after `Settle` (I35) and for programs whose raw writes are canonical (egglog's contract); for every child it holds whenever the child is clean.
- **Own rows:** every own row was canonical when appended (`InsertLayered` canonicalizes; catch-up and rebuild copies are canonicalized). An id that became non-canonical later entered the journal at or after the append, so the pass that consumed that journal row rewrote the row (step 3: rows mentioning journal ids since the mark, plus rows since the mark), and `RunOnTablesCore` notified the layer so the rewrite was merged (X9). `Rebuild` runs whenever the journal mark is behind (`IsClean`).
- **Ancestor rows present at V's creation** were canonical in P: by induction for a child P, and by I35 for the root. Those mentioning an id V displaced later are hidden by step 4 (own losers) or are invisible at P (imports).
- **Ancestor rows appended after V's creation** were examined by V's catch-up, which copied and hid every one that mentions an id non-canonical in V (branch (a)), including ids stale in their own layer.

*Checker:* `Find(c) == c` for every visible id cell.

**I14. Hidden content (row-level context = copy).** Every `r ∈ H_V[t][j]` is either invisible to P or retracted (`HasRetractions`), or else:
- some visible row s has key `key(canonical_V(r))`;
- for an Id output, `Find(s.out) == Find(canonical_V(r).out)`;
- for other functions, `merge(s, r')` returns false on a scratch catalog;
- for relations, s equals r′ except for its timestamp.

*Proof.* Each reason stages or merges the information in the same pass: `Mentions` (copy), `KeyHeld` (the merge output), `Restamped` (copy). Hides are applied only after the carrying rows are pending or appended (X12). *Checker:* tables check I14, with DEBUG reasons.

**I15. Lemma F holds.** For every layer j and every live keyed row r invisible to V, no row with key `r.key` is visible below j. This is §6.4. *Checker:* direct.

**I16. Generation stability.** For every live descendant D of V and every shared table t, `D`'s cursor major equals the layer's major, and every `HiddenSet.Generation` equals it. Compaction is gated (§6.8), clears and deletes are forbidden by decision 1 and R1, and `$uf` is never cleared with descendants. *Checker:* tables check.

**I17. Row cursors.**
- After `Rebuild`, every cursor equals its ancestor's version and stamp.
- `Minor ≤ layer.Version.Minor` at all times.
- Settled depths are frozen.

Cursors commit only after the pass loop, while ancestors cannot change: they share the domain or are frozen. *Checker:* tables check.

**I18. Hidden sets are sealed and bounded.** Outside a `MergeAll` of its owner, every hidden set of a context is sealed: hides happen only inside `MergeAll` or right before the `MergeAll` of the same pass, and the `MergeAll`/`MergeTable` epilogue, `Rebuild`, `ClearTable` and `Freeze` seal every set registered since (X40). So every read path (sealed-state `ContainsSealed`, `HiddenCursor`, `HiddenSet.Union`) agrees with the write path's any-state `IsHiddenHere`. Every row is below its layer's physical count, in the current generation (`Add` takes the count). *Checker:* tables check (`UnsealedCount == 0`); `ExecutorInvariants` check 8; `LookupPathsAgree` after every `Write`, `RunRules` and `FlushUpdates`, rebuilt or not.

**I19. Sort order and creation stamps (S1, S4).**
- Every layer's ts column is nondecreasing, and its run index agrees.
- Every row of context X or of its descendants has `ts ≥ X.CreatedAt`.

In a child every stored row carries the merge's epoch by construction (X38), which is at least `CreatedAt`; descendants are created later. At the root `CreatedAt` is 0. *Checker:* `ExecutorInvariants` checks 3 and 4.

**I20. Monotone visibility (S2).** Within a generation, hidden sets and tombstones only grow; nothing is un-hidden or un-tombstoned. Compaction keeps each row's content and ts and is gated. No operation removes a hide. Any future pruning (B1) must copy the row with a fresh epoch instead. *Checker:* by construction; `HiddenSetTests`.

**I21. New rows are stamped at or above the floor (S3, C7 condition 2).** A row appended to context X's layer carries an epoch at least as large as every watermark committed so far by X or any descendant of X.
- In a child, every appended row carries the merge's epoch (X38), which is at least every earlier capture of X (I3).
- A descendant's commit T happened while X was idle (same domain) or frozen. After the descendant's operation the clock is at least T+1, and X's later captures are larger.
- At the root, every engine path and the bridge stamp with the state's timestamp; host writes are stamped at flush (X24); raw `Context.Write` is asserted in DEBUG.
- Catch-up (c′) re-stamps, for each reading context, any ancestor append below that context's floor (§7.2).

*Checker:* `RestampedCopies == 0` in tests.

**I22. Retraction safety.** No context with a live descendant retracts a shared row, and a child retracts only under a frozen parent. This is the guard in §6.7, at every entry point. *Checker:* `RetractionTests`.

**I35. The root is settled before it is shared.** When a child is created from the root, and when the root freezes, every root row is canonical in the root's view: `Settle` rewrote the rows written since each table's rebuild mark, and rows before the mark are kept canonical by the journal-driven rebuilds (§7.6). *Checker:* `RootSettleTests`; the root part of the tables check, run at `CreateChild` in DEBUG.

### 12.4 Lifecycle and concurrency

**I23. Frozen means immutable, and frozen contexts form a prefix of every chain.**
- Every mutator calls `ThrowIfFrozen` or stages into a sealed `PendingState`.
- Indexes refresh only on a version change, and a frozen context cannot change version. Lazy cells are built once, serially.
- A frozen context never writes its own arrays after the freeze, even for tables added later (X43).
- `Freeze` requires a frozen parent.

*Checker:* bridge check C2 (frozen state current: rebuild indexes `UpdatedTo == Version`, union-find compressed, catalogs never reset); `ExecutorInvariants` check 2.

**I24. Thread domains.** A non-frozen context and its descendants share one `ThreadAffinityGuard`, and every public operation enters it. Frozen contexts are only read. Children of a frozen context write only their own state, the counters, the interners, copy-on-write registries, once-cells and locked child lists. *Checker:* `TwoThreadsOnOneDomainThrow`; concurrent differentials.

**I25. Stop flag (L4).** `IsContradicted` implies a contradiction row is visible in the view, or merged by the last run: `RunRuleSet` always merges, and a nullary row merges unconditionally. The flag is sticky. A stopped context answers only `IsContradictory`. Treating it as vacuous is sound. *Checker:* bridge check C5.

**I34. Structure publication during a run.** During a search of context C (`ContextTables.InSearch`):
- C's catalog changes only through `TableCatalog.Publish`, under `ContextTables.layerLock`, as a new map published with `Volatile.Write`;
- C's layer slots change only under the same lock, each slot or grown array published with `Volatile.Write`;
- C's table map, view cache, `ViewGeneration` and hidden sets do not change.

Outside a search, on the domain thread, `AdoptDeferred` moves new layers into the table map (at each `TakeRound`), and the `MergeAll` epilogue seals and invalidates. So no reader sees a torn map or a view change within a run, at most one layer exists per (context, table), and every staged write reaches a layer that `MergeAll` visits. *Checker:* `LayerCreationConcurrencyTests`, `LayeredParallelRunTests`.

### 12.5 Execution

**I26. Visibility on every path (V).** A row reaches a sink, binding set, refined subset or existence check only if it is visible. This holds because:
- scans use the hidden-aware loops;
- per-subset index builds scan with `lv.Hidden`;
- cached-index results carry `MayHoldInvisible` and pass `RefineVisible`;
- headers and root nodes feed only those paths;
- point lookups follow §6.5;
- the hidden sets those paths read are sealed (I18).

*Checker:* T2, T3, T8.

**I27. Match equivalence (J).** The layered executor emits the same set of distinct bindings as a naive join over C's visible rows.
- Visible row sets of different layers are disjoint, and their union is the view.
- A driver partitions its candidates by layer and groups each layer by key.
- Probed sides are refined to the union of hits over layers.
- Fused and Mat stages enumerate rows or tuples one at a time and probe unions.
- By induction on stage position, the reachable (bindings, row sets) equal the flat executor's, except that a driver key present in k layers yields k frames that partition the flat row set.

Multiplicity may exceed the flat executor's only for keys that the own layer shares with ancestors (allowed by decision 6). *Checker:* T8.

**I28. Cache keys and catalogs.**
- A cached child or index is a function of (node rows, slot, key) for a run (§9.6).
- A layer's catalog indexes only that layer, refreshed from its own `TableVersion`.
- Hides are not content.
- Frozen catalogs are never reset (frozen databases never merge).
- One generation per (layer, table) per run (I16, A-S6: the domain guard prevents changes during a search).

*Checker:* T4, T5; `ExecutorInvariants` check 2.

**I29. Header coherence.** Headers and root nodes come from the same `TableView`s: the rule set's `ViewSnapshot`, taken at `Build` from the cache that header evaluation used. Nothing invalidates a view during the search (I34), and `RunRuleSet` asserts `ViewGeneration` is unchanged. *Checker:* T6.

### 12.6 Rules and lift

**I30. Executed constants (D3, K).** K equals the canonical form, in the running context, of every Id constant of the rule, query and action (X19); in a bare `Database`, the written values. Substitution rewrites exactly the recorded positions (§9.8), so the run matches and writes exactly K. A watermark is used only when its stored K equals the current K, so a constant that lost a union forces a full run, at the root as in a child. *Checker:* bridge check C1 (lengths); T7.

**I31. Watermark soundness (C7).** Let rule r run in clean context C with watermark W (own or inherited). After its actions are merged and C is rebuilt, every match of r in C's view has been applied, in C or in an ancestor whose effects C sees.

- **Lemma 1 (the variants partition the view).** By I19, `GeConst(ts, W)` and `LtConst(ts, W)` split each layer into a suffix and a prefix (one binary search per layer). By I26, invisible rows are on neither side. For a match m, take the smallest atom index i whose row has `ts ≥ W`. Variant i finds m by I27.
- **Lemma 2 (old rows are stable).** Rows appended to C's layer or any ancestor's after C's run at W have `ts ≥ W` (I21). A row now visible with `ts < W` was appended earlier, and by I20 it was visible then, with the same content.
- **Case 1 (own W).** A match made only of rows with `ts < W` existed at C's previous run, with equal K (I30). By induction over runs it was applied, and contexts are monotone, so its effects persist. Matches with a new row are found by Lemma 1.
- **Case 2 (inherited W0).** Let A be the nearest ancestor with `(W_A, K_A)`, A′ its child on the path, and `W0 = min(W_A, A′.CreatedAt)` (§10.2). Take a row visible in C with `ts < W0`:
  1. By I19 it is not owned by A′ or below, so it lies in A's layer or an ancestor's.
  2. It is hidden at no level between its layer and C, so it is visible in A.
  3. `ts < W_A`, so by I21 at A it was appended before A's run. By I20 it was visible to that run, unchanged.
  4. `K = K_A`.

  So a match over such rows is a match of A's run over rows older than `W_A`, applied in A by Case 1 recursively. Because r's actions are query-determined, applying the match in A wrote what applying it in C would write, up to the equalities C adds; its effects reach C rewritten by `find_C = overlay ∘ find_A` (I7). Matches with a row of `ts ≥ W0` are found by Lemma 1.
- **Commit after the merge (X18) is sound:** every row the run writes has `ts ≥ T`. A failure commits nothing (D18).
- **Preconditions, all enforced:**
  - actions are query-determined: `AllowInheritance` is false otherwise (`ActionReadAnalysis`, §10.2);
  - naive, split and callback rules do not inherit (D4, D21);
  - a non-frozen ancestor never runs concurrently with its descendants (I24);
  - rows are stamped at or above the floor (I21, by construction in children).

*Checker:* T9; `SeededWatermarkDifferentialTests` (§17, M10.3).

**I32. Member maps (L2).** `Members(c)` is exactly the set of non-local, parent-canonical m with `Find_K(m) = c` (or `[c]` for an untouched class). The overlay's classes are exactly C's merged classes over P-canonicals once current (I5, I6). `MemberMap` copies the slots, filters local ids, and is invalidated by `UnionFindOverlay.Version`. *Checker:* bridge check C4.

**I33. Lift soundness and completeness (L1, rule-layer §3.9, G1 fixed).**
- **Soundness.** A lifted fact or equality holds in every live case, vacuous cases have no satisfying assignment (I25), and coverage is the caller's obligation. A case-joined value satisfies `v_K ⊑ J`. `StageTerm` asserts nothing.
- **Completeness of facts.** Suppose R(Y) holds in seed case r and not in P. Let ρ = R(Find_r(Y)).
  - If ρ is an own row of r, step (a) yields Y through `Members`.
  - Otherwise ρ is read through and visible in P. Since R(Y) is not in P, some `Y_i ≠ c_i = Find_r(Y_i)`, so c_i is a non-local group root mentioned by ρ, and step (b) finds ρ (`ScanAncestorsMentioning`, L1).
- **Completeness of equalities.** Parent-canonical ids `y1 ≠ y2` that are equal in every live case lie in one group of r, whose list includes the root when it is a parent id (I32). The meet then refines fully.
- **Termination.** Each round grows P from finite case contents, with `RoundLimit` as a backstop.

*Checker:* `LifterPropertyTests`.

### 12.7 L3 checker composition (B2)

`Context.CheckInvariants()` (`Contexts/Context.Invariants.cs`) requires `IsClean`, collects every violation, and throws `InvalidOperationException` listing them. It runs:

1. **`CheckUnionFindInvariants`:**
   1. members P-canonical, `canonical = min ∈ members`, `classOf` consistent, `Σ counts == classOf.Count` (I5, I6);
   2. coarsening on members, journal ids, parent journal ids since creation, and 256 sampled ids (I7);
   3. journal uniqueness, order, non-canonicity, J, and the completeness form "every parent suffix id is non-canonical here" (I8);
   4. cursor bounds and `IsUnionFindCurrent` (I9);
   5. local ids absent from ancestor overlays, journals and root rows (I10);
   6. frozen prefix, and a flattened map that agrees with the chain (I23);
   7. `Gate.LiveDescendants` equals the recursive count.
2. **`ContextTables.CheckInvariants`:** I12–I18 (including `UnsealedCount == 0`), I10 (the tables part), and every own layer adopted with an empty pending queue.
3. **`ExecutorInvariants.Check`** (`Execution/ExecutorInvariants.cs`): view coherence, frozen catalogs current, per-layer sort order, creation stamps, watermark bounds (`Since ≤ clock`, `≥ CreatedAt`), canonical view, unique keys, hidden rows in range.

`EGraphContext.CheckInvariants()` (always compiled) calls the above and adds:
- C1: progress `LastRunAt ≤ clock` and `≥ CreatedAt` in a child;
- C2: frozen state current;
- C4: member maps;
- C5: stop implies `IsContradictory`;
- C6: no own displaced id is an eq-container element;
- C7: tree and descendant counts.

It throws `ContextInvariantException` (`Bridge/ContextInvariantException.cs`).

Harnesses run the checker after every `Rebuild`, `RunRules` and `FlushUpdates`, and run `TableViewTests.LookupPathsAgree`'s comparison (write-phase `TryGetVisibleRow` against read-phase `TableView.FindRow` and a filtered scan) after every `Write`, `RunRules` and `FlushUpdates`, whether or not it rebuilt. DEBUG builds run the checker at the end of `Context.Rebuild`.

---

## 13. Edge cases

**Union-find**
1. A union of equal or already-equal ids appends nothing, and may carry an old ts.
2. A union between distinct classes whose ts is older than the journal's last row throws and changes nothing.
3. Ids beyond the root array, never minted, or local are canonical unless merged.
4. The parent displaces an id C never touched → an import.
5. The parent displaces a key or a canonical → `Rekey`, which may dissolve a class. Chains inside one batch use the final `find_P`.
6. A local id smaller than a parent id (G2; only under a writable parent): the local id wins, the parent id becomes an own loser, and `ParentIdLosers` counts it (L7).

**Tables**

7. **Untouched table:** the view is the parent's view instance.
8. **No id columns:** layers come only from C4 merges; such a table has no rebuild index anywhere, and `ScanAncestorsMentioning` returns at once.
9. **Zero-key tables:** `FindRow([])` works everywhere; the nullary contradiction table freezes without a rebuild index.
10. **A copy whose key did not change:** its probe hits the now-hidden original → absent → appended.
11. **Two copies with one canonical key:** they fold through the own row (lattice, so order does not matter).
12. **Rebuild `remove(k)` plus `insert(k′)` in one batch:** delete runs first.
13. **`UnionIdMerge` returns false:** the ancestor row stays visible and the union is staged in C.
14. **A hub loser (G3):** copy volume equals its uses, measured by `RowsCopied`.
15. **Non-lattice table touched by a child:** throws when a layer would be created, on the first staged write or copy into it (decision 2). A lookup that hits creates no layer and does not throw.
16. **A merge function reading its own stratum in a child:** `InvalidOperationException` (the root throws `KeyNotFoundException`).
17. **Context-private tables:** not layered, no catch-up, deletes allowed, never gated.
18. **A write with a stale id:** in a child it is stored under the canonical key and merges with the visible row there; at the root it is stored as given and rewritten by the next rebuild, or by the settle when a child is created or the root freezes.
19. **A delete with a stale key:** a no-op in a child (visible rows are canonical); at the root it removes only a row written with that stale key, as in egglog.
20. **A raw write with a wrong sort cell:** in a child the cell is overwritten with the merge's epoch; at the root DEBUG asserts, and a descendant's catch-up re-stamps a root append below its floor.
21. **Rule tasks staging into tables without layers, in parallel:** one layer per table, no lost writes (I34).

**Lifecycle**

22. `Freeze` with a writable parent, a stopped or faulted context, or a failure not taken → throws. A second `Freeze` is a no-op. Freezing a context that has live children is allowed: the children catch up once and get their own domains.
23. `Dispose` of the root or of a context with children → throws. A second `Dispose` is a no-op. Every later call → `ObjectDisposedException`.
24. `CreateChild` on a dirty writable parent rebuilds it first (and settles the root). On a stopped or faulted parent, at depth 31, or re-entrantly from inside an operation of the same domain, it throws.
25. Clock or ids reaching their limits → `OverflowException` (store ids at `UnionFind.MaxId`). Children consume the shared counter (§4.2 memory note).
26. `Database.Clone()` of a child, or of a root with descendants → throws.
27. `AddTable` is allowed at any time (X16); frozen contexts answer for the new id through their parents (X43). `AddCounter` after the first child → throws.
28. Root writes staged and not flushed before `RunRules` merge in the run at T; root reads in between see the committed rows (X42).

**Rules**

29. A reused `RuleId`: the generation differs → no stale progress.
30. A zero-atom rule under an inherited `W0 > 0` does not re-fire, which is correct because the ancestor's effects are visible.
31. A constant that lost a union, at the root or in a child → K differs → full run, matching the class.
32. A rule whose actions read a table (`Lookup`, a function's value) → never inherits; a child re-runs it fully on first use.
33. `IsLocalId`, `ScanMerges`, `ScanOwn` or `ScanAncestorsMentioning` on the root → throws.

**Lift**

34. A lift with zero cases, a frozen parent, or a foreign case → throws.
35. All cases vacuous → the parent contradicts.
36. A function with an Id output and no common parent member → skipped (a documented gap).
37. An eq container interned in a child → throws (G-a). A child union of an eq-container element → `NotSupportedException` (G-b); dispose the context.

---

## 14. Performance budgets

### 14.1 What the root pays

| Added work | Where | Cost |
|---|---|---|
| Clock comparison in `ReadCounter` | Per call | 1 int compare |
| Block test in `IncrementCounterInView` | Per mint | 1 null test |
| Layer dispatch in `SortedWritesTable.Merge` | Per table merge | 1 null test |
| Compaction gate in `MaybeRehash` | Per merge | 1 field load |
| Retraction guard in `StageRemove` | Per staged-removal call | 1 null test (the guard is null at a root without live descendants, §6.7); with descendants, 1 `CheckRemove` call per staged removal |
| Layered branch in `PredictRow`/`PredictColumn` | Per call (bridge merge functions call it once per merged row) | 1 null test |
| Layered branch in lookup instructions | Per instruction batch | 1 null test |
| `LookupOrInsertDefault` buffer order | Per instruction batch | 1 null test |
| Catalog read in `GetForStaging` | Per (state, table) buffer creation | 1 volatile load |
| Layer adoption in `TakeRound` | Per merge round | 1 null test |
| Contradiction check in the `MergeAll` loop | Per round | 1 null test |
| `MergeAll`/`MergeTable` epilogue | Per call | 1 null test |
| `ThrowIfFrozen` | Per entry point | 1 volatile read |
| Sealed check in `PendingState` | Per buffer flush | 1 volatile read |
| New `TableKind` arms | Per scan or lookup call | 1 jump-table arm |
| `hidden.IsNone` | Per scan call | 1 test |
| `PieceCount == 1` | Per probe | 1 cached compare |
| `Layer` byte in a frame | Per dense refinement | 1 byte store |
| Child-cache slot | Per child-cache lookup | 1 `IsMultiLayer` test (single-layer nodes keep today's slot) |
| Id constants (Store-backed root) | Per rule per run, for rules with constants | O(constants) `FindNaive` calls and one compare against the written vector |
| View snapshot | Per rule set build | O(tables read), cached instances; outside the measured run |
| `CompressRows` | Per merge that unions | O(new unions × path) |
| Settle | Per `CreateChild` of the root, and at its freeze | O(rows written since the last rebuild) |
| Freeze | Once | O(IdCount + rows) |

`Canonicalizer` is identical after JIT specialization. Nothing is added per scanned row, per staged row, or per find.

**Gates:** the M9.0 root benchmark set within 2% (X33), each milestone on the classes listed for it in §17, and `ExecutorAllocationProbeTests` unchanged.

### 14.2 What a child pays (proportional to what it touches)

| Operation | Cost |
|---|---|
| Creation | O(tables + depth) small objects; nothing is allocated for the union-find or a table before first use |
| Layer creation | One empty single-shard table and `TableInfo` (about 1–2 KB, measured by `ManyChildrenBenchmarks`) plus an O(tables) catalog copy, once per touched table |
| `find` | Root `FindNaive` (one hop when frozen) + at most one dense load + one probe per non-empty level below the prefix |
| Own union | 2 parent finds + 2 probes + O(1) or O(smaller class) |
| Lookup | One hash probe per layer + one sealed-set test on a hit |
| Incoming row at the merge | One `find` per id cell (X38) |
| Pass step 3 | O(Δ ids + their uses + rows since the mark), own layers only (incremental override, §7.3); O(own rows) only when the delta exceeds them |
| Pass step 4 | O(depth × new own losers) index probes + O(uses); never a scan |
| Catch-up | O(appends to unsettled ancestors); zero under frozen ancestors after the first time |
| View build | O(depth) per touched table + O(Σ live sets) |
| Executor | Ancestor dynamic-index work ≤ `|rows with ts ≥ W| + |probe results|`; dense ancestor pieces use shared indexes at zero build cost |
| Memory | ~8 B per overlay member + 4 B per member-list entry + ~40 B per class + 8+8 B per journal row; 4 B per hidden row (or n/8 B as a bitset); tables and views at 8 B × tables |

**Guidance to the rule layer:** saturate, or run every rule once after the last change, before `Freeze`. Otherwise every child re-matches the parent's last delta.

---

## 15. Changes to existing files

| File | Change |
|---|---|
| `UnionFind/UnionFind.cs` | `CompressPath`, `CompressAll` |
| `UnionFind/DisplacedUnionFind.cs` | `CompressRows`, `CompressAll`, `LastTimestamp`; `Search` becomes `internal static` |
| `Tables/DisplacedTable.cs` | `DisplacedSpec` internal; compression in `Merge`; `Freeze` override; `HasPendingUnions`; throws when frozen; hidden-aware scan overloads and `RefineVisible` |
| `Tables/Canonicalizer.cs` | Bodies move to `CanonicalizeLoops<RootIdFinder>`; public surface unchanged |
| `Tables/TableKind.cs` | Adds `OverlayDisplaced` |
| `Tables/Table.cs` | `OverlayDisplaced` arms; `RefineVisible(Subset, HiddenRows)`; hidden-aware overloads; `virtual void Freeze()` |
| `Tables/SortedWritesTable.cs` | `Merge` dispatches to `MergeLayered`; `Freeze` override (rebuild index only with rebuild columns) |
| `Tables/SortedWritesTable.Rehash.cs` | Gate in `MaybeRehash`; `ForceRehash`; `CompactionsBlocked` |
| `Tables/SortedWritesTable.RebuildIndex.cs` | `RefreshRebuildIndex` becomes internal; `SharedRebuildIndex` (nullable); `IsShared` |
| `Tables/SortedWritesTable.Rebuild.cs` | Layered incremental override in `RebuildWith`; `SettleAppends` |
| `Tables/SortedWritesTable.RebuildWatermark.cs` | `FirstRowSinceRebuild` and `MarkRebuilt` internal; `HasRowsSinceRebuild` |
| `Tables/SortedWritesTable.Lookup.cs` | `FindRowHashed` |
| `Tables/SortedWritesTable.Scan.cs` | Hidden-aware loops; `RefineVisible` |
| `Tables/TableCatalog.cs` | Volatile map reads; `Resolver` (init); `GetForStaging`; copy-on-write `Publish` |
| `Tables/ExecutionState.cs` | `CreateBuffer` uses `GetForStaging`; `StageRemove` calls `guard?.CheckRemove`; the clone copies view, layers, guard and views |
| `Tables/PendingState.cs` | `Seal`, `IsSealed`; `Enqueue*` throw when sealed |
| `Planning/QueryEntry.cs` | `IsIdConstant` flag; `IdConst(Value)` factory |
| `Actions/ExecutionState.Database.cs` | `ReadCounter` pin; the view constructor sets `layers` and `guard`; `GetTable` throws when `layers` is set |
| `Actions/ExecutionState.Predicted.cs` | `ReadTable` throws when layered; layered `PredictRow`/`PredictColumn` (both forms); `ValueFromCounter` rejects `≥ StaleRep` |
| `Actions/ExecutionState.Actions.cs` | Layered dispatch at lines 137, 143, 208 and 290; `LookupOrInsertDefault` creates its buffer at the first miss in a child |
| `Actions/VectorizedLookup.cs` | `TableView` overloads; `OverlayDisplaced` arm |
| `Containers/ExecutionState.Containers.cs` | Unchanged; documented as reading the shared root instance |
| `Database/Database.cs` | `AddCounter` sealing; `ClearTable` calls `RetractionGuard?.CheckClear`, bumps `ChangeStamp`, calls `ThrowIfFrozen`, routes in a child; `NewBuffer` uses `GetForStaging`, calls `ThrowIfFrozen`; `Clone` guard; `IncrementCounter` → `IncrementCounterInView`; `uint?` timestamps; layered member behaviour per §3.4; `GetTableSpec`, `GetTableName` |
| `Database/Database.Merge.cs` | `uint?` timestamp; adoption in `TakeRound`; contradiction round stop; serial strata when layered; `ChangeStamp` in `MergeOne`/`MergeTable`; `AfterMerge` epilogue in `MergeAll` and `MergeTable`; `ThrowIfFrozen` |
| `Database/Database.Rebuild.cs` | No change in code; `RunWithTakenOut` and `RunOnTables` are now also called by `ContextTables` and `Context.Settle` |
| `Database/DatabaseView.cs` | `IncrementCounter` → `IncrementCounterInView`; `ClockCounterRep`; `IncrementSharedCounter`; layered `GetTable` (throws), `GetTableName`, `TableIds` |
| `Database/CounterSet.cs` | `IncrementBy(CounterId, long)` |
| `Database/DependencyGraph.cs` | Copy-on-write `levelSnapshot` for `LevelOf` |
| `Database/ExternalFunctionRegistry.cs` | Copy-on-write snapshot |
| `Database/ExternalFunction.cs`, `Database/DelegateExternalFunction.cs` | `ReadsTables` |
| `Database/TableInfo.cs` | `FreezeIndexes()` |
| `Database/Database.Constraints.cs` | Delegates to `ConstraintProcessor` |
| `Database/ColumnCardEstimator.cs` | Static per-`TableInfo` overload |
| `Indexes/Index.cs`, `Indexes/IndexCatalog.cs` | `Refresh(Table, bool allowParallel)`; `SetSerialRefresh` |
| `Indexes/ColumnIndex.cs`, `ColumnIndex.Build.cs`, `TupleIndex.cs`, `TableIndexing.cs`, `ColumnIndexGatherSink.cs` | Hidden-aware builds; `AddValue` |
| `Concurrency/NotificationList.cs` | `IsEmpty` |
| `Containers/ContainerValues.cs`, `ContainerEnvBase.cs`, `ContainerEnv.cs`, `ContainerValueIndex.cs`, `Database.Containers.cs` | `holdsIds`; `HasEqContainers`; `AnyEqContainerMentions`; G-a; `MintId` via the shared counter; `ContainsElement`; child throws; dirty-log append |
| `Planning/PlanTemplate.cs`, `PlanInstance.cs` | `InstantiateLayered`; `Instantiate` kept over `SingleLayerEvaluator`; `LayeredSubset[]` headers |
| `Query/IdConstant.cs` | Remarks per X19 |
| `Query/ActionInfo.cs` | `IdConstants` |
| `Query/CachedPlan.cs` | Variants (`GetVariantFor`) |
| `Query/RulePlan.cs` | `WrittenTemplate`, `WrittenActions`, `CanonicalConstants` |
| `Query/RuleSet.cs` | `Views`, `ViewGeneration`; `BuildCachedPlan` uses written plans |
| `Query/RuleSetBuilder.cs` | Instantiates through `db.View`; computes K and substitutes in `AddRuleFromCachedPlan` and `AddBuiltRule`; snapshots views in `Build`; `EstimateSize` through the layered member |
| `Query/RuleBuilder.cs` | `GetSpec` via `GetTableSpec`; records `ActionIdConstant`s |
| `Query/RuleQueryBuilder.cs` | Arity via `GetTableSpec`; `GetIdColumns` layered |
| `Query/SymbolMap.cs` | Names via `GetTableName` |
| `Query/Database.Query.cs` | Layered `IQuerySchema`, `IPlanStatistics`, `IHeaderEvaluator`, `PlanQuery` per §3.4; `SplitConstraints` via `ConstraintProcessor` |
| `Execution/TrieNode.cs`, `TrieNodePool.cs`, `Prober.cs`, `ProberCursor.cs`, `FrameUpdate.cs`, `FrameUpdates.cs`, `JoinRun*.cs`, `SparseColumnIndex.cs`, `RuleWork.cs`, `Database.Execution.cs` | Per §9.5 and §9.1 (`JoinRun` and `RuleWork` take the snapshot; `RunRuleSet` sets `InSearch` and the root state's views) |
| Tests: `TrieNodeTests`, `ProberTests`, `FrameUpdatesTests`, `CachedPlanTests`, `RuleSetBuilderTests`, `TrieNodeSpareIndexTests`, `SparseColumnIndexTests` | Mechanical API updates only; assertions unchanged |
| Tests: `Planning/PlanInstantiationTests`, `Execution/DecomposedExecutionTests`, `Planning/PlanTestDatabase`, `Query/DatabaseQueryTests` | Compile unchanged through the kept `Instantiate` overload |
| Tests: `Execution/JoinPathTests`, `Execution/ExecutorVisibilityTests` | No source change (root `GetTableInfo` calls remain valid); re-run as executor regression |
| `src/Eggnog.Relations.Tests/Eggnog.Relations.Tests.csproj` | `ProjectReference` to `../Eggnog.Storage/Eggnog.Storage.csproj` (second oracle), used only through `using Spike = Eggnog.Storage;` (X32) |
| `Bridge/*` (M8) | `EGraph`, `RuleInfo`, `PanicFunction`, the rule builder (lowers Id-typed constants through `QueryEntry.IdConst`), `IterationReport`, scheduler hooks, per §10 |

---

## 16. New files (one type each, namespace `Eggnog.Relations`)

**Engine (`src/Eggnog.Relations/`):**

| Folder | Files |
|---|---|
| `Contexts/` | `Store`, `Context` (+ `.Lifecycle`, `.Epochs`, `.UnionFind`, `.CatchUp`, `.Writes`, `.Reads`, `.Invariants`), `ContextOptions`, `Database.Contexts` (partial), `Database.Freeze` (partial), `IdBlockAllocator`, `IdBlock`, `IMergeVisitor`, `UnionFindStatistics`, `EqContainerGuard` |
| `UnionFind/` | `UnionFindOverlay`, `DisplacedJournal`, `LayeredUnionFind`, `FlattenedUnionFind`, `IIdFinder`, `RootIdFinder`, `LayeredIdFinder` |
| `Hashing/` | `IdEntry`, `IdEntryHasher`, `IdEntryMatch` |
| `Layers/` | `LayeredSchema`, `LayeredTableSchema`, `ContextTables` (+ 9 partials), `TableLayer`, `AncestorCursor`, `HiddenSet`, `HiddenRows`, `HiddenCursor`, `VisibleRowFilter`, `HiddenOnlyFilter`, `HiddenSkippingSink`, `HideReason`, `TableView` (+ `.Lookup`), `LayerView`, `ILayerViewSource`, `DescendantGate`, `RetractionGuard`, `LayerStatistics`, `ExecutionState.Layers` (partial) |
| `Tables/` | `OverlayDisplacedTable`, `OverlayCanonicalizer`, `CanonicalizeLoops`, `OverlayDisplacedConstraintFilter`, `SortedWritesTable.Layered` (partial), `RowOps` |
| `Actions/` | `OverlayDisplacedLookup`, `LayeredKey1Lookup`, `LayeredKey2Lookup`, `LayeredKey3Lookup`, `LayeredKeyNLookup` |
| `Offsets/` | `LayeredSubset`, `SubsetArrayPool` |
| `Execution/` | `ContextView`, `ViewSnapshot`, `ProberPiece`, `LayerHits`, `ExecutorInvariants`, `ExecutorCounters` |
| `Planning/` | `ILayeredHeaderEvaluator`, `SingleLayerEvaluator`, `CachedIndexKey`, `PlanIndexKeys` |
| `Database/` | `ConstraintProcessor`, `FreezeWarmup`, `SettleTableOperation` |
| `Query/` | `CanonicalConstants`, `ConstantSubstitution`, `TemplateSubstituter`, `ActionSubstituter`, `ConstantVariant`, `ActionIdConstant`, `ActionReadAnalysis` |
| `Concurrency/` | `ThreadAffinityGuard`, `ParallelismBudget`, `ParallelismLease` |
| `Containers/` | `ContainerDirtyLog` |
| `Bridge/` | `EGraphContext` (+ 8 partials), `RuleProgress`, `RuleTable`, `HostWrites`, `ContextLocalSlot`, `StopOnContradictionFunction`, `SaturateReport`, `ConcurrentChildren`, `ContextInvariantException` |
| `Cases/` | `MemberMap`, `ClassPartition`, `LiftSpec`, `LiftTable`, `LiftTableKind`, `LiftOptions`, `LiftReport`, `LiftSignature`, `LiftCandidates`, `LiftKeySink`, `LiftWrites`, `Lifter` (+ `.Equalities`, `.Facts`), `CaseSet`, `SplitDriver`, `SplitRequest`, `SplitKey`, `SplitRecord`, `SplitOptions`, `SplitReport`, `ISplitSource`, `ISplitDemand`, `ICaseSchedule` |

Tests and benchmarks are listed with their milestones in §17.

---

## 17. Milestones

Every milestone has the same baseline definition of done:
- `dotnet build src/Eggnog.Relations.Tests -f net10.0` has no errors and no warnings in new files;
- every new test and every existing suite passes, with only the mechanical updates of §15;
- the conventions hold (one type per file, XML docs);
- the egglog defects of §8.1 touched by the milestone are fixed, with a citing comment;
- the root benchmark classes the milestone gates on are within 2% of the M9.0 baseline.

Each milestone below adds its own gates. A milestone may ship interim behaviour for a later milestone's feature; the interim behaviour is stated, and tests that need the full feature live in the milestone that delivers it.

### M9.0: Root benchmark baseline (before any engine change)

**Content:** new classes in `src/Eggnog.Relations.Benchmarks/Root/`, one type per file, run on the unchanged engine; their exported reports are kept with the milestone PR as the baseline.

| Class | Workload | Exercises |
|---|---|---|
| `RuleSetRunBenchmarks` (with `AcSaturationWorkload`, `MathWorkload`) | The AC-saturation and math e-graph programs of `Execution/AcSaturation.cs` and `Execution/ExecutionMathEgraph.cs`, ported to the benchmark project, at 20k and 200k rows, serial and parallel | `RunRuleSet`: headers, trie, probers, kernels, actions, `MergeAll` |
| `ConstrainedScanBenchmarks` | `ScanGenericBounded` with `EqConst`, ts ranges and slow constraints; `RefineLive`; tables with and without tombstones | scan loops, `TableKind` dispatch |
| `IndexRefreshBenchmarks` | `ColumnIndex` and `TupleIndex` full and incremental refresh at 20k–200k rows | index builds |
| `DisplacedRebuildBenchmarks` | `DisplacedTable.Merge` of N unions, then `Database.Rebuild` over function tables | union-find merge, compression, `ApplyRebuild` |
| `ActionLookupBenchmarks` | Action batches dominated by `LookupOrInsertDefault`, `Lookup` and a merge function calling `PredictRow` | action dispatch, prediction |

The existing `Writes/WritesBenchmarks` and `UnionFind/*` are part of the set.

**Gate map** (classes each milestone must keep within 2%):

| Milestone | Classes |
|---|---|
| M9.1 | `ActionLookupBenchmarks`, `DisplacedRebuildBenchmarks`, `WritesBenchmarks`, `RuleSetRunBenchmarks` |
| M9.2 | `DisplacedRebuildBenchmarks`, `UnionFind/*` |
| M9.3 | `WritesBenchmarks`, `ActionLookupBenchmarks`, `RuleSetRunBenchmarks` |
| M9.4 | `DisplacedRebuildBenchmarks`, `RuleSetRunBenchmarks` |
| M10.1 | `ConstrainedScanBenchmarks`, `IndexRefreshBenchmarks` |
| M10.2 | `RuleSetRunBenchmarks`, `ConstrainedScanBenchmarks` |
| M10.3 | `RuleSetRunBenchmarks` (plus the M8 bridge workloads once they exist) |

**Done when:** every class runs in-process (`Program.cs`) and the baseline reports are recorded.

### M9.1: Store, contexts, clock and ids (no layered rows yet)

**Content:**
- `Store`; `Context` skeleton (`CreateChild` without layers, `Dispose`, `Domain`, `Gate`, options, depth limit, `CreatedAt`, `Version`, re-entrance refusal);
- the child `Database` constructor and shared parts, with the layered member behaviour of §3.4 that needs no layers (definitions throw, names and specs from the schema);
- the C1 pin (`ExecutionState.ReadCounter`); `uint?` timestamps;
- `IdBlockAllocator` (16-id first blocks, `UnionFind.MaxId` cap); `CounterSet.IncrementBy`;
- schema rules (X16): `DependencyGraph.levelSnapshot`, copy-on-write `ExternalFunctionRegistry`;
- the `Clone` guard; `NotificationList.IsEmpty`; `ThreadAffinityGuard`; `DescendantGate`;
- `ContainerEnv.MintId` on the shared counter.

**Interim:** the child `Database` gets a placeholder `DisplacedTable` at `TableId 0` that is never written (unions arrive in M9.2), and no `Resolver`: staging into a child throws `NotSupportedException("Layers arrive in M9.3.")`.

**Tests:**
- `Contexts/StoreTests`: `$uf` is `TableId 0`; counters 0 and 1; clock starts at 1; `AddTable` after children works; `AddCounter` throws; `ReadClock` overflow.
- `Contexts/ContextEpochTests`:
  - while a sibling thread advances the clock, `ReadCounter(clock)` returns T;
  - every row of a run has ts T;
  - root timestamps are identical to the pre-change sequence.
- `Contexts/IdBlockAllocatorTests`:
  - 8 allocators plus root mints on 8 threads, about 10⁶ ids, all distinct;
  - `Contains` is exact;
  - block sizes follow the rule (16 first);
  - overflow at `UnionFind.MaxId`.
- `Concurrency/ThreadAffinityGuardTests`.
- `Database/DependencyGraphConcurrencyTests`.
- `Database/ExternalFunctionRegistryConcurrencyTests`.
- `Contexts/ContextLifecycleTests` (non-frozen chains): dispose rules, gates, depth limit, the shared domain, two threads on one domain throw, re-entrant `CreateChild`/`Dispose` throw. Domains under frozen parents move to M9.2.

**Done when:** the baseline holds and `ExecutorAllocationProbeTests` is unchanged.

### M9.2: The layered union-find

**Content:** §5 in full:
- overlay, journal, layered and flattened finders, `IIdFinder` family;
- `CanonicalizeLoops`, `OverlayCanonicalizer`, `OverlayDisplacedTable` and its filter and lookup, `TableKind` and switch arms;
- root compression; `DisplacedTable.Freeze`;
- the `Context` union-find API, `CatchUpUnionFind`, `ScanMerges`;
- union-find freeze and flattening; `CheckUnionFindInvariants`.

**Interim:**
- A minimal `Context.Write`: domain, `BeginEpoch`, `CreateExecutionState`, action, `Flush`, `RecordFailure`, `MergeAll`, `EndEpoch`, faulting. The child's `Resolver` maps `$uf` to the `OverlayDisplacedTable` and throws `NotSupportedException` for other tables until M9.3.
- `Context.Rebuild` runs union-find catch-up plus `MergeAll`; `NeedsCatchUp` is `false` while `Tables` is null; `IsClean` omits the tables term.
- A union-find-only `Context.Freeze`: `Rebuild`, `DisplacedTable.Freeze` / `OverlayDisplacedTable.Freeze`, flattening, domain reassignment and the volatile frozen flag, so frozen parents and their concurrent children exist.

**Tests:**
- `UnionFind/`:
  - `UnionFindOverlayTests`;
  - `UnionFindOverlayRekeyTests`: the seven hand cases (key displaced; canonical displaced; dissolve; two classes joined; trivial id; chain `d1→p1→p2`; a dissolved class joins another), then random cases against a naive replay;
  - `DisplacedJournalTests`, `LayeredUnionFindTests`, `FlattenedUnionFindTests` (8× dense boundary), `UnionFindCompressionTests`.
- `Tables/DisplacedTableFreezeTests`, `Tables/OverlayDisplacedTableTests`, `Tables/OverlayCanonicalizerTests`, `Actions/OverlayDisplacedLookupTests`.
- `Contexts/ScanMergesTests`: the G1(a) winner is present; no local ids; dissolved classes absent; nested `Find` allowed.
- `Contexts/UnionFindContextDifferentialTests`, with `ReferenceUnionFindContext` and `UnionFindContextPair`:
  - 400 seeds (`EGGNOG_CONTEXT_SEEDS`, `EGGNOG_CONTEXT_SOAK`); `LongProgramsAgree` (40 × 600);
  - checks: exact canonicals, `ScanMerges`, `IsLocalId`, journal completeness, own displacements, invariants;
  - second oracle: the spike (`Spike.Store`), compared modulo renaming.
- `Contexts/UnionFindInvariantCheckerTests`: six deliberate corruptions.
- `Contexts/UnionFindConcurrencyTests.StressFrozenRoot`: 200k ids; 1000 children in parallel under the union-find-only freeze; the root array is bit-identical afterwards.
- `Contexts/ContextLifecycleTests.FrozenParentDomains`: children of a frozen context get their own domains; freezing reassigns live subtrees.

**Done when:** the baseline holds, the union-find differential is green at 400 seeds plus a 10⁵-step soak against the spike, and the existing `UnionFind/*`, `CanonicalizerTests`, `Database/CongruenceTests` and `Differential/*` suites are unchanged and green.

### M9.3: Layered tables (layers, hidden sets, lookups, merges, storage freeze)

**Content:**
- `LayeredSchema`, `ContextTables` (core, lookup, merge hook, views, freeze), `TableLayer`;
- layer creation and publication (X37): `TableCatalog.Publish`/`GetForStaging`/`Resolver`, the layer lock, deferred adoption in `TakeRound`, `InSearch`;
- `HiddenSet` and the filter family, with sealing in the `MergeAll`/`MergeTable` epilogue (X40); `TableView` lookup half and `LayerView`;
- `SortedWritesTable.Layered` with canonicalizing and stamping `InsertLayered` (X38); `ExecutionState.Layers` with the full call-site table of §6.5; `LayeredKey*Lookup`;
- decision 2 check; `RetractionGuard` (decision 1 plus R1, guard snapshot); contradiction flag and `MergeAll` stop;
- `Context.Write`, `ClearTable`, `GetView`, `TryGetRow`, `IsContradictory`;
- the storage part of freeze: `Table.Freeze` (rebuild index only with rebuild columns), `Database.Freeze`, `ContextTables.Freeze`, `FreezeRoot`, X43.

**Interim:** `Rebuild` still runs only the union-find steps plus `MergeAll`, and throws `NotSupportedException("Row catch-up arrives in M9.4.")` when `NeedsCatchUp`. Random layered states are built by writing each ancestor before creating its children and freezing it, with no unions below the root.

**Tests** (`Layers/` unless noted):
- `HiddenSetTests`: 1000 property sequences against `SortedSet<int>`; conversion; `Union`; reopening a sealed set.
- `HiddenFilterTests`.
- `HiddenSealingTests`: R1-1's counterexamples. `f(Id) → i64` with `MaxMerge` and a ts column; root `f(a)=1`, frozen; child writes `f(a)=5`; a scan of `GetView(f)` yields only `f(a)=5`, and a rule over `f` derives `g(a,5)` only. Delete variant: after `StageRemove(f, [a])`, `TryGetRow(f, [a])` is absent. Plus `UnsealedCount == 0` after every `Write`.
- `TableViewTests`: the C4 cases plus **`LookupPathsAgree`**.
- `LayeredMergeTests`: new key; ancestor key with true and false merges; a three-row fold; `UnionIdMerge` staging into the child; non-lattice throws at first write, not at a hitting lookup; deletes; same-stratum read throws.
- `CanonicalWriteTests` (child part): a write naming a stale id lands on the canonical key and merges with the visible row; a raw write with ts 0 is stored at the merge's epoch.
- `LayerCreationConcurrencyTests`: 8 threads, each with clones of one state (as `ActionBatchWork` makes them), staging into the same untouched tables; exactly one layer per table, every row merged, the catalog never torn, `ViewGeneration` unchanged until the merge.
- `RetractionTests`: every cell of §6.7; a root state without descendants has no guard.
- `ContradictionTests`: storage level, with and without the option.
- `Contexts/ChildSchemaTests`: the §3.4 table for members that need no planning (`GetTableSpec`, `GetTableName`, `GetIdColumns`, `EstimateSize`, `NewBuffer`, `ClearTable` routing, definitions throw).
- `Contexts/FreezeTests` (storage part): freezing a store with the nullary contradiction table and a value-only table; `AddTable` after a freeze, then views of the new table from concurrent children of a frozen child (X43).
- `Actions/LayeredLookupTests`.

**Done when:** the baseline holds and `LookupPathsAgree` is green on 2000 random layered states.

### M9.4: Child rebuild, catch-up, settle and dispose (the M9 gate)

**Content:**
- `ContextTables.CatchUpRows` (with `SemiNaiveFloor`), `RebuildPass` (steps 3–5 through `RunWithTakenOut`, the layered incremental override), `AncestorCursor`, `settled`, `CommitCursors`, `NeedsCatchUp`;
- `SharedRebuildIndex`, `ForceRehash`, the compaction gate, `ColumnIndexGatherSink.AddValue`;
- `Context.Rebuild` in full (root and child conditions, X42); `IsClean`; `ChangeStamp`;
- `Context.Settle`, `SortedWritesTable.SettleAppends`, `SettleTableOperation` (X38);
- container interim (dirty log through `RunOnTables`, eq guard, G-a);
- `Context.Freeze` in full; `Dispose` in full; faulting;
- `ScanOwn`, `ScanAncestorsMentioning`; `Context.CheckInvariants`.

**Unit and targeted tests:**
- `Layers/ChildRebuildTests`: losers only; epochs; congruence through C4; `AncestorRowsExamined` equals uses; rows-since-mark; **own rewrites are merged** (R2-3: C unions 3 into 1, `f(3)` becomes `f(1)` and C is clean and canonical); the incremental path on a 5k-row layer examines only the rows mentioning the loser.
- `Layers/CatchUpTests`: one per C8 branch, plus (c′) against the defined floor (a root row stamped below a child's `NoteRuleRun` epoch is re-stamped; one at or above it is not).
- `Layers/CanonicalWriteTests` (rest): after `Union(x, y)`, `Rebuild()`, a `Write` of `f(y)` merges into `f(x)` with no rebuild and `CheckInvariants` passes (R2-4).
- `Contexts/RootSettleTests`: a raw root write of a stale id, then `CreateChild`, gives a canonical child view; a stale root append after creation is copied and hidden by catch-up (a); `Freeze` settles; a root program that creates no child never settles (its timestamp sequence is unchanged).
- `Layers/CompactionGateTests`.
- `Contexts/FreezeTests` (full): frozen finds do not write (8 threads × 10⁵).
- `Contexts/DisposeTests`.
- `Layers/LiftReadTests`: includes the **G1(b)** example; tables without rebuild columns return nothing.
- `Contexts/ContainerContextTests`: storage part.
- `Layers/CheckInvariantsCatchesViolations`.

**Gate tests:**
- `Contexts/ContextDifferentialTests`:
  - the spike's `DifferentialTests`, `Harness/Pair` and `Reference/` ported to `Contexts/Spike/` as `SpikePair`, `SpikeTestSchema`, `SpikeReferenceContext` (with ordered `SpikeReferenceOp` including `Delete`/`Clear`), `SpikeReferenceView` and `SpikeContextFacade` (X32);
  - the facade canonicalizes Id cells through `Context.Find` before every write, as the spike's `Insert`/`GetOrAdd` do (`Eggnog.Storage/Context.cs` `Canonicalize`);
  - 400 seeds; 3 deep; changing ancestors; random freezes; dispose; retractions only where §6.7 allows, asserting throws elsewhere;
  - views modulo renaming, lookups, column scans, `Scan(since)`;
  - `CheckInvariants` after every `Rebuild`; `LookupPathsAgree` after every write.
- `Contexts/ContextCopyTests` (*context = copy*): replay the chain into a fresh `Store` with explicit ids; exact canonicals; **C's journal equals the copy's displaced set minus the ids displaced in P's view when C was created** (the harness records that set at `CreateChild`); row sets modulo renaming.
- `Contexts/SpikeLockstepTests`: 5000-step programs, no deletes.
- `Contexts/ConcurrentChildrenStorageTests`: 16 children of a frozen root on 8 threads.
- `Contexts/PerChildCostTests` (`[Trait("Category","Performance")]`): `AncestorRowsExamined`, `RowsCopied` and `CatchUpRowsExamined` identical for 20k- and 200k-row roots; allocation ratio ≤ 1.5×.

**Benchmarks** (`Contexts/`): `PerChildCostBenchmarks` (storage half: 100 unions plus rebuild, roots of 20k, 200k and 2M rows), `ManyChildrenBenchmarks` (1000 single-union children; spike ≈ 100 ms; reports bytes per empty layer), `ChildFindBenchmarks`.

**Done when:** the baseline holds; every gate test is green; per-child counters are flat and time and bytes are flat within 1.5× from 20k to 2M (L6, B4).

### M10.1: Visibility in scans, subsets and indexes

**Content:** §9.2–§9.3, `RowOps`, the `HiddenSet` M12 accessors.

**Tests:**
- T1 `Offsets/LayeredSubsetTests` (2000 seeds against per-layer `SortedSet<int>`).
- T2 `Tables/HiddenScanTests`: every constraint kind; chunk sizes 1, 7, 64 and ∞; with `HiddenRows.None`, results bit-identical to the old overloads.
- T3 `Indexes/HiddenIndexBuildTests`: catalog refresh stays hidden-agnostic.

**Done when:** the baseline holds (gate classes per M9.0).

### M10.2: Layered query execution

**Content:**
- §9.1, §9.4–§9.10: `ContextView`, `ViewSnapshot`, `Database.View`, the layered schema and planning members (§3.4), `ConstraintProcessor`, the evaluators, `PlanTemplate.InstantiateLayered`/`PlanInstance`;
- the trie, prober and frame changes and the kernels; existence checks; planner statistics;
- Id constant substitution for query and action constants at every Store-backed level (`QueryEntry.IdConst`, `ActionIdConstant`, `ActionSubstituter`); `PlanIndexKeys`, `FreezeWarmup.From`; `ExecutorInvariants`; `ExecutorCounters`;
- the `$uf` atom guard.

**Tests:**
- T4 `Execution/LayeredTrieNodeTests`.
- T5 `Execution/LayeredProberTests`.
- T6 `Planning/LayeredHeaderTests`.
- T7 `Query/ConstantSubstitutionTests`: query and action constants; a Store-backed root canonicalizes a constant that lost a union; a bare `Database` runs it as written; `BuildCachedPlan` keeps the written plan.
- T8 `Execution/LayeredExecutorPropertyTests`:
  - `SyntheticLayers : ILayerViewSource` plus `LayerFlattener`;
  - 1500 small and 200 large seeds; every mode, strategy and decomposition setting;
  - an S6-off variant.
- T9 `Differential/DiffLayeredSemiNaiveStep`.
- T11 executor checker tests.
- T12 `Execution/LayeredAllocationProbeTests`: no allocation per row, probe or frame in a warm child run.
- `Query/ChildPlanningTests` (R2-1): `NewRule`, `AddAtom`, `Build` and `PlanQuery` in a child that never wrote the rule's tables; `SymbolMap` names; statistics from the child's view; `$uf` atoms throw.
- `Execution/LayeredParallelRunTests` (R1-2, R2-2): a child with `ParallelRuleSetsOverride = true` runs rules whose actions write untouched tables and look up untouched tables; equal to the serial run; a hitting `LookupOrInsertDefault` creates no layer; `ViewGeneration` unchanged during the search.

**Done when:** the baseline holds; `ExecutorAllocationProbeTests` is unchanged; with `EGGNOG_COUNTERS`, ancestor-layer scanned rows and dynamic-index rows are 0 once `W ≥` every root stamp.

### M10.3: Per-context rules in the bridge, including concurrent children

**Content:** §10 in full, on the M8 port: `EGraph`/`EGraphContext`, `RuleTable`, `RuleProgress`, watermarks and inheritance with `ActionReadAnalysis` and `ExternalFunction.ReadsTables`, `RunRules`/`EnsureClean`/`Saturate`/`FlushUpdates`/`HostWrites`/reads, early stop, panics per context, `IterationReport` fields, `ContextLocalSlot`, bridge freeze, container registration and G-a, `ParallelismBudget`, `ConcurrentChildren`, `EGraphContext.CheckInvariants`.

**Unit tests** (`Bridge/`):
- `EpochTests`: `RootTimestampSequenceMatchesEgglog`; `RootStagedWritesMergeInRun` (a row staged through `WithExecutionState` and not flushed merges in the run at T; a `LookupId` between the staging and the run does not throw).
- `WatermarkSeedingTests`: the P-never-ran case (`min(W_G, c_P)`), a K mismatch, `RuleId` reuse, non-inheriting rules, and zero-atom rules.
- `ActionReadAnalysisTests`: each instruction kind; constructors versus lattice functions; externals with and without `ReadsTables`.
- `EGraphContextTests`: isolation, per-context panics, `VersionIgnoresNoOps`, locals, `IsLocalId` covers term mints and not container mints, the context API canonicalizes host ids at the root.
- `ContextRebuildTests`: the run's first pass reuses T; flush rebuilds at the next epoch; containers are never rebuilt in a child.
- `FreezeTests` (bridge), `ContainerContextTests` (bridge), `ContradictionTests` (bridge).
- `RuleTableTests`, `ParallelismBudgetTests`, `ConcurrentChildrenTests` (budget cap, unwrapped exceptions, two threads on one domain throw).

**Differential tests** (`Colors/`). Program families: **monotone** (no `AssertNe`/`AssertAnyNe` on Id columns, no primitives that depend on id order, no deletes, no table reads in actions) and **history** (all of those allowed, deletes only where §6.7 allows).
- `ReferenceRuleContext` (with `TermNamer`) is operational: it extends `SpikeReferenceContext` with rule checkpoints. At each rule run in context X it computes X's reference view from the ordered ops of X's chain, runs `NaiveRuleRunner` with the engine's iteration schedule (one Jacobi iteration then closure, repeated as `Saturate` does), and appends the newly derived facts to X's ordered ops as `Derived` assertions. Descendants inherit them like any assertion, so facts derived in an ancestor under fewer equalities, or before a child's delete, persist as they do in the engine.
- `ContextRuleDifferentialTests`: both families against `ReferenceRuleContext`; family A (no minting) compared modulo renaming, family B (constructor minting) compared by term names; 400 seeds.
- `ContextRuleCopyTests` (monotone family only): a child's view after saturation equals a fresh root replaying the chain's assertions and running the same rules; random conjunctive queries agree; includes **`ConstantLosesRootUnionBeforeChildRuns`** (R1-3: rule `R(x, 7) → S(x)`, root `R(1,3)` and `union(7,3)`, root run, freeze; a child that asserts nothing has the same view as its parent).
- `SeededWatermarkDifferentialTests` (both families): `InheritWatermarks = false` gives equal views at every checkpoint, and seeded match totals are ≤ unseeded; includes R1-4's counterexample (`(set (g x) (f x))` with `f` raised in the child) as a regression.
- `IdConstantContextTests`.
- `ConcurrentChildRulesTests`: 16 children on 8 threads equal serial replays.
- `EggPushPopTests`: once a `.egg` reader exists, for programs whose rules all have `AllowInheritance` (egglog's push copies `last_run_at`, which then coincides with inheritance); other programs are reported, not compared. The M10 gate per map §6.

**Done when:** the baseline holds; every differential is green at 400 seeds; I31 is exercised by the seeded-versus-unseeded property over both families (map §8.2 risk retired); `RestampedCopies == 0` throughout.

### M10.4: Lift primitives and split hooks

**Content:** §11 in full, plus `EGraphContext.Lift.cs` (`ScanOwn`, `ScanMerges`, `IsLocalId` and `ScanAncestorsMentioning` by `FunctionId`).

**Tests** (`Cases/`):
- `MemberMapTests`, `ClassPartitionTests`.
- `LifterTests`: **`G1bFactThroughWinnerIsLifted`**, boolean split, three-case meet, no aliasing with one live case, vacuous cases dropped, all-vacuous contradicts, lattice case-join, bottom-up term lift, limits, memo, Id-output functions, not subsumed.
- `LifterPropertyTests`: soundness ⊆ E, completeness ⊇ E; 400 seeds.
- `SplitDriverTests`: lift-before-deepening order, demand filter, ancestor-covered keys, limits, disposal order, stopped cases disposed.

**Benchmarks** (`Contexts/`):
- `CaseSplitMemoryBenchmarks` (L6, B5): bytes per structure, against `Database.Clone()`, the in-house egglog push baseline.
- `CopyVolumeBenchmarks` (G3): histogram of rows copied per union.
- `LateParentMintBenchmarks` (L7, G2): `ParentIdLosers`, copies per lift round, and the root's id-array growth from reserved blocks (§4.2).
- `PerChildCostBenchmarks` (full workload: 100 merges plus one rule run).

**Done when:** the baseline holds, the property tests are green, and the L6 and L7 reports are produced and attached to the M10 PR.

---

## 18. Traceability

### 18.1 Map decisions C1–C14

| Decision | Where satisfied |
|---|---|
| C1 ids and clock, captured epochs | §4.1–4.2, X3, X4, X5, I1–I3 |
| C2 union-find layering, compression, re-keying | §5; X6, X7; I4–I9. The alternating chase is replaced by eager re-keying (§5.4). Compression at merge and freeze (§5.1, §8.1). |
| C3 layers, hidden sets, losers only, visibility everywhere | §6.2–6.4, §9.2–9.5, X40, I12–I15, I18, I26 |
| C4 child lookups and merges, bound `ExecutionState` | §6.5–6.6, X1, X2, X38 |
| C5 incremental child rebuild through ancestors' rebuild indexes | §7.3, X9, `SharedRebuildIndex` |
| C6 per-layer catalogs, never reset when frozen, per-piece index choice | §9.5–9.6, §8.1, I28 |
| C7 per-(rule, context) watermarks, seeding, re-stamping | §10.2, §7.2 (c′ with the defined floor), X41, I19–I21, I31 |
| C8 cursor catch-up, order of steps | §7.1–7.3 (union-find narrowed to one cursor, X7) |
| C9 freeze contract, ownership, concurrency, parallelism budget | §8, §10.9, X13–X15, X37, X43, I23–I24, I34 |
| C10 deletion, subsumption, clear, context-private tables | §6.7 (plus R1, X28), I22 |
| C11 lattice merges where children write | §6.2 (`GetOrCreateLayer` check, decision 2) |
| C12 compaction trigger and gate | §6.8 (stricter, X27), I16 |
| C13 containers | §7.4, §10.7 (interim: base containers global; eq refused in children), X31 |
| C14 light children | §6.2 (lazy single-shard layers, created without pre-allocation, X37), §5.2 (lazy union-find), §14.2 |

### 18.2 Decisions 1–12

| # | Where satisfied |
|---|---|
| 1 Deletion across contexts | §6.7, I22; refined by R1 (§19) |
| 2 Non-lattice merges root-only | §6.2 check at layer creation, including copy-on-touch; reads create no layer (§6.5) |
| 3 Losers only | §7.3 step 4 (own losers), I13 |
| 4 Determinism | Serial child runs are deterministic (ascending table and row order; sorted gathers; ordered `ScanMerges`). Parallel runs and concurrent siblings differ only in id choice (blocks); tests compare modulo renaming. |
| 5 Fresh-id scope | Unchanged: the predicted cache lives per `ExecutionState`; child states behave the same |
| 6 Match-count parity | Not required; layered multiplicity (I27) |
| 7 Plan parity | Golden plans unaffected; per-context replanning not done (§9.7) |
| 8 Dynamic reordering when compiled | §9.9: per-piece kernels; the interpreter keeps the outer loops |
| 9 Containers in children | §10.7 G-a; §7.4 G-b, G-c |
| 10 No rule-based rebuild | §7.3 native path only; X23 |
| 11 `IterationReport.Changed` | §10.3 (`RebuildChanged` separate) |
| 12 Primitives and capability contexts | Unaffected (M8); external functions shared copy-on-write (§4.3); `ReadsTables` added (§10.2) |

### 18.3 Literature requirements L1–L7

| # | Where satisfied |
|---|---|
| L1 winner probe | §6.10 `ScanAncestorsMentioning`; §11.2 step (b); I33; `G1bFactThroughWinnerIsLifted` |
| L2 member lists, `ScanMerges` from them | §5.2, §5.7, §11.1, I32 |
| L3 invariant checker | §12.7 (coverage: overlay keys canonical once caught up (I6); coarsening (I7); hides have reasons (I14); local ids absent (I10); cursors valid (I9, I17)) |
| L4 stop flag | §6.9, §10.6, §11.2 (`CaseSet` disposal), I25 |
| L5 demand-driven splitting, lift before deepening | §11.3 |
| L6 benchmarks | §17 M9.4 (`PerChildCost`, flat 20k–2M), M10.4 (`CaseSplitMemory` against clone, `CopyVolume`) |
| L7 measure smaller-id-wins | `UnionFindStatistics.ParentIdLosers` (§5.6); `LateParentMintBenchmarks` |

Review corrections honoured:
- 12 (B1 not adopted);
- 13 (B3 deferred as a probe-efficiency measure);
- 15 (G1(a) implemented from the overlay);
- 18 ("once catch-up is done").

B6 is in the split driver; B7 in the overlay.

---

## 19. Points for the decisions owner, and deferred work

1. **R1** (X28): a child's point delete or clear of a shared table requires a frozen parent. This adds to decision 1. The alternative is retracted keys plus a snapshot of the union-find at delete time; it is not specified here.
2. **C12 made stricter** (X27): a layer never compacts while descendants live. `remap(old, new)` remains the upgrade path.
3. **Rule-layer D18 guidance for child scopes:** a child that throws is faulted and must be disposed (X12). Re-running after `Rebuild()` remains valid at the root.
4. **The root deviates from egglog** only:
   - on the panic path (X18): progress is not committed after a recorded failure;
   - for Id constants that have lost a union (X19): a Store-backed root matches and writes the class's canonical id. Proposed addition to map §8.1 as row 22: "Cached plans bake Id constants; after a constant loses a union the rule silently stops matching (`eb/lib.rs:1397-1420`, `cr/query.rs` constant entries). Eggnog: canonicalize every Id constant per run in the running context."
   - in operations egglog lacks: `CreateChild` and `Freeze` settle the rows written since the last rebuild (§7.6).
5. **Inheritance is narrower than egglog's push** (X41): rules whose actions read tables never inherit a watermark, whereas egglog's push copies `last_run_at` for every rule. egglog push/pop is therefore an oracle only for programs whose rules all inherit.
6. **Host writes at the root** (X38): the egglog-compatible `EGraph` API writes as given; the `EGraphContext` API canonicalizes Id cells at every level, which is what the rule layer's invariant C assumes.
7. **G2 ranking** (parent-visible ids first) is deferred until the L7 report exists.
8. Deferred:
   - **B1** pruning;
   - **B3** flattening of hidden sets;
   - **G5** memory budget per `Run`;
   - **G8** thaw;
   - **G9** shared sibling ids;
   - eq containers in children (C13 after M10);
   - per-context replanning (§8.3 #13);
   - deduplicating driver keys across pieces (an optimization for I27's multiplicity; measure first).
9. **The C7 safeguard (c′)** stays as defence in depth. Once I21 is proven over M10 workloads, turn a nonzero `RestampedCopies` into a `Debug.Assert`.

---

## 20. Review responses

Two reviews were answered. Every finding was accepted. One fix uses the second of the options a reviewer offered, for the reason given.

**First review**

| # | Severity | Finding | Resolution |
|---|---|---|---|
| R1-1 | critical | Hides added by a merge that does not grow the union-find were never sealed; reads used sealed state only | Sealing in every layered `MergeAll`/`MergeTable` epilogue, plus `Rebuild`, `ClearTable`, `Freeze`; sealed inputs required by `Union` and `HiddenRows`; DEBUG assertion in `ThrowIfNotReadable`; `LookupPathsAgree` after every write. X40, §6.3, §7.5, I18, `HiddenSealingTests`. |
| R1-2 | major | Lazy layer creation from parallel action clones lost writes and changed `ViewGeneration` mid-run | Lock-protected, copy-on-write creation that never touches views; deferred adoption into the table map; rule-set view snapshot; lookups create no layer. The reviewer's preferred pre-creation was not adopted as the main mechanism: it allocates an empty layer for every writable table in every case child (C14). X37, X21, §6.2, I29, I34. |
| R1-3 | major | Root ran Id constants as written, children canonicalized | One semantics at every Store-backed level, for query and action constants; proposed §8.1 row; `ConstantLosesRootUnionBeforeChildRuns`. X19, §9.8, I30, §19. |
| R1-4 | major | Inherited watermarks ignored view-reading actions; two gates conflicted | `AllowInheritance` from `ActionReadAnalysis`; oracle governance stated per program family. X41, §10.2, I31, §17 M10.3. |
| R1-5 | minor | Fresh-root replay is not an oracle for non-monotone bodies or deletes | Operational `ReferenceRuleContext` with derived-fact checkpoints; the fresh-root copy property restricted to the monotone family. X41, §17 M10.3. |
| R1-6 | minor | `semiNaiveFloor` undefined; I19/I21 relied on callers stamping | Floor defined as `max(CreatedAt, largest NoteRuleRun epoch)`, per context; children stamp at the merge; DEBUG assertion at the root. §7.2, X38, I19, I21. |
| R1-7 | minor | Frozen contexts' arrays grown by child threads after `AddTable` | Frozen contexts answer functionally beyond their arrays. X43, §6.3, I23. |
| R1-8 | minor | Journal-equals-copy gate wrong when the parent unions before `CreateChild` | The gate subtracts the ids displaced in P's view at C's creation. §17 M9.4. |

**Second review**

| # | Severity | Finding | Resolution |
|---|---|---|---|
| R2-1 | major | A child `Database` could not answer schema or planning questions | Layered member table; call sites switched. X39, §3.4, §9.1, `ChildPlanningTests`. |
| R2-2 | major | Lazy layer creation during parallel search; `LookupOrInsertDefault` created layers on reads | As R1-2; buffer at first miss in a child. X37, §6.5. |
| R2-3 | major | Step 3 staged without notifying, so rewrites were never merged | Step 3 and the container refresh run through `RunWithTakenOut`/`RunOnTables`, which notify. X9, §7.3, §7.4. |
| R2-4 | major | Writes with stale ids left a clean but non-canonical child | Children canonicalize and stamp at the merge; the root settles before it is shared; catch-up (a) covers later root appends. X38, §6.6, §7.6, I13, I35. |
| R2-5 | major | `Freeze` built a rebuild index for tables without rebuild columns | Skipped; `SharedRebuildIndex` nullable; `ScanAncestorsMentioning` returns at once. §8.1, §6.10, `FreezeTests`. |
| R2-6 | major | Milestones depended on later milestones | Interim behaviour stated per milestone; frozen-domain tests moved to M9.2; storage freeze moved to M9.3. §17. |
| R2-7 | major | The ≤2% gates had no benchmarks behind them | M9.0 baseline set and gate map. X33, §17. |
| R2-8 | minor | `semiNaiveFloor` undefined | As R1-6. |
| R2-9 | minor | Root `EnsureClean` and reads broke egglog parity with pending writes | Root cleanliness ignores pending notifications; `EnsureClean` rebuilds the root only when its journal mark is behind. X42, §7.5, §10.3, §10.5. |
| R2-10 | minor | Root cost claims overstated | §1 and §14.1 list every per-call, per-batch, per-probe and per-refinement cost; the root guard is null without descendants. §6.7, §14.1. |
| R2-11 | minor | Action wiring incomplete | `ReadTable` is the single dispatch point; every call site listed; `InChildContext`, `VisibleRowCount`, `TableIds`, `Containers` defined; one `CreateLayer` signature. §6.5, §6.2. |
| R2-12 | minor | Step 3 took the full path below 10 000 rows | Layered incremental override. §7.3, §14.2. |
| R2-13 | minor | Frozen views cache grown concurrently | As R1-7. |
| R2-14 | minor | Id blocks inflate root arrays; overflow bound wrong | First blocks of 16; `UnionFind.MaxId` cap; memory note; measured in `LateParentMintBenchmarks`. §4.2. |
| R2-15 | minor | Test list and naming clashes | `Instantiate` overload kept; affected tests listed; `Spike` alias and `Spike`-prefixed ported types. X32, §9.4, §15. |
