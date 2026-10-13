# Roadmap

What is open or planned. The library's model and the engine are described in [`architecture.md`](architecture.md);
the reasoning behind the items below, where it is recorded, is in [`history/`](history/README.md).

## Engine work

- **Parallelism test plan.** The engine's parallel paths are in place (parallel table merges, index builds and
  rebuilds, rule-level and morsel parallelism, concurrent children of a frozen context) and tested for serial and
  parallel equivalence on the existing suites. Still to do: serial-against-parallel equality with lattice merges that
  change values (egglog's own parallel tests never reach that branch); runs above the parallel thresholds at one and at
  N threads; and stress tests of concurrent children compared with serial runs.
- **Compiled plans.** Compile right-hand-side programs, merge functions and join-stage kernels to delegates with
  `System.Linq.Expressions`, cached per plan and stage order, and test them against the interpreter with the existing
  random-program generators. The engine already gives every primitive a compiled plan would call on a hot path an
  overload over arrays and offsets, on a sealed type, for this purpose (see [`engine/conventions.md`](engine/conventions.md)).
- **Performance parity.** Profiling, SIMD probes, pooling and parallel tuning, measured with `egglog -j1` and `-jN` on
  egglog's math microbenchmark and other large programs, and on a 200,000-row frozen root with a thousand children.
- **The root `BasicMath` regression.** `RuleSetRunBenchmarks.BasicMath` runs 10-15% slower than the root baseline
  recorded before colored contexts were added (commit `f9b76ab`), in all four of its configurations, with allocation
  unchanged. The other root benchmark classes are within noise.
- **Smaller engine items:**
  - a flag on the engine's lift report that says when the lift memo answered, so that `RunReport.Lifts` can count only
    lifts that were computed;
  - an engine hook that reports each lift of a split step as it happens, with its own figures;
  - a cancellation token for split-step lifts, checked between rounds (a lift already started now runs to its end);
  - taking a failure a run's own catch-up recorded together with the run's own failure, in
    `EGraphContext.RunRulesCore` (the library already takes such failures first, so this is not needed for soundness);
  - lazy `IndexCatalog` objects, only if an idle-machine gate shows the root unharmed;
  - shrinking `ProberPiece` (moving `View` and `Rest` out of line) if the `RuleSetRunBenchmarks` parallel gate shows the
    prober's growth matters;
  - `ComputeCostsFromRootSorts` and `FunctionToDag` over a context (only the on-demand extractor takes one today, and the
    library needs no more);
  - a fault-injection hook, so that a test can fail when the child's canonical-in-parent check in the member map is
    removed.
- **Left open by design:** parent-visible ids winning over smaller ids after a late parent mint (its measured cost is
  linear in cases times terms per round and independent of the root, so there is no reason to hurry it), pruning
  and flattening of hidden sets, per-context replanning of rules, eq-sort containers in child contexts (interning an eq
  container from a child throws today), deduplicating split-driver keys across pieces, a memory budget per run that
  disposes settled split records first (cases live for the whole top-level run today), running a split's cases in
  parallel, which needs a way to thaw a sealed parent before the lift (cases run one after another on the parent's
  thread today), and shared ids for a term that several sibling cases derive (each case mints its own today). The
  reasoning is in [`history/colored-contexts-spec.md`](history/colored-contexts-spec.md) ("Points for the decisions
  owner, and deferred work" and "Still deferred") and in the literature review
  ([`history/colored-egraph-literature.md`](history/colored-egraph-literature.md)).

## Benchmarks and timed gates

Everything below needs an idle machine; until now only Release builds, dry runs and allocation counts were taken, on a
machine shared with other work.

- **Root gates.** Each change to the engine's root paths is gated on the root benchmark set (`RuleSetRunBenchmarks`,
  `ConstrainedScanBenchmarks`, `IndexRefreshBenchmarks`, `DisplacedRebuildBenchmarks`, `ActionLookupBenchmarks`,
  `WritesBenchmarks` and the union-find benchmarks) staying within 2% of the baseline recorded before colored contexts.
  The comparisons for the colored-context changes have not been timed. They include the egglog-bridge `math` and `ac`
  workloads on a store-backed root, the cost of building an `EGraph`, and extraction through the context API against
  the root API. Allocation-only dry runs moved little (`math` 9.80 GB to 9.61 GB, `ac` 15.47 GB to 15.57 GB).
- **Per-child cost.** Time per child flat within 1.5x as the root grows from 20,000 to 2,000,000 rows (bytes are
  already flat, at about 96 KB for 100 unions and a rebuild, and about 145 KB with a flush and a rule run); and memory
  per structure (layer tables, hidden sets, overlay, cursors, watermarks, indexes) with time and failures reported
  separately for case splits against full clones.
- **Library paths.** A benchmark of the library's own paths, beside the engine's in `src/Eggnog.Benchmarks`: queries on a
  frozen scope and their transient children, mirror syncs, the per-match cost of comparing class constants, the
  catch-up each call makes first, a split step's request search and case wrappers, the in-case schedule run again in
  each live case, the lift memo check, and the per-iteration and per-write overhead at the root. Then timed gates for
  them, including a split step that finds nothing new and a host lift.
- **Latency of transient children,** including the per-iteration cost of idle local rules on a query child under a frozen
  scope with 100 and 1,000 local rules (one of the Cosmos plan's requests, below).

The last timing of egglog-bridge's examples predates the store-backed root (commit `47eb33d`): on an i7-7700K, warm,
`math` took about 6.6 s against the Rust's 7.4 s, and `ac` about 15.5-16 s on both. It is to be repeated on the
current code.

## Library work

- **Requests of the Cosmos integration plan** ([`history/cosmos-integration.md`](history/cosmos-integration.md),
  "Library requests") that are still open:
  - latency figures for transient children and idle local rules (above); if idle local rules prove costly,
    demand-indexed local rules, which skip in an iteration every local rule none of whose body tables has rows newer
    than the parent's watermark;
  - values that die with a scope, or interning scoped to a context (interning pools are permanent per graph today;
    the workarounds are canonicalizing literals and retiring graphs);
  - a tested user lattice over a registered struct with a case join (a numeric bound, checked with
    `ValueLattice<T>.Verify`), which the API allows but no test covers;
  - provenance for triage: the first rule and iteration that derived each row (no proofs or explanations exist; the
    workaround is the run report's per-rule statistics and steps, plus ablation on a fresh graph);
  - control over what a non-root freeze plans: a `Freeze` or `ScopeOptions` option that plans only the scope's own
    local rules, or nothing, or per-context replanning of global rules, so that rules planned on the first frozen
    scope's statistics do not serve every later one;
  - an iteration budget per case (today one `IterationLimit` counts every case's iterations);
  - a "no result" outcome for user primitives, so that a partial primitive can drop a match instead of failing the rule
    (only the built-in `Add` and `Subtract` are partial today);
  - documentation: a worked note on synthesizing hidden discriminators, and the limits of the Cosmos test theory's
    numeric bound and domain rules.
- **Split refinements:**
  - tags that only a split's cases carry, so that an in-case rule or nested split matcher can read `T.In(caseTag)`
    (`Run` refuses such a read today, before any work);
  - counting in `RunReport.SplitsSkipped` the splits that `SplitDepthLimit` keeps from being looked for (not done
    because it would run every nested matcher in the deepest cases only for a statistic).
- **Push/pop conformance.** egglog programs that intern eq-sort containers in a child (`container-rebuild.egg`,
  `container-fail.egg`, `vec.egg`) wait on eq containers in children. Programs that need sorts the engine's bridge lacks
  (`UnstableFn`, `Map`) and the web demo's `math.egg` (74 rules, 21 push blocks, which needs nothing missing but has not
  been translated) wait on a `.egg` reader, which is not planned, or on translation by hand.
