# Smaller engine items

This plan covers the eight items listed under "Smaller engine items" in [the roadmap](../roadmap.md). Each one is
evaluated separately, because they have little in common beyond their size. Neither of the owner's two questions
(interning under colored contexts, and compiled plans) falls in this workstream. They are answered in
[interning-and-value-lifetime](./interning-and-value-lifetime.md) and [compiled-plans](./compiled-plans.md).

| # | Item | Verdict |
|---|---|---|
| S1 | A flag on the engine's lift report saying that the lift memo answered | Do now, as an internal engine flag with tests. The public members wait for a consumer or the pre-release API review |
| S2 | An engine hook reporting each lift of a split step as it happens | Drop |
| S3 | A cancellation token for split-step lifts, checked between rounds | Optional, low priority |
| S4 | Report a failure recorded by the run's own catch-up together with the run's own failure, in `RunRulesCore` | Drop. Document the behaviour instead |
| S5 | Lazy `IndexCatalog` objects | Drop |
| S6 | Shrink `ProberPiece` (move `View` and `Rest` out of line) | Do after an experiment: first a cheap padding test inside the `BasicMath` investigation in [performance-parity](./performance-parity.md); the prior is low |
| S7 | `ComputeCostsFromRootSorts` and `FunctionToDag` over a context | Drop |
| S8 | A test that fails when the member-map check's canonical-in-parent test is removed | Do now (small). It needs a reflection-based test, not a hook |

## 1. Summary and recommendation

**Recommendation: Do now**, for a narrowed bundle of about one day: S8, and S1 as an internal engine flag. S3 is
optional. Drop S2, S4, S5 and S7. Hand S6 to the `BasicMath` investigation, starting with a padding experiment.
**Confidence: medium-high.**

The case for each part:

- **S1, internal only.** No test observes the lift memo. A dry run for this plan disabled the memo check in
  `Lifter.Run` and ran the full net10.0 suite: no test failed (section 3). An internal `LiftReport.Memoized`, set in
  the memo branch and asserted by the memo tests, closes that gap. It needs no owner decision, because the engine's
  `LiftReport` is internal. The public side (`Memoized` on the library's `LiftReport`, a memo count on `RunReport`) is
  additive and can land at any time. No consumer has asked for it, and `RunReport.Lifts` already documents that it
  includes memo answers. So it waits, unless the owner wants `Lifts` to change meaning before a stable release
  (question 1).
- **S8** needs no production hook. A reflection-based corruption test of the kind that
  `UnionFindInvariantCheckerTests` already uses kills the recorded surviving mutant. A prototype passed on `eda0c61`
  and failed when the check was removed (section 3). Its value is modest: the line it guards is mostly redundant with
  the overlay check (section 4).
- **S3** threads the run's token into the split driver's lifts. Its gain is unmeasured and probably small: a case's
  own iteration is just as uncancellable as a lift, so the bound becomes the larger of one iteration and one lift
  round, not "one round instead of sixteen", and later rounds of a lift usually stage less than the first. Nothing
  depends on it.
- **The dropped items** serve no consumer. S2 has no consumer, and its reconstruction is exact in count and order.
  S4 cannot occur through the library. S5 saves bytes only on layers that are touched but never probed. S7 serves
  egglog's `print-function`, which Eggnog has no frontend for.
- **S6** is the one item that might matter for performance, but the prior is low (section 6). A padding experiment on
  `3a6e0b7` tests the same hypothesis for a fraction of the cost of the shrink prototype.

## 2. What it is and why it was planned

All eight items are leftovers. Each was noticed while the colored contexts or the rule layer were being built, and
each was deferred because it was not needed for soundness or for the feature being built.

- **S1 (N92).** Rule-layer spec §3.6 first said that `RunReport.Lifts` counts the lifts computed, not the ones the memo
  skipped. As built, it counts every lift a split step ran, memo answers included. The reason is that the engine's
  `LiftReport` cannot tell a memo answer from a lift that wrote nothing: both report `Rounds = 0` and
  `Changed = false`. See [rule-layer spec](../history/rule-layer-spec.md), "15.5 Deferred" (which records it as "No
  milestone") and note N92 in "15.9 R3: case splits and lift".
- **S2 (N93).** Rule-layer spec §6.11 asked for one `RunStep` per lift. The driver lifts inside `SplitDriver.Step`,
  between case runs, and the engine request (E8) defined no callback. The rule layer therefore reconstructs the lift
  steps from the step's `SplitReport.Lifts` counter each time a case is about to run and when the step ends (N93).
- **S3 (N108, D42).** The `Scope.Run` token documentation first said that cancellation is checked "between
  iterations, split steps and lift rounds". The engine's `SplitOptions` carries no token, so a split-step lift runs to
  its end. D42 states the resulting bound: a run overshoots a limit or a cancellation by at most one iteration or one
  lift.
- **S4.** `EGraphContext.RunRulesCore` catches the context up (`EnsureClean`) before running. If that catch-up records
  a lattice-merge failure, the run later reports that failure as its own, and the run's own failure, if any, is lost.
  The database keeps only the first failure. Rule-layer spec "15.5 Deferred" lists this as "not needed for soundness",
  because the library takes catch-up failures first (N113, N114).
- **S5.** The colored-contexts spec (§14.2, and the M9.4 and M10.4 notes in "21.5" and "21.10") reduced the bytes of a
  bare child and of an empty layer. It made pending queues and notification flags lazy, but kept `IndexCatalog`
  objects eager. A lazy catalog adds a null test to the root's index lookups, so the spec gated it on an idle-machine
  run showing the root unharmed ([colored-contexts spec](../history/colored-contexts-spec.md), "21.11 Still
  deferred").
- **S6.** Layered query execution (colored-contexts spec, "M10.2: Layered query execution") split a prober into
  pieces, one per layer. A `Prober` grew from 48 B to 72 B: the piece now carries `Layer` and `View`, and the prober
  carries `Rest` and `RestCount`. The spec's §9.5/§14.1 row in "21.10" says that shrinking it means moving `View` and
  `Rest` out of line in every kernel, and leaves that to the idle-machine gate ("21.11").
- **S7.** Engine request E5 asked only for the on-demand extractor to take a context. `ComputeCostsFromRootSorts` and
  `FunctionToDag` stayed root-only. The rule layer's second stage did not need them ("21.11 Still deferred").
- **S8.** The oracle review of the lift primitives found a surviving mutant. Removing the member-map check "member ...
  is not canonical in the parent" from the invariant checker fails no test, because the map is built from the overlay
  that the check reads. The record says "only fault injection could fail it" (colored-contexts spec, the §12.7 row of
  "21.10", and "21.11").

## 3. Current state

### S1: the memo flag

- `src/Eggnog/Relations/Cases/LiftReport.cs` (internal, like every engine type) has no memo flag. The memo branch in
  `Lifter.Run` (`src/Eggnog/Relations/Cases/Lifter.cs`) returns a report with `Rounds = 0` and `Changed = false`, and
  fills `LiveCases`, `VacuousCases` and `ParentContradicted`. A full lift whose first round stages nothing
  (`writes.IsEmpty`) returns the same shape.
- `SplitDriver.Lift` (`src/Eggnog/Relations/Cases/SplitDriver.cs`) adds one to `SplitReport.Lifts` for every lift.
  `ScheduleExecutor.EndSplitStep` (`src/Eggnog/Scheduling/ScheduleExecutor.cs`) sums those counts into
  `RunReport.Lifts`. The public documentation of `RunReport.Lifts` (`src/Eggnog/Scheduling/RunReport.cs`) already says
  that it includes "those answered at once because nothing they read had changed since the last full lift". The
  public counter is therefore correct as documented.
- The public `LiftReport` (`src/Eggnog/Cases/LiftReport.cs`) mirrors the engine's report and has no memo flag either.
- The Cosmos plan reads `RunReport` only for triage ([cosmos-integration](../history/cosmos-integration.md), §4 and
  the triage table). No consumer has asked to tell memo answers apart.
- Tests. `LiftMemoTests` (`src/Eggnog.Tests/Relations/Cases/LiftMemoTests.cs`) recognize a memo hit only by
  `Changed == false` and `Rounds == 0`. Its other tests check the opposite direction: that the memo does not skip a
  wider spec or a wider limit.
- **Dry run for this plan.** On a private clone of `eda0c61`, in Debug on net10.0, the memo condition in `Lifter.Run`
  was replaced by `false`, which turns off the memo. Two test runs followed:
  - the 276 tests whose names match `Cases`, `Split`, `Lift` or `Diagnostics` all passed;
  - the full `Eggnog.Tests` suite ran 12,448 tests: 12,438 passed, 10 opt-in tests were skipped, and none failed (the
    count includes the prototype test of S8 below).

  The memo is therefore a performance feature that no test observes. A regression that breaks it, such as a signature
  that never matches, would show only as slower split steps. The dry run shows a test gap. It does not show that a
  public counter is worth having.

### S2: the per-lift hook

- `ScheduleExecutor.ObserveLifts`, `CaseRunEnded` and `EndSplitStep` use the frames of `SplitStepFrame`
  (`src/Eggnog/Scheduling/SplitStepFrame.cs`). Each time a case is about to run (`ScheduleCaseRunner.Run` calls
  `ObserveLifts` first), and when the step ends, they record one `RunStepKind.Lift` step for each lift that
  `SplitReport.Lifts` gained since the last case run ended.
- What is exact and what is not:
  - **Count and order** of the lift steps are exact.
  - **Changed** comes from the scope's `Version` over the window. It is exact in a window that holds one lift.
  - **Elapsed** is a share of the time since the last case run ended. That window holds the lift, and, when another
    record follows in phase A, that record's `RunEach` up to its first runnable case: the catch-up (`EnsureClean`) of
    its cases and the disposal of the ones found contradictory. The catch-up can outweigh a memo-answered lift, so
    `Elapsed` is approximate in general, not only in multi-lift windows.
- A window holds more than one lift only in phase A of `SplitDriver.Step`, when `CaseSet.RunEach` runs no case of the
  next record because every one of its cases is found contradictory on catch-up and disposed
  (`src/Eggnog/Relations/Cases/CaseSet.cs`). Phase B cannot produce one: a phase-B lift needs `deeper`, which needs a
  case run (`deeper |= inCase.Run(...)`, then `if (deeper && Lift(...))`), and a case run calls `ObserveLifts` first.
- `DiagnosticsTests` (`src/Eggnog.Tests/Diagnostics/DiagnosticsTests.cs`) checks that the number of lift steps equals
  `RunReport.Lifts`.
- A lift `RunStep` (`src/Eggnog/Diagnostics/RunStep.cs`) carries only `Changed` and `Elapsed`, beyond the kind and the
  scope.

### S3: cancelling split-step lifts

- `Lifter.Run` and the engine's `CaseSet.Lift` take a `CancellationToken` and check it before every round
  (`src/Eggnog/Relations/Cases/Lifter.cs`, `src/Eggnog/Relations/Cases/CaseSet.cs`). Every round then brings the parent
  and every case up to date (`parent.EnsureClean()`, `@case.EnsureClean()`) before staging. The library's public
  `CaseSet.Lift(LiftOptions?, CancellationToken)` (`src/Eggnog/Cases/CaseSet.cs`) passes its token through.
- `SplitDriver.Step(EGraphContext, int, SplitReport)` and `SplitOptions` carry no token. `SplitDriver.Lift` calls
  `set.Lift(spec, options.Lift)` with the default token.
- `ScheduleExecutor.BeforeStep` checks the run's token, then the iteration, time and row limits, before every
  iteration and split step. A case's own iterations are therefore cancellable between them, but an iteration, once
  started, is not. The lifts between case runs are not cancellable at all.
- The executor holds the run's token in a private field (`readonly CancellationToken cancellationToken`). It builds one
  `SplitDriver` per split node and run, with a fresh `Relations.SplitOptions` built from the run's own limits
  (`ScheduleExecutor.DriverFor`). `SplitSchedule.Execute` (`src/Eggnog/Scheduling/SplitSchedule.cs`) is the one
  production caller of `Step`.
- Engine tests call `Step` about 41 times under `src/Eggnog.Tests/Relations/Cases/` (`SplitDriverTests` 20,
  `SplitDriverSharingTests` 7, `SplitStepRevisitTests` 7, `SplitDriverOracleTests` 5, `CaseWorkloadRestampTests` 2),
  with more under `Relations/Contexts/`.
- A lift is bounded by `LiftOptions.RoundLimit` (default 16) and `CandidateLimit` (default 64). No lift has been timed.
  The only figures are byte counts: a lift over one case's union, fact and rule run allocates 24.5 KB (colored-contexts
  spec §14.2, from the footprint mode).
- The documentation states one bound for limits and cancellation together: [architecture](../architecture.md) ("an
  iteration or a lift cannot be interrupted, so a run overshoots by at most one of them"), the `RunOptions` class
  summary (`src/Eggnog/Scheduling/RunOptions.cs`: "overshoots a limit or a cancellation by at most one engine
  iteration or one lift"), the token parameter of `Scope.Run` (`src/Eggnog/Graph/Scope.Engine.cs`), and rule-layer D42
  and N108. The `RunOptions.TimeLimit` and `RowLimit` members say "overshoots by at most one iteration", which omits
  the lift.
- Tests. `LifterTests.TheTokenIsCheckedBetweenRounds` (`src/Eggnog.Tests/Relations/Cases/LifterTests.cs`) cancels from
  a case join during round 1 of a two-round lift, and `LiftTests.CancellationIsCheckedBetweenRounds`
  (`src/Eggnog.Tests/Cases/LiftTests.cs`) does the same for the library's host lift. Both are templates for a
  split-step test.

### S4: pairing a catch-up failure with the run's own failure

- In a child, `RunRulesCore` (`src/Eggnog/Relations/Bridge/EGraphContext.Run.cs`) calls `EnsureClean`, which rebuilds,
  and a rebuild records a failing lattice merge in the child database without throwing.
  - `Database.RecordFailure` (`src/Eggnog/Relations/Database/Database.cs`) keeps the first failure only
    (`Interlocked.CompareExchange`).
  - After the merge, `TakeFailure` takes that first failure, the run throws `PanicException` with the catch-up's
    message, and the run's progress is not committed. The merge's catch block also skips `Commit` when
    `database.Failure` is set.
  - The outcome is sound: the context is faulted and the run is not recorded. Only the message is incomplete.
- The library never reaches this path with a pending catch-up failure:
  - `Scope.CatchUp` takes each writable scope's catch-up failure right after its rebuild (N113).
  - `ScheduleCaseRunner` and `SplitSchedule` take a case's catch-up failure on entry to the case and at the end of each
    split step (N114).
  - Within one case's in-case schedule, the parent does not change between iterations: lifts happen between case
    runs, on the same thread. The run's own `EnsureClean` therefore finds nothing new to catch up.
- The engine is internal (every type in `src/Eggnog/Relations` is `internal`), so no external caller reaches
  `RunRulesCore` directly.

### S5: lazy index catalogs

- `IndexCatalog<TIndex>` (`src/Eggnog/Relations/Indexes/IndexCatalog.cs`) creates its insert lock lazily. A catalog
  object has three references and a flag. An allocation count in a private clone of `eda0c61` (Release, net10.0,
  `GC.GetAllocatedBytesForCurrentThread` over 1,000 constructions) gives 48 B for each of `IndexCatalog<TupleIndex>`
  and `IndexCatalog<ColumnIndex>`, so 96 B per `TableInfo`.
- `TableInfo` (`src/Eggnog/Relations/Database/TableInfo.cs`) creates two of them eagerly. A child pays one `TableInfo`
  per touched table, in each `TableLayer` (`src/Eggnog/Relations/Layers/TableLayer.cs` builds `Info` per layer).
- The catalogs are read through `TableInfo.GetIndex` and `GetColumnIndex`, which a stage reaches whenever it builds a
  prober over that layer (once per index lookup, not per row), and through `ResetIndexes` (per merged table),
  `FreezeIndexes` and `ExecutorInvariants`. A layer whose table is probed therefore needs its catalogs anyway; a lazy
  catalog saves bytes only on layers that are touched but never probed.
- Measured steady-state bytes, from the colored-contexts spec §14.2 and the footprint mode
  (`src/Eggnog.Benchmarks/Relations/Contexts/ContextFootprint.cs`, which prints "TableInfo (two IndexCatalogs)" as a
  part):

  | What | Bytes |
  |---|---|
  | A bare child | 2,454 B |
  | An empty layer | 1,062 B |
  | A case | 2,975 B |
  | A case with one union and one fact | 13.4-13.7 KB |
  | The same case, lifted | 17.3-19.9 KB |

  The two catalogs are about 9% of an empty layer (96 B of 1,062 B), the most favourable comparison for S5.

### S6: shrinking `ProberPiece`

- `ProberPiece` (`src/Eggnog/Relations/Execution/ProberPiece.cs`) holds `Kind` (a byte enum), `Layer` (a byte),
  `HasOuter` and `MayHoldInvisible` (two bools), `Outer` (an `OffsetRange`, two ints) and four references: `Column`,
  `Tuple`, `Sparse` and `View`. That is 12 B of scalars, padded to 16, plus 32 B of references: 48 B.
- `Prober` (`src/Eggnog/Relations/Execution/Prober.cs`) holds `Node` (a reference), `First` (a piece), `Rest` (a piece
  array) and `RestCount` (an int, padded to 8): 72 B. The colored-contexts spec records 72 B against 48 B before
  layering ("21.10", the §9.5/§14.1 row) and the same probe confirmed both sizes with `Unsafe.SizeOf`.
- Size arithmetic for a shrink, by the same layout rules:
  - moving `View` to a side array, with `RestCount` in the piece's padding: piece 40 B, prober 56 B (it still holds
    `Rest`);
  - also moving `Rest` out of the prober: prober 48 B.

  Only the second gets back to 48 B, which is why the spec and the roadmap name both `View` and `Rest`.
- Allocation moved little with the growth: the parallel triangle probe went from 970 KB to 991 KB (+2.2%), and the
  parallel 200k math probe from 14.135 MB to 14.14 MB (+0.05%) (colored-contexts spec §14.1).
- How probers are held and passed:
  - They are per-position scratch. `JoinRun.TakeProbers` (`src/Eggnog/Relations/Execution/JoinRun.Plan.cs`) allocates
    `new Prober[Math.Max(count, MaxProbers)]` per position, once per join task (per morsel in a parallel run). A task
    holds the plan's positions times `MaxProbers` probers: for typical rules, a few hundred bytes to a few kilobytes.
  - The root path passes them by reference: `RefineScan` takes `in ProberPiece`, `ProbeRefineScan` takes
    `ref Prober`, and the kernels take `ref` locals into the scratch array (`src/Eggnog/Relations/Execution/
    JoinRun.Index.cs`, `JoinRun.Intersect.cs`).
  - By-value copies through `Prober.Piece(int)` occur in `RefineHits` (`JoinRun.Index.cs`), which handles layer hits
    from several pieces, and in `NoteDriver` (`JoinRun.cs`), which runs only when `ExecutorCounters.Enabled`.
- The roadmap's "root `BasicMath` regression" puts `RuleSetRunBenchmarks.BasicMath` 10-15% slower than the baseline
  `3a6e0b7` in all four configurations, with allocation unchanged. The commit messages on the branch
  `wip/perf-basicmath` (for example `f886771`) describe it as the regression after "colors M10.2", the step the
  colored-contexts spec calls layered query execution, which is also the step that grew the prober. No published
  measurement attributes any part of it to the prober's size.
- The unreviewed experiments on `wip/perf-basicmath` (`5efe32d`, `7551ab7`, `f886771` and later) target the kernel
  branches that layering added: they split `RefineScan` and `RunIntersectTwo` into single-piece and layered paths, fuse
  root paths, and move pool rents and index lookups out of line. They read `piece.View` and `piece.Layer` in
  `BuildPiece` and `RefineScan`, so a shrink would conflict with them. The branch predates the fold of the engine into
  `src/Eggnog` and would need rebasing. None of its experiments was measured conclusively.

### S7: extraction members over a context

- `Extractor.ComputeCostsFromRootSorts` (`src/Eggnog/Relations/Extraction/Extractor.cs`) and
  `Extractor.FunctionToDag` (`src/Eggnog/Relations/Extraction/Extractor.Functions.cs`) take an `ExtractionSchema` over
  the root.
- Their only callers are engine tests under `src/Eggnog.Tests/Relations/Extraction/`. The library extracts through the
  on-demand extractor (`CreateExtractor`, used by `src/Eggnog/Graph/EngineScope.cs` and `ContextEngineScope.cs`), which
  takes a context.
- `FunctionToDag` is egglog's `function_to_dag`, which serves the frontend's `print-function`. Eggnog has no `.egg`
  frontend (rule-layer D14).

### S8: the member-map fault test

- `EGraphContext.CheckMemberMaps` (`src/Eggnog/Relations/Bridge/EGraphContext.Lift.cs`) builds a `MemberMap` from the
  overlay with `MemberMap.Build` and checks it. For membership it compares only `map.MemberCount` with the number of
  non-local overlay members. It never tests that each map member is a member of its group's overlay slot. Per member it
  checks order, locality, canonicity in the parent, and that the member finds its group root in the context.
- The overlay check in `Context.Invariants.cs` already reports overlay members that are not canonical in the parent,
  under "overlay members:". The member-map line "is not canonical in the parent" therefore adds something only when
  `MemberMap.Build` emits an id the overlay does not hold, and the count comparison would catch most such faults too.
- There is no fault-injection hook in the engine, and no practice of adding one. The tests corrupt private state
  through reflection instead:
  - `UnionFindInvariantCheckerTests.AMemberNotCanonicalInTheParentIsReported`
    (`src/Eggnog.Tests/Relations/Contexts/UnionFindInvariantCheckerTests.cs`) replaces an overlay member through the
    overlay's `members` and `classOf` fields;
  - `MemberMapTests.TheMemberMapCheckReportsACachedMapThatDiffersFromTheOverlay` replaces the cached map.
- **Dry run for this plan.** A prototype test was added to a private clone of `eda0c61`. It builds a case under a root
  that has displaced one id, corrupts the case's overlay in the same way as the union-find checker test so that it
  names the displaced id, and asserts that `ContextInvariantException.Violations` holds a violation starting with
  "member maps: member " and ending with "is not canonical in the parent".
  - It passed with `MemberMapTests` (14 tests passed, net10.0).
  - With the check disabled (`if (false && parentFinder.Find(m) != m)`), it failed and the other 13 passed.

  The recorded mutant is killed without any change to production code. The test corrupts the overlay, a fault the
  overlay check also reports, so it proves that the member-map line runs, not that it catches a fault nothing else
  catches.

## 4. Value

The engine is internal, so every item's value reaches users only through the library (`Scope.Run`, `RunReport`,
`RunStep`, `CaseSet`) or through maintainers.

| Item | Who benefits | By how much | If never done |
|---|---|---|---|
| S1 (internal) | Maintainers | Makes the lift memo testable, which closes a gap the dry run shows (no test fails without the memo). The planned library-path "lift memo check" benchmark can also verify that it measures memo hits | A broken memo would go unnoticed until a split workload slows down |
| S1 (public) | Library users tuning splits, should any ask | A user could tell lifts that ran rounds from memo answers; today `Lifts = 50` could be 50 full lifts or 49 memo answers | `RunReport.Lifts` stays as documented (all lifts). Additive later at no extra cost |
| S2 | Users reading `RunReport.Steps` | Exact `Elapsed` per lift step, and exact `Changed` in multi-lift windows. No consumer has asked for it; the Cosmos plan reads `RunReport.Rules`, `StopReason` and `Steps` only for triage ([cosmos-integration](../history/cosmos-integration.md), §4 and the triage table) | Lift steps stay windowed approximations, documented as such |
| S3 | Hosts that cancel runs: the Cosmos adapter cancels on `ForceHorn`, a runtime override and schema disposal (cosmos-integration, "Cancellation") | Cancellation latency during a split step falls from at most one lift to at most the larger of one iteration and one lift round. Each round first catches the parent and every case up, and later rounds usually stage less than the first, so the gain is well under the 16x the round limit suggests. Unmeasured; at the Cosmos plan's scale (B3: p99 of 5 ms per derivation with splits) probably tens of microseconds | D42's bound stays at "one iteration or one lift", which is documented and sound. The library's host `CaseSet.Lift` already honours its token |
| S4 | Direct engine callers only, of which there are none outside tests | A second failure message in a case that the library cannot produce | Nothing observable through the library |
| S5 | Memory per case | 96 B per touched layer that is never probed. A probed layer needs its catalogs anyway | Nothing a budget notices. The Cosmos budgets are in time and transient megabytes (B3: at most 4 MB transient for the host-driven case track) |
| S6 | Root performance, possibly | Unknown, and the prior is low (section 6). If the prober's size explains part of the `BasicMath` regression, up to that part of 10-15% on that benchmark; if not, nothing | The `BasicMath` regression stays as it is, owned by performance-parity |
| S7 | Nobody today | It would serve a "print a table as terms" API on scopes, which is not planned | Nothing |
| S8 | Maintainers | Closes a recorded surviving mutant for a line that is mostly redundant with the overlay check. With a membership check added (section 7), the line would guard a fault no other check reports | The checker keeps a line that no test can fail |

## 5. Cost and risk

| Item | Effort | What dominates it | Correctness risk and oracles | Root performance risk | Maintenance after it lands |
|---|---|---|---|---|---|
| S1 (internal) | Small | Tests: `LiftMemoTests` assertions and one `SplitStepRevisitTests` case | Low. The flag is set in one place, the memo branch of `Lifter.Run`. `CaseSet.Lift`'s all-disposed path builds its own report, is not a memo answer and leaves it false. Oracles: `LiftMemoTests`, `SplitDriverOracleTests` | None. One bool per lift; no root path | One internal property and one internal counter |
| S1 (public) | Small | The public documentation and the API review | Low | None | One public property on `LiftReport` and one counter on `RunReport` (or a changed meaning; see question 1) |
| S2 | Medium if built as a hook (a callback interface on the driver, re-entrancy rules while the parent's domain is held, ordering with `ObserveLifts`). Small if built as a list of per-lift reports on `SplitReport` | The interaction with `ScheduleExecutor`'s frames | Medium for a hook: a callback runs while the lift holds the domain, which `Lifter` today forbids except for case joins | None | A second path for lift diagnostics |
| S3 | Small | The token in the per-run `SplitOptions` built by `DriverFor` (or an optional `CancellationToken cancellationToken = default` parameter on `Step` plus an internal accessor for the executor's token), one test per layer, and the overshoot documentation in five places | Low. A cancelled lift leaves only whole rounds in the parent, each sound on its own (each round's writes hold in every live case, colored-contexts spec §11.2). Oracles: the two between-rounds tests above, recast for a split step, and `RunLimitTests`-style checks that the scope stays usable | None. No root path | None for the test call sites with either signature |
| S4 | Small | Deciding how two messages combine in `PanicException` | Low, but the only test would have to fault a catch-up and a run together through the engine, a state the library never makes | None | A test of an unreachable library state |
| S5 | Small to medium | Touching every `TableInfo.Indexes` and `ColumnIndexes` site, and the clone constructor | Low | A null test per index lookup and per reset. Small, since a lookup happens once per prober build, not per row; but any root change needs the 2% root gate on an idle machine | One more lazily initialised field to reason about under concurrency (`TableInfo` is shared by concurrent children of a frozen context, X14) |
| S6 | Padding experiment: small. Shrink: medium | Shrink: every kernel that reads `piece.View`, plus `Prober.Rest`/`RestCount`; `ProberPiece`'s fields are documented as "plain fields so compiled kernels can read them", so the shape also binds [compiled-plans](./compiled-plans.md). It also conflicts with the kernel splits on `wip/perf-basicmath` | Medium for the shrink: layered visibility refinement reads `View`. Oracles: the layered executor differential tests, `ExecutorInvariants`, the colored-context property tests | The shrink is a root performance change by definition; it needs the root gates | A side array per position for `View`, and `Rest` held outside the prober |
| S7 | Small to medium | A context-aware `ExtractionSchema` | Low | None | Code with no caller |
| S8 | Small | One reflection-based test, prototyped already; optionally a membership check | None for the test; low for the check (checker code only) | None | The test depends on private field names `members` and `classOf`, as `UnionFindInvariantCheckerTests` already does |

## 6. Worth-it evaluation

| Item | Deciding question | Cheapest decisive experiment | Go/no-go threshold | Idle machine? | Preliminary verdict |
|---|---|---|---|---|---|
| S1 (internal) | Does any test observe the memo? | Done for this plan: disable the memo and run the suite (section 3) | Go if no test fails without the memo (the dry run says none fails) | No | **Do now** |
| S1 (public) | Does a consumer, or the owner, want memo answers told apart in the public report? | None: a question for the owner and consumers | Go if a consumer asks, or if the owner wants `Lifts` to count computed lifts only before a stable release | No | **Later**, at the pre-release API review or on request |
| S2 | Does any consumer need exact per-lift figures beyond what the windowed reconstruction gives? | None that decides it today. If it comes up, count multi-lift windows on the library-path benchmark's split workloads once they exist ([idle-machine-measurement](./idle-machine-measurement.md)) | Go only if a consumer asks | No | **Drop**. If wanted later, the driver can append each lift's `LiftReport` to the `SplitReport`, which gives exact per-lift `Changed` and memo flags with no callback |
| S3 | Can a single lift take long enough to matter for cancellation? | The library-path benchmark's "host lift" and "split step that finds nothing new" timings ([idle-machine-measurement](./idle-machine-measurement.md)); record the p99 of one lift on `CaseWorkloadRestampTests`-sized and Cosmos-sized workloads | Worth doing on value alone if one lift exceeds 1 ms at p99 on Cosmos-sized workloads, or 10% of a typical `TimeLimit` | Yes, for the timing | **Optional, low priority**. Cheap, and it makes the split path honour the token as the host lift does, but nothing depends on it |
| S4 | Can the library reach a run whose own catch-up records a failure? | Static reading, done for this plan (section 3). A confirming check: assert in DEBUG at `RunRulesCore`'s entry, in a private clone, that a child's `Database.Failure` is null after `EnsureClean` when called from the library, and run the library suites | Go only if the assertion fires in a library test | No | **Drop**. Document in `RunRulesCore`'s remarks that a catch-up failure is reported as the run's failure |
| S5 | Do index catalogs matter to per-case bytes? | Run the footprint mode (`--context-footprint`, allocation only) and count the touched layers that are never probed in a typical case | Go only if catalogs on never-probed layers exceed 5% of a lifted case's bytes, and the root gate stays within 2% | Footprint: no. Root gate: yes | **Drop**. The catalogs are about 9% of an empty layer but are needed on every probed layer; the saving is 96 B per touched, never-probed layer, against 13-20 KB per case |
| S6 | Does the prober's size explain any of the `BasicMath` regression? | Step 1: on `3a6e0b7`, pad `Prober` from 48 B to 72 B with unused fields (checked with `Unsafe.SizeOf`), and A/B `BasicMath` padded against unpadded. Step 2, only if step 1 reproduces a slowdown: a shrink prototype on `eda0c61` that moves `View` to a per-position side array and `Rest` out of the prober, checked at 48 B with `Unsafe.SizeOf` in the prototype before any timing, then A/B on `RuleSetRunBenchmarks` (all four configurations, `BasicMath` first) | Each step, ABBA order with the in-process toolchain and server GC, preceded by an A/A run to measure the spread. Step 1 goes on only if padding slows `BasicMath` by more than the A/A spread. Step 2 goes if the shrink alone recovers at least 3% and at least twice the A/A spread on `BasicMath` in the parallel configurations; else drop | Yes | **Do after an experiment**, owned by performance-parity, inside the `BasicMath` investigation. Prior: low |
| S7 | Does any planned library API need extraction-wide costs or a function DAG in a child scope? | None needed: check [rule-layer-split-features](./rule-layer-split-features.md) and the Cosmos requests | Go only when such an API is scheduled | No | **Drop** |
| S8 | Can the mutant be killed without a production hook? | Done for this plan: the prototype test kills it (section 3) | Go | No | **Do now**, as a test |

Why the S6 prior is low. A join task holds a few hundred bytes to a few kilobytes of probers, which stay in the L1
data cache; the root path passes pieces by `in` and `ref`, and by-value copies occur only on layered paths and under
counters (section 3). The more likely causes of the root regression are the kernel branches that layering added
(single-piece against layered paths), which are what the `wip/perf-basicmath` experiments target. The padding
experiment tests the size hypothesis directly and cheaply, and it does not touch the kernels, so it does not conflict
with that branch.

Sunk design effort is not a reason to build any of these. S2, S4, S5 and S7 are recorded in the history as "no
milestone", and none has a consumer.

## 7. Plan if it goes ahead

One branch, two small phases and an optional third, merged together. Each phase follows the project's practice:

1. implement;
2. review from several angles: correctness, documentation, and a mutation-testing review of the new tests;
3. fix;
4. verify with full `Eggnog.Tests` runs on net8.0 and net10.0.

### Phase 1: S8, the member-map fault test

- **Entry:** none.
- **Work:**
  - Add `AMemberMapMemberNotCanonicalInTheParentIsReported` to `MemberMapTests`
    (`src/Eggnog.Tests/Relations/Cases/MemberMapTests.cs`). It reuses the corruption of
    `UnionFindInvariantCheckerTests.AMemberNotCanonicalInTheParentIsReported` on a case's overlay, and asserts the
    specific "member maps:" violation in `ContextInvariantException.Violations`, not just the prefix. The overlay
    check also fires, so a prefix-only assertion would not kill the mutant.
  - Optional, and the better fix for the redundancy: add a membership check to `CheckMemberMaps`, so that each map
    member must be a member of its group root's overlay slot. Test it by corrupting the map that `MemberMap.Build`
    returns (or the cached map) with an id outside the overlay. With it, the canonical-in-parent line guards a fault
    that the overlay check cannot see. The alternative, deleting the redundant line, is also acceptable; the plan
    prefers the membership check because it adds a real guard for the lift's completeness inputs at checker cost only.
  - Move the field-access helpers to a shared test helper if a third class needs them.
- **Exit:** the test passes; with the canonical-in-parent line disabled it fails (the prototype showed this). Close
  the row in colored-contexts spec "21.11" and remove the roadmap bullet.

### Phase 2: S1, the internal memo flag

- **Entry:** none. No owner decision is needed, because every type touched is internal.
- **Work:**
  - Add `LiftReport.Memoized` to the engine's report (`src/Eggnog/Relations/Cases/LiftReport.cs`), set in
    `Lifter.Run`'s memo branch. `CaseSet.Lift`'s all-disposed path is not a memo answer and leaves it false.
  - Add an internal memo count to `SplitReport` (`LiftsMemoized`), counted in `SplitDriver.Lift`, so that split-step
    tests can assert it.
- **Tests:**
  - `LiftMemoTests` asserts `Memoized` on every hit and its absence on every non-hit.
  - A `SplitStepRevisitTests` case asserts that a revisit with nothing new is memoized.
  - Re-run the memo-off mutant of section 3 and see at least one test fail.
  - A no-production-change alternative, if the owner prefers no new member: a lattice workload in `LiftMemoTests`
    whose cases share a key, with a counting case join. A memo hit runs no round and so calls no join, while a full
    lift calls it.
- **Exit:** the memo-off mutant fails at least one test; both TFMs green.
- **Docs:** close N92's engine half in rule-layer spec "15.5"; note that the public side waits.
- **Deferred public side.** `Memoized` on the library's `LiftReport` (`src/Eggnog/Cases/LiftReport.cs`) and
  `RunReport.LiftsMemoized` go to the pre-release API review in
  [conformance-docs-release](./conformance-docs-release.md). They are needed there only if the owner wants `Lifts` to
  change meaning; otherwise they wait for a consumer. With the internal counter in place, either change is a few
  lines.

### Phase 3 (optional): S3, the token for split-step lifts

- **Entry:** none.
- **Work:**
  - Put the run's token in the per-run `Relations.SplitOptions` that `ScheduleExecutor.DriverFor` builds, and have
    `SplitDriver.Lift` call `set.Lift(spec, options.Lift, options.CancellationToken)`. `DriverFor` already holds the
    token, so no accessor is needed and no test call site changes. The alternative is an optional
    `CancellationToken cancellationToken = default` parameter on `SplitDriver.Step`, passed by `SplitSchedule.Execute`
    through a new internal accessor for the executor's private `cancellationToken`; it also leaves the 41 test call
    sites alone.
- **Tests:**
  - An engine test: a case join cancels the token during round 1 of a two-round split-step lift; `Step` throws
    `OperationCanceledException`, round 2's writes are absent, the scope is not faulted, and the records stay for
    `DisposeRecords`. Recast from `LifterTests.TheTokenIsCheckedBetweenRounds`.
  - A library test: `Scope.Run` with a lattice whose case join cancels the run's token stops with
    `OperationCanceledException` before the lift's second round, and the scope remains usable.
  - A mutation check that dropping the token from `SplitDriver.Lift` fails the engine test.
- **Exit:** both TFMs green.
- **Docs.** Split the overshoot statement in two, and update every place that states it together:
  - cancellation overshoots by at most one iteration or one lift round;
  - limits (`IterationLimit`, `TimeLimit`, `RowLimit`) overshoot by at most one iteration or one lift, since only the
    token is checked between lift rounds.

  The places: [architecture](../architecture.md) (the limits paragraph), the `RunOptions` class summary and its
  `TimeLimit` and `RowLimit` members (`src/Eggnog/Scheduling/RunOptions.cs`; the members' "at most one iteration"
  omits the lift today and should be corrected whether or not S3 lands), the token parameter of `Scope.Run`
  (`src/Eggnog/Graph/Scope.Engine.cs`), and rule-layer D42 and N108 (close N108's row in "15.5").

### Not in the bundle

- **S6:** handed to [performance-parity](./performance-parity.md) as the two-step experiment in section 6, inside the
  `BasicMath` investigation.
- **S2, S4, S5, S7:** remove the bullets from [the roadmap](../roadmap.md) with a one-line reason each, or move them to
  a "considered and dropped" list. Mark their rows in the history's deferred tables as closed by decision. Add the
  `RunRulesCore` remark described for S4.

### Benchmarks

None of the bundle touches a root path, so no root gate is needed.

S1 adds one bool per lift. The library-path benchmark's "lift memo check" can use the internal memo count to verify
that it measures memo hits once that benchmark exists ([idle-machine-measurement](./idle-machine-measurement.md)).

## 8. Dependencies and ordering

- **No hard dependencies:** phases 1 to 3 can land before any other workstream.
- **S1's public side** belongs with the API review in [conformance-docs-release](./conformance-docs-release.md), and
  only if the owner wants it there.
- **S1 and the library-path benchmarks:** the internal memo count helps
  [idle-machine-measurement](./idle-machine-measurement.md) verify its "lift memo check" benchmark.
- **S3** sits next to the split refinements in [rule-layer-split-features](./rule-layer-split-features.md). If that
  workstream adds an iteration budget per case or per-case limits, it should reuse the same token path.
- **S6 depends on:**
  - the idle-machine campaign ([idle-machine-measurement](./idle-machine-measurement.md));
  - the `BasicMath` investigation in [performance-parity](./performance-parity.md), and its decisions on the kernel
    splits of `wip/perf-basicmath`, which a shrink would conflict with.

  It interacts with [compiled-plans](./compiled-plans.md), since compiled kernels would read `ProberPiece`'s fields
  directly. Settle the piece's shape before kernels are generated against it.
- **S5** would also depend on the root gates. Dropping it removes that dependency.

## 9. Questions for the owner

1. **The meaning of `RunReport.Lifts` (S1, public side).** It counts every lift, memo answers included, as documented.
   Do you want it to count only computed lifts, as rule-layer spec §3.6 first said, before a stable release? If not,
   the plan leaves the public report alone until a consumer asks, and then adds `LiftsMemoized` beside `Lifts`.
2. **The overshoot bound (S3).** If S3 lands, is "cancellation overshoots by at most one iteration or one lift round;
   limits by one iteration or one lift" the guarantee you want? Checking `TimeLimit` between lift rounds as well would
   need a stop callback into the engine, which this plan does not propose.
3. **S8's extra check.** Add a membership check to `CheckMemberMaps` (preferred), or delete the redundant
   canonical-in-parent line, or keep only the test?
4. **The dropped items.** May S2, S4, S5 and S7 be closed as decided, with their roadmap bullets removed? Or do you
   want them kept as visible "not planned" entries?
5. **S6.** Should the padding experiment run as part of the `BasicMath` investigation you deferred, or wait until the
   idle-machine campaign has timed the root gates?

## Review

A skeptical review of the first version of this plan raised thirteen points. Each was checked against the code and
the docs.

Changed:

- **S6 size arithmetic.** Moving only `View` gives a 40 B piece and a 56 B prober; reaching 48 B also needs `Rest` out
  of the prober. The prototype now moves both and checks the size with `Unsafe.SizeOf` before any timing.
- **S6 threshold.** The old go clause ("the parallel gate fails by more than 2% without the shrink") was already met
  by the known regression. The threshold now asks for a recovery of at least 3% and twice a measured A/A spread.
- **S6 cheaper experiment and prior.** A padding experiment on `3a6e0b7` comes first; the prior is stated as low, with
  the evidence (prober scratch per task, pieces passed by reference on the root path, `TakeProbers`, `RefineScan`
  taking `in ProberPiece`), and the conflict with the kernel splits on `wip/perf-basicmath` is noted.
- **S1 split.** The internal flag and its tests are done now with no owner dependency; the public members wait for the
  API review or a consumer. The counting-case-join alternative is mentioned.
- **S2 reconstruction.** Multi-lift windows occur only in phase A, when a whole record is found vacuous; `Elapsed` is
  approximate in general because the window includes the next record's catch-up and vacuity disposal.
- **S2 experiment.** Removed; the drop rests on the absence of a consumer.
- **S3 docs.** The overshoot statement is split into cancellation (one iteration or one lift round) and limits (one
  iteration or one lift), and the `RunOptions` class and member documentation are added to the places to update.
- **S3 cost.** The call-site count is corrected to about 41 under `Relations/Cases`. The token goes in the per-run
  `SplitOptions` built by `DriverFor`, or an optional parameter, so no test call site changes. The earlier objection
  to `SplitOptions` is withdrawn: it is built per run.
- **S3 value.** Restated as unmeasured and well under 16x, and S3 is now optional.
- **S1 contradiction.** Section 5 now says, as phase 2 did, that `CaseSet.Lift`'s all-disposed path is not a memo
  answer.
- **S8 value.** Stated as closing a recorded mutant for a mostly redundant line; a membership check (preferred) or
  deleting the line is offered.
- **S5 argument.** Compared against the per-layer cost (about 9% of an empty layer), with the stronger point that a
  probed layer needs its catalogs anyway.
- **S6 citation.** The unpublished working record is no longer cited. The dating of the regression now cites the
  `wip/perf-basicmath` commit messages (`f886771`) and the colored-contexts spec's "M10.2: Layered query execution";
  the unpublished ABBA confirmation is removed.

Rejected: none of the thirteen. One detail of the S6 point was narrowed rather than adopted as stated: the review
listed `JoinRun.cs`'s by-value copy as a layered path, but `NoteDriver` runs on every path, only when
`ExecutorCounters.Enabled`; the plan says so.
