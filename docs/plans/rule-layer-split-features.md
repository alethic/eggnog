# Deferred rule-layer and split features

This plan covers ten deferred additions to the library's API and its tests: six from rule-layer design decisions, two
from the split work, one from the review of the colored e-graph literature, and one gap in the tests of the public value
API. For each one it says what it is, what exists, what it would be worth, what it would cost, and whether to build it.
Section 2 lists the neighbouring items that other plans own.

Neither of the owner's two questions falls in this workstream. Interning in colored contexts is answered in
[interning-and-value-lifetime](./interning-and-value-lifetime.md), and compiled plans in
[compiled-plans](./compiled-plans.md). Two items here touch them. The struct-lattice dry run in section 4 shows interned
values staying behind when a child is disposed. A "no result" primitive shaped as a `Try` call is what a compiled plan
would call directly (section 9).

## 1. Summary and recommendation

**Recommendation: Later, for the workstream's new API. Confidence: medium-high.** Two small pieces of work should be done
now, and both are tests and documentation: a contract test for a user lattice over a registered struct, and a test plus
guidance showing that a split's in-case schedule already bounds each case. One item waits for a named trigger. One item
moves into per-context replanning. Six should be dropped, provenance among them, and the roadmap should record why and
what would reopen each one.

**Reason.** No user has asked for any of the new API, and each item is judged here on what the library and its tests
show. Three findings from the sources and from dry runs made for this plan settle most items:

- **A per-case iteration budget can already be written.** `Schedule.Repeat(n, steps)` runs up to `n` passes and stops
  after a pass with no change, so it is a bounded `Saturate`. `Schedule.Split(rule, inCase)` accepts any schedule as
  `inCase`. In a dry run, a split with one runaway case completed under `Split(rule, Repeat(5, r))`, and its sibling's
  fact was lifted. Under `Split(rule, Saturate(r))` the same run stopped on `IterationLimit` with nothing lifted
  (section 4).
- **Provenance as designed adds little to what a host already has.** The general grounds: ablation (run again without
  a ruleset and see whether the fact disappears) answers whether a ruleset is needed for a fact, which a record of the
  rule that first wrote a row cannot answer; and D41's recipe, plus a run observer that diffs `Rows` after each step,
  already gives the step in which each row appeared or changed and the candidate rules that matched in that step. A dry
  run of that observer worked on the test fixture `PredicateTheory` (`src/Eggnog.Tests/Predicates`) with no library
  change (section 4). A further limit applies to lattice-heavy theories such as that fixture: a first-write stamp
  records the first write to a key, not the write that gave a lattice value its current value, so it misattributes
  narrowed values. For constructors, relations and unions a first-write stamp does not misattribute.
- **No real case runs away in the test fixture.** Across all 56 runs of the scoped programs over `PredicateTheory`,
  every run completed. Cases opened by a split step took 8 to 14 iterations, and no run exceeded 57 iterations (section
  4).

| # | Item | Verdict | Size if built | Trigger that would reopen or start it |
|---|---|---|---|---|
| 7 | A user lattice over a registered struct, with a case join | **Do now** as a contract test of the public value API, over a numeric bound with an open flag | small | — |
| 3 | An iteration budget per case | **Do now** as a test and documentation of `Split(rule, Repeat(n, …))`. **Later** only for a budget across all executions of a case and a report of truncated cases | small | A user for whom `n` per execution is not enough, or who must tell from the report that a case was cut off (section 5) |
| 2 | A "no result" outcome for user primitives | **Later** | small | A user whose partial function forces a filter and a computation that duplicate parse logic |
| 4 | A `Freeze` or `ScopeOptions` option that plans only local rules, or nothing | **Moved to per-context replanning** ([colored-context-deferred-features](./colored-context-deferred-features.md), item B, Later) | small | Decided by the plan comparison of Q10 in [idle-machine-measurement](./idle-machine-measurement.md), which needs no idle machine |
| 1 | Provenance: the first rule and iteration that derived each row | **Drop** as a library feature; document D41's recipe and the observer diff | medium (value-keyed only) to large (lattice values, engine) | A user debugging a named theory for whom D41's recipe, the observer diff and ablation cannot place a row's derivation |
| 5 | Tags that only a split's cases carry, readable as `T.In(caseTag)` | **Drop** | medium | A theory that needs a nested split or an in-case rule to read an outer case's view |
| 6 | Counting depth-limited splits in `RunReport.SplitsSkipped` | **Drop** | small | A user who tunes `SplitDepthLimit` and needs a signal (then a cheap flag, section 7) |
| 8 | Rule callbacks that write | **Drop** | medium | A user whose host logic must run inside a `Saturate` loop and cannot use the host loop of section 5 |
| 9 | A public untyped layer for tables with more than four key columns | **Drop** | medium | A user who needs more than four key columns and cannot use a struct key |
| 10 | A guarded lift for guard-scoped reasoning | **Drop** as a feature; keep the guidance | large | A user who reasons over if-then-else guards in volume |

## 2. Neighbouring items and their owners

Some questions near this workstream belong to other plans. This plan carries no item for them.

| Subject | Owner | State |
|---|---|---|
| Latency of transient children and the cost of idle local rules | [idle-machine-measurement](./idle-machine-measurement.md), Q8 | Its follow-up, demand-indexed local rules, is an engine change with no public API. [performance-parity](./performance-parity.md) owns it, conditional on Q8 |
| Values that die with a scope | [interning-and-value-lifetime](./interning-and-value-lifetime.md) | — |
| Control over what a non-root freeze plans | This plan, item 4, merged into [colored-context-deferred-features](./colored-context-deferred-features.md), item B | Decided by idle-machine Q10 |
| A worked modelling note on hidden-discriminator synthesis and sound numeric bounds | [conformance-docs-release](./conformance-docs-release.md), item C | — |

[idle-machine-measurement](./idle-machine-measurement.md), section 8, says its Q8 and Q10 verdicts feed this plan. Q8
feeds performance-parity, as above. Q10 feeds replanning, which absorbs item 4 (section 5).

## 3. What it is and why it was planned

Each item came from one of four places. Most are rule-layer design decisions that deferred a feature. Some are notes
from the split implementation that refused or left out a case. One comes from the review of the colored e-graph
literature. Item 7 is a gap in the tests of the public value API.

1. **Provenance.** Decision D41 in [../history/rule-layer-spec.md](../history/rule-layer-spec.md)
   (section 1.3) gives a host the run report, steps and plans. It excludes proofs and per-row explanations, and defers
   "a provenance mode (the first rule and iteration that derived each row)", because it "needs per-row write
   attribution in the engine's merge, which no milestone provides". D41 also gives the recipe a host uses instead: run
   with `RecordSteps` and `RecordPlans`, find the first step in which the rule matched, and read the rule and the facts
   it reads. The other route a host has is `RunReport.Rules` signatures plus ablation on a fresh graph: run again
   without a ruleset and see whether the fact disappears.
2. **A "no result" outcome for user primitives.** D31 makes a primitive that throws fail the rule. Only the built-in
   `+` and `-` are partial, as egglog's checked arithmetic is: an overflow in a body let drops the match (note N9 in
   section 15.3). Section 15.5 lists "a partial-primitive API" as "unscheduled; additive (D28) when a consumer needs
   one". A typical partial function renders or parses a literal, such as an identifier or an instant, and refuses on
   malformed input; a rewrite that depends on it must then not fire.
3. **An iteration budget per case.** D42 makes `IterationLimit` count "engine iterations in every scope of the run",
   cases included. So a run that splits needs a larger run-wide limit than the same theory without splits, and one
   case that does not terminate (residual risk 6 in section 11 of the rule-layer spec: a lattice with infinite
   descending chains combined with arithmetic) can use up the limit of the whole run.
4. **Control over what a non-root freeze plans.** Engine request E10 lets a freeze pre-plan a chosen set of rules. The
   rule layer plans, at a non-root scope's freeze, every rule the scope's descendants can run
   (`Scope.CollectPlanRules` in `src/Eggnog/Graph/Scope.Tree.cs`). Plans are made once per rule (residual risk 4 in
   section 11 of the rule-layer spec), so later sibling scopes reuse plans made on the first one's statistics. An
   option that plans only the scope's own local rules, or nothing, was proposed as a mitigation, beside per-context
   replanning.
5. **Tags that only a split's cases carry.** Note N109 (rule-layer spec section 15.9): `Run` requires every tag a
   reachable rule reads to resolve before any work. A tag that only a split's cases carry does not resolve at the
   top-level scope, so `Run` refuses it. N109 calls this "an over-refusal, not a soundness hole: allowing it needs a
   resolution per split node".
6. **Counting depth-limited splits.** Note N107: `SplitsSkipped` counts requests a split step found and did not take.
   A split below `SplitDepthLimit` is never looked for, so it is not counted, because counting it "would run every
   nested matcher in the deepest cases only for a statistic".
7. **A tested user lattice over a registered struct.** The public API lets a user register a struct with
   `ValueStore.Register`, define a `ValueLattice<T>` over it with a merge and a case join, and check it with
   `ValueLattice<T>.Verify`. No test covers that combination (section 4). A natural instance is a numeric bound with an
   open flag, `(decimal Value, bool Open)`, where an open bound excludes its value (as JSON Schema's
   `exclusiveMinimum` does), with two tables: `lo` merges to the greater bound (an open bound wins a tie) and joins
   cases to the lesser, and `hi` mirrors that. Such a bound is also the sound model of numeric bounds over values that
   are not integers, which the test fixture's integer stepping is not
   ([conformance-docs-release](./conformance-docs-release.md), section 3.2).
8. **Rule callbacks that write.** D21 (status **Deferred**): version 2 has no rule callbacks. Callbacks that write
   "return only when a consumer needs them". They would then opt out of watermark inheritance by overriding
   `ExternalFunction.ReadsTables`. [../architecture.md](../architecture.md), section 2.13, item 6, lists "no rule
   callbacks" among the deliberate differences from egglog.
9. **A public untyped layer.** D13 (**Kept**, narrowed): typed handles go up to four key columns. The untyped layer is
   internal "until a consumer needs more than four columns".
10. **A guarded lift.** The literature review
    ([../history/colored-egraph-literature.md](../history/colored-egraph-literature.md), section 3.1, B8) describes it
    as "Guidance now, and an optional rule-layer feature later". The feature would create one child per distinct
    guard condition, lift equalities only at the boundaries of the if-then-else (`Φ`) nodes, and let the child's
    congruence closure replace the path copies of contextual equality saturation (SAS). Review correction 11 says that
    the `Under(cond, class)` relation it relies on is a lossy form of SAS's index. It costs one propagation rule per
    constructor argument.

## 4. Current state

**Provenance (1).** Nothing records which rule wrote a row. What a host has is in
[../architecture.md](../architecture.md), section 2.6:

- `RunReport.Rules` gives per-rule matches, search time and iterations with matches, and the plan when
  `EGraphOptions.RecordPlans` is set.
- Each `RunStep` (`src/Eggnog/Diagnostics/RunStep.cs`) has the scope it ran in, its index among the run's steps, whether
  it changed the scope, and, for an iteration, the rules that matched in it. Its remarks repeat D41's recipe.
- `RunOptions.Observer` (`src/Eggnog/Scheduling/RunOptions.cs`) receives every step as it happens and "may read the
  scopes of the run (`Check`, `Get`, `CountRows`, `Rows`, …)".
- `Scope.Rows` returns `TableRow` (`src/Eggnog/Diagnostics/TableRow.cs`), which has only keys and an output.

The engine stamps every row it writes with the iteration's timestamp (`RuleAction` in
`src/Eggnog/Relations/Bridge/RuleAction.cs` writes `bindings.NextTimestamp` into the timestamp column). A merge that
changes nothing writes no row. So a row's stamp records when it last changed: the first derivation for a value-keyed
relation, the last change for a lattice-valued function, and the last rebuild for a row with class ids. The library
does not expose the stamp.

A dry run made for this plan tested the observer route. An observer snapshotted `Rows` of every table in the step's
scope after each step and compared it with the previous snapshot of that scope. A case's first snapshot was compared
with its parent's, and changes seen after a split step or lift were set aside as lifted. It ran on the test fixture
`PredicateTheory` (`src/Eggnog.Tests/Predicates`): a schema scope with a three-way split on a discriminator, compiled
with `Facts`, then a query, run with `Saturation`, that is a disjunction of two of the discriminator's values conjoined
with the presence of a path that only those two branches declare. These are counts, not timings.

| Run | Iterations | Iterations with visible changes | Rows added | Lattice values changed | Rules matched per changed row (mean, max) |
|---|---|---|---|---|---|
| Schema compile | 36 | 6 | 15 | 3 (`dom`) | 1.4, 2 |
| Query | 51 | 15 | 41 | 6 (`dom`, `types`) | 4.8, 9 |

So the observer names, for every new row and every narrowed lattice value, the step in which it appeared and a short
list of candidate rules. The list counts every rule that matched in the step. Keeping only rules whose head writes the
row's table would shorten it. That needs a public way to read a rule's head tables, or the rendering from
`Rule.ToString()`.

**"No result" (2).** `Primitive.IsPartial` (`src/Eggnog/Terms/Primitive.cs`) is internal and set only on
`Primitives.Add` and `Primitives.Subtract` (`src/Eggnog/Terms/Primitives.cs`). `PrimitiveCall.TryInvoke`
(`src/Eggnog/Rules/PrimitiveCall.cs`) catches `OverflowException` when the call site is a body let of a partial
primitive and returns `false`, which drops the match without recording a failure. Any other exception records a failure
and fails the rule (D31). So a user primitive cannot refuse by throwing. The engine's `ExternalFunction.TryInvoke`
already returns `bool`, so the engine needs no change for a "no result". Today a user writes a partial function as a
`Where` filter (`FilterCall`, `src/Eggnog/Rules/FilterCall.cs`, which drops the match on `false`) followed by a
`Compute.Of` of the total part (`src/Eggnog/Terms/Compute.cs`). That is two primitive calls, and two decodes of the
arguments, per match where one would do. No exception is thrown.

**Per-case budget (3).** `RunOptions` has `IterationLimit`, `TimeLimit`, `RowLimit`, `CaseLimit`, `SplitDepthLimit`
and `Lift`. `ScheduleExecutor.BeforeStep` (`src/Eggnog/Scheduling/ScheduleExecutor.cs`) checks the one run-wide
iteration count before every iteration and split step, and throws `ScheduleStopException`, which ends the whole run.
`ScheduleCaseRunner.Run` (`src/Eggnog/Scheduling/ScheduleCaseRunner.cs`) runs the in-case schedule in each case and lets
that exception propagate.

The in-case schedule is any `Schedule`. `Schedule.Repeat(n, steps)` (`src/Eggnog/Scheduling/Schedule.cs`;
`RepeatSchedule.Execute` breaks after a pass that changes nothing) is a `Saturate` bounded at `n` passes. So
`Split(rule, Repeat(n, horn, lattice))` bounds each case at `n` passes each time the case runs. A case cut off this way
is not treated specially: it stays live, its facts are sound under its assumption, and the lift (an intersection or a
case join) stays sound. `Repeat(n, Saturate(…))` would not bound anything, because each pass is a full saturation. The
test fixture `PredicateTheory` passes an unbounded `settle = Saturate(Horn, Lattice)` as its in-case schedule.

`SplitDriver.Step` (`src/Eggnog/Relations/Cases/SplitDriver.cs`) runs each case in phase A and lifts, then deepens in
phase B and lifts again. A later execution of the split step runs the in-case schedule again in the cases it opened.
So a `Repeat` bound applies per execution of the in-case schedule, not per case over the run.

A dry run made for this plan checked this. At a root, one relation `disc`, a domain function `dom` (intersect lattice)
with `dom(k) = {a, b}`, a `Max` function `counter` and a relation `fact`. Rules: in the case `b`, increment `counter`
without end; in either case, derive `fact(x)`. Schedule: `Saturate(r, Split(split, inCase))` with `IterationLimit` 200.

| `inCase` | Stop | Iterations | Per case (`a`, `b`) | `fact(x)` lifted to the root |
|---|---|---|---|---|
| `Repeat(5, r)` | `Completed` | 27 | 5, 20 | yes |
| `Saturate(r)` | `IterationLimit` | 200 | 2, 197 | no |

With `Repeat`, the runaway case ran four executions of five passes: phase A and phase B of each of the two executions of
the split step. The outer `Saturate` ended because the lifts stopped changing the root. A runaway value that a case join
lifts and that keeps changing the parent would keep the outer loop going until the run-wide limit. With `Saturate`, the
limit fired in phase A of the first split step, before its lift, so the sibling case's fact was lost.

A second dry run counted iterations per scope in every run of the scoped programs over `PredicateTheory` as they stood
when measured (25 tests, 56 runs; those tests are not kept in the suite, the theory is), with steps recorded:

- Every run stopped on `Completed`.
- Runs that opened split cases: two schema compiles with a discriminator split (34 and 36 iterations; cases of 8 to
  10), a schema compile that splits an undiscriminated union on a hidden discriminator (26; cases of 10), and queries
  that split on a disjunction (51 to 57; cases of 14). Host-driven cases took 4 iterations each.
- The programs over numeric bounds and over an integer domain never split. Their runs took 2 to 15 iterations. The
  longest split-free run took 24.

So a run-wide `IterationLimit` sized for the split-free runs of a theory can be too small for its split runs, which
here took up to 57 iterations against at most 24 without a split, because the limit counts the iterations of every
case.

**Freeze planning (4).** `Scope.Freeze()` takes no options, and `ScopeOptions` (`src/Eggnog/Graph/ScopeOptions.cs`) has
only `DegreeOfParallelism`. `CollectPlanRules` (`src/Eggnog/Graph/Scope.Tree.cs`) passes nothing for the root and, for
any other scope, every global, local, query, mirror and split rule its descendants can run.
`EGraphContext.Freeze(IReadOnlyCollection<EGraphRuleId>)` (`src/Eggnog/Relations/Bridge/EGraphContext.Freeze.cs`)
plans each given rule that has no plan yet, then warms the indexes of every given rule's plan (`FreezeWarmup`,
`src/Eggnog/Relations/Database/FreezeWarmup.cs`; step 5 of section 10.8 of
[../history/colored-contexts-spec.md](../history/colored-contexts-spec.md)). One list drives both planning and warm-up.

[colored-context-deferred-features](./colored-context-deferred-features.md), item B, found that when a non-root scope is
saturated by a run before it is frozen, as the fixture's schema scopes are, the run comes before the freeze. Every
`Horn` and `Lattice` rule and the schema split's matcher is planned at the first iteration of the first such scope's
run, on statistics with every derived table empty. Only the rules that run does not use (`Rewrite`, the query split's
matcher, queries) are planned at the freeze. A freeze option cannot reach the plans of the rules that the run used.

**Case-only tags (5).** `Run` resolves every tag a reachable rule or split rule reads before any work (section 6.5.3
of the rule-layer spec). `ScheduleExecutor.Resolvable` leaves out of an iteration any rule, defined during the run,
whose tags do not resolve. `EGraph.DefineSplit` and `DefineDomainSplit` (`src/Eggnog/Graph/EGraph.Splits.cs`) take a
`caseTag`. The test fixture `PredicateTheory` defines one, `Case`, for its two domain splits, but no rule anywhere in
the tests reads `T.In(Case)`.

**Depth-limited splits (6).** `SplitDriver.Step` returns at once when `depth >= DepthLimit`, and skips phase B when
`depth + 1 >= DepthLimit`. `RunReport.SplitsSkipped` (`src/Eggnog/Scheduling/RunReport.cs`) documents that such splits
are not counted. Three tests in `src/Eggnog.Tests/Cases/SplitTests.cs` assert that rule:
`SplitsSkippedCountsCoveredKeysButNeverADepthLimitedSplit`, `ASplitTheDepthLimitStopsIsNotLookedFor` and
`ASplitCutOffByTheDepthLimitIsNotLookedFor`. In the count above, no split in the fixture's programs nested.

**Struct lattice (7).** `ValueLattice<T>` (`src/Eggnog/Values/ValueLattice{T}.cs`) works over any registered type.
`LatticeMerge<T>` and `CaseJoinAdapter<T>` decode both cells, call the delegate and encode the result, which interns it
for a registered type (`InternedCodec<T>`). That machinery is already exercised together, with case joins, in children
and in lifts: the built-in `ValueLattice.Intersect<T>()` and `Union<T>()` (`src/Eggnog/Values/ValueLattice.cs`) are
built with the same public constructor over the interned `ValueSet<string>`, and `LiftTests`, `SplitTests` and both
predicate theories (`PredicateTheory` and `ContextArgumentTheory`) use them. Registration of a struct is covered by
`Point` (`src/Eggnog.Tests/Support/Point.cs`, tested in `src/Eggnog.Tests/Values/ValueTests.cs`). What no test covers
is a user's own lattice over the user's own struct: `LatticeTests.cs` verifies only built-in lattices, and the user
lattices in `LiftTests.cs` are over `long`.

A dry run made for this plan used a `readonly record struct Bound(long Lo, long Hi)`, registered with
`ValueStore.Register`, and a `ValueLattice<Bound>` whose merge intersects intervals and whose case join takes their hull.

- `Verify` passed on five samples, the unbounded interval included.
- Rules that derive a bound from `gt` and `lt` facts gave `[500, 500]` at the root.
- Two host-driven cases (`CaseSet`) holding `[1, 9]` and `[21, 29]` lifted `[1, 29]`, one value.
- 64 concurrent children of a frozen scope each derived their own bound correctly.
- `ValueStore.InternedCount` grew by 128 over those 64 children: each child interned one rule output and one merge
  result, which remained after it was disposed.

This ran on net10.0 only, in a Debug build. Nothing failed. The interning growth is the behaviour D15 describes;
[interning-and-value-lifetime](./interning-and-value-lifetime.md) already lists lattice merges among its interning
sources. A bound over `decimal`, the natural type for numeric bounds that are not integers, differs from this dry run's
in one way that matters for a test: `decimal` values such as `1.0m` and `1.00m` are equal but render differently, and
interning keeps the first form it sees.

**Callbacks (8).** None exist. A run observer sees each step and may read, but any write from it throws. Queries deliver
matches through `MatchCollector` (`src/Eggnog/Rules/MatchCollector.cs`), which a callback rule could reuse to collect
its matches during an iteration. The bridge side needs nothing new: `ExternalFunction.ReadsTables`
(`src/Eggnog/Relations/Database/ExternalFunction.cs`) already removes a rule from watermark inheritance
(`ActionReadAnalysis`, `src/Eggnog/Relations/Query/ActionReadAnalysis.cs`).

**Untyped layer (9).** D13 names the untyped members, and all are internal:

- `EGraph.DefineRelation`, `DefineFunction` and `DefineConstructor` over `IReadOnlyList<ColumnType>`
  (`src/Eggnog/Graph/EGraph.Definitions.cs`);
- `Apply` on `RelationHandle`, `FunctionHandle` and `ConstructorHandle` (`src/Eggnog/Tables/`);
- `Term.Constant(ColumnType, object)` (`src/Eggnog/Terms/Term.cs`);
- `Fact.Equal` and `Fact.NotEqual` (`src/Eggnog/Terms/Fact.cs`);
- `Compute.Of(Primitive, params Term[])` (`src/Eggnog/Terms/Compute.cs`);
- `Var(ColumnType, string)` on `RuleBuilder` and `QueryBuilder` (`src/Eggnog/Rules/`);
- `UntypedVar`, `UntypedApplicationTerm`, `UntypedComputeTerm` and `UntypedConstantTerm` (`src/Eggnog/Terms/`).

`Eggnog.Tests` reaches them through `InternalsVisibleTo` (`src/Eggnog/Eggnog.csproj`). The widest table in either
predicate theory has four key columns: `form-cases` in `ContextArgumentTheory` (`src/Eggnog.Tests/Predicates`). A
registered struct can serve as a key column (`ValueTests` defines `DefineFunction<Point, string>`).

**Guarded lift (10).** Not implemented. The general lift exists. `CaseSet.Lift` and the split step's lift lift relation
rows, function values with their case joins, and equalities with a term lift (`src/Eggnog/Cases/`; the engine's
`Lifter`).

## 5. Value

**1. Provenance.**

- *Who benefits:* users debugging a theory, who want to know why a fact holds.
- *What it can and cannot do:* provenance can narrow which ablation to run. It cannot replace ablation. "Which rule
  first wrote this row" does not say that removing that rule's ruleset removes the row. The row may come from a chain
  whose decisive step belongs to another ruleset, and when two rulesets both derive it, a stamp names one of them and
  hides the other.
- *The design this plan first proposed does not fit lattice-valued facts.* A hidden table with the same keys and a `Min`
  stamp records the first write to a key, not the write that gave the key its current value. For a lattice-valued
  function the two differ. In a lattice-heavy theory like `PredicateTheory`, many of the facts worth explaining are
  narrowed lattice values (a domain narrowed by a disequality, a type narrowed by a domain, a bound tightened by a
  comparison). Attributing those needs attribution of the changing write: in the engine's merge, as D41 says, or in a
  hidden table keyed by keys and value.
- *The existing route:* D41's recipe and the observer diff of section 4 give the step in which each row appeared or each
  lattice value narrowed, and the rules that matched in that step (1.4 and 4.8 per changed row in the dry run). That
  covers lattice values too, needs no library change, and runs wherever the host runs its theory, as ablation does.
- *If never done:* users explain derivations with D41's recipe, the observer diff and ablation.

**2. "No result".**

- *Who benefits:* library users with partial functions, such as a rendering or parsing step that refuses malformed
  input.
- *How much:* the workaround costs one extra decode and one extra delegate call on every match that the filter passes.
  That is CPU time, not allocation, and it falls as refusals rise, because the computation then runs less often. Its
  larger cost is duplicated logic: the filter must parse what the computation parses again.
- *If never done:* the workaround stands. Either a `Where` filter with a predicate that repeats the check, followed by
  the total computation, or a computation that returns a sentinel followed by a filter on it. Both are sound.

**3. Per-case budget.**

- *Who benefits:* hosts whose cases may not terminate, such as a lattice with infinite descending chains combined with
  arithmetic (residual risk 6 in section 11 of the rule-layer spec).
- *What exists:* `Split(rule, Repeat(n, …))` bounds each case per execution and keeps a truncated case live, which is the
  semantics a per-case budget would have. A host whose in-case schedule is unbounded (`Saturate`) can have one case use
  up the run-wide `IterationLimit`. If that happens in phase A, the run ends before the step lifts, and the top-level
  scope gets nothing from that step's cases, including those that had saturated (the dry run of section 4).
- *What `Repeat` does not give:* a single budget across all executions of a case (phase A, phase B and later executions
  of the step), and a signal that a case was cut off. A truncated case lifts weaker facts while the run reports
  `Completed`. A host that reads `RunReport.StopReason` to tell a run cut off by a limit from a complete one would not
  see it. A host can detect truncation from the steps: a case execution whose `n` passes all changed the case.
- *How much:* in the `PredicateTheory` fixture, nothing (section 4). For any host, the test and the guidance make the
  existing pattern known.

**4. Freeze-planning option.**

- *Who benefits:* nobody, as proposed. When a non-root scope is saturated by a run before its freeze, the rules that
  run are planned before any freeze (section 4), so the option cannot reach them. For the rules it can reach, planning
  nothing at a scope's freeze moves each rule's single plan to its first run, a query child of whichever sibling scope
  is queried first. Its statistics read through to that scope, so the dependence on the first-comer remains. Planning
  "only local rules" has the same effect on global rules. Warm-up need not be lost: because one list drives planning
  and warm-up, the option would still pass the rules that already have plans, and only the rules it leaves unplanned
  would lose their warm indexes.
- *The real fix* for residual risk 4 is per-context replanning, item B of
  [colored-context-deferred-features](./colored-context-deferred-features.md), which that plan marks Later. If
  replanning goes ahead, an opt-in on `Freeze` to replan on this scope's statistics would replace this option.
- *If never done:* nothing is lost that replanning would not recover better.

**5. Case-only tags.**

- *Who benefits:* a theory whose nested split matcher, or an in-case rule, needs to read an outer case's view, for
  example an erasing licence per case.
- *How much:* no theory or test does this, and the fixture's splits never nest (section 4). The refusal happens before
  any work, so a theory that tries it fails loudly at its first run and never silently.
- *If never done:* the over-refusal stays documented (N109).

**6. Depth-limited split count.**

- *Who benefits:* a host tuning `SplitDepthLimit`.
- *How much:* the exact count would cost a matcher run in every deepest case, for a statistic. No test workload nests
  splits.
- *If never done:* the documented behaviour stands.

**7. Struct lattice test.**

- *Who benefits:* every user who defines a lattice over a struct of their own. The public API offers this combination
  and no test covers it. The machinery is already exercised by the built-in set lattices (section 4), so the test's
  value is to pin the public contract: a lattice over the user's own registered struct, its tie rule, `decimal` values
  of different scale, and both case-join directions.
- *How much:* small but certain.
- *If never done:* a change to that contract (for example to how a registered struct's equality or rendering is used
  in merges) would reach users before any test saw it.

**8. Callbacks that write.**

- *Who benefits:* hosts that want host code to react to matches inside a run.
- *How much:* the host loop already does this: run a schedule, call `ForEachMatch` on a query, write through
  `Scope.Write`, and repeat until nothing changes. What it loses is semi-naive matching, because queries are naive
  (D34) and re-match in full on each pass, and integration with `Saturate` and the run's limits. No user has asked.
- *If never done:* the host loop stands.

**9. Untyped layer.**

- *Who benefits:* users with tables of more than four key columns. The widest table in either predicate theory has
  four.
- *If never done:* a wider key is written as a registered struct in one key column. Each distinct key value is then
  interned, and permanently (D15). The other workaround is to split the table into two joined on a value key.

**10. Guarded lift.**

- *Who benefits:* translation-validation-style workloads with many if-then-else guards. The fixture lifts value-keyed
  facts only (residual risk 5 in the rule-layer spec).
- *If never done:* the guidance stands. Write `Under(cond, class)` as rules. For a few guards, a host can do the guarded
  lift by hand in four steps:
  1. Create a child per distinct condition and assume the condition.
  2. Run the theory in the child.
  3. Extract the then-branch's best term in the child.
  4. `Make` that term in the parent inside a `Φ` node and `Union` it with the original.

## 6. Cost and risk

| # | Effort, and what dominates it | Correctness risk, and the oracle that would guard it | Risk to the root's performance | Maintenance after it lands |
|---|---|---|---|---|
| 7 | Small: one test file of about seven tests and a support type | None (tests only) | None | A test |
| 3, now | Small: one test class and documentation | None (tests and docs) | None | A test and a paragraph |
| 3, later | Small: a run-wide count per case and a `RunReport` counter of truncated cases, in `ScheduleExecutor`, `ScheduleCaseRunner`, `RunOptions` and `RunReport`. The design question is whether phase B and later executions share the budget | Low. A truncated case is kept live and its facts are sound under its assumption, so the lift stays sound. Oracles: the same run with `Repeat` of a large `n`, and the literal encoding (rule-layer spec section 8.2) | None at the root: the check runs only for scopes in the run's case set | A public option interacting with phase A and phase B |
| 2 | Small: either a `Try`-shaped delegate per arity (four delegate types, one per file under the house style) or `Compute.Of` overloads over `Func<…, (bool, TResult)>`; `Primitive.CreatePartial` overloads, a `TryInvokeCore` beside `InvokeCore`, and the lowering's choice of body and head semantics | Low. Oracles: the existing partial built-ins' tests (`Add` and `Subtract`), and the differential generator extended with a primitive that refuses on some inputs, compared semi-naive against naive and scoped against literal | Negligible: one more call shape in `PrimitiveCall`, off the engine's per-row loops (external functions are per match) | Public API: four delegate types, or a set of overloads |
| 1 | Medium for value-keyed relations only; large for lattice values, which need attribution of the changing write (in the engine's merge, or hidden tables keyed by keys and value) | Medium. Provenance writes must change nothing the theory derives, nor iteration counts, nor saturation; hidden tables must stay out of mirrors, `RowLimit`, extraction and `Rows` | None when off. When on: an extra call and write per head write | Medium: every later change to lowering, lift or hidden tables must keep provenance consistent |
| 4 | Small (the engine already takes the list) | Low | None if already-planned rules are still warmed | An option whose effect is hard to explain, and which cannot reach rules planned during a run before the freeze |
| 5 | Medium: tag resolution per split node in `Run`'s pre-check, mirror tables and copy rules for case tags, and mirror syncs inside cases. The mirror-creation guard counts frozen scopes per tag, and cases are never frozen, so it needs rethinking | Medium: mirror exactness (invariant M) in cases that are re-run in phase B and caught up after lifts | None at the root | Medium |
| 6 | Small for the exact count, but it runs every nested matcher in the deepest cases | None | None at the root; more work in deep splits | Small |
| 8 | Medium: collector, delivery after the iteration, a writer, `ReadsTables` opt-out, guard rules for writes from inside a run, and failure semantics | Medium: writes between iterations and inheritance opt-out (the colors spec, section 10.2) | None when unused | Medium: a second write path inside runs |
| 9 | Medium: the members of section 4 made public, with documentation, surface baseline (`src/Eggnog.Tests/Surface/PublicSurface.txt`) and tests by subject | Low, but type errors move from compile time to run time for its users | None | Medium: a second public API for every table feature |
| 10 | Large: a new split kind with its own lift and term lift at `Φ` boundaries, and the pointwise-guard caveat (loops) | High: soundness rests on guards being pointwise. The review's caveat on PEG loop nodes (`θ`) has no oracle in the repository | None at the root | High |

## 7. Worth-it evaluation

| # | Deciding question | Cheapest decisive experiment | Go / no-go | Idle machine? | Verdict |
|---|---|---|---|---|---|
| 7 | Is the public contract for a user lattice over a user struct pinned? | Done for an interval of two longs (section 4). The test itself is the experiment for a `decimal` bound with an open flag | Go: the contract is untested | No | **Do now** |
| 3 | Can a host bound a case today, and does any real theory need more than that? | Done: the `Repeat` dry run and the iteration count over the fixture (section 4). A host sizes `n` from the iterations its cases take, read from `RunReport.Steps` | Go for the test and guidance now. Go for the later option only if a user finds `n` per execution insufficient, or must tell truncated cases from the report | No | **Do now** (test, docs); option **Later** |
| 2 | Does a user need a partial primitive? | When a user reports a partial function: check whether its filter and computation duplicate parsing | Go if a user's filter and computation duplicate parse logic. A timed comparison only if someone asks for time | Only for a timed comparison | **Later** |
| 1 | Does debugging need more than D41's recipe, the observer diff and ablation? | None until a user reports a derivation those cannot place, on a named theory. Then record which facts stay unexplained and why | Reopen only on such a report. Then design attribution of the changing write | No | **Drop**; document the existing route |
| 4 | Do rules planned on the first sibling scope serve later ones badly? | Q10's plan comparison in [idle-machine-measurement](./idle-machine-measurement.md): the fixed corpus of sibling schema scopes that `ScopedQueryCases.CompileAll` builds on the test fixture, frozen in both orders, with `RecordPlans`; compare the plans. No idle machine is needed for the plans; only the timing half needs one | If plans differ and a later scope's queries are slower (Q10's threshold), the response is replanning (item B there), not this option | Plans: no. Timing: yes | **Moved** to per-context replanning |
| 5 | Does any theory need to read a case's view by tag? | Search a user's theory for a nested split or an in-case rule that would read an outer case. The test fixture has none | Go only on a named theory | No | **Drop** |
| 6 | Does a host need to know that `SplitDepthLimit` cut work off? | If asked: a counter at the point where `SplitDriver.Step` skips phase B because `depth + 1 >= DepthLimit` (one increment per record, no matcher run). It says that deeper splitting was possible, not that it would have found requests | Go for the counter only on a user request; no-go for the exact count | No | **Drop** |
| 8 | Is the host loop too slow or too awkward for some user? | On a user's request: run the user's reaction both as a host loop and as a prototype callback rule, and count matches re-found per pass (the host loop's query is naive) | Go if the host loop re-matches more than 10× the new matches on the user's workload. Otherwise no-go | Counts: no | **Drop** |
| 9 | Does a user need more than four key columns that a struct key cannot serve? | None until asked | Go only on a named table that a struct key cannot serve (for example, because its keys include classes, which a struct cannot hold) | No | **Drop** |
| 10 | Does any user reason over many if-then-else guards? | None until asked | Go only on a named workload | No | **Drop** as a feature; keep the guidance |

No decision here needs a timing, except the timed half of Q10, which another plan owns.

## 8. Plan if it goes ahead

Every phase follows the project's practice:

1. Implement.
2. Review from several angles: soundness, API and documentation, root cost, and a mutation-testing review of the new
   tests (each new assertion must fail when the line it guards is removed or inverted).
3. Fix.
4. Verify with full test runs on net8.0 and net10.0.

**Phase 1 (now): two contract tests, guidance, and the roadmap.**

- *Entry:* none.
- *Struct lattice tests,* in `src/Eggnog.Tests/Values/LatticeTests.cs`, with a registered `readonly record struct` of a
  `decimal` value and a `bool Open` flag, in its own file under `src/Eggnog.Tests/Support/` (one type per file), and
  two lattices, `lo` and `hi`, as section 3 defines them:
  1. `Verify` accepts both lattices on samples that include ties of value with different `Open`, and rejects a variant
     whose tie rule returns its first argument (not commutative).
  2. Rules merge through `lo` and `hi` at the root, an open bound winning a tie.
  3. `decimal` values of different scale (`1.0m` and `1.00m`) merge as one value; the test records which form a read
     returns, so a change in that behaviour is seen.
  4. Host writes merge through the lattices.
  5. A host-driven `CaseSet` lifts the looser bound, and a split rule with the bound tables in its lift spec does too.
  6. Children of a frozen scope merge independently, and concurrently.
  7. A lattice delegate over the struct that throws in a child is reported there, as
     `ALatticeThatThrowsInAChildsRunKeepsItsCause` does for `long`.
  8. `ValueStore.InternedCount` grows by every distinct encoded value, rule outputs and merge results. This documents
     D15 for struct lattices.

  The concurrent-children test covers concurrent interning from separate contexts only. Children of a frozen scope run
  serially inside themselves (`ParallelHeuristics.Serial` unless `ScopeOptions.DegreeOfParallelism` is set; colors spec
  section 10.9), and the tables are small, so no parallel merge runs. It can join the concurrent-children stress tests
  of [parallelism-test-plan](./parallelism-test-plan.md); to serve that plan's parallel paths, it would need a table
  above the parallel thresholds, or that plan's setting that sets every threshold to zero, run at degree of
  parallelism 1 and N, with the results compared.
- *Per-case bound tests,* in `src/Eggnog.Tests/Cases/SplitTests.cs`, shaped like the dry run of section 4:
  1. With `inCase = Repeat(n, r)`, a runaway case (a `Max` function and an increment rule) stays live, the sibling's
     fact is lifted, and the run stops on `Completed`.
  2. With `inCase = Saturate(r)` and an `IterationLimit`, the run stops on `IterationLimit` and the sibling's fact is not
     lifted (the behaviour the guidance warns about).
  3. The runaway case runs `n` passes per execution of the in-case schedule, counted from `RunReport.Steps`.
  4. The lifted facts with `Repeat` of a large `n` equal those of `Saturate` on a theory whose cases terminate.
- *Guidance:*
  - `Schedule.Split` and `RunOptions.IterationLimit` documentation: to bound each case, pass `Repeat(n, steps)` as
    `inCase` (not `Repeat(n, Saturate(…))`); the bound applies per execution; a truncated case stays live and lifts
    what it derived; the run still reports `Completed`; size `n` from the iterations the cases take in
    `RunReport.Steps`; and a run-wide `IterationLimit` counts every case's iterations, so a run that splits needs a
    larger one than the same theory without splits.
  - [../architecture.md](../architecture.md), section 2.6: the observer diff as a way to find the step and candidate
    rules for each new row or narrowed lattice value, beside D41's recipe, with its caveats (case baselines, lifted
    rows, candidates not filtered by table), and ablation as the decisive test of whether a ruleset is needed for a
    fact.
- *Roadmap work,* in [../roadmap.md](../roadmap.md), "Library work": mark items 3 and 7 done when their tests land.
  Move item 4 under the per-context replanning entry, decided by Q10. Move items 1, 5, 6, 8, 9 and 10 to a short
  "Considered and not planned" list, with each one's reopening trigger from section 1. Keep item 2 with its trigger,
  and the later per-case option with its trigger.
- *Exit:* the tests pass on both target frameworks; the mutation review finds no assertion that survives the removal
  of what it guards; the documentation and roadmap are updated.

**Phase 2 (on trigger): a budget across executions of a case.**

- *Entry:* the trigger in section 1.
- *Design:* `RunOptions.CaseIterationLimit` (`int?`, `null` by default; at least 1, or `Run` throws
  `ArgumentOutOfRangeException` before any work). It counts the engine iterations run in one case scope over the whole
  run: phase A, phase B and later executions of the step. Mirror syncs do not count, as for `IterationLimit`, and nested
  cases have budgets of their own. The check sits in `ScheduleExecutor.BeforeStep` for scopes in the run's case set; on
  reaching the limit it throws an internal stop that `ScheduleCaseRunner.Run` catches, and the case stays live.
  `RunReport.CasesTruncated` counts the cases cut off, by this option or by a `Repeat` that ended on a changing pass.
- *Tests:* as phase 1's per-case tests, plus a case truncated across two executions, nested budgets, the report counter
  and `ArgumentOutOfRangeException` for 0.
- *Docs:* `RunOptions`, `RunReport`, [../architecture.md](../architecture.md) section 2.6.
- *Exit:* the review checklist above, on both target frameworks.

**Phase 3 (on trigger): partial user primitives.**

- *Entry:* the trigger in section 1, and the owner's answer to question 3.
- *Design:*
  - Either public delegates `PartialFunc<T1, TResult>` through `PartialFunc<T1, T2, T3, T4, TResult>`, each returning
    `bool` with an `out TResult`, in separate files, or `Compute.Of` and `Primitive.CreatePartial` overloads over
    `Func<…, (bool, TResult)>`, which reuse the BCL delegate types.
  - `Primitive.CreatePartial` overloads for one to four parameters.
  - An internal `Primitive.TryInvokeCore` that the partial primitives implement. The total ones return `true` after
    `InvokeCore`.
  - In a body let, "no result" drops the match without recording anything. In a head, it fails the rule with a
    `RuleEvaluationException` ("primitive 'x' had no result"), as an overflow of `+` does today.
  - Optionally, move `Add` and `Subtract` onto the `Try` path (checked arithmetic with `Math` overflow tests) and
    retire the `OverflowException` catch.
- *Tests:* body drop against head failure; failure records name the primitive and bindings; the differential generator
  (`src/Eggnog.Tests/Differential/RuleLayerDifferentialTests.cs`) gains a partial primitive; the public surface baseline
  is updated.
- *Docs:* `Primitive`, [../architecture.md](../architecture.md) sections 2.5 and 2.13, and decision D31's note in the
  history (as a pointer, not an edit of the history).
- *Exit:* as above.

**If provenance is reopened.** Its trigger (section 1) would come with requirements this plan's review established:

- It must attribute the write that gave a key its current value, not only the first write, because the facts worth
  explaining are often lattice values. A key-level `Min` stamp covers only value-keyed relations.
- Stamps must use a per-run step index (`RunStep.Index`), not a graph-wide counter, so that concurrent children of a
  frozen scope get deterministic origins.
- With the option on and off, visible tables, `Changed`, iteration counts and saturation must be equal, and visible
  tables must be equal under each limit. Hidden writes for a row that reached a scope by a lift (and so has no hidden
  row) must not mark an iteration as changed.
- Its value must be measured against the observer diff, not against ablation alone.

## 9. Dependencies and ordering

- **[interning-and-value-lifetime](./interning-and-value-lifetime.md).** Phase 1's interning assertion documents D15
  for struct lattices; that plan already lists lattice merges among its sources. If scoped interning is ever built, the
  assertion changes from "grows" to "returns to its level after dispose".
- **[parallelism-test-plan](./parallelism-test-plan.md).** Phase 1's concurrent-children test covers concurrent
  interning only. It can join that plan's concurrent-children stress tests; it serves its parallel paths only with a
  table above the thresholds, or with that plan's zero-threshold setting.
- **[colored-context-deferred-features](./colored-context-deferred-features.md), item B (per-context replanning).** It
  absorbs item 4. If replanning goes ahead, its API decides whether `Freeze` gains an option.
- **[idle-machine-measurement](./idle-machine-measurement.md).** Q10's plan comparison decides item 4 through item B;
  Q8 decides demand-indexed local rules, which performance-parity owns. Neither feeds new API here.
- **[performance-parity](./performance-parity.md).** It owns demand-indexed local rules. No other interaction: phases 2
  and 3 leave the root path alone.
- **[compiled-plans](./compiled-plans.md).** A partial primitive (phase 3) should be `Try`-shaped or tuple-returning,
  which any of the compilation options can call without an exception path. Neither blocks the other.
- **[smaller-engine-items](./smaller-engine-items.md).** Its S3 suggests that a per-case budget reuse its token path;
  phase 2 should do so if built.
- **[conformance-docs-release](./conformance-docs-release.md).** It owns the modelling note on hidden discriminators
  and sound numeric bounds. Phase 1's documentation, and the guidance for the dropped items (the filter
  workaround for partial functions, the struct key for wide tables, the host loop for callbacks, the hand-written
  guarded lift, the observer diff), belong in its user documentation pass.
- **Order:** phase 1 now. Phases 2 and 3 are independent of each other and of the other workstreams, and each waits
  for its trigger.

## 10. Questions for the owner

1. Do you agree to drop items 1, 5, 6, 8, 9 and 10 (provenance, case-only tags, the depth-limited split count, write
   callbacks, the public untyped layer, and the guarded lift as a feature), and to record each one's reopening trigger
   in the roadmap? And to move item 4 under per-context replanning?
2. Is `Split(rule, Repeat(n, …))`, tested and documented, an acceptable way to bound cases, with a budget across
   executions and a truncation count left for a user who needs them?
3. For partial primitives, if they are built: four `Try`-shaped delegate types (`bool Try(args, out result)`, matching
   the engine's `ExternalFunction.TryInvoke`), or overloads over `Func<…, (bool, TResult)>`, which add no public
   types?
4. Is provenance for value-keyed relations alone useful to anyone, given that the facts worth explaining are often
   lattice values and the observer diff covers both?

## Review

A skeptical review of the first version raised nine problems. Each was checked against the sources.

**Accepted, and what changed:**

- *Per-case budget already expressible.* Confirmed in `Schedule.Repeat`, `RepeatSchedule.Execute` and
  `Schedule.Split`, and by a new dry run (section 4). Item 3 is now "Do now" as a test and guidance; the option is
  reduced to a Later budget across executions plus a truncation count. The new analysis adds one thing the review did not
  raise: a truncated case reports `Completed`, which hides it from a host that reads `StopReason`. The old owner
  question on truncation semantics is replaced.
- *Provenance design attributes the first write.* Accepted. The claim that hidden tables replace D41's engine
  requirement is withdrawn. The concurrency and change-flag gaps, and the per-run step index, are recorded as
  requirements if provenance is reopened.
- *Provenance value overstated; observer route omitted.* Accepted, and tested: an observer diff on a schema compile and
  a splitting query over the fixture found each new row and narrowed lattice value with 1.4 to 4.8 candidate rules.
  Provenance moved from Later to Drop, with a trigger measured against that route.
- *Sibling plans contradicted.* Accepted. Section 2 now names the neighbouring items and their owners. Item 4 is
  "moved to per-context replanning", decided by Q10's plan comparison, and stays under replanning in the roadmap.
- *Iteration counts from one program.* Accepted. The count now covers all 56 runs of the scoped programs over the
  fixture. The programs over numeric bounds and an integer domain never split.
- *Concurrent-children test is not the serial-against-parallel case.* Accepted; reworded, with what it would need.
- *Struct lattice rationale.* Accepted. The test pins a contract, modelled on a `decimal` bound with an open flag and a
  scale case, and test 8 counts every distinct encoded value.
- *Partial primitives.* Accepted. The exception claim is removed, the allocation and refusal-share thresholds are
  dropped, and the tuple-returning `Func` shape is offered in question 3.
- *Factual slips.* Accepted: why a split run needs a larger run-wide limit, the phase A qualifier, and the full D13
  list. On the warm-up point, the code (`EGraphContext.Freeze`) warms only the rules it is given, so the review's
  reason ("warms every cached plan") is not quite right. Its conclusion holds, because an option can still pass the
  already-planned rules to be warmed; section 5 says so.

**Rejected:** none in substance. The review's suggested owner for the follow-up on transient children was adjusted:
its measurement belongs to idle-machine Q8, and demand-indexed local rules to performance-parity, as that plan states.
