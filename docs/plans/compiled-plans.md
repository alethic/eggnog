# Compiled plans (expression trees, hand-emitted IL, or composed generic structs)

## 1. Summary and recommendation

**Recommendation: Drop compiled plans as dynamic code. Keep kernel specialization open as closed-world C# under
[`./performance-parity.md`](./performance-parity.md).** Confidence: high that no dynamic technique should be built now
(about 85%); about even (50%) on whether specializing the join kernels would gain little.

Two findings carry the recommendation, and they have different strength.

- **The right-hand side loses little to interpretation. This is measured.** Its interpreter dispatches once per
  instruction per batch. On egglog-bridge's `math` and `ac` examples a dispatch serves 127-128 live lanes on average
  (counts taken for this plan, section 3). Sampling profiles of `RuleSetRunBenchmarks.BasicMath` put
  `ExecutionState.RunInstr` and `ActionBuffer.Push` at about 3% exclusive together. Those profiles are indicative only,
  because their sampler is biased toward GC safe points (section 3).
- **The join kernels' specialization headroom is not measured.** The kernels hold most of the samples. The best
  evidence on their shape is the root `BasicMath` regression: `BasicMath` runs 10-15% slower than its baseline `3a6e0b7` ([`../roadmap.md`](../roadmap.md), "The root `BasicMath` regression"). Its likely cause is lost inlining:
  the kernels became general over the table kinds and layered views that colored contexts added (section 4). That is
  the cost of generality, which per-shape specialization exists to remove. So on root workloads about 10-15% may be
  at stake in kernel shape. Fusing plans of one or two stages could also remove frame replay for those plans.

Neither finding calls for dynamic code. Whatever the kernels can gain, closed-world C# fast paths chosen by the
interpreter's existing switches can reach the same ceiling for a bounded set of shapes. That is the pattern
`VectorizedLookup` uses and the branch `wip/perf-basicmath` extends. Such fast paths stay reviewable, are precompiled by
ReadyToRun, take part in tiering and dynamic PGO, and work under NativeAOT. Each dynamic technique adds a lasting cost:

- Expression trees cannot use the engine's unchecked ref loops and need a duplicate array API.
- Hand-emitted IL removes that duplicate API, as the owner proposes. But wrong IL can corrupt memory silently, and the
  emitter is hard to review, debug and mutation-test.
- Composed generic structs leave every instantiation in memory for good and run slowly until tiering promotes them.

None of the three works without dynamic code. Small runs also argue against compiling at run time. On a sample of
small queries over the predicate test theories, a dispatch serves 1.2-4.0 lanes, and a test's whole engine run is a few
hundred instructions. The JIT time of one compiled shape is expected (not yet measured; section 6, Q5) to exceed the
interpreted run it would serve.

The plan is therefore:

1. Retire the expression-tree convention.
2. Delete the array overloads that nothing in the engine calls. Section 3 gives the corrected inventory.
3. Remove "Compiled plans" from the roadmap.
4. Record "kernel specialization" under performance parity, decided by one ceiling prototype with a single go/no-go
   cut-off (section 6, Q3).

## 2. What it is and why it was planned

The engine map planned compiled plans as the second half of a two-step executor: interpret first, compile later.

- [`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), section 0 point 5: "The executor is interpreted
  first. Compiled RHS programs and per-stage kernels come later. The interpreter keeps the recursion and the dynamic
  stage reordering."
- Section 5.4, "Where compiled delegates replace interpretation", lists the pieces to compile:
  - right-hand-side action programs, as one delegate per `ActionInfo`, with the mask in two `ulong` locals and table
    handles as constants;
  - stage kernels, as one delegate per (plan, stage, prober-kind pattern), invoked once per stage per morsel;
  - whole-plan fusion, "Only for plans whose order is fixed (one or two stages, single-atom rules, rebuild-style
    rules)";
  - merge functions, as one row-merge delegate per table;
  - slow constraints, inlined into the scan kernels.

  Planning, `merge_all`, rebuild, index builds and catch-up stay hand-written.
- Section 5.2, "Rules for hot types", and section 5.3, "The JIT-inlinable cursor and index API that compiled plans
  call", fixed the technique, `System.Linq.Expressions`, and its consequence: "Expression trees cannot use `Span`,
  `ref struct`, `ref` locals, `stackalloc` or `ref` returns". So every primitive a plan calls needs an overload over
  arrays and offsets on a sealed type. The same rule is binding today in
  [`../engine/conventions.md`](../engine/conventions.md), "Performance rules".
- Section 6, the build order, row M12 "Compiled plans": compiled RHS programs, merge functions, stage kernels and a
  cache of plan permutations, tested "interpreted against compiled, using the M7/M10 generators".
- Section 7.4, evidence E2, gave the performance motivation. E2 measures "the M1–M7 interpreted core on three
  workloads (`math-microbenchmark`, a herbie repro, and a synthetic 200k-row saturation), against `egglog -j1`". The
  port wins if it is "within about 2× interpreted, and about 1.3× after M12 compiled kernels". The fork question
  reopens if it is "worse than 3× after M12". Section 7.4 point 4 also gave a strategic reason: "Expression-compiled
  plans are an optimization path that only a .NET engine has." Section 8.2 names compiled kernels as a mitigation of
  the risk "Performance gap". Under "Dynamic reordering against compilation" it notes that "whole-plan fusion needs a
  fixed order".
- [`../engine/decisions.md`](../engine/decisions.md), decision 8, "Dynamic reordering when compiled": keep the
  reordering, compile per-stage kernels, and let the interpreter keep the recursion and reordering.
- [`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md), section 9.9 "What M12 needs", extended
  the plan to colored contexts. It asks for:
  - one delegate per (plan, stage, piece-kind pattern) with a `hiddenIsNone` bit;
  - array accessors on `HiddenSet`;
  - `ProberPiece.TryGetSubset` overloads;
  - frames that carry `Layer`.

  The interpreter keeps the outer layer loop, `Drain`, re-sorting and recursion.

**Why it was never started, from the record.**

- The build order put compiled plans after the parallelism milestone, and both came after the bridge.
- The work then went to colored contexts and to the rule layer (the design history index,
  [`../history/README.md`](../history/README.md)).
- The parallelism test plan is still open too (see [`./parallelism-test-plan.md`](./parallelism-test-plan.md)).
- Meanwhile the interpreter beat or matched the Rust on the two workloads that were timed: egglog-bridge's `math` and
  `ac` examples, against egglog-bridge's Rust. [`../roadmap.md`](../roadmap.md), "Benchmarks and timed gates", records
  `math` at about 6.6 s against the Rust's 7.4 s and `ac` at about 15.5-16 s on both. These were warm runs on an
  i7-7700K, timed before the store-backed root (the roadmap dates them by commit `b041e70`). E2's own three workloads
  against `egglog -j1` have not been measured.

The record contains no decision to drop compiled plans, and no measurement of what they would save. The roadmap still
lists them under "Engine work".

## 3. Current state

**Code.** Nothing of a compiler exists. A text search of `src/` finds no use of any of these: `System.Linq.Expressions`,
`DynamicMethod`, `ILGenerator`, `Reflection.Emit`, `MakeGenericType`, `MakeGenericMethod`, `RuntimeFeature`. A
compiler would replace the following interpreter pieces.

- **Right-hand-side programs.** `ExecutionState.RunInstrs` and `RunInstr`
  (`src/Eggnog/Relations/Actions/ExecutionState.Actions.cs`) run one `switch` per instruction per batch of up to 128
  lanes, with `Unsafe.As` to the sealed instruction class. Inside a kernel the lane loop is already specialized with
  struct-constrained generics:
  - `VectorizedLookup` (`src/Eggnog/Relations/Actions/VectorizedLookup.cs`) picks a probe once per batch and runs
    `Mask.Fill<TProducer>`, with no virtual call per lane. The probes are `SortedKey1Lookup`-`SortedKey3Lookup`,
    `SortedKeyNLookup`, `DisplacedLookup`, `OverlayDisplacedLookup` and the `LayeredKey*Lookup` structs.
  - `ArgGather` (`src/Eggnog/Relations/Actions/ArgGather.cs`) loads a lane's variable operands with
    `MemoryMarshal.GetArrayDataReference` and `Unsafe.Add`, after one range check.
- **Stage kernels.** `JoinRun.RunPlan` (`src/Eggnog/Relations/Execution/JoinRun.Plan.cs`) switches on the stage kind
  once per call. The kernels in `JoinRun.Intersect.cs`, `JoinRun.Fused.cs` and `JoinRun.Materialized.cs` loop over
  prober pieces and keys. They branch on `ProberKind` per key (`ProberCursor.MoveNext`). They loop over `Constraint[]`
  per row (`Constraint.Evaluate`, `src/Eggnog/Relations/Tables/Constraint.cs`). `Drain`, `Replay` and
  `StageOrdering.SortPlanBySize` stay in the interpreter under decision 8.
- **Merge functions.** `FunctionTableMerge.Merge` (`src/Eggnog/Relations/Bridge/FunctionTableMerge.cs`) is one virtual
  call per conflict. For every kind but a constant or a primitive, it skips the merge when both outputs are equal.
  Otherwise it runs `ResolvedMergeFn.Run`, a `switch` that recurses into arguments. The table merges `MaxMerge`,
  `MinMerge`, `KeepIncomingMerge`, `KeepCurrentMerge`, `AssertEqualMerge` and `UnionIdMerge`
  (`src/Eggnog/Relations/Tables/`) are sealed classes.
- **Library primitives and lattices.** These reach the engine as `ExternalFunction` subclasses: `PrimitiveCall`
  (`src/Eggnog/Rules/PrimitiveCall.cs`) and `LatticeMerge<T>` (`src/Eggnog/Values/LatticeMerge{T}.cs`). Each call:
  - enters `EngineCallMarker`;
  - runs inside `try`/`catch`/`finally`;
  - decodes its arguments, invokes the user's delegate and encodes the result.

  A compiled caller would still pay all of this per call, through one virtual call.

**Code prepared for compilation.** About 50 comment lines in 37 files under `src/` cite compiled plans, compiled
kernels or expression trees.

The inventory below was taken by marking each candidate array overload `[Obsolete("PROBE", true)]` in a private clone
of `eda0c61` and building `src/Eggnog` and `src/Eggnog.Tests` for net10.0. Each error CS0619 names a caller. A text
search is not enough, and neither is making a member `private` in a scratch build. Arrays convert implicitly to spans.
So once an array overload is inaccessible, overload resolution falls back to the span overload, and the build still
succeeds. The deletion would then change generated code without anyone noticing.

Array overloads that the engine calls:

| Member | Engine callers |
|---|---|
| `Bindings.Push(uint[], Variable[], Variable[])` (`src/Eggnog/Relations/Actions/Bindings.cs`) | `ActionBuffer.Push` (`src/Eggnog/Relations/Execution/ActionBuffer.cs`), once per match. The exact array overload wins over the span overload. |
| `TableView.TryGetColumn(uint[], int, int, out uint)` (`src/Eggnog/Relations/Layers/TableView.Lookup.cs`) | `LayeredKeyNLookup.TryProduce` (`src/Eggnog/Relations/Actions/LayeredKeyNLookup.cs`), per lane |
| `TableView.FindRow(uint[], int, out int)` (same file) | `TableView.FindRow(ReadOnlySpan<uint>, out int)` for zero-cell keys and keys wider than three cells; `TryGetColumn` above |
| `ExternalFunction.TryInvoke(ExecutionState, uint[], int, int, out uint)` | `ExternalCallProducer`, `ExecutionState.Database.cs` |
| `Constraint.Evaluate(uint[], int)` | `LiveConstraintFilter` |
| `ProberPiece.TryGetSubset(uint[], out SubsetRef)` | `JoinRun.Index.cs`, `JoinRun.Materialized.cs`, `Prober.cs` |
| `TableKeyHash.Hash(uint[], int, int)` | `SortedWritesTable` inserts, deletes, lookups, rehash and layered paths; `ContextTables.Lookup.cs` and the invariant checkers; `StagedOutputs` |
| `SortedWritesTable.FindRow(uint[], int)` | `ContextTables.CatchUp.cs`, `ContextTables.Invariants.cs`, `SortedWritesTable.Lookup.cs` |
| `PredictedValues.Hash(TableId, uint[], int, int)` | `PredictedValues` itself |

Array overloads that nothing in `src/Eggnog` calls (the benchmarks do not call them either):

| Member | Test callers |
|---|---|
| `RowOps.IsVisible` (`src/Eggnog/Relations/Tables/RowOps.cs`) | `HiddenAwareViewAgreementTests`, `HiddenScanTests` |
| `SortedWritesTable.SortAtLeast`, `SortBelow` (`src/Eggnog/Relations/Tables/SortedWritesTable.Scan.cs`) | `SortedWritesTableTests` |
| `ExecutionState.StageInsert(TableId, uint[], int)`, `StageRemove(TableId, uint[], int)` (`src/Eggnog/Relations/Tables/ExecutionState.cs`) | `ExecutionStateTests`, `RetractionTests` |
| `MutationBuffer.StageInsert(uint[], int)`, `StageRemove(uint[], int)` (`src/Eggnog/Relations/Tables/MutationBuffer.cs`) | `MutationBufferTests`, `RetractionTests`. In the engine they are reached only through the uncalled `ExecutionState` overloads. |
| `RowBuffer.AddRow(uint[], int)` (`src/Eggnog/Relations/Rows/RowBuffer.cs`) | `RowBufferTests` |

Host writes, merges and rule actions stage rows through the span overloads. Calls such as
`state.StageInsert(table, full)` pass an array as a span.

**Colored-context accessors.** The accessors of section 9.9 exist:

- `HiddenSet.SortedRows`, `SortedCount` and `Bits` (`src/Eggnog/Relations/Layers/HiddenSet.cs`) are used by
  `HiddenCursor`.
- `TableView` and `ProberPiece` (`src/Eggnog/Relations/Execution/ProberPiece.cs`) have plain fields.
- `ProberPiece.TryGetSubset` has a `uint` and a `uint[]` overload.
- Frames already carry their layer (`FrameUpdates.RefineAtomDense(atom, layer, range)`).

The owner's proposal names `SubsetRef` among the engine's ref structs. It is in fact a plain 16-byte `readonly struct`
(`src/Eggnog/Relations/Offsets/SubsetRef.cs`), made so for expression trees. The engine's actual ref structs are
`FunctionRow` (`src/Eggnog/Relations/Bridge/FunctionRow.cs`), and `TaggedRow` and the four row-buffer enumerators in
`src/Eggnog/Relations/Rows/`.

**Tests that would serve as oracles.** These exist:

- `ActionPropertyTests` with `ReferenceActionInterpreter` (`src/Eggnog.Tests/Relations/Actions/`);
- the database-level differential harness (`src/Eggnog.Tests/Relations/Differential/`: `DiffGenerator`,
  `DiffReferenceJoin`, `DiffShrinker`);
- `ExecutorPropertyTests` and `LayeredExecutorPropertyTests` (`src/Eggnog.Tests/Relations/Execution/`);
- `BridgeDiffGenerator` (`src/Eggnog.Tests/Relations/BridgeDifferential/`);
- `ColorRuleGenerator` (`src/Eggnog.Tests/Relations/Colors/`);
- the allocation probes (`ExecutorAllocationTests`, `LayeredAllocationTests`).

**Measurements.**

- **Timing.** See section 2: `math` about 6.6 s against 7.4 s, `ac` about 15.5-16 s on both. These were warm runs on
  an i7-7700K, timed before the store-backed root (the roadmap dates them by commit `b041e70`).
- **Sampling profiles of `RuleSetRunBenchmarks.BasicMath` (indicative only).** These are EventPipe CPU sampling
  profiles of the workload (fresh graph per invocation, serial). They were taken during the colored-context work, at the baseline `3a6e0b7` and at the layered-execution stages. Each has about 12,000-14,000 samples, and they are kept
  outside the repository. Exclusive shares:

  | Method | Exclusive share |
  |---|---|
  | `JoinRun.RunIntersectTwo` | 10.7-12.8% |
  | `JoinRun.Replay` | 10.1-11.1% |
  | `JoinRun.RunFusedScan` | 7.8-9.5% |
  | `ExecutionState.RunInstr` | 1.4-1.7% |
  | `ActionBuffer.Push` | 1.2-1.5% |
  | `StageOrdering.EstimateSize` | about 0.6% |
  | `JoinRun.RunPlan` | under 0.1% |
  | `Array.Resize` | 34-41% |

  The attribution is doubtful, as [`./performance-parity.md`](./performance-parity.md) sets out in section 3:
  - The analysis stripped the runtime's GC-poll helper from the top of each stack. A sibling trace from the same
    period that kept it put 44% of all samples in that helper.
  - EventPipe's sampler suspends threads at safe points, so its samples are biased toward them.
  - Most of the `Array.Resize` time sits under `ExecutionState.CreateBuffer`. That function runs once per table for
    each fresh `ExecutionState` that `ActionBuffer.Push` clones for a full batch (`root.Clone()`). The `Array.Resize`
    share is most likely an artifact of that bias.

  The right-hand side's own share comes from subtracting this doubtful `CreateBuffer` share from `RunInstrs`'s
  inclusive share (36-43%). That leaves about 4% for the whole right-hand side, including its table probes and staging.
  `Database.MergeAll` is 13-15% inclusive. A profile of a two-scan workload from the same period puts `RunIntersectTwo`
  and `Replay` at 73% exclusive together, and `ActionBuffer.Push` at 0.1%. Methods the JIT inlines, such as
  `ArgGather.Load`, the lookup structs, `Constraint.Evaluate` and `ProberCursor.MoveNext`, count toward their callers.
  So these method-level shares cannot separate the parts of a kernel loop that specialization would remove from the
  parts it keeps.
- **JIT shape of the kernels.** [`./performance-parity.md`](./performance-parity.md), section 3, compares the JIT's
  output between the baseline and the regressed build. `RunIntersectTwo`'s IL grew from 474 to 538 bytes, and its
  tier-1 code shrank from about 10.2 KB to 7.9 KB. `RunFusedScan`'s IL grew from 489 to 610 bytes. This fits the
  hypothesis that the arms colored contexts added push hot methods past what the JIT inlines into the kernels.
- **Dispatch counts, measured for this plan.** These are counts, not timings, so the busy machine does not affect
  them. A private clone of `eda0c61` was given temporary counters in `RunInstrs`, `RunPlan`, `FrameUpdates.FinishFrame`,
  `ActionBuffer.Push`, `ExternalCallProducer` and `FunctionTableMerge.Merge`. It was then run on egglog-bridge's
  examples (`--bridge-example`, both repetitions) and on two suites of scenario tests over the predicate test theories
  (net10.0).

  | Workload | Instruction dispatches | Live lanes per dispatch | Matches | Stage-kernel calls | Matches per stage call | External calls | Merge calls (merge function run) |
  |---|---|---|---|---|---|---|---|
  | bridge `math` | 811,095 | 126.6 | 30.7 M | 4.09 M | 7.5 | 0 | 28.7 M (16.6 M) |
  | bridge `ac` | 4,107,878 | 127.9 | 177.0 M | 207 K | 855 | 0 | 209.9 M (46.6 M) |
  | scenario tests on `PredicateTheory`, scopes as contexts (24 tests) | 2,259 | 1.2 | 1,184 | 2,446 | 0.5 | 478 | 646 (382) |
  | scenario tests on `ContextArgumentTheory`, contexts as data arguments (27 tests) | 2,852 | 4.0 | 4,021 | 5,069 | 0.8 | 2,726 | 1,514 (1,023) |

  The merge function runs only when the two outputs differ. On the bridge examples that is the constructors'
  `UnionId`, one `switch` case. Both theories are test fixtures in `src/Eggnog.Tests/Predicates/`: predicates over
  document paths (presence, forms, domains, types and numeric bounds) and their simplification. The scenario tests that
  produced these counts are not kept in the suite; the theories are. Each test is one small query or a few, so the
  counts are a sample of small runs, not a workload.
- **Experiments for the root `BasicMath` regression.** Branch `wip/perf-basicmath` (commits `5efe32d` to `c47077e`)
  attacks the regression by writing root fast paths of the join kernels in C#. Examples are a fused intersect over a
  plain, single-layer view and a split two-scan intersect. This is hand specialization, not compilation. It is
  unreviewed, and its commits say it was "Not measured conclusively". The branch is on the layout from before the fold
  (`src/Eggnog.Relations/...`), so reusing it means porting it to `src/Eggnog/Relations/`.

**Packaging.** `src/Eggnog/Eggnog.csproj` targets net8.0 and net10.0. It declares neither `IsAotCompatible` nor
`IsTrimmable` (see [`./conformance-docs-release.md`](./conformance-docs-release.md)).

## 4. Value

The owner's second question asks whether compiled plans are worth building now. The answer depends on the part of the
executor.

**Right-hand side, on large workloads (bridge examples, root benchmarks).** Little value.

- **Dispatch.** The dispatches a compiled program removes are already spread over 127-128 lanes each. There are 811 K
  of them across both `math` repetitions, against 30.7 M matches. Removing every dispatch saves roughly a `switch`, a
  cast and a few field loads per 128 matches.
- **Fusion of instructions.** A compiled program could keep a lane's values in registers instead of writing a
  `Bindings` segment per instruction. The indicative profiles bound that saving by the right-hand side's whole share,
  about 4% of `BasicMath`. Most of that share is table probes and staging, which fused code still does.
- **Merges.** On constructors, a merge runs one `switch` case: 16.6 M runs on `math` and 46.6 M on `ac`.

**Join kernels.** Possibly worth something. Not measured.

- The kernels hold most of the samples in every profile above. Part of that time is work any kernel does: hash probes,
  trie-child lookups and subset copies in `RefineScan` (`src/Eggnog/Relations/Execution/JoinRun.Index.cs`), and frame
  writes.
- Specialization would remove these: the per-key `ProberKind` branch, the constraint loop, the layered-view arms on
  plain root views, and arity and column numbers held in registers instead of folded as constants.
- **Evidence of size.** The root `BasicMath` regression is 10-15% ([`../roadmap.md`](../roadmap.md)).
  [`./performance-parity.md`](./performance-parity.md) puts it at 5-6 ms per 200,000-row run and attributes it, from
  the JIT-shape data, to lost inlining in kernels made general over the new table kinds and layered views. That is the
  cost of generality, and it suggests that about 10-15% on root workloads depends on kernel shape. The branch that
  specializes the kernels for the root has not shown its effect above the noise. Whether specialization can gain more
  than the regression cost is unknown.
- **Replay.** Decision 8 keeps `Replay` in the interpreter for plans that reorder. But whole-plan fusion (engine map
  section 5.4) would compile plans of fixed order, such as one or two stages or single-atom rules, into nested loops
  with no frames. That removes `Replay` and frame writes for those plans. `Replay` is about 10% exclusive in the
  `BasicMath` profiles and part of the 73% that `RunIntersectTwo` and `Replay` hold in the two-scan profile (both
  indicative). The same fusion can be written as closed-world C#. The split two-scan intersect on `wip/perf-basicmath`
  begins to do this.
- On `math`, a stage-kernel call yields 1.3 frames and 7.5 matches on average. So the fixed cost per call matters as
  much as the inner loop: `TakeProbers`, `GetIndex`, `TakeFrames` and `Drain`. Unfused per-stage kernels would still
  pay them.

Only a ceiling prototype can decide the kernel question (section 6, Q3). Method-level profiles cannot.

**Library users with small runs.** Compilation at run time would make these slower. A host that runs each request in
a query child under a frozen scope runs small programs. On the `PredicateTheory` sample (section 3) a dispatch serves
1.2 lanes and a stage-kernel call yields 0.5 matches. A test's whole engine run is on the order of 100 instructions.
Time per query is dominated by fixed costs, which compilation does not touch:

- catch-up and planning;
- a fresh `ExecutionState` and staging buffers per batch;
- the library's primitive path (`PrimitiveCall`, about one external call per two to three matches here).

Under any dynamic technique, each compiled program or kernel is a method the JIT compiles on first use. The
interpreter's code is shared by every rule and is compiled once per process, or ahead of time by ReadyToRun.

An optimizing JIT of one kernel-sized method is expected to take on the order of hundreds of microseconds. This is not
measured here; Q5 measures it. A run of about 100 instructions, serving one or two lanes each, is expected to take
less. So a query that compiled even one shape on its path would spend more on that one method than on the work it
speeds up, and it would pay that again for each new shape and after each graph rebuild. Compiling lazily, only after a
plan has run many times, would avoid that cost on small runs, and would also deliver nothing there. Closed-world fast
paths have no such cost: ReadyToRun can precompile them.

**Maintainers.**

- Not building a compiler removes an obligation. The conventions' rule makes every hot primitive carry a second,
  array-based overload. Eight of those overloads now have no engine caller (section 3). Retiring the rule simplifies
  the API.
- Building a dynamic compiler adds a second execution engine. It must agree with the interpreter on every instruction
  kind, prober kind, layering case and merge kind, and it must be maintained and differentially tested for good.
- Closed-world fast paths also duplicate kernel code, but only for the shapes measured to matter. They are tested by
  the same differential families.

**If it is never done.** The interpreter remains the only executor. Throughput stays where the timed gates in
[`./idle-machine-measurement.md`](./idle-machine-measurement.md) put it. The open performance gaps on record are these:

- The root `BasicMath` regression. It is the cost of general kernels, and kernel specialization in C# is its remedy.
- Parallel writes, 11-16% behind at 8 threads. That is staging and merges, which no executor change reaches.

Both belong to [`./performance-parity.md`](./performance-parity.md). The strategic argument of section 7.4 ("an
optimization path that only a .NET engine has") is no longer needed: the port was chosen, and it is faster than the
Rust or level with it on the two workloads timed.

### The three techniques compared (the owner's question)

| | Expression trees (planned) | `DynamicMethod` with hand-emitted IL (owner's proposal) | Composed generic structs (`MakeGenericType`/`MakeGenericMethod`) |
|---|---|---|---|
| **What it can express** | No ref locals, ref returns, `Span<T>` or ref-struct locals, `stackalloc`, or results of ref-returning calls such as `Unsafe.Add` and `MemoryMarshal.GetArrayDataReference`. `ref`/`out` arguments are possible through by-ref parameters (engine map section 5.2). Non-primitive constants (tables, indexes) are hoisted into a closure array, so each use is an array load and a cast, not a constant. | Everything IL allows: ref locals and returns, `in`/`ref` parameters, `Span<T>` and ref-struct locals, `localloc`, `Unsafe`, `MemoryMarshal`, integer constants folded by the JIT. Object constants still come from a bound target or an argument. | Everything C# allows inside the composed methods, including spans, refs, `Unsafe` and the engine's current APIs. Shapes are limited to a type vocabulary: kinds as struct types, and small integers (arity, columns, key widths) as struct types with static abstract members the JIT folds. Value constants and tables stay struct fields. |
| **Duplicate array overloads** | Required: this technique is the reason they exist. Compiled code would also index arrays with bounds checks where the interpreter now uses unchecked refs (for example `ArgGather.Load`), so some inner loops could get slower. | Not needed. | Not needed. |
| **Expected speed against the interpreter** | Bounded by the ceiling in section 6, minus closure-constant loads and restored bounds checks. Possibly no gain. | At best the ceiling: equal to hand-specialized C# with the same constants. | At best the ceiling, once at tier 1. Tier-0 code does not inline, so a tower of small struct methods runs far slower than the interpreter until promoted (after 30 calls, or by on-stack replacement for loops). `AggressiveOptimization` on the entry points avoids that, and gives up PGO. |
| **Tiering and PGO** | Compiled into a dynamic method: compiled once, optimized, never tiered, no dynamic PGO (engine map section 5.2). | Same as expression trees. | Normal tiering and dynamic PGO, unless `AggressiveOptimization` is used. |
| **Where dynamic code is unavailable** | Under NativeAOT, `LambdaExpression.Compile` returns delegates run by the expression interpreter, far slower than the engine's own interpreter. The engine must check `RuntimeFeature.IsDynamicCodeCompiled` and keep its own interpreter. | `DynamicMethod` is not supported. The fallback is the interpreter. | `MakeGenericType` and `MakeGenericMethod` require dynamic code (`RequiresDynamicCode`). Under NativeAOT only instantiations generated ahead of time exist. The fallback is the interpreter. |
| **ReadyToRun and cold start** | Nothing precompiled; one JIT per delegate. | Nothing precompiled; one JIT per method. | Instantiations composed at run time are not precompiled; one JIT per shape, at tier 0 first. |
| **Memory over a long-lived service** | Collectible: delegates are freed with their plans. | Collectible. | Not collectible: every instantiation lives as long as the process. Shapes must come from a bounded vocabulary. Otherwise a service that retires graphs and defines its rules again (as the [interning plan](./interning-and-value-lifetime.md)'s usage gauge invites) grows without bound. |
| **Access to internal engine members** | `Compile` uses a skip-visibility dynamic method on CoreCLR, so internals are reachable. `MethodInfo`s found by reflection need trimming annotations. | An owner type or module plus `skipVisibility` gives access. Same trimming caveat. | Same assembly; no issue. `MakeGenericType` itself is unsafe under trimming. |
| **Correctness risk** | Low: trees are type-checked when built. | High. The JIT rejects unbalanced stacks with `InvalidProgramException`, but it does not verify type safety. Wrong IL around byrefs or `Unsafe` can corrupt memory silently. | Low: C# is type-checked, and composition errors fail at `MakeGenericType` (constraint violations). |
| **Debugging and review** | No source or line numbers in stack traces; trees can be printed. | No source, no PDB, no stepping. Reviewers must simulate the evaluation stack. Mutation testing of an emitter yields many mutants that fail only by crashing the JIT. Tests could save the same IL through `PersistedAssemblyBuilder` and run ILVerify, but only on net10.0 (the type exists from .NET 9). | Ordinary C#: debuggable, reviewable and mutation-testable. Type-level composition is awkward to read, and nesting depth can exceed the JIT's inlining budget. |
| **Effort** | Large, plus upkeep of the array API. | Largest: the compiler, plus an IL test harness. | Large: dozens of small vocabulary types (one per file under the layout rules), plus composition code. |

A fourth column belongs beside these: **closed-world C# fast paths**. It expresses what C# expresses, but only for
shapes written in advance. Its ceiling is the same as the best dynamic technique's for those shapes. It has full
tiering and PGO, is precompiled by ReadyToRun and works under NativeAOT and trimming. It has no visibility issue and the
lowest correctness risk. Its effort grows with the number of shapes, which Q4 counts.

**Verdict on the owner's proposal.** IL emission removes exactly what the owner names: the duplicate overloads, the
copies into arrays and the closure-array constants. It is the most expressive dynamic technique. But not compiling at
run time removes the duplicate overloads just as completely. Any kernel gain that is found can be had in closed-world
C#, without unverifiable IL, a second executor, or the loss of NativeAOT and ReadyToRun. If dynamic code is ever
needed, for a shape distribution too open for fast paths, composed generic structs are the better dynamic technique for
this project. They keep the C# reviewable, keep PGO and keep the engine's ref-based APIs. Their two hazards,
non-collectible instantiations and tier-0 slowness, have known mitigations: a bounded vocabulary and
`AggressiveOptimization` entry points. Expression trees are the weakest of the three and should be ruled out whatever
is decided.

## 5. Cost and risk

**Effort, if a dynamic compiler were built.** Large under any technique.

- Right-hand-side programs: medium. They cover 13 instruction kinds, root and layered lookups,
  `LookupOrInsertDefault` with the predicted cache, fallbacks and external calls.
- Merges: small.
- Join kernels: large. They cover three stage kinds, one-, two- and many-scan intersects, five prober kinds, leaf scans,
  materializations, and layered pieces with and without hidden rows.
- Medium: the cache per (plan, stage order, piece-kind pattern), lazy compilation thresholds, the interpreter fallback,
  and a run mode that forces compilation for tests.
- Dominant cost: the kernels across colored-context layering, and the differential test matrix.

**Effort of closed-world specialization.** Medium per group of shapes. It starts with porting the relevant commits of
`wip/perf-basicmath` from the layout before the fold (`src/Eggnog.Relations/...`) and reviewing them. That branch is
unreviewed.

**Complexity and correctness risk.** A second executor, or a fast path, must agree with the general path on match
multiplicity, frame order and staging order wherever golden tests depend on them (decision 4: serial runs are
deterministic). The oracles are the random-program harnesses listed in section 3, the context invariant checkers and the
conformance programs. For a dynamic compiler they would run in a compile-always mode against the interpreter.
Hand-emitted IL adds silent memory corruption as a failure mode. Differential tests catch it only when the corrupted
state is observed.

**Risk to the root's performance.** Dynamic techniques make cold start and first-run latency worse. Steady state could
improve by the amount the ceiling prototype finds. Gates of 2% on the root benchmark set would apply
([`../roadmap.md`](../roadmap.md), "Root gates").

**Maintenance.** A dynamic compiler doubles every later change to an instruction, a prober kind, a constraint kind or
the layering: it must be made in the interpreter and in the compiler or its vocabulary. Fast paths double such changes
only for the shapes they cover. The test suite now takes 10-15 minutes per target framework
([`../engine/conventions.md`](../engine/conventions.md), "Building and testing"). A dynamic compiler would run the
differential families in two modes.

**Cost of the recommended cleanup.** Small.

- One convention paragraph is replaced.
- About 50 comment lines are rewritten to state why each array form or plain struct exists today, or deleted with the
  member.
- Eight array overloads with no engine caller are deleted, with the test assertions that cover only them.
- The roadmap entry moves.

Risk to behaviour: none for the uncalled members, as the probe build shows. One more change is optional:
`Bindings.Push(uint[], Variable[], Variable[])`. Deleting it moves `ActionBuffer.Push`, once per match, onto the span
overload. The three explicit `ArgumentNullException.ThrowIfNull` checks go, and the array-to-span conversions' own null
tests remain. That is a codegen change on a hot path. It needs the root gate, or the overload stays with a reworded
comment.

## 6. Worth-it evaluation

Each question below decides part of the matter. The counts need no idle machine. The profiles and timings are taken in
the sessions that [`./idle-machine-measurement.md`](./idle-machine-measurement.md) and
[`./performance-parity.md`](./performance-parity.md) hold anyway. Only Q3 decides whether kernel specialization is
built.

**Q1. Where does the time go, and which workload and plans should a prototype target?**

- **When.** After performance-parity has settled whether the `CreateBuffer` share is real. Until then the profiles'
  attribution is in doubt.
- **Experiment.** Take unbiased CPU profiles with ETW kernel sampling through PerfView or Windows Performance Recorder,
  with the JIT's symbols, as [`./performance-parity.md`](./performance-parity.md) specifies. Do not use EventPipe's
  sampler. Profile warm runs of these workloads:
  - bridge `math` and `ac` (example mode);
  - `RuleSetRunBenchmarks.AcSaturation` and `BasicMath` at 200,000 rows;
  - `ActionLookupBenchmarks`;
  - a primitive-heavy synthetic program built on the library (integer arithmetic primitives in rule heads and lets);
  - a loop of query children on `PredicateTheory` (`src/Eggnog.Tests/Predicates/`) under a frozen schema scope, as a
    sample of small runs.

  Use a normal build. A build with hot helpers marked `NoInlining` changes the shape of the loops being measured, so if
  one is used at all, report it beside the normal build, never instead of it.
- **Output.** For each workload: the shares of the join kernels, `Replay`, the right-hand side, merges and staging; the
  run-time share of each plan; and the plans that hold the most time.
- **Use.** This picks the target workload for Q3, in advance, and the plans to prototype. It does not decide go or
  no-go. Method-level buckets cannot separate the replaceable parts of a kernel loop (kind branches, unfolded
  constants) from the kept parts (probes, frame writes).
- **Idle machine.** Required.

**Q2. How much work does one dispatch serve?**

- **Experiment.** Count, not time. Run the counter patch described in section 3 on every Q1 workload. Add keys per
  stage call and rows scanned per stage call.
- **Answer already measured.** The bridge examples serve 127-128 lanes per right-hand-side dispatch. The
  predicate-theory scenario tests serve 1.2-4.0 lanes and 0.5-0.8 matches per stage call.
- **Conclusion, limited to right-hand-side dispatch.** On large workloads, compiling the right-hand side removes
  little. On small workloads a run is too short to repay compiling anything at run time. These counts say nothing
  about the time spent inside the join kernels.
- **Idle machine.** Not needed.

**Q3. What is the ceiling? This is the only decider for kernels.**

- **Target.** One workload, chosen in advance from Q1: the one where the join kernels and `Replay` hold the largest
  share. If the owner's priority is the latency of small query children, the target is the `PredicateTheory` query
  loop of Q1 instead, measured as time per query at p50 and p99.
- **Experiment.** In a private branch, hand-write in C# the code a compiler would emit for the target's three hottest
  plans:
  - their right-hand-side programs, fused;
  - their stage kernels, with arity, columns, key widths and constraint kinds as literals and no kind branches;
  - for any of them with a fixed order of one or two stages, a fused version with nested loops and no frames or
    `Replay`.

  Select the prototype through a test-only hook keyed by plan. Verify it against the interpreter with the action and
  executor property generators.

  All three dynamic techniques produce at best what the JIT makes of equivalent C#. Expression trees produce less,
  because of closure constants and bounds checks. So this prototype bounds them all, and it is also the first version
  of a closed-world fast path.
- **Metric.** End-to-end time with BenchmarkDotNet (in-process, server GC, the machine's configured power plan) on the
  target and on the root set (`RuleSetRunBenchmarks`). Report:
  - the measured gain;
  - the share of the target's run time that the three plans hold, from the Q1 profile;
  - the gain scaled to full coverage (measured gain divided by that share). This is an optimistic estimate, since
    other plans may gain less.
- **Cut-off, with no gap.**
  - **Go:** the scaled gain on the target is 10% or more, and no root benchmark is worse by more than 2%.
  - **No-go:** otherwise.

  A go hands the work to performance-parity as closed-world specialization (section 7.2). That work must then
  measure at least 10% on the target before it lands.
- **Idle machine.** Required.
- **Preliminary answer.** Not run. The right-hand-side evidence predicts little from that part. The `BasicMath`
  regression suggests the kernels could be worth about 10% on root workloads. The result is open.

**Q4. If Q3 says go, how many shapes would fast paths need?**

- **Experiment.** Over the Q1 workloads, count the distinct shapes the winning specializations need:
  - instruction sequences;
  - (stage kind, prober kinds, scan count, arity) tuples;
  - piece-kind patterns with the `hiddenIsNone` bit;
  - plans of fixed order (one or two stages, single-atom rules, rebuild-style rules), and their share of run time.

  A test-only tally over planned rules does this.
- **Threshold.** If 20 or fewer shapes cover 90% of the run time, write those shapes as C# fast paths selected by the
  interpreter's existing switches. Only an open-ended shape distribution would justify composing code at run time.
- **Idle machine.** Not needed for the count.

**Q5. If dynamic code is ever chosen, does its JIT cost fit small runs and cold start?**

- **Experiment.** Cover query children of `PredicateTheory`'s schema scopes and a synthetic schema scope with 1,000
  local rules. With compilation forced, count the methods and IL bytes compiled, using
  `System.Runtime.JitInfo.GetCompiledMethodCount` and `GetCompiledILBytes` before and after. Then measure
  `JitInfo.GetCompilationTime` on the idle machine, beside the interpreted time per query. This also checks the
  expectation in section 4 that one optimized kernel JIT costs on the order of hundreds of microseconds.
- **Go.** With compilation on, the first query after a scope's freeze, and every later one, is no slower than with
  compilation off, on both the `PredicateTheory` queries and the 1,000-rule scope, without ReadyToRun. In practice that
  means a lazy threshold under which no query-child-sized run compiles anything.
- **Idle machine.** Required for the time; the counts can be taken now.

**Q6. Should the duplicate overloads go now?** This needs no experiment. Not compiling, IL emission and composed
generics all make them unnecessary. Only expression trees need them, and those are ruled out in section 4.

- **Answer: yes** for the eight members with no engine caller (section 3).
- The comments on members the interpreter calls should state their present reason instead.
- `Bindings.Push` over arrays goes only after a root-gate measurement.

**Preliminary verdict.**

- Drop compiled plans as dynamic code, and do the cleanup now.
- Move the kernel question to performance-parity as closed-world specialization. Q3's single cut-off decides it, and
  Q1 feeds it.

## 7. Plan if it goes ahead

Two plans follow. The first is the recommended cleanup. The second is kernel specialization, which runs only if Q3
says go, within [`./performance-parity.md`](./performance-parity.md).

### 7.1 The recommended cleanup (do now)

**Phase C1: inventory.**

- **Entry:** the owner accepts the drop.
- **Work:** list every member and type whose documentation cites compiled plans, compiled kernels or expression trees,
  with its callers in `src/Eggnog`, `src/Eggnog.Tests` and `src/Eggnog.Benchmarks`. Find the callers by marking each
  member `[Obsolete("...", true)]` in a scratch build and reading the CS0619 errors, or with Roslyn's find-references.
  Do not make members `private` to find callers: the build then falls back to the span overloads and reports nothing.
- **Exit:** a table of members to delete (no engine caller) and members to keep (with the caller that justifies them).
  Section 3 gives the expected result.

**Phase C2: change.**

- Delete the eight array overloads with no engine caller:
  - `RowOps.IsVisible`;
  - `SortedWritesTable.SortAtLeast` and `SortBelow`;
  - `ExecutionState.StageInsert`/`StageRemove(TableId, uint[], int)`;
  - `MutationBuffer.StageInsert`/`StageRemove(uint[], int)`;
  - `RowBuffer.AddRow(uint[], int)`.

  Edit the tests that call them: `ExecutionStateTests`, `RetractionTests`, `MutationBufferTests`, `RowBufferTests`,
  `SortedWritesTableTests`. Change each assertion to use the span overload where it tests the operation, or delete it
  where it tests only the overload. `RowOps.IsVisible` is used by `HiddenAwareViewAgreementTests` and
  `HiddenScanTests` as an agreement check. It either moves into the test project as a test helper, or is deleted with
  those assertions.
- Keep the array overloads the engine calls (section 3). Rewrite their comments to give the present reason, for
  example "the array form, for callers that hold an array".
- Optionally change `ActionBuffer.Push` to the span overload of `Bindings.Push` and delete the array overload, but only
  if the root set stays within 2% on an idle machine. Otherwise keep the overload with a reworded comment.
- Change `SubsetRef`, `OffsetEnumerator`, `SortedOffsetSlice` and the cursor types only in their comments (for example,
  "a plain struct, so that it can be stored in fields"). Making them ref structs would gain nothing and would stop them
  being stored in fields.
- **Exit:** builds without warnings on net8.0 and net10.0, and the full suite passes on both.
- **Review:**
  - a review for API removal, to confirm no library caller was missed;
  - a review of the comments against the rule that they be self-contained and free of milestones;
  - a mutation-testing review limited to any test that was edited.

**Phase C3: documents.**

- [`../engine/conventions.md`](../engine/conventions.md), "Performance rules": replace the expression-tree paragraph
  with these rules:
  - Spans, refs and ref structs are the default on hot paths.
  - Array overloads exist where a caller holds an array.
  - Specialization uses struct-constrained generics chosen by a switch.
- [`../engine/decisions.md`](../engine/decisions.md): change decision 8, which presumes compilation. Either restate it
  as "the interpreter is the only executor; specialization is closed-world", citing this plan, or keep it as history
  with that note (question 5).
- [`../roadmap.md`](../roadmap.md): remove "Compiled plans". Add "Kernel specialization" under "Performance parity",
  with Q3 as its deciding experiment.
- [`../architecture.md`](../architecture.md): no change is needed beyond a check that section 3.5 makes no claim about
  compilation.

### 7.2 Kernel specialization (if Q3 says go)

**Phase 1: closed-world fast paths.**

- **Entry:** Q3 says go, and Q4 finds 20 or fewer shapes covering 90% of run time.
- **Work:**
  - Port the relevant commits of `wip/perf-basicmath` to the current layout.
  - Promote the Q3 prototype to fast paths behind the existing switches, including fused fixed-order plans of one or
    two stages.
- **Tests:**
  - every differential family unchanged;
  - a test per fast path asserting that it and the general path give identical frames, bindings and staged rows on the
    property generators' programs;
  - allocation probes unchanged.
- **Exit:** at least 10% measured on the target on an idle machine, and the root set within 2%.

**Phase 2: composed generic structs.** Only if Phase 1 cannot cover the distribution.

- **Entry:** Q4 shows the shapes are open-ended, and Q5's go condition holds.
- **Work:**
  - a bounded vocabulary of struct types, one per file;
  - composition behind `RuntimeFeature.IsDynamicCodeSupported`, falling back to the interpreter;
  - `AggressiveOptimization` entry points;
  - a cache keyed by (plan, stage order, piece-kind pattern);
  - compilation only after a plan has run a configured number of times.
- **Tests:**
  - a run mode with three settings, never, always (threshold 0) and auto, set by an environment variable as
    `EGGNOG_COUNTERS` is;
  - every differential family in the always mode against the interpreter, on net8.0 and net10.0;
  - a mutation-testing review of the vocabulary and the composer, where every surviving mutant needs a test;
  - a test that the interpreter path is taken when dynamic code is reported unavailable.
- **Benchmarks:** the root set, bridge `math`/`ac`, the query-child latency of
  [`./idle-machine-measurement.md`](./idle-machine-measurement.md) (its Q8), and Q5's JIT counts and times.

**Phase 3: hand-emitted IL.** Only for a piece the vocabulary cannot express at the required speed.

- **Entry:** a measured shortfall of Phase 2 on a named kernel.
- **Work:**
  - an emitter that writes through `ILGenerator`, so the same code can target a `DynamicMethod` and, in tests, a
    `PersistedAssemblyBuilder`;
  - ILVerify in the net10.0 test run;
  - a review in which the reviewer annotates the stack state at every instruction.

**Documentation for 7.2.** Update the conventions, the decisions (the technique and its fallback), architecture section
3.5 (the executor) and the benchmarks README.

## 8. Dependencies and ordering

- [`./idle-machine-measurement.md`](./idle-machine-measurement.md): Q1's profiles, Q3's timings and Q5's JIT time are
  taken in its sessions. The drop can be decided now. Kernel specialization can be decided only after Q3.
- [`./performance-parity.md`](./performance-parity.md): owns the root `BasicMath` regression, `wip/perf-basicmath`, and
  the unbiased profile that settles the `CreateBuffer` share. Q1 waits for that profile. Kernel specialization becomes
  one of its items, and its port of the branch is Phase 1's first step. That plan asks that compiled plans be decided
  before the branch is ported beyond measurement. Dropping dynamic compilation settles that question: the branch's
  fast paths are the chosen technique, not throwaway work.
- [`./conformance-docs-release.md`](./conformance-docs-release.md): decides whether the package declares AOT or
  trimming compatibility. A "yes" there makes any dynamic technique a permanent second path, which strengthens the
  drop. The conventions and roadmap edits in C3 should land with its documentation pass.
- [`./parallelism-test-plan.md`](./parallelism-test-plan.md): fast paths in parallel kernels would need its
  serial-against-parallel equality tests, in the original order of the build.
- [`./smaller-engine-items.md`](./smaller-engine-items.md): shrinking `ProberPiece` (moving `View` and `Rest` out of
  line) is unconstrained once compiled kernels no longer need its plain fields. Measure it in the same root-gate
  session as Q3.
- [`./colored-context-deferred-features.md`](./colored-context-deferred-features.md): per-context replanning would
  multiply the shapes a dynamic compiler caches. That is another argument against compiling at run time while
  replanning is open.
- [`./rule-layer-split-features.md`](./rule-layer-split-features.md) and
  [`./performance-parity.md`](./performance-parity.md): the items that bear on run time (demand-indexed local rules in
  performance parity, a "no result" primitive outcome in rule-layer split features) address fixed per-run costs and the
  primitive path, which compilation would not reach.
- [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md): no dependency.

## 9. Questions for the owner

1. Do you accept dropping compiled plans as dynamic code, and replacing the roadmap item with "kernel specialization"
   under performance parity, decided by Q3? Q3's cut-off is a scaled gain of 10% or more on a target workload chosen in
   advance, with the root set within 2%.
2. Is NativeAOT or trimming support a goal for the package? If it is, every dynamic technique becomes a permanent
   second execution path beside the interpreter, which strengthens the drop.
3. For the cleanup:
   - Should the eight uncalled array overloads be deleted, or kept with reworded comments? They are `RowOps.IsVisible`,
     `SortedWritesTable.SortAtLeast`/`SortBelow`, the array forms of `ExecutionState.StageInsert`/`StageRemove`, the
     array forms of `MutationBuffer.StageInsert`/`StageRemove`, and `RowBuffer.AddRow(uint[], int)`.
   - Should `RowOps.IsVisible` move to the test project as a visibility oracle, or be deleted?
   - Should `ActionBuffer.Push` move to the span overload of `Bindings.Push`, subject to the root gate, or keep the
     array overload?
4. Is hand-written closed-world specialization, as on `wip/perf-basicmath`, the accepted way to buy root speed, given
   that it duplicates kernel code for the shapes it covers?
5. Should decision 8 be restated as "the interpreter is the only executor; specialization is closed-world", or left
   as history with a note that compilation was not built?
6. For Q3, which target matters more: root throughput (bridge examples and root benchmarks) or the latency of small
   query children?

## 10. Review

A skeptical review of the previous version raised nine problems. Each was checked against the code, the docs and a
probe build of `eda0c61`.

**Changed.**

1. **Inventory of array overloads (sections 3, 5, 6 Q6, 7.1, 9).** Confirmed by a probe build with
   `[Obsolete(..., true)]`.
   - `Bindings.Push` over arrays is called once per match by `ActionBuffer.Push`.
   - `TableView.TryGetColumn`/`FindRow` over arrays are called by `LayeredKeyNLookup` and by the span `FindRow`.
   - `RowBuffer.AddRow(uint[], int)` and the array forms of `MutationBuffer` and `ExecutionState` have no engine
     caller. The previous claim that host writes and merges call them was wrong.

   The lists are corrected, and test callers are added. C1 now uses the Obsolete probe. Deleting the `Bindings.Push`
   array form is now optional and gated. `TableView` is removed from the delete question.
2. **Headline claim (sections 1, 4).** Rewritten. Little interpretive overhead is shown only for the right-hand side.
   Kernel headroom is unmeasured. The `BasicMath` regression (10-15%, lost inlining in general kernels) is now cited
   as evidence that kernel shape is worth about that much on root workloads. "None of them is interpretive overhead"
   is gone. Closed-world kernel specialization is now the live remedy under performance-parity, not a fallback.
   Confidence is split: about 85% for no dynamic code, about 50% for "specialization gains little". The
   recommendation stays Drop for the dynamic techniques, with its reason restated.
3. **Profile attribution (sections 3, 6 Q1).** The EventPipe figures are labelled indicative, with the reason
   (safe-point bias, GC-poll frames stripped, the subtraction of a likely artifact). Q1 now uses ETW sampling after
   performance-parity settles `CreateBuffer`, drops the `NoInlining` build as a primary measurement, and states that
   only Q3 can decide the kernel question.
4. **Thresholds (section 6).** Q1 no longer has go/no-go thresholds; it picks the target and the plans. Q3 is the
   only decider. It has one cut-off with no gap (a scaled gain of 10% or more on a target chosen in advance, root set
   within 2%) and reports the coverage of the prototyped plans. The root requirement no longer applies to a
   small-query target, except as a guard against regression.
5. **Fusion and `Replay` (sections 4, 6 Q3, Q4).** Whole-plan fusion of fixed-order plans would remove `Replay` and
   frame writes for those plans. Q3 now includes a fused plan of one or two stages, and Q4 counts fixed-order plans.
6. **`CreateBuffer` rationale (section 6 Q1).** The reason given for building the graph outside the sampled region was
   wrong: `CreateBuffer` runs for every batch's cloned state. It is removed. Q1 now waits once for performance-parity's
   unbiased profile.
7. **Small-run evidence (sections 3, 4, 6 Q2).** The scenario suites are called a sample of small runs, not a
   workload. The expectation that one optimized kernel JIT costs hundreds of microseconds is stated as unmeasured; Q5
   measures it. Q2's arbitrary thresholds are removed, and its conclusion is limited to right-hand-side dispatch.
8. **E2 (sections 2, 4).** Restated. The interpreter beats or matches the Rust on the two bridge examples that were
   timed. E2's own three workloads against `egglog -j1` have not been measured.
9. **Small errors.**
   - `b041e70` is now cited with the roadmap's wording, as the commit that dates the timing.
   - `FunctionRow`'s path is corrected to `src/Eggnog/Relations/Bridge/`.
   - The port cost of `wip/perf-basicmath`, which predates the fold, is added to sections 3, 5 and 7.2.
   - "The user's power plan" now reads "the machine's configured power plan".

**Rejected.**

- **The link to `./conformance-docs-release.md`.** The plan file exists among these plans, so the link resolves.
  Every other plan link was also checked and resolves.
- **"Deleting `Bindings.Push(uint[]...)` removes three null checks per match."** Only partly accepted. The three
  explicit `ThrowIfNull` checks go, but the array-to-span conversions keep their own null tests. So the change is a
  codegen change of uncertain sign, which is why section 5 gates it on the root set rather than claiming a saving.
