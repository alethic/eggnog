# Performance parity and performance work

This plan covers the roadmap's "Performance parity" and "The root `BasicMath` regression" items
([`../roadmap.md`](../roadmap.md), "Engine work"). It also covers four performance follow-ups that are not on the
roadmap yet: staging-buffer growth, write allocation, the parallel writes gap and a separate shard-count option. And it
covers the engine side of the roadmap's "Latency of transient children" item ([`../roadmap.md`](../roadmap.md),
"Benchmarks and timed gates"), the cost of idle local rules, and its conditional follow-up, demand-indexed local rules
([`../roadmap.md`](../roadmap.md), "Engine work", "Idle local rules").

## 1. Summary and recommendation

**Recommendation: Do now, in part; the rest after an experiment. Confidence: medium.**

The plan splits into three parts.

**Do now (small): idle local rules allocate too much.** A count-only probe in
[`./idle-machine-measurement.md`](./idle-machine-measurement.md) (section 3.6) shows that a transient query child under a
frozen schema scope allocates about 1.5 KB per idle local rule per iteration: 165 KB per query at 100 idle rules, and
1.5 MB at 1,000. About two thirds of that allocation happens in the engine before it finds that a rule has nothing new.
`PlanTemplate.InstantiateLayered` allocates its header arrays and lists before it evaluates the first header (§3), and
an idle variant then discards them. The other third is the library's per-rule bookkeeping. The same waste occurs at the
root, at about 1.17 KB per idle global rule per iteration. An allocation-free path for empty headers, plus trimmed
bookkeeping, is small, has strong oracles, and serves every graph whose runs list rules that find nothing new. In the
same pass, at almost no cost:

- count execution-state clones and buffer creations per run;
- size a clone's buffer map up front, which removes the `Array.Resize` frames that the disputed profile figure points
  at.

**Do after an experiment: the throughput items.** The campaign was planned to answer one question: can a C# port of
egglog's engine keep up with the Rust ([`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), §7.4,
evidence E2, and the row M13 of §6)? The only answer on record predates the store-backed root. Warm, egglog-bridge's
`math` example ran in about 6.6 s against the Rust's 7.4 s, and `ac` at parity. The figure was recorded in `b041e70`,
after layered query execution, and the commit that was timed is not recorded. Both sides used every processor. E2 asked
for a comparison against `egglog -j1`, which has never been run. Parity on `main` is therefore unconfirmed. If `math`
had lost a share like `BasicMath`'s, it would sit at parity with no margin.

The write benchmarks, in single runs on a busy machine, gave these ratios of C# to Rust time:

| Threads | Ratio | Result |
|---|---|---|
| 1 | 0.95-1.03 | level |
| 2 | 0.94-1.08 | |
| 4 | 1.07-1.16 | |
| 8 | 1.11-1.17 | |

The root `BasicMath` microbenchmark is 6-29% slower than its baseline, depending on the configuration. The experiment
branch for it has never been measured above the noise. Each of these is a small question that a noise-controlled
idle-machine session can settle. Each fix should be built only if its threshold says so.

**Conditional: demand-indexed local rules.** Build these only if, after the allocation fix, the idle-rule overhead
per query at 100 idle local rules exceeds 25% of the query's own time at 0 rules, at p50 or at p99: the measurement
and the rule are in §6, row 3.

Drop the herbie and eggcc ports. They need a `.egg` reader, which is not planned.

Verdict by item:

| Item | Verdict | Size if built |
|---|---|---|
| Idle-local-rule allocation: an allocation-free empty-header path in the engine, and trimmed per-rule bookkeeping in the library | **Do now** | Small |
| Execution-state clone counters, and a buffer map sized up front in `ExecutionState`'s clone | **Do now** (with the counters) | Trivial |
| Parity campaign as scoped (profiling, SIMD, pooling, tuning; `-j1`/`-jN`; math microbenchmark, herbie, eggcc, a 200,000-row root with 1,000 children) | Reframe as one measurement pass on existing workloads, at every processor and at one thread, plus a library-API port of `math-microbenchmark.egg`. **Drop** the herbie and eggcc ports | Small (measurement); medium per gap found |
| The root `BasicMath` regression | **Do after an experiment**: `main` against `main` with the ported branch, on the root classes the branch touches and on the bridge programs | Medium (port, rewrite comments, review) or nothing |
| Staging-buffer growth (`Array.Resize` under `ExecutionState.CreateBuffer`) | **Do after an experiment**: the counters and the map sizing first. Run an ETW profile only if those leave the question open | Small to medium |
| Write allocation (about 630-650 MB per 4M-row invocation) | **Do after an experiment**. The bytes are already broken down (§3). Prototype two fixes, count bytes now, and time them with a direct A/B under both collectors | Small |
| Parallel writes gap, and the shard count | **Do after an experiment**: one writes row that compares medians on both sides and includes a 16-shard parameter at 1, 2 and 4 threads. The existing 4-thread figures already exceed 10% | Small (measurement) |
| Demand-indexed local rules | **Later**, conditional on the per-query rule in §6, measured after the allocation fix | Medium |

## 2. What it is and why it was planned

**The parity campaign.** The engine map's build order ends with "M13 Performance parity: profiling, SIMD probes,
pooling, parallel tuning", tested against "`egglog -j1` and `-jN` on `math-microbenchmark`, the herbie repros and eggcc
samples; a 200k-row frozen root with 1000 children"
([`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), §6). In that order it comes after compiled plans
(M12). Its purpose is stated in §7.4 of the same document, "Recommendation, and the evidence that would settle it".
Evidence E2 ("C# kernel performance") was to measure the interpreted core against `egglog -j1` on three workloads:
`math-microbenchmark`, a herbie repro and a synthetic 200k-row saturation. The thresholds were:

- the port wins if it is within about 2x interpreted and about 1.3x after compiled kernels;
- the fork question reopens if the port is worse than 3x after compiled kernels.

The design risk "Performance gap" in §8.2 names the same mitigations. The roadmap keeps the item as "Performance
parity" ([`../roadmap.md`](../roadmap.md), "Engine work").

**The `BasicMath` regression.** Colored contexts were gated on keeping the root benchmark set within 2% of a baseline
recorded before any engine change ([`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md), §14.1
"What the root pays", "Gates"). The baseline is commit `3a6e0b7` on `main`. After rules ran in child contexts (layered query
execution, `fb7faff`), `RuleSetRunBenchmarks.BasicMath` measured slower than that baseline. The roadmap summarizes this
as "10-15% slower ... in all four of its configurations" ([`../roadmap.md`](../roadmap.md), "The root `BasicMath`
regression"). The per-configuration figures in §3 show a wider range. The owner deferred the work. Unreviewed
experiments are on the branch `wip/perf-basicmath`.

**Staging buffers, write allocation, the writes gap, shard count.** These came out of the benchmark work: the egglog
divan ports in `src/Eggnog.Benchmarks/Relations/Writes/`, and the sampling profiles taken while investigating
`BasicMath`. The benchmark README records the writes gap and the shard-count difference
([`../../src/Eggnog.Benchmarks/README.md`](../../src/Eggnog.Benchmarks/README.md), "Running" and "Differences from
the Rust"). They fall under the engine map's "pooling" and "parallel tuning".

**Idle local rules and demand-indexed local rules.** On a scope with local rules, every query child runs every local
rule of its scope on every iteration (`ScheduleExecutor.RunIteration` and `EGraphContext.RunRulesCore`, §3), whether or
not the query touches the rule's tables. Nothing measures that cost at 100 or 1,000 local rules. The roadmap asks for
latency figures for transient children, and for the per-iteration cost of idle local rules at 100 and 1,000 rules
([`../roadmap.md`](../roadmap.md), "Latency of transient children"). If that cost is significant, its follow-up is
demand-indexed local rules: skip, in an iteration, every local rule none of whose body tables has rows newer than the
parent's watermark.

## 3. Current state

### Workloads and benchmarks

- egglog-bridge's `math` and `ac` examples: `src/Eggnog.Benchmarks/Relations/Bridge/BridgeMathBenchmarks.cs`,
  `BridgeAcBenchmarks.cs`, with `BridgeMathProgram`, `BridgeAcProgram` and an example mode that prints the Rust's lines
  (`--bridge-example`). These programs build an engine `EGraph` with `new EGraph()`, which "uses every processor as
  rayon's global pool does" (`BridgeMathProgram`'s comment). The Rust's `RAYON_NUM_THREADS=1` has no C# counterpart
  yet.
- egglog's divan benches: `Relations/Writes/WritesBenchmarks.cs` (`ParallelInsert`, `ParallelInsertMerge2`,
  `ParallelInsertRemoveWithCollisions`, 1-8 threads), and the union-find classes.
- The root set: `Relations/Root/`. It includes `RuleSetRunBenchmarks` (two benchmarks, 20,000 and 200,000 rows, serial
  and parallel: 8 cases) and `ConstrainedScanBenchmarks` (7 benchmarks over 4 parameter combinations: 28 cases). The
  others are listed in the benchmark README. The full set has 112 cases (the status at `b041e70`: "median -1.0% over
  112 cases").
- The many-children case: `Relations/Contexts/ManyChildrenBenchmarks.cs` (a thousand single-union children of a
  200,000-row frozen root, in parallel and serially), and `PerChildCostBenchmarks.cs`.
- Not ported:
  - egglog's `tests/math-microbenchmark.egg` (73 lines: 24 rewrites, `(run 11)`, `Const i64`). It is close to
    `examples/math.egg`, which `BridgeMathProgram` ports in its bridge form.
  - the herbie repro `tests/repro-herbie-vanilla.egg` (3,374 lines, 471 rewrites and 165 rules over `BigRat`);
  - the eggcc samples `tests/eggcc-2mm.egg` (10,891 lines, 1,229 rules, nested `run-schedule`) and
    `tests/eggcc-extraction.egg` (1,710 lines, with heavy container use).

  There is no `.egg` reader ([`../roadmap.md`](../roadmap.md), "Push/pop conformance"), so these can only be translated
  by hand.
- The library's `EGraphOptions.DegreeOfParallelism` defaults to 1 (`src/Eggnog/Graph/EGraphOptions.cs`). This matches
  the egglog CLI's default `-j 1` (`ext/egglog/src/cli.rs`).
- No benchmark covers the library's own paths or transient children. The sibling plan
  [`./idle-machine-measurement.md`](./idle-machine-measurement.md) owns them (its Q8 and Q9).

### Measurements

All timings were taken on the i7-7700K (4 cores, 8 threads) while it was shared with other work. They are indicative
only. The noise on that machine is about as large as several of the effects below, which is why §6 starts with an A/A
run.

- **Bridge examples.** Warm, `math` took about 6.6 s against the Rust's 7.4 s, and `ac` took about 15.5-16 s on both.
  The figure first appears in the status document of `b041e70`. That
  commit comes after layered tables (`2e130f7`) and layered query execution (`fb7faff`), and before the store-backed
  root (`8444a46`, "The bridge EGraph now sits on a store"), per-context rules and the rule layer. The commit that was
  timed is not recorded: it lies between `673495d` (the examples' port) and `b041e70`. Both sides used every processor.

  Allocation-only dry runs moved little across the colored-context work: `math` went from 9.80 GB to 9.61 GB, and `ac`
  from 15.47 GB to 15.57 GB. If `math` had lost time in the same proportion as `BasicMath`'s 200,000-row parallel case
  (6-11%), it would take about 7.0-7.3 s against 7.4 s. That would still be parity, with almost no margin.
- **Writes against the Rust.** Each side was run once in the same session. The Rust figures are divan medians. The C#
  figures are BenchmarkDotNet means under server GC, so the two are not the same statistic.

  | Threads | `ParallelInsert` C# / Rust | `ParallelInsertMerge2` C# / Rust | `RemoveWithCollisions` C# / Rust |
  |---|---|---|---|
  | 1 | 420 / 441 ms (0.95) | 438 / 445 ms (0.98) | 70 / 68 ms (1.03) |
  | 2 | 300 / 278 ms (1.08) | 277 / 294 ms (0.94) | 49 / 46 ms (1.07) |
  | 4 | 219 / 205 ms (1.07) | 220 / 189 ms (1.16) | 41 / 37 ms (1.11) |
  | 8 | 205 / 177 ms (1.16) | 196 / 176 ms (1.11) | 35 / 30 ms (1.17) |

  A separate run of the same C# benchmarks, taken with the root baseline, gave 194, 191 and 31.5 ms at 8 threads. The
  run-to-run spread of the C# side is therefore about as large as the gap. Under the workstation collector, which
  library users get by default, the C# `ParallelInsert` took 263 ms at 8 threads against 205 ms under server GC
  ([`../../src/Eggnog.Benchmarks/README.md`](../../src/Eggnog.Benchmarks/README.md), "Running").
- **The `BasicMath` regression.** Alternating runs of the baseline build and the build after layered execution
  (baseline, candidate, candidate, baseline; BenchmarkDotNet medians) gave:

  | Configuration | Baseline | After layered execution | Slowdown | Absolute |
  |---|---|---|---|---|
  | 20,000 rows, serial | 4.16-4.21 ms | 5.10-5.37 ms | 21-29% | 0.9-1.2 ms |
  | 20,000 rows, parallel | 4.32 ms | 4.80-4.90 ms | 11-13% | 0.5-0.6 ms |
  | 200,000 rows, serial | 54.6-56.1 ms | 61.5-62.7 ms | 10-15% | 5.4-8.1 ms |
  | 200,000 rows, parallel | 36.3-37.0 ms | 39.2-40.4 ms | 6-11% | 2.2-4.1 ms |

  The roadmap's "10-15% in all four configurations" covers only the 200,000-row serial case. The 20,000-row serial
  case is worse, and the 200,000-row parallel case is milder.

  Allocation was equal on both sides (1.06 MB; 17.48 against 17.51 MB). With the branch's experiments applied up to the
  single-body sparse build, the 20,000-row serial median was 4.51-4.62 ms against the baseline's 4.24-4.29 ms in the same
  session, and 200,000 rows serial was within 2%. But the baseline binary's own medians drifted by 8% between sessions
  (4.16-4.48 ms), and one 200,000-row serial run of it measured 73.6 ms. On that machine the branch's effect is below the
  noise, as each of its commits says ("Not measured conclusively").
- **JIT shape.** The earlier investigation compared the JIT's output for the join kernels between the baseline and the
  regressed build. This comparison does not depend on machine load.
  - `JoinRun.RunIntersectTwo`'s IL grew from 474 to 538 bytes, and its tier-1 (on-stack-replacement) code shrank from
    about 10.2 KB to 7.9 KB.
  - `RunFusedScan`'s IL grew from 489 to 610 bytes.

  This fits the branch's hypothesis, recorded in its comments. The arms that colored contexts added (the
  `OverlayDisplaced` table kind, layered views) push hot methods past what the JIT inlines into the kernels, so the
  root pays a call per scan. The branch also names code-alignment sensitivity as a suspected source of noise.
- **The sampling profile.** An EventPipe sampling profile of the `BasicMath` workload at the baseline (12,320 samples,
  from a console harness that runs the benchmark's `MathWorkload`) reports:
  - `Array.Resize` at 40.5% exclusive, 4,746 of those samples under `ExecutionState.CreateBuffer` ←
    `ExecutionState.RunInstr` ← `ActionBuffer.Push`;
  - `RowBuffer.Dispose` at 8.7%;
  - the join kernels: `RunIntersectTwo` 10.7%, `Replay` 10.1% and `RunFusedScan` 9.0%;
  - the action interpreter, `ExecutionState.RunInstr`, at 1.7%.

  Profiles of the later stages showed the same shape. Every share in this profile, the kernels' and the interpreter's
  included, carries the same bias, so none of them is reliable:
  - The analysis stripped the runtime's GC-poll helper from the top of each stack. A sibling trace that kept it put
    44% of all samples in that helper. Those samples mean the thread was stopped at a GC safe point, not running
    `Array.Resize`. EventPipe's sampler suspends threads at safe points, so its samples are biased toward them.
  - The `Array.Resize` frames under `CreateBuffer` come from the buffer map, not from row storage.
    `CreateBuffer` (`src/Eggnog/Relations/Tables/ExecutionState.cs`) calls `buffers.Insert`, which calls
    `DenseIdMap.ReserveSpace` (`src/Eggnog/Relations/Collections/DenseIdMap.cs`). That method grows the map's two arrays
    with `Array.Resize` to at least four slots. A clone sizes its map from `parent.buffers.IdCount`. When the root state
    has staged nothing, that count is zero, so a clone's first write to each table with a higher id resizes both
    arrays. These arrays are tiny, so a real 40% share would require an enormous number of clones.
  - The number of clones is not known. `ExecutionState` objects are 13% of the sampled bytes in the allocation probe
    below: about 2.4 MB per 200,000-row serial run. At the order of 100 bytes per state (an estimate), that is tens of
    thousands of states per run. Full batches of 128 matches, over the rule's 10,000-100,000 matches, would make only
    about 80-800 batch clones. So most states come from other clone sites:
    - `ActionBuffer.FlushOnClone` (`src/Eggnog/Relations/Execution/ActionBuffer.cs`), which runs per rule run;
    - the per-shard clone in `SortedWritesTable.StageShard` (`Tables/SortedWritesTable.Insert.cs`);
    - the rebuild tasks (`Tables/SortedWritesTable.Rebuild.cs`, `SortedWritesTable.RebuildIndex.cs`);
    - `ContainerValues`.

    A counter settles this (§6).
- **Allocation probe.** These figures were measured for this plan on current `main`: net10.0 Release, server GC, one
  warm-up repetition and then measured repetitions, in a private clone. The method: `GC.GetTotalAllocatedBytes(true)`
  around each run, and the runtime's `GCAllocationTick` events grouped by type through an in-process `EventListener`.
  Byte counts and shares do not depend on machine load.

  | Workload | Bytes per run | Largest shares of sampled bytes |
  |---|---|---|
  | `BasicMath`, 200,000 rows, serial | 18.6 MB (BenchmarkDotNet: 17.5 MB) | `uint[]` 52%, `TableEntry[]` 23%, `ExecutionState` 13% |
  | `BasicMath`, 200,000 rows, parallel | 84 MB warm (a cold BenchmarkDotNet dry run: 194 MB; earlier default runs: 220-233 MB) | `uint[]` 48%, `FrameUpdate[]` 16%, `TrieNode` 11% |
  | Writes, 4M inserts, 1 thread | 632 MB | `uint[]` 49%, `TableEntry[]` 42%, `RowBuffer[]` 6%, `byte[]` 2.6% |
  | Writes, 4M inserts, 8 threads | 649 MB | `uint[]` 50%, `TableEntry[]` 41%, `MutationBuffer` 5%, `byte[]` 2.6% |
  | Bridge `math`, one run | 11.5 GB (BenchmarkDotNet dry run: 9.6 GB) | `uint[]` 58%, `TableEntry[]` 12%, `int[]` 8.7%, `PredictedEntry[]` 6.3%, `ulong[]` 4.9% |

  The probe also recorded collection counts. They are not used as evidence in this plan.
  `GC.CollectionCount(n)` counts collections of generation n and above. So the equal gen0, gen1 and gen2 counts
  recorded for `BasicMath` serial (0.2 per run each) mean that every collection in the window was a gen2: one in five
  runs. This could not be checked after the fact. A gen2 every 93 MB, under a server collector whose gen0 budget is
  larger than that, points to a collection that something other than allocation triggered, not to allocation
  pressure.

  The benchmark app's probe mode (`--allocation-probe`, Release, one warm-up and three measured invocations) gives the
  same table on both target frameworks and both collectors (workstation: `DOTNET_gcServer=0`). Bytes per invocation
  are means; the parallel cases vary between invocations, so their range is given. The shares are from net10.0 under
  the server collector; the other three configurations agree within six points.

  | Workload | net10.0, server | net10.0, workstation | net8.0, server | net8.0, workstation | Largest shares of sampled bytes |
  |---|---|---|---|---|---|
  | `BasicMath`, 200,000 rows, serial | 18.4 MB | 18.4 MB | 18.4 MB | 18.4 MB | `uint[]` 54%, `TableEntry[]` 23%, `ExecutionState` 12% |
  | `BasicMath`, 200,000 rows, parallel | 61.5 MB (52-70) | 63.3 MB (56-74) | 74.0 MB (60-97) | 82.0 MB (65-98) | `uint[]` 45%, `TrieNode` 15%, `FrameUpdate[]` 13% |
  | Writes, 4M inserts, 1 thread | 632.0 MB | 632.1 MB | 632.0 MB | 632.1 MB | `uint[]` 52%, `TableEntry[]` 42%, `MutationBuffer` 2.8%, `byte[]` 2.7% |
  | Writes, 4M inserts, 8 threads | 655.9 MB (645-662) | 651.6 MB (651-652) | 654.5 MB (643-661) | 657.4 MB (643-677) | `uint[]` 50%, `TableEntry[]` 42%, `MutationBuffer` 5.1%, `byte[]` 2.6% |
  | Bridge `math`, one run | 10.46 GB | 10.55 GB | 10.38 GB | 10.63 GB | `uint[]` 54%, `TableEntry[]` 13%, `int[]` 9.3%, `PredictedEntry[]` 6.9% |
- **Idle local rules.** These figures are from the sibling plan
  ([`./idle-machine-measurement.md`](./idle-machine-measurement.md), section 3.6). The probe is count-only, on
  `eda0c61`, net10.0 Release, server GC. The workload is a frozen schema scope with N local rules
  `p_i(x), p_j(x) => q_i(x)` over 10 relations of 1,000 rows. Per query: a child, one `Assume`, `Run(Saturate)`, one
  `Check`, `Dispose`.

  | Local rules | Idle: bytes per query | Active: bytes per query |
  |---:|---:|---:|
  | 0 | 11,380 | 10,504 |
  | 100 | 165,288 (1 iteration) | 314,868 (2 iterations) |
  | 1,000 | 1,512,104 (1 iteration) | 2,914,823 (2 iterations) |

  About two thirds of the 1.5 KB per idle rule per iteration is engine header instantiation (`LayeredSubset[]`,
  `Constraint[]`, `List<Constraint>`, `(AtomId, Constraint)[]`, `JoinHeaderSpec[]`, `RuleProgress[]`). The other third
  is the library's per-run bookkeeping. At the root, the same probe with global rules gives about 1.17 KB per idle rule
  per iteration.

### The code behind the figures

- **Idle local rules.** For each rule listed in a run, `EGraphContext.RunRulesCore`
  (`src/Eggnog/Relations/Bridge/EGraphContext.Run.cs`) does three things. It allocates `RuleInfo[]`,
  `CachedPlanInfo[]` and `RuleProgress[]` sized by the rule count. It resolves each rule's info and cached plan, and
  computes its canonical constants and watermark. Then it calls `BridgeQuery.AddRulesFromCached`. That method adds one
  semi-naive variant per body atom through `RuleSetBuilder.AddRuleFromCachedPlan`
  (`src/Eggnog/Relations/Query/RuleSetBuilder.cs`). Per variant, that method:
  - first builds a combined `(AtomId, Constraint)[]` (`CombineExtras`);
  - then calls `PlanTemplate.InstantiateLayered` (`src/Eggnog/Relations/Planning/PlanTemplate.cs`). This method
    allocates `new JoinHeaderSpec[count]`, `new LayeredSubset[count]` and two `List<Constraint>` before it evaluates
    anything. It evaluates the focus atom's `GeConst` on the timestamp column first (rule-layer decision D5,
    [`../history/rule-layer-spec.md`](../history/rule-layer-spec.md)), and returns `null` as soon as a header is
    empty.

  So an idle variant costs one binary search per layer of the focus table, plus four to five allocations that it then
  discards. In the library, `ScheduleExecutor.RunIteration` (`src/Eggnog/Scheduling/ScheduleExecutor.cs`) does more
  per-run work:
  - it lowers the rules into fresh `LoweredRule[]` and `EGraphRuleId[]` arrays;
  - per variant, it makes three lookups keyed by the variant's description string (`NumMatches`,
    `RuleSearchAndApplyTime`, `PlanOf`) and a `Dictionary<Rule, RuleStatisticsBuilder>` lookup;
  - `RulesFor` builds a `HashSet<CompiledRule>`.

  Nothing skips a rule before this work.
- **Per-batch staging.** `ActionBuffer.Push` (`src/Eggnog/Relations/Execution/ActionBuffer.cs`) runs a full batch of
  `Bindings.DefaultBatchSize` (128) matches on `root.Clone()`. On first write to a table, the clone creates a
  `MutationBuffer` with two arrays of the shard count (`Tables/MutationBuffer.cs`). Per shard, it creates a `RowBuffer`
  that starts empty and doubles through `PooledArray.Grow` (`Rows/RowBuffer.cs`, `Memory/PooledArray.cs`). A lookup
  that predicts a value also gets a fresh `PredictedValues` hash table (`Actions/ExecutionState.Predicted.cs`). egglog
  does the same per clone (`core-relations/src/action/mod.rs`, `impl Clone for ExecutionState`), but its allocator
  reuses freed memory at once. `TableCatalog.Count` gives the number of tables, so a clone could size its buffer map
  from it.
- **Write allocation, decomposed.**
  - *The hash index* is `TableEntry(ulong Hash, int Row)`: 16 bytes plus a control byte per slot. For 4M rows it ends
    at 2^23 slots, about 136 MB, and the geometric growth before that adds about as much again: about 270 MB in all.
    The probe's `TableEntry[]` share is 265 MB.
  - *The table's rows* are reserved once per merge (`SortedWritesTable.Insert.cs`, as egglog's `do_insert`): 4M rows of
    5 cells, about 84 MB.
  - *Staging* is the rest of `uint[]`, about 225 MB. A 1,024-row batch of 5-cell rows grows a shard's buffer from 16 to
    8,192 cells. Storage above 256 cells is rented from `ArrayPool<uint>.Shared`, and each grown-out array is dropped
    rather than returned (`RowBuffer.GrownCopy`: growth "never releases the array it replaces"). With one merge per
    invocation, every final buffer stays alive until the merge, so the pool cannot recycle any of them. This comes to
    about 55 bytes of staging per row at every thread count, which is why the total does not change with threads.
  - egglog does not reserve its hash index before inserting either (`core-relations/src/table/mod.rs` reserves rows
    only).
- **Shard count.** `ParallelHeuristics` (`src/Eggnog/Relations/Tables/ParallelHeuristics.cs`) gives a table twice the
  degree of parallelism in shards, rounded up to a power of two, and one shard when serial. egglog's writes bench
  builds its table outside `pool.install`, so it always gets twice the machine's threads. On the 8-thread machine both
  sides have 16 shards at `Threads = 8`. They differ at 1, 2 and 4 threads, and the 4-thread gaps are the second
  largest in the table above.

### The experiment branch

`wip/perf-basicmath` holds eight commits, `5efe32d` to `c47077e`:

- the split intersect;
- the fused scan and fused intersect root paths;
- plain views and tables;
- pool rent and `GetIndex` placement;
- the single-body sparse build.

It touches nine files: eight under `Execution/` and `Tables/Table.cs` (`git diff --shortstat 2fd4a18 c47077e`: 486
lines added, 69 removed). It is based on `2fd4a18`, and it exists only in the maintainer's repository, not on the
published remote. A dry run for this plan cherry-picked all eight commits onto `main` (`eda0c61`), across the move of
the engine to `src/Eggnog/Relations` (`f2bfea2`). There was one conflict, at `e84e9bf`, in a doc comment of
`Execution/SparseColumnIndex.cs`. The code ported cleanly. Its comments still cite spec sections (for example
"colored-contexts spec §14.1"), which the release conventions no longer allow in code.

## 4. Value

- **Hosts that run queries in transient children.** A service that freezes schema scopes with local rules
  and runs each request in a query child pays, per query, for every local rule of its scope, in latency and in memory.
  - Allocation grows by about 1.5 KB per idle local rule per iteration, and two thirds of it is discarded. The
    allocation-free empty-header path addresses that directly. By the sibling probe's attribution, removing the
    engine's two thirds would bring 100 idle rules from 165 KB to roughly 65 KB per query. Trimming the library's
    third would bring it to roughly 20-30 KB. These figures are estimates from the type shares, to be confirmed by the
    same count.
  - The same change removes allocation and its time from every idle variant, at the root too (about 1.17 KB per idle
    rule per iteration there).
  - The active case (315 KB at 100 rules over 2 iterations) includes real matching work. The fix helps only its idle
    variants.
  - Demand-indexed local rules would remove the remaining per-rule time, that is, the binary searches and the run's
    per-rule bookkeeping. Their value depends on what that time is per query, against the rest of the query (§6). For
    scale, if an idle rule cost 0.5-2 µs per iteration (an estimate, not measured), 100 idle rules at 1-2 iterations
    per query would cost 50-400 µs per query.
- **Library users with large saturation workloads.** The last measurement put them at egglog-level throughput on two
  programs, before the store-backed root and the rule layer. Parity on `main`, and at one thread (the library's
  default, and egglog's), has not been measured. The `BasicMath` loss is 6-29% depending on the configuration, and it
  has not been shown outside that microbenchmark. Whole-program allocation was unchanged.
- **Maintainers.** A decision on `wip/perf-basicmath` (land a reviewed subset or delete it) removes a stale branch that
  diverges further from `main` with every engine change. Re-measuring removes doubt about the root gates: today every
  colored-context gate is untimed ([`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md), §21.11).
  The clone counters and an unbiased profile keep the next optimizer from chasing the `Array.Resize` figure.
- **Allocation.** A hash reserve at merge, and staging arrays returned to the pool, could remove up to about 130 MB and
  200 MB respectively of the write bench's 630-650 MB, based on the decomposition in §3. Whether that buys time can
  only be shown by timing the prototype. Collection counts and pause time do not show it.

**If none of it is done:**

- Query children keep allocating, and discarding, about 1.5 KB per idle local rule per iteration.
- Parity with the Rust stays unconfirmed on `main` and unmeasured at one thread.
- The root `BasicMath` microbenchmark stays 6-29% behind its baseline.
- The roadmap keeps an open-ended campaign that no one can finish, because two of its workloads cannot be run.

Nothing breaks.

## 5. Cost and risk

- **Idle-local-rule allocation (do now).** Small: about two days with tests.
  - *Engine.* In `PlanTemplate.InstantiateLayered`, validate the extra constraints' atoms first, so the
    `ArgumentException` keeps its behaviour. Then evaluate the headers into scratch owned by the run's
    `RuleSetBuilder`, never by the shared `PlanTemplate`: templates are shared by concurrent children. The scratch is
    two reusable lists and a reusable subset buffer. Allocate the exact-size `JoinHeaderSpec[]` and `LayeredSubset[]`,
    and call `ToArray` on the constraints, only once every header is non-empty. Clear the scratch after each use, so
    it holds no `LayeredSubset`. In `RuleSetBuilder.AddRuleFromCachedPlan`, pass the span straight through when the
    cached plan has no extra constraints, and build the combined array only when an instance is returned.
  - *Library.* In `ScheduleExecutor`:
    - cache the lowered arrays per frozen ruleset, since local rules are fixed at freeze;
    - key the per-variant statistics by rule index, not by description string;
    - reuse the `RulesFor` set.

    `RunReport` and `RuleStatistics` must report exactly what they do today.
  - *Departure from egglog.* egglog's `instantiate` also allocates before evaluating. The change only removes
    allocation (owner question 4).
  - *Risk.* Low. An empty header still returns `null` before an action id is taken. A mistaken `null` for a non-empty
    header loses matches. The rule differentials catch that: `ContextRuleDifferentialTests`,
    `SeededWatermarkDifferentialTests`, `SemiNaiveDifferentialTests` and `BridgeDifferentialTests` under
    `src/Eggnog.Tests/Relations/`, and the library's scope and local-rule tests (`src/Eggnog.Tests/Scopes/`).
  - *New tests.* An allocation test in the style of `ExecutorAllocationTests` that runs N idle rules in a child and
    bounds the bytes per idle rule per iteration. A mutation review then checks that the differentials fail when the
    empty test is inverted, or when the scratch is not cleared.
- **Clone counters and map sizing (do now).** Trivial.
  - Two counters in `ExecutorCounters` (`src/Eggnog/Relations/Execution/ExecutorCounters.cs`, enabled by
    `EGGNOG_COUNTERS`): clones by site and buffer creations.
  - The clone constructor in `ExecutionState` would size the map to `catalog.Count`. The cost is two arrays of the
    table count per clone (9 bytes per table) instead of growth to the highest table id touched. For a catalog of
    hundreds of tables and a clone that touches a few low ids, this allocates more. So size it to `catalog.Count` only
    when the counts show growth actually happening, or make the first growth jump straight to `catalog.Count`.
- **Porting the `BasicMath` branch.** Medium. The code applies cleanly. The work is:
  - rewriting its comments to the self-contained, milestone-free form;
  - reviewing about 490 lines of hand-specialized root paths in the join kernels;
  - a mutation-testing review of the tests that guard them.

  Correctness risk: medium. The branch duplicates kernel paths (a plain-view root path beside the layered path). A
  defect in only one path passes any test that runs only the other.
  - Oracles, all under `src/Eggnog.Tests/Relations/`:
    - `ExecutorPropertyTests` and `MultiRuleExecutorPropertyTests` (random queries against a naive nested-loop join);
    - `LayeredExecutorPropertyTests` and `ContextRuleDifferentialTests` (the layered paths);
    - `SerialJoinReuseTests`;
    - `BridgeDifferentialTests` (a naive bridge oracle);
    - `SemiNaiveDifferentialTests`;
    - the allocation probes `ExecutorAllocationTests`, `ParallelRootMathAllocationTests` and `RootCostReportTests`.
  - Each duplicated path needs a test that reaches it and fails when it is broken. The mutation review checks this.

  Maintenance: two code paths per kernel for as long as the plain-view split exists. Every later change to the join
  must keep both. If compiled plans are dropped ([`./compiled-plans.md`](./compiled-plans.md), §1), hand-written C#
  fast paths like these are the remaining route to kernel speed. That plan names this branch as the pattern for its own
  fallback (section 6, Q4). So the review cost buys the technique the engine would use anyway.
- **Staging and predicted-cache pooling.** Medium if built. Per-thread pools with explicit return add ownership rules.
  A buffer returned while a pending queue still holds it would be a use-after-return that corrupts rows. It would be
  guarded by the same oracles, the write model tests in `Tables/`, and a debug lease check (the engine map's "debug
  lease counter", §5.1).
- **The write-allocation fixes.** Small.
  - Returning grown-out staging arrays to the pool is safe only for `MutationBuffer`'s private row buffers, which no
    reader holds. `RowBuffer` is shared with tables, where readers may hold old storage, so the change must be confined
    to staging.
  - A hash reserve from the pending row count over-allocates when many staged rows collide. Bound it, for example by
    the pending count capped at twice the live rows.
  - Root risk: low. These are fewer allocations on the same paths.
- **Shard-count option.** Small: an optional constructor argument on `ParallelHeuristics` and a benchmark parameter.
  It is internal; the library exposes only `DegreeOfParallelism`.
- **Demand-indexed local rules.** Medium.
  - The work: a per-(context, table) summary of the newest timestamp across the context's layer chain (its own layers,
    and ancestor layers that are not frozen). Merges, catch-up and adoption keep it current. `RunRulesCore` tests it
    per rule before any planning work.
  - Correctness risk: medium. Skipping a rule that a newly visible ancestor row should fire loses matches silently.
    Oracles: `SeededWatermarkDifferentialTests` and `ContextRuleDifferentialTests` (child runs against replayed roots),
    `SemiNaiveDifferentialTests`, and the library's local-rule and scope tests. Add a test-only switch that runs every
    program both with and without the skip.
  - Root risk: none if it applies only in children, where the skip compares against a parent's watermark. Local rules at
    a writable root keep today's path.
- **Herbie and eggcc ports.** Large without a reader: thousands of lines, `BigRat`, nested schedules and containers,
  translated by hand. Excluded.
- **Machine time.** This dominates the cost of the measurement. Estimates come from invocation lengths: `math` about
  7 s and `ac` about 16 s per invocation; 36 cases in the two root classes the branch touches, at about a minute each
  under the default job; 12 write cases a side.

  | Row (§6) | Machine time |
  |---|---|
  | A/A control | about 1 h |
  | Idle rules and transient children | the sibling plan's Q8 |
  | `BasicMath` port A/B, four rounds of two builds over 36 cases plus the bridge programs | about 5-6 h |
  | Rust comparison at both degrees of parallelism, and the library port | about 2 h |
  | Writes and shards | about 1.5 h |
  | Write-fix A/B under two collectors | about 2 h |
  | ETW profile, if needed | about 0.5 h |

  That is one night, about 12 h, and not a few hours. The cumulative root baseline (`main` against `3a6e0b7`) comes
  from the sibling plan's session and is not repeated here.

## 6. Worth-it evaluation

**Noise control applies to every timed row.**

- **A/A control.** Phase 1 starts with an A/A run. The same `main` binary runs in two alternating sessions of
  `RuleSetRunBenchmarks` and the warm `math` example. This gives the spread for each configuration.
- **Thresholds.** A threshold below is used only where it exceeds twice the A/A spread for that configuration. Where
  it does not, add rounds until the standard error of the ratio is under half the threshold, or report the row as
  unresolved.
- **Statistic.** Compare medians on both sides, including divan against BenchmarkDotNet.
- **Alignment control.** Every candidate that passes is re-run once from a second build with an unrelated change (a
  new unused method in the benchmark assembly, or a reordered file). An effect that the rebuild alone reproduces or
  erases is reported as alignment, not as a gain.

| # | Question | Cheapest decisive experiment | Metric and go/no-go | Idle machine? |
|---|---|---|---|---|
| 1 | Does the allocation fix remove the idle-rule waste? | The sibling plan's count-only probe, before and after the fix, with 0, 10, 100 and 1,000 idle and active local rules, at a child and at the root | The sibling plan's Q12 measure: the engine's share of idle-rule allocation falls by at least 90%, so bytes per query at 100 idle local rules fall by at least half. Record the active case. What remains of it after the fix is matching work, which demand indexing would not remove | No |
| 2 | Where do the execution states and the `Array.Resize` frames come from? | `EGGNOG_COUNTERS` per `BasicMath` run (both sizes, both modes): clones by site, buffer creations, and buffer-map growths. Then the same with the clone's map sized up front | Reconcile the clone count with the 13% `ExecutionState` byte share. If the growth counter goes to zero and nothing else remains under `CreateBuffer` in a re-taken EventPipe profile, close the staging item without ETW. Otherwise go to row 6 | No |
| 3 | Are idle local rules still costly in time, per query? | The sibling plan's Q8 benchmark after the fix of row 1: a query child under a frozen scope with 0, 100 and 1,000 idle local rules, time per query (p50 and p99), and iterations per query | Overhead per query = time per query at 100 idle rules minus at 0 rules, which already includes every iteration. Build demand-indexed local rules if that overhead exceeds 25% of the query's own time at 0 rules, at p50 or at p99. Otherwise record and drop. The sibling plan's Q8 uses the same measurement and rule | Yes |
| 4 | Does the branch recover `BasicMath`'s loss? | Port the branch to a scratch branch from `main`, as the dry run did. First, with no idle machine, compare JIT shape (`DOTNET_JitStdOutFile`, `DOTNET_JitDisasmSummary=1`, `DOTNET_JitDisasm=RunIntersectTwo RunFusedScan Replay`) on `3a6e0b7`, `main` and the port, on both target frameworks. Then time `main` against the port, alternating, four rounds: `RuleSetRunBenchmarks` and `ConstrainedScanBenchmarks` (the classes over the code the branch changes), plus `BridgeMathBenchmarks` and `BridgeAcBenchmarks` with `--iterationCount 10`. `main`'s loss per configuration comes from the sibling plan's cumulative comparison against `3a6e0b7` in the same session | Go if, in each `BasicMath` configuration where `main`'s loss exceeds twice the A/A spread, the port recovers at least half of that configuration's loss. No other case may be more than 2% worse, and `math` and `ac` must not be worse. Drop the branch if `main`'s loss is under 3% or under twice the A/A spread in every configuration. A whole-program gain of 2% or more on `math` or `ac` is a go on its own | JIT shape: no. Timing: yes |
| 5 | Does `main` still match the Rust, and does it match at one thread? | (a) `--bridge-example math` and `ac` against `ext/egglog/target/release/examples/math.exe` and `ac.exe`, alternating, three rounds: once at every processor, and once with the examples' new degree-of-parallelism option at 1 against `RAYON_NUM_THREADS=1`. (b) The library-API port of `math-microbenchmark.egg` (`DegreeOfParallelism = 1`) against `egglog -j 1 tests/math-microbenchmark.egg`, built with `cargo +1.91.0 build --release`, alternating, three rounds | Warm C# time / Rust time, medians, for each program and degree. At most 1.10: parity holds. 1.10 to 2.0: within E2's bar; record it, and profile that program only if the owner keeps throughput parity as a goal (question 1). Above 2.0: a regression to investigate before the next release | Yes |
| 6 | If row 2 leaves it open: is `CreateBuffer`'s share real? | An unbiased CPU profile of `BasicMath` (200,000 rows, both modes): ETW kernel sampling through PerfView or Windows Performance Recorder, with the JIT's symbols, over 50 runs. Do not use EventPipe's sampler | `CreateBuffer`'s inclusive share. Below 5%: close. At 5% or more: prototype per-thread reuse of the batch state (the clone's buffers, `MutationBuffer`s and `PredictedValues`), and keep it if `BasicMath` serial improves by at least 3% and by more than twice the A/A spread | Yes |
| 7 | Do the write-allocation fixes pay? | Prototype (a) staging buffers that return grown-out arrays to the pool, or that start at a capacity derived from the previous flush, and (b) a bounded hash reserve at merge. Count bytes now with the allocation probe. Then time `main` against the prototype under server GC and under the workstation collector (`DOTNET_gcServer=0`), alternating, two rounds. Record `GC.GetTotalPauseDuration` as context only | Bytes: at least 25% fewer on `ParallelInsert` at 1 and 8 threads, with the reference model's check passing. Time: land it if 8 threads is at least 3% faster under either collector, and above twice the A/A spread, with no configuration more than 2% worse. If the time is flat, land only the part that is free in time, as an allocation win | Bytes: no. Time: yes |
| 8 | Is the parallel writes gap real, and does the shard count explain part of it? | `cargo +1.91.0 bench -p egglog-core-relations --bench writes` in `ext/egglog` (`sample_count = 25`) against `WritesBenchmarks --iterationCount 25`, alternating, two rounds, medians on both sides. The C# side also runs a parameter that fixes 16 shards at 1, 2 and 4 threads | C# / Rust per thread count. At most 1.10 at every count: close the gap item. With 16 shards, if the 2- or 4-thread ratio moves by more than 5 points: keep the option for benchmark fidelity, and record the shard effect. Above 1.10 at 8 threads with equal shards: an ETW profile of the merge | Yes |
| 9 | Regression record only: what did whole programs lose since the examples were ported? | The bridge benchmarks at `673495d` against `main`, alternating, two rounds | Recorded in the benchmark README. Not a gate: the interval mixes layered tables, layered execution, the store-backed root, per-context rules and the rule layer, so no single change can be blamed for the result | Yes |

**Preliminary verdict from the evidence now.**

- The idle-rule allocation is worth fixing now. It is the only item here whose waste is known without timing: every
  idle variant allocates and discards its header buffers, at the root and in children. The fix is small and has strong
  oracles.
- The clone counters and map sizing cost minutes and may close the staging question without a profiler.
- The parity campaign as scoped is not worth running. Two of its workloads are out of reach, and its question needs
  only one re-measurement on existing workloads, at both degrees of parallelism. That re-measurement is worth doing:
  the only parity figure predates the store-backed root, and E2's one-thread comparison was never run. The library-API
  port of `math-microbenchmark.egg` is small: 24 rewrites, `i64` and strings, and the library's degree of parallelism
  lines up with egglog's `-j` default. It is the first benchmark of the published API against the egglog CLI.
- The `BasicMath` work is worth one measured decision and no more until then. The JIT-shape data supports a real cause
  (lost inlining). The effect is about 0.5-1.2 ms per 20,000-row run, and 2-8 ms per 200,000-row run depending on the
  mode, in a rule with 10,000-100,000 matches. If compiled plans are dropped, as [`./compiled-plans.md`](./compiled-plans.md)
  recommends, this branch's technique is the remaining route to kernel speed, which raises its value.
- The write fixes are cheap, and their allocation effect can be confirmed without an idle machine. Their time effect
  is unknown and is decided only by timing the prototype.
- Demand-indexed local rules wait on row 3, measured after the allocation fix.

## 7. Plan if it goes ahead

### Phase 0: without an idle machine (small)

Entry: the owner agrees to this plan.

In this order, the items that need no timing first:

1. **Idle-rule allocation (row 1).** Implement the engine and library changes of §5. Add the allocation test. Run the
   full suite on net8.0 and net10.0 and the mutation review. Record the sibling probe's figures before and after.
2. **Clone counters and map sizing (row 2).** Record clones by site and buffer-map growths per `BasicMath` run, then
   decide on the sizing.
3. **Branch port and JIT shape (row 4).** Port `wip/perf-basicmath` onto a scratch branch from `main`, resolving the
   one comment conflict, and build Release for both target frameworks. Record the JIT shape of the join kernels
   (`RunIntersectTwo`, `RunFusedScan`, `Replay`, `Table.ScanGenericBounded` callers) at `3a6e0b7`, at `main` and on
   the port, on both target frameworks. That needs a multi-targeted benchmark app or a net8.0 test host.
4. **Allocation-probe mode.** Add it to the benchmark app: per-type `GCAllocationTick` totals around a named workload,
   as used for §3. Record the §3 table for `main`.
5. **Write prototypes (row 7).** Prototype the two write-allocation fixes and count their bytes.
6. **Benchmark additions (rows 5 and 8).**
   - A degree-of-parallelism option for the bridge example programs, through the internal
     `EGraph(ParallelHeuristics)` constructor.
   - A shard-count parameter on `WritesBenchmarks`.
   - The library-API port of `math-microbenchmark.egg`, checked against the table sizes egglog prints (`print-size`).

Exit:

- the allocation fix is merged and row 1's figures are recorded;
- the counter and JIT-shape records exist;
- the prototypes compile;
- every benchmark's check passes in a dry run.

### Phase 1: one idle-machine night (about 12 hours)

Shared with [`./idle-machine-measurement.md`](./idle-machine-measurement.md), which owns the cumulative root baseline.
Run in this order, so the rows that decide open items are taken first:

1. the A/A control;
2. row 3 (idle rules and transient children; that plan's Q8);
3. row 4 (the `BasicMath` port A/B);
4. row 5 (the Rust at both degrees of parallelism, and the library port);
5. row 8 (writes and shards);
6. row 7 (the write fixes under both collectors);
7. row 6, only if row 2 left it open;
8. row 9, if time remains.

Exit: every go/no-go threshold in §6 has a value, or is marked unresolved against the A/A spread.

### Phase 2: decide (owner)

Close each item whose threshold says close. Schedule the rest.

### Phase 3: implement what passed (each item separately, small to medium)

For each item:

1. implement;
2. review from several angles: correctness against the oracles named in §5, the root's cost, allocation, and a
   mutation-testing review of the new or changed tests;
3. fix;
4. verify the full suite on net8.0 and net10.0;
5. re-run the item's benchmark and the affected root classes against the Phase 1 records, within 2% or twice the A/A
   spread, whichever is larger.

The items:

- **The `BasicMath` port.** Land only the commits that the A/B attributes gains to. Rewrite their comments in the
  self-contained form. Add a test per plain-view path that fails when that path is broken. For example: a property test
  that runs each random query once with the plain views enabled and once with them disabled, and compares.
- **Write allocation.** Keep staging-only pooling inside `MutationBuffer`. Bound the hash reserve. Keep the reference
  model check in `WritesBenchmarks`. Add an allocation test in the style of `ExecutorAllocationTests` that fixes the
  bytes per staged row.
- **Batch-state reuse** (only if row 6 confirms it). Per-thread reuse of the clone's buffers and the predicted cache,
  with a debug lease check. Extend `ExecutorAllocationTests` and `ParallelRootMathAllocationTests`.
- **Demand-indexed local rules** (only if row 3 crosses its threshold). The newest-stamp summary per (context,
  table), the skip in `RunRulesCore`, a test-only switch, and the differential runs with and without the skip.

### Documentation

- [`../roadmap.md`](../roadmap.md):
  - replace "Performance parity" with the outcome of Phase 1;
  - restate the `BasicMath` item with per-configuration figures, then close it or keep it with its idle-machine
    figures;
  - record the idle-rule allocation figures under the transient-children item.
- [`../../src/Eggnog.Benchmarks/README.md`](../../src/Eggnog.Benchmarks/README.md): the new figures against the Rust at
  both degrees of parallelism, the examples' degree-of-parallelism option, the allocation-probe mode, the shard
  parameter, the library-API math benchmark, and the regression record of row 9.
- [`../architecture.md`](../architecture.md): only if pooling changes buffer ownership, or if demand-indexed rules
  change how a run selects rules.
- [`../engine/conventions.md`](../engine/conventions.md): a note on profiling and timing:
  - EventPipe sampling is biased toward safe points, and unbiased profiles use ETW;
  - A/B timings alternate builds, start from an A/A control, and are checked against a rebuild for alignment.

## 8. Dependencies and ordering

- [`./idle-machine-measurement.md`](./idle-machine-measurement.md): Phase 1 runs in the same night as that workstream's
  cumulative root comparison and transient-child latency, and uses its root baseline. That plan owns the idle-rule
  measurement (its Q8). This plan owns the engine-side fix and the demand-indexed decision. The two plans use one
  measurement and one rule (row 3). That plan's question 6 asks whether header skipping should start before the
  idle-machine run; this plan answers yes.
- [`./compiled-plans.md`](./compiled-plans.md) recommends Drop and names hand-written C# fast paths, "as
  `wip/perf-basicmath` does", as the first remedy if interpretive overhead ever shows up. If compiled plans are dropped,
  this branch is the remaining route to kernel speed, and its review cost should be weighed on that basis. The
  interpreter's share used in that decision (about 2-3%) comes from the same EventPipe profile as the `Array.Resize`
  figure. It is provisional until an unbiased profile is taken (row 6, or that plan's Q1). That plan also asks that its
  profiles be taken after the staging question is settled here (rows 2 and 6).
- [`./parallelism-test-plan.md`](./parallelism-test-plan.md): any change to parallel paths (pooling across threads,
  shard counts, parallel merges) waits for its serial-against-parallel oracles with value-changing lattice merges.
- [`./smaller-engine-items.md`](./smaller-engine-items.md): the `ProberPiece` shrink and lazy `IndexCatalog` objects
  are gated on the same root classes. Measure them in the same night, so one baseline serves all.
- [`./rule-layer-split-features.md`](./rule-layer-split-features.md): demand-indexed local rules need no public API,
  so that plan carries no item for them. This plan owns them, engine side and decision.
- [`./conformance-docs-release.md`](./conformance-docs-release.md): the herbie and eggcc workloads come back only if a
  `.egg` reader is ever built there. The unreachable commit citations in the roadmap are a documentation fix.
- [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md) and
  [`./colored-context-deferred-features.md`](./colored-context-deferred-features.md): no ordering constraint.
  Per-context replanning, if it is built there, changes child performance, and this plan's latency figures would need
  to be re-taken.

## 9. Questions for the owner

1. Is throughput parity with egglog on saturation workloads still a goal in itself, beside the latency and memory of
   small query children? The answer decides whether anything beyond Phase 1 is built for the saturation workloads,
   and whether row 5's middle band (1.10 to 2.0) leads to profiling.
2. May the herbie repro and the eggcc samples be dropped from the plan, coming back only if a `.egg` reader is built for
   other reasons?
3. About `wip/perf-basicmath`:
   - Should the branch be pushed, so the published record has the commits it cites, or deleted after Phase 1 if its
     figures do not justify it?
   - Is a regression confined to the root microbenchmark acceptable if `math` and `ac` show no loss on `main` against
     the port?
4. Are departures from egglog that only remove allocation acceptable? There are three: an allocation-free empty-header
   path, a bounded hash reserve at merge, and staging arrays returned to the pool. egglog does none of them.
5. If the package keeps targeting net8.0 (synthesis question 3), should the benchmark app target net8.0 as well as
   net10.0, so the JIT behaviour that net8.0 users get is measured?
6. Settled: row 3's rule for demand-indexed local rules, used by this plan and by
   [`./idle-machine-measurement.md`](./idle-machine-measurement.md) alike, is to build them if, after the allocation
   fix, the overhead per query at 100 idle local rules exceeds 25% of the query's own time at 0 rules, at p50 or at
   p99.
7. When will an idle machine be available for a night?

## Review

A skeptical review of the previous version raised twelve points. Each was checked against the code, the history and
the sibling plans.

**Changed:**

- **Idle-rule allocation (accepted).** `PlanTemplate.InstantiateLayered` allocates two arrays and two lists before its
  first evaluation. `RuleSetBuilder.AddRuleFromCachedPlan` builds `CombineExtras`' array before that. `RunRulesCore`
  allocates three arrays per run. The sibling plan's count shows about 1.5 KB per idle rule per iteration, two thirds of
  it discarded. The fix is now a Do-now item, and the demand-indexed skip stays conditional. Sections 1, 3, 4, 5, 6 and
  7 were changed, and the headline recommendation moved from "Do after an experiment" to "Do now, in part".
- **Per-query measurement (accepted).** Row 3 now measures overhead per query, which includes every iteration, in place
  of a per-iteration slope. The two plans use the same measurement and the same relative rule.
- **Parity stated as current fact (accepted, with a correction).** The parity figure is now described as unconfirmed on
  `main`. One-thread rows for the bridge examples and the library port against `egglog -j 1` were added to §6 and to
  Phase 1. The previous version named `b041e70` as the store-backed root. It is the status document that records the
  figure; the store-backed root is `8444a46`.
- **Noise control (accepted).** The plan now has an A/A control, thresholds that must exceed twice the A/A spread,
  medians on both sides, and a rebuild check for alignment. The 1.02-1.03 gap is gone with the old row.
- **The `673495d` row (accepted).** It cannot attribute anything. The branch is now decided by `main` against `main`
  with the port, on the bridge programs too. `673495d` against `main` is kept as a record only (row 9).
- **Machine time (accepted).** The plan now gives estimates per row: about 12 h, one night. The branch A/B is limited
  to `RuleSetRunBenchmarks` and `ConstrainedScanBenchmarks`. The cumulative baseline is shared with the sibling plan,
  and Phase 1 is ordered by priority.
- **Write gaps at 4 threads, and the shard verdict (accepted).** All four thread counts are now reported with ratios.
  The 8-thread `RemoveWithCollisions` gap is now given as 17%. The shard test is part of the writes row.
- **`BasicMath` per configuration (accepted).** The plan now gives 21-29%, 11-13%, 10-15% and 6-11%, with absolute
  figures. The go rule is per configuration.
- **Staging-buffer argument (accepted).** The GC argument was removed. The plan now notes that the 13% `ExecutionState`
  share implies far more states than batch clones, and lists the other clone sites. Counters and map sizing were added
  before any ETW session.
- **GC-share gate (accepted).** The write fixes are now decided by a direct A/B under both collectors. Pause time is
  context only. The 0.2/0.2/0.2 counts are explained (`GC.CollectionCount` is cumulative over higher generations, so
  all were gen2) and are no longer used as evidence.
- **Compiled-plans dependency (accepted).** The interpreter's share is marked as coming from the same biased profile.
  The dependency now says that dropping compiled plans raises the branch's value.
- **Diff counts (accepted).** The branch diff is 486 lines added and 69 removed. The conflict is at `e84e9bf`.

**Rejected, in part:**

- **That the bridge timings predate layered tables and layered execution.** The figure first appears in `b041e70`,
  which comes after both. The commit that was timed is not recorded, so all that can be said is that it predates the
  store-backed root. The conclusion, unconfirmed on `main`, stands. The illustrative range was recomputed from
  `BasicMath`'s parallel 200,000-row loss.
- **That sizing the clone's map from the table count "costs almost nothing".** It allocates two table-count arrays per
  clone, which is more than today for a large catalog when a clone touches only low table ids. It is now made
  conditional on the growth counter, or done as a first growth straight to the table count.
- **The suggested alignment control (a padding change in a build).** It is adapted rather than adopted as given. For
  JIT-compiled code, the control is a rebuild with an unrelated change, re-timed. An effect that the rebuild alone
  moves is reported as alignment.
