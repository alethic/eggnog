# Deferred colored-context features

Workstream key: `colored-context-deferred-features`. Items: the "Left open by design" colored-context entries of
[`../roadmap.md`](../roadmap.md) ("Engine work") that are not about interning or containers, and the compaction upgrade
path that [`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md) §19 item 2 leaves open.

## 1. Summary and recommendation

**Recommendation: Do after an experiment** (confidence: medium), for one narrow piece of item A: a compaction of the
rows above the highest live descendant cursor ("step 0"). It goes ahead only if the owner first says that the library
should support holding children of a large writable scope (question 1), and an idle-machine timing then shows that the
gate costs time. Mark B "Later". Drop or shelve the rest.

| Item | Verdict | Size if built |
|---|---|---|
| A0. Compaction of the rows above the highest live descendant cursor ("step 0") | Do after an experiment: owner's question 1 first, then a two-sided timing | small to medium |
| A-d. Diagnostics and documentation of compaction blocked by live children | Do with A0, or alone if question 1 is answered "no" | small |
| A1. Remap of descendants' cursors when no descendant hides rows | Drop: step 0 supersedes it | small |
| A2. Remap of descendants' hidden sets (dead rows below the cursors) | Later: only if a split run shows that step 0 leaves much behind | medium |
| B. Per-context replanning of rules | Later: premise corrected below; a fair count decides it | small (provisional plans) to medium (plans per scope) |
| C. Parent-visible ids winning over smaller ids (G2 ranking) | Drop; close the decision as "smaller id wins" | medium |
| D. Pruning child rows an ancestor made redundant (B1) | Drop | medium |
| E. Flattening of hidden sets (B3) | Drop, with a revisit trigger | medium |
| F. A memory budget per run that disposes settled split records (G5) | Later: not planned until a user needs it | medium |
| G. Running a split's cases in parallel, with a thaw (G8) | Later: not planned until a user needs it | large |
| H. Shared ids for a term sibling cases derive (G9) | Drop | large |
| I. Deduplicating driver keys across layer pieces | Drop unless a count shows duplicates matter | small to medium |

Reason. Only item A has a measurement, and it is smaller than it first looked. In a dry run of egglog-bridge's `math`
program at a writable root (described exactly in §3), one child held unused from the start leaves the root's 13 function
tables with 22.6 million tombstones beside 16.0 million live rows, against 4.0 million without the child. Physical rows
grow 1.93 times, and the tables' row buffers grow by 293 MB. All of those dead rows lie above the child's cursor. With a
child that catches up after every iteration, 95% of them still lie above it. A compaction of that suffix renumbers no row
a descendant holds, so it needs no remap of hidden sets. The memory value is not established, though. In the same runs
the managed heap after a forced collection was 2.80 GB with the held child and 3.20 GB without it, which this probe
cannot attribute. The time value has not been measured either, and it can be negative, because every compaction forces
a full rebuild of the table's indexes. So the owner's question decides first, and an idle-machine timing that is
two-sided decides second.

Item B rested on a wrong premise. When sibling scopes under a frozen root are each saturated by a run and then frozen,
global rules are planned during the first such scope's run, on statistics taken before anything is derived, and not at
its freeze. Per-scope plans for every rule would cost about 2.8 KB retained per plan, measured on the test fixture
`PredicateTheory` (`src/Eggnog.Tests/Predicates`). That extrapolates to about 2.8 MB per frozen scope at 1,000 rules,
memory that grows with the number of frozen scopes times the number of rules, which rules out a plan per scope for every
rule as the first remedy. Nothing has measured what a mis-planned rule costs. B therefore waits for a count on a fixed
corpus of sibling schema scopes. For items C to I, the record already gives the answer, or no workload needs them.

## 2. What it is and why it was planned

All nine items come from the design of colored contexts. The engine map's cross-cutting decisions are in
[`../history/egglog-engine-map.md`](../history/egglog-engine-map.md) §4.2. The specification and its deferred list are in
[`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md) §19 and §21.11. The review of the
literature is in [`../history/colored-egraph-literature.md`](../history/colored-egraph-literature.md) §3.1 and §4. Each
item was left open on purpose: either the cost it answers had not been measured, or no user needed it.

- **A. Compaction under live descendants.** Engine map §4.2 C12 keeps egglog's compaction trigger (more than
  `max(16, n/2)` tombstones). It adds a gate: a layer whose "descendants have cursors behind it, or hidden sets keyed by
  its RowIds" does not compact, because compaction renumbers rows. It names "the `remap(old, new)` callback that
  `remove_stale` already reports in order" as the later alternative. The spec made C12 stricter (X27, §6.8 and §19
  item 2): a layer never compacts while descendants live, and `remap(old, new)` "remains the upgrade path". Invariant
  I16 (spec §12.3, "Generation stability") depends on the gate. Every live descendant's cursor and hidden sets must be
  of the layer's current generation.
- **B. Per-context replanning.** Engine map §8.2 ("Frozen plan statistics": "A child whose statistics differ sharply
  from the root's keeps the root's plan") and §8.3 question 13. Spec §9.7: "A plan is made once per rule, on the
  statistics of the context it first runs in, or of the frozen context that pre-plans it… Replanning stays open."
  Rule-layer spec §11 risk 4 accepts the risk ([`../history/rule-layer-spec.md`](../history/rule-layer-spec.md)). It
  matters most to a long-lived host that keeps several sibling scopes of different shapes frozen under one root and
  answers short-lived children of each; such a host's fallback is one graph per scope. An option on non-root freezes
  that plans only local rules, or nothing, was proposed as a mitigation
  ([`./rule-layer-split-features.md`](./rule-layer-split-features.md), item 4), and is now merged into this item.
- **C. G2 ranking.** The literature review's G2 ("Union-by-min under a parent that is not frozen"). When a writable
  parent mints a term's id after its cases minted theirs (the term lift), the parent's id is larger and loses in every
  case. Each case then hides and copies the parent rows that mention it. The proposed alternative ranks winners
  "parent-visible first, then min". [decisions](../engine/decisions.md) ("Requirements from the literature review",
  last row) required a measurement first. The L7 report made it, and the decision stays open (spec §19 item 7).
- **D. B1 pruning.** The literature review's B1, "Prune child rows that an ancestor row has made redundant": when an
  ancestor row appended after the child's cursor carries a value equal to the child's merged value, drop the child's
  own row and leave the ancestor row visible. B1 prunes a child's own rows, not hidden sets. The lift is the main source
  of such rows. Review correction 12 found that B1 as written conflicts with C4, because a tombstone in a child means
  "absent". B1 therefore needs a pruning mechanism other than a tombstone. It also creates spurious semi-naive deltas,
  because the ancestor row is newer.
- **E. B3 flattening.** The literature review's B3: when a child hides more than about half of an ancestor layer of a
  table, copy the rows that are still visible into the child and hide the whole layer. Review correction 13 recasts it
  as a probe-efficiency measure (avoiding ancestor probes that return mostly hidden rows), not a fix for copy volume.
- **F. G5 memory budget.** The literature review's G5 (case counts grow exponentially with depth) proposes a per-run
  memory budget, under which settled split records (lift signature unchanged) are disposed first. Rule-layer D7 instead
  keeps records "until the top-level `Run` ends".
- **G. G8 parallel cases.** The literature review's G8: seal the parent, run the cases in parallel, then thaw the
  parent after a barrier and lift. Rule-layer D17 keeps cases sequential: "A writable parent shares its thread domain
  with every case under it."
- **H. G9 shared sibling ids.** The literature review's G9: a term derived in k sibling cases is minted k times. The
  speculative remedy is a store-wide table that gives one id per (constructor, key) when every key id is
  parent-visible.
- **I. Driver keys across pieces.** Spec §19 item 8: "deduplicating driver keys across pieces (an optimization for I27's
  multiplicity; measure first)". In a child, the layered executor drives an atom "one frame per (piece, key)" (spec
  §9.5, "Kernels"), so a key that has rows in two layers is driven twice. The guarantee that governs is I27 (spec
  §12.5): the layered executor must emit "the same set of distinct bindings" as a naive join. Decision 6 ("Match-count
  parity: Not required") is what the spec's §18.2 maps to that multiplicity. Two roadmap phrases misdescribe these
  items. "Split-driver keys" names the wrong thing, because the item concerns join drivers in the executor, not split
  drivers. "Pruning and flattening of hidden sets" is wrong for B1, which prunes a child's rows. The corrections belong
  to [`./conformance-docs-release.md`](./conformance-docs-release.md).

## 3. Current state

**A. Compaction.** `SortedWritesTable.MaybeRehash` and `ForceRehash`
(`src/Eggnog/Relations/Tables/SortedWritesTable.Rehash.cs`) return early and count `CompactionsBlocked` when
`compactionGate.HasLiveDescendants`. The gate is the owner's `DescendantGate`
(`src/Eggnog/Relations/Layers/DescendantGate.cs`) for a child's layer (`SortedWritesTable.CreateLayer` in
`src/Eggnog/Relations/Tables/SortedWritesTable.Layered.cs`), and the store root's gate for a shared root table
(`LayeredSchema`, `src/Eggnog/Relations/Layers/LayeredSchema.cs`). Context-private tables are never gated.

- *The trigger.* `MaybeRehash` compacts when `staleRows > max(16, data.Count / 2)`, where `data.Count` counts physical
  rows. Without any child, then, tombstones can legitimately reach the number of live rows: a table can sit at up to
  2.0 physical rows per live row and still be in the steady state that egglog's trigger allows.
- *Tests.* `CompactionGateTests` (`src/Eggnog.Tests/Relations/Layers/CompactionGateTests.cs`) checks three things. The
  root and a child's layer do not compact while descendants live, private tables always do, and a freeze compacts only
  where the gate allows. `FreezeTests.CompactionAtFreezeRespectsTheGate`
  (`src/Eggnog.Tests/Relations/Contexts/FreezeTests.cs`) checks that a root frozen with a live child keeps its 30
  tombstones. In that test the child is created after the tombstones exist, so they lie below its cursor.
- *How long the gate stays closed.* The gate opens when the last descendant is disposed, and the next merge then
  compacts. A frozen table never changes again, so a scope frozen with live children keeps its tombstones for good.
  `Scope.Freeze` (`src/Eggnog/Graph/Scope.Tree.cs`) neither refuses nor reports live children, and no public
  diagnostic surfaces `CompactionsBlocked`. The rule layer disposes a run's cases at the end of the top-level run
  (`SplitDriver.DisposeRecords`, called in the `finally` block that ends a top-level run in `src/Eggnog/Graph/Scope.Engine.cs`). So a
  split run blocks compaction of its scope for the run's whole length. A child that a user holds under a writable scope
  (`Scope.CreateChild` allows it) blocks it for as long as the child lives.
- *Order.* Compaction does not always keep row order. `ParallelRehash` groups the rows of each sort run by shard, and
  only the serial `Rehash` keeps row order.
- *Where descendants hold row ids.* A child reads an ancestor only after catching up with it. `LayerView`'s remarks say
  that "an ancestor that appends makes the context not clean until it catches up, which drops its views". The cursor
  (`AncestorCursor`, `src/Eggnog/Relations/Layers/AncestorCursor.cs`) records the ancestor's layer version at the last
  catch-up. Rows appended since then are `[Minor, layer.Version.Minor)` "while the generation is unchanged". Hides are
  made by catch-up branch (b) (`ContextTables.CatchUp.cs`) and by union copies after a catch-up, so they name only rows
  below the cursor. A generation change under a live descendant is detected loudly. `ContextTables.CheckGenerations`
  throws, and the invariant checker (`ContextTables.Invariants.cs`) reports cursors and hidden sets of a stale
  generation.

*What dead rows cost in time.* Indexes refresh incrementally. `Index.Refresh`
(`src/Eggnog/Relations/Indexes/Index.cs`) is "O(1) when nothing changed; O(new rows) incrementally; O(rows) after a
generation change". Semi-naive delta atoms read only rows past a timestamp. Dead rows in old ranges are therefore not
walked by every scan or index build. They do cost in three places. Full scans of a table (`All()` covers every physical
row) walk them. Index probes return them, because an index keeps a tombstoned row's entry "until the next generation, so
readers filter tombstones" (`Index` remarks). And they occupy memory. Compaction has its own time cost. It is
O(physical cells), and it starts a new generation (`generation++` in both `Rehash` and `ParallelRehash`), which makes
the next refresh of every index on the table a full O(rows) rebuild.

*Measurement made for this plan (counts and bytes from short dry runs, no timing).* The probe ran egglog-bridge's `math`
program (`BridgeMathProgram`, `src/Eggnog.Benchmarks/Relations/Bridge/`) at its store-backed root, one
`EGraph.RunRules` call per iteration, for 12 iterations (0 to 11). It ran in three variants:

- with no child;
- with one child from `EGraph.Root.CreateChild()`, created before the first iteration and held, unused, to the end;
- with that child caught up (`EGraphContext.EnsureClean`) after every iteration.

The third variant moves the cursor the way a split step moves a case's cursor, but it makes no hides. After each
iteration, the probe summed four counts over the 13 function tables:

- `Count`, `StaleCount` and `CompactionsBlocked`;
- the tombstones at or above the child's cursor, found by scanning `RowBuffer.IsStale` from the table's physical row
  count when the child was created or last caught up;
- the tombstones above the trigger's bound, `max(0, stale − max(16, physical/2))` per table.

At the end it recorded the row buffers' capacity and used bytes, and `GC.GetTotalMemory` after two forced, blocking,
compacting collections. The build was Release on net10.0 with server GC (the benchmarks project's setting), and each
variant ran once. Counts vary by under 1% between runs, because the run is parallel.

| Iteration | Live rows | Tombstones, no child | Tombstones, held child | Tombstones, caught-up child | Of which above the caught-up child's cursor | Blocked (held) | Above the trigger's bound (held) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 8 | 28,303 | 8,489 | 8,489 | 8,489 | 5,358 | 0 | 0 |
| 9 | 136,446 | 81,605 | 81,863 | 81,726 | 69,735 | 0 | 0 |
| 10 | 1,047,896 | 184,122 | 926,779 | 924,927 | 820,597 | 3 | 52,286 |
| 11 (last) | 15,987,528 | 4,026,559 | 22,588,424 | 22,547,515 | 21,382,633 (95%) | 7 | 3,715,077 |

| At the end | No child | Held child | Caught-up child |
|---|---:|---:|---:|
| Physical rows ÷ live rows | 1.25 | 2.41 | 2.41 |
| Row-buffer capacity (MB) | 531.8 | 824.9 | 824.8 |
| Row-buffer bytes in use (MB) | 320.2 | 617.2 | 616.6 |
| Managed heap after forced collections (GB) | 3.20 | 2.80 | 3.07 |

The probe gives six read-outs:

- With the held child, every tombstone lies above the child's cursor.
- A child that catches up after every iteration blocks just as much. The 7 blocked compactions match the held child's.
  Even so, 95% of its tombstones lie above its cursor, because most rows that die in an iteration were appended in that
  iteration.
- The held child adds 18.6 million tombstones against the run without it at the same iteration. Physical rows go from
  20.0 million to 38.6 million (1.93 times). Without the gate, only 3.7 million of those tombstones would have been above
  the trigger's bound at that moment: the gate keeps rows that egglog's trigger would have removed at earlier merges.
- Row storage grows by 293 MB of capacity.
- The whole managed heap after forced collections did not grow. It was 0.4 GB smaller with the held child. One run per
  variant cannot attribute this. A likely cause is buffers that compaction and index rebuilds return to
  `ArrayPool<T>.Shared` (`ParallelRehash` rents its scratch buffer there, and `TupleIndex` rents from it). The pool keeps
  those buffers, and `GC.GetTotalMemory` counts them. Until that is attributed, the memory value of compaction under a
  live child is not established. Only the row-buffer growth is.
- The probe is not in the repository. §7 proposes adding it as a `--stale-probe` mode of `src/Eggnog.Benchmarks`, beside
  the context footprint mode, so the figures can be taken again.

**B. Replanning.** `RuleInfo.GetOrBuildCachedPlan` (`src/Eggnog/Relations/Bridge/RuleInfo.cs`) plans a rule once, under
the rule's plan gate, on the statistics of the database that first asks. It shares the plan with every context.
`CachedPlanInfo` (`src/Eggnog/Relations/Bridge/CachedPlanInfo.cs`) says "Never invalidated". `EGraphContext.Freeze`
(`src/Eggnog/Relations/Bridge/EGraphContext.Freeze.cs`) plans only the given rules that still have no plan, and warms
the indexes of every given rule's plan. The rule layer's `Scope.Freeze` passes nothing to plan at the root. For any
other scope, it passes every global, local, query, mirror and split rule that its descendants can run
(`CollectPlanRules`, `src/Eggnog/Graph/Scope.Tree.cs`).

A rule's plan is therefore fixed by the first run of that rule anywhere, or by the first non-root freeze if no run came
first. A run comes first whenever a host saturates a writable non-root scope before freezing it, which is how the test
fixture's schema scopes are built: under a frozen root, the scope assumes its declarations, runs
`PredicateTheory.Facts`, which is `Saturate(Saturate(Horn, Lattice), Split(SchemaSplit, …))`, and is then frozen. So
every Horn and Lattice rule, and the schema split's matcher, is planned at the first iteration of the first such scope's
run. At that point the scope holds its assumed declarations, and every derived table is still empty. Only rules that the
run does not use (the `Rewrite` rules, the query split's matcher, queries) are planned at that scope's freeze, on its
saturated statistics. Two things follow:

- choosing which sibling scope is built first chooses whose declarations the plans see, not whose derived facts they
  see;
- a freeze option cannot touch the plans of the rules that a run before the freeze used.

The planner reads statistics through `IPlanStatistics` (`GetApproximateSize`, `GetColumnUniqueness`;
`src/Eggnog/Relations/Planning/QueryPlanner.FreeJoin.cs`, `QueryPlanner.Decomposition.cs`). The executor can still
reorder stages by the sizes it meets ([`../architecture.md`](../architecture.md) §3.5). Whether a rule may inherit its
watermark (`AllowInheritance`) is computed from the plan's actions, which do not depend on statistics.
`ExecutorCounters` (`src/Eggnog/Relations/Execution/ExecutorCounters.cs`, enabled by `EGGNOG_COUNTERS`) counts the rows
that scans enumerate and that indexes absorb, own and ancestor. It keeps one instance per `Database` and counts per stage
call or index build. It has no breakdown per rule, so a query child's database gives one total per derivation.

*Measurement made for this plan (allocations, a dry run, no timing).* Under one frozen `PredicateTheory` root, two
sibling schema scopes received the same declarations (the fixture's schema with a three-way discriminator) without any
run. The first was frozen, which
planned the theory's 51 global rules and split matchers plus its own branch rules. Then the second was frozen, which
planned only its own branch rules. The probe recorded allocated bytes on the thread and the managed heap after forced
collections:

| | First freeze (plans everything) | Second freeze (plans only its local rules) | Difference |
|---|---:|---:|---:|
| Allocated | 708 KB | 74 KB | 634 KB (about 12 KB per plan) |
| Retained | 170 KB | 25 KB | 145 KB (about 2.8 KB per plan) |

On the full path (declare, run, freeze), the fixture's schema compile allocated 1.67 MB for the first scope and 1.04 MB
for an identical second one, a difference that matches the 634 KB above. Linear extrapolation is the only basis for
larger theories. On that basis, a plan per frozen scope for every rule at 1,000 rules would retain about 2.8 MB per
frozen scope, and each freeze would allocate about 12 MB. Nothing has measured how much a plan made on one scope's
statistics costs on another.

**C. G2.** `UnionFindOverlay.Union` (`src/Eggnog/Relations/UnionFind/UnionFindOverlay.cs`) sets
`winner = Math.Min(ca, cb)`. `Rekey` assumes that the parent's canonical is the smaller id. `UnionIdMerge`
(`src/Eggnog/Relations/Tables/UnionIdMerge.cs`) keeps the smaller id. The invariant checker
(`src/Eggnog/Relations/Contexts/Context.Invariants.cs`) checks that each class's canonical is its minimum.
`UnionFindStatistics.ParentIdLosers` counts the parent ids that lose in a child. `LateParentMintBenchmarks`
(`src/Eggnog.Benchmarks/Relations/Contexts/`) produced the L7 report
([`../history/colors-m10-reports.md`](../history/colors-m10-reports.md), "L7"). Parent-id losers number
`cases × (lifted terms − 1)`. The copies are one row per lifted term per case per round (2,048 rows per round for 8
cases of 256 terms). Neither depends on the root's size. The report's read-out says that ranking parent-visible ids first
"would remove the parent-id losers and these copies, at the price of rewriting the cases' own rows that name the local
id (the same order of rows, but inside the case's own layer rather than copied from the parent)". The test fixture
`PredicateTheory` lifts value-keyed facts only (rule-layer spec §11 risk 5), so it never lifts terms and never meets
G2.

**D. B1.** Not implemented. Catch-up branch (b) (`ContextTables.CatchUpRows`, spec §7.2;
`src/Eggnog/Relations/Layers/ContextTables.CatchUp.cs`) merges an ancestor row whose key the child holds into the
child's row and hides the ancestor row. The colored-context reports give a case's cost: 13.4 to 13.7 KB for one union
and one fact, 17 to 20 KB lifted, and 53 KB for a union, a fact, a rule run and a lift
([`../history/colors-m10-reports.md`](../history/colors-m10-reports.md), "Case splits against clones" and "One case,
structure by structure").

**E. B3.** Not implemented. `HiddenSet` (`src/Eggnog/Relations/Layers/HiddenSet.cs`) becomes a bitset once it holds at
least `MinBitsetCount` (64) rows and more than 1/`BitsetDivisor` (1/32) of its layer. Smaller sets are sorted arrays. The
copy-volume histogram ([`../history/colors-m10-reports.md`](../history/colors-m10-reports.md), "Copy volume per union")
measures a mean of 2.9 rows copied, and so hidden, per union, with a maximum of 32, at 20,000 and 200,000 root rows. So
the measured per-union sets are small sorted arrays. `LayerStatistics.RowsHidden` counts hides.

**F. G5.** Not implemented. `RunOptions` (`src/Eggnog/Scheduling/RunOptions.cs`) bounds a run with three options:

- `CaseLimit` (64 per split);
- `SplitDepthLimit` (4 nested splits);
- `RowLimit` ("the most rows the run may leave held by the top-level scope and by the run's live cases themselves").

Exceeding `RowLimit` stops the run soundly. Split records serve two purposes while they live (`SplitDriver` remarks,
`src/Eggnog/Relations/Cases/SplitDriver.cs`). Later steps skip their keys (B6), and later steps run and lift their cases
again. The lift memo makes an unchanged record cost one signature check.

**G. G8.** Not implemented. A child of a writable context shares its thread domain
([`../architecture.md`](../architecture.md) §3.8, "Freeze and thread domains"). The reason is that a writable context's
reads are not read-only (path compression, staged writes, lazily built indexes). Freeze, which makes them safe, is
one-way: it compacts, seals staging, flattens the union-find and builds views. `SplitDriver.Step` runs `CaseSet.RunEach`
on the parent's thread.

**H. G9.** Not implemented. Each case mints from its own id block (`IdBlockAllocator`,
`src/Eggnog/Relations/Contexts/IdBlockAllocator.cs`). The term lift reconciles a term that is present in every case by
minting it in the parent and unioning it in each case, which is the origin of G2's copies.

**I. Driver keys.** Not implemented, not measured. `JoinRun.Intersect.cs`, `JoinRun.Fused.cs` and `JoinRun.Materialized.cs`
(`src/Eggnog/Relations/Execution/`) drive one frame per (piece, key). A root run has one piece per atom and is
unaffected.

## 4. Value

**A. Compaction under live descendants.**
- *Who benefits:* library users who hold children of a large writable scope while it keeps running rules, users who run
  case splits at a large writable scope or at the root, and anyone who freezes a scope that has live children.
- *How much, in rows:* in the measured `math` run, a held child nearly doubles the root's physical rows (1.93 times;
  18.6 million more tombstones, 293 MB more row-buffer capacity).
- *Memory:* not established. The managed heap after forced collections did not grow in the same runs (§3).
- *Time:* not measured, and it can go either way. Dead rows slow full scans and index probes. Compaction costs
  O(physical cells) plus a full rebuild of every index on the table.
- *Split runs:* a split run at that scale pays the same, because its cases live from the first split step to the run's
  end (rule-layer D7). The caught-up variant suggests that step 0 would recover most of it, but no split run has been
  measured. No split benchmark exists in `src/Eggnog.Benchmarks`.
- *A scope split, then frozen:* not affected. A split run in a writable scope gates that scope's tables for the run,
  but `Run` disposes the cases before it returns (the fixture's schema compile asserts that the scope has no children
  after its run), so a freeze after the run compacts. Splits in short-lived children of a frozen scope gate only those
  children's own tables.
- *If never done:* the library documents the limitation. Do not hold children of a large writable scope across heavy
  work in it. Expect a split run at a large writable scope to keep its tombstones until it ends. Freeze a scope before
  creating children. The cost is memory and scan time, never correctness.

**A-d. Diagnostics and documentation.** These cover the one permanent harm, a scope frozen with live children, which
keeps its tombstones for good. Two remedies serve it without any remap. One is a documented rule: freeze before
creating children. The other is a report: the blocked compaction count at freeze in the scope's diagnostics, or a
count that a test or a host can read. Step 0 does not help that case when the tombstones lie below the child's cursor,
as in `FreezeTests`.

**B. Per-context replanning.**
- *Who benefits:* any user with several scopes of very different shapes under one graph, and in particular a
  long-lived host that saturates each sibling scope by a run before freezing it, whose global rules are then planned
  on the first scope's declarations before anything is derived.
- *How much:* unknown. The executor's run-time reordering mitigates a bad order only in part (engine map §8.2). When a
  scope's tables are small, a plan that is twice as bad may still cost little in absolute terms.
- *A benefit the first version of this plan missed:* plans made at freeze would replan the first scope's rules too.
  Today those plans see only declarations. A cheaper variant, "provisional plans", captures most of that benefit
  without plans per scope (§5).
- *If never done:* a host can choose which sibling scope is built first, and its fallback is one graph per scope,
  which costs a theory definition per graph (not measured here).

**C. G2.** No workload lifts terms in volume (the test fixture lifts value-keyed facts only). The measured cost is
linear in cases × terms per round and independent of the root. The alternative moves rewriting of the same order into
each case's own layer (L7 read-out). The expected saving is therefore the difference between copying a parent row and
rewriting an own row, not the removal of the work. *If never done:* no change. Smaller-id-wins stays egglog's rule at
every level, and the overlay, `Rekey`, `UnionIdMerge` and the invariant checker keep one ordering.

**D. B1.** It saves one own row and one hidden entry per lifted fact per live case. Cases die at the end of the run
(D7), and a whole case costs 13 to 53 KB, so the saving is a fraction of a few tens of KB per case for the length of one
run. *If never done:* nothing visible.

**E. B3.** It helps only a child that hides a large share of a large ancestor layer. Its benefit is avoiding ancestor
probes that return mostly hidden rows (literature review, correction 13). A representation change alone does not
remove those probes. Large hidden sets already become bitsets, which makes the membership test cheap but not the
probe. The measured copy volume per union is 2.9 rows on average (maximum 32), so measured hidden sets are small sorted
arrays and far from any layer's half. A child heavy enough to need B3 already costs about as much as a clone
(correction 13). *If never done:* such a child is slower than a clone would be. No workload has shown one.

**F. G5.** It lets a split run continue under a memory bound instead of stopping at `RowLimit`. Measured workloads open
few cases: the fixture's schema compile opens 3, and no split in the fixture's programs nests. *If never done:*
`RowLimit` and `CaseLimit` stop runaway runs soundly, and the report says why.

**G. G8.** Wall-clock time for split-heavy runs on several cores, for a host that is latency-bound on one split run. A
host that runs serial graphs and gets its concurrency from many children of frozen scopes at once gains nothing. *If
never done:* split runs use one core per top-level run. Children of frozen scopes still run concurrently.

**H. G9.** Fewer ids and fewer lift unions for terms that several cases derive. This matters only for the term lift,
which no workload in the repository uses in volume. *If never done:* nothing visible.

**I. Driver keys.** Less duplicate probe work in children whose keys have rows in more than one layer. Root runs are
unaffected. Children are small in every measured workload: the per-child benchmark matches 200 rows
([`../history/colors-m10-reports.md`](../history/colors-m10-reports.md), "Per-child cost as the root grows").
*If never done:* nothing visible. I27 requires only the same set of distinct bindings, so duplicates are correct.

## 5. Cost and risk

**A0. Compaction of the suffix above the highest live descendant cursor (small to medium).**
- *What it does:* when the trigger fires and the gate is closed, compute for the table `c` = the highest `Minor` among
  the live descendants' cursors into this layer, over every descendant at any depth, all in the current generation.
  Compact only the rows `[c, physical)`, serially and in order. Rows below `c` keep their ids. Hidden sets and cursors
  name only rows below the cursor (§3), so no row id that a descendant holds changes.
- *What it still has to touch:* the layer's generation. I16 is checked loudly (`CheckGenerations`, the invariant
  checker). Either every descendant's cursor entry and every `HiddenSet` over (layer, table) is re-tagged with the new
  generation, with contents unchanged, or `TableVersion` gains a stable prefix that cursors may rely on. I16 is restated
  as "a generation change keeps every row below every live descendant's cursor in place". All descendants of a
  writable context share its thread domain and hang off its `children` lists (`src/Eggnog/Relations/Contexts/Context.cs`),
  so the re-tag runs synchronously inside the parent's merge.
- *New primitives:* `RowBuffer.RemoveStale` from an offset, with `RemapRow` repointing only moved rows (it already
  skips rows whose id did not change). The sort-run list is truncated at `c` and rebuilt over the suffix.
- *Time cost:* the same as an ordinary compaction. It is O(suffix cells) for the move, but the generation change makes
  the next refresh of every index on the table a full O(rows) rebuild. An index truncation from `c` would avoid the full
  rebuild, but it is optional, and it needs an index walk of its own. Descendants' views over the layer are already
  dropped by the catch-up that follows any parent append, so they add nothing. Caches that survive a catch-up must be
  audited: child trie-node caches over ancestor layers (spec §9.6), `HiddenCursor`, rebuild-index refreshes for
  children, and extraction watches.
- *Risk to correctness:* lower than a remap. A missed re-tag fails loudly (`CheckGenerations` throws, the checker
  reports) instead of leaking hidden rows. A holder that is not generation-checked and names a row at or above its
  cursor would be a silent fault. The audit above must show that none exists, and the checker gains the check "every
  hidden entry and every cached row id is below its cursor".
- *What it does not fix:* tombstones below the cursors. In the probe they were 5% with a cursor that moved every
  iteration. In `FreezeTests` they were all of them.

**A-d. Diagnostics and documentation (small).** Two changes. First, a count of compactions blocked at freeze, surfaced
in the scope's diagnostics. Second, a paragraph in the architecture and API documentation: "freeze before creating
children", and what a split run at a large writable scope keeps. No engine semantics change.

**A1 (dropped). Cursor-only remap.** Compact when no live descendant hides rows of the layer, and map each cursor to the
count of surviving rows below it. Split cases hide parent rows from their first union copy and at every lift (catch-up
branch (b), union copies), so A1 almost never applies to split runs. For a held child it recovers nothing that A0 does
not. It also needs order-keeping parallel compaction, because `ParallelRehash` regroups rows within a sort run.

**A2 (later). Hidden-set remap.** Rewrite every descendant's `HiddenSet` over (layer, table) through a remap array,
dropping entries whose rows died, and map cursors as in A1. Cost per compaction: O(physical rows) for the remap array
plus O(hidden entries) across descendants. Risk: high if done wrong. A missed holder leaks hidden rows or skips appends,
and so yields wrong facts in children. Every future structure that names ancestor row ids must join the remap. The
oracles would be the context differential suites against replay from scratch, run in a mode that compacts at every merge
with a tombstone; the invariant checker after every rebuild; and a mutation-testing review of the new tests.

**B. Per-context replanning**, in two variants.
- *B-p, provisional plans (small).* A plan made during a run in a writable non-root scope is marked provisional.
  The first non-root freeze replans each provisional rule once, on the frozen scope's statistics, and publishes the new
  plan with the volatile write `RuleInfo` already uses. Plans are immutable, and progress and watermarks are kept per
  rule and context, not per plan, so a rule running concurrently on the old plan is unaffected. No memory per scope. It
  moves the plans of rules first run in a non-root scope from declaration-only statistics to that scope's saturated
  statistics. It does not help later sibling scopes that differ from the first.
- *B-s, selective per-scope plans (medium).* Each non-root freeze replans every rule it pre-plans and keeps a plan table
  entry (rule id to `CachedPlanInfo`) only where the plan differs from the shared one. A child resolves a rule's plan by
  walking up to the nearest frozen ancestor that has an entry, falling back to `RuleInfo`'s plan. Planning allocates
  about 12 KB per rule per freeze (§3), about 12 MB per frozen scope at 1,000 rules. The retained cost is about 2.8 KB
  per differing plan. Keeping every plan would retain about 2.8 MB per frozen scope at 1,000 rules, by extrapolation.
- *Correctness risk:* low for both. Results do not depend on plans, and the differential tests check results.
- *Interaction:* compiled plans ([`./compiled-plans.md`](./compiled-plans.md)) compile once per plan, so per-scope plans
  multiply compilation. B-p does not.
- *The freeze-planning option* of [`./rule-layer-split-features.md`](./rule-layer-split-features.md), item 4, cannot
  reach the plans of rules that a run before the freeze used. That plan reaches the same conclusion about the option,
  and merges it into this item.

**C. G2 (medium).** It touches the overlay's `Union` and `Rekey`, `UnionIdMerge` (which becomes aware of its context),
the invariant checker, the lift's member maps, and the root/child asymmetry with egglog. Correctness risk: medium,
guarded by the existing context differential and lift tests. It brings no performance gain anyone has shown.

**D. B1 (medium).** A new pruning mechanism (key removal from the child's hash, or a fall-through marker), a stamp rule
to avoid spurious deltas, and a B2 checker invariant. Correctness risk: medium, because it changes what a child lookup
returns.

**E. B3 (medium).** A `flattened` flag beside C10's `cleared` flag, a catch-up branch that copies later appends in, and
equivalence tests of flattened and unflattened children.

**F. G5 (medium).** It needs three pieces: a byte estimate per case, a "settled" test from the lift signature, and a way
to dispose a record while keeping its key skipped. Without the last, B6's skip disappears with the record and the next
step reopens the split, so cases would be opened, disposed and opened again. A re-split path must also reset progress.

**G. G8 (large).** A seal and thaw of a writable context (no path compression, no staging, prebuilt indexes and views,
then restoring all of it), thread-domain changes, a barrier and an ordered lift, and stress tests against serial runs
(decision 4 requires the same facts modulo renaming). It depends on the stress harness of
[`./parallelism-test-plan.md`](./parallelism-test-plan.md).

**H. G9 (large).** Store-wide id assignment per (constructor, parent-visible key), changes to `IsLocalId`, to which ids
may appear in which layer, and to the lift. It overlaps the store-global id questions of
[`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md).

**I. Driver keys (small to medium).** Merge a key's frames across pieces in the driver kernels, which are the hottest
child code. The root must keep its single-piece path untouched.

## 6. Worth-it evaluation

| Item | Deciding question | Cheapest decisive experiment | Go / no-go | Idle machine? | Preliminary verdict |
|---|---|---|---|---|---|
| A0 | First, owner's question 1: is holding children of a large writable scope, or splitting at one, a supported pattern? Then: does the closed gate cost time, and does A0 give it back? | No experiment before the owner's answer. If yes: time the root `math` run (and `ac`) with no child, with a held child, and with a held child on an A0 prototype. Take the counts of the `--stale-probe` mode beside each timing: tombstones above and below the highest cursor, `CompactionsBlocked`, row-buffer bytes, and the managed heap after forced collections with pool retention attributed. | No-go if question 1 is "no". Go if the held-child run is ≥ 10% slower than the run without a child and the A0 run is within 3% of the run without a child. No-go if A0 is not faster than the held-child run. | Yes, for time. Counts and bytes: no. | Do after the experiment |
| A-d | Is a scope frozen with live children a pattern users fall into? | None needed: `FreezeTests` shows the permanent cost, and nothing reports it | Go with A0. Also go alone if question 1 is "no", since the documented limitation then needs this text. | No | Do (small) |
| A1 | Does a cursor remap recover more than A0? | Already answered: the probe leaves ≤ 5% of tombstones below a cursor that moves every iteration, and split cases hide rows, which A1 excludes | No-go | No | Drop |
| A2 | In a split run that fires where it naturally fires, how many tombstones lie below the cases' cursors? | After A0, if the owner names a split workload at scale: run it and report the share of the run during which the gate was closed, peak row-buffer bytes, and tombstones below the highest live cursor | Go only if tombstones below the cursors are ≥ 25% of the tombstones at the run's peak and the row buffers they hold are ≥ 100 MB | Counts: no | Later |
| B | Does a plan made on one scope's statistics cost much on a sibling's? | First the kill test, already partly made: plan bytes per rule (2.8 KB retained, 12 KB allocated), which rules out plans per scope for every rule. Then build the fixed corpus that `ScopedQueryCases.CompileAll` builds on the test fixture (`src/Eggnog.Tests/Predicates/Scoped/`): every query case's schema plus `DiscriminatedSchema`, as sibling schema scopes under one frozen root, each through its compile path (declare, run, then freeze), in that order and in reverse. The corpus is fixed before any measurement, and no scope is added or chosen to make plans differ. Compare total derivation work per query (own and ancestor rows scanned plus rows indexed, from each query child's `ExecutorCounters`), or isolate rulesets for per-rule figures. Repeat with B-p prototyped. | Go for B-p if the declaration-only plans cost ≥ 1.5× the work of plans made on the frozen scope for some scope of that corpus. Go for B-s only if a later scope still costs ≥ 1.5× after B-p. | Counts: no. The timing half of Q10: yes | Later |
| C | Is copying parent rows into cases more expensive than rewriting the cases' own rows? | Already answered by the L7 report: same order of rows either way, linear in cases × terms, independent of the root | No-go unless a workload lifts terms in volume | No | Drop; record the decision |
| D | Does keeping duplicate own rows in cases cost memory that matters? | Count own rows equal to the visible ancestor row (the B2 invariant's "no own row is equal, after merging, to the visible ancestor row") at each lift, in `CaseSplitMemoryBenchmarks` and in the case-split tests over the test fixture (`src/Eggnog.Tests/Predicates`) | Go only if such rows are ≥ 25% of case bytes and some workload's cases live long | No | Drop |
| E | Does any child hide a large share of a large ancestor layer? | A test-only histogram of hidden-set size ÷ layer rows at dispose, over the context and scoped suites and `CopyVolumeBenchmarks` | Go only if some workload has a child hiding > 25% of an ancestor layer of ≥ 10,000 rows | No | Drop; revisit only if that histogram ever shows it |
| F | Do split runs fail on memory before `RowLimit` or `CaseLimit` stops them? | None until a user reports one | Go only on a workload that shows it | No | Later |
| G | Is in-case work the bulk of a split run, on a workload that runs large splits? | Count rows written and matches in cases against the parent per split run on that workload; then time it | Go only if in-case work is ≥ 70% of the run on that workload | Yes, for time | Later |
| H | Does the term lift matter to anyone? | None now | No-go | No | Drop |
| I | How often is a key driven from more than one piece? | A counter in the driver kernels for (position, key) frames whose key has rows in more than one piece, over the scoped suites, `PerChildCostBenchmarks` and `CaseSplitMemoryBenchmarks` | Go only if duplicates are ≥ 20% of child frames on a workload whose children are not small | No | Drop |

The A0 thresholds no longer use physical ÷ live ratios. Egglog's trigger allows any ratio up to 2.0 without a child,
and the run without a child measured 1.60 at iteration 9, so a ratio threshold could fail with compaction working
correctly.

## 7. Plan if it goes ahead

Only A0, A-d and B have plans. The others need none unless a user's workload reopens them.

**Phase 0: owner's answers and the cheap counts.**
- Entry: this plan accepted.
- Work: the owner answers questions 1 and 4. Add the probe to `src/Eggnog.Benchmarks` as `--stale-probe <iterations>
  none|held|caughtup`, which prints the per-iteration counts and the end-of-run bytes of §3. Attribute the heap figure,
  by trimming `ArrayPool<T>.Shared` or by counting the bytes of row buffers, indexes and the pool separately. Measure
  B's plan bytes at 100 and 1,000 rules on a synthetic theory, to replace the extrapolation.
- Exit: the figures written into this document, and a go or no-go for A0 and B-p.

**Phase A-d: diagnostics and documentation.**
- Entry: Phase 0 done (either answer to question 1).
- Work: blocked compactions at freeze surfaced in the scope's diagnostics, and the documentation paragraph of §5.
- Tests: a scope frozen with a live child reports its blocked compactions. A scope frozen without children reports
  none.

**Phase A0: suffix compaction.**
- Entry: question 1 answered "yes", and the idle-machine timing of §6 is a go.
- Work: suffix compaction in `SortedWritesTable.MaybeRehash` and `ForceRehash` when the gate is closed, serial and in
  order. The generation re-tag of descendants' cursors and hidden sets. The audit of every holder of ancestor row ids
  (§5). I16 restated in [`../architecture.md`](../architecture.md) and in the invariant checker, which gains the
  "below the cursor" check.
- Tests:
  - `CompactionGateTests` turned into suffix-compaction tests, with descendants at several depths and cursors at
    several positions.
  - A forced mode that compacts at every merge with a tombstone, running the context suites, the differential tests
    and the case-split tests over the test fixture (`src/Eggnog.Tests/Predicates`).
  - The held-child probe as an allocation test. After every iteration, each table's tombstones at or above the highest
    cursor are at most `max(16, physical/2)`, the same bound as the run without a child. No ratio is asserted.
- Practice: implement; review from several angles, including a mutation-testing review of the new tests; fix; verify on
  net8.0 and net10.0.
- Exit: all suites green in both modes. The root gates of [`./idle-machine-measurement.md`](./idle-machine-measurement.md)
  (`RuleSetRunBenchmarks`, `WritesBenchmarks`, `DisplacedRebuildBenchmarks`) within 2% when run. The A0 timing within
  3% of the run without a child.

**Phase B-p: provisional plans.**
- Entry: the sibling-scope count of §6 is a go for B-p.
- Work: a provisional flag on plans made in a writable non-root scope's run, and a replan at the first non-root
  freeze.
- Tests: the plan-golden tests. Differential runs with and without replanning must give equal results. The
  sibling-scope counts as regression tests.
- Documentation: [`../architecture.md`](../architecture.md) §3.5 ("A rule is planned once…"), and rule-layer residual
  risk 4 restated.

**Phase B-s: selective per-scope plans.** Only if B-p leaves a later sibling scope ≥ 1.5× worse, and the owner accepts
the retained plan bytes per frozen scope that Phase 0 measures at 1,000 rules. Work and tests as B-p, plus a freeze-time
allocation figure at 100 and 1,000 rules.

**Closing the dropped items (documentation only).** Remove G2, B1, B3, G9 and the driver-key item from the roadmap's
"Left open by design", with one line each saying why they are not planned. Keep G5 and G8 as "not planned until a
user needs them", and A2 as "later, if a split workload shows tombstones below the cursors". Record G2's decision in
[decisions](../engine/decisions.md): smaller id wins at every level. Correct the roadmap's two misdescriptions:
"split-driver keys" (driver keys across layer pieces) and "pruning and flattening of hidden sets" (B1 prunes a child's
rows). These edits belong to [`./conformance-docs-release.md`](./conformance-docs-release.md).

## 8. Dependencies and ordering

- **Interning and value lifetime** ([`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md)) owns
  store-global ids and eq-sort containers in children. G9 would build on its store-wide id design, which is another
  reason to drop G9 here.
- **Compiled plans** ([`./compiled-plans.md`](./compiled-plans.md)): B-s multiplies compiled delegates, and B-p does
  not. Decide B before compiled plans fix their cache key.
- **Performance parity** ([`./performance-parity.md`](./performance-parity.md)): A0 touches the root's merge path. Land
  it after the `BasicMath` regression is understood, so the two are not confounded.
- **Idle-machine measurement** ([`./idle-machine-measurement.md`](./idle-machine-measurement.md)): the timing that
  decides A0, the root gates after A0, and Q10, whose plan comparison and timing inform B.
- **Parallelism test plan** ([`./parallelism-test-plan.md`](./parallelism-test-plan.md)): a prerequisite of G8 if it is
  ever reopened.
- **Rule-layer and split features** ([`./rule-layer-split-features.md`](./rule-layer-split-features.md)): its item 4,
  the freeze-planning option, is merged into B here. It also owns D7, D17 and the split options that G5 and G8 would
  change.
- **Conformance, documentation and release** ([`./conformance-docs-release.md`](./conformance-docs-release.md)): the
  roadmap and decision edits of §7.
- **Smaller engine items** ([`./smaller-engine-items.md`](./smaller-engine-items.md)): no dependency.

## 9. Questions for the owner

1. Should the library make it cheap to hold a child of a large writable scope, or to run case splits at a large
   writable scope or at the root? If not, A0 is dropped, and A-d documents the limitation.
2. May G2 be closed with "smaller id wins at every level" recorded as a decision, given the L7 read-out?
3. Do you accept dropping A1, B1, B3, G9 and the driver-key item, and marking A2, G5 and G8 "not planned until a
   user needs them"?
4. For residual risk 4: global rules that a non-root scope's run uses before its freeze are planned on that scope's
   declarations alone, before anything is derived. Is that acceptable until the sibling-scope count exists? Should
   provisional plans (B-p) be the first remedy if the count shows a loss, with per-scope plans only after it?
5. If A0 goes ahead: may a forced-compaction test mode (compact at every merge with a tombstone) become a permanent test
   configuration on net8.0 and net10.0, even though it lengthens the context suites?
6. May the roadmap's "split-driver keys" be corrected to "driver keys across layer pieces", and "pruning and flattening
   of hidden sets" to "pruning of child rows an ancestor made redundant (B1) and flattening of hidden sets (B3)"?
7. Should `Scope.Freeze` also warn when the scope has live children, or is reporting blocked compactions enough?

## 10. Review

A skeptical review of the first version raised 15 problems. All were checked against the sources. Thirteen were
accepted as stated, and two were accepted in part. The recommendation narrowed: A became step 0 only, behind the
owner's question; B moved from "Do after an experiment" to "Later".

Changed:
- **Ratio thresholds dropped** (major). The trigger allows up to 2.0 physical rows per live row without a child
  (`MaybeRehash`). §6 and the A0 tests now use tombstones relative to the trigger's bound, tombstones relative to the
  highest cursor, `CompactionsBlocked`, row-buffer bytes and heap.
- **Step 0 added, step 1 dropped** (major). The probe was rerun with a caught-up child and a count of tombstones above
  the cursor. The held child's tombstones all lie above its cursor. A child caught up every iteration still has 95%
  above it. A0 replaces A1, and A2 became "Later", with its own evidence threshold. In part: A0 does need a generation
  re-tag of cursors and hidden sets, because I16 is checked loudly. "Needs no remap" holds for row ids only.
- **The A experiment** (major). Question 1 is now the entry gate, with no experiment before it. The deciding measure is
  an idle-machine timing that is two-sided, and the early-firing split experiment is gone. A natural split run decides
  A2 only.
- **Time cost restated** (major). Index refresh is incremental, delta atoms are narrowed by timestamp, and compaction
  forces full index rebuilds (§3, §4, §5).
- **B's premise corrected** (major). Plans are fixed by the first run of each rule. When a non-root scope is saturated
  before its freeze, that is the first such scope's run, on declarations alone (the fixture's schema compile with
  `PredicateTheory.Facts`). The benefit of replanning the first scope's rules is now credited, and provisional plans
  (B-p) are added as the cheap variant.
- **B's experiment** (major). Plan bytes were measured first: about 2.8 KB retained and 12 KB allocated per plan, which
  rules out a plan per scope for every rule at 1,000 rules. The comparison now uses sibling schema scopes built through
  the full path (declare, run, freeze), total derivation work per query (`ExecutorCounters` is per `Database`), and a
  threshold of 1.5×.
- **A for a scope split, then frozen** (minor): the split gates the scope during its run, and the freeze compacts after
  `Run` disposes the cases.
- **Figures corrected** (minor): "nearly doubles (1.93×)". Bytes now come from measured row-buffer capacities, not a
  four-cell estimate. The measured heap contradicts a memory gain, and that is now stated.
- **Probe described exactly** (minor), and proposed as a `--stale-probe` benchmark mode.
- **E corrected** (minor): measured hidden sets are small sorted arrays, and B3's benefit is avoiding probes.
- **I's citation** (minor, accepted in part): I27 now governs. Decision 6 stays as the item the spec's §18.2 maps to
  layered multiplicity.
- **The roadmap's B1 wording** (minor): added to the corrections and to question 6.
- **Depth in F** (minor): F now states only what the fixture's splits show, that none of them nests.
- **Publication** (minor): the milestone-labelled test name is replaced by the test classes, and decisions is a
  relative link. Every `./<key>.md` link was checked against the plan keys. All exist among the plans except
  `conformance-docs-release.md`, which is the key every sibling plan uses for that workstream, and which must exist
  before publishing.
- **Frozen with live children** (minor): A-d added as the cheap mitigation, with question 7.

Rejected: none in full. The two partial acceptances are noted above. No split benchmark exists to "measure what step 0
recovers in a split run". The caught-up child stands in for a case's cursor movement, and a natural split run remains
A2's experiment.
