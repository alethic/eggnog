# Roadmap

What is open. The library's model and the engine are described in [`architecture.md`](architecture.md); the reasoning
behind the items below, where it is recorded, is in [`history/`](history/README.md).

Each open item says what it is, why it matters, and the figure and the gate or trigger that decide it. Timed gates use
the campaign method of the benchmark README ([`Eggnog.Benchmarks`](../src/Eggnog.Benchmarks/README.md), "Comparing two
builds"). Items that wait on a trigger are under "Parked", and items considered and not planned are under "Closed",
each with what would reopen it.

## Fidelity with egglog

Fidelity with upstream egglog comes first. A way an egglog feature behaves differently here is a defect to remove,
unless .NET makes it unavoidable, and then it is recorded as such; an addition (scopes, case splits, host queries,
partial and variadic user primitives) may restrict only itself, never an egglog feature, and takes no name or form egglog
gives a meaning. The root, and any scope without live descendants, behaves as egglog does; restrictions apply only in
child scopes and splits, where soundness needs them. Each item lands when egglog's own tests of the feature, ported to
the C# API, pass; an item that touches the engine's root paths also passes the root gate ("Benchmarks and timed
gates").

- **Table reads in rule heads.** egglog 3.0.0 allows lookups in the heads of `:naive` and `:unsafe-seminaive` rules; here
  heads never read but in a full rule callback, egglog's `rust_rule_full` (architecture 2.13, item 2). Such a rule
  inherits no progress in a child.
- **Function values.** egglog's `unstable-fn` and `unstable-app` (src/sort/fn.rs), a container of a function and
  its partial arguments, and the higher-order container primitives over them, such as `unstable-multiset-map`.
- **Containers of classes in children.** egglog makes an eq-sort container in any command; here only a writable root
  makes one, and a child uses those the root holds (decisions row 9), since a child would have to rebuild it in its
  layers.
- **egglog 3.0.0.** The engine follows `5e655cae` until the behaviour items of
  [`egglog-3.0.0-todo.md`](egglog-3.0.0-todo.md) are followed: join-key order, container term forms, escaped string
  literals, duplicate rule names, the thread count per graph.
- **A `.egg` front end.** egglog's language: parser, typechecking, desugaring and commands, `push` and `pop` as child
  scopes with their definitions scoped too. It runs egglog's own test files, the fidelity suite for every item above,
  and the benchmark programs that need it (herbie, eggcc, `math.egg`, the transformer programs).
- **Proofs.** As in Parked, when upstream's proof API settles in a release.

Kept, not departures to remove: egglog defects the port fixes (decisions rows 6 and 7, and the defect list of
[`history/egglog-engine-map.md`](history/egglog-engine-map.md)), which change match counts and plans but not results;
relations as unit-valued functions where egglog makes them constructors over a sort of their own (architecture 2.13,
item 5), observable only in serialization, which emulates egglog's form; and the `BigInt` shift and the `BigRat`
`pow` past `int.MaxValue` bits, which no .NET `BigInteger` holds.

## Engine work

- **Kernel specialization.** Closed-world C# fast paths for a bounded set of plan shapes, chosen by the interpreter's
  switches ([decisions](engine/decisions.md), row 8), for speed. Built only if a prototype of a target's three hottest
  plans gains 10% or more scaled (gain over their share of run time) and 20 shapes or fewer cover 90%.
- **The specialization target.** Kernel specialization's target is fixed before its prototype, from per-plan ETW
  profiles taken once the staging-buffer share is settled: the workload where the join kernels and `Replay` hold the
  largest share, for root throughput, or the `PredicateTheory` query-child loop at p50 and p99, for query latency.
- **Landing the fast paths.** Kernel specialization's fast paths land only if they measure at least 10% faster on its
  target with the root set within 2%; otherwise the interpreter stays the only executor.
- **Parity with the Rust.** The bridge examples and the library port of `math-microbenchmark.egg` against `egglog -j1`
  and `-jN`: 1.10 or less is parity, 1.10 to 2.0 is recorded, above 2.0 is investigated. The last timing, before the
  store-backed root, had `math` at about 6.6 s against 7.4 s and `ac` at 15.5-16 s on both.
- **Parity of writes.** `WritesBenchmarks` against `cargo bench`: the gap closes at 1.10 or less at every thread count,
  and above 1.10 at 8 threads with equal shard counts an ETW profile of the merge follows. The `Shards` option stays if
  it moves the 2- or 4-thread ratio by more than 5 points.
- **The root `BasicMath` regression.** `RuleSetRunBenchmarks.BasicMath` is slower than at `3a6e0b7`, before colored
  contexts, with allocation unchanged: 21-29% at 20,000 rows serial, 11-13% parallel, 10-15% at 200,000 rows serial and
  6-11% parallel, from a shared machine; the campaign's root comparison replaces these figures.
- **The `BasicMath` port.** `campaign/basicmath-port` lands if, in each configuration whose loss exceeds twice the A/A
  spread, it recovers at least half of it, with nothing else more than 2% worse (child benchmarks included) and `math`
  and `ac` no worse; a gain of 2% or more on `math` or `ac` passes on its own.
- **Dropping the port.** `campaign/basicmath-port` is dropped if the `BasicMath` loss is under 3%, or under twice the
  A/A spread, in every configuration: no loss the campaign can measure is then left for it to recover.
- **The prober's size.** Layering grew `Prober` from 48 B to 72 B, a possible part of the `BasicMath` loss. A run on
  `3a6e0b7` padded to 72 B goes first; only if padding slows `BasicMath` by more than the A/A spread does shrinking the
  prober follow.
- **Shrinking the prober.** Moving `ProberPiece.View` and `Prober.Rest` out of line, back to 48 B, touches the kernels
  `campaign/basicmath-port` changes. It lands only if it recovers at least 3%, and more than twice the A/A spread, on
  `BasicMath` in the parallel configurations.
- **Idle local rules.** The idle-rule allocation commit was timed against `main` on a GitHub runner ([The idle-rule
  comparison](../src/Eggnog.Benchmarks/README.md#the-idle-rule-comparison)): queries with idle local rules ran 14-40%
  faster, and the root rule runs within about 1%. `BridgeMathBenchmarks` was left unsettled by the runner's noise and is
  rechecked against the commit's parent at the next idle machine or CI run. The cache commit on `idle-rule-cache` takes
  a query child under a frozen container with 100 idle local rules from about 53 KB to about 32 KB (Debug, net10.0), and
  is decided with the query-child figures or with "Demand-indexed local rules". Departures from egglog:
  [decisions](engine/decisions.md), row 19.
- **Demand-indexed local rules.** After the idle-rule allocation commit, a query child under a frozen scope is timed
  at 0, 100 and 1,000 local rules. They are built only if 100 idle local rules add more than 25% to a query's own time
  at 0 rules, at p50 or p99.
- **Write allocation.** Pooled staging arrays, a growth hint and a sampled index reserve on `campaign/write-alloc` cut
  `ParallelInsert`'s bytes by 32-44%, and the bridge `math` and `ac` by 12% and 8% ([decisions](engine/decisions.md),
  row 19).
- **The write-allocation gate.** The `campaign/write-alloc` fixes land if 8 threads run at least 3% faster under either
  collector, by more than twice the A/A spread, with no configuration more than 2% slower. If the time is flat, only the
  parts that cost no time land, as an allocation win. On GitHub runners ([The write-allocation
  comparison](../src/Eggnog.Benchmarks/README.md#the-write-allocation-comparison)) the writes ran 12-15% faster under
  both collectors, and `ActionLookupBenchmarks` in parallel at 200,000 rows 4-10% slower under the workstation one.
- **Staging buffers.** The clone counters show no buffer-map growth on `BasicMath`, and 782 serial clones at 128 B do
  not explain the 13% `ExecutionState` share of its sampled bytes (about 100 KB, not 2.4 MB). The item closes if a
  re-taken EventPipe profile shows nothing else under `ExecutionState.CreateBuffer`.
- **The `CreateBuffer` profile.** If the re-taken profile shows more under `CreateBuffer`, an ETW profile decides: below
  5% inclusive the staging-buffer item closes; at 5% or more, per-thread reuse of the batch state is prototyped and kept
  only if `BasicMath` serial gains at least 3% and more than twice the A/A spread.
- **Epochs per split case.** Each case of a split advances the clock nine times (21 + 9 x cases epochs per split query,
  pinned by `ScopeChurnTests`). At 100 queries a second with 64 cases the clock ends a graph in under a day. The work is
  to find which of the nine advances a case needs.
- **Parallelism soaks.** One-hour `EGGNOG_CONCURRENT_SOAK` runs on x64 (net8.0, net10.0) and on an arm64 CI runner,
  which the owner runs by hand; the item closes when they come back clean. They widen the concurrent-children check to
  fresh seeds; the targeted tests pin the known races, and nothing waits on the soaks.
- **Cancelling split-step lifts.** A cancellation token checked between lift rounds, so a run overshoots `TimeLimit` by
  at most one iteration or one lift round. Worth doing if one lift's p99, from the library-path timings, exceeds 10% of
  a typical `TimeLimit`.

## Benchmarks and timed gates

Everything below needs an idle machine, unless an item says otherwise. The campaign runs on two quiet nights that the
owner schedules, and the benchmark README lays out what each night times and what moves to a later window.
`Eggnog.Benchmarks --compare` applies its rules: a 2% gate on cases of 100 µs or more; a round whose same-build runs
differ by more than 1%, or whose spread is unknown, is dropped; a case is decided if at least two rounds remain and all
of them fall on the same side of the gate; otherwise it is inconclusive, up to four rounds, then reported and ungated.

- **Root gates.** A change to the engine's root paths is gated as a candidate: the candidate on `main` against `main`,
  within 2%, on the root benchmark set (benchmark README, "The root benchmark set") or on the narrower set its own item
  names. The two campaign candidates (`campaign/basicmath-port` and `campaign/write-alloc`), and any other change to the
  root paths, land only after their gate. The root set against `3a6e0b7`, from before colored contexts, is the
  cumulative comparison only. It gates no candidate: `main` is already slower than `3a6e0b7` on `BasicMath` ("The root
  `BasicMath` regression"), so every candidate would fail it.
- **The cumulative comparison.** One comparison of `3a6e0b7` against `main` replaces the per-stage gates of the
  colored-contexts work, and it also times the bound check on root id mints. It records where `main` stands. A
  confirmed failure becomes a roadmap item; it is bisected over the engine-changing commits only if the owner
  schedules it.
- **The bridge on a store-backed root.** `math` and `ac` within 2% of `2fd4a18`; allocation-only dry runs moved little
  (`math` 9.80 GB to 9.61 GB, `ac` 15.47 GB to 15.57 GB). The run at `673495d` against `main` is a record, not a gate.
- **Extraction through the context API.** `ContextExtractionBenchmarks`' `RootContext` within 2% of its `RootApi` at
  `main`, a ratio within one build.
- **`ActionBuffer.Push`.** The root gate decides whether it moves to the span form of `Bindings.Push`, which would let
  the array form be deleted; otherwise the array form stays.
- **Per-child cost.** Bytes per child are flat as the root grows from 20,000 to 2,000,000 rows (about 96 KB for 100
  unions and a rebuild, 145 KB with a flush and a rule run); time per child must stay flat within 1.5x, or children cost
  more on large graphs.
- **Case splits against clones.** Time for case splits against full clones, reported beside the byte figures, so that
  colored contexts are shown to beat clones in time as well as in bytes.
- **Memory per structure.** Per-child bytes split by structure (layer tables, hidden sets, overlay, cursors, watermarks,
  indexes) from a `dotnet-gcdump` snapshot of 1,000 live children, whose total must agree with `--context-footprint`
  within 5%. It needs no idle machine.
- **Library paths.** `QueryScopeBenchmarks`, `LibraryRunOverheadBenchmarks` and `BusyParentCatchUpBenchmarks` check
  their results and wait for the campaign's times. Catch-up is at most 9.5% of a split step's rows at 8 cases, under the
  25% threshold, so only its time per examined row is left.
- **Library run overhead.** The library adds about 320 B per rule plus about 1 KB per run over the engine. A library
  `Run` must stay within 1.05x of the engine's `RunRules` at 10 rules, or its per-run bookkeeping is trimmed.

## Library work

- **Scopes managed by the garbage collector.** A child scope that is never disposed is never collected: it keeps 3-30
  KB, its ancestors' live-descendant counts and its local rules. An internal core per scope with a private sentinel
  makes `Dispose` optional ([decisions](engine/decisions.md), row 18).
- **The scope core's gate.** The scope core lands if the full suite passes on both frameworks in Release, tiered
  compilation on and off, with a collection forced at every run observer, split step and `CreateChild`; no sentinel
  finalizer runs for a disposed scope, a bare child costs at most 5% more, and a dropped one retains at most 64 B.
- **Queries managed by the garbage collector.** A query that is never disposed keeps about 6.1 KB and an engine rule for
  the graph's life, and its name blocks redefinition. After the scope core, each query gets a sentinel too, and its name
  becomes a label that need not be unique.
- **The query gate.** Queries managed by the garbage collector land if 1,000 dropped queries that ran retain at most
  64 B each, and a disposed query runs no sentinel finalizer.
- **The hidden-discriminator note.** A subsection of [`architecture.md`](architecture.md) section 2.9, which no living
  doc covers: synthesizing a hidden discriminator key with guarded branch rules, the `governs(k, p)` remedy for split
  demand, the cost in cases, and `SplitExtractionTests.ASplitOnAHiddenDiscriminatorJoinsTheBranchDomains`.
- **Sound numeric bounds.** The hidden-discriminator note also says that a comparison that first converts a stored
  value proves no type, and that a bound that steps by one is sound only over integers. Both are soundness traps that
  only history documents record.
- **`PredicateTheory`'s limits.** The test fixture's class remarks are to state its three limits: comparisons between
  numbers only, stepping by one over `long`, and domains of strings only (the last limits what a theory can state).
  Today only history documents record them.
- **The `RunOptions` remarks.** `TimeLimit` and `RowLimit` say a run overshoots by at most one iteration; it is one
  iteration or one lift, as the class summary says.

## Parked

Each waits on its trigger.

- **Proofs** as upstream egglog builds them (`EGraph::new_with_proofs` and its term encoding): when upstream egglog's
  proof API settles and ships in a release ([decisions](engine/decisions.md), row 17).
- **Compaction above the highest live descendant cursor:** when a workload holds a child of a large writable scope, or
  splits at one, once the `BasicMath` regression is understood. A held child on `math` leaves 2.41 physical rows per
  live row (1.25 with none). Built if a held child is 10% or more slower than none and compaction brings it within 3%.
- **The hidden-set remap:** when a split workload shows tombstones below the cases' cursors. On it, the tombstones below
  the highest live cursor and the row-buffer bytes are measured at the run's peak; the remap is built only if those
  tombstones are at least 25% of the peak's and hold at least 100 MB of row buffers.
- **A memory budget per run** that disposes settled split records first: when a user's split runs fail on memory before
  `RowLimit` or `CaseLimit` stops them.
- **Running a split's cases in parallel**, which needs a way to thaw a sealed parent before the lift: when in-case work
  is 70% or more of a split run on a user's workload. Parallel cases, or ids shared across sibling cases, then join the
  `EGGNOG_CONCURRENT_SOAK` soaks.
- **Failures of case splits under a timeout policy**, reported against full clones: when a deep-split workload that can
  time out exists, such as a TheSy-style suite. No current workload can time out, so the count would be zero.
- **A first id block of 4** (`IdBlockAllocator.FirstBlockSize` is 16): when the usage gauge shows ids ending a host's
  graphs before the clock, at a costly retirement rate. It extends a graph's life by about 2.1x for plain queries, and
  lands with allocation per child within +1% and time per child within 2%.
- **Ids and epochs local to the transient children of a frozen scope:** when a host is forced to retire graphs more
  often than it can afford ([decisions](engine/decisions.md), row 14). Only both halves together lift the limits; the
  ids half alone gains no more than a first id block of 4.
- **Scoped interning:** when a host that cannot canonicalize literals would retire graphs more often than it can afford,
  or exceed its memory budget between retirements: 2-3 values (150-230 B) per fresh-literal query, 1.3-2 GB a day at 100
  queries a second. Gate: `RuleSetRunBenchmarks` and bridge `math` and `ac` within 2%.
- **A per-case budget across executions, and a count of truncated cases:** when `Split(rule, Repeat(n, steps))`, which
  bounds each execution of a case, is not enough for a user, or a user must tell from the report that a case was cut
  off.
- **Public lift-memo figures** (`Memoized` on `LiftReport`, a `LiftsMemoized` beside `RunReport.Lifts`): when a user
  asks. `RunReport.Lifts` keeps counting every lift, memo answers included.
- **The eager parallel switch:** when a change rewrites a parallel kernel and the new kernel lacks targeted tests above
  its threshold, or a seeded mutation is caught only in eager mode.
- **A random-program harness comparing concurrent children with serial replays:** when a seeded mutation escapes both
  the targeted tests and the soaks.
- **The NativeAOT smoke application:** when NativeAOT becomes a supported target whose runtime output is tested, beyond
  the compatibility the CI publish check enforces ([decisions](engine/decisions.md), row 16).
- **The deferred benchmark classes:** `ScopeCatchUp`, `MirrorSync`, `EqualStep` and `SplitStep` when a change touches
  those paths; `TheoryDefinition` when a host's graph rebuild needs a figure.

## Closed

Considered and not planned. Each line says what would reopen it.

- Compiling plans at run time (expression trees, emitted IL, generic structs composed by reflection): if kernel
  specialization goes ahead and its shapes prove open-ended.
- 64-bit cells, and recycling ids or the clock globally in place: if a host cannot retire its graph on the gauge.
- Timing the cost of building an `EGraph` against `2fd4a18`: closed by allocation (6,193 B for the engine's graph and
  56,000 B for the library's, both under one child's 96 KB); reopen if construction allocates more than a child.
- A `Max` or `Min` lattice that takes an `IComparer<T>`: if a user needs an order other than ordinal.
- A separate serial-against-parallel suite for lattice merges that change values: if child tables ever take the parallel
  merge.
- A one-thread eager mode: if an eager failure at N threads does not reproduce at a fixed seed or does not shrink below
  10 steps.
- A systematic concurrency tester (for example Coyote): if its support for `Parallel.For` and `Monitor` is established
  and a concurrency defect escapes the tests and soaks.
- Provenance as a library feature (use the first matching step and the observer diff, with re-running without the
  ruleset as the decisive test): for a user debugging a named theory whom these routes cannot help.
- Case-only tags (`T.In(caseTag)`): for a theory that needs a nested split or an in-case rule to read an outer case's
  view.
- Counting depth-limited splits in `RunReport.SplitsSkipped`: for a user who tunes `SplitDepthLimit` and needs a signal.
- A guarded lift as a feature: for a user who reasons over if-then-else guards in volume; the guidance is in
  [`architecture.md`](architecture.md), section 2.9.
- Per-lift reporting for split steps: if a consumer needs exact per-lift figures.
- Pairing a catch-up failure with the run's own in `RunRulesCore`: if an engine caller outside the library appears.
- Lazy `IndexCatalog` objects: if never-probed catalogs exceed 5% of a lifted case's bytes and the root gate stays
  within 2%.
- `ComputeCostsFromRootSorts` and `FunctionToDag` over a context: when a scope-level "print a table as terms" API is
  scheduled.
- A remap of descendants' cursors: only as part of the hidden-set remap.
- Parent-visible ids winning over smaller ids: if a user lifts terms in volume.
- Pruning child rows an ancestor made redundant: if such rows are 25% or more of case bytes and cases live long.
- Flattening hidden sets: if a child hides more than 25% of an ancestor layer of 10,000 rows or more.
- Shared ids for a term that sibling cases derive: if the term lift is used in volume.
- Deduplicating driver keys across layer pieces: if such keys are 20% or more of child frames on a workload whose
  children are not small.
- Per-context replanning, freeze planning control and timing sibling scopes' plans: no plan depends on freeze order, and
  a plan per scope for every rule retains 2.7-3.2 KB. Reopen if a statistics-dependent planning strategy is adopted.
- Sizing a clone's buffer map up front: if `--executor-counts` shows buffer-map growth on some workload.
