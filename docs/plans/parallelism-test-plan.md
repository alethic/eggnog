# Parallelism test plan

Key: `parallelism-test-plan`. Roadmap entry: [Parallelism test plan](../roadmap.md).

## 1. Summary and recommendation

**Recommendation: Do now**, as a small slice. **Confidence: medium-high.**

Most of the roadmap item is either done already or not worth building now:

- **Serial against parallel with lattice merges that change values.** Targeted tests above egglog's thresholds
  already cover this at the root (section 3.2, item 1). A separate suite adds nothing, so drop it.
- **Runs above the thresholds at one thread and at N.** These are covered site by site. For this plan, a mutation
  comparison ran in a private clone, with no timing (section 3.4). It seeded seven behavioural defects into the
  parallel paths, including egglog's parallel-only defects 3 and 4. The default suite caught all seven. An eager
  mode, with every threshold at zero, also caught all seven. It caught two of them with small programs where the
  default suite needed a bulk program, but it found nothing that the default suite misses. So an eager switch, its CI
  combination and new test modes are not worth building now. They become worth building when someone rewrites a
  parallel kernel and the new kernel has no targeted tests of its own (section 8).
- **Stress of concurrent children against serial runs.** This rests on fixed seeds. The cheap step is a time-boxed
  soak variable that draws fresh seeds and child counts in the existing concurrency tests. A general harness that
  compares random programs with serial replays is deferred until a mutation shows that the targeted tests miss
  something.

What to do now:

1. Qualify design decision 4 in [`../engine/decisions.md`](../engine/decisions.md) for merges that are not
   associative. `MergeFunction` and the root differential's generator already document this limit, but decision 4
   leaves it out.
2. Add a fresh-seed soak variable to `ConcurrentChildRulesTests`, `ConcurrentChildrenStorageTests` and the library's
   `ConcurrencyTests`, as the other differentials already have.
3. Record the eager-mode recipe and the mutation list of section 3.4. A later change to a parallel kernel can then
   repeat the comparison.

Size: small. It changes no production code and needs no idle machine.

## 2. What it is and why it was planned

The plan comes from the engine's build order. The parallelism row of
[`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), section 6 "Build order", lists the parallel
operations (delete, insert, rehash, index build, rebuild, rule-level and morsel parallelism, concurrent children of a
frozen base). It tests them with "serial against parallel equality using lattice merges *that change values*
(egglog's parallel tests never exercise this branch); `run_serial_and_parallel` above the thresholds at 1 and N
threads; stress tests with concurrent children compared to serial runs".

Three facts from the design record explain why it was planned:

- **egglog's parallel tests never reach the parallel code.** egglog's `run_serial_and_parallel`
  (`ext/egglog/core-relations/src/tests.rs`, lines 29-37) runs each test in a rayon pool of 1 thread and in one of
  32. Every parallel path is gated on a size threshold as well as on the thread count: 10,000 rows for
  database-level operations and rebuilds, and 20,000 for table operations and index builds (engine map section
  2.11). Those tests are small, so they run the serial code twice.
- **The parallel-only defects are real in egglog.** The engine map's defect list ("8.1 egglog defects: what the port
  does instead") records four defects that occur only in parallel mode:
  - Defect 3: the parallel insert discards the merge output, which gave wrong `+`, `set-union` and subsumption results
    at `-j 32`.
  - Defect 4: the parallel insert reports changes that did not happen, so a `:naive` saturate never ends.
  - Defect 11: `merge_simple` ignores strata.
  - Defect 15: the threshold for full container rebuilds is wrong.

  Defects 3 and 4 were confirmed experimentally. The engine fixes all four ([`../architecture.md`](../architecture.md),
  section 3.2: "The parallel insert stores the merge's output and reports only real changes").
- **Races here were found in design review, not by tests.** Two design reviews of the colored-contexts spec reported
  one race: lazy layer creation from parallel action clones lost writes and changed `ViewGeneration` during a run
  (R1-2; R2-2 is recorded "As R1-2"). A race on the frozen flag in `Store.AddTable` was also "found in review" and
  corrected in the spec (section 4.3). See [`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md),
  section 20 "Review responses" and the corrections table. The spec that records them (commit 5434d1e) predates the
  layered-tables code (2e130f7). Targeted tests now pin both races down:
  `LayerCreationConcurrencyTests.ConcurrentClonesCreateOneLayerPerTableAndLoseNoWrite`, `LayeredParallelRunTests`
  and `StoreTests.ATableAddedWhileTheRootFreezesIsFrozenOrRefused`. The record shows no concurrency defect in this
  code base that reached code and was then found by randomized testing.

The tests enforce design decision 4 ([`../engine/decisions.md`](../engine/decisions.md)), the determinism contract:
"Parallel execution must produce the same view modulo renaming of ids and the same set of facts". The engine map's
section 5.6 "Determinism" says the same ("Parallel mode can differ from it only in id choice and match counts"). The
colored contexts implemented two parts of the parallelism row: concurrent children and the parallelism budget
(colored-contexts spec, section 10.9). The rest of the test plan moved to [`../roadmap.md`](../roadmap.md) as this
item.

## 3. Current state

### 3.1 Where the engine decides to go parallel

`ParallelHeuristics` (`src/Eggnog/Relations/Tables/ParallelHeuristics.cs`) holds egglog's thresholds as `const`
fields, and every decision is `size > Threshold && DegreeOfParallelism > 1`. A degree of 1 also fixes one shard per
table (`ShardData.ForParallelism`, `src/Eggnog/Relations/Hashing/ShardData.cs`). Several parts of the engine use
`ParallelHeuristics.Serial` structurally, to mean "one shard, never parallel":

- layer tables: `src/Eggnog/Relations/Tables/SortedWritesTable.Layered.cs`, which then reads `hash.GetShard(0)`
  directly, and `src/Eggnog/Relations/Layers/TableLayer.cs`;
- a child's union-find table: `src/Eggnog/Relations/Contexts/Database.Contexts.cs`;
- temporary indexes: `src/Eggnog/Relations/Indexes/ColumnIndex.cs` and `TupleIndex.cs`.

Each decision site has targeted tests above its threshold, at one thread and at N:

| Decision site | Threshold | In a child | Targeted tests above the threshold, serial against parallel |
|---|---|---|---|
| `SortedWritesTable.DoInsert` (`src/Eggnog/Relations/Tables/SortedWritesTable.Insert.cs`) | table op, 20,000 | never (layer tables are single-shard and `Serial`) | `SortedWritesTableParallelTests.ParallelInsertMatchesSerial` (30,000 to 60,000 rows with min, max, sum, union-id and assert-equal merges; change reports and union-find compared), `ParallelInsertStoresTheMergeOutput`, `ParallelInsertReportsOnlyRealChanges`, `ParallelCrossShardSortMismatchLeavesTheTableConsistent`; `SortedWritesTableFailureTests`; `BridgeDifferentialParallelMergeTests`; `RootThreadingTests.LatticeMergesOnPoolThreadsAreRefusedToo` |
| `SortedWritesTable` delete and rehash | table op | never | `ParallelDeleteMatchesSerial`, `ParallelRehashMatchesSerial` |
| `SortedWritesTable` rebuild (incremental decision, `RebuildRange`, `RebuildIndex`; `src/Eggnog/Relations/Tables/SortedWritesTable.Rebuild.cs`) | rebuild, 10,000 | never | `ParallelFullRebuildMatchesSerial`; `IncrementalRebuildTests.RebuildingRowsInParallelMatchesSerially`; `TableRebuildTests`; `DifferentialRegressionTests` |
| Index refresh (`src/Eggnog/Relations/Indexes/Index.cs`, `ColumnIndex.Parallel.cs`, `TupleIndex.Parallel.cs`) | index build, 20,000 | ancestors' frozen indexes are built serially (`IndexCatalog.SetSerialRefresh`) | `IndexPropertyTests` (its harness also calls `MergeParallel` directly), `HashIndexTests.BasicUpdates` |
| `Database.MergeAllCore` (`src/Eggnog/Relations/Database/Database.Merge.cs`) | database level, 10,000 | never (`parallel = false` when layered) | `DatabaseMergeTests.ParallelStrataAgreeWithSerialMerging` and its union-find variant |
| `Database.RunOnTablesCore` (`src/Eggnog/Relations/Database/Database.Rebuild.cs`) | database level | yes | `DatabaseRebuildTests.ParallelRebuildsAgreeWithSerialRebuilds`, `ParallelRebuildMergesReadDependenciesFirst` |
| Rule sets (`src/Eggnog/Relations/Execution/Database.Execution.cs`) | database level, or `ParallelRuleSetsOverride` | yes | executor property tests in modes `Serial`, `Parallel1` (the parallel executor on one thread) and `Parallel4` (`src/Eggnog.Tests/Relations/Execution/ExecutionTestUtil.cs`); `LayeredParallelRunTests`; `ChildParallelRuleSetTests` |
| Containers (`src/Eggnog/Relations/Containers/ContainerEnv.Rebuild.cs`, `ContainerEnv.Parallel.cs`) | 1,000 per environment; more than 1 environment | n/a (no eq-sort containers in children) | `ContainerDatabaseTests.UnionsOfContainerIdsKeepEveryStructureExactAndCanonical` (degrees 1 and 4, large programs); `ContainerEnvTests` |

The thresholds cannot be overridden. The only override is `Database.ParallelRuleSetsOverride`, which forces the
parallel rule-set executor. So randomized differentials reach the table, index and rebuild paths only through
programs large enough to cross the thresholds:

- the bulk bridge programs (`src/Eggnog.Tests/Relations/BridgeDifferential/BridgeDiffGenerator.cs`);
- `CongruenceTests.BulkProgramsAgreeWhenParallel`;
- the root differential's big loads.

The bridge differential (`src/Eggnog.Tests/Relations/BridgeDifferential`) compares the semi-naive and naive bridges
with a naive oracle. With parallel settings, both bridges run at four threads. One program in eight uses parallel
settings, and so do half of the bulk programs (`BridgeDifferentialTests.RandomProgramsAgree`, `BulkProgramsAgree`).
Every merge kind it generates is a lattice (`BridgeDiffFunctionKind`).

### 3.2 The three roadmap items against the code

1. **Serial against parallel with lattice merges that change values: covered at the root.**
   `BridgeDifferentialParallelMergeTests.ParallelInsertsThatMergeAgree` stages more than 20,000 conflicting rows into
   min and max tables, with subsumption. It then stages 84,000 writes that change nothing. It checks the change flags
   against the serial naive bridge and the oracle. `SortedWritesTableParallelTests` does the same at the table level
   for five merges. In a child the question does not arise, because a child's tables never merge in parallel
   (section 3.1). A child's parallel rule tasks stage into serial merges, and `LayeredParallelRunTests` covers that
   with a lattice.
2. **Runs above the thresholds at one thread and at N: covered site by site** (section 3.1), but not systematically.
   Two things are missing:
   - (a) The parallel table, index and rebuild algorithms never run at one thread, because a degree of 1 never
     parallelizes and gives one shard. The rule-set executor does have a one-thread parallel mode (`Parallel1`).
   - (b) No randomized whole-program differential reaches the table, index and rebuild paths on small programs. At
     library level, the parallel-equivalence tests repeat egglog's weakness. `ParallelEquivalenceTests`
     (`src/Eggnog.Tests/Concurrency/ParallelEquivalenceTests.cs`) runs the Cosmos programs at degree 4, but its
     `ForceParallel` sets only `ParallelRuleSetsOverride`. Those programs are below the thresholds of 10,000 and
     20,000 rows, so their table, index and rebuild paths run serially in both runs.

   Section 3.4 measures what (b) costs in practice.
3. **Stress of concurrent children against serial runs: fixed seeds only.** The tests that exist:
   - `ConcurrentChildRulesTests` (`src/Eggnog.Tests/Relations/Bridge/ConcurrentChildRulesTests.cs`) runs 16 children
     of a frozen root on 8 threads through `ConcurrentChildren`. It has six `InlineData` seeds and a program
     generator that already takes a `Random`. It compares each child with a serial replay modulo renaming, and the
     root with itself bit for bit.
   - `ConcurrentChildrenStorageTests` (`src/Eggnog.Tests/Relations/Contexts/ConcurrentChildrenStorageTests.cs`)
     compares 16 children driven by random storage programs (grandchildren, freezes, disposal) with the reference
     context, on three seeds.
   - `ConcurrentChildParallelRuleSetTests` and `ConcurrentSiblingTests`.
   - At library level, `ConcurrencyTests` (`src/Eggnog.Tests/Concurrency/ConcurrencyTests.cs`) runs 32 queries under
     one frozen container, compares them with serial runs, and also runs split queries on parallel threads.
     `ScopeThreadingTests` covers freezes, disposal and definitions under load.

   None of these is time-boxed or draws fresh programs. The soaks do both (`EGGNOG_BRIDGE_DIFF_SOAK`,
   `EGGNOG_CONTEXT_SOAK`, `EGGNOG_DIFF_SOAK`). The rule-in-context, scope, split and lift differentials run serially.
   `ColorRunner` (`src/Eggnog.Tests/Relations/Colors/ColorRunner.cs`) builds `new ColorSchema()`. The library
   drivers build `new EGraph()`, whose default degree is 1: `src/Eggnog.Tests/Differential/EggnogDriver.cs`,
   `src/Eggnog.Tests/Differential/Scoped/ScopedDriver.cs` and the split oracles.

CI (`.github/workflows/Eggnog.yml`) runs the whole suite on net10.0 in five jobs: win-x64, linux-x64, linux-arm64,
osx-x64 and osx-arm64. The `include` entry with `counters: '0'` matches the linux-x64 combination exactly, so GitHub
Actions merges it into that job instead of adding a sixth. linux-x64 therefore runs only with counters off. The
concurrency tests run on two arm64 platforms. net8.0 runs only locally
([`../engine/conventions.md`](../engine/conventions.md), "Building and testing").

### 3.3 Dry run: every threshold at zero

A private clone of `eda0c61` was patched so that an environment variable makes every `ParallelHeuristics` decision
ignore its size threshold. The patch kept `DegreeOfParallelism > 1`, so `ParallelHeuristics.Serial`, layer tables
and every degree-1 path are unchanged. The run used a Release build on net10.0, on a loaded machine.

- **Engine tests**: 11,805 tests, 14 failures.
  - 3 failures are by design. `ConstraintTests.ParallelHeuristicsUseEgglogsThresholds` and two cases of
    `ContainerEnvTests.TheFullRebuildGoesParallelOnlyAboveTheIntraContainerThreshold` assert the thresholds.
  - 11 failures come from **the non-associative `KeepIncoming` merge at the root**: seven cases of
    `SortedWritesTableModelTests.MatchesTheFoldModel` with `keep-incoming`, and four `DifferentialTests` cases. Their
    "sharded" tables use `ParallelHeuristics.Default` (every processor), so with zero thresholds they took the
    parallel insert. One failure shrank to two steps. The first loads `t1 [3, 12, 26]`. The second loads
    `[3, 24, 22]` and `[3, 13, 26]` in one batch into a table with a `KeepIncoming` merge. The serial fold changes
    the row twice and reports a change. The parallel path folds the batch first, merges the result into the stored
    row, and reports nothing. The stored values can also differ.
  - No failure involved a lattice merge. None involved `keep-current` or `sum` either: these are not lattices, but
    they are associative.
- **Library tests**: 550 tests in the rule-layer, scope, split, case, Cosmos, rules, scheduling and concurrency
  namespaces. In this run graphs also defaulted to degree 4, and children of frozen scopes inherited it. One test
  failed, by design: `ScopeTreeTests.ScopeOptionsReachTheContext` asserts the serial default.

**The KeepIncoming divergence is a known limit, not a new finding.** `MergeFunction`
(`src/Eggnog/Relations/Tables/MergeFunction.cs`) says the parallel path "agrees with the serial order only for
associative merges; and the order of batches from different threads is not fixed". The root differential's generator
keeps `KeepIncoming` tables single-sharded under big loads for this reason: the result "legitimately differs from the
serial fold the model replays" (`DiffGenerator.RandomTable`, `src/Eggnog.Tests/Relations/Differential/DiffGenerator.cs`).
egglog behaves the same way (engine map section 2.3).

The case cannot reach library users:

- The engine is internal: there is no public type under `src/Eggnog/Relations`.
- The library creates only lattice merges. `src/Eggnog/Graph/EGraph.Definitions.cs` uses `AssertEq`, `UnionId` and
  `.AsLattice()`, and `src/Eggnog/Relations/Bridge/MergeFn.cs` makes `AssertEq` and `UnionId` lattices.
- Decision 2 confines non-lattice merges to the root.

The only thing wrong is the wording of decision 4, which omits the limit that `MergeFunction` and `DiffGenerator`
document.

### 3.4 Mutation comparison: does eager mode catch what the default suite misses?

This experiment decides whether an eager mode is worth building, and it needs no timing. It used a private clone of
`eda0c61` with the dry-run switch of section 3.3. Each mutation sat in the parallel branch only and was switched on by
an environment variable. The whole suite then ran twice for each mutation, with `-stopOnFail`, so only the first
failing test was recorded:

- (a) with default settings;
- (b) with every threshold at zero. For this run the clone also kept `KeepIncoming` tables single-sharded in
  `DiffGenerator`, skipped the `keep-incoming` cases of `MatchesTheFoldModel`, and excluded the two threshold
  assertions.

With no mutation, run (b) passed all 12,437 tests it ran.

| Mutation (parallel branch only) | Site | (a) Default suite: first failing test | (b) Eager: first failing test |
|---|---|---|---|
| egglog defect 3: keep the incoming row, drop the merge output | `SortedWritesTable.MergeAppendedRows` | `SortedWritesTableFailureTests.LargeShardsFoldInSeveralBatches` | `DatabaseRebuildTests.ParallelRebuildMergesReadDependenciesFirst` |
| egglog defect 4: report a change for every staged row | `SortedWritesTable.MergeAppendedRows` | `SortedWritesTableParallelTests.ParallelInsertMatchesSerial("assert-equal")` | `SortedWritesTableModelTests.MatchesTheFoldModel("keep-current", 1, true)` |
| egglog defect 11: merge a round's tables as one stratum | `Database.MergeRound` | `DatabaseRebuildTests.ParallelRebuildMergesReadDependenciesFirst` | the same |
| Skip the cross-shard sort check | `SortedWritesTable.CheckParallelSortValues` | `SortedWritesTableParallelTests.ParallelCrossShardSortMismatchLeavesTheTableConsistent` | the same |
| Do not notify a table that the operation changed | `Database.RunOnTablesParallel` | `CongruenceTests.BulkProgramsAgreeWhenParallel` | `EGraphCongruenceTests.ARandomProgramAgreesWithTheReferenceWithParallelSettings` |
| Drop the last row of the last batch of a parallel tuple-index build | `TupleIndex.SplitBatch` | `IndexPropertyTests.IncrementalRefreshMatchesNaiveGroupBy` (its harness calls `MergeParallel` directly) | `BridgeDifferentialPanicTests.PanicsHappenInTheSameIteration` |
| Skip the last row of the last chunk of a parallel full rebuild | `SortedWritesTable.RebuildFullParallel` | `BridgeDifferentialTests.BulkProgramsAgree` | `BridgeDifferentialPanicTests.PanicsHappenInTheSameIteration` |
| Use the serial incremental-rebuild ratio on the parallel path | rebuild decision in `SortedWritesTable.Rebuild.cs` | not caught | not caught |

Result:

- **No new detection.** The default suite caught all seven behavioural mutations, and eager mode caught none that
  the default suite missed. By the decision rule of section 6, row 2, the eager switch, its test modes and its CI
  combination are not built now.
- **The last mutation is not a correctness defect.** It changes only which of two equivalent paths runs. The code
  states that both paths stage the same writes (`SortedWritesTable.Rebuild.cs`, the `Rebuild` remarks), so only a
  unit test of the decision function itself could catch it. It is a performance choice.
- **Smaller failures.** For two mutations (the dropped notification and the skipped rebuild chunk), the default suite
  needed a randomized bulk program, while eager mode failed on small programs. The shrinkers handle small failing
  programs well (`BridgeDiffShrinker`, and the root differential's shrinker). Eager mode also caught the defect-4
  mutation with a small model test. This makes failures smaller, but it does not catch more.
- **Limit of the comparison.** It covers defects of kinds already known. Eager mode would add detection for a
  rewritten kernel that has no targeted tests. Section 8 ties the switch to such a rewrite.

## 4. Value

- **The contract.** Decision 4's wording does not mention non-associative merges at the root, while `MergeFunction`
  and `DiffGenerator` document the limit. One added sentence makes the decisions file match the code.
- **The Cosmos adapter.** It runs serial graphs (`DegreeOfParallelism` 1, D46;
  [`../history/cosmos-integration.md`](../history/cosmos-integration.md), section 1.1 "Decisions"), so parallelism
  inside an operation does not reach it. It does depend on concurrent children of frozen scopes (section 3.5 "Thread
  safety"). Its production phase requires "32 concurrent plans equal serial plans" (section 5.2, phase 2 row). The
  adapter's own test for that criterion (section 7, item 8) "mirrors `ConcurrencyTests`", which the library already
  has. A fresh-seed soak of the library and engine concurrency tests is useful before that phase, but it is not
  required.
- **Library users who set `DegreeOfParallelism` above 1.** The option is public ([`../architecture.md`](../architecture.md),
  section 2.11). The dry run and the mutation comparison are evidence that lattice behaviour is correct and well
  tested today.
- **Maintainers and performance work.** [Performance parity](./performance-parity.md) plans parallel tuning, pooling
  and SIMD probes. [Compiled plans](./compiled-plans.md) would replace kernels that run in morsels. If either one
  rewrites a parallel kernel, the recipe of section 3.4 (eager switch plus mutations) is the check to rerun. If the
  new kernel lacks targeted tests above its threshold, the switch is built then.

**By how much.** The value is preventive. The mutation comparison measured it for the known defect classes: eager
mode adds no detection today, and it gives smaller failing programs for two of the seven mutations. No concurrency
defect in this code base is recorded as found by randomized testing (section 2). The races that occurred were caught
in design review, and targeted tests now pin them.

**If it is never done.** Users get nothing different. Decision 4 stays inaccurate for an internal case, and the
concurrent-children tests keep their fixed seeds.

## 5. Cost and risk

**Effort: small.**

- The decision 4 qualification is a one-sentence doc edit that cites `MergeFunction`.
- The soak variable is a test-only change in three classes. It needs a time budget read from an environment variable
  (for example `EGGNOG_CONCURRENT_SOAK`, in seconds), a loop that draws a seed, a child count and a thread count, and
  a failure message that prints the seed for replay. The existing soaks follow the same pattern.
- The recipe is a short section in [`../engine/conventions.md`](../engine/conventions.md): the switch, the test
  adjustments it needs, and the mutation list.

**Risk to correctness or performance.** None, because no production code changes.

**Maintenance burden.** None by default, because the soak is opt-in.

**If the eager switch is built later** (section 8), the following costs apply. The previous draft understated them.

- It must not touch `ParallelHeuristics.Serial` or any degree-1 path, because layer tables and temporary indexes
  depend on `Serial` meaning one shard (section 3.1). The dry-run form (thresholds ignored, `DegreeOfParallelism > 1`
  kept) is safe in this respect.
- Tests that assume serial folds must skip, or stay single-sharded, under the switch, as `DiffGenerator` already does
  for big loads. The same applies to the assertions on thresholds and default degrees.
- The default test modes stay as they are. `ExecutionTestUtil.Modes` has three modes today. Adding `Eager` modes to it
  would add about two thirds more executor property-test time to every CI job. Instead, the process-wide switch in one
  CI combination carries the eager coverage.
- The eager suite ran about as long as the default suite on an 8-thread desktop. GitHub's standard runners have fewer
  cores, and the test step's timeout is 60 minutes.

## 6. Worth-it evaluation

| Question | Cheapest decisive experiment | Metric and threshold | Idle machine? | Evidence now |
|---|---|---|---|---|
| Do the parallel paths diverge from serial on small programs? | Zero thresholds across the whole suite (done, section 3.3) | Any lattice-merge divergence means "fix first" | No | No divergence for lattice or associative merges. `KeepIncoming` diverges, as documented |
| Does eager mode catch defects that the default suite misses? | Mutation comparison (done, section 3.4) | Build the eager switch if at least one mutation is caught only in eager mode | No | The default suite caught 7 of 7 behavioural mutations; none was caught only in eager mode. **No go now** |
| Does a failure in eager mode at N threads reproduce at a fixed seed and shrink? | With the switch rebuilt, reintroduce defect 3 and run the bridge differential with eager settings at four threads and a fixed seed, shrinking the failure | Reproduces on every run and shrinks below 10 steps | No | Not measured. The dry-run `KeepIncoming` failures at N threads shrank to two steps, because the divergence was algorithmic. A one-thread mode is built only if this check fails |
| Does a fresh-seed soak find what fixed seeds miss? | Add the soak variable. Then, in a clone, seed mutations that the targeted tests miss, for example in the deferred release of a frozen scope with live children, or in `ConcurrentChildren` budget leasing. Run the soak for 10 minutes on x64 and on an arm64 runner | Build a general random-program harness only if a broader generator catches a mutation that the soak loop misses | No | The targeted tests already catch the known races (R1-2, the `Store.AddTable` flag), so those races cannot tell the two approaches apart |

**Verdict.**

- Do now: the decision 4 qualification and the soak variable. Both are small, and neither touches production code.
- Drop a separate "lattice merges that change values" suite (section 3.2, item 1).
- Do not build the eager switch, its test modes or a CI combination now (section 3.4). Rerun the comparison when
  someone rewrites a parallel kernel.
- Do not build a one-thread eager mode. The executor already has `Parallel1`, the table-level algorithms are
  deterministic per shard, and the dry-run failures at N threads already shrank.
- Do not adopt a systematic concurrency tester such as Microsoft Coyote. It rewrites binaries and adds a dependency,
  and its support for `Parallel.For` and for code based on `Monitor` is not established.

## 7. Plan if it goes ahead

Each step follows the project's practice: implement; review, including a mutation review of the new tests; fix; then
verify with the whole suite on net8.0 and net10.0.

### Step 1: decision 4 (doc only)

- *Work:* in [`../engine/decisions.md`](../engine/decisions.md), row 4, add that non-associative merges at the root
  (for example `KeepIncoming`) may differ between parallel and serial runs in values, timestamps and change reports,
  as in egglog. Cite `MergeFunction`, and mention `DiffGenerator`'s single-sharding rule as the test-side
  counterpart.
- *Alternative, only if the owner prefers it (question 1):* make `DoInsert` take the serial insert at every size for
  tables whose merge is not a lattice. This changes the engine's parallel behaviour relative to egglog for internal
  non-lattice programs, which matters to the parity comparisons with egglog.
- *Exit:* the doc states the limit. No code or test changes.

### Step 2: fresh-seed soak of concurrent children (small)

- *Work:* `ConcurrentChildRulesTests`, `ConcurrentChildrenStorageTests` and the library `ConcurrencyTests` each gain a
  soak test that is skipped by default and runs for as long as `EGGNOG_CONCURRENT_SOAK` (seconds) allows. Each
  iteration draws a seed, a child count and a thread count, and prints them on failure for replay. On the engine
  side, half the iterations also give children a `ContextOptions.Parallelism` of 2.
- *Mutation review:* remove the lock around lazy layer creation (R1-2), and remove the re-read of the frozen flag in
  `Store.AddTable`. Each must fail within the default fixed seeds or within one minute of soak.
- *Exit:* a one-hour soak is clean on both frameworks, on x64 and, run by hand on a CI runner, on arm64.

### Step 3: record the recipe

- *Work:* document the eager-mode recipe of section 3.4 in [`../engine/conventions.md`](../engine/conventions.md),
  "Building and testing", as the check to rerun when a parallel kernel changes: the form of the switch, the test
  adjustments, the mutation list and the `-stopOnFail` runs.

### If the eager switch is built later

- `ParallelHeuristics` reads one `static readonly bool` from an environment variable, as `EGGNOG_COUNTERS` is read.
  Each `Parallelize*` method becomes `(Eager || size > Threshold) && DegreeOfParallelism > 1`. `Serial` is not
  touched.
- Tests that assume serial folds, thresholds or default degrees skip, or stay single-sharded, under the switch.
- CI: add a matrix dimension, for example `mode: [default]`, and an `include` entry
  `{ run: Eggnog.Tests, sys: linux-arm64, tfm: net10.0, mode: eager }`. Because `mode: eager` overwrites an original
  value, GitHub creates a new combination instead of merging the entry into the existing linux-arm64 job, so the
  default arm64 run stays. The step passes `matrix.mode` to the environment, as it passes `matrix.counters`.
- *Exit:* the whole suite is green with the switch on, and every mutation of section 3.4 is caught.

### Benchmarks

None.

### Documentation

- [`../engine/decisions.md`](../engine/decisions.md), row 4 (step 1).
- [`../engine/conventions.md`](../engine/conventions.md), "Building and testing": the soak variable and the recipe.
- [`../architecture.md`](../architecture.md), section 5.2 ("Inheritance and parallelism have their own equivalence
  tests"): mention the soak.
- [`../roadmap.md`](../roadmap.md): remove the item when steps 1-3 land. The task of rerunning the eager comparison
  moves to the plans that rewrite parallel kernels.

## 8. Dependencies and ordering

- **No prerequisites.** Steps 1-3 can start at once and need no idle machine.
- **[Performance parity](./performance-parity.md) and [Compiled plans](./compiled-plans.md).** If either one rewrites
  a parallel kernel (table insert, rebuild chunks, index build, morsel execution), it reruns the comparison of section
  3.4 against the new code. It builds the eager switch if the new kernel lacks targeted tests above its threshold, or
  if a seeded mutation is caught only in eager mode. Compiled plans would add a "compiled" axis to the executor modes.
- **The Cosmos production phase.** The soak of step 2 is useful before it, but not required: the adapter runs serial
  graphs and has its own 32-plan test (section 4). See [Deferred rule-layer and split features](./rule-layer-split-features.md)
  for the Cosmos requests.
- **With [Deferred colored-context features](./colored-context-deferred-features.md).** Two of those features add
  concurrency that the step 2 soak should cover when they land: running a split's cases in parallel, and shared ids
  across sibling cases.
- **With [Interning and value lifetime](./interning-and-value-lifetime.md).** Scoped interning, or values that die
  with a scope, would let disposal race with interning by concurrent siblings. `BaseValuesConcurrencyTests` covers
  that interning today. The library soak of step 2, which disposes scopes under load, is a place to test it.
- **With [Smaller engine items](./smaller-engine-items.md).** The planned fault-injection hook and the split-lift
  cancellation token would each need a concurrent test, and the soak can host both.
- **[Conformance, documentation and release](./conformance-docs-release.md).** The documentation changes of section 7.

## 9. Questions for the owner

1. Non-lattice merges at the root, in parallel. The default is to qualify decision 4 for non-associative merges: a
   doc edit that matches `MergeFunction` and egglog. Or should `DoInsert` merge non-lattice tables serially at every
   size, so that decision 4 holds as written, at the cost of departing from egglog's parallel behaviour?
2. Should the concurrent-children soak run on a schedule (a nightly workflow on linux-arm64), or only by hand?
3. Should CI also run net8.0? The conventions require both frameworks, but the CI matrix runs only net10.0, and
   threading behaviour differs between runtimes.
4. When the eager switch is built, is a process-wide switch read from an environment variable acceptable in library
   code, as `EGGNOG_COUNTERS` is? Or should eager settings reach tests only through internal constructors?

## 10. Review

A skeptical review of the previous draft raised eleven problems. Each was checked against the sources.

Changed:

- **The coverage experiment could not decide the question (accepted).** With zero thresholds, more parallel coverage
  is certain. The experiment was replaced by the mutation comparison the review proposed, and that comparison was run
  (section 3.4). Its result changed the recommendation: the default suite caught all seven behavioural mutations, so
  the eager switch, its test modes and its CI combination are not built now.
- **Eager1 was not one flag, and the dry run did not exercise it (accepted).** `ShardData.ForParallelism` gives one
  shard at degree 1, `ParallelHeuristics.Serial` is used structurally, and layer tables read `GetShard(0)`. The
  one-thread eager mode is dropped. The shrinkability row now asks about eager settings at four threads. The plan now
  mentions the executor's existing `Parallel1` mode.
- **The KeepIncoming "finding" was already documented (accepted).** `DiffGenerator.RandomTable` and `MergeFunction`
  document it. Removed: the claims of an undocumented gap, that no test pins it, and that the dry run paid for itself;
  also the "second model" work item. The divergence is now attributed to non-associative merges, since `keep-current`
  and `sum` passed. The serial-insert alternative now notes that it departs from egglog.
- **The evidence of races here was overstated (accepted).** R1-2 and R2-2 are one race, and both it and the
  `Store.AddTable` race were found in design review of the spec. The plan now says so, names the targeted tests that
  pin them, and states the value as preventive, sized by the mutation comparison.
- **Phase 3's decision rule was inverted, and its Cosmos value overstated (accepted).** The default is now a soak
  variable on the existing tests. A general harness requires a mutation that the targeted tests miss. "Before the
  Cosmos production phase" became "useful before it; not required".
- **The CI matrix was misread (accepted).** The `counters: '0'` include merges into the linux-x64 job, so there are
  five jobs in total. A future eager combination uses a new matrix dimension, so that it does not replace the default
  linux-arm64 job.
- **The CI cost was understated (accepted).** The default modes stay unchanged. If eager coverage is built, it comes
  from the process-wide switch in one combination, and the plan states the cost of extra modes.
- **ParallelEquivalenceTests forces only the executor (accepted).** Added to section 3.2, item 2(b), as evidence that
  the library-level parallel tests do not reach the table, index and rebuild paths.
- **Publication rules (accepted).** Paths are now relative to the repository root. The `ScopedDriver` path and the
  `ext/egglog/.../tests.rs` path (lines 29-37) are fixed. "M11" is replaced by "the parallelism row of the build
  order".
- **The coverage tool was not ready (accepted, now moot).** The coverage measurement was removed.
- **Parallel.For determinism at one thread (accepted, now moot).** The one-thread mode was dropped. The executor's
  parallel path uses `WorkScope` (`ThreadPool.UnsafeQueueUserWorkItem`), not `Parallel.For`.

Rejected or adjusted:

- **"Notify in completion order in `RunOnTablesParallel`" as a mutation (adjusted).** The current code already
  notifies from inside the parallel loop, in completion order. `NotificationList` holds a set of tables, which
  `TakeRound` sorts by stratum and id before merging, so that mutation would change nothing. The comparison used "do
  not notify a changed table" instead.
- **"The value of the eager modes is what (b) catches and (a) misses" (kept, with one addition).** The comparison also
  recorded that eager mode reaches two mutations with small programs instead of bulk ones (section 3.4). The plan
  reports this as a gain in failure size, not in detection, and it does not change the decision.
