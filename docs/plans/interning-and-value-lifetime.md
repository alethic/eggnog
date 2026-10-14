# Interning and value lifetime

This plan covers the interning pools and the other store-wide state that outlives a scope: decision D15 and its child
case, values that die with a scope (scoped interning), eq-sort containers interned from a child context, a
retained-memory test of query-scope churn, and the exhaustion of the store's id counter and clock. It answers the
owner's question on interning in colored contexts in sections 3 and 4.

## 1. Summary and recommendation

**Recommendation: Do now, for a small part of the workstream. Confidence: medium-high.**

Three small parts should be done now:

- Phase A documents how a child interns values and what a disposed scope leaves behind.
- Phase B adds churn and retained-memory tests, which pin that a disposed query scope keeps nothing beyond the values it
  newly interned.
- Phase C adds a public usage gauge. The gauge reports interned values, ids reserved and epochs used, each against its
  limit. Phase C also gives every minting path one documented exhaustion bound and one exception type.

None of the three waits on anything else. Phase C matters most to hosts that run case splits on a long-lived graph,
because the clock runs out first there (section 3.4).

Four parts wait for data (**Later**), each on a named trigger:

- a smaller first id block for children (16 ids to 4);
- ids and epochs local to the transient children of a frozen scope;
- scoped interning;
- eq-sort containers in children.

Two parts should be dropped: 64-bit cells, and recycling ids or the clock globally in place.

The reason is a set of measurements made for this plan, described in section 3.4. Two dry runs of query-scope churn on
the predicate test theory found that interning is not what limits the life of a long-lived graph:

- Queries that reuse their literals intern nothing per query.
- A disposed query scope retains no memory beyond the values it newly interned: 0.6 B per query over 5,000 queries.

The limits that bind are the store's id counter and its clock. Every query scope that mints an id reserves 16 ids. A
plain query, including its snapshot read, uses 15 epochs. A query with a domain split uses about 20 + 10 x *cases*
epochs on this theory. That is 50 epochs at 3 cases and 660 epochs at 64 cases, the default `CaseLimit`. At an
illustrative 100 queries per second:

- plain queries exhaust the ids in about 15.5 days, and the clock in about 33 days;
- split queries exhaust the clock first: in about 10 days at 3 cases and in about 18 hours at 64 cases.

When either limit is reached, a raw `OverflowException` is thrown. It is not documented, it does not fault the scope,
and once the clock has run out `CreateChild` throws too. The only usage figure the library exposes today is
`ValueStore.InternedCount`, which D15 provides so that a host can retire a graph before it grows too large. It cannot
see exhaustion coming: on a workload that reuses its literals it does not move at all. With a gauge, the host can
retire the graph before the limit, and exhaustion becomes documented behaviour. Of everything in this workstream, the
gauge gives the most value for its cost.

Retiring a graph is paid once per graph life: the host builds a new `EGraph` (about 56 KB allocated, see
[idle-machine measurement](./idle-machine-measurement.md), section 3.3), defines its theory again and rebuilds its
frozen scopes. That cost depends on the host's theory and is not measured here. At the horizons above it is spread over
millions of queries, and that is the main reason the other parts can wait. A smaller first block extends a plain-query
graph's life by about 2.1x and a split graph's by nothing, since the clock binds first. Recycling ids and epochs within
the transient children of a frozen scope would remove both limits for a host whose root and schema scopes are frozen.
It is medium-to-large work, and its clock half carries a high risk. Neither change is worth its cost while a host can
retire a graph on the gauge.

## 2. What it is and why it was planned

- **Base values are global, as in egglog.** The engine map's per-subsystem table
  ([../history/egglog-engine-map.md](../history/egglog-engine-map.md), section 4.3 "Per-subsystem requirements",
  row "Base values") considered a per-context overlay and chose "Global interner, as in egglog". The same document
  lists "`u32` exhaustion" among its risks (section 8.2). Ids and timestamps come from store-global 32-bit counters and
  are never reclaimed. The document says to "decide early whether a store can be recycled, or whether 64-bit cells are
  worth their cost". That decision was never recorded.
- **D15 (interner growth)** is in [../history/rule-layer-spec.md](../history/rule-layer-spec.md), section 1
  "Decisions" (subsection 1.1). Section 6.1 "Values, interning, primitives, lattices" and section 11, residual risk 3,
  expand on it. D15 keeps the pools permanent per `EGraph`:
  - Host reads never intern (`TryEncodeExisting`, engine request E1).
  - `ValueStore.InternedCount` reports growth so that a host can recycle a graph.
  - Constants of rules and queries intern when they are defined.
  - Invariant V (section 6.1) requires equal values of a column type to have equal cells in every scope, because value
    joins and not-equal tests compare raw cells.
- **Values that die with a scope.** The roadmap ([../roadmap.md](../roadmap.md), "Library work") lists values that die
  with a scope, or interning scoped to a context, as open. Its workarounds are canonicalizing literals (mapping a
  request's literals onto a bounded set of values) and retiring graphs. No test churns query scopes to check what a
  disposed scope leaves behind (section 3.5).
- **Eq-sort containers in children** (C13 in the engine map, section 4.2) were deferred. The interim rule refuses
  them ([../engine/decisions.md](../engine/decisions.md), decision 9;
  [../history/colored-contexts-spec.md](../history/colored-contexts-spec.md), sections 7.4 and 10.7, section 19 item
  8, and section 21.11 "Still deferred"). The roadmap ([../roadmap.md](../roadmap.md)) lists the feature under "Left
  open by design" and lists the push/pop programs that wait on it under "Push/pop conformance".
- **Id blocks.** Children mint ids from blocks reserved on the store's counter
  ([../history/colored-contexts-spec.md](../history/colored-contexts-spec.md), section 4.2). The first block is 16
  ids. That size was chosen in response to review finding R2-14 (section 20), so that the root's arrays are not
  inflated. [../history/colors-m10-reports.md](../history/colors-m10-reports.md) ("L7", read-out "Id-array growth")
  measures the growth of the root's array.

## 3. Current state

### 3.1 How a child context interns (answer to the owner's first question, from the code)

**One pool per graph, shared by every scope.** A child's engine database shares its root's container environments
and base-value registry by reference:

- The child constructor in `src/Eggnog/Relations/Contexts/Database.Contexts.cs` sets
  `containers = root.Containers; baseValues = root.BaseValues;`. Its remarks give the reason: "a value the root boxed
  unboxes in the child".
- The library layer does not reach the pool through a context at all. `EGraph`'s constructor
  (`src/Eggnog/Graph/EGraph.cs`) binds `ValueStore` and every column type to `Engine.BaseValues`.
- `ColumnType<T>.Encode(T)` (`src/Eggnog/Values/ColumnType{T}.cs`) takes no scope.

So the encoding of a value does not depend on which scope encodes it. This is what keeps invariant V true across
scopes, siblings and lifts.

**What interns, by path.** Every call to `Encode` or `EncodeObject` interns into the graph's pool when its codec cannot
box the value inline, whichever scope makes the call. The following paths reach that code:

| Path in a child scope | Code | Interns |
|---|---|---|
| Host writes with literals (`Make`, `Assume`, `Write`) | `src/Eggnog/Host/HostBatch.cs` (`PlanTerm`, `type.EncodeObject`) | each new string, double, set or registered value, and each `long` outside `[0, 2^31)` |
| Rule-head and body primitive results computed in a child run | `src/Eggnog/Terms/Primitive{…}.cs` (`InvokeCore` encodes the result), called by `src/Eggnog/Rules/PrimitiveCall.cs` | each distinct result, for example a new set from `Union` or `Intersect` |
| Lattice merges in a child run, and in a child's catch-up | `src/Eggnog/Values/LatticeMerge{T}.cs` (`type.Encode(lattice.Merge(…))`) | each distinct merged value |
| Case joins at lift | `src/Eggnog/Values/CaseJoinAdapter{T}.cs` | each distinct joined value, interned in the shared pool for the parent |
| Split-case assumptions | `src/Eggnog/Cases/SplitAssumptions.cs` (grounds a case's terms, `EncodeObject` and `InvokeCore`) | for example the singleton set a domain split assumes in each case |
| Constants of local rules and queries, at definition | `src/Eggnog/Rules/ProgramBuilder.cs` (`Operand.Constant(type, type.EncodeObject(…))`) | each new constant |
| Filters | `src/Eggnog/Rules/FilterCall.cs` encodes `true` once | nothing new (booleans box inline) |

Host reads, checks and comparisons never intern:

- They use `TryEncodeExisting` (`BaseValues.TryGetExisting`). A constant that was never interned makes its fact false.
  `src/Eggnog.Tests/Host/HostReadTests.cs`, `src/Eggnog.Tests/Rules/QueryMatchTests.cs` and
  `src/Eggnog.Tests/Values/ValueTests.cs` assert this.
- The second dry run in section 3.4 confirms it for a snapshot read (`Check`, `TryGet` and `ForEachMatch`), which
  interned 0 values per query.

Decoding is lock-free. Interning takes one of 16 shard locks (`src/Eggnog/Relations/Hashing/InternTable.cs`).

**What remains after the child is disposed.** Everything the child interned. The remarks of `BaseValues`
(`src/Eggnog/Relations/BaseValues/BaseValues.cs`) say: "Interned values are never freed: the store is append-only, as in
egglog". egglog's push and pop behave the same way. Its `BaseValues` clone shares the intern tables through `Arc`s
(`BaseValues.Clone` remarks), so a pop frees nothing. For each type, `InternTable<T>` keeps the following:

- A `keys` array that doubles as it grows and never shrinks. For `long` and `double` it holds the value itself. For
  every other type it holds a reference to each interned object: a string, a `ValueSet<T>` and its element array, or a
  user's registered object with everything it references.
- One `RawHashTable<uint>` entry per value, in one of its 16 shards.
- The count that `InternedCount` sums.

No table row refers to these values any more, but nothing can tell that. So they stay reachable for the life of the
graph, and each full garbage collection traces them.

**The cell space is finite but is not the practical limit.** A string, double, set or registered value is stored as
its intern index. A `long` outside `[0, 2^31)` is stored as `BaseValues.InternedTag` plus its index. Two checks bound
the tables:

- `BaseInternTable.InternInTable` (`src/Eggnog/Relations/BaseValues/BaseInternTable.cs`) throws "Interned value
  overflowed" past `BaseValues.MaxTaggedIndex`.
- `InternTable.Push` throws "The intern table is full" at `Array.MaxLength`.

That allows about 2.1 x 10^9 values per base type. Section 3.4 measures 68-76 bytes per value, so memory runs out long
before that bound: 2 x 10^9 values would need well over 100 GB.

### 3.2 Other store-wide state that a disposed scope leaves behind

| State | What a child does to it | Reclaimed at dispose? | Evidence |
|---|---|---|---|
| Base-value pools | interns into them (section 3.1) | no | `BaseValues` remarks |
| The store's id counter | each child that mints reserves blocks: first 16 ids, then twice the total so far, up to 4,096 (`src/Eggnog/Relations/Contexts/IdBlockAllocator.cs`). A child that mints nothing reserves nothing. Base-value containers interned from a child take ids straight from the shared counter (`ContainerEnv.MintId`, `src/Eggnog/Relations/Containers/ContainerEnv.cs`) | no. The minting paths do not agree on the limit or on the exception (see the minting-sites table below) | dry runs, section 3.4 |
| The store's clock | each run, rebuild pass and write batch of the child advances it (`Store.AdvanceClock`, `src/Eggnog/Relations/Contexts/Store.cs`) | no; `Store.CheckEpoch` throws `OverflowException` at `Value.StaleRep` (2^32 - 1) | dry runs, section 3.4 |
| The root's union-find arrays | unaffected while the root does not union. A root that later unions an id above the children's reserved blocks grows its dense arrays to that id, at 8 bytes per id below it | no | colored-contexts spec section 4.2 ("Memory at the root"); colors-m10-reports L7: n single-mint cases reserve 16n ids, so 10^5 cases reserve 1.6 M ids and a later root union costs about 13 MB. A frozen root never unions, so this does not arise under one |
| Base-value containers (engine level only) | interned from a child into the root's shared environment | no | `ContainerEnv.GetOrInsert`. The library registers no containers (D20), so only engine-level programs reach this path |
| Local rules and their call-site external functions | registered when the rule is defined | yes. `RuleVariants.Free` (`src/Eggnog/Rules/RuleVariants.cs`) frees each variant, removes its `VariantsByDescription` entry and frees its external functions. Freed rule ids and external-function ids are reused, most recently freed first (`src/Eggnog/Relations/Bridge/RuleTable.cs`, `src/Eggnog/Relations/Database/ExternalFunctionRegistry.cs`) | the first dry run's local-rule pass: `VariantsByDescription` back to its baseline after 5,000 local rules |
| Constants of local rules and per-request queries | interned at definition | no, as base values | D15 |
| Scope tags | none: tags are graph definitions (`EGraph.DefineScopeTag`, `src/Eggnog/Graph/EGraph.Scopes.cs`), kept for the graph's life | not applicable. A host that defines one tag per request grows the graph's tag array and its mirror registry | `src/Eggnog/Graph/ScopeTag.cs` |
| Mirror tables | the first scoped read of a `(table, tag)` pair creates one, which is a graph definition (`src/Eggnog/Scopes/MirrorRegistry.cs`) | no, but there are at most (value-only tables) x (tags) of them | `MirrorRegistry.GetOrCreate` |
| The child's own layers, overlay, journal and cursors | its own | yes, once it has no live children and no call is in progress (D40; [../architecture.md](../architecture.md) section 2.12) | `src/Eggnog.Tests/Scopes/DisposeTests.cs`, `DeferredReleaseTests.cs` |

**Minting sites and their bounds.** Four code paths mint ids. They use three different bounds and throw two different
exception types at the bound:

| Site | Used by | Bound | Exception |
|---|---|---|---|
| `Context.NewId` (`src/Eggnog/Relations/Contexts/Context.UnionFind.cs`) at the root, and `IdBlockAllocator` in a child | library host writes (`HostBatch` through `EGraphContext.FreshId`), and every term a child mints | `UnionFind.MaxId` = `Array.MaxLength - 1` = 2,147,483,590 | `OverflowException`. In a child the counter has already moved, so the failed block's ids are lost (`IdBlockAllocator` remarks) |
| `ExecutionState.ValueFromCounter` (`src/Eggnog/Relations/Actions/ExecutionState.Predicted.cs`) | rule mints. At the root they reach it through `Database.IncrementCounterInView`, which increments the shared counter with no `MaxId` check | below `Value.StaleRep` (2^32 - 1) | `OverflowException`. A root id between `MaxId` and `StaleRep` passes here and fails later, when the union-find grows to it (`UnionFind.Grow` throws `ArgumentOutOfRangeException`) |
| `Relations.EGraph.FreshId` (`src/Eggnog/Relations/Bridge/EGraph.cs`) | `EGraphContext.AddTermAtRoot` | `checked((uint)…)`, which admits `StaleRep` itself | `OverflowException` only above `StaleRep` |
| `ContainerEnv.MintId` (`src/Eggnog/Relations/Containers/ContainerEnv.cs`) | container ids, on the shared counter | below `Value.StaleRep` | `InvalidOperationException` |

Under a frozen root, only the child path, the first row, is reached. The other rows matter to hosts that keep a
writable root.

### 3.3 Eq-sort containers in children

Eq-sort containers in children are refused. Four mechanisms enforce this:

- `ContainerEnv.GetOrInsert` throws when `state.InChildContext && HoldsIds`.
- `src/Eggnog/Relations/Contexts/EqContainerGuard.cs` faults a child whose own displacement appears in an eq container
  (`NotSupportedException`). It re-checks whenever `ContainerValues.EqVersion` moves.
- Frozen databases intern no new eq container.
- Child databases never rebuild containers (colored-contexts spec, section 7.4).

The library registers no container. `ValueSet<T>` is an interned base value, and there are no sets of classes (D20).
So only engine-level programs can reach this code. The following programs wait on the feature:

- `container-rebuild.egg` needs only a `Vec` of an eq sort.
- `container-fail.egg` also needs only a `Vec` of an eq sort. It is egglog's regression test for issue 831, a child
  container that rebuilt in place.
- `vec.egg` also needs `UnstableFn`.
- The web demo's `lambda.egg`, `typeinfer.egg` and `fusion.egg` need `Set` containers of e-classes.

The remarks of `src/Eggnog.Tests/Relations/Bridge/EggPushPopTests.cs` and `EggPushPopWebDemoTests.cs` list these
programs.

### 3.4 Measurements: two dry runs of query-scope churn

**Method.** These figures are counts and heap deltas, not timings. Both dry runs used a private clone of the current
`main` (`eda0c61`), a Release build, net10.0 and workstation GC, on a machine shared with other work. Console probes
used the scoped predicate test theory, `PredicateTheory` (`src/Eggnog.Tests/Predicates/`): a theory of predicates over
document paths (presence, forms, domains, types and numeric bounds) in which scopes are the contexts.

*First dry run.* The setup was a frozen root and one frozen schema scope. The schema declares `kind` with domain
`{a, b, c}` and `n` within `[0, 100]`. For each query the probe:

1. ran `CreateChild(Query)`;
2. ran `Make` of the predicate;
3. ran `Assume` of the where clause;
4. ran `Run`. Plain rows saturated the horn, lattice and rewrite rulesets. Split rows ran the theory's `Saturation`
   schedule, with a domain split over a discriminated `kind`;
5. ran `Dispose`.

Each row ran 200 warm-up queries, then 5,000 measured ones. Before and after each row, the probe read
`InternedCount`, the store's id and clock counters, the child's `IdBlocks.Minted`, `GC.GetTotalAllocatedBytes` and
`GC.GetTotalMemory(true)`.

| Query shape | Interned per query | Ids reserved per query | Ids minted per query | Epochs per query | Retained heap per query | Allocated per query |
|---|---:|---:|---:|---:|---:|---:|
| `StrEq(kind, "a")`, same literal | 0 | 16 | 1 | 14 | 0.6 B | 301 KB |
| `StrEq(kind, fresh)` | 2 | 16 | 1 | 14 | 136 B (68 B per value) | 321 KB |
| `NumGt(n, 50)`, same number | 0 | 16 | 1 | 11 | 0 B | 230 KB |
| `NumGt(n, fresh small number)` | 0 | 16 | 1 | 11 | 0 B | 247 KB |
| `Or(StrEq(kind, fresh), StrEq(kind, "a"))` | 3 | 16 | 3 | 17 | 227 B (76 B per value) | 431 KB |
| same literal, plus one local rule with a fresh constant | 1 | 16 | 1 | 14 | 51 B | 350 KB |
| split query (3 cases), same literals | 0 | 16 | 3 | 50 | 0.1 B | 1.15 MB |
| split query (3 cases), fresh literal | 2 | 16 | 3 | 50 | 148 B (74 B per value) | 1.15 MB |

*Second dry run* (counts only, 20 warm-up and 200 measured queries per row):

- The split query was repeated with the discriminator's domain widened from 3 values to 6, 12, 24, 48 and 64. Branches
  were still declared on `a`, `b` and `c`.
- The same-literal plain query was extended with a snapshot read of the kind a host makes after each query:
  `Check(present(kind))`, `TryGet` of `dom(kind)` and `types(kind)`, and one `ForEachMatch` over a reusable query of
  `ne` rows.

| Query shape | Cases opened (`RunReport.CasesOpened`) | Epochs per query | Ids reserved per query | Ids minted per query | Interned per query |
|---|---:|---:|---:|---:|---:|
| plain, same literal, with snapshot read | 0 | 15 (12 in `Run`, 1 in the read) | 16 (0 in the read) | 1 | 0 (0 in the read) |
| split, domain of 3 | 3 | 50 | 16 | 3 | 0 |
| split, domain of 6 | 6 | 80 | 16 | 3 | 0 |
| split, domain of 12 | 12 | 140 | 16 | 3 | 0 |
| split, domain of 24 | 24 | 260 | 16 | 3 | 0 |
| split, domain of 48 | 48 | 500 | 16 | 3 | 0 |
| split, domain of 64 | 64 | 660 | 16 | 3 | 0 |

On this theory a split query uses exactly 20 + 10 x *cases* epochs. The cases mint nothing and reserve no block, so ids
per query do not move with the number of cases. `RunOptions.CaseLimit` defaults to 64 and `SplitDepthLimit` to 4
(`src/Eggnog/Scheduling/RunOptions.cs`).

What these figures show:

- **A disposed query scope retains nothing beyond what it newly interned. Its allocation per query is large.**
  - With reused literals, a disposed query retains nothing: 0.6 B per query over 5,000 queries is noise.
  - With fresh literals, the retained memory equals the newly interned values, at 68-76 B each (short strings and
    singleton sets).
  - Local rules free their variants: `VariantsByDescription` returned to its baseline.
  - Allocation per query is 230-431 KB for a plain query and 1.15 MB for a 3-case split query on this theory. The
    [idle-machine-measurement plan](./idle-machine-measurement.md) owns the allocation and latency figures of
    transient children. It already traces most of a transient child's per-rule allocation to
    `PlanTemplate.InstantiateLayered`, so this plan hands the allocation figures to it.
- **Fresh literals intern per query unless the host canonicalizes them.** On the test theory a fresh string literal
  interns 2 values: the string, and the singleton set that `inset` computes. A host that maps its literals onto a
  bounded set of values interns nothing per query. The test theory's `long` bounds intern nothing for small numbers,
  because they box inline. That holds only for `long` values in `[0, 2^31)`. An encoding that stores numbers as
  strings, doubles, sets or registered values interns each fresh number or bound (section 3.1).
- **Ids and epochs are what a query consumes.**
  - A query that mints 1 to 3 ids reserves a whole 16-id block.
  - A third run of the plain shapes, with `IdBlockAllocator.FirstBlockSize` set to 4, reserved 4 ids per query. All
    other columns stayed the same, and allocation per query differed by less than 0.1%.
  - A split's epochs grow with its cases. Its ids do not, as long as the cases mint nothing.
  - On a theory whose cases do mint, each minting case reserves its own first block (colors-m10-reports L7). Ids per
    query would then be 16 x (1 + minting cases), against about 10 epochs per case, and the ids would bind first.
- **What exhaustion looks like.** The probe also advanced the store's counters to just below their limits.
  - When the ids run out, `Make` in a new query throws a raw `OverflowException` ("The store has run out of ids…"). The
    scope is not faulted, and every later child that mints fails the same way.
  - When the clock runs out, the query's write throws `OverflowException`, and then `CreateChild` itself throws, so the
    graph can create no more children.
  - Neither exception is documented on the public API: no XML comment under `src/Eggnog/Graph` or `src/Eggnog/Values`
    mentions `OverflowException`.

**Horizons.** The limits are `UnionFind.MaxId` = 2,147,483,590 ids and `Value.StaleRep` - 1 = 4,294,967,294 epochs.
The query rates are illustrative; no rate is assumed. The `InternedCount` rows are written for a host that retires its
graph at T interned values; no threshold is assumed either. A graph's life is the shorter of its id horizon and its
clock horizon.

| Limit | Per query | Queries until exhaustion | At 10 per second | At 100 per second | At 1,000 per second |
|---|---:|---:|---:|---:|---:|
| ids, 16-id first block (plain or split, cases not minting) | 16 | 134 M | 155 days | 15.5 days | 37 hours |
| ids, 4-id first block | 4 | 537 M | 1.7 years | 62 days | 6.2 days |
| clock, plain query with snapshot read | 15 | 286 M | 331 days | 33 days | 3.3 days |
| clock, 3-case split | 50 | 86 M | 99 days | 9.9 days | 24 hours |
| clock, 12-case split | 140 | 31 M | 36 days | 3.6 days | 8.5 hours |
| clock, 64-case split | 660 | 6.5 M | 7.5 days | 18 hours | 1.8 hours |
| `InternedCount` at T, fresh literals not canonicalized | 2 | T / 2 | T / 20 seconds | T / 200 seconds | T / 2,000 seconds |
| `InternedCount` at T, canonicalized literals | 0 | unbounded | | | |

So:

- For plain queries the ids bind, at about half the clock horizon.
- For any split workload the clock binds. Even a 3-case split exhausts the clock before the ids, and the gap widens
  with the number of cases.
- Epochs per case depend on the theory's schedule and rules. These figures hold for the test theory. The gauge in
  phase C reports them for any theory.
- This plan did not examine whether the 10 epochs per case could be reduced in the engine.

### 3.5 Tests and benchmarks that exist

- **Interning:** `src/Eggnog.Tests/Relations/BaseValues/BaseValuesTests.cs`, `BaseValuesPropertyTests.cs` and
  `BaseValuesConcurrencyTests.cs` cover `InternedCount`, clones and concurrent interning. The library's assertions that
  reads never intern are in the three files named in section 3.1.
- **Id blocks:** `src/Eggnog.Tests/Relations/Contexts/IdBlockAllocatorTests.cs` pins the block sizes.
  `LateParentMintBenchmarks` (`src/Eggnog.Benchmarks/Relations/Contexts`) reports the root's array growth.
- **Missing:** no test churns scopes for memory or checks retained memory.
  `src/Eggnog.Tests/Concurrency/ScopeThreadingTests.cs` churns query definitions for thread safety, not for memory. No
  test exercises exhaustion of the id counter or the clock through the library.

## 4. Value

**For long-lived services built on frozen scopes.** A host that freezes its root and schema scopes and runs each
request in a transient child is the case D15 and `InternedCount` were written for. If it reuses or canonicalizes its
literals, `InternedCount` never moves (section 3.4). The id counter or the clock then ends the graph's life:

- in 15.5 days for plain queries at 100 queries per second;
- in 10 days to 18 hours for split queries, depending on the number of cases.

The host cannot see either limit coming. When one runs out, every minting call on that graph fails with an
undocumented `OverflowException`. The scope is not faulted, so a host that watches for faulted scopes sees nothing,
and once the clock is out `CreateChild` fails too. The graph serves no further query until the host retires it.

A public usage gauge lets the host retire the graph before the limit, as D15 lets it do for `InternedCount`. That
replaces undocumented failure with documented behaviour and a usable signal. It is the largest value in this
workstream, and it costs little. The split horizons make it more urgent: at 64 cases and 100 queries per second, the
clock runs out in under a day.

Documenting the child interning paths tells hosts exactly which of their operations intern (section 3.1): `inset`
singletons, case joins and local-rule constants. The churn test turns "a disposed scope retains nothing beyond new
interning" and "reused literals intern nothing" into regression tests.

**For hosts that cannot canonicalize.** A host whose rules read the contents of strings through primitives cannot map
its literals onto a bounded set:

- Such a host interns about 2-3 values (150-230 B) per fresh-literal query.
- At 100 such queries per second with no retirement, that is about 1.3-2 GB a day.
- Retiring at T interned values, the graph is retired every T / 300 to T / 200 seconds, so the interval is set by the
  host's own threshold.

A scoped pool would remove that retained memory between retirements. It would not remove retirement, because the ids
and the clock still run out (section 3.4).

**For maintainers.** [../architecture.md](../architecture.md) section 2.2 states that the pools are permanent per
graph. The "Ownership" paragraph of section 3.8 says that a child shares base values "by reference". No document says
explicitly that values a child interns outlive it, or which child operations intern. Writing that down, and pinning it
in tests, stops it from being rediscovered. The minting-sites table in section 3.2 also shows three bounds and two
exception types for one limit. That inconsistency is a maintenance hazard in its own right.

**For performance.** Nothing in this workstream speeds up the root.

- A smaller first id block extends a graph's life by at most min(4, clock horizon / id horizon). That is about 2.1x
  for plain queries with a snapshot read, and 1x for splits whose cases do not mint.
- Recycling ids and epochs inside transient domains would remove both horizons for a host whose root and schema
  scopes are frozen (section 5, row H2).
- Once retirement on the gauge exists, a longer graph life is worth little. Retirement costs one graph construction
  (about 56 KB allocated), the host's theory definitions and its frozen scopes, once per graph life. That cost is the
  host's and is not measured here; at the horizons of section 3.4 it is spread over millions of queries.

**If it is never done.**

- The documentation gap remains, and retained memory and per-query interning have no regression test.
- A long-lived graph eventually fails every minting call with an undocumented exception, until the host notices and
  retires it.
- Without scoped interning, nothing changes for hosts that canonicalize. Hosts that cannot canonicalize keep retiring
  graphs, which is the workaround the roadmap documents.
- Without eq containers in children, five or six egglog push/pop programs stay untranslated, and no library feature is
  affected.

## 5. Cost and risk

| Part | Effort | What dominates it | Risk to correctness, and its guard | Risk to the root's performance | Maintenance |
|---|---|---|---|---|---|
| A. Document the child interning paths and the leftover state | small | writing; one engine test per statement | none | none | low |
| B. Churn and retained-memory tests | small | a non-parallel probe for heap slopes (as for the existing allocation probes, `src/Eggnog.Tests/Relations/Execution/AllocationProbeCollection.cs`) and an opt-in 100,000-query soak | none (tests only) | none | low. A heap bound can be flaky, so it is a slope with a generous bound, and the deterministic assertions carry the weight |
| C. Public usage gauge; one exhaustion bound and one exception type across the minting sites | small | the public API's shape (a `src/Eggnog.Tests/Surface/PublicSurface.txt` change), the choice of exception, and the four minting sites of section 3.2 | low. Tests advance internal counters, as the dry run did | none: the gauge reads counters on demand, and the bound checks already exist on each path (a `MaxId` check replaces a `StaleRep` check) | low |
| D. First id block of 4 (or adaptive) | small | measurement: counters, and latency on an idle machine | low. The context differential suites and `IdBlockAllocatorTests` guard it (the full suite was run with the change, see section 6) | none at the root, which mints directly. A child that mints many ids makes 1-2 more block reservations (a lock, an atomic add, a 2-element array copy), and `Contains` searches 1-2 more blocks | low |
| E. Scoped interning (transient-domain pools, section 7) | large | every encode and decode site: 38 library call sites of `Encode`/`Decode`/`EncodeObject`/`TryEncodeExisting`; 18 engine `Unwrap`/`Get`/`Format` sites; `Primitive.InvokeCore`'s signature; extraction's `BaseSort`; printing; the lift's cell comparisons; and a design for global interning while pools are live | **high**. A value with two cells in one scope breaks invariant V. Value joins then miss matches, and `NotEqualFunction`, which compares cells, derives `x != x`, which is unsound. Guards: a V checker over each scope's visible cells; the scope and split differentials run with pools on and off; targeted not-equal tests | a range test on every decode of an interned cell and a null test on every encode, gated on `RuleSetRunBenchmarks` and the bridge's `math` and `ac` workloads | high: every new encode site must be scope-aware |
| F. Eq containers in children (C13) | large | the overlay environment (`to_id`, `to_container`, `val_index` deltas); an exact parent `val_index`; child container rebuild through the child's union-find; a dirty-id refresh; removing the eq guard and the frozen-database rule; a reference model with container programs in the context differentials | high: container rebuild is egglog's own defect area (issue 831) | the root's container rebuild must keep its cost; gated on the bridge workloads | medium-high |
| G. 64-bit cells | very large | every table, index, subset, SIMD probe, and the egglog layout the port follows | high | doubles memory per cell | — (dropped) |
| H1. Recycling ids or the clock globally, in place | medium | proving that no structure still holds a disposed child's ids. Under a writable parent, a recycled low block makes parent ids lose to smaller ids in children, with copies as in L7. The global clock cannot be reset while any frozen scope's rows carry stamps | medium-high | none | medium (dropped) |
| H2. Ids and epochs local to the transient children of a frozen scope | medium to large | two parts, below | ids: medium. Clock: high, because semi-naive evaluation depends on stamps and a mistake silently loses matches. Guards: the context and split differentials and the `PredicateTheory` tests with the feature on and off, and the `ContextTables` invariant checkers | none at the root; a domain test on each clock read in a child | medium: every new clock read must go through the context |

**Row H2 in detail.** A *transient domain* is a child of a frozen scope, together with its cases and arms. `Freeze`
requires the parent to be frozen (`Scope.Freeze`, `src/Eggnog/Graph/Scope.Tree.cs`), so every ancestor of such a
domain is frozen.

- **Recycled id blocks.** Keep a free list on each frozen scope. It holds the blocks that its disposed transient
  children reserved after it froze, and a block joins the list once deferred release (D40) has run.
  - Every such block lies above every id the frozen chain holds, so smaller-id-wins still favours parent ids. This is
    the case where row H1's objection does not apply.
  - Container ids come from the shared counter, not from blocks, and children cannot intern eq containers (decision
    9). So once a child is released, nothing in the engine holds its block ids.
  - A library `ClassId` carries its scope's origin, so using a stale one in a later child is already refused
    (`src/Eggnog/Graph/ClassId.cs`). But `ClassId.Equals` compares only the cell and the graph. A `ClassId` kept from
    a disposed query would compare equal to an unrelated class of a later query that reuses the block. Hosts that key
    dictionaries by `ClassId` across requests would see that change, and it would need documenting.
- **A domain clock.** The domain stamps with a counter of its own, which starts at the store clock's value when the
  domain is created and never advances the store clock. This would redirect the clock counter in the domain's view, as
  `Database.IncrementCounterInView` redirects the id counter to blocks today.
  - Siblings never compare stamps, and nothing is caught up from a frozen parent.
  - `ContainerDirtyLog` is appended only by a writable root (its remarks), and `ContainerValues.EqVersion` is a version
    counter, not a stamp.
  - What the design must prove is the shared-clock ordering inside the domain. That is the obligation of
    [../history/colored-contexts-spec.md](../history/colored-contexts-spec.md) section 12.3: "After the descendant's
    operation the clock is at least T+1, and X's later captures are larger". It must hold between the query child and
    its cases. It must hold at each of the clock's reads, advances and counter references, about 24 lines in 13 files
    of `src/Eggnog/Relations`. They include `Context.Epochs.cs`, `EGraphContext.Writes.cs`, `Database.Contexts.cs`
    (`CurrentClock`), `ExecutionState.Database.cs`, `EGraph.Run.cs` and the invariant checkers.
  - Rules that read the clock as a value (egglog's `next_ts`) would see domain values.
- **What it removes and what it leaves.** It removes both horizons of section 3.4 for a host whose root and schema
  scopes are frozen. It leaves interning growth and theory changes, so retirement stays. Its ids half alone gains no
  more than part D, because the clock binds next. Only the two halves together remove the limits.

## 6. Worth-it evaluation

| Question | Cheapest decisive experiment | Metric and threshold | Idle machine? | Verdict now |
|---|---|---|---|---|
| Does a disposed query scope retain anything beyond new interning? | Done for this plan (section 3.4). The test in phase B repeats it | Retained heap slope per same-literal query below 16 B; rule, variant and external-function counts back to baseline. Measured: 0.6 B, and the counts returned to baseline | no | holds; write the test |
| How much does a query child allocate, and where? | Allocation counts per query shape, attributed by type | Recorded, with no gate here | no for counts | 230 KB-1.15 MB per query on the test theory; handed to [./idle-machine-measurement.md](./idle-machine-measurement.md), which owns the allocation of transient children |
| What binds a long-lived graph: interning, ids or the clock? | Done (section 3.4): ids, epochs and interned values per query shape and per number of cases | Whichever horizon is shortest at the host's rate | no | ids for plain queries, the clock for any split; build the gauge |
| Is a first block of 4 worth taking? | Done in part: 4 ids per query with allocation unchanged, and the net10.0 suite failed only the 2 tests that pin the size (below). Still to do: the net8.0 run, counting block reservations per child over the context and split differentials and `CaseSplitMemoryBenchmarks`, and the per-child latency benchmarks (`ManyChildrenBenchmarks`, `PerChildCostBenchmarks`) | Take it only if the gauge, in a host's long-lived use, shows the id counter ending graphs before the clock, on split-free workloads or on theories whose cases mint, at a retirement rate the owner considers a measurable cost. Then the gates: allocation per child within +1%, time per child within 2% | the latency part only, and only once triggered | **Later, on data**. It extends graph life by at most min(4, clock horizon / id horizon): about 2.1x for plain queries, 1x for splits whose cases do not mint |
| Are ids and epochs local to transient domains worth building (H2)? | The gauge in a host's long-lived use: how often ids or the clock force a retirement | Build only if forced retirements happen more often than an owner-set bound (for example, hourly) | no | **Later, on data**. Retirement is paid once per graph life, and at 64-case splits and 100 queries per second it would happen about once in 18 hours |
| Is scoped interning worth building? | A host's own numbers: its fresh values per request and its request rate, and the retirement interval they imply | Build only if a host that cannot canonicalize would retire more often than an owner-set bound (for example, once an hour), or would retain more than its memory budget between retirements | no | **Later**; nothing measured so far meets the trigger |
| Are eq containers in children worth building? | An owner decision: will the library offer containers of classes (sets or vectors of terms)? | Build only if the library will expose such containers, or a host needs them through the engine | no | **Later** (park); the only gain today is six conformance programs |
| 64-bit cells? | none needed | a 2x memory cost per cell everywhere, against a limit that a gauge plus retirement already handles | no | **Drop** |
| Recycling ids or the clock globally, in place (H1)? | none needed | Under a writable parent, recycled blocks break smaller-id-wins, and the global clock cannot be reset while frozen rows carry stamps. Where recycling is safe, H2 covers it | no | **Drop** |

**Full suite with a 4-id first block.** The whole suite was run once on net10.0 (Debug) in a private clone with
`IdBlockAllocator.FirstBlockSize = 4`. Of 12,447 tests, 12,435 passed, 10 were skipped (the opt-in soaks) and 2
failed. Both failures are in `src/Eggnog.Tests/Relations/Contexts/IdBlockAllocatorTests.cs`, and both write the old
size into their arithmetic:

- `BlocksStartAtSixteenAndDoubleTheReservedTotalUpToTheCap` takes the 16-id first block as given.
- `ABlockPastTheUnionFindMaximumOverflows` places the counter 20 ids below the limit and expects the second block to
  overflow. With 4-id blocks the second block still fits.

No context, colored, split or predicate-theory test failed, and no allocation probe failed. If the trigger is ever met,
the change costs two test edits plus the gates above.

## 7. Plan if it goes ahead

The project's practice applies to every phase: implement; review from several angles, including a mutation-testing
review of the new tests; fix; verify on net8.0 and net10.0.

**Phase A: document (small).** Entry: none.

- [../architecture.md](../architecture.md):
  - section 2.2: a child scope interns into the graph's pools, and the values stay after it is disposed. Add the list
    of interning paths from section 3.1 of this plan;
  - section 2.12: what a disposed scope leaves behind (the table in section 3.2);
  - section 3.8 ("Ownership", "Ids and time"): ids and epochs per child, epochs growing with cases, the exhaustion
    limits, and the exception thrown at exhaustion (as phase C settles it).
- XML remarks:
  - `ValueStore.InternedCount` and `Scope.Dispose`: values interned in a scope outlive it;
  - `EGraph.DefineScopeTag`: tags and mirrors are graph definitions; do not define one per request;
  - `Scope.DefineRule` and `EGraph.DefineQuery`: constants intern at definition.
- [../roadmap.md](../roadmap.md): replace the "values that die with a scope" bullet with a link to this plan, and add
  the gauge.
- An engine test, `src/Eggnog.Tests/Relations/Contexts/ChildInterningTests.cs`, pins each statement:
  - A value interned in a child has the same cell at the root and in a sibling.
  - After the child is disposed, the value is still decodable and counted once.
  - A base-value container interned from a child stays in the shared environment and takes its id from the shared
    counter, not from a block.
- Exit: the documents are merged, and the test passes on both frameworks.

**Phase B: churn and retained-memory tests (small).** Entry: none; may run in parallel with A.

- The churn shape is a request on a frozen schema scope: `CreateChild`, `Make`, `Assume`, `Run`, then the snapshot
  read (`Check`, `TryGet`, one `ForEachMatch` over a reusable query), then `Dispose`. A split variant runs the theory's
  `Saturation` schedule.
- `src/Eggnog.Tests/Scopes/ScopeChurnTests.cs` (deterministic), on `PredicateTheory` (`src/Eggnog.Tests/Predicates/`):
  - `InternedCount` is unchanged across a second pass of same-literal queries;
  - it rises by exactly the known count for each fresh-literal shape (2 for `StrEq`);
  - the snapshot read interns nothing and reserves no ids;
  - after local-rule churn, the rule table's count, the external-function registry's count and
    `VariantsByDescription` are back to their baselines;
  - the frozen schema has no children left;
  - a `WeakReference` to a disposed query's engine context is collected after a full GC;
  - the store's id counter rises by exactly one first block per single-mint query;
  - the clock rises by the same count per query on each pass, and by a fixed amount per case on the split variant.
    This pins the 20 + 10 x *cases* shape, so that a change to it is noticed.
- A retained-heap probe in a non-parallel collection. It reads `GC.GetTotalMemory(true)` at two points, after 5,000
  and after 10,000 measured queries (after a warm-up), and asserts on the slope between them:
  - same-literal queries retain less than 16 B per query;
  - fresh-literal queries retain at most 128 B per newly interned value.

  The slope excludes one-off growth from caches and tiering. The `WeakReference` and count assertions remain the
  deterministic guard.
- An opt-in 100,000-query soak behind an environment variable, long enough to show a small per-query retention that
  10,000 queries would hide in the heap's noise.
- Mutation review: the tests must fail in each of these cases:
  - `RuleVariants.FreeVariant` stops removing `VariantsByDescription` entries;
  - `FreeExternalFunction` is skipped;
  - deferred release never runs;
  - a host read interns;
  - a child interns into a private registry instead of the shared one.
- Exit: the tests pass on both frameworks, and each mutant that survives the review is killed or explained.

**Phase C: usage gauge and exhaustion behaviour (small).** Entry: the owner agrees the API's shape (section 9).

- A read-only snapshot on `EGraph`, for example `EGraph.Usage` returning a `GraphUsage` record struct. It reports
  `InternedValues`, `IdsReserved`, `IdLimit`, `Epochs` and `EpochLimit`, plus the largest fraction of a limit used.
  The type gets its own file, and `src/Eggnog.Tests/Surface/PublicSurface.txt` is updated.
- One bound and one exception for every minting path in section 3.2's minting-sites table: `Context.NewId`,
  `IdBlockAllocator`, `ExecutionState.ValueFromCounter` on the root path, `Relations.EGraph.FreshId` and
  `ContainerEnv.MintId`.
  - The bound is `UnionFind.MaxId`. A root rule mint then fails at the mint, not later in `UnionFind.Grow`.
  - The exception type is one documented type. Either keep `OverflowException` and document it on `Make`, `Write`,
    `Run` and `CreateChild`, or wrap it in a documented library exception.
  - The clock already has one bound, `Store.CheckEpoch`. Document it in the same way.
  - Say in the documentation that exhaustion does not fault the scope, and that once the clock is out the graph can
    create no more children.
- Tests:
  - the gauge moves as phase B's counts say;
  - an internal hook, as in the dry run, advances each counter to just below its limit, and the documented exception
    and state follow on every minting path, including a root rule mint and a container mint.
- Exit: merged on both frameworks. The gauge's documentation says that a long-lived host should retire its graph on
  the gauge's largest fraction as well as on `InternedCount`, and that split workloads reach the clock's limit first.

**Phase D (Later, on data): first id block.** Trigger: section 6's row on the first block.

- Change `IdBlockAllocator.FirstBlockSize` from 16 to 4, or size a child's first block from its parent's history.
- Run the full suite on both frameworks, and update the two `IdBlockAllocatorTests` that pin the size.
- Count block reservations per child over the context and split differentials.
- Re-read the `LateParentMintBenchmarks` table, and record the new figures in the engine docs.
- Run the per-child latency gate on an idle machine ([./idle-machine-measurement.md](./idle-machine-measurement.md)).

**Phase H2 (Later, on data): ids and epochs local to transient domains.** Trigger: section 6's row on H2. Build it in
two steps, recycled id blocks first and the domain clock second. The design and its decision record go in
[../engine/decisions.md](../engine/decisions.md). Oracles:

- the context and split differentials and the `PredicateTheory` tests, run with the feature on and off;
- the `ContextTables` invariant checkers, extended to assert that epochs are monotone within each domain;
- a test that a `ClassId` kept from a disposed query is refused in a later one that reuses its block.

**Phase E (Later, only on its trigger): scoped interning.** If it is ever built, use the narrowest design that keeps V:

- **A pool per transient domain.** A pool belongs to a scope whose parent is frozen, and its descendants (cases and
  arms) share it. A lift from a case to its parent then compares cells within one pool, and nothing a pool holds is
  visible above it. Writable chains keep the global pool, because catch-up from a writable ancestor could bring global
  cells for values the child interned locally. This is the same domain as H2's. If both are ever built, build them on
  one notion of a domain.
- **Disjoint cell ranges.** Local cells use a range that global cells never reach. For codecs that never box inline,
  that is the upper half of the cell space, which is unused today because indexes stay below `Array.MaxLength`. For
  `Int64Codec`, it is a split of the tagged index space. Ranges are recycled when a pool is disposed.
- **Lookup order.** A pool looks up its own values first, then the global pool.
- **Global interning while pools are live.** This is the hard case, and it needs a decision. It arises when a value is
  interned globally while a live pool already holds it, for example the constant of a global rule defined while
  queries are in flight. The options are to refuse such a definition, or to make global interning consult live pools.
- **Oracles.**
  - A V checker: in every scope, no two visible cells of a type decode to equal values.
  - The scope and split differentials, run with pools on and off.
  - Not-equal soundness tests.
  - A root gate: `RuleSetRunBenchmarks` and the bridge's `math` and `ac` workloads within 2%.
- The design and its decision record go in [../engine/decisions.md](../engine/decisions.md).

**Phase F (Later, only on its trigger): eq containers in children.**

- Build the overlay environment of the engine map's C13.
- Run the context differentials with containers against a reference model.
- Translate `container-rebuild.egg`, `container-fail.egg`, `lambda.egg`, `typeinfer.egg` and `fusion.egg`.
- Retire `EqContainerGuard` and decision 9.

## 8. Dependencies and ordering

- **Phases A, B and C** depend on nothing and can start now. No other workstream waits on them.
- **Allocation per query child** goes to [./idle-machine-measurement.md](./idle-machine-measurement.md), which owns the
  transient-child figures.
- **Phase D**, if triggered, needs an idle machine for its latency gate. It also touches the same id machinery as two
  items in [./colored-context-deferred-features.md](./colored-context-deferred-features.md): the ranking that lets
  parent-visible ids win (G2), and shared sibling ids (G9). Whichever lands second re-reads the L7 figures. H2's
  recycled blocks interact with G9 in the same way.
- **Eq containers in children** also appear in that plan's "left open by design" list. The two plans should agree.
  This plan recommends parking the feature until the library offers containers of classes.
- **Scoped interning** (phase E) changes `Encode`, `Decode` and `Primitive.InvokeCore`, which compiled plans
  ([./compiled-plans.md](./compiled-plans.md)) would call or inline. If both are ever built, design them together.
- [./idle-machine-measurement.md](./idle-machine-measurement.md): phase B's churn harness can be reused for its
  transient-child latency figures (its Q8), and its 100-sibling release test (Q11) sits beside `ScopeChurnTests`.
- [./parallelism-test-plan.md](./parallelism-test-plan.md): its stress tests of concurrent children can run phase B's
  churn on several threads under one frozen schema. That also exercises concurrent interning into the shared pool.
- [./conformance-docs-release.md](./conformance-docs-release.md): phase A's documentation, and the push/pop
  conformance list in the roadmap, which should say that these programs are parked with eq containers in children.
- [./smaller-engine-items.md](./smaller-engine-items.md): the minting-bound consistency fix of phase C could go there
  instead. It should not be split from the gauge's documentation of the exception.

## 9. Questions for the owner

1. **The gauge's public shape.** Is a public usage snapshot on `EGraph` acceptable, for example `EGraph.Usage` and a
   `GraphUsage` record struct? At exhaustion, should the library keep a documented `OverflowException` that leaves the
   scope unfaulted, or throw a documented library exception? Either way, should one bound (`UnionFind.MaxId`) and one
   exception type hold across all four minting paths?
2. **The first id block.** Do you agree to leave it at 16 until the gauge shows the id counter ending graphs before the
   clock, at a retirement rate you consider costly?
3. **Containers of classes.** Will the library ever offer containers of classes (sets or vectors of terms)? If not,
   should eq containers in children, and the push/pop programs that wait on them, be dropped rather than left open?
4. **The trigger for scoped interning.** What retirement interval, or what retained memory between retirements, would
   justify scoped interning for a host that cannot canonicalize its literals?
5. **Retiring graphs.** Should the library provide a helper that rebuilds a graph from a definition delegate when the
   gauge passes a threshold? Or should it only document the pattern: retire on the gauge, rebuild from the host's own
   definitions?
6. **The 100,000-query churn.** Should it run in the default suite, or as an opt-in soak, with a shorter churn by
   default?
7. **The `u32` decision.** The engine map's section 8.2 asked for this decision to be made "early". Do you agree to
   record it in [../engine/decisions.md](../engine/decisions.md) as follows?
   - 64-bit cells: dropped.
   - Global recycling of ids or the clock in place: dropped.
   - Ids and epochs local to the transient children of a frozen scope: possible, and deferred. The reason is that a
     host can retire a graph on the gauge, once per graph life. The trigger is forced retirements more often than a
     bound you set.

## Review

A skeptical review of the first version raised nine problems. Each was checked against the code, the documents and,
where it rested on a count, a second dry run.

**Changed:**

1. **Split horizons.** The second dry run reproduced the review's figures exactly: 20 + 10 x *cases* epochs per split
   query, with ids unchanged. It extended them to 6, 24, 48 and 64 cases. Section 3.4 now reports epochs as a function
   of cases and adds rows for 12-case and 64-case splits (18 hours at 100 per second). It states that the clock binds
   for any split. Sections 1 and 4 and the gauge's urgency now reflect this.
2. **First id block.** Phase D moved from "likely go" to **Later, on data**, and its idle-machine gate is deferred
   with it. Its gain is now stated as min(4, clock horizon / id horizon). That is 2.1x for plain queries, not the
   review's 2.3x, because the snapshot read adds an epoch, so the plain clock horizon is 33 days, not 35. It is 1x for
   splits whose cases do not mint. The plan also notes the case where ids do bind: theories whose cases mint.
3. **Allocation per query.** The plan now says that only retained memory stays flat. Allocation per query
   (230 KB-1.15 MB on the test theory) is recorded and handed to the idle-machine-measurement plan rather than
   rule-layer-split-features, as the review suggested, because that plan owns the transient-child figures and already
   has an allocation finding on transient children.
4. **Recycling.** Row H became H1 (global recycling in place: Drop) and H2 (ids and epochs local to transient domains
   under a frozen scope). H2 is evaluated on its merits in section 5 and rated **Later, on data**. The evaluation adds
   one consequence the review did not raise: `ClassId` equality across recycled blocks. Owner question 7 now asks to
   record that reasoning.
5. **Retirement signals.** The plan now explains why `InternedCount`, the only usage figure exposed today, cannot see
   exhaustion coming, and why an unfaulted scope gives a host no other signal. The gauge's value is restated as
   retiring before the limit and documenting the behaviour.
6. **Number encodings.** The claim about small numbers is now limited to the test theory's `long` encoding. An
   encoding that stores numbers or bounds as interned values interns each fresh one.
7. **Minting bounds.** Section 3.2 now lists the four minting sites with their bounds (`MaxId`, below `StaleRep`, up to
   and including `StaleRep`) and exceptions (`OverflowException`, `InvalidOperationException`, and a later
   `ArgumentOutOfRangeException` from `UnionFind.Grow`). Each was verified in the code. Phase C covers all of them.
8. **Citations.**
   - D15 is cited from section 1 of the rule-layer spec, and section 6.1 by its real title.
   - The surface file is cited by its full path.
   - The maintainers' sentence now credits architecture.md section 2.2.
9. **Snapshot read and the heap slope.** The second dry run added the snapshot read: 1 epoch, 0 ids, 0 interned. Phase
   B's churn shape includes it, and phase B's heap assertion is a slope between two points.

**Rejected:** none of the nine problems was rejected outright. Two of the review's proposals were adjusted:

- The proposed handoff target for the allocation figures was changed, for the reason given in item 3.
- The proposed summary sentence on retirement signals ("The only trigger that can see exhaustion coming is none") was
  reworded with the same content.
