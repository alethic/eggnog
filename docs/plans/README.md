# Plans for the open roadmap

These plans evaluate everything still open in [the roadmap](../roadmap.md), one workstream per file. Each plan states
the current state from the code, the value, the cost, the experiment that decides it, and a recommendation. This page
is the synthesis: what to do, in what order, what to drop, the gates the owner will be asked to pass, and the owner's
questions without duplicates.

The owner's two questions are answered in full in their plans:

- **Interning in colored contexts** ([interning-and-value-lifetime](./interning-and-value-lifetime.md), sections 3.1
  and 3.4). A child interns base values and containers into the graph's shared pools, and nothing is removed when the
  scope is disposed. Two dry runs of query-scope churn on the test fixture `PredicateTheory` show that this is not
  what limits a long-lived graph. Canonicalized literals intern nothing per query, and a disposed query retains 0.6 B.
  The store's id counter and clock are what run out: 16 ids per query that mints, and 20 + 10 x *cases* epochs per
  split query. At an illustrative 100 queries per second, split queries at 64 cases exhaust the clock in about 18
  hours. Exhaustion then throws an undocumented `OverflowException`. The plan proposes a usage gauge and one
  documented bound now, and scoped interning only on a named trigger.
- **Compiled plans** ([compiled-plans](./compiled-plans.md), sections 2 and 4). The build order put them after the
  bridge and the parallelism work. The work then went to colored contexts and the rule layer. Meanwhile the
  interpreter beat or matched the Rust on `math` and `ac`. The record holds no decision to drop them, and no
  measurement of what they would save. On those two examples, each instruction dispatch serves 127-128 lanes.
  Expression trees, `DynamicMethod` with hand-emitted IL and composed generic structs are compared on equal terms in
  that plan's table. Hand-emitted IL is the most expressive of the three and removes the duplicate array overloads.
  But it brings unverifiable IL, a second executor and the loss of NativeAOT and ReadyToRun. Closed-world C# fast
  paths reach the same ceiling without those costs. The recommendation is to build none of the three, to delete the
  array overloads that nothing calls, and to keep kernel specialization open as closed-world C#, decided by one
  ceiling prototype.

## 1. Summary

| Workstream | Recommendation | Size | Confidence | Waits on |
|---|---|---|---|---|
| [Interning and value lifetime](./interning-and-value-lifetime.md) | **Do now** (small part): document child interning, add churn and retained-memory tests, add a public usage gauge with one exhaustion bound and one exception type. Later on triggers: a 4-id first block, domain-local ids and clock, scoped interning, eq containers in children | Small now; later parts small to large | Medium-high | The gauge's public shape and exception (question 10) |
| [Compiled plans](./compiled-plans.md) | **Drop** dynamic code (all three techniques). Do the cleanup now: retire the expression-tree convention, delete eight uncalled array overloads. Kernel specialization moves to performance parity as closed-world C# | Small cleanup; medium per shape group if Q3 says go | High (about 85%) on the drop; even on kernel headroom | Owner acceptance of the drop (question 7); Q3 for kernels |
| [Performance parity](./performance-parity.md) | **Do now**: the idle-local-rule allocation fix and clone counters. **After the campaign**: the `BasicMath` port, write-allocation fixes, the writes gap, parity against the Rust at all processors and at one thread. Drop the herbie and eggcc ports | Small now (about two days); medium per item that passes | Medium | Whether parity is still a goal in itself (question 1); the campaign |
| [Idle-machine measurement](./idle-machine-measurement.md) | **Do now**: the comparison method, the library benchmark classes, the 100-sibling release test, the catch-up counters. Then one campaign of two nights | Medium, plus two unattended nights | Medium | An idle window (question 17); whether confirmed root failures will be scheduled (question 6) |
| [Parallelism test plan](./parallelism-test-plan.md) | **Do now** (small slice): qualify decision 4, add a fresh-seed soak, record the mutation recipe. No eager switch now | Small, no production code | Medium-high | Decision 4 wording (question 9) |
| [Colored-context deferred features](./colored-context-deferred-features.md) | **Do after an experiment**: suffix compaction above the highest live cursor (A0), only if the pattern is supported. Diagnostics of blocked compaction (A-d) in any case. Replanning (B) later, on a count over the test fixture's fixed corpus of sibling schema scopes. Drop or shelve the rest | Small to medium | Medium | Whether holding children of a large writable scope is supported (question 4) |
| [Rule-layer and split features](./rule-layer-split-features.md) | **Later** for new API. Do now: a struct-lattice contract test, and a test plus guidance for `Split(rule, Repeat(n, ...))` as the per-case budget. Drop provenance and five other items | Small now | Medium-high | Approval of the drops (question 8); each later item's named trigger |
| [Smaller engine items](./smaller-engine-items.md) | **Do now** (about one day): the member-map test (S8) and an internal lift-memo flag (S1). S3 optional. S6 goes to the `BasicMath` investigation. Drop S2, S4, S5, S7 | Small | Medium-high | None for the part to do now |
| [Conformance, docs and release](./conformance-docs-release.md) | **Do now**: ordinal `Max`/`Min` for strings (a defect), net8.0 in CI, the AOT and trimming declaration with a publish check, a modelling note (hidden discriminators, sound numeric bounds), directed subsume and delete tests in children. Drop `math.egg` and the programs that need a `.egg` reader | Small | Medium-high | Whether net8.0 stays a target (question 3), for net8.0 in CI; whether NativeAOT is a supported target (question 2), for the smoke app only |

## 2. Order of work

### First: work that needs no idle machine

These items can proceed in parallel. They are listed in the order that serves the later work and the measurement
campaign best: first what verifies the rest, then the waste and the failure modes known without timing, then the
campaign's preparation.

1. **net8.0 in CI**, on linux-x64 at least ([conformance](./conformance-docs-release.md), H). The package ships a
   net8.0 build that CI never runs. This goes first because it then verifies every later item on both frameworks. It
   holds while the package targets net8.0, which .NET 8's end of support in November 2026 puts to the owner
   (question 3).
2. **The idle-local-rule allocation fix** ([performance parity](./performance-parity.md), row 1; the same engine change
   is Q12 in [idle-machine measurement](./idle-machine-measurement.md)). A count-only probe shows a query child
   allocating about 1.5 KB per idle local rule per iteration: 165 KB per query at 100 idle rules, and 1.5 MB at 1,000.
   About two thirds of the bytes come from `PlanTemplate.InstantiateLayered`
   (`src/Eggnog/Relations/Planning/PlanTemplate.cs`), which allocates before it learns that a header is empty, and an
   idle variant then discards them. The same waste occurs for idle global rules at the root (about 1.17 KB per rule
   per iteration). It is the only item whose waste is known without timing. Performance parity owns the code,
   including the library's bookkeeping trim. The campaign times it. It lands only after night 2's root gate.
3. **The interning gauge and one exhaustion bound** ([interning](./interning-and-value-lifetime.md), phase C).
   Nothing waits on it, but today a long-lived graph runs out of ids or epochs with an undocumented exception that
   `InternedCount` cannot see coming. It matters most to hosts that run case splits on a long-lived graph, because the
   clock runs out first there.
4. **Ordinal `Max`/`Min` for strings** ([conformance](./conformance-docs-release.md), A). Today the result depends on
   the machine's culture, and the merge is not commutative on canonically equivalent strings. Nothing uses
   `Max<string>` yet, so the change costs a release note now and more later.
5. **Campaign preparation** ([idle-machine measurement](./idle-machine-measurement.md), phases 0 and 1;
   [performance parity](./performance-parity.md), phase 0). This covers:
   - the comparison method and its script;
   - `QueryScopeBenchmarks` and `LibraryRunOverheadBenchmarks`;
   - `BusyParentCatchUpBenchmarks`;
   - the 100-sibling release test, landed together with the interning plan's `ScopeChurnTests`;
   - a net8.0 target for the benchmark app, if net8.0 stays a target (question 3);
   - the bridge examples' degree-of-parallelism option, a shard parameter on `WritesBenchmarks`, and the library-API
     port of `math-microbenchmark.egg`;
   - the allocation-probe mode;
   - `wip/perf-basicmath` ported to a temporary branch from `main`, and the write-allocation prototypes.
6. **Tests and documentation with no ordering constraint.** These are:
   - the parallelism slice;
   - S8 and the internal S1 flag;
   - the struct-lattice and per-case-budget tests;
   - the AOT publish check, and the modelling note (hidden discriminators, sound numeric bounds) with the test
     fixture's class remarks;
   - the compiled-plans cleanup;
   - blocked-compaction diagnostics (A-d);
   - the interning documentation and churn tests (phases A and B).

   The documentation edits share one pass, and so do the roadmap and [decisions](../engine/decisions.md) edits.

### Cheap experiments to run first

Each of these is a count, needs no idle machine, and settles a question the plans otherwise leave open. Ranked by what
they decide:

| Experiment | Decides | Plan |
|---|---|---|
| The idle-rule probe before and after the header fix, at 0, 10, 100 and 1,000 idle and active rules | Whether the fix removes the engine's share of the idle-rule bytes. Whether demand-indexed local rules are needed is timed, by idle-machine Q8 | [performance parity](./performance-parity.md), row 1 |
| `EGGNOG_COUNTERS` per `BasicMath` run: clones by site, buffer creations, buffer-map growths | Whether the staging-buffer item closes without a profiler. This gates the ETW profile and compiled plans' Q1 | [performance parity](./performance-parity.md), row 2 |
| The fixed corpus of sibling schema scopes that `ScopedQueryCases.CompileAll` builds on the test fixture (every case's schema plus `DiscriminatedSchema`), under one frozen root in both orders, with recorded plans | Whether per-context replanning, with control over what a non-root freeze plans, is needed, and in which form | [colored-context](./colored-context-deferred-features.md), B; [idle-machine](./idle-machine-measurement.md), Q10 |
| Catch-up rows against split-step rows in domain splits on `PredicateTheory` under a writable query scope, at 3 and 8 cases | Whether the busy-parent catch-up risk of the engine map closes | [idle-machine](./idle-machine-measurement.md), Q7 |
| JIT shape of `RunIntersectTwo`, `RunFusedScan` and `Replay` at `3a6e0b7`, at `main` and on the port, on both frameworks | The prior for the `BasicMath` A/B | [performance parity](./performance-parity.md), row 4 |
| A subsume at a writable root with a live child, then catch-up | Whether `RetractionGuard.CheckSubsume` is deleted or wired in | [conformance](./conformance-docs-release.md), F |
| A NativeAOT publish in CI with IL2xxx and IL3xxx as errors | Whether to declare `IsAotCompatible` or only `IsTrimmable` | [conformance](./conformance-docs-release.md), B-min |

Several deciding runs are already done and are cited in their plans:

- the interning churn and exhaustion dry runs;
- the parallel mutation comparison (seven of seven caught by the default suite);
- the lift-memo removal (no test failed);
- the ICU culture runs;
- the iteration counts of the 56 scoped runs over the test fixture (every run completed), as the suite stood when
  measured; the scenario tests they ran are no longer in it;
- the lanes-per-dispatch counts.

### Then: one idle-machine campaign

All timed questions are grouped into one campaign, defined in
[idle-machine measurement](./idle-machine-measurement.md), section 6. It has:

- one baseline, `3a6e0b7`;
- one method: interleaved builds, two rounds, ratio of medians;
- one gate: 1.02 on cases of 100 µs or more, with a rule for inconclusive cases;
- one profiler: ETW kernel sampling, not EventPipe.

Every night starts with an A/A control, and no threshold is used below twice the A/A spread. Where the two plans
differ, the campaign's settings apply: two nights, not one, and five pairs against the Rust, not three rounds.

- **Night 1** (about 4 hours):
  - the A/A control;
  - the root set, `3a6e0b7` against `main`;
  - `BridgeMath` and `BridgeAc` at `673495d`, `2fd4a18` and `main`;
  - the bridge examples against the Rust, at every processor and at one thread.
- **Night 2**:
  - the candidates against `main`: the idle-rule fix, and the `BasicMath` port as a three-build run with `3a6e0b7`;
  - S6's padding experiment, on `3a6e0b7`;
  - the per-child, case-split and catch-up timings;
  - the library classes on net8.0 and net10.0 under both collectors;
  - writes against the Rust with the shard parameter, and the write fixes under both collectors;
  - ETW profiles, including the workloads that compiled plans' Q1 names;
  - A0's two-sided timing, only if the owner answered question 4 yes.
- **A later window, only on demand**:
  - compiled plans' Q3 ceiling prototype, after Q1's profiles have chosen the target;
  - bisection, only for a confirmed failure the owner will schedule;
  - the first-id-block latency gate, only if its trigger fires.

### Dependencies that fix the order

- The campaign runs before any root-path change lands. This covers the idle-rule fix, the `BasicMath` port, the write
  fixes, S6 and A0.
- The compiled-plans drop can be decided now. It makes the `BasicMath` branch's fast paths the chosen route to kernel
  speed, not throwaway work.
- A0 lands after the `BasicMath` regression is understood, so that the two effects are not confounded.
- Any rewrite of a parallel kernel, whether from performance parity or from kernel specialization, reruns the
  parallelism plan's mutation comparison. It builds the eager switch if the new kernel lacks targeted tests.
- Eq containers in children, and the six push/pop programs that need them, move together or not at all.

## 3. Drop or park

**Drop.** Nothing measured needs these, the record already answers them, or something cheaper covers them. Each keeps a
reopening trigger in its plan.

- **Compiled plans as dynamic code**, in all three forms. Interpretation of the right-hand side is amortized over 127
  lanes on large workloads. Small query runs are too short (1.2-4.0 lanes per dispatch on the test fixture) to repay
  compiling at run time. Closed-world C# reaches the same ceiling.
- **The herbie and eggcc ports, `math.egg`, and the `UnstableFn`, `Map`, `MultiSet` and `BigRat` programs.** They need
  a `.egg` reader, which is not planned (rule-layer decision D14), or they add little beyond directed tests.
- **64-bit cells, and recycling ids or the clock globally in place.** A host that retires its graph on the gauge pays
  one rebuild per graph life, which at the measured horizons is spread over millions of queries.
- **A separate serial-against-parallel suite.** Targeted tests already cover it at the root.
- **Provenance** as a library feature. Ablation answers whether a ruleset is needed for a fact, and D41's recipe plus
  an observer diff already gives the step and the candidate rules; that worked in a dry run. In lattice-heavy theories
  a first-write stamp would also misattribute narrowed values.
- **The rule-layer items:**
  - case-only tags;
  - the depth-limited split count;
  - write callbacks;
  - a public untyped layer;
  - the guarded lift as a feature.
- **The smaller engine items:**
  - S2, the per-lift hook: nothing needs it;
  - S4, catch-up failure pairing: unreachable through the library;
  - S5, lazy index catalogs: saves 96 B only on layers that are never probed;
  - S7, extraction members over a context: no caller.
- **The colored-context items:**
  - A1, the cursor remap: superseded by A0;
  - G2, the ranking: close it as "smaller id wins";
  - B1, pruning redundant child rows;
  - B3, flattening hidden sets;
  - G9, shared sibling ids;
  - deduplicating driver keys across layer pieces.

**Park.** Each of these waits on a named trigger:

- interning:
  - the 4-id first block;
  - ids and epochs local to transient children;
  - scoped interning;
  - eq containers in children;
- performance:
  - demand-indexed local rules;
  - batch-state pooling;
  - kernel specialization, until Q3;
- colored contexts:
  - A2, the hidden-set remap;
  - per-context replanning (B);
  - a per-run memory budget (G5);
  - parallel cases (G8);
- the API:
  - partial primitives;
  - a per-case budget across executions;
  - the public lift-memo members;
  - S3;
- tests and release:
  - the eager parallel switch;
  - the NativeAOT smoke app;
  - the deferred benchmark classes (`ScopeCatchUp`, `MirrorSync`, `EqualStep`, `SplitStep`, `TheoryDefinition`).

## 4. Decision gates

Every timed gate uses the campaign's A/A rule. No threshold applies below twice the measured spread.

| Gate | Evidence | Threshold |
|---|---|---|
| Idle-rule fix lands | Probe bytes before and after; full suites on both frameworks; night 2 root set | The engine's share of idle-rule bytes falls by at least 90%, so bytes per query at 100 idle rules fall by at least half. No confirmed root failure |
| Root set holds | Q1, `3a6e0b7` against `main`, 129 cases | Ratio of medians at most 1.02 in both rounds, with same-build runs within 1%. Inconclusive: up to four rounds, then reported and ungated |
| Whole programs and parity | `main` against `2fd4a18`; C# against the Rust at every processor and at one thread | Against `2fd4a18`: at most 1.02. Against the Rust: at most 1.10, parity holds; 1.10 to 2.0, record (profile only if parity remains a goal); above 2.0, investigate before release |
| `BasicMath` port lands | Main against the port, with the three-build run from `3a6e0b7` | Recovers at least half of each configuration's loss, nothing else more than 2% worse, `math` and `ac` not worse. A whole-program gain of 2% or more passes on its own. Drop the branch if the loss is under 3% everywhere |
| Demand-indexed local rules are built | Q8 after the fix | Overhead per query at 100 idle rules (time per query minus time at 0 rules) exceeds 25% of the query's own time at 0 rules, at p50 or at p99. One measurement and one rule for both plans |
| Write fixes land; writes gap closes | Bytes now; A/B under both collectors; writes against `cargo bench` | Fixes: at least 25% fewer bytes, 8 threads at least 3% faster, no configuration more than 2% worse. Gap: closed at 1.10 or less. Shard option kept if it moves the 2- or 4-thread ratio by more than 5 points |
| Staging buffers | Clone counters; ETW only if they leave it open | Close if buffer-map growth goes to zero. Otherwise pool at 5% or more inclusive |
| Kernel specialization (compiled plans Q3) | A hand-written ceiling prototype for the target's three hottest plans | A scaled gain of at least 10% on a target workload chosen in advance, with the root set within 2%. Build fast paths only if 20 or fewer shapes cover 90% of run time |
| Suffix compaction (A0) | Owner question 4; then `math` with no child, a held child, and a held child on A0 | Held child at least 10% slower than no child, and A0 within 3% of no child |
| Replanning (B) | Derivation work per query on the fixed corpus of `ScopedQueryCases.CompileAll`, in both freeze orders | Provisional plans at a loss of 1.5x or more. Per-scope plans only if a loss of 1.5x or more remains after them, and the owner accepts the retained plan bytes per frozen scope |
| Catch-up under a busy parent | Q7 counts | Open an engine item if catch-up exceeds 25% of a split step's rows at 8 cases. Otherwise close the risk |
| Library run overhead | Q9 | Library `Run` at most 1.05 x engine `RunRules` at 10 rules. Otherwise trim the per-run bookkeeping |
| Interning, later parts | The gauge in a host's long-lived use | First block: ids end graphs before the clock at a retirement rate the owner calls costly. Domain-local ids: forced retirements above an owner-set rate. Scoped interning: retirements or retained memory above an owner-set bound for a host that cannot canonicalize |
| S6, `ProberPiece` | Padding on the baseline, then the shrink | Continue past padding only if it slows `BasicMath` by more than the A/A spread. The shrink must recover at least 3% |
| AOT declaration | CI publish with warnings as errors | No IL2xxx or IL3xxx and exit code 0: declare `IsAotCompatible`. Any IL3xxx: `IsTrimmable` only |

## 5. Questions for the owner

### Direction

1. Is throughput parity with egglog on saturation workloads still a goal in itself, beside the latency and memory of
   small query children? This decides whether a 1.10-2.0 ratio leads to profiling. It also decides which workload
   compiled plans' Q3 targets: root throughput or the latency of small query children.
2. Is NativeAOT or trimming a supported target for the package? A yes adds the AOT smoke app. It also makes any
   dynamic-code technique a permanent second execution path.
3. Should the package keep targeting net8.0, given that .NET 8's support ends in November 2026? If it stays, net8.0
   goes into CI first and the benchmark app gains a net8.0 target. If it goes, both are dropped, and the conventions'
   "net8.0 and net10.0" changes with it.
4. Should the library make it cheap to hold a child of a large writable scope, or to run case splits at one? A no
   drops A0 and documents the limitation.
5. Will the library ever offer containers of classes? A no drops eq containers in children and the six push/pop
   programs that wait on them.
6. If the campaign confirms a root regression other than `BasicMath`, will it be scheduled? A no skips bisection and
   only records the failure.

### Approvals

7. Do you accept the compiled-plans decision? It drops dynamic code and replaces the roadmap item with kernel
   specialization under performance parity, decided by Q3. Decision 8 would be restated as "the interpreter is the only
   executor; specialization is closed-world". Hand-written fast paths, as on `wip/perf-basicmath`, would be the
   accepted way to buy root speed. For the cleanup:
   - delete the eight uncalled array overloads;
   - move `RowOps.IsVisible` to the tests as an oracle;
   - move `ActionBuffer.Push` to the span form of `Bindings.Push`, subject to the root gate.
8. May every item in section 3's drop list be closed, with its reopening trigger recorded in the roadmap? This covers
   G2 recorded as "smaller id wins at every level", and the u32 decision recorded in
   [decisions](../engine/decisions.md) as the interning plan words it. Or should some stay listed as "not planned"?
9. Decision 4 and non-lattice merges at the root in parallel. The default is to qualify decision 4 to match
   `MergeFunction` and egglog. The alternative is to merge such tables serially at every size.
10. The gauge. Is a public `EGraph.Usage` returning a `GraphUsage` record struct acceptable? At exhaustion, should the
    library throw a documented `OverflowException` that leaves the scope unfaulted, or a library exception? Should one
    bound (`UnionFind.MaxId`) hold on all four minting paths? Should the library also offer a helper that rebuilds a
    graph from a definition delegate, or only document the pattern of retiring a graph on the gauge? And what
    retirement interval, or retained memory between retirements, would justify scoped interning for a host that
    cannot canonicalize its literals?
11. May the idle-rule header change (Q12) go ahead now, landing after night 2's root gate? The rule for
    demand-indexed local rules is settled, and both plans share it: build them if, after the fix, the overhead per
    query at 100 idle local rules exceeds 25% of the query's own time at 0 rules, at p50 or at p99.
12. Are departures from egglog that only remove allocation acceptable? There are three: an empty-header path, a
    bounded hash reserve at merge, and staging arrays returned to the pool.

### API details

13. Should `RunReport.Lifts` count only computed lifts before a stable release? If not, the public memo members wait
    until a user asks for them.
14. Is `Split(rule, Repeat(n, ...))`, tested and documented, an acceptable way to bound cases, with a budget across
    executions and a count of truncated cases left until a user needs them? If S3 lands, should cancellation
    overshoot by at most one iteration or one lift round?
15. Global rules that a non-root scope's run uses before its freeze are planned on that scope's declarations alone
    (residual risk 4 of the rule-layer spec). Is that acceptable until the sibling-scope count exists, with
    provisional plans as the first remedy? Should `Scope.Freeze` warn when the scope has live children?
16. If partial primitives are built, should they be `Try`-shaped delegates or tuple-returning `Func` overloads? Is
    provenance for value-keyed relations alone useful to anyone, given that the facts worth explaining are often
    lattice values?

### Measurement and CI

17. When will an idle machine be available for two nights? Does an unattended night on the i7-7700K with other
    applications closed count? Should the High performance power plan be selected, and are administrator rights
    available for ETW?
18. Do you accept the campaign rules?
    - The 2% gate applies to cases of 100 µs or more.
    - Inconclusive cases get up to four rounds.
    - One cumulative comparison replaces the per-stage gates of colored-contexts spec section 21.11.
    - `main` becomes the baseline after the campaign.
    - Graph construction is closed by allocation.
    - Literature B5's failure half is deferred.
19. Where should results live: a report under `docs/`, tables in `src/Eggnog.Benchmarks/README.md`, or both? Are
    BenchmarkDotNet's exported reports kept? May the benchmark app link `PredicateTheory.cs` and `Pred.cs` from
    `src/Eggnog.Tests/Predicates`, or should they move to a shared source folder? Compiled plans' Q1, Q3 and Q5 time
    query children of that fixture, and idle-machine Q10 times the corpus that `ScopedQueryCases.CompileAll` builds.
20. Should `wip/perf-basicmath` be pushed so the record has the commits it cites, or deleted if the A/B does not
    justify it? Is a regression confined to the root microbenchmark acceptable if `math` and `ac` show no loss? Should
    S6's padding run inside that investigation?
21. net8.0, if it stays a target (question 3): in CI on linux-x64 only, or on all five platforms? And in the benchmark
    app?
22. Soaks:
    - Should the concurrent-children soak run nightly, on linux-arm64, or by hand?
    - Should the 100,000-query churn be opt-in?
    - May a forced-compaction mode become a permanent test configuration if A0 goes ahead?
    - When the eager switch is built, may it be read from an environment variable?
23. Smaller choices:
    - S8: add a membership check to `CheckMemberMaps`, or delete the redundant line?
    - Where should the hidden-discriminator note live: `architecture.md` section 2.9, or a modelling guide?
    - Should the roadmap's two misdescribed colored-context entries be corrected as the colored-context plan words
      them?
