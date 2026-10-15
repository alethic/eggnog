# Idle-machine measurement campaign and library-path benchmarks

This plan covers the timed gates and benchmarks that the design record left open because the machine was shared. They
are:

- the root benchmark set against the baseline from before colored contexts;
- egglog-bridge's `math` and `ac`, against the build from before the store-backed root and against the Rust;
- the per-child and case-split figures, and catch-up under a busy parent;
- the benchmarks the library itself has never had: its own paths, the latency of transient children, and the cost of
  idle local rules.

It is also the single definition of the measurement campaign that [performance parity](./performance-parity.md) shares:
one baseline, one comparison method, one profiler and one set of thresholds.

## 1. Summary and recommendation

**Recommendation: Do now**, narrowed. **Confidence: medium.**

**Now, with no idle machine:**

- Write the comparison method against `3a6e0b7`, the baseline on `main`.
- Write `QueryScopeBenchmarks` (with the recorded plans of the sibling-schema-scope question) and
  `LibraryRunOverheadBenchmarks`.
- Write the 100-sibling release test and the counters for catch-up under a busy parent.
- Open one small engine item: allocate in header instantiation only once a run is known not to be empty. Allocation
  counts verify it; the root gate times it.

**At the first idle window:** one campaign, shared with performance parity:

- the root set, `3a6e0b7` against `main`;
- `BridgeMath` and `BridgeAc` at `673495d`, `2fd4a18` and `main`, under the same 2% gate;
- the C# time over the Rust time, reported as a figure;
- the timed halves of the per-child, case-split and query-child questions;
- ETW profiles.

The campaign has a rule for inconclusive cases, and a stated action for each outcome.

**Deferred:**

- the `ScopeCatchUp`, `MirrorSync`, `EqualStep` and `SplitStep` classes, until a change touches those paths;
- `TheoryDefinitionBenchmarks` and a timed `EGraph` construction comparison;
- the per-structure byte split of a child;
- the literature review's B5 failure count under a timeout policy.

The new evidence is a count-only dry run made for this plan. It used a synthetic workload: two-atom local rules over
ten 1,000-row tables, under a frozen schema scope. A transient query child allocates about 1.5 KB per idle local rule
per iteration: 165 KB per query at 100 rules and 1.5 MB at 1,000. The slope depends on rule shape, because semi-naive
evaluation makes one variant per body atom. About two thirds of those bytes come from `PlanTemplate.InstantiateLayered`
(`src/Eggnog/Relations/Planning/PlanTemplate.cs`). It allocates its arrays and lists before it learns that a header is
empty, and an idle variant then discards them. The same waste occurs for idle global rules at the root. No timing can
change that finding, so the fix should not wait for the campaign (section 3.6, Q12).

The value is real for hosts that run each query in a transient child under a scope with many local rules, and for any
later root-path change. For the historical root gates it is mostly record-keeping, unless the owner will act on a
confirmed regression other than `BasicMath`. The owner has deferred `BasicMath` itself (section 9, question 1).

## 2. What it is and why it was planned

- **Root gates.** The colored-contexts specification subjects every change to the root's paths to a gate: the root
  benchmark set must stay within 2% of a baseline recorded before the change. That rule answered a review finding that
  the 2% gates had no benchmarks behind them ([colored-contexts spec](../history/colored-contexts-spec.md), section 20
  "Review responses", second review, R2-7).
  - The baseline classes were added first. On `main` that commit is `3a6e0b7` ("Add the root benchmark set for the
    colored contexts work").
  - Section 21.11 ("Still deferred") of the spec lists the gates owed by each stage, under that document's milestone
    labels:

    | Stage (spec label) | Commit on `main` | Content | Classes |
    |---|---|---|---|
    | M9.1 | `8e4d371` | stores, contexts, the shared clock, id blocks | `ActionLookupBenchmarks`, `DisplacedRebuildBenchmarks`, `WritesBenchmarks`, `RuleSetRunBenchmarks` |
    | M9.2 | `c5ece1b` | the layered union-find | `DisplacedRebuildBenchmarks`, the union-find classes |
    | M9.3 | `00ba318` (merge) | layered tables, child rebuild, catch-up | `WritesBenchmarks`, `ActionLookupBenchmarks`, `RuleSetRunBenchmarks` |
    | M9.4 | `00ba318` (merge) | layered tables, child rebuild, catch-up | `DisplacedRebuildBenchmarks`, `RuleSetRunBenchmarks` |
    | M10.1 | `2fd4a18` (merge) | hidden rows in scans, subsets and index builds | `ConstrainedScanBenchmarks`, `IndexRefreshBenchmarks` |
    | M10.2 | `2fd4a18` (merge) | layered query execution | `RuleSetRunBenchmarks`, `ConstrainedScanBenchmarks` |
    | M10.3 | `8444a46` | rules per context, the store-backed root | `RuleSetRunBenchmarks`; `BridgeMath` and `BridgeAc` on a store-backed root; the cost of building an `EGraph`; `ContextExtractionBenchmarks` `RootContext` against `RootApi` |
    | M10.4 | `5212046` | lift primitives, split hooks | `WritesBenchmarks`, `RuleSetRunBenchmarks` |

  - The roadmap lists two smaller engine items that the record makes conditional on an idle-machine gate: lazy
    `IndexCatalog` objects, and shrinking `ProberPiece` if the prober's growth shows in the parallel
    `RuleSetRunBenchmarks` cases ([roadmap](../roadmap.md), "Engine work", "Smaller engine items"). The
    [smaller engine items](./smaller-engine-items.md) plan drops the first and moves the second into performance
    parity.
- **Parity with the Rust.** The engine is a reimplementation of egglog's engine. The benchmark app runs egglog-bridge's
  `math` and `ac` examples the way their Rust `main`s do (`src/Eggnog.Benchmarks/README.md`, "The egglog-bridge
  examples"). The last timing predates the store-backed root. The roadmap cites it at `b041e70`. In that timing `math` took about 6.6 s against the Rust's 7.4 s, and `ac` about 15.5-16 s on
  both ([roadmap](../roadmap.md), "Benchmarks and timed gates").
- **Per-child cost and case splits against clones.** These come from the literature review
  ([colored-egraph-literature.md](../history/colored-egraph-literature.md)). This plan writes its labels as
  "literature B4" and "literature B5", as that review names them:
  - literature B4: per-child time and bytes must stay flat as the root grows, because Easter Egg's per-colour work
    "never clearly beat clones";
  - literature B5: compare bytes per structure against a clone baseline on a case-split workload, and report time and
    failures separately under a stated timeout policy;
  - G6: run time is "the unmeasured risk".

  [`../engine/decisions.md`](../engine/decisions.md) ("Requirements from the literature review") lists these
  benchmarks without a factor. The 1.5x threshold comes from colored-contexts spec section 21.11 ("flat within 1.5x
  from 20k to 2M rows") and the roadmap's "Per-child cost" item.
- **Catch-up under a busy parent.** [egglog-engine-map.md](../history/egglog-engine-map.md) section 8.2 ("Design
  risks") says: "A parent that is not frozen and changes a lot makes every child re-scan its appends. The design
  assumes parents are mostly frozen." Split cases under a writable query scope break that assumption, because each
  lift writes the parent.
- **Library paths and their timed gates.** The rule-layer spec's deferred list
  ([rule-layer-spec.md](../history/rule-layer-spec.md), section 15.5 "Deferred") asks for two benchmarks:
  - one of the scope paths: queries on a frozen scope, transient children, mirror syncs, the `Equal` step per match,
    the catch-up each call makes first, and the `ScopeWriter` catch-up (the spec's implementation notes N86-N88);
  - one of the case-split paths: a split step's request search, the case wrappers, the in-case schedule run again per
    live case, the lift memo check, the extractor-query catch-up, and the reads of recorded failures (N112-N114).

  It also asks for timed gates: a split step that finds nothing new, a host lift, and the per-iteration and per-write
  overhead at the root. Nothing was timed because the machine was shared.
- **Transient children, idle local rules and sibling scopes.** The roadmap's "Latency of transient children" item
  ([roadmap](../roadmap.md), "Benchmarks and timed gates") asks for the latency of a query child under a frozen scope,
  and for the per-iteration cost of idle local rules at 100 and 1,000 rules. On a scope with local rules, every query
  child runs every local rule of its scope on every iteration (`ScheduleExecutor.RunIteration`, section 3.6), so that
  cost grows with the number of rules whatever the query touches. If it proves significant, the follow-up is
  demand-indexed local rules, which [performance parity](./performance-parity.md) owns. Two further questions arise
  from the same pattern of many frozen scopes with local rules under one root:
  - plans are made once per rule, on the statistics of the first context that runs or pre-plans it
    ([rule-layer-spec.md](../history/rule-layer-spec.md), section 11, residual risk 4; engine map section 8.2,
    "Frozen plan statistics"), so a later sibling scope may run plans made on another scope's statistics;
  - deferred release (D40) and `RuleVariants.Free` are tested for one released scope, not for many siblings.

## 3. Current state

### 3.1 The benchmark app

`src/Eggnog.Benchmarks` is a net10.0 console app. It runs BenchmarkDotNet 0.15.8 in process, under server GC, and
keeps the user's power plan (`Program.cs`, `Eggnog.Benchmarks.csproj`). It references the library and has
`InternalsVisibleTo` from `src/Eggnog/Eggnog.csproj`. Every class is an engine class under `Relations/`. There are 44
`[Benchmark]` methods:

- `Root/`: the root set;
- `Writes/` and `UnionFind/`: ports of egglog's divan benches;
- `Bridge/`: `math` and `ac`, plus an example mode (`--bridge-example`) that prints the Rust's lines;
- `Contexts/`: per-child, many-children, find, extraction, case-split memory, copy-volume and late-mint classes, plus a
  footprint mode (`--context-footprint`).

The root set runs 129 cases: 60 in `Root/`, 12 in `Writes/` and 57 in `UnionFind/`. Every class checks its result.
The library has no benchmark. `src/Eggnog.Tests` has one allocation probe outside `Relations/`
(`Rules/QueryTests.cs`).

Running in process means the runtime and the GC mode are the process's own. `Program`'s remarks say that options needing
another runtime or GC mode "are rejected by BenchmarkDotNet's in-process validator". The README's "Running" section
selects workstation GC with `DOTNET_gcServer=0`. So each target framework and each GC mode needs its own process
launch.

### 3.2 Root gates

None has been timed on an idle machine.

- **The benchmark sources match.** Between `3a6e0b7` (`src/Eggnog.Relations.Benchmarks/Root/`) and `main`
  (`src/Eggnog.Benchmarks/Relations/Root/`) the root classes differ only in comments. `Writes/` and `UnionFind/` are
  identical. Both apps target net10.0 with server and concurrent GC and the in-process emit toolchain. So both builds
  run the same workloads, and a cumulative comparison is sound. The baseline's executable is
  `Eggnog.Relations.Benchmarks.exe` in the old project layout.
- **The cumulative comparison covers more than colored contexts.** Eleven first-parent commits between `3a6e0b7` and
  `main` change engine sources:
  - `8e4d371`, `c5ece1b`, `00ba318`, `2fd4a18`, `8444a46` and `5212046`: the colored-context stages above;
  - `2d0148e`: the merge of the bridge and extraction port, which also changed `Execution/Database.Execution.cs`,
    `Tables/PendingState.cs` and `Tables/Table.cs`;
  - `3ccde1e` and `14cd695`: small engine edits that came with the library merges;
  - `f2bfea2`: the fold into one project;
  - `ea32185`: the release preparation, which touched 298 engine files.

  The scope and case-split commits (`d8b0349`, `64cd651`) change no engine source.
- **`BasicMath` is known to regress.** It runs 10-15% slower than the baseline in all four configurations
  ([roadmap](../roadmap.md), "The root `BasicMath` regression"). The finding comes from interleaved runs (baseline,
  candidate, candidate, baseline) on the shared machine with BenchmarkDotNet's default job. One such round gave ratios
  of 1.25 and 1.12 (20,000 rows, serial and parallel) and 1.12 and 1.09 (200,000 rows). Its allocation is unchanged,
  which shows that allocation does not predict time here. Unreviewed fix experiments are on the branch
  `wip/perf-basicmath` (eight commits). The owner deferred that work.
- **A sequential short run cannot test a 2% gate.** An earlier pass ran each class first on the baseline build and
  then on the candidate, with three iterations per case on the shared machine. Its time deltas ranged from -36% to
  +124%. The largest were on cases of a few microseconds (union-find at 1,000 ids).
- **Allocation-only dry runs moved little** (colored-contexts spec section 21.11): `math` 9.80 GB to 9.61 GB, `ac`
  15.47 GB to 15.57 GB, and `RootContext`/`RootApi` 612.5 KB/608.6 KB.
- **Run lengths from earlier shared-machine runs** under the default job, in BenchmarkDotNet's "Global total time":
  - `BasicMath`'s four cases took 16-25 s per run;
  - the 57 union-find cases took about 4.5 minutes;
  - the 12 writes cases took 1.6-2 minutes.

  These are the basis of the campaign estimate in section 5.

### 3.3 Bridge against the earlier build and against the Rust

The C# side is ready (`BridgeMathBenchmarks`, `BridgeAcBenchmarks`, `BridgeExampleRunner`).

- **The sources match across builds.** `BridgeMathBenchmarks` first exists at `673495d`. The bridge sources are
  identical from `673495d` to `2fd4a18`, the last commit before the store-backed root. Against `main`
  (`src/Eggnog.Benchmarks/Relations/Bridge/`) they differ only in comments and in the usage string of
  `BridgeExampleRunner`. A C#-against-C# comparison is therefore available:
  - `2fd4a18` against `main` is the spec's gate for the store-backed root;
  - `673495d` against `main` is performance parity's question of whether `BasicMath`'s regression shows in whole
    programs.

  The root baseline `3a6e0b7` has no bridge benchmark, so the root comparison cannot include these workloads.
- **The Rust binaries must be rebuilt.** They were built in earlier work under
  `ext/egglog/target/release/examples/`. These are build outputs and are not in the repository. They must be rebuilt at
  the submodule's pinned commit before timing.
- **Building an `EGraph` is cheap in bytes.** A count-only dry run for this plan measured allocations when building
  one (`--context-footprint`-style, after warm-up, averaged over 200):
  - the engine's `Eggnog.Relations.EGraph` on its store-backed root: 6,193 B;
  - the library's `Eggnog.EGraph`: 56,000 B.

  Both are less than one child of section 3.4 (95.9 KB or more). No figure exists from before the store-backed root to
  compare with. Each bridge invocation builds a fresh graph, but construction is a negligible part of a 6.6 s run.

### 3.4 Per-child cost and case splits

From [colors-m10-reports.md](../history/colors-m10-reports.md) ("L6: what a child costs, and memory against full
clones"):

- **Bytes per child are flat.** 95.9-96.3 KB for 100 unions and a rebuild, and 144.7-145.0 KB with a flush and a rule
  run, at 20,000, 200,000 and 2,000,000 rows, frozen or not. The examination, copy and match counters are identical at
  every size.
- **Bytes per case against clones.** A case costs 13.4-13.7 KB (17.3-19.8 KB lifted). A clone costs 1.21 MB at 20,000
  rows and 9.49 MB at 200,000 rows.
- **One case, step by step:**

  | Step | Bytes |
  |---|---:|
  | The case | 2,975 |
  | A union and its rebuild | 9,848 |
  | A fact | 3,317 |
  | A rule run | 12,491 |
  | A lift | 24,537 |

- **Not measured:** any time figure (the report says the dry-run times "are not evidence either way"), and the split of
  a child's bytes into layer tables, hidden sets, overlay, cursors, watermarks and indexes.

The classes are `PerChildCostBenchmarks`, `CaseSplitMemoryBenchmarks`, `ManyChildrenBenchmarks` and `ContextFootprint`
in `src/Eggnog.Benchmarks/Relations/Contexts/`. No workload in them can time out, so literature B5's failure count
would be zero by construction.

### 3.5 Catch-up under a busy parent

There is no benchmark. The cost is clear from `ContextTables.CatchUpRows` in
`src/Eggnog/Relations/Layers/ContextTables.CatchUp.cs`. Each catch-up reads every row that each unsettled ancestor
appended to a shared table since the context last caught up. Its remarks state the cost as "O(rows appended to
unsettled ancestors since the last catch-up) plus O(tables × unsettled depth)". For each row read, the catch-up does
three things:

- a `HiddenBetween` test;
- a `find` per rebuild column;
- a `FindRow` in the context's own layer.

`LayerStatistics.CatchUpRowsExamined` counts the rows read. With k live children and A rows appended to the parent, the
children examine k × A rows in all. `PerChildCostBenchmarks` asserts that this counter is 0 under a frozen root. The
executor's counters (`ExecutorCounters`, enabled by `EGGNOG_COUNTERS`) count the rows that rule execution scans in the
same context (`OwnRowsScanned`, `AncestorRowsScanned` and the index-row counters). So the share of a split step's row
work that goes to catch-up can be counted without timing.

### 3.6 Library paths, transient children and idle local rules

Nothing is written. The paths to measure are in these files:

- `src/Eggnog/Host/QueryRunner.cs`: a query on a frozen scope runs in a transient child, created and disposed for the
  call;
- `src/Eggnog/Scopes/MirrorSync.cs`;
- `src/Eggnog/Graph/Scope.cs`: `CatchUp`, `CatchUpAndThrowIfStopped`;
- `src/Eggnog/Graph/ScopeWriter.cs`;
- `src/Eggnog/Scheduling/ScheduleExecutor.cs`: `RunIteration`;
- `src/Eggnog/Scheduling/SplitSchedule.cs` and `ScheduleCaseRunner.cs`.

`ScheduleExecutor.RunIteration` does work per rule beyond the engine's own:

- it lowers the rules, allocating the `LoweredRule[]` and `EGraphRuleId[]` arrays;
- for each variant, it makes three lookups keyed by the variant's description string (`NumMatches`,
  `RuleSearchAndApplyTime`, `PlanOf`);
- it makes one `Dictionary<Rule, RuleStatisticsBuilder>` lookup.

**The dry run.** For this plan, a count-only dry run was made in a private clone of `eda0c61`, with these settings:

- net10.0 Release, server GC;
- allocated bytes per call after 50 warm-up calls, averaged over 200;
- an unpublished probe, with no timing.

The workload is synthetic:

- ten relations `p0`..`p9` over `long`, each with 1,000 rows at the root, and the root frozen;
- a schema scope that defines N local rules of the two-atom form `p_i(x), p_j(x) => q_i(x)`, runs them to
  saturation and freezes;
- per query: a child of the schema scope, one `Assume`, `Run(Saturate)`, one `Check`, and `Dispose`.

In the idle case the child asserts into a table no rule reads. In the active case it asserts into `p0`.

| Local rules | Idle: bytes per query | Active: bytes per query |
|---:|---:|---:|
| 0 | 11,380 | 10,504 |
| 10 | 27,472 (1 iteration) | 34,569 (1 iteration) |
| 100 | 165,288 (1 iteration) | 314,868 (2 iterations) |
| 1,000 | 1,512,104 (1 iteration) | 2,914,823 (2 iterations) |

On this workload each idle local rule costs about 1.5 KB allocated per iteration. Each rule has two body atoms, so
semi-naive evaluation gives it two variants. The figure per rule therefore scales with the number of body atoms, and
the 1,000-row tables and ten touched tables are this probe's choice, not a property of the engine.

**Where the bytes go.** Allocation-tick sampling by type at 1,000 idle rules attributes about two thirds of them to
the engine's header instantiation:

- `LayeredSubset[]`, `Constraint[]` and `List<Constraint>`;
- `(AtomId, Constraint)[]` and `JoinHeaderSpec[]`;
- `RuleProgress[]`.

The source agrees. `PlanTemplate.InstantiateLayered` allocates `new JoinHeaderSpec[count]`, `new LayeredSubset[count]`
and two `List<Constraint>` before `TryEvaluate` returns false on `subset.IsEmpty`. An idle variant fails on its first
header, the focus atom's timestamp bound, which is evaluated first ([performance parity](./performance-parity.md),
section 3, "The code behind the allocation figures", "Idle local rules"). So all of those allocations are wasted for it. `ContextView.EvaluateLayers`
(`src/Eggnog/Relations/Execution/ContextView.cs`) allocates further arrays and lists per header over two or more
layers.

About a third of the bytes go to the library's per-run bookkeeping: the `HashSet<CompiledRule>` behind `RulesFor`, the
`RuleStatisticsBuilder` and `RuleStatistics` dictionaries and objects, and `Ruleset` enumerators.

The same probe at the root uses N global rules, and a write plus `Run(Saturate)` that changes nothing the rules read.
It allocates 1,170,334 B at 1,000 rules: about 1.17 KB per idle rule per iteration, mostly the same engine header
arrays. The root goes through the same method, because `PlanTemplate.Instantiate` wraps a single-table evaluator and
calls `InstantiateLayered`.

**What this decides.** A query child's allocation grows linearly with the idle local rules of its scope, at about
1.5 KB per rule per iteration on this two-atom workload, and two thirds of it is allocated and then discarded. That is
settled whatever the time turns out to be. Allocation does not predict time (`BasicMath`, section 3.2), so the time per
idle rule, and whether demand-indexed local rules are needed, still need an idle machine (Q8).

### 3.7 The sibling-scope test

None exists. Related pieces:

- `src/Eggnog.Tests/Scopes/LocalRuleTests.cs` tests that one released scope frees its local rules
  (`ALocalRuleIsGoneAfterItsScopeIsReleased`). No test covers 100 siblings or retained memory.
- `EGraph.VariantsByDescription` (`src/Eggnog/Graph/EGraph.cs`) holds "every live engine variant", global rules' and
  local rules' alike. `RuleVariants.FreeVariant` (`src/Eggnog/Rules/RuleVariants.cs`) removes entries one variant at a
  time. The engine's `EGraph.RuleCount` (`src/Eggnog/Relations/Bridge/EGraph.Rules.cs`) counts registered rules, and
  each rule's cached plan lives in its `RuleInfo`.
- The [interning and value lifetime](./interning-and-value-lifetime.md) plan proposes `ScopeChurnTests` on the same
  scoped test theory, `PredicateTheory` (`src/Eggnog.Tests/Predicates/`), for churn and retained-memory tests.
- The predicate fixture (`src/Eggnog.Tests/Predicates/Scoped/PredicateTheory.cs` and
  `src/Eggnog.Tests/Predicates/Pred.cs`) does not depend on xunit and could be linked into the benchmark app.
  `DiscriminatedSchema.cs` and `ScopedQueryCases.cs` use `Assert`, so their queries can be counted in tests but not
  timed as they stand. Compiled plans' Q1, Q3 and Q5 time query children of this fixture.

### 3.8 Platforms

The library targets net8.0 and net10.0. The benchmark app targets net10.0 only, and so do the baseline apps at
`3a6e0b7`, `673495d` and `2fd4a18`. So the JIT behaviour that net8.0 users get is not measured.

## 4. Value

- **Maintainers and later root-path changes.** A trustworthy baseline at `main` for the root set, and a tested way to
  compare two builds, are preconditions for any root-path change that follows. The known candidates are these:
  - the header-instantiation fix (Q12);
  - a reviewed subset of `wip/perf-basicmath`, and the `ProberPiece` experiment that
    [smaller engine items](./smaller-engine-items.md) hands to [performance parity](./performance-parity.md);
  - performance parity's write-allocation and pooling fixes.

  Without this campaign each of those either guesses or repeats the work. The cumulative comparison also says whether
  `BasicMath` is the only regression. The roadmap's "within noise" for the other classes rests on shared-machine runs.
  [Compiled plans](./compiled-plans.md) recommends Drop. Its profiles would come from this campaign only if that plan
  were revived, so it is not counted here as a reason to do this work.
- **The historical gates.** Their value depends on what follows a failure. A pass closes the colored-context gates and
  sets a new baseline. A confirmed failure other than `BasicMath` becomes a roadmap item. If the owner will not
  schedule such items, as with `BasicMath`, the timed root comparison is record-keeping. Its main use is then the new
  baseline that the changes above need.
- **Hosts that run queries in transient children.** A service that freezes schema scopes with local rules and runs
  each request in a query child pays, per query, for every local rule of its scope. The dry run in section 3.6 already
  shows that cost in bytes and its cause, before any timing:
  - the header-instantiation fix removes most of the engine's two thirds;
  - trimming the library's per-run bookkeeping would address the other third;
  - demand-indexed local rules would remove nearly all of the per-rule cost, at larger cost and risk.

  Whether demand-indexed rules are needed after the fix depends on the time per idle rule, which only an idle machine
  gives. Q10 also says whether sibling scopes run plans made on another scope's statistics, which decides per-context
  replanning in [colored-context deferred features](./colored-context-deferred-features.md).
- **Library users.** The release states no performance figures (`README.md` and `src/Eggnog/README.md` make no timing
  claim), so nothing published is wrong today. Users would gain two numbers that decide how a service uses scopes: the
  latency of a transient child and the cost of a rule.
- **The literature requirement.** Literature B4's time half and G6 are the only checks against the failure mode the
  review found in Easter Egg: per-colour work that grows with the root. The counters already show no root-size-dependent
  work in the measured paths, so the remaining risk is cache effects at 2,000,000 rows. The expected value of the timed
  half is confirmation, not discovery. It costs only machine time.
- **If it is never done:**
  - root-path changes stay ungated, and any `BasicMath` work cannot show it has finished;
  - the latency of a transient child and the time cost of an idle local rule stay unknown, and demand-indexed local
    rules stay undecided;
  - claims of parity with the Rust stay frozen at a figure from before the store-backed root.

## 5. Cost and risk

- **Effort: medium for the scope recommended here.** The full scope of the earlier draft would be medium-large.
  - **Code.** Two library benchmark classes, one engine class for busy-parent catch-up, one release test, a comparison
    script, and adding net8.0 to the benchmark app if net8.0 stays a target.
  - **Review.** Each piece goes through the project's review practice:
    - review from several angles: workload realism, result checks, allocation accounting and API use;
    - a mutation review of the checks;
    - verification on both target frameworks.

    That review roughly doubles the writing time.
  - **The engine item.** Q12 is a small change in one method and its header evaluator, but it is on the root's path.
    It needs the full test suites on both frameworks and the root gate.
  - **Deferred classes.** They are not counted. `EGraphConstructionBenchmarks` at `2fd4a18` is dropped. It would have
    needed a backport into the old project and API to answer a question that allocation already answers (section 3.3).
- **Campaign length, estimated from the run lengths in section 3.2.** Phase 0 refines it from `--list flat` and a dry
  run.
  - **The root set.** One pass of the 129 cases under the default job takes about 15-20 minutes, plus a process start
    per class. Two rounds of baseline, candidate, candidate, baseline are eight passes: about 2.5 hours.
  - **The bridge.** Run three builds in the order A B C C B A, for two rounds, with two warm-up iterations and fifteen
    measured iterations. That is about 2 minutes per `math` run and 4.5 minutes per `ac` run: about 1.3 hours.
  - **The example mode against the Rust.** Five pairs each: about 10 minutes.
  - **Night 1** holds those three, about 4 hours, in priority order.
  - **Night 2** holds the rest:
    - the context classes and the library classes on four configurations;
    - the candidate builds (Q12, and performance parity's port);
    - the ETW profiles.

    A third night may be needed if inconclusive cases repeat.
- **Bisection is conditional.** It costs one build of each engine-changing stage commit (section 3.2), only for
  confirmed failures, and only if the owner will act on the result.
- **Complexity: low** for benchmarks. Q12 is local to header instantiation.
- **Risk to correctness.**
  - **Benchmarks.** None in the product. The risk is benchmarks that measure the wrong thing. The guards:
    - the project's rule that every benchmark checks its result against a model or known counts, so that a broken
      path fails the run;
    - a mutation review of those checks: disable a rule, drop a union, skip a catch-up, or skip the transient child's
      dispose, and each must fail its benchmark;
    - counters asserted in the setups, as `PerChildCostBenchmarks` asserts examined, copied and caught-up rows.
  - **Q12.** It changes when buffers are allocated, not what is evaluated. These suites cover it:
    - the semi-naive differential tests;
    - the context differential tests;
    - the library suites.
- **Measurement risks:**
  - In-process runs share heap, JIT and dynamic PGO state across classes (`src/Eggnog.Benchmarks/README.md`,
    "Running"). Run each class in its own process, as the earlier comparisons did.
  - Code alignment can move small kernels by a few percent between builds.
  - All builds must roll forward to the same installed runtime, so record `dotnet --list-runtimes`.
  - Cases of a few microseconds cannot meet a 2% tolerance on this machine (section 3.2).
  - The parallel cases on a 4-core, 8-thread machine, and short union-find cases, may not repeat within 1%. The
    inconclusive rule in Q1 handles them.
- **Maintenance burden: small.**
  - The library classes use the public API, which is in prerelease and changes additively (rule-layer spec decision
    D28).
  - The engine class uses internal API, which churns more. Each benchmark costs about one build-breaking fix per API
    rename.

## 6. Worth-it evaluation

**Shared settings for every timed row below, and for performance parity's rows run in the same campaign:**

- **Method.** Each class runs in its own process, with builds interleaved:
  - two builds: baseline, candidate, candidate, baseline;
  - three builds: A B C C B A.

  Use two rounds and the ratio of medians per case. Report the spread between same-build runs beside each ratio.
- **Gate.** A ratio of at most 1.02, applied to cases of 100 µs or more. Shorter cases are reported, not gated.
- **Outcomes.**
  - *Pass*: the ratio is at most 1.02 in both rounds, and same-build runs agree within 1%.
  - *Confirmed failure*: the ratio exceeds 1.02 in both rounds, and same-build runs agree within 1%.
  - *Inconclusive*: anything else. Add rounds, up to four in all. If the case is still inconclusive, report it with its
    spread, leave it ungated, and re-run it in the next window.
- **Actions.**
  - A pass makes `main` the new baseline for that case.
  - A confirmed failure becomes a roadmap item naming the case and, if bisected, the stage.
  - An inconclusive case is re-run in the next window.
- **Profiler.** ETW kernel sampling through PerfView or Windows Performance Recorder, with the JIT's symbols. It needs
  administrator rights. EventPipe's sampler is not used, because it stops threads at safe points
  ([performance parity](./performance-parity.md), section 6).
- **Launch matrix.**
  - Root and bridge classes: net10.0 under server GC, as the baselines were built.
  - Library classes: `dotnet run -c Release -f net10.0` and, while net8.0 stays a target, `-f net8.0`, each with and
    without `DOTNET_gcServer=0`. That is four processes per class, or two without net8.0.

| # | Question | Cheapest decisive measurement | Metric and threshold | Idle machine? | Preliminary verdict |
|---|---|---|---|---|---|
| Q1 | Did the changes since `3a6e0b7` cost the root more than 2% anywhere other than `BasicMath`? | The shared method, two builds, each root class (129 cases). Bisect over the engine-changing stage commits (section 3.2) only for confirmed failures, and only if the owner will act. | The shared gate and outcomes. | Yes | Do it. It records the baseline at `main` that later changes need. `BasicMath` is expected to fail; the others are unknown. |
| Q2 | Did the store-backed root cost whole programs more than 2%, and where do they stand against the Rust? | (a) `BridgeMath` and `BridgeAc` at `673495d`, `2fd4a18` and `main`, with the shared three-build method, `--warmupCount 2 --iterationCount 15`. (b) `--bridge-example math` and `ac`, alternated with the Rust `math.exe` and `ac.exe` rebuilt at the pinned submodule (toolchain 1.91.0), five pairs each, median of the warm repetitions. | (a) `main` ÷ `2fd4a18` under the shared gate: this is colored-contexts spec section 21.11's gate. `main` ÷ `673495d` answers performance parity's whole-program question, using that plan's thresholds (≤ 1.02 local to the microbenchmark; > 1.03 supports the branch work). (b) C# ÷ Rust is reported beside 6.6 s/7.4 s and 15.5-16 s. Above 1.10 on either, profile that program (performance parity's threshold). It is a figure, not the gate. | Yes | Unknown. Allocation moved -1.9% (`math`) and +0.6% (`ac`), but allocation does not predict time (section 3.2). Cheap to run. |
| Q3 | Is `RootContext` within noise of `RootApi`, and does building an `EGraph` matter? | `ContextExtractionBenchmarks` timed at `main` (the class first exists at `8444a46`, so the ratio is within one build). Construction: none beyond section 3.3's allocation counts. | `RootContext` ÷ `RootApi` ≤ 1.02 at `main`. The spec's construction gate is closed by allocation (6.2 KB engine, 56 KB library, less than one child of section 3.4), subject to the owner (section 9). | Yes for time | Low stakes. Run it on night 2. |
| Q4 | Is per-child time flat within 1.5x from 20,000 to 2,000,000 rows (literature B4)? | `PerChildCostBenchmarks` timed, both methods, frozen and not. | Time(2M) ÷ Time(20k) ≤ 1.5 for each method. | Yes | Expected to pass, because the counters are identical across sizes. A failure would come from cache misses in the rebuild-index probes, and would itself be a finding. |
| Q5 | How do case splits compare with clones in time (literature B5)? | `CaseSplitMemoryBenchmarks` timed (no new code). | Contexts' time per case flat in root size. The time ratio of clone to context is reported. Failures are not possible in this workload. | Yes | Run it. Defer "failures under a timeout policy" until a deep-split workload exists (section 9). |
| Q6 | What are the bytes of each per-child structure? | A heap snapshot by type, taken with `dotnet-gcdump`, of a process holding 1,000 live children of the bridge workload. Map types to structures. | Bytes per structure per child; the total agrees with the footprint mode within 5%. | No | Later. The total is flat and small, and section 3.6 shows that per-rule cost, not child structure, dominates a query child. |
| Q7 | What does catch-up under a busy, writable parent cost? | **Now:** a new engine class, `BusyParentCatchUpBenchmarks`. A writable root has k children (1, 8, 64), each with one union. The parent then appends A rows (100, 10,000) to a table whose rows name the children's losers in a set fraction, and every child rebuilds. Count `CatchUpRowsExamined` and `RowsCopied`. Also count, in a test, `CatchUpRowsExamined` against the executor's scanned and index rows per split step in domain splits on `PredicateTheory` (`src/Eggnog.Tests/Predicates/`) under a writable query scope, at 3 and 8 cases. **Later:** time the engine class in ns per examined row. | Counters: examined = k × A exactly, asserted in the setup. Decision by counts: if catch-up's examined rows exceed 25% of all rows a split step examines (catch-up plus rule scans plus index rows) at 8 cases, open an engine item (catch-up through the rebuild index, or by losers). Otherwise close engine-map section 8.2's risk with the figures, converted to time on night 2. | Counters no; time yes | Write the counters now. Expected small for splits whose lifts write few rows, since a lift writes one row per lifted fact. |
| Q8 | What does a transient query child cost, and what does an idle local rule cost per iteration? | `QueryScopeBenchmarks` over the public API, as in section 3.6's workload, with two-atom and four-atom rule bodies. Vary touched tables (10, 30, 50), local rules (0, 10, 100, 1,000), and idle or active. Report time per query (p50 and p99), allocation, and the slope per rule. Use the launch matrix. | Latency of a query child: reported, not gated. Idle-rule overhead, measured after Q12: time per query at 100 idle rules minus time at 0 rules, which includes every iteration. Build demand-indexed local rules if that overhead exceeds 25% of the query's own time at 0 rules, at p50 or at p99; otherwise record and drop. This is [performance parity](./performance-parity.md)'s row 3, and both plans use it. Allocation is answered now: about 1.5 KB per idle rule per iteration on the synthetic two-atom workload, with a slope that depends on rule shape. | Yes for time | Do now (the code). The time decides demand-indexed local rules, beyond what Q12 removes. |
| Q9 | What does the library add per run at the root? | `LibraryRunOverheadBenchmarks`: a library `Run` at the root against the engine's `RunRules` on the same lowered rules, at 10 and 100 rules. | Library time ≤ 1.05 × engine time at 10 rules. Above that, trim the per-run bookkeeping of section 3.6 (the description-keyed lookups and the per-run statistics objects). | Yes for time; bytes no | Do now. The other path classes (`ScopeCatchUp`, `MirrorSync`, `EqualStep`, `SplitStep`) wait until a change touches those paths. Nothing waits on their figures, and their thresholds would have no follow-up today. |
| Q10 | Do rules planned on the first frozen schema scope serve later sibling scopes badly (rule-layer spec residual risk 4)? | In `QueryScopeBenchmarks`: the fixed corpus of sibling schema scopes that `ScopedQueryCases.CompileAll` builds on the test fixture ([colored-context deferred features](./colored-context-deferred-features.md), B), fixed before any measurement and frozen in both orders, with `EGraphOptions.RecordPlans`. The plans can be compared in a test now; timing them in the benchmark app needs the fixture there (question 11). Compare the recorded plans now, and time queries under each later. | If the plans differ by freeze order and a later scope's query is ≥ 2x slower than under its own first freeze, the figures go to per-context replanning in [colored-context deferred features](./colored-context-deferred-features.md) and to freeze planning control in [rule-layer split features](./rule-layer-split-features.md). | Plans no; time yes | Do it in the same class as Q8. |
| Q11 | Do 100 sibling frozen scopes with local rules release their memory? | A test beside `ScopeChurnTests` of the interning plan, on `PredicateTheory`. Record a reference state, then create 100 siblings with 100 local rules each, freeze, query each, and dispose all. Then run a second pass over the same keys and compare the two. | `WeakReference`s to the scopes and their compiled variants collected. `VariantsByDescription.Count` and the engine's `RuleCount` return exactly to their values before the siblings. The retained heap after the second pass differs from after the first by no more than the newly interned values (`InternedCount` delta). | No | Do now; small. |
| Q12 | Can idle variants stop allocating in header instantiation? | In `PlanTemplate.InstantiateLayered` and `ContextView.EvaluateLayers`, either evaluate the extra (timestamp) constraints before allocating the per-run arrays and lists, or take those buffers from a per-thread pool and copy into exact-size arrays only when every header is non-empty. Count with the section 3.6 probe at the root and in a query child. | Bytes: the engine's share of idle-rule allocation falls by at least 90%, so idle-query bytes at 100 rules fall by at least half. Tests: every suite passes on both frameworks. Time: the root set, `main` with the change against `main`, under the shared gate on night 2. Keep the change only if no case is a confirmed failure. | Bytes no; time yes | Do now. The cause is known, the change is local, and it removes allocation that every idle variant discards, at the root and in children, whatever Q8's timing shows. |

`TheoryDefinitionBenchmarks`, the time to define a theory on a new graph, is deferred until a host's graph rebuild
needs a figure for it: for example, a host that retires its graph on the usage gauge of the
[interning plan](./interning-and-value-lifetime.md) more often than the horizons there suggest.

**A cheaper signal before the idle window.** Instruction counts are much less sensitive to a busy machine than wall-clock
time. The benchmark app does not reference `BenchmarkDotNet.Diagnostics.Windows`, which BenchmarkDotNet's hardware
counters (`InstructionRetired`) need on Windows. Those counters also need administrator rights, and whether they work
with the in-process toolchain is untested. Phase 0 tries them, and falls back to process CPU time for serial cases.
The result is an early signal for Q1 and Q12, never a replacement for the timed gate.

**Verdict.**

- **Now:** Q12, Q7's counters, Q8's and Q9's benchmark code, Q10's plan comparison and Q11.
- **At the first idle window:** Q1, Q2, Q3-Q5, and the timed halves of Q7, Q8, Q10 and Q12.
- **Deferred:** Q6, literature B5's failure half, `TheoryDefinitionBenchmarks`, and the other library path classes.
- **Dropped:** the per-stage gates. A cumulative comparison plus conditional bisection gives the same answer for every
  stage that matters, at a fraction of the builds.

## 7. Plan if it goes ahead

### Phase 0: comparison method (no idle machine)

- **Entry:** owner agreement on section 9's questions 1-4.
- **Work:**
  - Write the procedure into `src/Eggnog.Benchmarks/README.md` ("Comparing two builds"):
    - Build each baseline in a separate worktree. `3a6e0b7`, `673495d` and `2fd4a18` build
      `src/Eggnog.Relations.Benchmarks`; commits from `f2bfea2` on build `src/Eggnog.Benchmarks`.
    - Pin the runtime, and close other applications.
    - Follow the shared method, gate, outcomes and launch matrix of section 6.
  - Add a comparison script. It reads the exported `*-report.csv` files and prints ratios, spreads and each case's
    outcome. Keep it beside the app if the owner wants it in the repository.
  - Estimate the campaign from `--list flat`, the case counts and a dry run, and split it into nights by priority:
    Q1 and Q2 first.
  - Probe the instruction-count and CPU-time signal described in section 6.
- **Exit:** a dry run of the procedure (`--job dry`) on every build produces matched case lists for every root and
  bridge class.

### Phase 1: new benchmarks, the release test and the engine item (no idle machine)

- **Entry:** Phase 0 done. Q12 may start at once, because it does not depend on Phase 0's script.
- **Work:**
  - Library classes under `src/Eggnog.Benchmarks/Library/`, in their own namespace, over the public API:
    `QueryScopeBenchmarks` (Q8, Q10) and `LibraryRunOverheadBenchmarks` (Q9).
  - The engine class `Relations/Contexts/BusyParentCatchUpBenchmarks`, and the counting test over domain splits on
    `PredicateTheory` (Q7).
  - The release test of Q11, coordinated with the interning plan's `ScopeChurnTests`. Whichever lands first holds both.
  - Q12's change in the engine, with the probe's figures before and after.
  - Each benchmark checks its result in its setup or in every invocation: match counts, the facts that must hold, and
    the counters that must equal a formula.
  - If the package keeps targeting net8.0 (synthesis question 3), add net8.0 to the benchmark app's target
    frameworks, at least for the library classes, because the library targets net8.0.
- **Review:** the project's practice applies:
  - implement, then review from several angles: workload realism (rule shapes, touched tables and the number of local
    rules), result checks, allocation accounting, and API use;
  - a mutation review of the checks (section 5);
  - fix, then verify on net8.0 and net10.0: dry runs of every class, and the full test suites on both frameworks
    (they must pass for Q12).
- **Exit:**
  - Every new class passes `--job dry` on both frameworks.
  - These figures are recorded: Q7's counters, Q10's recorded plans, the allocation slopes of Q8 and Q9 (for two-atom
    and four-atom rules), and Q12's bytes before and after.
  - Q8's allocation figures, with and without Q12, go to the owner at this point.

### Phase 2: the campaign (idle machine)

- **Entry:**
  - Phase 1 merged, except Q12, which waits on its own gate.
  - A window of two nights.
  - The machine idle (no Teams or Edge), with the power plan chosen by the owner.
  - Administrator rights for the ETW profiles.
- **Night 1, in order:**
  1. The root set, `3a6e0b7` against `main` (Q1).
  2. The bridge at `673495d`, `2fd4a18` and `main` (Q2a).
  3. The bridge against the Rust (Q2b).
- **Night 2, in order:**
  1. The candidates against `main` on the root set:
     - Q12's change;
     - performance parity's port of `wip/perf-basicmath`, with `3a6e0b7` as its third build, as that plan's section 6
       specifies.
  2. `ContextExtractionBenchmarks`, then `PerChildCostBenchmarks`, `CaseSplitMemoryBenchmarks`,
     `ManyChildrenBenchmarks` and `ChildFindBenchmarks` (Q3-Q5).
  3. `BusyParentCatchUpBenchmarks` (Q7).
  4. The library classes on the launch matrix (Q8-Q10), with Q12 applied if it passed.
  5. Performance parity's GC-share and writes-against-Rust rows.
  6. ETW profiles of:
     - `BasicMath` at 200,000 rows on `3a6e0b7` and `main`;
     - `math`;
     - a query child at 1,000 idle local rules.
- **Exit:**
  - Every figure is recorded with its method, and every case has an outcome.
  - The new root baseline at `main` is exported and kept.
  - Inconclusive cases are listed for the next window.

### Phase 3: bisect (conditional)

- **Entry:** a confirmed root regression other than `BasicMath`, and the owner's statement that it will be scheduled.
- **Work:** run the failing class only, with the shared method, over the engine-changing first-parent commits of
  section 3.2. Bisect to a commit, then record the item in the roadmap with its stage.
- **Exit:** each regression attributed to a commit.

### Phase 4: report and documentation

- **Work:**
  - A report in the style of [colors-m10-reports.md](../history/colors-m10-reports.md): method, machine, runtime, every
    table, and an outcome per question.
  - Updates to the [roadmap](../roadmap.md):
    - close the gates that passed;
    - record the decisions that follow: demand-indexed local rules (Q8), catch-up indexing or closing engine-map
      section 8.2's risk (Q7), and the evidence for per-context replanning and freeze planning control (Q10).
  - Name the new baseline commit in `src/Eggnog.Benchmarks/README.md` ("The root benchmark set") for future gates.
- **Exit:** the owner has decided each follow-up.

## 8. Dependencies and ordering

- **This workstream depends on nothing else.** It measures the code on `main`.
- **It defines the campaign that other plans use:**
  - [Performance parity](./performance-parity.md) runs its section 6 rows in Phase 2, with the shared method,
    profiler and thresholds:
    - the idle-rule measurement and rule, shared with Q8: overhead per query at 100 idle rules against 25% of the
      query's own time at 0 rules, at p50 and at p99;
    - C# ÷ Rust of at most 1.10;
    - the `673495d` whole-program comparison;
    - the three-build `BasicMath` run.

    Where that plan's text differs, for example three rounds against the Rust instead of five pairs, this plan's
    settings apply to the shared campaign.
  - [Smaller engine items](./smaller-engine-items.md): S6 (the `ProberPiece` shrink) is gated through performance
    parity in this campaign. S5 (lazy `IndexCatalog`) is dropped by that plan, so nothing here waits on it. S3 goes
    ahead without the split-step timings deferred here. S1's memo count would let a future `SplitStepBenchmarks`
    verify that it measures memo hits.
  - [Rule-layer split features](./rule-layer-split-features.md): Q10's results are evidence for freeze planning
    control. Demand-indexed local rules, decided by Q8, belong to performance parity.
  - [Colored-context deferred features](./colored-context-deferred-features.md): Q7 for any work on busy writable
    parents (G8 thaw, parallel cases), Q10 for per-context replanning, and Q4/Q5 as the baseline for pruning or
    flattening hidden sets.
  - [Interning and value lifetime](./interning-and-value-lifetime.md): Q11 sits beside that plan's `ScopeChurnTests`,
    and both are counts. Its first-block experiment needs this campaign's method for its latency half.
  - [Parallelism test plan](./parallelism-test-plan.md): the parallel cases of the root set are timed here. Its stress
    tests are correctness work and do not wait on this.
  - [Conformance, docs and release](./conformance-docs-release.md): the report, the README updates and the roadmap
    citation fix of Phase 4.
  - [Compiled plans](./compiled-plans.md) recommends Drop. If it is revived, its profile questions read Phase 2's ETW
    profiles.
- **Ordering:**
  - Phases 0-1 and Q12's code now.
  - Phase 2 before any root-path change lands: Q12, performance parity's fixes, or S6.
  - Phase 3 only on a confirmed failure that the owner will schedule.

## 9. Questions for the owner

1. **Acting on root failures.** If the cumulative comparison confirms a regression other than `BasicMath`, will it be
   scheduled? If not, should Phase 3's bisection be skipped and the failure only recorded?
2. **Per-stage gates.** Can the per-stage gates of colored-contexts spec section 21.11 be replaced by one cumulative
   comparison of `3a6e0b7` against `main`, plus the bridge at `2fd4a18` against `main`? Bisection would follow only
   where a class fails. The cumulative comparison also covers the bridge port merge (`2d0148e`) and the release
   preparation (`ea32185`).
3. **The idle machine.** What counts as idle: an unattended night on the i7-7700K with other applications closed, or
   a dedicated runner? Should the High performance power plan be selected for the campaign? The app keeps the current
   plan. Are administrator rights available for ETW?
4. **Tolerance and outcomes.** Is 2% applied only to cases of 100 µs or more? Is the inconclusive rule acceptable:
   up to four rounds, then report and leave ungated?
5. **The new baseline.** After the campaign, do future root gates compare against `main` as measured, with
   `BasicMath`'s regression recorded as open, rather than against `3a6e0b7`?
6. **Q12.** May the header-instantiation change go ahead now, landing after its root gate on night 2?
7. **Construction.** Can the spec's "cost of building an `EGraph`" gate be closed by allocation (6.2 KB engine, 56 KB
   library) without a timed backport to `2fd4a18`?
8. **net8.0.** If the package keeps targeting net8.0 (synthesis question 3), may the benchmark app target net8.0 as
   well as net10.0, so the library's net8.0 target is measured?
9. **Literature B5's failure half.** Is it deferred until a deep-split workload exists, such as a TheSy-style suite?
   The current case-split workload cannot time out.
10. **Where results live.** A report under `docs/` like the colored-context reports, tables in
    `src/Eggnog.Benchmarks/README.md`, or both? Are BenchmarkDotNet's exported reports kept in the repository?
11. **The predicate fixture in benchmarks.** Is linking `PredicateTheory.cs` and `Pred.cs` from
    `src/Eggnog.Tests/Predicates` into the benchmark app acceptable, or should they move to a shared source folder?
    Compiled plans' Q1, Q3 and Q5 time query children of that fixture. Q10's timing half also needs the corpus that
    `ScopedQueryCases.CompileAll` builds, whose `Assert` checks would have to move out before it can be linked.

## 10. Review

A skeptical review raised fifteen problems. Each was checked against the repository and the sibling plans.

**Changed:**

- **Baseline commits.** The plan cites the baselines by their commits on `main`, `3a6e0b7` and `b041e70`.
- **Bridge gate.** The 2% gate for the store-backed root was replaced by a Rust ratio, and that ratio was wrongly
  called "the ratio measured before". Q2 now gates `main` against `2fd4a18`. The bridge sources are identical from
  `673495d` to `2fd4a18`, and differ from `main` only in comments and a usage string. Q2 also folds in performance
  parity's `673495d` comparison. C# ÷ Rust is a reported figure, with performance parity's 1.10 action threshold.
- **Inconclusive outcome.** Section 6 now defines pass, confirmed failure and inconclusive (up to four rounds, then
  report and leave ungated), with an action for each. Bisection now waits on the owner's commitment (question 1).
- **Idle-rule allocation.** It is now phrased as a slope on a synthetic two-atom workload that depends on rule shape.
  The header-instantiation fix is a new item, Q12. Allocation counts verify it, and the root gate times it. Q8 keeps
  the timing for demand-indexed rules beyond the fix.
- **One campaign definition.** The plan now has one baseline, ETW instead of EventPipe, and performance parity's
  thresholds (the shared idle-rule measurement, C# ÷ Rust ≤ 1.10). Performance parity's rows are folded into Phase 2.
  Compiled plans is no longer counted as value.
- **Scope cut to medium.** The other path classes, `TheoryDefinitionBenchmarks` and `EGraphConstructionBenchmarks`
  are deferred or dropped. Q7's 25% threshold is now decided by counts, which a test can take, and is converted to
  time later.
- **Allocation as a predictor.** The plan no longer uses it. Q2's verdict is "unknown", and `BasicMath` is cited as
  the counter-example.
- **Benchmark count.** It is now 44 (`git grep -c "\[Benchmark"`). The root set's 129 cases are added.
- **Citations.** The two engine items are cited to the roadmap's "Smaller engine items", and the 1.5x factor to spec
  section 21.11 and the roadmap.
- **Bytes per child.** The range is now 95.9-96.3 KB.
- **Milestone labels.** They appear only in a table attributed to spec section 21.11, beside commits and their content.
- **B labels.** The plan writes "literature B4/B5" throughout.
- **Launch matrix.** It is stated in section 6.
- **Q11 conditions.** It now asserts exact counts against the state before the siblings (`VariantsByDescription.Count`,
  `RuleCount`), and measures the heap on a second pass.
- **Campaign length.** It is estimated from earlier run lengths (section 3.2) and split into nights by priority.
- **Cheaper signal.** A Phase 0 probe of instruction counts or CPU time is added, as an early signal only.
- **Links.** All sibling plans linked in section 8 exist and were checked.

**Rejected:** none. Three points were found to go further than the review stated:

- The bisection list in the previous draft included the scope and case-split commits (`d8b0349`, `64cd651`), which
  change no engine source, and omitted the bridge port merge (`2d0148e`) and the release preparation (`ea32185`),
  which do. Section 3.2 now lists the engine-changing commits.
- Lazy `IndexCatalog` no longer waits on this campaign, because its plan drops it.
- `ContextExtractionBenchmarks` first exists at `8444a46`, so Q3 is a ratio within one build, not a comparison with
  `2fd4a18`.
