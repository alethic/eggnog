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

**Implementation.** M9.0–M10.4 are implemented. §21 records, per milestone, where the code departs from this text and why, which departures were corrected in the text above, and what is still deferred.

---

## 1. The design in brief

1. **Each context owns one `Database`.**
   - The root's `Database` is egglog's, unchanged.
   - A child's `Database` has its own catalog, notification list, pending queues, merge, index catalogs and size estimate.
   - It shares the store-wide `CounterSet`, `ExternalFunctionRegistry`, `DependencyGraph`, `ContainerValues` and base-value registry (`BaseValues`) by reference.
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
| X21 | **View source and invalidation** (revised). QE: polls `GetViewVersion(d)`. TB: `InvalidateViews`. | Storage builds and caches `TableView`s (`ContextTables.GetView`). Only the `MergeAll` epilogue, `Rebuild`, `ClearTable` and `Freeze` invalidate views and bump `ViewGeneration`; layer creation never does (X37). `RuleSetBuilder.Build` snapshots the views of every table its rules read (`ViewSnapshot`); the builder records `ViewGeneration` and the chain's change stamp when its first header is evaluated, and a child's `RunRuleSet` refuses the rule set when either moved (§9.1). QE's `ILayerSource` shrinks to `ILayerViewSource` (§9.1). | One owner of view state; the search never reads the cache, and synthetic layers stay testable. |
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
| X37 | **Layer creation off the domain thread** (R1-2, R2-2). Lazy creation from `ExecutionState.CreateBuffer` ran on rule tasks and action-batch clones (`Execution/ActionBuffer.cs`), mutated `DenseIdMap`s without a lock, and bumped `ViewGeneration` mid-run. Reviewers offered (a) pre-creating every write-set layer before the search, or (b) lock-protected copy-on-write creation that leaves views alone. | (b). `GetOrCreateLayer` double-checks under `ContextTables.layerLock`, publishes the layer to the catalog with `TableCatalog.Publish` (a new map published with `Volatile.Write`) and to the layer slots, and changes no view and no `ViewGeneration`. During a search (`ContextTables.InSearch`) the layer's `TableInfo` waits in a deferred list, and the next `TakeRound` adopts it into `Database.tables` on the domain thread. `RunLookupOrInsertDefault` creates its buffer at the first miss in a child, so a lookup that hits creates nothing. | An empty layer adds no visible row and hides nothing, so a view taken before it exists stays correct for the rest of the run. (a) would allocate an empty layer (estimated at 1–2 KB, measured at about 2.7–3.0 KB in M9.4, §14.2: hash table, two row buffers, pending queues, `TableInfo`) for every table any rule of a set could write, in every case child, against C14. |
| X38 | **Canonical and stamped writes** (R2-4, R1-6). Incoming rows were not canonicalized, a clean child could hold stale ids, and I19/I21 relied on callers stamping the sort cell. | **Children:** `InsertLayered` canonicalizes every incoming row's rebuild columns through `Owner.UnionFind.Find` and sets its sort cell to `state.Timestamp` before it probes (child-only code; zero root cost). **Root:** egglog's semantics for raw writes (written as given, rewritten by the next rebuild). Before the root is shared, `Context.Settle()` canonicalizes the rows written since its last rebuild (§7.6), and a non-frozen root's later stale appends are copied and hidden by each child's catch-up branch (a). DEBUG builds assert stamps in `Context.Write` at the root. | Children stay canonical whenever clean, at the cost of one `find` per incoming id cell. The root keeps egglog's numbering and contents for every program that creates no child. |
| X39 | **Schema and planning in a child** (R2-1). Rule building and planning read `Database.tables`, which in a child holds only own layers and `$uf`. | A layered `Database` answers schema questions (`GetTableSpec`, `GetTableName`, `GetIdColumns`, `IQuerySchema`) from `LayeredSchema`, and statistics, header evaluation and size estimates from its `ContextView`. `RuleBuilder.GetSpec`, `RuleQueryBuilder.AddAtom`, `SymbolMap.Create`, `RuleSetBuilder` and `PlanQuery` switch to these members. The full member list is in §3.4. | Rules are then built and planned in any context with that context's statistics (§9.7). |
| X40 | **Unsealed hides** (R1-1, critical). New hides went into a set's unsorted tail, readers used the sealed state only, and only `Rebuild` sealed. | Every layered `MergeAll` and `MergeTable` seals, in its `finally` epilogue, every set that gained a hide since the last seal (`ContextTables.AfterMerge`). `Rebuild`, `ClearTable` and `Freeze` seal too. A context is never readable with an unsealed set (I18), which `ThrowIfNotReadable` asserts in DEBUG. `HiddenSet.Union` and `HiddenRows` require sealed inputs. | Hides are added only inside `MergeAll` (`InsertLayered`, `DeleteLayered`) or right before the `MergeAll` of the same pass (catch-up, step 4), so the epilogue covers every path. |
| X41 | **Which rules may inherit, and which oracle governs** (R1-4, R1-5). | `RuleInfo.AllowInheritance` is computed from the rule's compiled actions (`ActionReadAnalysis`): false when any instruction reads a table whose contents can change (`Lookup`, `LookupWithDefault`, `LookupWithFallback`, `LookupOrInsertDefault` on a table whose merge is not `UnionIdMerge`, or an external function flagged `ReadsTables`), and for naive, split and callback rules. Oracles: seeded == unseeded governs every rule in programs whose retractions touch only tables no rule writes; the operational `ReferenceRuleContext` governs every rule; the fresh-root copy property governs monotone programs only; egglog push/pop governs programs whose rules all inherit. | I31's precondition "actions are query-determined" is then enforced, not assumed. A fresh-root replay is not an oracle for non-monotone bodies or for deletes, because facts derived earlier in an ancestor persist (correct operational semantics). Under retraction, inheritance gives egglog's push semantics: a child that deletes a fact an ancestor's run derived, or hides a constructor row that a lookup-or-insert read, does not re-derive it, whereas an unseeded child does; the two schedules then differ by design (§21.9). |
| X42 | **Root cleanliness and egglog parity** (R2 minor). `EnsureClean` rebuilt the root whenever writes were staged, shifting every timestamp, and root reads threw on pending writes. | At the root, `IsClean` ignores pending notifications (staged writes are invisible until merged, as in egglog), and the bridge's `EnsureClean` rebuilds the root only when its journal mark is behind. `Context.Rebuild()` at the root runs passes when the journal mark is behind or writes are pending. | Root host writes staged through `WithExecutionState` merge inside the run's `MergeAll` at T, as in egglog, also when every rule of the run was pruned: egglog's `run_rule_set` then skips its `merge_all` (map §8.1 row 26), and the bridge merges at T itself (§10.3). |
| X43 | **Frozen contexts and tables added later** (R1-7, R2 minor). | A frozen context never grows or writes its arrays. For a table id beyond them, `GetView` returns the parent's view (or `RootView`) without caching, and `TryGetLayer` returns `null`. `Store.AddTable` publishes the new table's `RootView`. | A frozen context has no layer and no hides for a table added after its freeze, so its view of that table is its parent's. No shared array is resized by concurrent readers. |

---

## 3. Objects and the interfaces between areas

### 3.1 Ownership

| Object (file) | Owns | Shares by reference |
|---|---|---|
| `Store` (`Contexts/Store.cs`) | The root `Context`, `LayeredSchema`, `UnionFindTable` (`TableId 0`), `IdCounter` (`CounterId 0`), `ClockCounter` (`CounterId 1`), `ContainerDirtyLog`, `DefinitionLock`, and the "has had children" flag | — |
| Root `Context` | The root `Database` (an egglog `Database`; `$uf` is `DisplacedTable`), its `LayeredUnionFind`, `Gate`, `Domain` and children | — |
| Child `Context` | Its `Database` (catalog, notifications, pending queues, own-layer `TableInfo`s, `ChangeStamp`, idle `JoinRun`, `ContextView`), its `ContextTables`, its `OverlayDisplacedTable` (`UnionFindOverlay` plus `DisplacedJournal`), `IdBlockAllocator`, union-find cursor, semi-naive floor, `Gate`, `Domain`, and, once frozen, a `FlattenedUnionFind` | `CounterSet`, `ExternalFunctionRegistry`, `DependencyGraph`, `ContainerValues` and `BaseValues` (root's); frozen ancestors' layers, overlays, journals and indexes (read-only); plan templates |
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
    internal bool IsViewCurrent { get; }                  // §7.5, §9.1
    internal long ViewStamp { get; }                      // Σ Database.ChangeStamp over the context and its ancestors (§9.1)
    public object? Host { get; set; }                     // the bridge's EGraphContext; set once
    // Context.Lifecycle.cs (§7, §8)
    public Context CreateChild(ContextOptions? options = null);
    public bool Rebuild();
    public void Freeze(FreezeWarmup? warmup = null);
    public void Dispose();
    internal void PrepareForWrite();
    internal void Settle();                               // root only, §7.6
    // Context.Epochs.cs (§4.1)
    internal uint BeginEpoch();  internal void EndEpoch();  // BeginEpoch also records the latest epoch
    internal void UseEpoch(uint epoch);                   // a child's rule run at a caller-chosen T: refuses T < max(SemiNaiveFloor, latest epoch) (§3.4)
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
internal RetractionGuard? GuardForNewState();                    // the database's guard at every Store-backed database, root included; null for a bare one (§6.7)
public bool IsFrozen { get; }
internal void AdoptLayer(TableLayer layer);                      // tables.Insert(layer.Id, layer.Info); domain thread, outside a search (I34)
internal bool IsTakenOut(TableId table);                         // the layer is adopted and the table map does not hold it
internal long IncrementCounterInView(CounterId c);               // idBlocks is {} b && c == b.Counter ? b.Next() : counters.Increment(c)
internal long IncrementSharedCounter(CounterId c);               // never redirected
internal TableSpec GetTableSpec(TableId table);                  // X39
internal string? GetTableName(TableId table);                    // X39
```

**The child constructor:**
- copies the root's `counters`, `externalFunctions`, `deps`, `containers` (`root.Containers`) and `baseValues` (`root.BaseValues`, declared in `Bridge/Database.BaseValues.cs`) by reference; both properties create the root's instance if it has none, so a value the root boxed unboxes in the child and a registration through either is seen by both;
- builds `catalog = new TableCatalog { Resolver = id => layers!.ResolveForStaging(id) }`;
- places `TableInfo("$uf", unionFind, ParallelHeuristics.Serial)` at `TableId 0` (the `OverlayDisplacedTable` from M9.2 on; a placeholder `DisplacedTable` that is never written in M9.1, §17);
- sets `parallelism` to the C9 default (§10.9).

`Database.View` (§9.1) is assigned for children and created lazily at the root.

**Members of a layered (child) `Database` (X39).** Every member of `Database/Database.cs`, `Database/Database.Constraints.cs`, `Database/Database.Rebuild.cs`, `Containers/Database.Containers.cs` and `Query/Database.Query.cs` that reads the table map behaves as follows in a child; the root and a bare `Database` are unchanged, except for `AddTable` at a Store's root.

| Member | In a child |
|---|---|
| `GetTableSpec(t)` (new) | `LayeredSchema.GetSpec(t)`: `DisplacedTable.DisplacedSpec` for `$uf`, else `schema[t].RootInfo.Spec`. Root: `GetTableInfo(t).Spec`. |
| `GetTableName(t)` (new) | `LayeredSchema.GetName(t)`. Root: `GetTableInfo(t).Name`. |
| `GetIdColumns(t)` (`Query/Database.Query.cs:45`) | `schema[t]?.RebuildColumns`, empty for `$uf` |
| `IQuerySchema.GetArity` / `GetKeyCount` | `GetTableSpec(t)` (both levels) |
| `IQuerySchema.SplitConstraints` | `ConstraintProcessor.Split(schema[t].RootInfo, …)`: the split depends only on the table's kind and cacheable columns, which every layer shares; `$uf` throws `NotSupportedException` (X23) |
| `IQuerySchema.IsCacheable` (new default member, `true`) | From `GetTableSpec(t)` (both levels); `FreezeWarmup.From` reads it (§9.10) |
| `IPlanStatistics` | `View` (`ContextView`) |
| `IHeaderEvaluator.Evaluate` | Throws `InvalidOperationException`; every caller instantiates through `View` (`ILayeredHeaderEvaluator`, §9.4) |
| `ProcessConstraints(t, …)` and the instance `ColumnCardEstimator` | Throw `NotSupportedException` for every table (`Database.GetQueryTableInfo`): a child's rows lie in several layers, which only `View` evaluates; `$uf` with the X23 message |
| `PlanQuery(q)` | `QueryPlanner.PlanQuery(q, View)` |
| `EstimateSize(t, c)` | `View.EstimateSize(t, c)` (sum over present layers); `$uf` throws `NotSupportedException` (X23) |
| `GetTable`, `GetTableInfo`, `TryGetTableInfo` | The own-layer and `$uf` map only, for the database's own merge, rebuild and index work; `MissingTable` says "Table t has no layer in this context; read it through Context.GetView" |
| `NewBuffer(t)` | `catalog.GetForStaging(t).NewBuffer()`, then notify |
| `ClearTable(t)` | `Owner.ClearTable(t)` (§6.7) |
| `AddTable`, `AddCounter`, `AddExternalFunction`, `FreeExternalFunction`, `NextTableId` | Throw `InvalidOperationException("Definitions are added through the Store.")`. `AddTable` throws at a Store's root database too: a table added there would be unknown to the store's schema, so children would disagree with the root about it and the root's rebuild (`Schema.FunctionTables`) would skip it. The `Store` adds through the internal `AddTableCore`; a bare `Database` and a clone (no owner) keep egglog's `AddTable`. |
| `TableCount` | `LayeredSchema.TableCount` |
| `VisibleTableIds` (behind `DatabaseView.TableIds` and `ExecutionState.TableIds`) | `LayeredSchema.Ids` |
| `ApplyRebuild`, `RefreshRowsForValues`, `RebuildContainers`, `RebuildPass`, `Rebuild` (public) | Throw `InvalidOperationException`; a child rebuilds through `Context.Rebuild`, whose `ContextTables` calls the internal `RunWithTakenOut` and `RunOnTables` |
| `Clone` | Throws (§4.3) |
| `NewRuleSet`, `MergeAll`, `MergeTable`, `CreateExecutionState`, `WithExecutionState` | Unchanged in shape (layered behaviour per X2, X21, X37, X40); `MergeTable` also adopts deferred layers first (§6.2) |
| `RunRuleSet`, `ExecuteRuleSet`, `MergeRuleSet` | The run is the context's (§8.3, X12, X37). It enters the context's domain and refuses, with `InvalidOperationException`: a call from inside a callback of the domain (§8.3), views that are not current (§7.5), and a rule set whose view generation or chain stamp moved since its first header was evaluated (§9.1, I29). It refuses a timestamp below `max(SemiNaiveFloor, the context's latest epoch)` with `ArgumentOutOfRangeException` (`Context.UseEpoch`; I3, I19, I21). The search runs as a callback of the domain with `InSearch` set, so a nested `Write` or `Rebuild` from an action or external is refused. An escaping exception faults the context (X12). Every refusal happens before anything runs and does not fault. `MergeRuleSet` applies the domain, the timestamp floor and the fault rule only. |

Call sites switched to the new members: `Query/RuleBuilder.cs:389` (`GetSpec` → `owner.Database.GetTableSpec`), `Query/RuleQueryBuilder.cs:128` (`AddAtom` arity → `GetTableSpec(table).Arity`), `Query/RuleQueryBuilder.cs:190` (`GetIdColumns`, layered as above), `Query/SymbolMap.cs:58` (`GetTableName`), `Query/RuleSetBuilder.cs` (`EstimateSize`; `AddBuiltRule` and `GetRuleWithExtraConstraints` instantiate through `db.View`), and `Database/ColumnCardEstimator.cs` (a static per-`TableInfo` overload used by `ContextView`). `DatabaseView.GetTableName` forwards to `GetTableName`, and `DatabaseView.GetTable` throws `InvalidOperationException` in a child (a readonly field cached at construction; one field test per call at the root).

### 3.5 Who calls whom

| Caller | Callee | Contract |
|---|---|---|
| Executor (`JoinRun`, `RuleWork`) | The rule set's `ViewSnapshot` → `TableView` / `LayerView` / `HiddenRows` | Taken at `RuleSetBuilder.Build` while the context's views are current (§7.5). Views are immutable for the run; hidden sets are sealed (I18, I29). |
| Actions (`ExecutionState`) | Search phase: the state's `ViewSnapshot`, else `ContextTables.GetView(t)`; write phase: `ContextTables.TryGetVisibleRow` / `TryGetVisibleColumn` | C4. `layers` is resolved from `view.Owner.Layers`. |
| Staging (`ExecutionState.CreateBuffer`) | `TableCatalog.GetForStaging` → `ContextTables.ResolveForStaging` | Thread-safe; may create an empty layer (X37, I34). |
| Tables (`ContextTables`) | `Context.UnionFind.Find`, `Context.IsOwnDisplacement`, `OverlayDisplacedTable` (rebuilder table), journal rows `[journalMark, Count)` | The union-find is current before any row work (§7.1). |
| `Context.Rebuild` | `CatchUpUnionFind(e)` → `ContextTables.CatchUpRows(e)` → `ContextTables.RebuildPass(e)` | In this order, first pass only for the first two (§7.1). |
| Bridge (`EGraphContext`) | `Context.BeginEpoch/EndEpoch/NoteRuleRun/PrepareForWrite/Rebuild/ClearTable/Freeze/CreateChild/Dispose`; `Database.CreateEngineState` and `MergeAll` (host writes, §10.4; the raw escape, `WithExecutionState`, uses `CreateExecutionState`); `Database.ExecuteRuleSet` and `MergeRuleSet` at T (§10.3; only the internal raw iteration calls `RunRuleSet`) | The bridge flushes its own `HostWrites` up the chain before calling storage (§10.4). `EGraphContext.CreateChild` and `Dispose` (rule-layer E7) wrap §8.2's calls: a parent that is not frozen is flushed and made clean first, and a failure recorded meanwhile is taken at once (§10.1). |
| Extraction (`Extractor`, context form) | `ContextTables.GetView` → each present `LayerView`'s cached index and hidden-aware scan; `Context.UnionFind.Find`; `ExtractionWatch` over the table and union-find versions of the context and its non-frozen ancestors | Created on a clean context; each query enters the context's domain as a callback (§8.3, §10.10). |
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
- **Timestamp parameters.** `Database.MergeAll`, `MergeTable`, `RunRuleSet`, `CreateExecutionState`, `WithExecutionState` and `RebuildPass` change their timestamp parameter to `uint? timestamp = null`. `null` resolves to `ClockCounterRep < 0 ? 0 : checked((uint)ReadCounter(clock))`. This is source-compatible, and a bare `Database` behaves as before. `RebuildContainers` with a `null` timestamp also resolves to the store clock on a Store-backed database; a bare `Database` keeps egglog's newest-union timestamp there. Under the pin, a clock-reading `UnionContainerMerge` would otherwise stamp the newest union's timestamp instead of the clock value egglog's merge reads.
- **Per-context order.** Epochs of one context never decrease (I3). After a context's `EndEpoch` the clock is at least e+1, and nothing decreases it.
- **Root equivalence.** A root that is not frozen runs sequentially with all its descendants, and nothing else increments the clock during its run. The pinned value therefore equals the live value at every read. With X4 and X42, `run → (no advance) → pass(T) → advance` reproduces `run_rules_inner` exactly. The root departs from egglog only (a) when a rule's Id constant has lost a union (X19), (b) in operations egglog does not have (`CreateChild`, `Freeze`, which settle, §7.6), (c) on the panic path (X18, map §8.1 row 22), and (d) when every rule of a run was pruned, where the bridge still merges what is staged at T (§10.3, map §8.1 row 26).

### 4.2 Id blocks and `IsLocalId`

**`IdBlockAllocator`** (`Contexts/IdBlockAllocator.cs`) and **`IdBlock`** (`Contexts/IdBlock.cs`, a sealed class with fields `long Next`, `readonly long Start`, `readonly long End` and `readonly long ReservedBefore`, so `Minted` is read from the one published block):

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
   - choose `size = clamp(2 × reserved so far, 16, 4096)`; the total reserved equals `Minted` at refill time, because the current block is spent;
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
1. Takes `lock (DefinitionLock)`. If the root is not frozen, it also enters `Root.Domain`, so it cannot run concurrently with root work, and then reads the frozen flag again: a root that froze meanwhile takes the frozen path of step 4.
2. Checks that the table is a `SortedWritesTable` (which `Register` requires), so a rejected table leaves the root unchanged, then calls `root.Database.AddTableCore(...)` (the public `AddTable` throws at a Store's root, §3.4).
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

**Clone:** `Database.Clone()` throws `InvalidOperationException` when `owner` is a child or `owner.Gate.HasLiveDescendants`. egglog's push/pop clone stays root-only. A clone keeps the source's clock (the counter layout is the same) and has no owner: it is a bare egglog `Database`.

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
- **Constructor:** `(LayeredUnionFind parentFinder, UnionFindOverlay overlay, DisplacedJournal journal, Table parentUnionFind, IdBlockAllocator? localIds = null)`. `localIds` tells `Merge` which ids are local when a union happens, for `ParentIdLosers` (§5.6, G2, L7).
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
1. Throw `ArgumentOutOfRangeException` for an id above `UnionFind.MaxId`, as the root's `Reserve` does. `a = parentFinder.Find(l)`, `b = parentFinder.Find(r)`.
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
  require parent frozen or clean
  from = journal.Count; end = parent.DisplacedCount
  if unionFindCursor == end: return from
  throw if overlayTable.HasPendingUnions                 // only when there is something to catch up with
  ("Unions staged in a context must be merged before it catches up with its parent.")
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

- `Find`, `AreEqual`: on a context that is not frozen they enter its domain re-entrantly (I24), as `ScanMerges` and `CheckInvariants` do; a frozen context takes no guard (§8.3). Then `ThrowIfNotReadable()` and `UnionFind.Find`. `Debug.Assert(IsUnionFindCurrent)`.
- `NewId`: `ThrowIfNotWritable()`. A child uses `idBlocks.Next()`; the root uses `checked((uint)Counters.Increment(IdCounter))`, bounded by `UnionFind.MaxId` (§4.2).
- `Union(a, b)`: `Write(s => s.StageInsert(uf, [a, b, s.Timestamp]))`. Returns whether `DisplacedCount` grew. Rows are rewritten at the next `Rebuild`.
- `ScanMerges<TVisitor>(ref visitor)`:
  - throws on the root, and requires `IsClean`;
  - for every slot with count ≥ 2, visits `(m, canonical)` for each member m with `IsLocalId(m) == false`, in slot order then insertion order (deterministic, decision 4);
  - reads, including `Find`, are allowed inside the visitor; the visitor runs as a callback of the domain, so `Write`, `Union` and a `Rebuild` that would run a pass are refused inside it (§8.3);
  - it counts as a running scan of the context, which `Dispose` refuses (§8.2), and it throws if the overlay's `Version` moves during a visit (a second safeguard).

  `IMergeVisitor` (`Contexts/IMergeVisitor.cs`) is `void Visit(uint member, uint canonical)`. This is rule-layer D8's contract: a merged class's canonical id is reported paired with itself when it is a parent id (L2, G1(a)).

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
internal sealed class LayeredTableSchema                 // immutable: get-only properties
{
    public TableId Id { get; }  public string? Name { get; }  public TableSpec Spec { get; }
    public SortedWritesTable Root { get; }  public TableInfo RootInfo { get; }
    public bool IsContextPrivate { get; }  public bool IsContradiction { get; }   // a volatile flag, set once
    public int Stratum { get; }  public int KeyCount { get; }  public int Arity { get; }  public int SortColumn { get; }
    public ReadOnlySpan<ColumnId> RebuildColumns { get; }
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
    internal void NoteUnsealed(HiddenSet set, SortedWritesTable layer);   // the ancestor table, whose row count seals the set
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
    L = new TableLayer(this, s)                                                // depth from its owner; Own = s.Root.CreateLayer(L); Info = new TableInfo(s.Name, Own, Serial)
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

**Adoption.** `Database.TakeRound` (`Database/Database.Merge.cs`) starts with `layers?.AdoptDeferred()`, which moves deferred layers into `Database.tables` on the domain thread. `Database.MergeTable` and `Database.Freeze` adopt them first as well. Every layer is therefore adopted before `MergeAll` takes a round, and `IsTakenOut(t)` (`TryGetLayer(t) is { IsAdopted: true } && tables.ContainsKey(t) == false`) is exact whenever the write phase consults it.

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
    public bool IsCleared { get; }  public int Version { get; }
    public bool HasRetractions { get; }   // the context retracted rows of this table: the guard allowed a delete or subsume, or DeleteLayered hid an ancestor row
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
- `Subset RefineVisible(Subset s)`: `Hidden.IsNone ? Table.RefineLive(s) : Table.RefineVisible(s, Hidden)`;
- `bool AnyVisible(SubsetRef s)`: whether some row of `s` is visible (`TableView.AnyVisible` forwards to it).

**`TableView`** (`Layers/TableView.cs`, `Layers/TableView.Lookup.cs`, sealed, immutable):

```csharp
internal sealed partial class TableView
{
    internal TableView(TableId id, TableSpec spec, LayerView?[] layers);
    internal static TableView ForTable(TableId id, TableInfo info, bool isFrozen);   // one layer, HiddenRows.None, any table kind (root and bare-database views, $uf included)
    public TableId Id; public TableSpec Spec; public LayerView?[] Layers; public int LayerCount; public uint PresentMask;
    public LayeredSubset AllRows(SubsetArrayPool? pool);       // Dense(0, n_l) per present layer with rows, n_l its physical row count (tombstones and hidden rows included); a one-layer view gives the table's All()
    public bool AnyVisible(int layer, SubsetRef subset);        // zero-variable atoms (map §8.1 row 2); false for an absent or out-of-range layer and an empty subset; ArgumentOutOfRangeException for a subset past the layer's rows
    public long ApproximateCount { get; }  public uint LatestStamp { get; }   // D16; computed on each read
    public long VisibleCount { get; }                           // exact: Σ live rows − live hidden rows; the hidden part is computed once per view, O(hidden)
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
- **Layer tables.** Every layer of these views is a `SortedWritesTable`. `LayeredSchema.RootView` serves registered function tables, from an array that grows geometrically; `RootView($uf)` throws `NotSupportedException` (X23).
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
  | `Predicted.cs:54`, `:78` `PredictRow` (both forms) | `ReadRootTable(t).TryGetRow` | `layers.TryGetVisibleRow`; arity and spec from `layers.Schema` |
  | `Predicted.cs:94`, `:120` `PredictColumn` (both forms) | `ReadRootTable(t).TryGetRowColumn` | `layers.TryGetVisibleColumn` |

  At the root, `PredictRow` and `PredictColumn` test `layers` first and then call `ReadRootTable` (the former `ReadTable` body, aggressively inlined), reading the spec only on a miss; their layered paths are `PredictRowLayered` and `PredictColumnLayered`. This keeps their pre-context root shape. `ReadTable` stays the throwing dispatch point the lookup instructions use.

  `ViewFor(t)` is `views?.Get(t) ?? layers.GetView(t)`. For `t == $uf` the lookups use the `OverlayDisplaced` arm: in a child, lookups, `PredictRow`, `PredictColumn` and `Context.TryGetRow` of `$uf` go through the `OverlayDisplacedTable`'s view-level lookup, and `GetView($uf)` throws `NotSupportedException` (X23). The new `VectorizedLookup` overloads over `TableView` run `LayeredKey1Lookup`, `LayeredKey2Lookup`, `LayeredKey3Lookup` or `LayeredKeyNLookup` (`Actions/`, structs mirroring `SortedKey*Lookup` and calling `TableView.TryGetColumn`).
- `ExecutionState.GetTable` (`Actions/ExecutionState.Database.cs:116`) throws when `layers` is set (it reads `DatabaseView.GetTable`, which throws in a child).
- `ExecutionState.TableIds` lists `LayeredSchema.Ids` in a child (§3.4).
- `ExecutionState.Containers` (`Containers/ExecutionState.Containers.cs:16`) reads `RequireView().Owner.Containers`, which in a child is the root's instance (the child constructor copies the reference).
- New `ExecutionState.VisibleRowCount(TableId t)`: `ReadTable(t).Count` at the root, `ViewFor(t).VisibleCount` in a child. For `$uf` in a child, where `ViewFor` refuses, it returns `Context.DisplacedInViewCount`: the number of ids not canonical in the child's view (the parent's view count when the child was created plus the journal, by I8), which is what the parent and a copy report. The bridge's `TableAction.row_count` (`eb/lib.rs:1293-1294`, `state.get_table(t).len()`) uses it.

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
ThrowIfNotWritable(); using Domain.Enter(); ThrowIfNotWritable()   // the first check reports "frozen" rather than a domain conflict
throw if inside a callback of the domain (§8.3); PrepareForWrite()
e = BeginEpoch()
try   { st = Database.CreateEngineState(e); res = action(st); st.Flush(); Database.RecordFailure(st)
        if IsRoot: Debug-only AssertPendingSortCells(e) on every registered table      // X38
        Database.MergeAll(e) }                                                        // epilogue seals and invalidates (X40)
catch when !IsRoot { faulted = true; throw }
finally { EndEpoch() }
```

It leaves rebuild work pending (spike semantics): a union makes the context not clean until `Rebuild`. In a child, incoming rows are canonical when stored, so a write with a stale id lands on the canonical key and merges with the visible row there. At the root, `Context.Write` is egglog's raw write: rows are stored as given, the sort cell is the caller's contract (asserted in DEBUG by `SortedWritesTable.AssertPendingSortCells`, which walks the pending row queues without dequeuing), and a stale row is rewritten by the next rebuild or settle.

The engine's own writes (this batch, catch-up and rebuild copies, host-write flushes, container interning) create their states with the internal `Database.CreateEngineState(timestamp)`, which stamps every row with the state's timestamp. `CreateExecutionState` and `WithExecutionState` are the raw API (egglog's): rows carry whatever stamps the caller gives them, so in DEBUG builds they mark the store (`Store.HasRawWrites`), which exempts it from the re-stamp assertion (§7.2 (c′), §19 item 9).

**`PrepareForWrite()`:**
- if the parent is not frozen, call `Parent.Rebuild()`, then apply the stopped-parent rule (§6.9);
- if `!IsUnionFindCurrent || Tables!.NeedsCatchUp`, or the eq-container version moved since the context's guard last ran (§7.4), call `Rebuild()`.

### 6.7 Retraction (C10, decision 1, R1)

**`RetractionGuard`** (`Layers/RetractionGuard.cs`): `CheckRemove(TableId)`, `CheckClear(TableId)` and `CheckSubsume(TableId)`. A context-private table is always allowed. Throws are `InvalidOperationException`s naming the table and the rule.

| Operation | Root, no live descendants | Root, live descendants | Child, no live descendants, parent frozen | Child, no live descendants, parent writable | Child, live descendants |
|---|---|---|---|---|---|
| `CheckRemove` | ok | throw | ok | **throw (R1)** | throw |
| `CheckClear` | ok | throw | ok | **throw (R1)** | throw |
| `CheckSubsume` | ok | throw | ok | ok | throw |

**Enforcement points:**
- Both `ExecutionState.StageRemove` overloads (`Tables/ExecutionState.cs:131`, `:145`) call `guard?.CheckRemove(table)`. `Database.GuardForNewState()` returns the database's guard at every Store-backed database, the root included, and `null` only for a bare `Database`. A snapshot that is `null` at a root without live descendants would be unsound: a state from `CreateExecutionState`, or a buffer from `NewBuffer`, can outlive a later `CreateChild` and then delete the new child's rows. `RetractionGuard.AttachRoot` gives the root's guard the root's gate, and `CheckRemove`, `CheckClear` and `CheckSubsume` return after one inlined volatile read while the root has no live descendants. A Store's root therefore pays one guard load and one gate read per staged removal; a bare `Database` pays one null test. Rebuild and refresh stage through `MutationBuffer.StageRemove` and are never treated as retractions.
- Buffers from `Database.NewBuffer` carry the database's guard (`MutationBuffer.AttachRetractionGuard`; `FreshHandle` copies it), so I22 holds at that entry point too. The check reuses the buffer's existing `allowsDelete` branch, so unguarded removals pay nothing. Buffers from `Table.NewBuffer` (rebuild, refresh and the internal table API) carry no guard.
- The rule `Remove` instruction consults the guard once per batch; rule deletes would otherwise bypass decision 1 and R1.
- `Database.ClearTable` calls `RetractionGuard?.CheckClear(table)`. `$uf` is never private, so clearing it throws while descendants live.
- `Context.ClearTable` in a child refuses `$uf` and runs the guard and decision 2 checks before it changes anything; only a failure after those checks faults the child.
- A frozen (shared) `SortedWritesTable` refuses `Clear`.
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
- **`DescendantGate`** (`Layers/DescendantGate.cs`): `HasLiveDescendants` (volatile read), `LiveDescendants`, `Enter()`, `Exit()` (interlocked). Every `Context` owns one. It also carries change marks (`BeginChange`, `EndChange`, `Changes`, `IsChanging`), which `CreateChild` and `Dispose` set on every gate they change, so the checker compares a gate with the tree only on a consistent reading (§12.7).

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

**Children of a stopped parent.** After rebuilding a parent that is not frozen, `Rebuild` and `PrepareForWrite` (so `Write`, `ClearTable` and `CreateChild`) check whether that parent was stopped by a contradiction. A child with `StopOnContradiction` becomes contradicted, which is sound because the contradiction row is visible in its parent (I25). Any other child gets `InvalidOperationException` without being faulted; it can only be disposed.

### 6.10 Lift support reads (L1)

Both require `IsClean` and throw on the root. Like `ScanMerges` (§5.7), they enter the domain of a context that is not frozen, run the sink as a callback of the domain (§8.3), and count as running scans, which `Dispose` refuses (§8.2).

- **`Context.ScanOwn<TSink>(t, ref sink)`** scans `TryGetLayer(t)?.Own.All()`, skipping tombstones. These rows are canonical here (I13). O(own rows).
- **`Context.ScanAncestorsMentioning<TSink>(t, id, ref sink)`**:
  - returns at once for a table without rebuild columns (no id can be mentioned), for a context-private table, and for a local id (I10);
  - for each `j ∈ [ClearedFloor(t), Depth)`, takes `AncestorLayer(j, t).SharedRebuildIndex()!.Data.TryGetSubset(id, out rows)`;
  - delivers each live row with `!HiddenBetween(t, j, r)`, each row once;
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

Details the loop above leaves out:
- Inside a callback of the domain (§8.3), a `Rebuild` that would run a pass throws `InvalidOperationException`; one with nothing to do returns false.
- After `Parent.Rebuild()`, a child applies the stopped-parent rule (§6.9).
- When the eq-container version moved since the child's guard last ran, the guard runs over the whole journal before the clean test, taking no epoch when nothing else is to do (§7.4).
- `Rebuild` returns false when the catch-up only moved cursors and copied nothing. The settled depth advances only when a rebuild runs passes.
- The DEBUG `CheckInvariants` is skipped when a contradiction stopped the merge with writes still pending.

### 7.2 Catch-up of ancestor rows (C8 step 2)

`ContextTables.CatchUpRows(uint e)`:

```text
if settled == Depth: return false
for a in [settled, Depth): generation check: every seen ancestor layer has Version.Major == cursor's, else Fault (I16)
st = Database.CreateEngineState(e); hides = []; floor = Owner.SemiNaiveFloor
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
                                                                   Debug.Assert(Store.HasRawWrites)                       // §19 item 9
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
- **(c′)** is the C7 safeguard. It never fires when writers stamp with the state's timestamp (I21): every engine and bridge path does, and children enforce it at the merge (X38). Tests assert `RestampedCopies == 0`, and DEBUG builds assert at every re-stamped copy unless the store was written through the raw execution-state API (§6.6, §19 item 9).
- Ancestor tombstones need no work: a replaced row reappears as an append. This relies on an invariant: an ancestor with live descendants tombstones a row without appending one only when its rebuild folds a rewritten row into an existing row after a union. It holds because every retraction entry point is guarded (§6.7) and compaction is gated (§6.8). `ContextTables.AncestorRowsChanged` documents it.
- Context-private tables are left out of the row cursors and of the generation check (decision 1: descendants never read them).
- `CatchUpRows` and the generation check skip an ancestor whose `ChangeStamp` did not move since its cursor's `Stamp`; this is equivalent to the loop above and cheaper.

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
st = Database.CreateEngineState(e); hides = []
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
- Two shortcuts, each equivalent to the loop above: step 3 is skipped when no id was displaced since the last pass (X38), and step 4 leaves out own losers that are local to the child, which no ancestor row mentions (I10).
- Step 4 reads a writable ancestor's rebuild index, which it refreshes from that ancestor's appends since the child's creation (§8.2, §14.2).
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
  - A child's `RefreshDirtyContainerIds(e)` consumes `Since(ref dirtyCursor)` and, when it is nonempty, calls `Database.RunOnTables(own layers with rebuild columns, new RefreshTableOperation(ids, e))` (`Database/RefreshTableOperation.cs`), which notifies every layer that re-staged rows and returns whether any did. Root rows are re-stamped by the root itself and arrive as appends.
  - `Context.Dispose` clears the log when the root gate reaches zero.
- **Eq guard (G-b).** `EqContainerGuard.Check(ctx)` (`Contexts/EqContainerGuard.cs`): if `ContainerValues.HasEqContainers`, it throws `NotSupportedException` when an eq-sort container holds one of the context's own displacements (`IsOwnDisplacement`, `AnyEqContainerMentions(id)`). The context is then faulted.
  - Checking only the journal rows of each pass is not enough: the root can intern an eq-sort container naming a child's loser after the child displaced it, and the root's container rebuild can produce one.
  - So `ContainerValues.EqVersion`, shared with every environment that holds ids, moves on each new content (an `Interlocked` increment), and a child records the version its guard last checked against. `IsClean` includes `IsEqGuardCurrent`: the journal is empty, or the version has not moved (§7.5).
  - When the version moved, `Rebuild` runs the guard over the whole journal before its clean test, taking no epoch when nothing else is to do, and so does each pass's guard; `PrepareForWrite` rebuilds in that case.
  - Own displacements are still the only ids checked: an intermediate ancestor's losers are its own guard's job, because a child is clean only while its parent is.
- **Frozen databases intern no new eq-sort container** (`InvalidOperationException` in `ContainerEnv`'s insert path). A frozen context never runs its guard again, so a container interned at a frozen root naming a frozen descendant's loser could never be refused. Looking a container up, re-interning an existing one, and interning containers over base values stay allowed.
- **Child databases never rebuild containers.** `RebuildContainers`, `RebuildPass` and `Rebuild` throw `InvalidOperationException` when `owner` is a child (§3.4).

### 7.5 `IsClean`

```text
IsClean (root)  = !disposed && !faulted && (frozen || journalMark == DisplacedCount)                       // X42
IsClean (child) = !disposed && !faulted && (frozen ||
                  (Database.Catalog.Notifications.IsEmpty && journalMark == DisplacedCount &&
                   IsUnionFindCurrent && !Tables.NeedsCatchUp && IsEqGuardCurrent && Parent.IsClean))   // IsEqGuardCurrent: §7.4
```

- At the root, pending notifications do not make it unclean: staged writes are invisible until merged, as in egglog. A child's view never includes them either; when the root merges them, its `ChangeStamp` moves and the child catches up.
- The recursion stops at the first frozen ancestor, so the cost is O(unfrozen ancestors), as D16 requires.
- **`IsViewCurrent`** is `IsClean` without its pending-notifications term, at every level of the chain: writes staged and not merged are invisible, as at the root (X42). Rule building, planning and rule runs in a child require it (§9.1).
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
       MarkRebuilt(uf) on every function table                                       // past the merged rewrites
finally: EndEpoch()
Rebuild()                                                                           // when the merge staged unions
```

- **`SortedWritesTable.SettleAppends(TableId uf, Table ufTable, uint e, ExecutionState st)`** (new, `Tables/SortedWritesTable.Rebuild.cs`): returns false for a table without rebuild columns; otherwise gathers the live rows in `[FirstRowSinceRebuild(uf), Rows.Count)` and rewrites the stale ones with `RebuildRows` (the egglog rewrite: `remove` plus canonical `insert` stamped e). It shares `RebuildRange` with `RebuildFull`. `Settle` moves every function table's rebuild mark only after its `MergeAll`, past the merged rewrites, so the next `CreateChild` does not settle the same rows again. `HasRowsSinceRebuild(uf)` is `FirstRowSinceRebuild(uf) < data.Count` (`Tables/SortedWritesTable.RebuildWatermark.cs`), and false for a table without rebuild columns.
- **`SettleTableOperation`** (`Database/SettleTableOperation.cs`, `ITableOperation`) mirrors `RebuildTableOperation`, so `RunOnTablesCore` notifies settled tables.
- **Why it suffices.** Rows before a table's rebuild mark were canonical at the mark and are kept canonical by the journal-driven rebuilds since (egglog's rebuild). Rows after it are exactly the rows a raw write could have left stale. Once settled, a child's view starts canonical (I35); later root appends are checked by the child's catch-up branch (a).
- **Cost.** O(rows written since the last rebuild) per `CreateChild` of a non-frozen root, and once at the root's freeze. Root programs that never create a child never settle.

---

## 8. Freeze, dispose and threading (C9)

### 8.1 `Context.Freeze(FreezeWarmup? warmup = null)`

1. Return if already frozen. Throw if called re-entrantly (§8.3). Enter `Domain`. Throw if disposed, faulted or contradicted, or if a failure is recorded and not taken.
2. If the parent is not frozen, throw `InvalidOperationException("A context can be frozen only once its parent is.")`.
3. `Rebuild()`, then, at the root, `Settle()` (§7.6). At the root, pending writes merge in the first pass (§7.1). The context is now clean: pending queues are empty and the rebuild is complete (C9 step 1). A failure the rebuild or the settle recorded, and a contradiction the rebuild found, are refused here. These refusals, like those of step 1, do not fault: a recorded failure is not an exception (§8.4), and the context stays unfrozen and consistent, so the caller can take the failure and freeze again. An exception from the rebuild or from the database freeze still faults a child (X12).
4. `Database.Freeze(warmup)` (`Contexts/Database.Freeze.cs`):
   1. Adopt deferred layers (§6.2). Throw if notifications are pending, or if any table has pending writes (`Table.HasPendingWrites`); every table is checked before any is frozen.
   2. For each `TableInfo`, call `info.Table.Freeze()` (X13). This is a new abstract member of `Table`.
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
- `SortedWritesTable.Clear` throws on a frozen (shared) table.

**`FreezeWarmup`** (`Database/FreezeWarmup.cs`) holds a list of `CachedIndexKey(TableId, ColumnId[])` (`Planning/CachedIndexKey.cs`). `FreezeWarmup.From(IEnumerable<PlanTemplate>, IQuerySchema)` uses `PlanIndexKeys.Collect` (§9.10); it takes the schema to know which columns are cacheable (`IQuerySchema.IsCacheable`, §3.4). The bridge passes every cached plan (§10.8).

### 8.2 `Context.CreateChild(ContextOptions? options = null)` and `Dispose()`

**`CreateChild`:**
```text
ThrowIfDisposed; throw if faulted or contradicted; throw if called re-entrantly (§8.3)
if !frozen: using Domain.Enter(); Rebuild(); if IsRoot: Settle()     // the parent is clean and canonical when cursors are initialized
            refresh the rebuild indexes of this context and its non-frozen ancestors   // RefreshRebuildIndexesForChildren, ContextTables.RefreshOwnRebuildIndexes
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

- **Stopped parent.** The `Rebuild` above applies the stopped-parent rule of §6.9 to this context before a child is created.
- **Failures.** A failure that this context's rebuild or the root's settle records during `CreateChild` stays recorded on this context, as for `Write` and `Rebuild`, which record failures without throwing. The child, which has its own failure slot, is still created. Taking that failure right after `CreateChild` is the bridge's job (§10.8).
- **Rebuild indexes.** Creating a child of a context that is not frozen brings the rebuild indexes of that context and of its non-frozen ancestors up to date, as root work at child creation (§1 item 11): O(tables) plus the appends since each index's last refresh (all rows the first time, or after a compaction between child lifetimes). Afterwards the child's step 4 and its lift reads refresh a writable ancestor's index only from that ancestor's appends since the child's creation (§14.2).
- DEBUG builds check I35 (`CheckRootSettled`) at `CreateChild` and `Freeze` of the root.

**`Dispose`:**
- A second call does nothing. The root throws. A re-entrant call throws (§8.3).
- `lock (children)`: throws if any child is live, and throws while a `ScanMerges`, `ScanOwn` or `ScanAncestorsMentioning` of the context runs, from its visitor or from another thread: a per-context scan counter that `Dispose` closes with a compare-and-swap. A frozen context is scanned without its domain, so the re-entrance check cannot see a dispose from inside its visitor.
- The ancestors' gates and the tree change between `BeginChange` and `EndChange` (§6.8).
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
- `int Depth` (owner-thread only); `IsHeld`.
- `ThreadAffinityScope` (`Concurrency/ThreadAffinityScope.cs`) makes `using Domain.Enter()` allocation-free.

**Re-entrant structural calls.** `CreateChild`, `Freeze` and `Dispose` throw `InvalidOperationException` when the calling thread already holds the context's domain (`Domain.Depth > 0` at entry): from inside a `Write` action, a rule's action or external, a `ScanMerges` visitor or a sink. Reads inside visitors stay allowed. This keeps every structure a running operation captured (states, guards, snapshots) valid until it ends.

**Nested writes.** A write batch's action, a `ScanMerges`, `ScanOwn` or `ScanAncestorsMentioning` visitor or sink, and a child's rule search (§9.1) run as callbacks of the domain (`ThreadAffinityGuard.EnterCallback`, `ExitCallback`, `InCallback`). Inside a callback, `Write` and `Union` on any context of the domain, and a `Rebuild` that would run a pass, throw `InvalidOperationException` before anything is staged or faulted. Otherwise a nested rebuild would run an epoch inside the batch's epoch (breaking I3, and faulting the child through the journal's order check), a nested ancestor write would change the parent the batch caught up with, and a write inside a visitor would change the partition being scanned. Reads, and rebuilds with nothing to do, stay allowed.

The bridge (§10) follows the same rule. The raw escape's action (`EGraph.WithExecutionState`, `EGraphContext.WithExecutionState`), an `EGraphContext.ForEach` visitor and an extraction over a context run as callbacks too (the root's egglog-compatible `EGraph.ForEach` takes no domain, as before), and inside a callback every bridge call that writes or changes a context of the domain throws before anything is staged: `StageRow`, `StageTerm`, `StageUnion`, `AddValues`, `AddTerm`, `FlushUpdates`, `RunRules`, `ClearTable`, and the root's egglog-compatible `AddValues`, `AddTerm`, `FlushUpdates`, `RunRules`, `ClearTable` and `RunIteration`. Reads, `FreshId` and a nested raw escape stay allowed, as egglog's `with_execution_state` borrows the e-graph shared and its `&mut self` calls cannot be made inside the closure. Without the refusal, a raw action's rows (stamped with the clock read before the action, and merged when the next flush runs) would merge after a context-API write the action issued before them, or below the watermark a run inside the action committed, so no later run matched them (I31). `GetContainerValue` is not refused: interning is keyed by contents and order-independent.

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
- the root's `BaseValues` registry (thread-safe);
- frozen ancestors' once-cells (serial builds);
- `children` lists (locked).

**Per-context state** is touched by the owning domain only, with one exception: the tasks of the context's own rule run may create layers. During a search, rule tasks stage writes into their states' buffers, read the rule set's view snapshot, read ancestors' immutable structures, and may call `GetOrCreateLayer`, which publishes an empty layer to the catalog and the layer slots under `ContextTables.layerLock` (I34). The table map, the view cache, `ViewGeneration` and the hidden sets do not change until the search ends.

### 8.4 Exceptions

- **Root:** `Database`'s guarantee (`Database/Database.Merge.cs` catch): every table is back, the unmerged round stays notified, caches are reset.
- **Child:** any exception escaping `Write`, a rule run, `Rebuild` or `Freeze` sets `IsFaulted` (X12). Every later call except `Dispose` throws. Refusals made before anything changes do not fault: the rule-run guards (§3.4), `Freeze`'s failure and contradiction refusals (§8.1), `ClearTable`'s checks (§6.7), nested writes (§8.3) and the stopped-parent refusal (§6.9).
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
    public static ContextView ForRoot(Database root);          // one LayerView per table (TableView.ForTable), HiddenRows.None; caches its own root views, so a bare Database works too
    public ContextView(Database database, ILayerViewSource source);
    public Database Database { get; }  public int Depth { get; }  public int LayerCount => Depth + 1;
    public bool IsLayered => Depth > 0;
    public ILayerViewSource? Source { get; }  public bool CanCanonicalize { get; }
    public ParallelHeuristics Parallelism => Database.Parallelism;
    public long SizeEstimate => Database.TotalSizeEstimate;     // child: own rows only (C9, X26)
    public long Generation { get; }
    public long Stamp { get; }                                  // Σ Database.ChangeStamp over the context and its ancestors (Context.ViewStamp); 0 without a context
    public TableView GetTableView(TableId table);               // child: $uf throws NotSupportedException (X23)
    public ViewSnapshot Snapshot(ReadOnlySpan<TableId> tables); // the cached instances, pinned for one run
    internal ViewSnapshot Snapshot(ReadOnlySpan<RulePlan> rules, DenseIdMap<ActionId, ActionInfo> actions, TableId? skipLookups);   // atoms and lookup tables of a rule set (RuleSet.LookupTable)
    public bool TryCanonicalize(uint id, out uint canonical);   // false only for a bare Database (X19)
    public LayeredSubset Evaluate(TableId t, ReadOnlySpan<Constraint> cs, List<Constraint> fast, List<Constraint> slow);   // §9.4
    public int EstimateSize(TableId table, Constraint? constraint);
    long IPlanStatistics.GetApproximateSize(AtomId atom, QueryAtom info);
    ColumnUniqueness IPlanStatistics.GetColumnUniqueness(TableId t, ColumnId c);
}
internal sealed class ViewSnapshot          // Execution/ViewSnapshot.cs: TableView?[] by table index; immutable
{
    public int Count { get; }  public TableView? Get(TableId table);  public TableView Require(TableId table);
    internal bool Wraps(TableView?[] array);                    // whether it is a snapshot over this array (the shared root snapshot)
}
```

`TryCanonicalize` is `Store.RootUnionFind.FindNaive` at a Store-backed root, `Owner.UnionFind.Find` in a child, and false for a bare `Database`, which has no designated union-find.

**Wiring:**
- `Database.View` is created lazily at the root and assigned at child construction.
- `RuleSetBuilder(Database db)` evaluates headers through `db.View` (`InstantiateLayered`, §9.4).
- `RuleSetBuilder.Build()` stores in the `RuleSet` (`RuleSet.View`, `RuleSet.Views`) the snapshot `View.Snapshot(rules, actions, skip)`: the tables of every rule's atoms and of its actions' lookup instructions (`Lookup`, `LookupWithDefault`, `LookupWithFallback`, `LookupOrInsertDefault`), found through `RuleSet.LookupTable` without building a list. In a child `skip` is `$uf`, which a child reads through its overlay table, not a view (X23).
- The builder records `View.Generation` and `View.Stamp` (`RuleSet.ViewGeneration`, `RuleSet.ViewStamp`) when its first header is evaluated, not at `Build`. Header subsets name row ids, which a merge, hide, clear or compaction anywhere on the chain changes without changing any view's structure; the stamp catches those, and the generation catches dropped views. Header evaluation and the snapshot read the same cached instances while neither moved.
- `Database.RunRuleSet` throws `ArgumentException` unless the rule set was built on this database's view. A rule set assembled by hand through `RuleSet`'s public constructor (no view) runs on a database that is not layered, its views pinned when the run starts, and is refused with `ArgumentException` on a layered one. In a child the run applies §3.4's guards: it throws `InvalidOperationException` when the views are not current (§7.5) or when the generation or the stamp moved since the first header. The check is at the run, not at `Build`, because the views can also move between `Build` and the run. The root keeps egglog's rule: build right before running. The run sets `layers.InSearch = true` around `RunRulesSerial`/`RunRulesParallel`, as a callback of the domain (§8.3), and gives the root state `views = ruleSet.Views`.
- **Current views.** `ContextView.Evaluate`, a child's `Snapshot` (when it reads any table) and the planning statistics require `IsViewCurrent` in a child (§7.5); `EstimateSize` does not, and building an empty rule set needs no current views.
- `JoinRun(Database db, ViewSnapshot views, ExecutionSignals signals, bool parallel, FrameUpdatesPool?)` and `RuleWork` read tables from the snapshot (today's `db.GetTableInfo(info.Table)` at `Execution/JoinRun.cs:178` becomes `views.Get(info.Table)`). `idleSerialJoin` stays per `Database`.
- **Root views.** `ContextView.ForRoot` caches its own single-layer views (`TableView.ForTable`, over any table kind, so `$uf`, tables that are not `SortedWritesTable`s and bare databases are covered), rather than using `LayeredSchema.RootView`. Every root rule set shares one `ViewSnapshot` over that cache, which is sound because its entries are only ever filled; a new snapshot is made only when the cache array grows. A root snapshot leaves out the tables of action lookups, which root lookups never read through it (§6.5). Building a root rule set therefore allocates no snapshot.

### 9.2 Layered subsets (`Offsets/`)

**`LayeredSubset`** (`Offsets/LayeredSubset.cs`) is an owned, move-only struct, with `MaxLayers = 32`.

**Fields:**
- `Subset single`, `int singleLayer`: the rows when they lie in one layer;
- `Subset[]? layers` with `uint mask`: per-layer subsets when two or more layers are non-empty.

**Members:**
- `Empty`, `Single(int, Subset)`, `FromLayers(Subset[], SubsetArrayPool?)`: accepts an array of any length up to 32, takes ownership of it, disposes its empty entries, and collapses to `Single` when possible;
- `Size`, `IsEmpty`, `IsMultiLayer`, `LayerMask` (0 for an empty single-layer subset), `SingleLayer` (−1 for a multi-layer subset), `SingleRows` (throws `InvalidOperationException` for a multi-layer subset);
- `GetLayer(int) → SubsetRef`;
- `Intersect(in LayeredSubset, SubsetArrayPool?)`: two singles in the same layer use today's `Subset.Intersect`, which is the root path; otherwise it works per layer and collapses;
- `Clone(SubsetArrayPool? pool = null)`, `Take`, `TakeSingle`, `Dispose(pool)`, and a single-layer `GetEnumerator`, so code written against `Subset` headers (`PlanInstantiationTests`, `NaiveRuleRunner`) compiles unchanged.

**`SubsetArrayPool`** (`Offsets/SubsetArrayPool.cs`) is a non-thread-safe stack of `Subset[LayerCount]`, owned by `TrieNodePool` (§9.5). It keeps only arrays of exactly `LayerCount` entries, at most `MaxRetained` (1024) of them, and checks in DEBUG that a returned array owns no buffers.

### 9.3 Visibility in scans and index builds (decisions' engine-wide rule)

**`Tables/Table.cs`:**
- `abstract Subset RefineVisible(Subset s, HiddenRows hidden)`.
- Hidden-aware overloads of `ScanGenericBounded`, `ScanGeneric`, `ScanProject` (sink and `TaggedRowBuffer` forms) and `ForEachColumn`.
- The existing overloads are unchanged: they keep calling the plain sealed scans, and the hidden-aware overloads fall back to them when given `HiddenRows.None`. Results are the same as forwarding `None`, and the root, which calls the plain overloads, pays not even an `IsNone` test per call (§1 item 11). Dispatch is on `Kind`, with new `OverlayDisplaced` arms.

**`Tables/SortedWritesTable.Scan.cs`:**
- When `hidden.IsNone`, it runs today's code byte for byte.
- Otherwise it uses separate loops with `EmitIfMatchesHidden`: tombstone test, then `hidden.Contains` with a cursor, then constraints.
- `RefineVisible` uses `VisibleRowFilter` through `Subset.Retain`.

**`Tables/DisplacedTable.cs` / `OverlayDisplacedTable`:** hidden overloads through `HiddenSkippingSink` and `HiddenOnlyFilter`.

**Index builds:**
- `Indexes/ColumnIndex.cs` gains `BuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)`, `RebuildForSubset(...)` and internal `MergeSubsetVisible(...)`.
- `ColumnIndex.Build.cs` gains `RebuildFull(..., HiddenRows)`; both `RebuildFull` overloads share the private `RentPairs` and `GroupPairs`, and the root's `RebuildFull` behaves as before.
- `TupleIndex.BuildForSubset(..., HiddenRows)` and internal `TupleIndex.MergeSubsetVisible(...)`, matching `ColumnIndex`'s.
- `TableIndexing.GroupByColumn/GroupByKey(..., HiddenRows)`.
- `Execution/SparseColumnIndex.Build(Table, SubsetRef, ColumnId, HiddenRows)`, with a private `Group`.
- As for scans, the plain index-build overloads are unchanged, and the hidden-aware ones fall back to them for `HiddenRows.None`. T2 and T3 assert that `None` matches the plain overloads bit for bit.
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
if tv.PresentMask == 0: ConstraintProcessor.Split(root entry of t, or the database's own entry for synthetic layers, ...); return Empty
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
- A single-layer node, which includes every root node, keeps a plain `Subset` plus a layer byte inline. A multi-layer node's rows, and the spare indexes of a recycled node, live in a `TrieNodeSideState` (`Execution/TrieNodeSideState.cs`), created on first use and kept with the node through the pool, so a warm serial run allocates none; a concurrent node with multi-layer rows (a child's parallel run) allocates one. `IsMultiLayer` is one flag test, and the node stays 64 B. Holding a `LayeredSubset` inline grew it to 80 B, which a parallel root run pays per frame at join positions 0 and 1 (§1 item 11).
- New members: `Size`, `IsEmpty`, `LayerMask`, `IsMultiLayer`, `SingleLayer`, `GetLayerRows(int)`, `Initialize(LayeredSubset, bool)`, `Initialize(int layer, Subset, bool)`, `Intersect(in LayeredSubset, SubsetArrayPool?)`, `GetCachedIndex(ColumnId, int layer, LayerView)`, `ResetDense(int layer, OffsetRange, TrieNodePool)`. Cache arrays are sized from the rows' highest layer, and growing them copies the old entries.
- Children: `TryGetChild(int slot, ...)`, `AddChild(int slot, ...)`, `static ChildSlot(int column, int selector, int arity)`.
- Index slot: `IsMultiLayer ? layer * arity + column : column`. Child-map slot: `selector * arity + column`, where selector 0 means all layers and `l+1` means layer l only. A single-layer node, which includes every root node, uses today's slots after one `IsMultiLayer` test.

**`TrieNodePool`:** `SubsetArrayPool? Layers` (null for one-layer runs, so a root run allocates no array pool), `Rent(LayeredSubset)`, `Rent(int layer, Subset)`; `Rent(Subset)` means layer 0.

**Probers:**
- **`Execution/ProberPiece.cs`** (struct): `Kind`, `byte Layer`, `HasOuter`, `MayHoldInvisible`, `Outer`, `Column`, `Tuple`, `Sparse`, `LayerView View`, `Count`, and `TryGetSubset(uint)` / `TryGetSubset(uint[])`.
- **`Execution/Prober.cs`** (changed): `TrieNode Node`, `ProberPiece First`, `ProberPiece[]? Rest`, `RestCount`, `PieceCount`, `Count` (the sum), `GetPiece(ref p, i)`; `TryGetSubset` forwards to the first piece. A prober is 72 B (48 B before M10.2). It is per-position scratch, allocated once per join task, so a parallel root run pays it per morsel (§14.1).
- **`ProberCursor.MoveNext(in ProberPiece)`.**
- **`Execution/LayerHits.cs`:** per-position scratch (`SubsetRef[] Rows`, `int[] Pieces`, `Count`, `Total`). The per-position array is allocated on first use, so a root run never allocates it.
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
- `Replay` passes `e.Layer`; `CloneForMorsel` copies `views` and the roots array (tasks only compare nodes against it, to attribute counters).

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
- Action constants (new): `ActionIdConstant(int Instr, int Operand, Value Value)` (`Query/ActionIdConstant.cs`, a readonly record struct). `RuleBuilder.Build` (`Query/RuleBuilder.cs`) records them once, by scanning the program through `ActionSubstituter.Operands`, so the operand order is defined in one place. It records one for every constant operand that:
  - lands in an Id column of the instruction's table: `Insert` and `InsertIfEq` values, `Remove` keys, `Lookup`/`LookupWithDefault`/`LookupWithFallback` keys, `LookupOrInsert` keys and `WriteVal` entry defaults (`GetIdColumns(table)`), or either id column of the store's union-find table; or
  - is flagged as an Id by the caller: new `QueryEntry.IdConst(Value)` (`Planning/QueryEntry.cs`) sets a new `IsIdConstant` flag. This covers `AssertEq`, `AssertNe`, `AssertAnyNe`, `InsertIfEq`'s comparands and external-function arguments, which have no table column. The bridge lowers every Id-typed constant through `IdConst`. `QueryEntry` equality ignores the flag (a flagged and an unflagged constant of one value are the same operand), and `WithConstant` keeps it.

  `Operand` indexes the instruction's operands in the order `ActionSubstituter` defines per `InstrKind`. `ActionInfo` (`Query/ActionInfo.cs`) gains `ActionIdConstant[] IdConstants`; `CachedPlan` carries it through `Actions`. A substituted `ActionInfo` keeps the written list.

**Types:**

| Type (file) | Role |
|---|---|
| `CanonicalConstants` (`Query/CanonicalConstants.cs`) | `static uint[]? Compute(ContextView view, IdConstant[] query, ActionIdConstant[] action, uint[]? written = null)`: `null` iff there are none; otherwise, for each constant in that order, `view.TryCanonicalize(c) ? canonical : c.Value.Rep`. This is **K**. When no constant changes, and always for a bare `Database`, it returns the written vector itself (`Written(query, action)`, which the cached plan keeps), so a run allocates nothing per rule. |
| `ConstantSubstitution` (`Query/ConstantSubstitution.cs`) | The query entries `(atom, column, written, canonical)` and action entries `(instr, operand, written, canonical)` that differ. `Create` returns `null` when nothing differs. |
| `TemplateSubstituter` (`Query/TemplateSubstituter.cs`) | `Substitute(PlanTemplate, ConstantSubstitution)` rewrites only the matching `EqConst`s in atoms, headers, existence checks, stages and blocks. It shares unchanged sub-objects; new stage arrays get their own `PreparedBlock`. |
| `ActionSubstituter` (`Query/ActionSubstituter.cs`) | `Substitute(ActionInfo, ConstantSubstitution)` builds a new `ActionInfo` whose changed instructions are new instances with the canonical operands; unchanged instructions are shared. |
| `ConstantVariant` (`Query/ConstantVariant.cs`) | `(uint[] Key, PlanTemplate Template, ActionInfo Actions)`. |
| `CachedPlan.GetVariantFor(uint[]? K)` (`Query/CachedPlan.cs`) | Returns `(Plan, Actions)` when K is null or equals the written values. Otherwise it consults a copy-on-write `ConstantVariant[]` (at most `MaxVariants` = 16 entries; insert under a lock, then double-check). When the array is full, a new K's variant replaces the oldest; if it stopped caching instead, a Store-backed root would rebuild its variant on every run after 16 canonical changes. |

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

`FreezeWarmup.From(plans, schema)` feeds this list to `Database.Freeze` (§8.1).

---

## 10. Per-context rules in the bridge (M8 port + this spec)

This section names M8 types as egglog-bridge's: `EGraph`, `RuleInfo`, `FunctionInfo`, `BridgeQuery.AddRulesFromCached`, `PanicFunction`, `TableAction`, `UnionAction`, `IterationReport` and `PanicException` (`Bridge/`). If the port names them differently, map them one to one.

### 10.1 Split of state

**`EGraph`** keeps the definitions:
- `funcs`, a copy-on-write `functionTables` snapshot, and `RuleTable rules`;
- `panicFunctions` (a `ConcurrentDictionary`, with registration under `registrationGate`);
- `StopOnContradiction` (an `ExternalFunctionId`) and `Contradiction` (a `FunctionId?`), set by **`DesignateContradiction(FunctionId)`** (rule-layer E4): the function must be nullary, once per e-graph, and the call is refused once the store has had a child. The refusal and `Store.DesignateContradiction` run under the store's definition lock, under which the first child notes itself before its constructor reads the contradiction table, so a designation and a first `CreateChild` on another thread never interleave;
- the context-local slots, made by **`NewLocalSlot<T>(Func<EGraphContext, T> factory)`** (rule-layer §13 item 4), and the `ParallelismBudget` (§10.9);
- `Root` (an `EGraphContext`).

Its constructor, **`EGraph(ParallelHeuristics)`** (rule-layer E9; `EGraph()` passes `ParallelHeuristics.Default`), builds a `Store` with those heuristics; `$uf`, the id counter and the clock come from it, and functions are added through `Store.AddTable`. The root-only egglog API (`RunRules`, `FlushUpdates`, `AddValues`, `AddTerm`, `LookupId`, `TableSize`, `ForEach`, `WithExecutionState`, `FreshId`, `ClearTable`, `Rebuild`, `RunIteration`) runs on `Root` with egglog's semantics, so every M8 test keeps its meaning:
- `EGraph.RunRules` flushes the root's bridge-side host writes and does not rebuild first (egglog's `run_rules` never does); `EGraph.FlushUpdates` leaves a recorded failure for the next run;
- `EGraph.AddValues` and `AddTerm` stage straight into database buffers stamped with the current clock, as M8 and egglog's `add_values` do, then flush: rows replayed as given need no catch-up at the root, so nothing of the batch is kept bridge-side (§10.4, §14.1);
- `EGraph.ClearTable` is the root context's `ClearTable`, which also drops the rows of the function staged on the root's bridge side (§10.4).

Only the `EGraphContext` API flushes, ensures clean and throws `PanicException` itself.

**`EGraphContext`** (`Bridge/EGraphContext.cs`, partials `.Run.cs`, `.Watermarks.cs`, `.Writes.cs`, `.Reads.cs`, `.Freeze.cs`, `.Locals.cs`, `.Invariants.cs`, `.Extraction.cs` (§10.10), and `.Lift.cs` in M10.4) holds:
- `owner`, `parent` and `context` (the storage `Context`);
- the rule progress map (§10.2), `HostWrites hostWrites` (`Bridge/HostWrites.cs`) and `object?[] locals`;
- from M10.4, `List<SplitRecord>? splitRecords` and `MemberMap? memberMap` (the map carries the overlay version it was built at), both `null` until first use.

Its properties forward to storage: `Version`, `CreatedAt`, `IsFrozen`, `IsStopped` (= `context.IsContradicted`), `IsDisposed`, `IsFaulted`, `Parallelism` (the context database's heuristics, §10.9) and `OwnRowCount` (`context.Database.TotalSizeEstimate`, rule-layer D42). `Parent` is the parent's bridge context, `null` at the root. **`CreateChild(ContextOptions?)`** and **`Dispose()`** (rule-layer E7) have the storage semantics of §8.2: `CreateChild` of a context that is not frozen ensures it clean first (§10.3), and a failure recorded on this context by that flush, the rebuild or the root's settle, or before the call, is taken right after: the new child is disposed and `PanicException` is thrown. `Dispose` releases the host writes, the progress and the locals. `RunRules` reports at the e-graph's `ReportLevel` (E7).

**Other bridge types:**
- `ContextState` is dropped (storage flags suffice).
- `ContextLocalSlot<T>` (`Bridge/ContextLocalSlot.cs`): `GetLocal<T>(slot)` builds the context's value with the slot's factory on first use and installs it under the context's locals gate, so concurrent first calls keep one value. Scheduler collect-matches buffers live there (map §8.1 row 10).

### 10.2 Watermarks (C7, D3, D4, D18, X41)

```csharp
internal readonly struct RuleProgress { public RuleProgress(uint lastRunAt, uint[]? constants, int generation); uint LastRunAt; uint[]? Constants; int Generation; }   // Bridge/RuleProgress.cs
internal sealed class RuleTable { RuleId Add(Func<RuleId,int,RuleInfo> create); void Remove(RuleId id); bool TryGet(RuleId id, out RuleInfo info); }   // Bridge/RuleTable.cs
```

- **`RuleTable`** is written under a lock and read lock-free: its slot array is copied only when it grows, slot writes are volatile, and reusing an id bumps the rule's generation. Copying on every change would cost O(n²) when the rule layer defines one rule per query.
- **Progress per context** is a small open-addressed map from rule index to `RuleProgress` (`EGraphContext.Watermarks.cs`: parallel key and value arrays, a power-of-two capacity of at least 8, at most half full), not an array indexed by rule id: a context pays for the rules it ran, not for every rule the e-graph defines (§14.2). It is written on the domain thread only (`SetProgress`, which `Commit` calls) and read by descendants, which share the domain or read it frozen.

**`RuleInfo` changes:**
- drop `LastRunAt`;
- add `int Generation`, `planGate`, a volatile `cachedPlan`, and `GetOrBuildCachedPlan(Database db)`, double-checked under the gate;
- add `bool AllowInheritance`, computed once with the cached plan: false for naive, split and callback rules (D4, D21), and false unless `ActionReadAnalysis.IsQueryDetermined(cached.Actions, store.Schema, externals)` holds;
- keep a read-only `LastRunAt`, which reads the root context's committed progress, and a getter-only `CachedPlan`, so M8's tests that read root watermarks keep their meaning.

**`ActionReadAnalysis`** (`Query/ActionReadAnalysis.cs`, static). An action program is *query-determined* when what it writes depends only on the match and the context's equalities, not on table contents that can change in a descendant. It is query-determined iff every instruction is one of:
- `Insert`, `InsertIfEq`, `Remove`, `AssertEq`, `AssertNe`, `AssertAnyNe`, `ReadCounter`;
- `LookupOrInsertDefault` on a table whose merge function is `UnionIdMerge`, or a bridge constructor (a `FunctionTableMerge` over a union-id merge, `LayeredTableSchema.IsConstructor`), when the instruction reads a key column or the union column, not a subsumption or timestamp cell (a constructor: the row exists after the ancestor's run, and its value changes only by unions, which descendants see through `find`);
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

- `TryOwn` returns false when the map holds no progress for the rule, or holds another generation's.
- Reading an ancestor's `progress` is safe: the ancestor is frozen (immutable) or shares this domain.
- **Commit (X18):** `(T, K, generation)` is staged per rule at instantiation and committed when `RunRuleSet` returns and `Database.TakeFailure()` is `null`.
- An inherited watermark is at most `this.CreatedAt` (the first `bound`), which `SemiNaiveFloor` relies on (§7.2).

### 10.3 `RunRules(rules) → IterationReport`

```text
using context.Domain.Enter(); ThrowIfNotWritable()
if IsStopped: return IterationReport.Stopped
EnsureClean()                                           // EGraph.RunRules: flush the root's host writes only (§10.1)
if IsStopped: return Stopped
plans = [rules.TryGet(r).GetOrBuildCachedPlan(database) for r]   // before the epoch: a bad id changes nothing
T = context.BeginEpoch(); context.NoteRuleRun(T); ufBefore = context.DisplacedCount
rsb = database.NewRuleSet(); staged = []
for r: K = CanonicalConstants.Compute(database.View, cached.IdConstants, cached.Actions.IdConstants)
       W = r listed earlier in this run ? (same K ? T : 0) : GetWatermark(r, info, K)   // O(1) per rule
       info.Query.AddRulesFromCached(rsb, W, cached, K); staged.Add((r, T, K))
using rs = rsb.Build()                                  // snapshots the views (§9.1)
rep = database.ExecuteRuleSet(rs, level, T)             // an exception: nothing staged survives, nothing committed;
                                                        // a child is faulted (X12)
rep = rs.IsEmpty && IsRoot ? MergeAll(T) : database.MergeRuleSet(rs, rep, T)   // merges at T (see below)
failed = TakeFailure(out m)
if !failed: commit staged progress                      // X18
if IsStopped: context.EndEpoch(); if failed: throw PanicException(m); return Stopped report
if context.DisplacedCount == ufBefore: context.EndEpoch(); if failed: throw PanicException(m); return (rep, RebuildChanged: false)
rb = context.Rebuild()                                   // the first pass reads the clock again (X4)
if failed: TakeFailure(); throw PanicException(m)       // a failure the rebuild records belongs to the same iteration
if TakeFailure(out m2): throw PanicException(m2)
return (rep, RebuildChanged: rb, Stopped: IsStopped)
```

- **Panics** are thrown after the iteration's bookkeeping (the clock advance, or the rebuild), not right after the merge as in egglog: map §8.1 row 22, which M8 fixed and the bridge keeps. No progress is committed after a recorded failure.
- **An exception while the run's writes merge** cannot be undone: progress is committed unless a failure was recorded, so no match is applied twice; a child is faulted anyway (X12). An exception in the execute phase commits nothing and does not advance the clock.
- **An empty rule set at the root** (every variant pruned: no visible row at or after any rule's watermark) still merges what is staged and not merged at T. egglog's `run_rule_set` returns before its `merge_all` in that case (`core-relations/src/free_join/execute.rs:349`), yet `run_rules` records T as every rule's `last_run_at`; a raw row staged before two such runs then lies below every watermark when it finally merges, and no rule ever matches it, at the root (I31 Case 1) or in a child that inherits the root's watermarks (Case 2). Map §8.1 row 26. A child needs none of this: it is clean before it runs and stamps rows at the merge (X38).
- **A rule listed twice** runs its second listing with T. The earlier listing is found by scanning back over at most 16 listings, or through a dictionary for longer lists, so a run stays O(rules).

**`EnsureClean()`** (X42):
```text
if parent is not null && !parent.IsFrozen: parent.EnsureClean()
if hostWrites nonempty: FlushUpdates()
if IsRoot: if context.DisplacedCount != context.journalMark: context.Rebuild()   // pending staged writes merge inside the run, as in egglog
else: context.Rebuild()
```

- At the root, writes staged through `WithExecutionState` or `Database.NewBuffer` and not flushed stay pending into the run and merge in its `MergeAll` at T, as in egglog's `run_rule_set`, and also when the run's rule set is empty (above). At rest the root's journal mark is always current (every root operation that grows the union-find rebuilds), so `EnsureClean` adds no pass and no epoch at the root. The one way left to stamp such a row below a later watermark is the internal `EGraph.IncrementTimestamp` (egglog's `inc_ts`), which advances the clock without merging; only a test uses it.
- `IterationReport` gains `bool RebuildChanged`, `bool Stopped` and `int RebuildPasses`. `Changed` stays the rule application's own change (decision 11), which includes what an empty run's merge changed.
- `Saturate(rules, max)` loops until `Changed`, `RebuildChanged` and `Stopped` are all false, or the limit is reached. It returns `SaturateReport { Iterations, Stopped, HitLimit }` (`Bridge/SaturateReport.cs`).

### 10.4 Host writes (X24, X38)

- **`HostWrites`** keeps logical rows per table (`uint[]` arenas without the ts and subsume cells), union pairs, and per row whether its id cells are canonicalized at the replay. Arenas up to 4096 cells and 1024 rows or unions are kept across flushes, so a warm context stages its usual batches without allocating; larger ones are released when the flush clears them, so a long-lived context keeps nothing proportional to its largest batch.
- **Staging** (the `EGraphContext` API):
  - `StageRow(f, row)` appends the row;
  - `StageTerm(f, key)` mints `id = (uint)database.IncrementCounter(IdCounter)`, which comes from a block in a child, then stages the row and returns id;
  - `StageUnion(a, b)` stages the pair;
  - `FreshId()` mints directly;
  - `AddValues(rows)` stages each row as it validates it and, when one is malformed or refused (decision 2), drops the rows the call staged (`HostWrites.TruncateRows`), so nothing is staged; then `FlushUpdates()`. `AddTerm(f, inputs)` is `id = StageTerm(...)`, `FlushUpdates()`, then returns `Find(id)`.
  - Decision 2 is checked when a row is staged, so a refused write never faults the child at the merge.
- **`ClearTable(f)`** clears first (refusals per §6.7 change nothing), then drops the rows of `f` staged here (`HostWrites.RemoveRows`): the operations take effect in the order issued, as egglog's `clear_table` drops what in-flight buffers staged for the table. Rows of other functions and staged unions are kept.
- **`FlushUpdates()`:**
  ```text
  using Domain.Enter(); ThrowIfNotWritable(); if IsStopped: hostWrites.Clear(); return false
  if Parent is not null && !Parent.IsFrozen: Parent.EnsureClean()
  context.PrepareForWrite()                 // catch-up BEFORE anything is staged
  T = context.BeginEpoch(); ufBefore = DisplacedCount
  st = database.CreateEngineState(T); hostWrites.ReplayInto(st, T, context.UnionFind); hostWrites.Clear()   // ts cell = T, subsume cell = NOT_SUBSUMED
  st.Flush(); database.RecordFailure(st); changed = database.MergeAll(T); context.EndEpoch()   // egglog flush_updates
  if DisplacedCount != ufBefore: context.Rebuild()
  ThrowIfFailed(); return changed
  ```
  An exception escaping a child's flush faults it (X12). The state is created only when something is staged.
- `ReplayInto` canonicalizes the Id columns of rows staged through the `EGraphContext` API at every level (`UnionFind.Find` is current after `PrepareForWrite`); this gives the rule layer the spike's canonicalizing `Insert` (rule-layer invariant C). The egglog-compatible `EGraph` root API does not stage here at all (§10.1): its rows go straight into database buffers as given, so the root matches egglog; in a child, rows are canonicalized at the merge anyway (X38).
- **Order of the staged and the raw write paths.** Writes land in the order the host issued them. Before a raw path stages into database buffers of its own (`EGraph.AddValues`, `EGraph.AddTerm`, `EGraph.WithExecutionState`, the internal raw iteration `EGraph.RunIteration` and `EGraph.RunRuleSet` over it, and `EGraphContext.WithExecutionState` in any context), the context flushes whatever it staged earlier through the `EGraphContext` API (rows, terms and unions in `HostWrites`), in an epoch of its own, so an order-dependent merge (`:merge new`, `:merge old`) sees the earlier write first; a raw row staged before a context-API write already merges first, since the flush replays `HostWrites` after the pending buffers. When nothing is staged bridge-side, as under egglog's API alone, this is one emptiness test and the timestamp sequence is egglog's; mixing the two paths costs one extra epoch per switch. The flush comes before the raw path reads its own input, so an `EGraph.AddValues` refused because a row is malformed has still merged what was staged before it, while nothing of the refused batch is published; the next flush would have merged those writes anyway, and checking the batch first would take a second pass over it. In a child the flush is the child's own `FlushUpdates` (catch-up first, X24); only context-private tables can have an order-dependent merge there (decision 2), so for the rest it changes epochs, not results. Writes issued from inside a raw escape's action are refused rather than ordered (§8.3): the action's rows reach the pending buffers only when it returns, after anything the action staged through the context API.

### 10.5 Reads

`Find`, `TryLookup(f, key, out ret)` (Id key columns canonicalized with `Find`), `ContainsKey`, `LookupId`, `TableSize`, `ForEach` and `IsContradictory` all go through `ThrowIfNotReadable`:
- in a child they use `context.GetView(t)` / `TryGetRow`;
- at the root, the root path. Root reads do not throw for writes staged and not yet flushed; they see the committed rows, as egglog does (X42).

Bridge `IsClean` is `context.IsClean && hostWrites.IsEmpty && (Parent?.IsClean ?? true)`, where the recursion stops at frozen ancestors.

### 10.6 Contradiction early stop and panics

- **`StopOnContradictionFunction`** (`Bridge/StopOnContradictionFunction.cs`): if `state.View?.Owner.Owner is { Options.StopOnContradiction: true }`, it calls `state.TriggerEarlyStop()`.
- The bridge appends `External(StopOnContradiction, [])` after every head insert into the contradiction function when the rule is lowered (planned), not when the builder records it: a rule planned before `DesignateContradiction` keeps its plan without the early stop, and its insert still sets the stop flag when it merges. The serial executor runs actions in 128-lane batches, so the early stop cuts a search only once a batch has run. The storage flag is set when that row merges (§6.9).
- `PanicFunction` and `LazyPanicFunction` call `state.ReportFailure(message)`, never a field closed over the `EGraph`, so failures stay per context. For a root or bare state (`InChildContext` false) they also set the e-graph's panic side channel, which M8's tests read; a child's panic never reaches it.
- `EGraph.NewPanic` is thread-safe.

### 10.7 Containers (decision 9, C13)

- **Registration:** `ContainerValues.RegisterType<T>(CounterId, ContainerMergeFunction, bool holdsIds = true)`. `ContainerEnvBase.HoldsIds`. Containers over base values register with `holdsIds: false`.
- **New members:** `ContainerValues.HasEqContainers`, `ContainerValues.AnyEqContainerMentions(uint)`, `ContainerValues.EqVersion`, `ContainerEnvBase.AnyContainerMentions(uint)`, `ContainerEnvBase.EqVersionCounter` and `ContainerValueIndex.ContainsElement(uint)`. The storage members exist since M9.4, because G-a and G-b are M9.4 content; the bridge sets `holdsIds` when it registers containers.
- **G-a:** `ContainerEnv.GetOrInsert` throws `InvalidOperationException` when `HoldsIds && state.InChildContext` (§6.5). `TryGetId` stays allowed.
- `EGraphContext.GetContainerValue` registers the container type on first use only at the root; a child requires it registered first, since registration is a definition of the e-graph.
- **G-b, G-c:** §7.4.

### 10.8 Freeze (bridge part)

`EGraphContext.Freeze(IReadOnlyCollection<EGraphRuleId>? planRules = null)` (rule-layer E10):
1. `FlushUpdates()`, when host writes are staged (an empty flush would only spend an epoch; raw pending writes are merged by the storage freeze's rebuild).
2. `Rebuild()`.
3. Take a recorded failure and throw it as `PanicException`; the context stays unfrozen and may be frozen again.
4. Plan, on this context's statistics (§9.7), every live rule without a cached plan when `planRules` is `null`, or exactly the given rules that have none.
5. `context.Freeze(FreezeWarmup.From(cached plans, schema))`.

### 10.9 Concurrent children and the parallelism budget (C9, M11 parts the colors need)

- **Default parallelism:** a child of a frozen context uses `ParallelHeuristics.Serial`. A child of a non-frozen context inherits its parent's heuristics, since they never run concurrently. `ContextOptions.Parallelism` overrides both. Under inherited heuristics a child's rule set can go parallel once its own rows pass `DbLevelThreshold`; layer creation from those tasks is safe (X37).
- **`ParallelismBudget`** (`Concurrency/ParallelismBudget.cs`): `Total`, `Available`, `Lease(int)` (blocks), `TryLease`, `Release`; `0 ≤ Available ≤ Total`. A request outside `[1, Total]` can never be granted and throws `ArgumentOutOfRangeException` instead of blocking; releasing more than was leased throws `InvalidOperationException`. One private lock with `Monitor.Wait`/`PulseAll`; waiters are not served in arrival order (a cold path: one lease per child body). `EGraph.ParallelismBudget` has the root's degree of parallelism.
- **`ParallelismLease`** (`Concurrency/ParallelismLease.cs`): `IDisposable` readonly struct; `default` holds nothing.
- **`ConcurrentChildren.Run(children, body)`** (`Bridge/ConcurrentChildren.cs`), with a `body(child)` and a `body(child, index)` overload:
  - before running anything, requires every child's parent to be frozen and no child to be an ancestor of another, and refuses a null, repeated, disposed or root child and a child of another e-graph;
  - runs `Parallel.For` with `degree = max(1, Total / widest)`, each body under a lease of `min(child DOP, Total)`, where `widest` is the largest such lease;
  - a run nested in a body (the thread holds a lease already, tracked per thread) runs its children serially under that lease: leasing again could block forever on a budget the outer run holds;
  - rethrows a single inner exception unwrapped; several stay aggregated.

### 10.10 Extraction over a context (rule-layer E5)

The M8 extractor (`Extraction/Extractor*.cs`, Knuth's algorithm with D10's content ties) reads the root only: discovery probes `Database.GetTableInfo(t)`, and an extractor goes stale on any clock tick. Rule-layer E5 asks for extraction in a context, with staleness keyed on what the extractor reads. Every scoped Cosmos scenario extracts, so E5 gates the whole R2 scoped suite.

```csharp
public static Extractor CreateOnDemand(ExtractionSchema schema, EGraphContext context, CostModel? costs = null, ExtractionOptions? options = null);   // Extraction/Extractor.cs
public EGraphContext Extractor.Context { get; }            // the context it reads; the root for the root forms
internal ExtractionQueryScope EGraphContext.EnterExtraction(bool requireClean);   // Bridge/EGraphContext.Extraction.cs
internal sealed class ExtractionWatch { ExtractionWatch(Context context, ReadOnlySpan<TableId> tables); int LevelCount { get; } int FindChange(); }   // Extraction/ExtractionWatch.cs
internal readonly record struct ExtractionTableMark(int Generation, int Rows, int Live, int Structure);   // Extraction/ExtractionTableMark.cs
internal readonly struct ExtractionQueryScope : IDisposable;   // Extraction/ExtractionQueryScope.cs
```

**Semantics.** The context's extraction is the extraction of the context's view: classes are canonical in the context, and a class's nodes are the rows of its producing functions that the context sees (I12). Costs, ties and variants are the root extractor's, so a child's extraction equals that of a fresh root holding the child's view, up to the renaming of ids, and, where costs tie, up to D10's id-dependent order. The root forms (`CreateOnDemand(schema)`, `ComputeCostsFromRootSorts`) are unchanged in result; at the root the context form reads what they read and chooses the same terms.

**Creation.**
- The context must belong to the schema's e-graph (`ArgumentException`).
- `EnterExtraction(requireClean: true)` enters the context's domain unless it is frozen and calls `ThrowIfNotReadable` (§10.5): disposed, faulted, stopped or not clean throws. A clean view is canonical (I35), which discovery relies on.
- The extractor records `host` (the context), `layers` (`Context.Tables`, null at the root) and `finder` (`Context.UnionFind` in a child), and builds an `ExtractionWatch` over its slots' tables.

**Canonical values.** A queried value is canonicalized once per query: `finder.Find` in a child, the root's `FindNaive` at the root. Nodes are never canonicalized: a clean view's rows already are.

**Discovery** (`Extractor.Discovery.cs`). `Settle` tests `layers` once and calls one of two loops:

```text
DiscoverAtRoot:     for each queued class (v, sort), each producing slot f:
                        rows = rootDb.GetTableInfo(f.Table).GetColumnIndex(f.Output).TryGetSubset(v)
                        f.Table.ScanGeneric(rows, sink)                                  // as in M8
DiscoverInLayers:   for each queued class (v, sort), each producing slot f:
                        tv = layers.GetView(f.Table)                                     // never Database.GetTable in a child
                        for each present layer lv of tv:
                            rows = lv.Info.GetColumnIndex(f.Output).TryGetSubset(v)      // the layer's own cached index
                            lv.Table.ScanGeneric(rows, lv.Hidden, sink)                  // hidden-aware scan (§9.3)
```

- Lemma F does not apply here: discovery looks rows up by their output column, not by their key, so every layer is probed and hidden rows are filtered row by row. A hidden ancestor row carries information the view holds elsewhere or retracted (I14), in the layer of the level that hid it: a row that level replaced, subsumed or re-stamped (`KeyHeld`, `Restamped`: the replacement, subsumed if it was), one it deleted (`Retracted`: nothing replaces it), or one that names one of its losers (`Mentions`: the canonical copy). Skipping hidden rows reads each visible row exactly once.
- A frozen layer's cached index is shared and built lock-free on first use (§9.5, X14); a writable ancestor's is refreshed on the domain thread, as a rule run refreshes it.
- Container elements and base values are read from the store-wide `ContainerValues` and `BaseValues` (§1 item 1). G-a and G-b keep every eq-sort container's elements canonical in a clean child.

**Staleness** (`ExtractionWatch`). Keyed on the extraction tables and the union-find of the context and its non-frozen ancestors, never on the clock:

```text
levels = the context, then its ancestors up to the first frozen one       // frozen levels are a prefix of the chain (§8.1)
for each level L, each slot table t:
    root:  mark = (t.Version.Major, t.Version.Minor, t.Count, 0)
    child: layer = L.Tables.TryGetLayer(t); mark = layer is null ? default
                 : (layer.Own.Version.Major, layer.Own.Version.Minor, layer.Own.Count, layer.Version)
for each level L: uf = L.UnionFindTable.Version                             // root DisplacedTable, child journal
FindChange (each query): the first level whose uf or one of whose marks moved → stale
```

- **Why it suffices.** Within a generation a table's rows only grow and lose rows only to tombstones, so an insert, merge, rewrite or delete moves `Rows` or `Live`; compaction and clear move `Generation`; a hide or a clear of ancestor rows moves the layer's `Version`. Every union moves the level's union-find (the root's row count, a child's journal, which also takes the parent displacements it imports). Frozen levels never change. So any change to the rows the extractor reads, or to their canonical form, moves a mark of a watched level.
- **What does not invalidate.** Writes to other tables, at any level; host writes staged and not merged; flushes with nothing staged; batches that restate rows already there (an empty layer that a no-op write created marks as `default`, as an absent one); rule runs, queries and `EnsureClean` that change none of the above, although they tick the clock (rule-layer D49). Queries therefore do not require the context clean again: a non-frozen ancestor's write to an unrelated table leaves the child behind its parent, yet the rows the extractor reads, and their canonical form, are as they were.
- **Conservative cases.** A compaction (a merge's, or a freeze's) renumbers rows without changing them and still counts as a change. A watched level that freezes later stays watched; its marks no longer move.
- **Messages.** A union-find change throws `InvalidOperationException("The e-graph changed since the extractor was created; create a new one.")`; a table change names the function.

**Threading.**
- Each query (`Enter`) of the context form enters the context's domain while it is not frozen, and marks it as running a caller's code (`EnterCallback`): a second thread fails fast, and a cost function may read the context but its writes, rebuilds and structural calls are refused (§8.3). `ExtractionQueryScope` exits the callback and the domain also when the query throws. The root forms hold nothing, as egglog's root does not.
- On a frozen context the watch is empty, finds are read-only (§5.4), views are cached thread-safely (§6.3) and layer indexes are built thread-safely. Any number of extractors may run at once, one per thread; a single extractor stays single-threaded, because queries extend its caches.

**What the root pays.** Per query, the watch reads O(slots) table versions and one union-find version, where M8 read the slots' versions and the clock. Per node, nothing: discovery tests `layers` once per `Settle`, and canonicalization tests `finder` once per queried value.

**What a child pays.** Per class discovered, one index probe per present layer of each producing function's view, plus the rows found; per query, O(watched levels × slots) version reads. The first probe of a layer's column builds that layer's index.

**Freeze and cached indexes** (engine defect fixed). `Database.Freeze` compacts every table with tombstones (`ForceRehash`) after the last merge has reset the indexes, but a search that merged nothing, or an extraction, may have refreshed an index since. That index kept the old row ids, and a frozen catalog is never reset again, so a descendant's probe (a rule's, or a child's extraction) read renumbered rows; DEBUG builds caught it as "a refreshed index lags its table". `Database.Freeze` now re-arms the indexes of every table whose generation the freeze moved, before the warm-up.

**Tests** (`Extraction/`):
- `ContextExtractionTests`: a child sees its parent's terms, under a frozen or a writable parent; child-only terms are seen in the child and below, not at the root or in a sibling; a class merged in the child extracts the child's cheapest member, whichever side wins; rows a child subsumes or deletes under a frozen parent are not extracted; staleness (unrelated tables and no-op batches and runs keep an extractor current; a relevant write, union or subsumption in the context or in a non-frozen ancestor makes it stale; frozen ancestors are not watched; a clear in a child invalidates); creation and query checks (other e-graph, not clean, another thread, a cost function that writes, disposal); the context form at the root matches the root API on random graphs; the freeze-compaction index regression; and eight threads extracting from a frozen context and a frozen root at once.
- `ContextExtractionDifferentialTests` (with `ContextExtractionProgram`, `ContextExtractionWorld`, `ContextExtractionStep` and `ContextExtractionStepKind`): 400 random programs over chains of depth 1 to 3, with frozen and writable parents, writes to a writable parent after its child was created, unions and saturating rules. The deepest context's extraction equals that of a fresh root replaying the chain: classes correspond one to one through the program's terms, costs are equal, and each side's term, rebuilt in the other e-graph, is a term of the matching class.

---

## 11. Lift primitives and split hooks (L1, L2, L4, L5)

All files are in `Cases/`, one type each. The context reads they use are in `Bridge/EGraphContext.Lift.cs`: §6.10's reads by `FunctionId`, `ScanMerges`, `IsLocalId`, `GetMemberMap`, and the split-record storage.

### 11.1 Member maps (L2, G1(a), X29)

```csharp
internal sealed class MemberMap
{
    public static MemberMap Build(EGraphContext k);      // from UnionFindOverlay slots: members not local, ascending, per canonical
    public int GroupCount { get; }  public ReadOnlySpan<uint> GroupRoots { get; }   // ascending
    public ReadOnlySpan<uint> Members(in uint caseCanonical);   // group; else [c] if c is not local; else empty
    public ReadOnlySpan<uint> GroupMembers(int group);  public int IndexOf(uint root);  public bool IsLocal(uint id);
    public int MemberCount { get; }  public int Version { get; }
}
```

- `Members` takes its argument by `in`. The one-element answer `[c]` is then a span over the caller's variable, so nothing is allocated per lookup.
- A group is a class with at least one member that is not local. A class whose members are all local stands for no parent id and is not a group.
- `GetMemberMap()` is cached while `UnionFindOverlay.Version` is unchanged.
- `ClassPartition.Meet(MemberMap seed, IReadOnlyList<EGraphContext> live, int seedIndex, List<uint[]> blocks)` returns the number of blocks it appended. It:
  - builds, for each seed group, each member's signature `(K_j.Find(m))` over the cases j ≠ seed;
  - sorts the members by signature (`MemberSignatureComparer`, a struct comparer over pooled scratch, so the sort allocates nothing);
  - emits every run of length ≥ 2 as a block.

### 11.2 The lift

**Types:**

| Type | Shape |
|---|---|
| `LiftTableKind` | `Relation`, `Constructor`, `Function` |
| `LiftTable` | `(FunctionId, LiftTableKind, Func<uint,uint,uint>? caseJoin)` |
| `LiftSpec` | A list of `LiftTable`, each function at most once. `HasSameTables(other)` compares content |
| `LiftedTable` | A `LiftTable` resolved against the definitions for one lift: the key layout, which key cells are ids, and whether the output is one |
| `LiftOptions` | `RoundLimit = 16`, `CandidateLimit = 64` |
| `LiftReport` | `Rounds`, `LiveCases`, `VacuousCases`, `ParentContradicted`, `Unions`, `Rows`, `Terms`, `Values`, `Skipped`, `HitRoundLimit`, `Changed` |
| `LiftSignature` | The versions of P, of P's non-frozen ancestors and of each case; the identities of P and the cases; and the spec lifted. `Matches(P, cases, spec)` allocates nothing |
| `LiftCandidates` | A deduplicating key arena. A reset costs what the arena held, not its capacity |
| `LiftKeySink` | `IRowSink`, with an optional winner filter |
| `LiftWrites` | Pending unions, rows and terms |
| `Lifter` | `Lifter.cs`, `Lifter.Equalities.cs`, `Lifter.Facts.cs` |

**Resolving the spec.** Each table is checked when the lift runs. A failed check throws `ArgumentException` before anything is written:
- the function must be declared;
- a `Constructor` must have an id output;
- a `Relation` must not have an id output: its output is written as the cases hold it, and the seed's id could be local to the seed (I10);
- a case join is allowed only for a `Function` with a base output.

The contradiction function is skipped, because L4 writes it. In a child parent, a table the parent cannot write (a merge that is not a lattice merge, decision 2) is left out: no case can hold rows of it either.

**`Lifter.Lift(P, cases, spec, options, ref memo, ct)`:**

```text
throw if cases are empty, listed twice or not children of P; P is frozen, stopped, faulted or disposed; or the spec is invalid
enter P's domain (the cases share it); throw if inside a callback of it
loop:
  ct.ThrowIfCancellationRequested()
  P.Rebuild(); each case not stopped: Rebuild()
  if first round && memo matches (P, cases, spec): return (Changed = false, ParentContradicted = no live case)
  memo = null
  live = cases not IsContradictory; VacuousCases = |cases| - |live|                    // L4
  if live empty: ContradictParent(P); ParentContradicted = true; break
  maps = live.GetMemberMap(); r = argmin GroupCount
  ClassPartition.Meet(maps[r], live, r, blocks); for each block: union block[0] with each other member   // equalities
  for h in the resolved spec:
     sources: live[r].ScanOwn(h) keys                                                         // (a)
            ∪ for each group root c of maps[r], not local: live[r].ScanAncestorsMentioning(h, c) keys mentioning c   // (b) L1
     candidates: expand the Id key cells through maps[r].Members (skip a source if any list is empty; if the
                 product > CandidateLimit, skip it and add the product to Skipped)
     for each key that every live case holds and P does not:
       Relation    → every live case holds the same output: a Row with it
       Constructor → the first parent id that every live case's output stands for (y with K.Find(y) == v_K for every K):
                     a Row with it; none: a Term (a fresh id, whose equalities the next round's meet lifts)
       Function    → Id output: a common parent id as above: a Row with it; none: nothing
                     base output: fold with caseJoin, or require all values equal: a Row (counted in Values)
     a key that P holds already is skipped, except for a base-output Function whose joined value differs from P's,
     and an id-output Function where some live case's output is not its class of P's stored output
  if writes empty: break
  apply: StageUnion, then StageRow (NOT_SUBSUMED), then StageTerm; v0 = P.Version; P.FlushUpdates(); rounds++
  if P.Version == v0: break
  Changed = true; if rounds == RoundLimit: HitRoundLimit = true; break
P.Rebuild(); cases.Rebuild()
memo = settled ? LiftSignature.Of(P, cases, spec) : null   // settled: not HitRoundLimit, and the last round skipped nothing
```

Notes on the lift:
- **A key P already holds, with an id output** (a constructor, or an id-output function whose every live case holds its own class of P's stored output), writes nothing. Under a `UnionId` or `AssertEq` merge that is always so: every case sees P's row, so the case's output is that id's class there, and any equality between it and another parent id is the meet's. Writing a second id next to P's would merge two ids in one flush before the meet's union applies, and an `AssertEq` merge refuses that. Any other merge on an id output (a declared lattice over ids, which decision 2 lets cases write) can move a case's output off the stored class; when every live case does, and they share a common parent id, that id is written over P's stored output, as a base-output function writes its joined value. It is never P's class of the stored output, since each case's find extends P's.
- **ContradictParent** writes the e-graph's contradiction function, if one is designated and P does not see it yet. The output is the function's constant default; else, for an id output, a term (a fresh id of P, since a case's output can be local to it, I10); else the base value a vacuous case merged into its own layer. A `CaseSet` keeps that value from the first vacuous case it disposes that holds one (`Lifter.TryReadContradictionOutput`), so a lift after every case was disposed writes the same contradiction as a lift over the undisposed cases. `ParentContradicted` is reported also when no contradiction function is designated; nothing is written then.
- **The memo** is keyed on everything a lift reads, the spec included, since a lift of other tables reads other rows. Specs compare by reference, then by content, so a host that builds an equal spec for each call still hits the memo. The limits are not part of the key. A lift whose answer could depend on them (cut short by `RoundLimit`, or whose last round skipped rows over `CandidateLimit`) has not settled and leaves no memo; a settled lift's answer is the same under any limits. A lift that throws part-way leaves no memo either.
- The lift holds P's domain for its whole run and calls no caller code but the case joins.

**`CaseSet`** (`Cases/CaseSet.cs`, rule-layer E8):
- `CaseSet(P)` refuses a frozen or disposed parent.
- `AddCase(ContextOptions? options = null)` calls `parent.CreateChild(options ?? ContextOptions.Case)`. The options must set `IsCase` and `StopOnContradiction` (`ArgumentException` otherwise).
- `RunEach(ICaseSchedule schedule, int depth, bool allowSplits)` rebuilds each case. A contradictory case is disposed at once (its split records first) and counted in `VacuousCount` (L4); every other case runs the schedule. It returns whether a schedule reported a change.
- `Lift(LiftSpec spec, LiftOptions options, CancellationToken ct = default)` calls `Lifter.Lift` with the set's memo and one `Lifter` kept for the set's life; the token is checked between rounds. When every case was disposed as vacuous, the parent is contradicted (L4), with the base output the set kept when it disposed them if the contradiction function needs one, and `ParentContradicted` is reported. With no case, and none found vacuous, it throws `InvalidOperationException`.
- `DisposeVacuous()` disposes the contradictory cases and returns their number. It serves host-driven lifts, which never call `RunEach`.
- The set owns its cases. A case disposed outside the set was not found vacuous, and without it the remaining cases do not cover the parent. `RunEach`, `Lift` and `DisposeVacuous` therefore refuse such a set (`InvalidOperationException`); only `Dispose` is allowed.
- `Dispose()` disposes in reverse creation order, each case's split records first.
- `IsDisposed`, and `Cases`: the cases in creation order, without those disposed as vacuous. A case disposed outside the set stays listed, disposed, until the set is disposed.

### 11.3 Split hooks (L5, B6)

**Types** (rule-layer E8):

```csharp
interface ISplitSource { void FindRequests(EGraphContext scope, List<SplitRequest> requests, SplitReport report);
                         void Assume(EGraphContext @case, in SplitRequest request, int caseIndex); }
readonly record struct SplitRequest(SplitKey Key, int CaseCount, object? Payload);
readonly struct SplitKey : IEquatable<SplitKey> { SplitKey(int source, uint[] values); }
interface ICaseSchedule { bool Run(EGraphContext @case, int depth, bool allowSplits); }
interface ISplitDemand { bool Wants(EGraphContext scope, in SplitRequest request); }
sealed class SplitOptions { int DepthLimit = 4; int CaseLimit = 16; LiftOptions Lift; ContextOptions CaseOptions = ContextOptions.Case; }
sealed class SplitReport { int CasesOpened; int SplitsSkipped; int Lifts; long LiftRowsSkipped; }
sealed class SplitRecord { SplitKey Key; SplitRequest Request; ISplitSource Source; CaseSet Cases; int Depth; SplitDriver Driver; bool IsLive; }
```

`SplitOptions.CaseOptions` must set `IsCase` and `StopOnContradiction`; through it, `InheritWatermarks` reaches the cases.

**`SplitDriver.Step(scope, depth, report)`** refuses a frozen or faulted scope. It returns false at once for a stopped scope or at `depth ≥ DepthLimit`; otherwise it rebuilds the scope, and returns false if the scope is then stopped or contradictory. Then:
1. Find the split requests of every source.
2. Skip a request covered by a live record in the scope **or any ancestor** (B6): a record of the same source object (compared by reference, whichever driver opened the record) over the same key cells; or one whose key was taken earlier in the same step. A `SplitKey`'s source index numbers the sources of one driver only, so it is not compared across drivers.
3. Skip a request over the limits: more than `CaseLimit` cases, or fewer than one.
4. Skip a request that `ISplitDemand` rejects. Demand is checked before any case exists.
5. Open the cases. The record is registered on the scope first, so a failure while opening leaves it for `DisposeRecords`; then each case is created with `CaseOptions` and assumed by its source.
6. **Phase A:** for every live record of the scope **that this driver opened**, in creation order (those its earlier steps opened as well as those just opened): `RunEach` with nested splits disabled, then lift, then `DisposeVacuous`. This is lift before deepening (TheSy). An earlier record's cases have caught up with the scope's new facts, so what they now derive is lifted, and a case those facts make vacuous is dropped (rule-layer §6.9.3, D43). The memo makes an unchanged record cost one signature check.
7. **Phase B:** while `depth + 1 < DepthLimit`, for every such record, run the in-case schedule again in each live case with nested splits enabled (which recurses into `Step` at `depth + 1`), and lift again when that changed something.

Several drivers may serve one scope (the rule layer builds one per split node, rule-layer §6.9.3). Each record names the driver that opened it, and only that driver runs its in-case schedule in the record's cases and lifts them with its spec; the drivers share coverage (step 2) only through shared source objects.

A lift that contradicts the scope ends the step. `Step` returns whether it changed the scope: it opened cases, or a lift changed the scope. In-case work alone is no change, so a schedule that repeats the step until nothing changes ends.

The driver never holds the scope's thread domain while it calls a source, the demand or the schedule, which may create children (nested splits, §8.3). Exceptions from a source or the schedule propagate, and the records opened so far stay.

`DisposeRecords(scope)` disposes depth-first, in reverse creation order. Records are kept on the scope's `EGraphContext` (`AddSplitRecord`, `CoversSplit(source, values)`, `CopyLiveSplitRecords(into, driver)`, `TakeSplitRecords`, `SplitRecordCount`), whose list is created on first use.

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

*Checker:* `RestampedCopies == 0` in tests; DEBUG builds assert at every re-stamped copy unless the store was written raw (§19 item 9).

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

**I29. Header coherence.** Headers and root nodes come from the same `TableView`s: the rule set's `ViewSnapshot`, taken at `Build` from the cache that header evaluation used. Nothing invalidates a view during the search (I34). In a child, `RunRuleSet` refuses a rule set whose `ViewGeneration` or chain stamp (`ContextView.Stamp`) moved since its first header was evaluated (§9.1). *Checker:* T6.

### 12.6 Rules and lift

**I30. Executed constants (D3, K).** K equals the canonical form, in the running context, of every Id constant of the rule, query and action (X19); in a bare `Database`, the written values. Substitution rewrites exactly the recorded positions (§9.8), so the run matches and writes exactly K. A watermark is used only when its stored K equals the current K, so a constant that lost a union forces a full run, at the root as in a child. *Checker:* bridge check C1 (lengths); T7.

**I31. Watermark soundness (C7).** Let rule r run in clean context C with watermark W (own or inherited). After its actions are merged and C is rebuilt, every match of r in C's view has been applied, in C or in an ancestor whose effects C sees.

- **Lemma 1 (the variants partition the view).** By I19, `GeConst(ts, W)` and `LtConst(ts, W)` split each layer into a suffix and a prefix (one binary search per layer). By I26, invisible rows are on neither side. For a match m, take the smallest atom index i whose row has `ts ≥ W`. Variant i finds m by I27.
- **Lemma 2 (old rows are stable).** Rows appended to C's layer or any ancestor's after C's run at W have `ts ≥ W` (I21). A row now visible with `ts < W` was appended earlier, and by I20 it was visible then, with the same content.
- **Case 1 (own W).** A match made only of rows with `ts < W` existed at C's previous run, with equal K (I30). By induction over runs it was applied, and contexts are monotone, so its effects persist. Matches with a new row are found by Lemma 1. (Under retraction, an effect the context or an ancestor retracts after the match was applied is not re-derived while the match's rows are unchanged: egglog's semi-naive semantics, which the history-family reference shares.)
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

**I33. Lift soundness and completeness (L1, rule-layer §6.9, G1 fixed).**
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
   7. `Gate.LiveDescendants` equals the recursive count. The comparison is made only on a consistent reading of the tree and the gate (the gates' change marks, §6.8, with up to 16 retries). Under a frozen context whose descendants never stop changing, one run may skip this item rather than report a false violation; a context that is not frozen is always checked exactly, inside its domain.
2. **`ContextTables.CheckInvariants`:** I12–I18 (including `UnsealedCount == 0`), I10 (the tables part), and every own layer adopted with an empty pending queue. Its cost is proportional to the context: I12 and I15 are checked over own rows and own hides; the ancestor part of I13 through the ancestors' rebuild indexes and the rows appended since creation; I14 by `Find` for constructors, by key for relations, and otherwise by the table's merge on a scratch catalog. I35 is a separate `CheckRootSettled`, run in DEBUG at `CreateChild` and `Freeze` of the root. `Context.CheckInvariants` adds a G-b part with this item, `CheckEqContainerElements`: no own displacement of a child is an element of an eq-sort container (the storage half of the bridge's C6).
3. **`ExecutorInvariants.Check`** (`Execution/ExecutorInvariants.cs`): view coherence, frozen catalogs current, per-layer sort order and run index, creation stamps, watermark bounds (`Since ≤ clock`, `≥ CreatedAt`), canonical view, unique keys, hidden rows in range. Scope as built:
   - the row-level properties (sort order and run index, creation stamps, canonical cells, unique keys) are checked on the context's own rows only;
   - the view checks are skipped while any hidden set is unsealed, which the tables part reports (I18);
   - two states are tolerated, as they are not violations: an empty layer created without a merge (X37) that a cached view does not have, and a `LayerView` that still says not frozen after its context froze;
   - the watermark bounds are checked on `SemiNaiveFloor`; per-rule watermarks are bridge state, checked by C1.

`EGraphContext.CheckInvariants()` (always compiled) calls the above and adds:
- C1: progress `LastRunAt ≤ clock` and `≥ CreatedAt` in a child;
- C2: frozen state current;
- C4: member maps;
- C5: stop implies `IsContradictory`;
- C6: no own displaced id is an eq-container element (a wrapper of `CheckEqContainerElements`);
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

22. `Freeze` with a writable parent, a stopped or faulted context, or a failure not taken → throws. A failure recorded, or a contradiction found, by `Freeze`'s own rebuild or settle → throws without faulting; the context stays unfrozen (§8.1). A second `Freeze` is a no-op. Freezing a context that has live children is allowed: the children catch up once and get their own domains.
23. `Dispose` of the root or of a context with children → throws. A second `Dispose` is a no-op. Every later call → `ObjectDisposedException`.
24. `CreateChild` on a dirty writable parent rebuilds it first (and settles the root). On a stopped or faulted parent, at depth 31, or re-entrantly from inside an operation of the same domain, it throws. A failure the rebuild or settle records stays on the parent, and the child is created (§8.2). When a writable context's parent was stopped by a contradiction, the context's `Rebuild`, `Write`, `ClearTable` and `CreateChild` make it contradicted if it has `StopOnContradiction`, and otherwise throw without faulting it (§6.9).
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
37. An eq container interned in a child → throws (G-a). A child union of an eq-container element, or an eq container interned or rebuilt later at an ancestor naming a child's own loser → `NotSupportedException` at the child's next rebuild (G-b); dispose the context. A new eq container interned in a frozen database → throws (§7.4).

---

## 14. Performance budgets

### 14.1 What the root pays

| Added work | Where | Cost |
|---|---|---|
| Clock comparison in `ReadCounter` | Per call | 1 int compare |
| Block test in `IncrementCounterInView` | Per mint | 1 null test |
| Layer dispatch in `SortedWritesTable.Merge` | Per table merge | 1 null test |
| Compaction gate in `MaybeRehash` | Per merge | 1 field load |
| Retraction guard in `StageRemove`, `Database.NewBuffer` buffers and the rule `Remove` instruction (once per batch) | Per staged removal | Store-backed root without live descendants: 1 guard load and 1 inlined volatile gate read; bare `Database`: 1 null test; with descendants, 1 full `CheckRemove` (§6.7) |
| `GuardForNewState` | Per state creation | 1 owner test |
| Layered fields of `ExecutionState` (`layers`, `guard`, `views`) | Per state and per clone | +24 B (three references) |
| `DatabaseView.GetTable` child test | Per call | 1 readonly field test |
| Layered branch in `PredictRow`/`PredictColumn` | Per call (bridge merge functions call it once per merged row) | 1 null test |
| Layered branch in lookup instructions | Per instruction batch | 1 null test |
| `LookupOrInsertDefault` buffer order | Per instruction batch | 1 null test |
| Catalog read in `GetForStaging` | Per (state, table) buffer creation | 1 volatile load |
| Layer adoption in `TakeRound` | Per merge round | 1 null test |
| Contradiction check in the `MergeAll` loop | Per round | 1 null test |
| `MergeAll`/`MergeTable` epilogue | Per call | 1 null test |
| `ThrowIfFrozen` | Per entry point | 1 volatile read |
| Flags check in `PendingState` | Per buffer flush | 1 volatile read: one `int` (`flags != 0`) covers the seal and, in children, lazy queues (M10.4); it was one `bool` |
| New `TableKind` arms | Per scan or lookup call | 1 jump-table arm |
| `hidden.IsNone` | Per hidden-aware scan or index-build call | 1 test; the plain overloads, which root paths call, pay nothing (§9.3) |
| Serial-refresh flag in `IndexCatalog.GetOrRefresh` | Per call | 1 volatile read |
| `PieceCount == 1` | Per probe | 1 cached compare |
| `Layer` byte in a frame | Per dense refinement | 1 byte store |
| Child-cache slot | Per child-cache lookup | 1 `IsMultiLayer` test (single-layer nodes keep today's slot) |
| `TrieNode` | Per node | None: 64 B as before, single-layer rows inline (§9.5) |
| `Prober` | Per prober (once per join task; per morsel in a parallel run) | 72 B, up from 48 B: `ProberPiece` carries `Layer` and `View`, the prober `Rest` and `RestCount`. Against the M10.1 build: parallel triangle probe 991 KB vs 970 KB (+2.2%), parallel 200k math probe 14.14 MB vs 14.135 MB (+0.05%) |
| Views check in `RunRuleSet`/`ExecuteRuleSet` | Per run | 1 reference compare and 1 null test |
| `RuleSet` and `RulePlan` fields | Per rule set, per rule | `RuleSet`: `View`, `Views`, `ViewGeneration`, `ViewStamp`; `RulePlan`: `WrittenTemplate`, `WrittenActions`, `CanonicalConstants`; `CachedPlan`: `WrittenConstants` and up to 16 variants |
| Id constants (Store-backed root) | Per rule per run, for rules with constants | O(constants) `FindNaive` calls and one compare against the written vector; no allocation when nothing changes (§9.8) |
| View snapshot | Per rule set build | None at the root: every root rule set shares one snapshot (§9.1) |
| Latest epoch in `BeginEpoch` | Per epoch | 1 compare, at most 1 store |
| Executor counters | Per counted event | None unless `EGGNOG_COUNTERS` is set (a static readonly switch the JIT folds) |
| Interning a new eq-sort container | Per intern that misses | 1 frozen check and 1 `Interlocked` increment (§7.4) |
| New content in a root container rebuild | Per new content | 1 `Interlocked` increment |
| G-a test | Per container intern | 1 field load (`InChildContext` is tested before `HoldsIds`) |
| Layer test in `SortedWritesTable.RebuildWith` | Per table rebuild | 1 null test |
| Pass result of `RunWithTakenOut`/`RunOnTables` | Per table | 1 bool |
| Dirty-count compare in `Database.RebuildPass` | Per pass | 1 compare |
| Rebuild diagnostics | Per rebuild | Stores of `LastRebuildRows` and `LastRebuildWasIncremental` |
| `CompressRows` | Per merge that unions | O(new unions × path) |
| Settle | Per `CreateChild` of the root, and at its freeze | O(rows written since the last rebuild) |
| Rebuild-index refresh | Per `CreateChild` of a writable root | O(tables) plus the appends since each index's last refresh (§8.2) |
| Freeze | Once | O(IdCount + rows) |
| Bridge on a `Store` (M10.3) | Per egglog-compatible root call (`RunRules`, `FlushUpdates`, `AddValues`, `AddTerm`, `ClearTable`, `RunIteration`, `WithExecutionState`) | One thread-domain entry (an `Interlocked.CompareExchange`); for the raw write paths (`AddValues`, `AddTerm`, `RunIteration`, `WithExecutionState`) also one emptiness test of the root's `HostWrites`; for `AddValues`, `AddTerm` and `RunIteration` one in-callback test (`RunRules`, `FlushUpdates` and `ClearTable` already had it); for `WithExecutionState` two callback-counter updates around the action |
| Root rows of the `EGraphContext` API | Per staged row; per flush with staged rows | A copy into `HostWrites` and a replay (the rule layer's canonicalizing insert, invariant C); one `ExecutionState` per such flush. The egglog-compatible `AddValues` and `AddTerm` bypass `HostWrites` and stage as M8 did (§10.4) |
| Rule progress (§10.2) | Per rule per run | One staging and one commit (an O(1) map probe); per run, three arrays of the run's length, plus a dictionary for lists longer than 16 rules |
| Empty rule set at the root (§10.3) | Per run whose every variant was pruned | One `MergeAll`: one pass over the notification list when nothing is staged |
| Contradiction early stop (§10.6) | Per head insert into the designated contradiction function | One external call (nothing when no function is designated) |
| Extraction staleness (§10.10) | Per extraction query | O(slots) table versions and one union-find version read, where M8 read the slots' versions and the clock |
| `ParallelismBudget` (§10.9) | Per `EGraph` | One small object |
| Lift and split state (M10.4) | Per `EGraphContext`, the root's included | Two reference fields (the cached member map and the split records), 16 B; nothing else until a lift read or a split. The root takes no lift read (§6.10) |
| Lazy child structures (M10.4) | — | None at the root: root and bare tables keep eager pending queues, root catalogs eager notification flags; `NotificationList.Notify` is unchanged, and `IndexCatalog`'s lazy insert lock is on its `NoInlining` insert path (24 B less until the first index) |
| Re-stamp assertion (§19 item 9) | — | DEBUG builds only (`Store.HasRawWrites`, set by the raw execution-state API) |

`Canonicalizer` is identical after JIT specialization. Nothing is added per scanned row, per staged insert, or per find; a staged removal at a Store's root pays the guard read above.

**Gates:** the M9.0 root benchmark set within 2% (X33), each milestone on the classes listed for it in §17, and `ExecutorAllocationProbeTests` unchanged.

### 14.2 What a child pays (proportional to what it touches)

| Operation | Cost |
|---|---|
| Creation | O(tables + depth) small objects; nothing is allocated for the union-find or a table before first use. Measured in M9.4 dry runs at about 7.4–9 KB for a bare child (4,726 B in steady state); after M10.4's lazy pending queues and notification flags, 2,454 B in steady state, 2,693 B for a bridge child (`--context-footprint`) |
| Layer creation | One empty single-shard table and `TableInfo` plus an O(tables) catalog copy, once per touched table. Estimated at 1–2 KB; measured in M9.4 dry runs of `ManyChildrenBenchmarks` (`ChildWithOneEmptyLayer` minus `ChildOnly`) at about 2.7–3.0 KB, mostly `PendingState`'s two `ConcurrentQueue`s per shard (a 32-slot segment each), `TableInfo`'s two `IndexCatalog`s, two `RowBuffer`s and the catalog copy. M10.4 creates a layer's queues on first publication: 1,062 B in steady state (2,766 B before) |
| Rebuild-index refresh | At creation under a writable parent: root work, §8.2. Afterwards step 4 and the lift reads refresh a writable ancestor's index from that ancestor's appends since the child's creation: per-child work of the same order as the catch-up's reading of those appends |
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
| Bridge state (M10.3) | One `EGraphContext` and one `HostWrites` object, empty arrays until the first staged write; the locals array and its gate on first use; rule progress at 20 B per slot (a 4 B key and a 16 B `RuleProgress`), 8 slots once a rule ran, and beyond that fewer than four times the rules the context ran (the map doubles when an insert would fill more than half of it), never the rules the e-graph defines |
| A case (M10.4) | Steady state, under a writable scope over a frozen root of 20,000 rows (`--context-footprint`): the case itself 2,975 B; one union and its rebuild 9.8 KB; one asserted fact 3.3 KB; one rule run 12.5 KB; a lift over it 24.5 KB, the scope's flush of the lifted union and facts included. A case of a split that makes one union and asserts one fact costs 13.4–13.7 KB, 17.3–19.9 KB with the lift, the same at 20,000 and 200,000 root rows; one `Database.Clone()` per case costs 1.21 MB and 9.49 MB |
| Lift (M10.4) | Per round: the cases' rebuilds; the seed's own rows and the uses of its group roots (L1), each expanded into at most `CandidateLimit` keys looked up once per live case and in the parent; one flush of the parent. The member map is cached per overlay version; the arenas are kept by the `CaseSet`'s `Lifter`, and a reset costs what they held. A memo hit costs O(depth + cases + tables) |
| Split record (M10.4) | One `SplitRecord` and its `CaseSet` on the scope; the scope's record list on first use. A later step's revisit of an unchanged record costs its cases' schedule runs and one memo check |

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
| `Tables/Table.cs` | `OverlayDisplaced` arms; `RefineVisible(Subset, HiddenRows)`; hidden-aware overloads; `abstract void Freeze()` and internal `HasPendingWrites` |
| `Tables/SortedWritesTable.cs` | `Merge` dispatches to `MergeLayered`; `Freeze` override (rebuild index only with rebuild columns) |
| `Tables/SortedWritesTable.Rehash.cs` | Gate in `MaybeRehash`; `ForceRehash`; `CompactionsBlocked` |
| `Tables/SortedWritesTable.RebuildIndex.cs` | `RefreshRebuildIndex` becomes internal; `SharedRebuildIndex` (nullable); `IsShared` |
| `Tables/SortedWritesTable.Rebuild.cs` | Layered incremental override in `RebuildWith`; `SettleAppends`; `RebuildFull` refactored into `RebuildRange`, which `SettleAppends` shares; internal diagnostics `LastRebuildRows` and `LastRebuildWasIncremental` |
| `Tables/SortedWritesTable.RebuildWatermark.cs` | `FirstRowSinceRebuild` and `MarkRebuilt` internal; `HasRowsSinceRebuild` |
| `Tables/SortedWritesTable.Lookup.cs` | No change; `FindRowHashed` lives in the new `SortedWritesTable.Layered.cs` (§6.6) |
| `Tables/MutationBuffer.cs` | `AttachRetractionGuard`; `FreshHandle` copies the guard (§6.7) |
| `Tables/SortedWritesTable.Scan.cs` | Hidden-aware loops; `RefineVisible` |
| `Tables/TableCatalog.cs` | Volatile map reads; `Resolver` (init); `GetForStaging`; copy-on-write `Publish` |
| `Tables/ExecutionState.cs` | `CreateBuffer` uses `GetForStaging`; `StageRemove` calls `guard?.CheckRemove`; the clone copies view, layers, guard and views |
| `Tables/PendingState.cs` | `Seal`, `IsSealed`; `Enqueue*` throw when sealed. M10.4: lazy queues for child tables (a shared, never-written empty queue until the first publication, then a queue created by compare-and-swap); a `flags` word replaces `isSealed` and covers the seal and the lazy queues; `PrepareRows`/`PrepareRemovals` (`NoInlining`) take the flagged path |
| `Planning/QueryEntry.cs` | `IsIdConstant` flag, ignored by equality; `IdConst(Value)` factory |
| `Planning/IQuerySchema.cs` | `IsCacheable` (default member, `true`) |
| `Actions/ExecutionState.Database.cs` | `ReadCounter` pin; the view constructor sets `layers` and `guard`; `GetTable` throws when `layers` is set |
| `Actions/ExecutionState.Predicted.cs` | `ReadTable` throws when layered; `ReadRootTable` (the former `ReadTable` body) on the root path of `PredictRow`/`PredictColumn`; layered `PredictRow`/`PredictColumn` (both forms, `PredictRowLayered`/`PredictColumnLayered` in `Layers/ExecutionState.Layers.cs`); `ValueFromCounter` rejects `≥ StaleRep` |
| `Actions/ExecutionState.Actions.cs` | Layered dispatch at lines 137, 143, 208 and 290; `LookupOrInsertDefault` creates its buffer at the first miss in a child |
| `Actions/VectorizedLookup.cs` | `TableView` overloads; `OverlayDisplaced` arm |
| `Containers/ExecutionState.Containers.cs` | Unchanged; documented as reading the shared root instance |
| `Database/Database.cs` | `AddCounter` sealing; `AddTable` throws at a Store's root, internal `AddTableCore`; `ClearTable` calls `RetractionGuard?.CheckClear`, bumps `ChangeStamp`, calls `ThrowIfFrozen`, routes in a child; `NewBuffer` uses `GetForStaging`, calls `ThrowIfFrozen`; `Clone` guard; `IncrementCounter` → `IncrementCounterInView`; `uint?` timestamps; layered member behaviour per §3.4; `GetTableSpec`, `GetTableName` |
| `Database/Database.Merge.cs` | `uint?` timestamp; adoption in `TakeRound`; contradiction round stop; serial strata when layered; `ChangeStamp` in `MergeOne`/`MergeTable`; `AfterMerge` epilogue in `MergeAll` and `MergeTable`; `ThrowIfFrozen` |
| `Database/Database.Rebuild.cs` | Child guards in `ApplyRebuild` and `RefreshRowsForValues` (§3.4); `RunWithTakenOut`, `RunOnTables`, `RunOnTablesCore` and `RunOnTablesParallel` return whether any table reported a change (each such table is still notified), which `RefreshDirtyContainerIds` needs (§7.4); they are now also called by `ContextTables` and `Context.Settle` |
| `Database/DatabaseView.cs` | `IncrementCounter` → `IncrementCounterInView`; `ClockCounterRep`; `IncrementSharedCounter`; layered `GetTable` (throws), `GetTableName`, `TableIds` |
| `Database/CounterSet.cs` | `IncrementBy(CounterId, long)` |
| `Database/DependencyGraph.cs` | Copy-on-write `levelSnapshot` for `LevelOf` |
| `Database/ExternalFunctionRegistry.cs` | Copy-on-write snapshot |
| `Database/ExternalFunction.cs`, `Database/DelegateExternalFunction.cs` | `ReadsTables` |
| `Database/TableInfo.cs` | `FreezeIndexes()` |
| `Database/Database.Constraints.cs` | Delegates to `ConstraintProcessor`; refuses a child through `GetQueryTableInfo` (§3.4) |
| `Database/ColumnCardEstimator.cs` | Static per-`TableInfo` overload; the instance form refuses a child (§3.4) |
| `Indexes/Index.cs`, `Indexes/IndexCatalog.cs` | `Refresh(Table, bool allowParallel)`; `SetSerialRefresh`. M10.4: `IndexCatalog`'s insert lock is created by the first insert |
| `Indexes/ColumnIndex.cs`, `ColumnIndex.Build.cs`, `TupleIndex.cs`, `TableIndexing.cs`, `ColumnIndexGatherSink.cs` | Hidden-aware builds; `AddValue` |
| `Concurrency/NotificationList.cs` | `IsEmpty`. M10.4: a lazy constructor (an empty flag directory); the existing growth branch creates segment 0 on the first notify |
| `Containers/ContainerValues.cs`, `ContainerEnvBase.cs`, `ContainerEnv.cs`, `ContainerValueIndex.cs`, `Database.Containers.cs` | `holdsIds`; `HasEqContainers`; `AnyEqContainerMentions`; `EqVersion` and `EqVersionCounter` (G-b, §7.4); the frozen-intern refusal; G-a; `MintId` via the shared counter; `ContainsElement`; child throws; dirty-log append; `RebuildContainers` resolves a `null` timestamp to the store clock (§4.1) |
| `Planning/PlanTemplate.cs`, `PlanInstance.cs` | `InstantiateLayered`; `Instantiate` kept over `SingleLayerEvaluator`; `LayeredSubset[]` headers |
| `Query/IdConstant.cs` | Remarks per X19 |
| `Query/ActionInfo.cs` | `IdConstants` |
| `Query/CachedPlan.cs` | Variants (`GetVariantFor`, `MaxVariants`); `WrittenConstants` |
| `Query/RulePlan.cs` | `WrittenTemplate`, `WrittenActions`, `CanonicalConstants` |
| `Query/RuleSet.cs` | `View`, `Views`, `ViewGeneration`, `ViewStamp`, `LookupTable`, and an internal constructor that takes them; `BuildCachedPlan` uses written plans |
| `Query/RuleSetBuilder.cs` | Instantiates through `db.View`; computes K and substitutes in `AddRuleFromCachedPlan` and `AddBuiltRule`; snapshots views in `Build`; `EstimateSize` through the layered member |
| `Query/RuleBuilder.cs` | `GetSpec` via `GetTableSpec`; records `ActionIdConstant`s |
| `Query/RuleQueryBuilder.cs` | Arity via `GetTableSpec`; `GetIdColumns` layered |
| `Query/SymbolMap.cs` | Names via `GetTableName` |
| `Query/Database.Query.cs` | Layered `IQuerySchema`, `IPlanStatistics`, `IHeaderEvaluator`, `PlanQuery` per §3.4; `SplitConstraints` via `ConstraintProcessor` |
| `Execution/TrieNode.cs`, `TrieNodePool.cs`, `Prober.cs`, `ProberCursor.cs`, `FrameUpdate.cs`, `FrameUpdates.cs`, `JoinRun*.cs`, `SparseColumnIndex.cs`, `RuleWork.cs`, `Database.Execution.cs` | Per §9.5 and §9.1 (`JoinRun` and `RuleWork` take the snapshot; `RunRuleSet` sets `InSearch` and the root state's views) |
| Tests: `TrieNodeTests`, `ProberTests`, `FrameUpdatesTests`, `CachedPlanTests`, `RuleSetBuilderTests`, `TrieNodeSpareIndexTests`, `SparseColumnIndexTests` | Mechanical API updates only; assertions unchanged |
| Tests updated as §21 records | `Contexts/ChildDatabaseTests`, `ContextEpochTests`, `ScanMergesTests`, `ContextUnionFindTests`; `Layers/RetractionTests`; `Actions/LayeredLookupTests`, `VectorizedLookupTests`; `Tables/DisplacedTableFreezeTests`; `Differential/DiffSemiNaiveStep`; and the review tests named there |
| Engine doc comments outside this list | 18 `cref` attributes that the hidden-aware overloads made ambiguous (CS0419) name their overload's signature (M10.1) |
| Tests: `Planning/PlanInstantiationTests`, `Execution/DecomposedExecutionTests`, `Planning/PlanTestDatabase`, `Query/DatabaseQueryTests` | Compile unchanged through the kept `Instantiate` overload |
| Tests: `Execution/JoinPathTests`, `Execution/ExecutorVisibilityTests` | No source change (root `GetTableInfo` calls remain valid); re-run as executor regression |
| `src/Eggnog.Relations.Tests/Eggnog.Relations.Tests.csproj` | `ProjectReference` to `../Eggnog.Storage/Eggnog.Storage.csproj` (second oracle), used only through `using Spike = Eggnog.Storage;` (X32) |
| `Bridge/*` (M8) | `EGraph` (on a `Store`; `.Containers`, `.Contexts`, `.Panics`, `.Rules`, `.Run`, `.Values`), `RuleInfo`, `PanicFunction`, `LazyPanicFunction`, `RuleAction`, `RuleBindings`, `BridgeQuery`, the rule builder (lowers Id-typed constants that land in no table column through `QueryEntry.IdConst`, and appends the early stop, §10.6), `TableAction` (`TryLookupColumn` and `VisibleRowCount` in the running context), `IterationReport`, scheduler hooks, per §10 |
| `Layers/LayeredTableSchema.cs` | `IsConstructor` recognizes bridge constructors; `ConstructorValueColumn` (§10.2) |
| `Layers/ExecutionState.Layers.cs` | `TryLookupColumn`, `VisibleRowCount` for bridge actions in a child |
| `Layers/ContextTables.cs` | `DiscardDeferredStaged` (the failed-run decision, §21.9) |
| `Contexts/Context.cs`, `Context.Lifecycle.cs`, `Context.Invariants.cs` | `Host`; `LastRebuildPasses`; `Report` shared with the bridge checker |
| `Contexts/Database.Freeze.cs` | Re-arms the indexes of every table whose generation the freeze moved, before the warm-up (§10.10) |
| `Database/Database.Merge.cs` | `MergeAll` and `MergeTable` refuse a faulted child, inside the existing layers test |
| `Execution/Database.Execution.cs` | The failed-run decision: deferred layers' staged writes are discarded when a child's search throws |
| `Extraction/Extractor.cs`, `.Discovery.cs`, `.Functions.cs`, `.Reconstruct.cs`, `.Variants.cs`, `ExtractionRowSink.cs` | Extraction over a context (§10.10) |
| `src/Eggnog/Graph/RootEngineScope.cs` | Calls `RuleInfo.GetOrBuildCachedPlan`; the library's public API is unchanged. M10.4: stages its root batches through `Database.CreateEngineState` (stamped with the store clock), so stores the library builds keep the re-stamp assertion (§19 item 9) |
| `Tables/TableCatalog.cs` (M10.4) | A constructor with lazy notification flags, for child catalogs |
| `Tables/SortedWritesTable.cs`, `SortedWritesTable.Layered.cs` (M10.4) | A private constructor with a lazy `PendingState`, which `CreateLayer` uses |
| `Tables/OverlayDisplacedTable.cs` (M10.4) | A lazy `PendingState` |
| `Contexts/Database.Contexts.cs` (M10.4) | A child's catalog has lazy notification flags |
| `Bridge/EGraphContext.Invariants.cs` (M10.4) | C4 (`CheckMemberMaps`): a map built from the overlay holds exactly its members that are not local, each canonical in the parent and mapped to its group root, roots and members ascending; a cached map whose version is current equals it |
| `Contexts/Store.cs`, `Database/Database.cs` (M10.4) | DEBUG-only `Store.HasRawWrites`, set by the raw `Database.WithExecutionState` and `CreateExecutionState`; `Database.CreateEngineState(timestamp)` for the engine's own writes, which never set it |
| `Contexts/Context.Writes.cs`, `Layers/ContextTables.Rebuild.cs`, `Bridge/EGraph.Containers.cs`, `Bridge/EGraphContext.Writes.cs` (M10.4) | Engine writes create their states through `CreateEngineState`; the raw escapes keep `CreateExecutionState` |
| `Layers/ContextTables.CatchUp.cs` (M10.4) | The re-stamp assertion (§19 item 9) |

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
| `Execution/` | `ContextView`, `ViewSnapshot`, `ProberPiece`, `LayerHits`, `TrieNodeSideState`, `ExecutorInvariants`, `ExecutorCounters` |
| `Planning/` | `ILayeredHeaderEvaluator`, `SingleLayerEvaluator`, `CachedIndexKey`, `PlanIndexKeys` |
| `Database/` | `ConstraintProcessor`, `FreezeWarmup`, `SettleTableOperation` |
| `Query/` | `CanonicalConstants`, `ConstantSubstitution`, `TemplateSubstituter`, `ActionSubstituter`, `ConstantVariant`, `ActionIdConstant`, `ActionReadAnalysis` |
| `Concurrency/` | `ThreadAffinityGuard`, `ThreadAffinityScope`, `ParallelismBudget`, `ParallelismLease` |
| `Containers/` | `ContainerDirtyLog` |
| `Bridge/` | `EGraphContext` (+ 9 partials: `.Run`, `.Watermarks`, `.Writes`, `.Reads`, `.Freeze`, `.Locals`, `.Invariants`, `.Extraction`, and `.Lift` in M10.4), `RuleProgress`, `RuleTable`, `HostWrites`, `ContextLocalSlot`, `StopOnContradictionFunction`, `SaturateReport`, `ConcurrentChildren`, `ContextInvariantException` |
| `Extraction/` | `ExtractionWatch`, `ExtractionTableMark`, `ExtractionQueryScope` (§10.10) |
| `Cases/` | `MemberMap`, `MemberSignatureComparer`, `ClassPartition`, `LiftSpec`, `LiftTable`, `LiftedTable`, `LiftTableKind`, `LiftOptions`, `LiftReport`, `LiftSignature`, `LiftCandidates`, `LiftKeySink`, `LiftWrites`, `Lifter` (+ `.Equalities`, `.Facts`), `CaseSet`, `SplitDriver`, `SplitRequest`, `SplitKey`, `SplitRecord`, `SplitOptions`, `SplitReport`, `ISplitSource`, `ISplitDemand`, `ICaseSchedule` |

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
| M10.4 | `WritesBenchmarks`, `RuleSetRunBenchmarks` (both publish staged writes, whose `PendingState` check M10.4 changed, §14.1) |

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

**Interim:** the child `Database` gets a placeholder `DisplacedTable` at `TableId 0` that is never written (unions arrive in M9.2), and a `Resolver` that always throws `NotSupportedException("Layers arrive in M9.3.")` (without a resolver, staging would throw `KeyNotFoundException`). The placeholder is in the child's table map but not in its catalog, so staging into `$uf` is refused the same way. `RunRuleSet` in a child throws `NotSupportedException` until M10.2, and the X23 refusal of `$uf` is in every query member of a child already (§3.4). (As shipped; M9.2–M10.2 replaced each of these.)

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
- In a child, `Context.Rebuild` runs union-find catch-up plus `MergeAll`; at the root it runs §7.1's root branch already (egglog's pass over `Schema.FunctionTables`, with the X42 clean test). `NeedsCatchUp` is `false` while `Tables` is null; `IsClean` omits the tables term.
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
- `Contexts/UnionFindInvariantCheckerTests`: nine deliberate corruptions (I4, I5, I6, two of I8, I9, I10, I23 and the gate; the gate one also on a frozen root), plus root I4 and full compression of a frozen root.
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

**Interim:** `Rebuild` still runs only the union-find steps plus `MergeAll`. It throws `NotSupportedException("Row catch-up arrives in M9.4.")` before anything changes, without faulting the child, only when row work would be needed: (a) an unsettled ancestor's shared layer version moved; (b) the union-find changes and a live own row names an id that stops being canonical; (c) a new own loser that is not local is named by a visible ancestor row. The same refusal reaches `Write`, `ClearTable`, `CreateChild` and `Freeze` through `Rebuild`. (Refusing whenever `NeedsCatchUp` holds would break M9.2's catch-up after every root union, and would not refuse a child's own unions over its rows.) Random layered states are built by writing each ancestor before creating its children and freezing it, with no unions below the root.

**Tests** (`Layers/` unless noted):
- `HiddenSetTests`: 1000 property sequences against `SortedSet<int>`; conversion; `Union`; reopening a sealed set.
- `HiddenFilterTests`.
- `HiddenSealingTests`: R1-1's counterexamples. `f(Id) → i64` with `MaxMerge` and a ts column; root `f(a)=1`, frozen; child writes `f(a)=5`; a scan of `GetView(f)` yields only `f(a)=5`, and a rule over `f` derives `g(a,5)` only. Delete variant: after `StageRemove(f, [a])`, `TryGetRow(f, [a])` is absent. Plus `UnsealedCount == 0` after every `Write`.
- `TableViewTests`: the C4 cases plus **`LookupPathsAgree`**.
- `LayeredMergeTests`: new key; ancestor key with true and false merges; a three-row fold; `UnionIdMerge` staging into the child; non-lattice throws at first write, not at a hitting lookup; deletes; same-stratum read throws.
- `CanonicalWriteTests` (child part): a write naming a stale id lands on the canonical key and merges with the visible row; a raw write with ts 0 is stored at the merge's epoch.
- `LayerCreationConcurrencyTests`: 8 threads, each with clones of one state (as `ActionBatchWork` makes them), staging into the same untouched tables; exactly one layer per table, every row merged, the catalog never torn, `ViewGeneration` unchanged until the merge.
- `RetractionTests`: every cell of §6.7; a Store's execution state always carries its database's guard, and raw buffers pass the guard.
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
  - on spike seeds the facade canonicalizes Id cells through `Context.UnionFind` inside the batch, as the spike's `Insert`/`GetOrAdd` do (`Eggnog.Storage/Context.cs` `Canonicalize`); on spike-free seeds of `ContextDifferentialTests` and odd seeds of `ContextCopyTests` it writes raw (`RawWrites`, `RawChildWrites`): child writes pass id cells as given with timestamp 0, so X38's canonicalization and stamping are exercised, while root writes stay canonical, because a stale raw root write is legitimately invisible until the next rebuild (X42);
  - 400 seeds; 3 deep; changing ancestors; random freezes; dispose; retractions only where §6.7 allows, asserting throws elsewhere;
  - `SpikeTestSchema` has a `bits` table with `OrMerge`, a lattice join that differs from both inputs, so a merge that stores the incoming row instead of the output is caught; the spike is the second oracle on even seeds, which retract nothing;
  - views modulo renaming, lookups, column scans, `Scan(since)`; `SpikePair.Check` compares `ScanColumn` with the visible rows as exact multisets;
  - `CheckInvariants` after every `Rebuild`; `LookupPathsAgree` after every write.
- `Contexts/ContextCopyTests` (*context = copy*, 400 seeds): replay the chain into a fresh `Store` with explicit ids; exact canonicals; **C's journal equals the copy's displaced set minus the ids displaced in P's view when C was created** (the harness records that set at `CreateChild`); row sets modulo renaming.
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
- T9 `Differential/DiffLayeredSemiNaiveStep`: inserts a layered step before every semi-naive step of the generated programs, sharing `DiffSemiNaiveStep`'s variants through a `Split` helper; the programs of the existing profiles are unchanged.
- T11 executor checker tests (`Execution/ExecutorInvariantsTests`).
- T12 `Execution/LayeredAllocationProbeTests`: no allocation per row, probe or frame in a warm child run.
- `Query/ChildPlanningTests` (R2-1): `NewRule`, `AddAtom`, `Build` and `PlanQuery` in a child that never wrote the rule's tables; `SymbolMap` names; statistics from the child's view; `$uf` atoms throw.
- `Execution/LayeredParallelRunTests` (R1-2, R2-2): a child with `ParallelRuleSetsOverride = true` runs rules whose actions write untouched tables and look up untouched tables; equal to the serial run; a hitting `LookupOrInsertDefault` creates no layer; `ViewGeneration` unchanged during the search.

**Done when:** the baseline holds; `ExecutorAllocationProbeTests` is unchanged; with `EGGNOG_COUNTERS`, ancestor-layer scanned rows and dynamic-index rows are 0 once `W ≥` every root stamp.
- The counters (`ExecutorCounters`) are kept per database (`Database.ExecutorCounts`), behind a static readonly switch set from `EGGNOG_COUNTERS`. The test project's module initializer turns counting on for the suite; `EGGNOG_COUNTERS=0` turns it off, and the counter tests then skip.
- Ancestor rows are counted in two kinds: rows of root nodes, narrowed by headers only, and rows of refined nodes (probe results). The 0 is read on the root-node counters, which measure the work that grows with the root; refined rows are bounded by the `|probe results|` term of §14.2.
- The 0 holds for size-ordered plans (Gj, the default, and PureSize). MinCover covers with the widest atom by design, so its plan scans the old side; a test documents this.

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

**Differential tests** (`Colors/`). Program families: **monotone** (no `AssertNe`/`AssertAnyNe` on Id columns, no primitives that depend on id order, no deletes, no table reads in actions, and no rule body that reads a lattice function's output: a body atom over a lattice function binds its output to a variable nothing else reads) and **history** (all of those allowed, deletes only where §6.7 allows). A body that joins on a lattice output is not monotone: once a write lowers the value, facts derived from the old value persist, at a plain root too, while a fresh root that replays every write before saturating never sees the old value.
- `ReferenceRuleContext` (with `TermNamer`) is operational: it wraps `SpikeReferenceContext` (sealed) with rule checkpoints, each op stamped by a shared clock so a view can be computed at any past tick. At each rule run in context X it computes X's reference view from the ordered ops of X's chain, runs one Jacobi iteration of its own evaluator over the closed view (`ReferenceRuleEvaluator` over the programs' rule IR, `ColorRule`, which is also built into the engine through `EGraphRuleBuilder`; `NaiveRuleRunner` runs core rule sets, and would run the bridge's rules through the same planner and action interpreter as the code under test), and appends the newly derived facts to X's ordered ops as `Derived` assertions. Descendants inherit them like any assertion, so facts derived in an ancestor under fewer equalities, or before a child's delete, persist as they do in the engine. The reference selects matches semi-naively by row content: a match fires only when one of its rows is unseen at the rule's watermark (its canonical content was absent at the watermark, or after a delete or clear since), with the watermark of §10.2. A naive reference disagrees with egglog's semi-naive semantics in the history family (a child that deletes a derived fact whose premises are unchanged does not re-derive it; an action's table read sees the row as of the iteration that fired it). Whether inheriting is sound is checked by the seeded-versus-unseeded property, and the reference decides `AllowInheritance` from the rule itself, not from `ActionReadAnalysis`.
- `ContextRuleDifferentialTests`: both families against `ReferenceRuleContext`; family A (no minting) compared cell for cell (both sides make a class's least id canonical, which is stronger than modulo renaming), family B (constructor minting) compared by term names; 400 seeds. Family B leaves out the id-order primitive, never deletes or clears constructor tables (a term minted again would share the old class's name), and generates its rules in strata so minting terminates.
- `ContextRuleCopyTests` (monotone family only): a child's view after saturation equals a fresh root replaying the chain's assertions and running the same rules; random conjunctive queries agree; includes **`ConstantLosesRootUnionBeforeChildRuns`** (R1-3: rule `R(x, 7) → S(x)`, root `R(1,3)` and `union(7,3)`, root run, freeze; a child that asserts nothing has the same view as its parent).
- `SeededWatermarkDifferentialTests` (both families): `InheritWatermarks = false` gives equal views at every checkpoint, and seeded match totals are ≤ unseeded; includes R1-4's counterexample (`(set (g x) (f x))` with `f` raised in the child) as a regression, through a lookup and through a primitive that reads tables. Its programs delete and clear only tables no rule writes: a child that retracts a derived fact re-derives it unseeded and not seeded (egglog's push semantics, X41). Every seeded program carries a no-op primitive over every body variable, so match counts are counts of distinct full bindings in both runs (a semi-naive run otherwise sums over its variants).
- `IdConstantContextTests`.
- `ConcurrentChildRulesTests`: 16 children on 8 threads equal serial replays.
- `EggPushPopTests` (and `EggPushPopWebDemoTests`): for programs whose rules all have `AllowInheritance` (egglog's push copies `last_run_at`, which then coincides with inheritance); other programs are reported, not compared. The M10 gate per map §6. There is no `.egg` reader (rule-layer D14): the programs are translated by hand through `EggPushPopProgram` (push is `CreateChild`, pop is `Dispose`), and only sizes, iteration counts and check results are compared, against values from the egglog 2.0.0 CLI. Compared: `calc.egg`, `tricky-type-checking.egg` blocks 1–6, a program written to make a push inherit progress, and the web demo's `bdd`, `push-pop`, `array` and `cyk`. Reported, not compared: programs that intern eq-sort containers in a child (decision 9) or need sorts the bridge lacks (§21.11).

**Done when:** the baseline holds; every differential is green at 400 seeds; I31 is exercised by the seeded-versus-unseeded property over both families (map §8.2 risk retired), in programs whose retractions touch only tables no rule writes (X41); `RestampedCopies == 0` throughout.

### M10.4: Lift primitives and split hooks

**Content:** §11 in full, plus `EGraphContext.Lift.cs` (`ScanOwn`, `ScanMerges`, `IsLocalId` and `ScanAncestorsMentioning` by `FunctionId`).

**Tests** (`Cases/`):
- `MemberMapTests`, `ClassPartitionTests`.
- `LifterTests`: **`G1bFactThroughWinnerIsLifted`**, boolean split, three-case meet, no aliasing with one live case, vacuous cases dropped, all-vacuous contradicts, lattice case-join, bottom-up term lift, limits, memo, Id-output functions, not subsumed.
- `LifterPropertyTests`: soundness ⊆ E, completeness ⊇ E; 400 seeds.
- `SplitDriverTests`: lift-before-deepening order, demand filter, ancestor-covered keys, limits, disposal order, stopped cases disposed.
- Added with the reviews and fixes: `LateIdLiftPropertyTests` (the lift property over ids the parent mints after its cases reserved their blocks, and under a writable parent; 200 seeds); `SplitStepRevisitTests` and `SplitDriverSharingTests` (a later step revisits its own driver's live records; several drivers on one scope); `LiftOracleTests` and `SplitDriverOracleTests` (behaviours the planted-bug review found unchecked); `LiftFixTests`; and the review and verification classes `LiftSoundnessReviewTests`, `LiftReviewTests`, `LiftCompletenessReviewTests`, `LiftRootCostReviewTests`, `NestedLiftPropertyReviewTests`, `LateParentLiftPropertyReviewTests`, `LiftFixVerificationTests`, `LiftFixRound2VerificationTests` and `SplitFixRound3VerificationTests`.
- Outside `Cases/`: `Contexts/ChildFootprintTests` and `Tables/LazyPendingStateTests` (lighter children), and `Bridge/ConcurrentChildParallelRuleSetTests` and `ConcurrentParallelChildrenReviewTests` (parallel rule sets inside concurrent children).

**Benchmarks** (`Contexts/`):
- `CaseSplitMemoryBenchmarks` (L6, B5): bytes per structure, against `Database.Clone()`, the in-house egglog push baseline.
- `CopyVolumeBenchmarks` (G3): histogram of rows copied per union.
- `LateParentMintBenchmarks` (L7, G2): `ParentIdLosers`, copies per lift round, and the root's id-array growth from reserved blocks (§4.2).
- `PerChildCostBenchmarks` (full workload: 100 merges plus one rule run).
- The footprint mode, `--context-footprint` (`ContextFootprint`): steady-state bytes of a bare child's parts, of a split per case against clones, and of one case step by step (B5).

**Done when:** the baseline holds, the property tests are green, and the L6 and L7 reports are produced and attached to the M10 PR.

Built as §21.10 records; the L6 and L7 reports are `docs/design/colors-m10-reports.md`.

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
   - for Id constants that have lost a union (X19): a Store-backed root matches and writes the class's canonical id. Added to map §8.1 as row 25: "Cached plans bake Id constants; after a constant loses a union the rule silently stops matching (`eb/lib.rs:1397-1420`, `cr/query.rs` constant entries). Eggnog: canonicalize every Id constant per run in the running context."
   - in operations egglog lacks: `CreateChild` and `Freeze` settle the rows written since the last rebuild (§7.6);
   - when every rule of a run was pruned (M10.3): the bridge still merges what is staged at the run's T, which egglog's `run_rule_set` skips while `run_rules` still records T as every rule's watermark. Added to map §8.1 as row 26.
5. **Inheritance is narrower than egglog's push** (X41): rules whose actions read tables never inherit a watermark, whereas egglog's push copies `last_run_at` for every rule. egglog push/pop is therefore an oracle only for programs whose rules all inherit.
6. **Host writes at the root** (X38): the egglog-compatible `EGraph` API writes as given; the `EGraphContext` API canonicalizes Id cells at every level, which is what the rule layer's invariant C assumes.
7. **G2 ranking** (parent-visible ids first) was deferred until the L7 report existed. The report (`docs/design/colors-m10-reports.md`, M10.4) measures the cost of smaller-id-wins after a late parent mint as linear in cases × terms per round and independent of the root; it gives no reason to hurry the change, and the decision stays open.
8. Deferred:
   - **B1** pruning;
   - **B3** flattening of hidden sets;
   - **G5** memory budget per `Run`;
   - **G8** thaw;
   - **G9** shared sibling ids;
   - eq containers in children (C13 after M10);
   - per-context replanning (§8.3 #13);
   - deduplicating driver keys across pieces (an optimization for I27's multiplicity; measure first).
9. **The C7 safeguard (c′)** stays as defence in depth. Once I21 is proven over M10 workloads, turn a nonzero `RestampedCopies` into a `Debug.Assert`. Done in M10.4 (§21.10): DEBUG builds assert at every re-stamped copy unless the store was written through the raw execution-state API, whose rows carry the stamps the caller gives them.

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

---

## 21. Implementation notes (M9.0–M10.4)

M9.0 through M10.4 are implemented. This section records, per milestone, where the code departs from the text above: what the text said, what was built, and why. Each departure is listed once, under the milestone that made it; when a later milestone changed the same thing again, the later entry says so.

The last column says what happened to the text:
- **Corrected (§…)**: the text above now says what was built; the sections named were changed in this revision.
- **Note**: the text is unchanged. The entry records interim behaviour that a later milestone replaced, work done earlier than planned (*placement*), or detail below the level of this document.

Reports that only confirmed the text are not listed; for example, `Context.NewId` does not enter the thread domain, as §5.7 says, and a root `Context.Write` asserts the sort cells of every registered table, as §6.6 says. §21.8 lists the edits that rule-layer spec v2 asked of this document, §21.9 records M10.3, §21.10 records M10.4, and §21.11 lists what is still deferred.

### 21.1 M9.0: Root benchmark baseline

No departure was reported. The baseline classes are in `src/Eggnog.Relations.Benchmarks/Root/`. The gates measured against them are pending (§21.11).

### 21.2 M9.1: Store, contexts, clock and ids

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §3.4, §4.3 | The root's `Database.AddTable` is unchanged, and `Store.AddTable` calls it. | `AddTable` on a Store's root throws `InvalidOperationException("Definitions are added through the Store.")`, before and after children; the `Store` adds through the internal `AddTableCore`. A table added past the store would be unknown to the store's schema: children's `TableCount`, specs and names would disagree with the root, and the root's rebuild over `Schema.FunctionTables` would never rebuild it. A bare `Database` and a clone (no owner) keep egglog's `AddTable`. | Corrected (§3.4, §4.3, §15) |
| §4.3 | `Store.AddTable` adds the table, then registers it. | It first checks that the table is a `SortedWritesTable`, which `LayeredSchema.Register` requires, so a rejected table leaves the root unchanged. | Corrected (§4.3) |
| §4.1 | `null` timestamps resolve to the store clock on the members listed there. | `RebuildContainers` with a `null` timestamp also resolves to the store clock on a Store-backed database; a bare database keeps the newest union's timestamp. Under the C1 pin, a clock-reading `UnionContainerMerge` would otherwise stamp the newest union's timestamp instead of the clock value egglog's merge reads, which breaks root parity. | Corrected (§4.1, §15) |
| §4.2 | `IdBlock` holds `Next` and `End`; the block size follows `Minted`. | `IdBlock` also holds `Start` and `ReservedBefore`, so `Minted` is read from the one published block. The size rule uses the total reserved so far, which equals `Minted` at refill time because the current block is spent. | Corrected (§4.2) |
| §4.3 | `Clone` is guarded; nothing else is said. | A clone keeps the source's clock (the counter layout is the same) and has no owner, so it is a bare egglog `Database`. | Corrected (§4.3) |
| §6.1 | `LayeredTableSchema` has public fields. | Get-only properties, plus `Spec`; `IsContradiction` is a volatile flag set once. A `ReadOnlySpan` cannot be a field of a class, and the schema should be immutable. | Corrected (§6.1) |
| §3.4 | `DatabaseView.GetTable` throws in a child. | It throws `InvalidOperationException`, testing a readonly field cached at construction: one field test per call at the root. | Corrected (§3.4, §14.1) |
| §3.4, X23, §17 | The `$uf` refusal is M10.2 work, for `SplitConstraints` and `GetTableView`. | Implemented in M9.1, in every query member of a child, through `Database.GetQueryTableInfo`: `ProcessConstraints` (and so `SplitConstraints`, `GetApproximateSize` and the header path), `EstimateSize` and `ColumnCardEstimator.GetColumnUniqueness`. Until views existed, these members would have answered from the child's never-written placeholder table, against context = copy. M10.2 gave them their final form (§21.7). | Note (placement) |
| §17 M9.1 | Interim: the child has no `Resolver`, so staging throws `NotSupportedException("Layers arrive in M9.3.")`. | A `Resolver` that always throws that exception: without a resolver, `GetForStaging` throws `KeyNotFoundException`. The `$uf` placeholder is in the child's table map but not in its catalog, so staging into it is refused the same way and it is never written. | Corrected (§17); replaced in M9.2 and M9.3 |
| §3.4, §17 M9.1 | `RunRuleSet` is unchanged in shape. | Until M10.2, `RunRuleSet` in a child threw `NotSupportedException("Rules in a child context arrive in M10.2.")`. Before views existed, the executor would have read the child's own layers (the `$uf` placeholder) as its view, and a rule set built on another database would have applied its header subsets to the child's tables. Root cost: one owner test per run. | Corrected (§17); replaced in M10.2 (§21.7) |
| X30, §17 | `ChangeStamp` comes with M9.4. | Bumped from M9.1, in `MergeOne`/`MergeTable` when a merge adds or removes rows and on a `ClearTable` that removes rows, because `Context.Version` (an M9.1 item) reads it; hides and the stop flag bump it from M9.3. One test per table merge. | Note (placement) |
| §15 | `Database/Database.Rebuild.cs` has no change in code; `Database.Constraints.cs` and `ColumnCardEstimator.cs` change in later milestones. | Child guards in `ApplyRebuild` and `RefreshRowsForValues`, which §3.4 requires to throw in a child, and the X23 guard in the other two files. M9.4 changed `Database.Rebuild.cs` again (§21.5). | Corrected (§15) |
| §16 | — | Helpers not listed in §16: `ThreadAffinityScope` (an allocation-free `using Domain.Enter()`); `ThreadAffinityGuard.IsHeld`; `Context.MaxDepth`, `ChildCount`, `IdBlocks`, `Fault` and `ThrowIf*` helpers; `Database.IsChild`, `AddTableCore` and `GetQueryTableInfo`; `IdBlockAllocator.FirstBlockSize`, `MaxBlockSize`, `BlockCount`, `BlockStart` and `BlockEnd`; `Store.UnionFindName`, `NoteChildCreated` and `CheckEpoch`. | Corrected (§8.3, §16) for `ThreadAffinityScope` and `IsHeld`; note for the rest |

### 21.3 M9.2: The layered union-find

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §7.1, §17 M9.2 | Interim: `Context.Rebuild` runs union-find catch-up plus `MergeAll`. | At the root, `Rebuild` runs egglog's pass already: `Database.RebuildPass` over `Schema.FunctionTables`, with the X42 clean test (journal mark and pending notifications). That is §7.1's root branch; the root has nothing to catch up with, and the pass keeps root rows canonical after root unions. | Corrected (§17) |
| §5.6 | `CatchUpUnionFind` refuses staged, unmerged unions before the cursor test. | It refuses them only when the parent has new displacements. Refusing when there is nothing to catch up with would fault the child for no reason, because `Rebuild` merges those unions right away. | Corrected (§5.6) |
| §5.5 | `OverlayDisplacedTable` takes four constructor parameters. | An optional fifth, `IdBlockAllocator? localIds`: `ParentIdLosers` (§5.6, G2, L7) needs to know which ids are local when the union happens. | Corrected (§5.5) |
| §5.5 | — | `OverlayDisplacedTable.Merge` refuses ids above `UnionFind.MaxId` with `ArgumentOutOfRangeException`, as the root union-find's `Reserve` does. | Corrected (§5.5) |
| §5.7 | `Find` and `AreEqual` call `ThrowIfNotReadable` only. | On a context that is not frozen, `Find`, `AreEqual`, `ScanMerges` and `CheckInvariants` enter its thread domain re-entrantly (I24). Frozen contexts take no guard (§8.3). | Corrected (§5.7) |
| §8.3 | Only `CreateChild`, `Freeze` and `Dispose` are refused from inside an operation of the domain. | Inside a write batch's action or a `ScanMerges` visitor, `Write` and `Union` on any context of the domain, and a `Rebuild` that would run a pass, throw `InvalidOperationException` before anything is staged or faulted (`ThreadAffinityGuard.EnterCallback`, `ExitCallback`, `InCallback`). Without this, a nested rebuild runs an epoch inside the batch's epoch, which breaks I3 and faults the child through the journal's order check; a nested ancestor write changes the parent the batch caught up with; and a write inside a visitor changes or dirties the partition being scanned. Reads, and rebuilds with nothing to do, stay allowed. The `ScanMerges` version check is kept as a second safeguard; with writes refused it cannot fire. M9.4 and M10.2 made the lift scans and a child's rule search callbacks too. | Corrected (§5.7, §6.6, §6.10, §7.1, §8.3) |
| §8.2 | `Dispose` is refused re-entrantly. | It also throws while a `ScanMerges` of the context runs, from its visitor or from another thread, through a per-context scan counter that `Dispose` closes with a compare-and-swap. A frozen context is scanned without its domain, so the re-entrance check cannot see a dispose from inside its visitor. M9.4 extended the counter to `ScanOwn` and `ScanAncestorsMentioning`. | Corrected (§5.7, §8.2) |
| §6.8, §12.7 | Checker item 1.7 compares `Gate.LiveDescendants` with the recursive count. | `DescendantGate` carries change marks (`BeginChange`, `EndChange`, `Changes`, `IsChanging`), which `CreateChild` and `Dispose` set on every gate they change. The comparison is made only on a consistent reading of the tree and the gate, with up to 16 retries. Under a frozen context whose descendants never stop changing, one run of the checker may skip the item rather than report a false violation; a context that is not frozen is always checked exactly, inside its domain. | Corrected (§6.8, §8.2, §12.7) |
| §6.6 | `Context.Write` enters the domain, then checks writability. | It checks `ThrowIfNotWritable` before entering as well, so a frozen context used from another thread reports "frozen" rather than a domain conflict. | Corrected (§6.6) |
| §6.7, §17 | The retraction guard arrives in M9.3. | Its union-find row is enforced from M9.2, because I9 depends on it: `Database.ClearTable` at a Store's root refuses to clear `$uf` while the root has live descendants. | Note (placement) |
| §8.1, §15 | `Table.Freeze` is a virtual whose default seals the pending state; `Context.Freeze` takes `FreezeWarmup?`. | In M9.2 the base `Table.Freeze` threw `NotSupportedException`, `DisplacedTable` and `OverlayDisplacedTable` overrode it, and `Context.Freeze` had no parameter. M9.3 made `Table.Freeze` abstract (§21.4) and added the parameter. | Note (interim) |
| §17 M9.2 | `UnionFindInvariantCheckerTests` has six corruptions. | Nine: I4, I5, I6, two of I8, I9, I10, I23 and the gate, the gate one also on a frozen root. The checker also covers root I4 and full compression of a frozen root. | Corrected (§17) |
| Tests | — | `ChildDatabaseTests.StagingIntoAChildIsNotSupportedBeforeItsLayersExist` became `StagingIntoAChildReachesOnlyItsUnionFindBeforeItsLayersExist`, because the interim resolver maps `$uf` to the child's `OverlayDisplacedTable`. `ContextEpochTests.RootTimestampsAreIdenticalToThePreChangeSequence` empties the calling thread's `ColumnIndex`, `TupleIndex` and `StagedOutputs` hash-table pools before each run: pooled tables keep their slot count, which changes iteration order and minted ids between runs in one process. The engine's pools, which mirror egglog's, are unchanged; this bears on decision 4. `ScanMergesTests.WritesAndStructuralCallsAreRefusedInsideTheVisitor` now asserts that the union does not land and that the context stays clean and not faulted, and has an ancestor-write case. | Note |

### 21.4 M9.3: Layered tables

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §17 M9.3 | Interim: `Rebuild` throws `NotSupportedException("Row catch-up arrives in M9.4.")` when `NeedsCatchUp`. | A narrower refusal, made before anything changes and without faulting, only when row work would be needed: (a) an unsettled ancestor's shared layer version moved; (b) the union-find changes and a live own row names an id that stops being canonical; (c) a new own loser that is not local is named by a visible ancestor row. The same refusal reaches `Write`, `ClearTable`, `CreateChild` and `Freeze` through `Rebuild`, and `Freeze` ran it before its faulting `try`. The literal interim would break M9.2's catch-up after every root union and would not refuse a child's own unions over its rows; a first, looser check left a clean child holding stale rows (found in review). M9.4 replaced the interim. | Corrected (§17); replaced in M9.4 |
| §7.2 | "A replaced row reappears as an append." | The interim, and M9.4's catch-up, rely on an invariant the text did not state: an ancestor with live descendants tombstones a row without appending one only when its rebuild folds a rewritten row into an existing row after a union. It holds because every retraction entry point is guarded and compaction is gated; `ContextTables.AncestorRowsChanged` documents it. Tombstone tracking in `AncestorCursor`, offered in review as defence in depth, was not added. | Corrected (§7.2) |
| §3.4, §6.7, §14.1, §17 | `GuardForNewState` is `null` at a root without live descendants, snapshotted when a state is created. | It returns the database's guard at every Store-backed database, the root included; only a bare database gets `null`. The snapshot was unsound: a state from `CreateExecutionState`, or a buffer from `NewBuffer`, can outlive `CreateChild` and then delete the new child's rows. `RetractionGuard.AttachRoot` gives the root's guard the root's gate, and `CheckRemove`, `CheckClear` and `CheckSubsume` return after one inlined volatile read while the root has no live descendants. A Store's root pays one guard load and one gate read per staged removal; bare databases, and so the M9.0 benchmarks, keep one null test. | Corrected (§3.4, §6.7, §14.1, §17) |
| §6.7 | Guarded entry points: the two `StageRemove` overloads and `ClearTable`. | Buffers from `Database.NewBuffer` carry the guard too (`MutationBuffer.AttachRetractionGuard`, copied by `FreshHandle`), because I22 requires it at every entry point; the check reuses the buffer's `allowsDelete` branch, so unguarded removals pay nothing, and buffers from `Table.NewBuffer` carry none. The rule `Remove` instruction consults the guard once per batch; rule deletes would otherwise bypass decision 1 and R1. | Corrected (§6.7, §14.1, §15) |
| §14.1 | (Root costs.) | Rows the design requires but the table omitted: the layered fields of `ExecutionState` (two references, 16 B per state and per clone, in M9.3; three with M10.2's `views`), `GuardForNewState` per state creation (one owner test), the serial-refresh volatile read per `IndexCatalog.GetOrRefresh`, and the guard read per staged removal. | Corrected (§14.1) |
| §6.5 | The root path of `PredictRow`/`PredictColumn` is `ReadTable(t)`. | They keep their pre-context root shape: after their own `layers` test they call `ReadRootTable` (the former `ReadTable` body, aggressively inlined) and read the spec only on a miss, through `PredictCore`/`PredictedColumn` overloads that take the table. The layered paths are `PredictRowLayered` and `PredictColumnLayered`. `ReadTable` stays the throwing dispatch point the lookup instructions use. | Corrected (§6.5) |
| §6.5 | `VisibleRowCount` in a child is `ViewFor(t).VisibleCount`. | For `$uf`, which `ViewFor` refuses (X23), it returns `Context.DisplacedInViewCount`: the parent's view count when the child was created plus the journal (I8), which is what the parent and a copy report. The text left this case undefined. | Corrected (§6.5) |
| §6.5 | `$uf` lookups use the `OverlayDisplaced` arm. | In a child, lookups, `PredictRow`, `PredictColumn` and `TryGetRow` of `$uf` go through the `OverlayDisplacedTable`'s view-level lookup, and `GetView($uf)` throws `NotSupportedException`. | Corrected (§6.5) |
| §4.3 | `Store.AddTable` takes the definition lock and, for a root that is not frozen, its domain. | The frozen flag was first read before the domain was entered, which allowed a race (found in review). It is read again after entering, and a root found frozen there takes the frozen path. | Corrected (§4.3) |
| §8.1, §15 | `Table.Freeze` is a virtual that seals pending writes. | Abstract, with an internal `HasPendingWrites`; `Database.Freeze` checks every table before freezing any of them. | Corrected (§8.1, §15) |
| §6.3, §6.7, §8.1 | — | A frozen (shared) `SortedWritesTable` refuses `Clear`. The layers of a context's views are `SortedWritesTable`s; `LayeredSchema.RootView($uf)` throws; root views live in an array that grows geometrically. | Corrected (§6.3, §6.7, §8.1) |
| §6.3 | `TableView.VisibleCount` is computed once per view. | Only its hidden-live part is cached; `LatestStamp` and `ApproximateCount` are computed on each read. | Corrected (§6.3) |
| §6.2 | `NoteUnsealed(HiddenSet set)`; `new TableLayer(this, s, Depth)`. | `NoteUnsealed` also takes the ancestor table, so it can seal with that table's row count, and `TableLayer` takes its depth from its owner. In M9.3 `LayerStatistics` held only the M9.3 counters. | Corrected (§6.2) |
| §6.2, §8.1 | `TakeRound` adopts deferred layers. | `Database.MergeTable` and `Database.Freeze` adopt them as well. | Corrected (§6.2, §8.1) |
| §6.7 | A child's `ClearTable` routes to `ContextTables.ClearTable`. | `Context.ClearTable` in a child runs the guard and decision 2 checks before it changes anything and refuses `$uf`; only a failure after those checks faults the child. | Corrected (§6.7) |
| §7.1 | DEBUG builds run the checker at the end of `Rebuild`. | Not when a contradiction stopped the merge with writes still pending. | Corrected (§7.1) |
| §15 | `FindRowHashed` is in `Tables/SortedWritesTable.Lookup.cs`. | It is in `Tables/SortedWritesTable.Layered.cs`. | Corrected (§15) |
| §6.8, §17 | The compaction gate and the row cursors are M9.4 items. | The gate was implemented in M9.3 (`MaybeRehash` and `ForceRehash` gated, root tables on the root's gate, shared layers on their owner's, `CompactionsBlocked` counting the skips), because the storage freeze compacts and children's hidden sets index row ids. `AncestorCursor` (with pending rows and stamp), `NeedsCatchUp`, `CommitCursors` and the settled-depth bookkeeping also arrived in M9.3, because the interim refusal needs them. | Note (placement) |
| §3.4 | Child query members answer through `ContextView`. | Until M10.2 the members that need views (`ProcessConstraints`, `SplitConstraints`, `IPlanStatistics`, `ColumnCardEstimator`) threw `NotSupportedException` for function tables. `EstimateSize` went through the child's `TableView` from M9.3. | Note (interim); final form in §21.7 |
| §6.3 | `LayerView.RefineVisible` calls `Table.RefineVisible`. | Until M10.1 it used `VisibleRowFilter` or `HiddenOnlyFilter` with `Subset.Retain`, because `Table.RefineVisible(Subset, HiddenRows)` is M10.1 content. | Note (interim); resolved in M10.1 |
| §6.5 | `ViewFor` reads the state's `ViewSnapshot` first. | Until M10.2 states had no snapshot, and `ViewFor` read `ContextTables.GetView`. | Note (interim) |
| Tests | — | `HiddenSealingTests`: the rule half of R1-1 was a scan of the view until rules in a child existed; M10.2 added it as `ARuleOverTheViewDerivesOnlyTheChildsValue`. `RootRetractionBypassReviewTests.ARawBufferOfAChildCannotRetractUnderAWritableParent` gained `child.Rebuild()` before its read; the test was wrong, because `NewBuffer` notifies the table when it creates the buffer (egglog's `new_buffer`) and a child with a pending notification is not clean (§7.5). `LayeredLookupTests.PredictionsUseTheWriteLookup` expected `VisibleRowCount($uf) == 0`, which encoded the defect above, and now expects 1. `RetractionTests.ARootStateWithoutDescendantsHasNoGuard` encoded the unsound snapshot; it is replaced by `AStoreStateAlwaysCarriesItsDatabasesGuard`, and `RawBuffersPassTheGuard` was added. `InterimRowWorkTests` (6 tests) was added. Earlier interim tests now assert M9.3 behaviour: `ChildDatabaseTests`, `ContextUnionFindTests.AnExceptionInAChildsWriteFaultsIt`, `DisplacedTableFreezeTests`, and a cast in `VectorizedLookupTests`. | Corrected (§17) for `RetractionTests`; note for the rest |
| §16 | — | Helper members not listed: `Context.ThrowIfRowWorkPending` and `DisplacedInViewCount`; `ContextTables.AncestorRowsChanged`, `OwnRowsMention` and `AncestorRowsMention`; `TableLayer.HasHidden`; `RetractionGuard.AttachRoot`; `MutationBuffer.AttachRetractionGuard`; `ExecutionState.ReadRootTable`. | Note |

### 21.5 M9.4: Child rebuild, catch-up, settle and dispose

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §7.4, §7.5, §6.6, §7.1 | G-b checks the journal rows of each pass. | That is unsound: the root can intern an eq-sort container naming a child's loser after the child displaced it, and the root's container rebuild can produce one. `ContainerValues.EqVersion`, shared with every environment that holds ids, moves on each new content (an `Interlocked` increment); a child records the version its guard last checked against. `IsClean` includes `IsEqGuardCurrent` (the journal is empty, or the version has not moved). When the version moved, `Rebuild` runs the guard over the whole journal before its clean test, taking no epoch when nothing else is to do, and so does each pass's guard; `PrepareForWrite` rebuilds in that case. A refusal throws `NotSupportedException` and faults the child, as G-b does. Own displacements are still the only ids checked: an intermediate ancestor's losers are its own guard's job, because a child is clean only while its parent is. | Corrected (§6.6, §7.1, §7.4, §7.5, §13 edge 37) |
| §7.4, §10.7, §13 | — | A frozen database interns no new eq-sort container (`InvalidOperationException` in `ContainerEnv`'s insert path). A frozen context never re-runs its guard, so a container interned at a frozen root naming a frozen descendant's loser could never be refused. Looking a container up, re-interning an existing one, and interning containers over base values stay allowed. | Corrected (§7.4, §13 edge 37) |
| §12.7 | C6 is a bridge check. | `Context.CheckInvariants` gains a G-b part, `CheckEqContainerElements`: no own displacement of a child is an element of an eq-sort container. This is the storage half of C6, added now because G-b is M9.4 content; `CheckInvariantsCatchesViolations` has a corruption test for it. | Corrected (§12.7) |
| §6.9, §5.6, §7.1, §8.2, §13 | Children of a stopped case: not stated. | After rebuilding a parent that is not frozen, `Rebuild` and `PrepareForWrite` (so `Write`, `ClearTable` and `CreateChild`) check whether that parent was stopped by a contradiction. A child with `StopOnContradiction` becomes contradicted, which is sound because the contradiction row is visible in its parent (I25). Any other child gets `InvalidOperationException` without being faulted; it can only be disposed. | Corrected (§6.6, §6.9, §7.1, §8.2, §13 edge 24) |
| §8.1, §8.4, §13 | `Freeze` throws on a failure not taken (edge 22). | A failure recorded and not taken is refused before the rebuild and again after the rebuild and, at the root, `Settle`; a contradiction the rebuild found is refused too. These refusals do not fault: a recorded failure is not an exception (§8.4), and the bridge's §10.8 also checks the failure outside storage's freeze. The context stays unfrozen and consistent, so the caller can take the failure and freeze again. Exceptions from the rebuild or from the database freeze still fault a child (X12). | Corrected (§8.1, §8.4, §13 edge 22) |
| §7.6, §8.2, §10.8 | What `CreateChild` does with a failure: not stated. | A failure that the parent's rebuild or the root's settle records during `CreateChild` stays recorded on the parent, as for `Write` and `Rebuild`, which also record failures without throwing. The child, which has its own failure slot, is still created. Taking that failure right after `CreateChild` is the bridge's job (M10.3). | Corrected (§8.2, §13 edge 24) |
| §6.6, §8.2, §14.2 | Rebuild indexes are refreshed when read. | `CreateChild` of a context that is not frozen brings the rebuild indexes of that context and of its non-frozen ancestors up to date (`RefreshRebuildIndexesForChildren`, `ContextTables.RefreshOwnRebuildIndexes`), as root work at child creation (§1 item 11): O(tables) plus the appends since each index's last refresh, all rows the first time or after a compaction between child lifetimes. Afterwards a child's step 4 and its lift reads refresh a writable ancestor's index only from that ancestor's appends since the child's creation. That is per-child work of the same order as its catch-up's reading of those appends, which §14.2 did not list. | Corrected (§7.3, §8.2, §14.1, §14.2) |
| §14.1 | (Root costs.) | Rows the table omitted: interning a new eq-sort container (only on a miss: one frozen check and one `Interlocked` increment); new content in a root container rebuild (one `Interlocked` increment); the G-a test (one field load at the root, as `InChildContext` is tested before `HoldsIds`); one `layer is not null` test per table rebuild in `SortedWritesTable.RebuildWith`; one `DirtyCount` compare per pass in `Database.RebuildPass`; the stores of `LastRebuildRows` and `LastRebuildWasIncremental` per rebuild; one bool per table for the pass result; the rebuild-index refresh at `CreateChild` of a writable root. | Corrected (§14.1) |
| X37, §14.2 | An empty layer costs about 1–2 KB. | Dry runs measure a bare child at about 7.4–9 KB and an empty layer at about 2.7–3.0 KB (`ManyChildrenBenchmarks`: `ChildWithOneEmptyLayer` minus `ChildOnly`). Most of the layer cost is `PendingState`'s two `ConcurrentQueue`s per shard (a 32-slot segment each), `TableInfo`'s two `IndexCatalog`s, two `RowBuffer`s and the O(tables) catalog copy. Reducing it means changing root hot paths (the pending queues), so it is left to M10.4 (§21.10). | Corrected (X37, §14.2) with the measured figures |
| §15 | `Database/Database.Rebuild.cs`: no change in code. | `RunWithTakenOut`, `RunOnTables`, `RunOnTablesCore` and `RunOnTablesParallel` return whether any table reported a change (each such table is still notified); `RefreshDirtyContainerIds` (§7.4) needs the result. Root cost: one bool per table. | Corrected (§7.4, §15) |
| §7.6, §15 | `SettleAppends` calls `MarkRebuilt`. | `Settle` moves every function table's rebuild mark after its `MergeAll`, past the merged rewrites, so the next `CreateChild` does not settle the same rows again. `HasRowsSinceRebuild` is false for tables without rebuild columns. `RebuildFull` was refactored into `RebuildRange`, which `SettleAppends` shares, and the internal diagnostics `LastRebuildRows` and `LastRebuildWasIncremental` were added; the root path is unchanged. | Corrected (§7.6, §15) |
| §6.2, §6.6 | `HasRetractions` is set when `DeleteLayered` hides an ancestor row. | It means that the context retracted rows of the table: `RetractionGuard` also marks the layer when it allows a delete or a subsume. The I14 checker needs this. | Corrected (§6.2) |
| §7.2, §7.3 | The loops as written. | Three equivalent, cheaper forms: step 4 leaves out own losers that are local to the child (I10); step 3 is skipped when no id was displaced since the last pass (X38); `CatchUpRows` and the I16 generation check skip ancestors whose change stamp did not move. Context-private tables are left out of the row cursors and the generation check (decision 1: descendants never read them). | Corrected (§7.2, §7.3) |
| §6.10 | `ScanOwn` and `ScanAncestorsMentioning` require `IsClean` and throw on the root. | They follow M9.2's rules for `ScanMerges`: they enter the domain of a context that is not frozen, run the sink as a callback, and count as running scans. `ScanAncestorsMentioning` returns nothing for context-private tables and delivers each row once. | Corrected (§6.10) |
| §12.7 | The tables checker covers I12–I18. | Its cost is proportional to the context. I12 and I15 are checked over own rows and own hides; the ancestor part of I13 through the ancestors' rebuild indexes and the rows appended since creation; I14 by `Find` for constructors, by key for relations, and otherwise by the table's merge on a scratch catalog. I35 is a separate `CheckRootSettled`, run in DEBUG at `CreateChild` and `Freeze` of the root. | Corrected (§8.2, §12.7) |
| §7.1 | `Rebuild` returns whether any pass changed something. | It returns false when the catch-up only moved cursors and copied nothing. The settled depth advances only when a rebuild runs passes. | Corrected (§7.1) |
| §10.7, §17 | Container registration with `holdsIds` is an M10.3 item. | `ContainerValues.RegisterType` gained `holdsIds`, and `ContainerEnvBase` gained `HoldsIds`, `AnyContainerMentions` and `EqVersionCounter`, in M9.4, because G-a and G-b are M9.4 content. The bridge passes `holdsIds` when it registers containers since M10.3 (§21.9). | Corrected (§10.7); placement |
| §17 M9.4 | The facade canonicalizes Id cells through `Context.Find` before every write. | On spike seeds it canonicalizes through `Context.UnionFind` inside the batch, as the spike requires. On spike-free seeds of `ContextDifferentialTests` and odd seeds of `ContextCopyTests` it has a raw-write mode (`RawWrites`, `RawChildWrites`): child writes pass id cells as given with timestamp 0, so X38's canonicalization and stamping are exercised. Root writes stay canonical, because a stale raw root write is legitimately invisible until the next rebuild (X42). Adopted from review. | Corrected (§17) |
| §17 M9.4 | Gate harness as listed. | Strengthened in review and adopted unchanged: `SpikeTestSchema` has a `bits` table with `OrMerge`, a lattice join that differs from both inputs, so a merge that stores the incoming row instead of the output is caught; `SpikePair.Check` compares `ScanColumn` with the visible rows as exact multisets, not only for completeness; the spike is the second oracle on even seeds, which retract nothing; `ContextCopyTests` runs 400 seeds. | Corrected (§17) |
| Tests | — | `InterimRowWorkTests` was deleted; its case `AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs` still holds and is restored verbatim in `Layers/CatchUpTests`. `SoundnessReviewTests` asserts the catch-up instead of the refusal. The `ChildDatabaseTests` container uses `holdsIds: false`. | Note |
| §16 | — | Types not listed: on the test side `SpikeTable`, `SpikeReferenceOpKind`, `SpikeProgram`, `Coverage`, `DisposingSink` and `OrMerge`; on the benchmark side `RootWorkload`, renamed from `FrozenRootWorkload`, with a freeze flag so that `PerChildCostBenchmarks` also measures writable roots (its `FrozenRoot` parameter). | Note |

### 21.6 M10.1: Visibility in scans, subsets and indexes

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §9.3, §14.1 | The existing plain overloads forward `HiddenRows.None`; every scan call pays one `IsNone` test. | The plain overloads keep calling the plain sealed scans, and the new hidden-aware overloads fall back to them when given `None`. The plain index-build overloads likewise stay unchanged, and the hidden-aware ones fall back to them: `ColumnIndex.BuildForSubset`, `RebuildForSubset`, `RebuildFull` and `MergeSubsetVisible`, `TupleIndex.BuildForSubset` and `MergeSubsetVisible`, and `SparseColumnIndex.Build`. Results are the same, and the root, and today's per-probe and per-node builds, no longer pay the one `IsNone` test per call that §14.1 budgeted (the root pays nothing). T2 and T3 assert that `None` matches the plain overloads bit for bit; `HiddenRootCostReviewTests` asserts zero allocation on both paths, and that `RefineVisible(None)` allocates what `RefineLive` does. | Corrected (§9.3, §14.1) |
| §9.2 | `LayeredSubset` and `SubsetArrayPool` as sketched. | `Clone` takes an optional `SubsetArrayPool`; `SingleLayer` is −1 for a multi-layer subset; `SingleRows` throws `InvalidOperationException` for a multi-layer subset; `LayerMask` of an empty single-layer subset is 0; `FromLayers` accepts arrays of any length up to 32, takes ownership of them and disposes empty entries. `SubsetArrayPool` keeps only arrays of exactly `LayerCount` entries, at most 1024 (`MaxRetained`), and checks in DEBUG that returned arrays own no buffers. | Corrected (§9.2) |
| §9.3, §16 | — | Helpers the text did not list: internal `TupleIndex.MergeSubsetVisible`, matching `ColumnIndex`'s; `LayerView.AnyVisible(SubsetRef)`, which `TableView.AnyVisible` forwards to; private `SparseColumnIndex.Group`; private `ColumnIndex.RentPairs` and `GroupPairs`, which both `RebuildFull` overloads share. The root's `RebuildFull` behaves as before. | Corrected (§6.3, §9.3) |
| §6.3 | `AllRows` gives `Dense(0, n_l)` per present layer with rows; `AnyVisible` serves zero-variable atoms. | `n_l` is each present layer's physical row count, tombstones and hidden rows included, and present layers without rows are left out. `AnyVisible` returns false for an absent or out-of-range layer and for an empty subset, and throws `ArgumentOutOfRangeException` for a subset that extends past the layer's rows. | Corrected (§6.3) |
| §6.3 | — | Resolves M9.3's interim (§21.4): `LayerView.RefineVisible` is now `Hidden.IsNone ? Table.RefineLive(s) : Table.RefineVisible(s, Hidden)`, exactly as §6.3 says. | Note |
| §1, §3.1, §3.4 | A child shares the root's counters, external functions, dependency graph and containers by reference. | It shares the root's `BaseValues` too (rule-layer v2 §13 item 3, filed as an M9.3 defect and fixed at the start of M10.1): engine code that reads base values through `ExecutionState.BaseValues` in a child would otherwise see an empty registry. The root's registry is created when the first child is created, if the root never used it, as its container environments already were. Cost: one small object, once per store. | Corrected (§1 item 1, §3.1, §3.4) |
| §15 | — | The hidden-aware overloads made 18 existing `cref` attributes ambiguous (CS0419); they now write out the overload signatures, and the touched paragraphs were rewrapped to the repository's width. With whitespace normalized, the generated XML documentation is identical apart from deliberate wording edits. A doc-only change outside §15's list. | Corrected (§15) |

### 21.7 M10.2: Layered query execution

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §3.4, §9.1, X21, I29 | Rule runs in a child are unchanged in shape; `Build` records `ViewGeneration`, and `RunRuleSet` asserts it unchanged. | `RunRuleSet`, `ExecuteRuleSet` and `MergeRuleSet` in a child run as the context's operation. They enter the context's domain, refuse a call from inside a domain callback, and require current views. They throw `InvalidOperationException` when the view generation, or the chain's stamp (`ContextView.Stamp`: the sum of `Database.ChangeStamp` over the context and its ancestors), moved since the rule set's first header was evaluated; the builder records both then, not at `Build`. They refuse a timestamp below `max(SemiNaiveFloor, the context's latest epoch)` with `ArgumentOutOfRangeException` (`Context.UseEpoch`). The search runs as a domain callback, so a nested `Write` or `Rebuild` from an action or external is refused, and an escaping exception faults the child (X12). `MergeRuleSet` applies the domain, the floor and the fault rule. Every refusal happens before anything runs and does not fault. Why: header subsets name row ids, which a merge, hide, clear or compaction anywhere on the chain changes without changing any view's structure, and a timestamp below the floor would break I3, I19 and I21. Refusing in `Build` was considered; the run is refused instead, because views can also move between `Build` and the run. The root keeps egglog's rule: build right before running. | Corrected (§3.3, §3.4, §8.3, §8.4, §9.1, X21, I29) |
| §3.5, §7.5, §9.1 | Reads, and so rule building, require `IsClean`. | Views are current when `IsClean` holds without its pending-notifications term, at every level of the chain (`Context.IsViewCurrent`): writes staged and not merged are invisible, as at the root (X42). `ContextView.Evaluate`, a child's `Snapshot` (when it reads any table) and the planning statistics require it in a child. `EstimateSize` keeps M9.3's unchecked behaviour. Building an empty rule set needs no current views. | Corrected (§3.3, §3.5, §7.5, §9.1) |
| §9.1 | `RunRuleSet` throws `ArgumentException` unless the rule set was built on this database's view. | A rule set assembled by hand through `RuleSet`'s public constructor (no view) still runs on a database that is not layered, its views pinned when the run starts. It is refused with `ArgumentException` on a layered database. | Corrected (§9.1) |
| §8.1, §9.10, §3.4 | `FreezeWarmup.From(IEnumerable<PlanTemplate>)`. | `FreezeWarmup.From(plans, IQuerySchema)`, which needs the schema to know which columns are cacheable. `IQuerySchema` gains a default member `IsCacheable` (`true`), which `Database` answers from `TableSpec`. | Corrected (§3.4, §8.1, §9.10, §15) |
| §9.8 | `CanonicalConstants.Compute` builds the canonical-or-written vector. | It returns the cached plan's written vector itself (`CachedPlan.WrittenConstants`) when no constant changes, and always for a bare database. Same semantics, without an allocation per rule per run. | Corrected (§9.8, §14.1) |
| §9.1, §6.3, §14.1 | Root views are `LayeredSchema.RootView`s; each rule set gets one snapshot array. | Four differences. `TableView.ForTable` builds single-layer views over any table kind, so root and bare-database views cover `$uf` and tables that are not `SortedWritesTable`s, and `AllRows` of a one-layer view is the table's `All()`. `ContextView.ForRoot` caches its own root views rather than using `LayeredSchema.RootView`, so bare databases work too. Every root rule set shares one `ViewSnapshot` over that cache, which is sound because its entries are only ever filled; a new one is made only when the cache array grows. A root snapshot leaves out the tables of action lookups, which root lookups never read through it (§6.5). Why: root rule-set builds allocated more than in M10.1 (found in review). | Corrected (§6.3, §9.1, §14.1) |
| §9.4 | With no present layer, `Evaluate` splits and returns `Empty`. | It classifies the constraints on the root's entry of the table, or on the database's own entry for synthetic layers. When layers classify a constraint differently, it counts as fast overall, and the layers where it was slow refine by it with `RefineOne`, as the text says. | Corrected (§9.4) |
| §9.5 | `TrieNodePool` owns a `SubsetArrayPool`. | `TrieNodePool.Layers` is `null` for one-layer runs, so a root run allocates no array pool. | Corrected (§9.5) |
| §9.5, §16 | `TrieNode`'s `Subset subset` becomes `LayeredSubset rows`. | A single-layer node, which includes every root node, keeps a plain `Subset` plus a layer byte inline. A multi-layer node's rows, and the spare indexes of a recycled node, live in a new type, `TrieNodeSideState`, created on first use and kept through the pool, so a warm serial run allocates none; a concurrent multi-layer child node allocates one. `IsMultiLayer` is one flag test. Cache arrays are sized from the rows' highest layer, and growth copies the old entries. Why: §1 item 11, because a parallel root run allocates a node per frame at positions 0 and 1, and the inline `LayeredSubset` grew the node from 64 B to 80 B (found in review). It is 64 B again. | Corrected (§9.5, §14.1, §16) |
| §9.5, §14.1 | The prober's size is not stated. | A `Prober` is 72 B (48 B in M10.1), because a `ProberPiece` carries `Layer` and `View` and the prober carries `Rest` and `RestCount`. It is per-position scratch, allocated once per join task, not per frame or row; a parallel root run therefore pays it per morsel. The `LayerHits` per-position array is now allocated on first use, so a root run never allocates it. Measured against the M10.1 build: parallel triangle probe 991 KB vs 970 KB (+2.2%); parallel 200k math probe 14.14 MB vs 14.135 MB (+0.05%). Shrinking the prober would mean moving `View` and `Rest` out of the piece in every kernel; that is left to the idle-machine gate (§21.11). | Corrected (§9.5, §14.1) |
| §9.8 | `CachedPlan` keeps at most 16 variants. | When the cache is full, a new K's variant replaces the oldest (copy-on-write, under the lock). A first version stopped caching once full, which made a Store-backed root rebuild its variant on every run after 16 canonical changes (found in review). | Corrected (§9.8) |
| §9.8 | `RuleBuilder` records an `ActionIdConstant` for every constant Id operand. | They are recorded once, in `RuleBuilder.Build`, by scanning the program through `ActionSubstituter.Operands`, so the operand order is defined in one place. A substituted `ActionInfo` keeps the written `IdConstants`. `QueryEntry` equality ignores the `IsIdConstant` flag, since a flagged and an unflagged constant of one value are the same operand; `WithConstant` keeps the flag. | Corrected (§9.8, §15) |
| §9.1, §9.5 | `Build` collects the atom and lookup tables; `CloneForMorsel` copies `views`. | A child's snapshot leaves out `$uf` lookups, because a child reads `$uf` through its overlay table (X23). The tables a rule set reads are collected by `ContextView.Snapshot(rules, actions, skip)` through `RuleSet.LookupTable`, without a list; `RuleSet.ReadTables` was removed. `JoinRun.CloneForMorsel` copies the roots array, which M10.1 allocated anew; tasks only compare against it, for counter attribution. | Corrected (§9.1, §9.5) |
| §12.7 | `ExecutorInvariants` checks view coherence, frozen catalogs, sort order, creation stamps and watermark bounds. | It checks row-level properties (sort order and run index, creation stamps, canonical cells, unique keys) on the context's own rows only. It skips its view checks while any hidden set is unsealed, because the tables part reports I18. It tolerates two states that are not violations: an empty layer created without a merge (X37) that a cached view does not have, and a `LayerView` that still says not frozen after its context froze. Watermark bounds are checked on `SemiNaiveFloor`; per-rule watermarks are bridge state (M10.3). | Corrected (§12.7) |
| §17 M10.2 | Done when, with `EGGNOG_COUNTERS`, ancestor-layer scanned rows and dynamic-index rows are 0 once `W ≥` every root stamp. | Counters are kept per database (`Database.ExecutorCounts`), behind a static readonly switch set from `EGGNOG_COUNTERS`, which the JIT folds away when off; the test project's module initializer turns counting on for the suite, and `EGGNOG_COUNTERS=0` turns it off (the counter tests then skip). Ancestor rows are counted in two kinds: rows of root nodes, narrowed by headers only, and rows of refined nodes (probe results). The 0 is read on the root-node counters, which measure the work that grows with the root; refined rows are bounded by §14.2's probe-results term. It holds for size-ordered plans (Gj, the default, and PureSize); MinCover deliberately covers with the widest atom, so its plan scans the old side, and a test documents this. | Corrected (§14.1, §17) |
| §3.4 | Child schema and planning members (carried over from M9.1 and M9.3). | Final form: `IQuerySchema.SplitConstraints` uses `ConstraintProcessor.Split` on the root's entry; `IPlanStatistics` and `PlanQuery` go through `View`; the single-table members (`ProcessConstraints`, the instance `ColumnCardEstimator`) refuse every table in a child with `NotSupportedException`, with the X23 message for `$uf`. | Corrected (§3.4, §15) |
| §9.2, §9.5, §15 | Mechanical test updates. | `ProberTests` constructs `new ProberPiece { ... }`, and `Prober.TryGetSubset` forwards to its first piece. `LayeredSubset` gained `TakeSingle` and a single-layer `GetEnumerator`, so `PlanInstantiationTests` and `NaiveRuleRunner` compile unchanged over `LayeredSubset` headers. | Corrected (§9.2, §9.5) |
| M10.1 finding | (M10.1 recorded a hazard: a child run that throws leaves writes staged in layers its search created, because `MarkStagedWrites` and `DiscardStagedWritesSince` visit only `Database.tables`, and the next `MergeAll` would adopt and merge them, against D18 and X18.) | Both `RunRuleSet` and `ExecuteRuleSet` set `InSearch` in a child, as the finding asked, and an exception escaping either faults the child (X12). Every later call except `Dispose` is then refused, including the run members, so those writes are never merged through the context. `DiscardStagedWritesSince` still visits only adopted tables, and `Database.MergeAll` and `MergeTable` do not test the fault flag. | Note; decided in M10.3 (§21.9) |
| §3.3, §9.1, §16 | — | Helper members not named in §3 or §9: `Context.ViewStamp`, `IsViewCurrent`, `ThrowIfViewNotCurrent`, `UseEpoch` and the `lastEpoch` field (`BeginEpoch` now records the largest epoch it returns); `ContextView.Stamp`, `Source`, `CanCanonicalize` and the internal `Snapshot(rules, actions, skip)`; `RuleSet.View`, `ViewStamp` and `LookupTable`, and an internal `RuleSet` constructor that takes the stamp; `ViewSnapshot.Wraps`, `Count` and `Require`; `TrieNode.Initialize(int, Subset, bool)`; `CachedPlan.WrittenConstants` and `CanonicalConstants.Written`. | Corrected (§3.3, §9.1, §9.5, §15) |
| §17 M10.2 | Tests as listed. | `ChildDatabaseTests`' M9.1 interim assertions now expect M10.2 behaviour: a rule set built at the root is refused in the child with `ArgumentException`, and an empty rule set returns `Empty`. `DiffSemiNaiveStep` gained a `Split` helper so that T9 shares its variants; T9 inserts a layered step before every semi-naive step of the generated programs, and the programs of the existing profiles are unchanged. The review probes `ParallelRootMathAllocationProbe` and `RootConstantVariantCostTests` write their report file to the test binary directory (`AppContext.BaseDirectory`), as the other review probes do, instead of the current directory, so a run from the repository root leaves no `rootcost.txt` in the working tree; `RC_OUT` still overrides it, and the assertions are unchanged. Every other review test was copied unchanged. | Corrected (§17) for T9; note for the rest |

### 21.8 Edits asked by rule-layer spec v2

Rule-layer spec v2 §13 asks nine edits of this document. Three need no new code here and are applied in this revision:

1. **Citations.** §5.7 cites rule-layer D8 and I33 cites rule-layer §6.9; the earlier citations ("§2.1", "§3.9") were version 1's section numbers. The "callback rules" of X41, §10.2 and I31 stay in the text, although rule-layer v2 defers callbacks.
2. **§10.7.** The sentence saying that the rule layer registers its `ValueSet<T>` as a container is dropped: the rule layer registers no container, and `ValueSet<T>` is an interned base value.
3. **`BaseValues`** in a child: §21.6.

Items 4–9 change bridge and lift APIs. Items 4–7 are applied with M10.3 (§3.5, §10.1, §10.8, §10.10; §21.9); items 8–9 are applied with M10.4 (§11.2, §11.3; §21.10).

### 21.9 M10.3: Per-context rules in the bridge, including concurrent children

M10.3 was built in four parts (the bridge on a store; the differential oracles; concurrent children and push/pop conformance; extraction over a context, rule-layer E5), merged, reviewed for soundness, root cost, oracle strength and completeness, and fixed in four rounds, the last three answering verification probes. The rows below record every departure the parts, the merge and the fixes reported. Rule-layer v2 §13 items 4–7 are applied: item 4 (`NewLocalSlot` with a factory) and item 5 (E4, E7, E9) in §10.1 and §3.5, item 6 (E10) in §10.8, and item 7 (E5) in §10.10.

**The bridge on a store (§10.1–§10.8)**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §10.3 | `RunRules` throws `PanicException` right after the run, before any rebuild or clock advance (egglog). | M8's fix of map §8.1 row 22 is kept: the iteration finishes its bookkeeping (the clock advance or the rebuild), then throws the first failure; a failure the rebuild records belongs to the same failed iteration and is dropped. M8's tests encode the fix. | Corrected (§10.3) |
| §10.3, X18 | Progress is committed when the run returns without a recorded failure. | When the merge itself throws, progress is committed unless a failure was recorded: a merge cannot be undone, so the matches must not be applied twice (M8's `AnExceptionWhileMergingKeepsTheWatermarks`); a child is faulted anyway. An exception in the execute phase commits nothing and does not advance the clock. | Corrected (§10.3) |
| §10.3, X42 | Writes staged raw at the root merge in the run's `MergeAll` at T, as in egglog. | Not when every rule was pruned: egglog's `run_rule_set` then returns before `merge_all`, yet the run's T becomes every rule's watermark, so after two such runs a raw row merged later lies below the watermark and is never matched, at the root and in children that inherit (found in review). The bridge merges at T itself when the rule set is empty at the root (map §8.1 row 26). The internal `EGraph.IncrementTimestamp` can still stamp a raw row below a later watermark, as egglog's `inc_ts` can; only a test uses it. | Corrected (§10.3, X42) |
| §10.3 | A rule listed twice uses T. | As written, but the earlier listing is found in O(1) per rule (a scan back over at most 16 listings, a dictionary beyond): the first form scanned every earlier listing, n(n−1)/2 compares per run for n rules, where egglog and M8 are linear (found in review). | Corrected (§10.3) |
| §10.1, §10.3 | The root-only egglog API delegates to `Root`. | `EGraph.RunRules` only flushes the root's bridge-side host writes; it does not rebuild first, because M8's tests merge unions raw and then run rules on tables left non-canonical, as egglog allows. `EGraph.FlushUpdates` does not throw a recorded failure; the next run reports it, as in egglog. Only the `EGraphContext` API flushes, ensures clean and throws. | Corrected (§10.1) |
| §10.4, §14.1 | `AddValues(rows)` stages each row into `HostWrites`, then flushes; every root write goes through `HostWrites`. | The egglog-compatible `EGraph.AddValues` and `AddTerm` stage straight into database buffers stamped with the clock, as M8 did, then flush. The first form kept the root's arenas, sized to its largest batch, for the e-graph's lifetime (about 6.5 MB after a 200,000-row load) and copied every row into a temporary list (33 B per row), against §14.1's "nothing per staged insert" (found in review). `HostWrites` now keeps at most 4096 cells and 1024 rows or unions across a flush. The context API's `AddValues` stages each row as it validates it and truncates on a refusal. The bypass first published the batch's buffers and only then replayed the rows the root had staged earlier through the context API, so the earlier write merged last and an order-dependent merge kept the wrong value (found in verification); `AddValues` and `AddTerm` now flush the root's host writes first when any are staged (§10.4). The raw escape had the same defect: `EGraph.WithExecutionState` and `EGraphContext.WithExecutionState` staged into pending buffers while earlier context-API writes waited in `HostWrites` until a later flush replayed them after the raw rows, so `StageRow` then a raw write then `FlushUpdates` (or `RunRules`, or `AddValues`) merged the earlier write last (found in the second verification). Both now flush the context's host writes first too, through the same test; `EGraph.WithExecutionState` enters the root's domain, as `AddValues` does. The third verification found three gaps around that. The raw escape did not mark its action as a callback of the domain, so a `RunRules` issued inside it committed a watermark above the action's rows, which then merged below it and were never matched (I31); and a `StageRow` issued inside it, before a raw row for the same key, merged after that row. The action now runs as a callback (as `Context.Write`'s batch does), and every bridge write and run refuses inside a callback (§8.3). And the internal raw iteration (`EGraph.RunIteration`, `RunRuleSet`) did not flush earlier context-API writes first; it now does, through the same test, and refuses inside a callback. | Corrected (§8.3, §10.4, §14.1) |
| §10.4 | Before a raw path stages, the context flushes what it staged earlier through the context API. | The flush comes before the raw path reads its input. `EGraph.AddValues` takes an `IEnumerable` and stages each row as it reads it, so a batch refused because a row is malformed has already merged the earlier context-API writes, while nothing of the batch is published. Those writes would have merged at the next flush anyway; checking the batch first would take a second pass over it, or a copy, which the bypass exists to avoid. | Corrected (§10.4) |
| §3.5 | The bridge calls `Context.Write` and `Database.RunRuleSet(rs, level, T)`. | Host writes replay into an `ExecutionState` after `PrepareForWrite` and merge with `MergeAll` (§10.4). A run calls `ExecuteRuleSet` and `MergeRuleSet` separately, as §10.3's outline does, so progress is staged between the two and an exception while merging is told apart from one in the execute phase (X18); an empty rule set at the root merges with `MergeAll`. Only the internal raw iteration calls `RunRuleSet`. `CreateChild` and `Dispose` are bridge members (E7). | Corrected (§3.5) |
| §10.4, §6.7 | `ClearTable` maps to `Context.ClearTable`. | It also drops the rows of the function staged here and not flushed, after the clear succeeds; `EGraph.ClearTable` goes through the root context. Without this a clear issued after a staged row took effect before it, against egglog's `clear_table` and `EGraph.ClearTable`'s own documentation (found in review). | Corrected (§10.4) |
| §10.2, §14.2 | `RuleProgress[] progress`, indexed by rule id. | A small open-addressed map from rule index to progress. An array made a context pay O(highest rule index) whatever it ran: a child's first commit grew it to the rule table's capacity, 16 B per defined rule (64 KB for 4001 rules, found in review). The rule layer defines one rule per query. | Corrected (§10.1, §10.2, §14.2) |
| §10.2 | `RuleTable` is copy-on-write. | Written under a lock; the slot array is copied only when it grows, slot writes are volatile, and readers are lock-free. Copying on every change costs O(n²) when one rule is defined per query. | Corrected (§10.2) |
| §10.2 | `RuleInfo` drops `LastRunAt`. | A read-only `LastRunAt` reads the root context's committed progress, and `CachedPlan` is getter-only; the stored field is gone. M8's tests read root watermarks. | Corrected (§10.2) |
| §10.2, §6.1 | `LookupOrInsertDefault` is query-determined on a table whose merge is `UnionIdMerge`. | `LayeredTableSchema.IsConstructor` also recognizes bridge constructors (a `FunctionTableMerge` over a union-id merge) and gains `ConstructorValueColumn`; a lookup-or-insert is query-determined only when it reads a key column or the union column, not a subsumption or timestamp cell. Otherwise `ActionReadAnalysis` refused inheritance to every term-creating rule. | Corrected (§10.2) |
| §10.6 | The bridge rule builder appends the early stop. | It is appended when the rule is lowered (planned): a rule planned before `DesignateContradiction` keeps its plan without it, and its insert still sets the stop flag when it merges. The serial executor runs actions in 128-lane batches, so the stop cuts a search once a batch has run. | Corrected (§10.6) |
| §10.6 | `PanicFunction` reports through `state.ReportFailure`. | `PanicFunction` and `LazyPanicFunction` do, and also set the e-graph's side channel for root or bare states only, a root-only compatibility path for M8's tests; a child's panic never reaches it. | Corrected (§10.6) |
| §10.1, E4 | `DesignateContradiction` must be called before any child exists. | Refused once the store has ever had a child (`Store.HasHadChildren`), stricter than "while a child exists". The check and the designation run under the store's definition lock, under which the first child notes itself before its constructor reads the contradiction table; the first form checked under the bridge's gate only, so a first `CreateChild` on another thread could interleave and miss the designation (found in review). | Corrected (§10.1) |
| §9.8, §15 | The bridge lowers Id-typed constants through `QueryEntry.IdConst`. | Only constants that land in no table column (external arguments, `AssertEq`/`AssertAnyNe` operands, the default of a union-find lookup), and only when typed `ColumnTy.Id`. Table-column constants are canonicalized by the core through the table's id columns. Bridge constant types are diagnostic only (egglog's constants are untyped), and the semi-naive differential types value constants as Id, which a type-based flag canonicalized wrongly. | Corrected (§15) |
| §10.7 | `GetContainerValue` registers as needed. | Registers the type on first use only at the root; a child needs it registered, since registration is a definition of the e-graph. | Corrected (§10.7) |
| §10.8, E10 | `FlushUpdates()`; `Rebuild()`; throw on a failure; plan every live rule. | The flush runs only when host writes are staged (raw pending writes are merged by the storage freeze's rebuild, which the review probe `FreezeMergesARowStagedRawAtTheRoot` confirms); a recorded failure is thrown as `PanicException` and the context stays unfrozen; `Freeze(planRules)` plans exactly the given rules (E10). | Corrected (§10.8) |
| §8.2, §21.5 | Taking a failure recorded during `CreateChild` is the bridge's job. | `EGraphContext.CreateChild` takes it right after: the new child is disposed and `PanicException` is thrown; the parent stays usable. | Corrected (§10.1) |
| §21.7 (M10.1 finding) | Decide whether an exception in a child's `ExecuteRuleSet` keeps faulting it. | It does (X12). The writes the failed search staged into deferred layers are discarded (`ContextTables.DiscardDeferredStaged`), and `Database.MergeAll` and `MergeTable` refuse a faulted child, inside the existing layers test (zero root cost). Documented in `Database.Execution.cs`. | Corrected (§15) |
| §12.7 | C2 reports host writes held by a frozen context. | `CheckInvariants` refused any unclean context before checking, so that item could never be reported (found in review); host writes held by a frozen context are now exempt from the precondition and reported as C2. C4 (member maps) needs `MemberMap` and moves to M10.4. | Note |
| §10.1 | — | Members the text did not list: `EGraphContext.Parallelism` (the context's heuristics, used by `ConcurrentChildren`), `Owner`, `Depth`, `Options`, `IsClean`, `SetProgress` and `CommittedLastRunAt`; `Context.Host` and `LastRebuildPasses`; `ExecutionState.TryLookupColumn` and `VisibleRowCount`, through which `TableAction` reads the running context's view (it read `state.GetTable`, which throws in a child). | Corrected (§10.1, §15) |

**Concurrent children (§10.9)**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §10.9 | Each body runs under a lease. | A lease is `min(child DOP, Total)`, and the degree uses the same capped value. `Lease` and `TryLease` refuse a request outside `[1, Total]`, which could never be granted, with `ArgumentOutOfRangeException`; over-release throws `InvalidOperationException`. Waiters are not served in arrival order. | Corrected (§10.9) |
| §10.9 | — | A run nested inside a body runs its children serially under the lease the thread holds (a per-thread count): the outer run holds the whole budget, so leasing again would block forever. Two overloads (`body(child)`, `body(child, index)`); a null, repeated, disposed or root child and a child of another e-graph are refused before anything runs; a one-child run, or a run on a thread that holds a lease, does not go through `Parallel.For`. | Corrected (§10.9) |

**Extraction over a context (§10.10)**

§10.10 is new in this revision: it was written with the code, for rule-layer v2 §13 item 7 (E5). The rows record where the built design departs from E5's wording; rule-layer N44 records the same from that side.

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §10.10, E5 | Staleness is keyed on the extraction schema's tables and the union-find of the context and its non-frozen ancestors, never on the clock. | Keyed on the tables of the extractor's slots (the schema functions it can use under its options), not on every function the schema declares: a write to a function the extractor never reads cannot change its answers. A compaction renumbers rows without changing them and still counts as a change; an empty layer that a no-op write created marks as an absent one. | Corrected (§10.10) |
| §10.10, §10.5 | — | Only creation requires the context clean; each query checks the watch instead. A non-frozen ancestor's write to an unrelated table leaves the child behind its parent, yet the rows the extractor reads, and their canonical form, are as they were. | Corrected (§10.10) |
| §10.10, §8.3, E5 | On a frozen context, any number of extractors run on any threads; a writable one is not discussed. | A context-form query enters the context's domain while it is not frozen and runs as a callback, so a second thread fails fast and a cost function may read the context but not write it. The root forms hold nothing, as egglog's root does not. A single extractor stays single-threaded, because queries extend its caches. | Corrected (§8.3, §10.10) |
| §10.10, §16 | — | Members E5 did not list: `Extractor.Context`; the internal `EGraphContext.EnterExtraction`, `ExtractionWatch`, `ExtractionTableMark` and `ExtractionQueryScope`. `ComputeCostsFromRootSorts` and `FunctionToDag` stay root-only (§21.11). | Corrected (§10.10, §16) |

**Differential oracles (§17 M10.3)**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §17 | `ReferenceRuleContext` runs `NaiveRuleRunner` naively, and extends `SpikeReferenceContext`. | It evaluates the programs' own rule IR (`ColorRule`, `ReferenceRuleEvaluator`) semi-naively by row content, with §10.2's watermarks, and wraps the sealed `SpikeReferenceContext` (which gained `Derive`, `DeriveUnion` and the `Derived` op kind). `NaiveRuleRunner` would run bridge rules through the planner and action interpreter under test, and a naive reference disagrees with egglog's semi-naive semantics in the history family (family A seeds 5, 11, 45 and 73 failed against one). The reference is therefore not independent of the watermark rule; inheritance soundness rests on the seeded-versus-unseeded property, and the reference decides `AllowInheritance` from the rule itself. | Corrected (§17) |
| §17, X41, I31 | Seeded == unseeded holds at every checkpoint for both families. | Only in programs whose deletes and clears touch tables no rule writes (`ColorFamily.DeletesOnlyUnwritten`): a child that retracts a derived fact re-derives it unseeded and not seeded, as egglog after a push. Likewise X41's "the row exists after the ancestor's run" fails once a child hides a constructor row. Under retraction, inheritance gives egglog's push semantics. | Corrected (§17, X41, I31) |
| §17 | Seeded match totals are ≤ unseeded. | Engine match counts depend on the plan (a full run counts distinct bindings of the variables the actions use; a semi-naive run sums over its variants; seed Bh:3 counted 11 seeded against 10 unseeded with equal views). Seeded programs add a no-op primitive over every body variable (`ColorSchema.Keep`), which makes both counts distinct full bindings. | Corrected (§17) |
| §17 | Family A modulo renaming; family B by term names. | Family A is compared cell for cell (stronger: both sides make the least id canonical). Family B leaves out the id-order primitive, never deletes or clears constructor tables, and generates rules in strata so minting terminates. | Corrected (§17) |
| §17 | The monotone family excludes `AssertNe`, id-order primitives, deletes and table reads in actions. | Also rule bodies that read a lattice function's output. Such a body is not monotone once a write lowers the value: a soak found seed 7000246, where a grandchild derives from `low = 3` before `low` drops to 0 and the fresh root never sees 3 (found in review); a plain root gives the context's result, so the engine is right and the oracle was not. Monotone bodies bind `low`'s output to a variable nothing else reads; the history family keeps such joins, under the operational reference. | Corrected (§17) |
| §17 | `EggPushPopTests` once a `.egg` reader exists. | There is no reader (rule-layer D14); the programs are translated by hand (`EggPushPopProgram`), with expected values from the egglog 2.0.0 CLI. One program was written so that a push inherits progress (the egglog test files never run rules at the root before a push); the review added the web demo's `bdd`, `push-pop`, `array` and `cyk` (`EggPushPopWebDemoTests`). | Corrected (§17) |
| §17 | — | `ContextRuleCopyTests` runs without the reference (the fresh-root copy is the oracle there). Every write is flushed as it is made, so CreateChild's flush of a parent's staged writes is covered by `ContextOracleReviewTests` instead. Test classes named in §17 as `FreezeTests`, `ContainerContextTests` and `ContradictionTests` are `BridgeFreezeTests`, `BridgeContainerContextTests` and `BridgeContradictionTests`, because storage classes of those names exist in the shared test namespace. | Note |

**Engine defects fixed**

| § | Defect | Fix | Text |
|---|---|---|---|
| §8.1, §10.10 | `Database.Freeze` compacted tables (`ForceRehash`) after the last merge had re-armed the index catalogs, so an index refreshed since (by a search that merged nothing, or an extraction) kept old row ids, and a frozen catalog is never reset again: children's probes read renumbered rows. Found independently by the differential (`row mark(#3); union(#1, #3); freeze`) and by the extraction differential (seed 13). | The freeze re-arms the indexes of every table whose generation it moved, before the warm-up; every compaction bumps the generation and `Index.Refresh` rebuilds fully on a generation change, so a table the freeze did not compact keeps its warm indexes. Tests: `FreezeIndexWarmupTests`, `ContextExtractionTests.AnIndexReadBeforeTheFreezeIsRebuiltWhenTheFreezeCompacts`. | Corrected (§10.10) |
| §10.5, §6.5 | `TableAction.TryLookup` and `RowCount` used `state.GetTable`, which throws in a child, so bridge merge functions and primitives failed there. | They read the running context's view (`ExecutionState.TryLookupColumn`, `VisibleRowCount`). | Corrected (§15) |
| §10.3 | Raw root rows stamped below the watermark after empty runs (above). | Map §8.1 row 26. | Corrected (§10.3) |

**Tests changed**

| Test | Change | Why |
|---|---|---|
| `EGraphRulesTests.APanicIsThrownAfterTheRunWithTheWatermarkAdvanced` | Renamed `…WithoutCommittingTheWatermark`; `LastRunAt` stays 0 and later runs panic again | X18 (egglog advances the watermark) |
| `SemiNaiveDifferentialTests`, `RandomRule.Evaluate`, `BridgeDiffOracle.Rules` | The reference oracles run Id constants of rules in their canonical form | X19: a store-backed root keeps matching a constant that lost a union |
| `Eggnog.Tests` `ExtractionTests.AStaleExtractorThrowsAfterAChangingWrite` | An unrelated relation write keeps the extractor current; a term write makes it stale | E5 and rule-layer D49 drop the clock from staleness |
| `RootThreadingTests.LatticeMergesOnPoolThreadsAreRefusedToo` (`Eggnog.Tests`), `HiddenRootCostReviewTests.Allocated` | The first waits for a second thread to enter; the second keeps the least of three measured windows | Both failed on a loaded machine, where the merge ran on the calling thread or a pool trim showed as an allocation; the checks keep their strength |
| `ContextOracleReviewTests` (two C1 cases), `EGraphContextInvariantsTests.C1CatchesCorruptProgress` | Corrupt progress through `EGraphContext.SetProgress` instead of writing the private array by reflection | The progress is a map now |
| `ActionReadAnalysisTests` | Its nested fixture became `ActionReadAnalysisSetup` | Conventions: one type per file |
| `RootSizeProbeTests`, `RootConstantVariantCostTests` | Joined `ExecutorStressCollection` | They append to the report file the other probes of that collection write, and failed with a sharing violation when run beside them |
| `RuleContextSoundnessReviewTests` | Re-encoded as UTF-8; the two `§` that were already UTF-8 and the re-encoding had mangled were repaired in verification | It was saved partly as Latin-1, which mangled `§` |
| `ContextFixReverifyTests` (from the second verification) | Added as written; its four ordering cases failed until the raw escape flushed staged writes first | §10.4 |
| `RawEscapeReverifyTests` (from the third verification) | Added as written; three cases failed until the raw escape's action ran as a callback, the bridge's writes refused inside callbacks, and the raw iteration flushed staged writes first. Each case accepts either a refusal or the ordered result; `RawEscapeCallbackTests` asserts the refusals | §8.3, §10.4 |
| `BridgeRootCostReviewTests.RootAddValuesAllocatesNoMoreThanStagingTheRowsDirectly` | Measures the two paths in interleaved windows and keeps the least of up to ten windows each, stopping once the bound holds | It failed once in a full suite, where every window of one path was inflated by a pool trim; the bound is unchanged |

### 21.10 M10.4: Lift primitives and split hooks

M10.4 was built in one part on the M10.3 core, merged with the M10.3 fixes, reviewed for soundness, root cost, oracle strength and completeness, and fixed in four rounds, the last three answering verification probes. The rows below record every departure the build, the merge and the fixes reported. Rule-layer v2 §13 items 8 and 9 (E8) are applied in §11.2 and §11.3; rule-layer §15.7 records the same from that side. The L6 and L7 reports are `docs/design/colors-m10-reports.md`.

**Member maps and the lift (§11.1, §11.2)**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §11.1 | `ReadOnlySpan<uint> Members(uint caseCanonical)` | `Members(in uint caseCanonical)`: the one-element answer `[c]` is a span over the argument, so nothing is allocated per lookup. Also `GroupMembers`, `IndexOf`, `IsLocal`, `MemberCount` and `Version`. A class whose members are all local is no group: it stands for no parent id | Corrected (§11.1) |
| §11.1 | `ClassPartition.Meet(...)` returns nothing | It returns the number of blocks appended, and sorts with the struct comparer `MemberSignatureComparer` | Corrected (§11.1, §16) |
| §11.2 | A constructor present in every live case and absent in P is lifted as a term | When every live case's output stands for one parent id, the row is written with that id (counted in `Rows`), otherwise as a term. The result is the same as the next round's meet gives, one round and one fresh id earlier; it is sound because the row holds in every live case | Corrected (§11.2) |
| §11.2 | Function with an id output: "an Id output must have a common parent member" | As written, for a key P does not hold. For a key P holds, nothing is written when every live case's output is its class of P's stored output, which a `UnionId` or `AssertEq` merge always gives: the first form wrote the common member next to P's stored output when the two differed in P, so an `AssertEq` merge, the rule layer's merge for a function to a sort without a lattice, panicked before the meet's union of the two ids applied (found in review). The first correction skipped every held key, which lost a value a lattice merge over ids had moved in every case (I33, found in verification); such a key is written again | Corrected (§11.2) |
| §11.2 | Relation rows are written when present in every live case | The output is written as every live case holds it, and only when they agree; an id output is refused (`ArgumentException`), and so is a case join on any kind but `Function`, or on an id output, and a constructor without an id output. The first form wrote the seed's output unchecked: a base output that differed between cases was lifted from one case (I33), and an id output could be the seed's local id (I10) (found in review). The rule layer lifts its relations as `Function` with a unit output (rule-layer §6.9.1), unaffected | Corrected (§11.2) |
| §11.2 | — | In a child parent, a table the parent cannot write (a merge that is not a lattice merge, decision 2) is left out of the lift: no case can hold rows of it either | Corrected (§11.2) |
| §11.2 | `if first round && sig == memo: return`; `memo = LiftSignature.Of(P, cases)` at the end; `LiftSignature` holds versions | The memo is keyed on the spec as well (compared by reference, then by content), and set only when the lift settled: not cut short by `RoundLimit`, and its last round skipped no row over `CandidateLimit`. A lift that runs clears it first, so one that throws leaves none. The first form ignored the spec and the limits, so a lift of a wider spec, or with a larger `CandidateLimit` after a lift that skipped rows, returned at once and lifted nothing (I33 completeness through E8's per-call spec and options; found in review). A memo hit reports `ParentContradicted` when no live case is left; it reported false (found in review) | Corrected (§11.2) |
| §11.2 | All-vacuous: `P.StageRow(c, [])` | The output is the function's constant default; else, for an id output, a term (a fresh id of P); else the base value a vacuous case merged into its own layer. The first form copied a vacuous case's cell for an id output too, which named an id local to that case in P while the cases lived (I10, found in review). A `CaseSet` keeps the base value when it disposes a vacuous case, so a lift after `DisposeVacuous` or `RunEach` disposed every case writes the contradiction too; the first form threw `InvalidOperationException` there for a base output with no constant default (found in verification). `ParentContradicted` is reported also when no contradiction function is designated (nothing is written then) | Corrected (§11.2) |
| §11.2, §14.2 | `LiftCandidates`: a deduplicating key arena | A reset clears only the slots the arena used when they are few against its capacity. The first form cleared the whole slot table, which only grows, so after one large table every later table and round paid for the largest one ever seen (found in review) | Corrected (§11.2, §14.2) |
| §11.2, §8.3 | — | The lift holds P's thread domain for its whole run (the cases share it), refuses to start inside a callback of it, and calls no caller code but the case joins | Corrected (§11.2) |
| §11.2, E8 | `RunEach(ICaseSchedule)` | `RunEach(schedule, depth, allowSplits)`, returning whether a schedule reported a change. `CaseSet` also has `VacuousCount` and `IsDisposed`; `AddCase` requires options with `IsCase` and `StopOnContradiction`; the constructor refuses a frozen parent. A lift over a set whose cases were all disposed as vacuous contradicts the parent; with no case ever found vacuous it throws, as E8's host-driven `Lift` does | Corrected (§11.2) |
| §11.2 | — | A case disposed outside the set makes `RunEach`, `Lift` and `DisposeVacuous` throw `InvalidOperationException`. The first form dropped it silently, so the lift ran over cases that no longer covered the parent and lifted what held in the rest, or contradicted the parent when the rest were vacuous (found in review). `Cases` still lists such a case, disposed; only `Dispose` is of use on the set then | Corrected (§11.2) |

**Split hooks (§11.3, E8)**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §11.3 | Step 6: "for every record, `RunEach`, then lift" | For every live record of the scope, in creation order, those earlier steps opened included. The first form ran and lifted only the records the same step opened, and returned before phase A when every request was covered (B6), so facts the cases derived from the scope's later facts were never lifted, and a case those facts made vacuous was never dropped, against rule-layer §6.9.3 and D43 (found in review). `Step` returns whether it opened cases or a lift changed the scope; in-case work alone is no change, so a schedule that repeats the step until nothing changes ends | Corrected (§11.3) |
| §11.3 | Step 6: "every live record of the scope"; step 2: "a key covered by a live record" | Step 6 runs and lifts only the live records the same driver opened; each `SplitRecord` names its `Driver`. B6 coverage compares the record's source object by reference and the key cells, not the `SplitKey` (whose source is an index into one driver's list). The first form ran one driver's in-case schedule in another driver's cases and lifted them with its own spec (and nested its own split nodes there), and let a record of one driver's source 0 cover an unrelated split of another driver's source 0, so that split was never taken; the rule layer builds one driver per split node over one scope (rule-layer §6.9.3), so both happened in programs with two `Split` nodes (found in verification). A split node that reuses the same source object is still covered, as B6 intends | Corrected (§11.3) |
| §11.3 | Step's phases | `Step` brings the scope up to date first and returns false for a stopped or contradictory scope or at the depth limit; it refuses a frozen scope (a lift could not write into it). A request with fewer than one case is skipped like one over `CaseLimit`, and so is a request whose key an earlier request of the same step took. After every lift the cases found vacuous are disposed; a lift that contradicts the scope ends the step | Corrected (§11.3) |
| §11.3 | Records | Kept on the scope's `EGraphContext`, created on first use. A record is registered before its cases are opened, so an exception while opening or running leaves it for `DisposeRecords` | Corrected (§11.3) |

**Checks, children and benchmarks**

| § | The text said | Built, and why | Text |
|---|---|---|---|
| §12.7 | C4: member maps | `CheckMemberMaps` in `EGraphContext.CheckInvariants`: a map built from the overlay holds exactly its members that are not local, each canonical in the parent and mapped to its group root, roots and members ascending; a cached map whose version is current equals it. It does not need a clean context, so it also runs where C2 reports a frozen context's host writes. A mutation that drops this check survives the tests: the map is built from the overlay the check compares it with, so only fault injection could fail it | Corrected (§15) |
| §19 item 9 | Turn a nonzero `RestampedCopies` into a `Debug.Assert` once I21 holds over M10 workloads | Done: the lift and split workloads assert `RestampedCopies == 0` over hundreds of seeds (`LiftReviewTests`, `LiftCompletenessReviewTests`). DEBUG builds now assert at every re-stamped copy unless the store was written through the raw execution-state API (`Database.WithExecutionState`, `CreateExecutionState`, the bridge's raw escapes), whose rows carry the stamps the caller gives them, as `CatchUpTests` relies on. The engine's own writes use `CreateEngineState`, which marks nothing; so do the rule layer's root host batches (`RootEngineScope`, stamped with the store clock), so stores the library builds keep the assertion (found in verification). Release builds are unchanged | Corrected (§3.5, §6.6, §7.2, §7.3, §10.4, §12.3, §14.1, §15, §19) |
| §14.2, §21.5 | Reduce a bare child and an empty layer: lazy `PendingState` segments and lazy `IndexCatalog`s | Lazy pending queues as specified, and lazy notification flags added (568 B per child catalog). The index catalogs stay eager, since a lazy catalog adds a null test to the root's index lookups; only their insert lock is lazy. Steady state: a bare child 4,726 → 2,454 B, an empty layer 2,766 → 1,062 B. The root pays what it did (§14.1); timing that is the M10.4 gate (`WritesBenchmarks`, `RuleSetRunBenchmarks`), still to be run on an idle machine (§21.11) | Corrected (§14.1, §14.2, §15, §17) |
| §17 | `CaseSplitMemoryBenchmarks`: bytes per structure | The benchmark measures a split's bytes against clones; the footprint mode (`--context-footprint`) adds the bytes of each part of a child (pending state, catalogs, overlay, journal, id blocks) and of one case step by step (the case, a union and its rebuild, a fact, a rule run, a lift) | Corrected (§14.2) |
| §17 | `PerChildCostBenchmarks` (full workload: 100 merges plus one rule run) | Built: 100 unions, a flush and one rule run whose watermark the child inherits from the root's run; the setup checks the 200 matches. Bytes are flat over 20k–2M root rows | Note |
| Deferred from M10.3 | Exercise a child's internal parallel rule set under `ConcurrentChildren`, above `DbLevelThreshold` | Covered by tests rather than the benchmark: `ConcurrentChildParallelRuleSetTests` and `ConcurrentParallelChildrenReviewTests` run several children with more than 10,000 rows of their own under `ConcurrentChildren`, whose heuristics then choose parallel rule sets; each child equals a serial replay and passes `CheckInvariants` | Note |
| §17 M10.4 | Tests and benchmarks as listed | Built as listed. The reviews and fixes added the late-id property, revisit, sharing, oracle and verification classes, and the footprint mode (`--context-footprint`); the gate map had no M10.4 row. §17 now lists them | Corrected (§17) |
| §16 | Files in `Cases/` | Also `LiftedTable` and `MemberSignatureComparer`. Test helpers `RecordingSchedule`, `TestSplitSource`, `TestSplitDemand`, `SplitWorld`, `RowCountSink`, `LiftFacts`, `SeedFailure`; benchmark helpers `CaseGraph`, `LateParentMintResult`, `ContextFootprint` | Corrected (§16) |

**Tests changed**

| Test | Change | Why |
|---|---|---|
| `LiftCompletenessReviewTests` | The completeness review's `LiftReviewTests`, renamed | The root-cost review added a different class of that name; both are kept |

No other existing test changed: every M10.4 test file is new, and the review tests pass as written.

### 21.11 Still deferred

The spec-text updates that the M9.1–M10.4 stages left for this document, and rule-layer v2 §13 items 1–9, are applied in this revision. Everything below is still open.

| Item | Recorded in | Target |
|---|---|---|
| Lazy `IndexCatalog` objects (M10.4 made only their insert lock lazy: a lazy catalog adds a null test to the root's index lookups) | M9.4, M10.4 | No milestone: only with an idle-machine gate showing the root unharmed |
| The ≤2% root gates against the M9.0 baseline, which need an idle machine (only Release builds and dry runs were done): M9.1 (`ActionLookupBenchmarks`, `DisplacedRebuildBenchmarks`, `WritesBenchmarks`, `RuleSetRunBenchmarks`); M9.2 (`DisplacedRebuildBenchmarks`, `UnionFind/*`); M9.3 (`WritesBenchmarks`, `ActionLookupBenchmarks`, `RuleSetRunBenchmarks`); M9.4 (`DisplacedRebuildBenchmarks`, `RuleSetRunBenchmarks`); M10.1 (`ConstrainedScanBenchmarks`, `IndexRefreshBenchmarks`); M10.2 (`RuleSetRunBenchmarks`, `ConstrainedScanBenchmarks`); M10.3 (`RuleSetRunBenchmarks` and the M8 bridge workloads `BridgeMath` and `BridgeAc`, which now run on a store-backed root and pay §14.1's M10.3 rows; the cost of building an `EGraph`; `ContextExtractionBenchmarks` `RootContext` against `RootApi`); M10.4 (`WritesBenchmarks` and `RuleSetRunBenchmarks`, after the `PendingState` flags change). Allocation-only dry runs: `BridgeMath` 9.80 GB → 9.61 GB, `BridgeAc` 15.47 GB → 15.57 GB (+0.6%), `RootContext`/`RootApi` 612.5 KB / 608.6 KB | M9.1–M10.4 | No milestone: an idle-machine run |
| M9.4's done-when: per-child time and bytes flat within 1.5× from 20k to 2M rows (L6, B4). Bytes are flat: about 96 KB for the storage workload and 145 KB for M10.4's full workload (100 unions, a flush and one rule run) across 20k, 200k and 2M rows, for frozen and writable roots (dry runs; 148–149 KB in steady state). Time is not measured | M9.4 | No milestone: an idle-machine run |
| Whether the `Prober`'s growth (+24 B per prober) stays within the `RuleSetRunBenchmarks` Parallel gate. If it does not, shrink `ProberPiece` by moving `View` and `Rest` out of line | M10.2 | Idle-machine run; an M10.2 follow-up if the gate fails |
| Push/pop programs that intern eq-sort containers in a child (`container-rebuild.egg`, `container-fail.egg`, `vec.egg`) | M10.3 | After M10, with eq containers in children (decision 9, §19 item 8) |
| Push/pop programs that need sorts the bridge lacks (`UnstableFn`, `Map`: `python_array_optimize.egg`, `stresstest_large_expr.egg`, the issue #113 blocks of `tricky-type-checking.egg`), and the web demo programs out of reach for the same reasons. The web demo's `math.egg` needs only `f64` and strings, which the bridge has, but is not translated (74 rules, 21 push blocks) | M10.3 | No milestone: there is no `.egg` reader (rule-layer D14); `math.egg` when a reader exists, or by hand if R2 needs more push/pop coverage |
| `ComputeCostsFromRootSorts` and `FunctionToDag` over an `EGraphContext` (only the on-demand extractor takes a context, as E5 asks) | M10.3 | No milestone; R2 if it needs them |
| B5 in full: bytes for each per-child structure separately (layer tables, hidden sets, overlay, cursors, watermarks, indexes; the footprint mode gives the parts of a bare child and the bytes each step of a case adds), and time and failures reported separately under a stated timeout policy | M10.4 | No milestone: the timed half needs an idle machine |
| A test that fails when C4's canonical-in-parent check is removed (a surviving mutant of the oracle review): the member map is built from the overlay the check reads, so only fault injection can fail it | M10.4 | No milestone: needs a fault-injection hook |
| Library use of the split hooks: `RuleSplitSource`, `SplitSchedule` with one cached `SplitDriver` per split node, and the host-driven `CaseSet` wrapper (rule-layer §6.9). The engine side is done, several drivers on one scope included (§11.3) | M10.4 | Rule-layer R3 |
| Left open by design (§19 items 7 and 8): G2 ranking (the L7 report now exists, `docs/design/colors-m10-reports.md`: its cost is linear in cases × terms per round and independent of the root), B1 pruning, B3 flattening of hidden sets, G5, G8, G9, eq containers in children, per-context replanning (§8.3 #13), and deduplicating driver keys across pieces | §19 | Later; no milestone |
