# Status

Last updated 2026-10-13.

## Done (on main)

- **Engine (`src/Eggnog/Relations`):** a C# reimplementation of egglog's core-relations, union-find, bridge and
  extraction (milestones M0-M8), plus colored contexts M9.0-M10.4: stores and contexts, the shared clock and id blocks,
  the layered union-find, layered tables with hidden sets, catch-up and child rebuild, freeze and dispose, hidden-aware
  scans and index builds, layered query execution (rules run in child contexts), and per-context rules in the bridge:
  the `EGraph` on a store with `EGraphContext`s (watermarks seeded from ancestors, host writes, reads, freeze, children,
  invariant checks), concurrent children under a parallelism budget, extraction over a context, and the lift
  primitives and split hooks (member maps, the lift of what holds in every live case, case sets, the split driver,
  several drivers on one scope), with lighter children (lazy pending queues and notification flags). The rule-layer
  engine requests E4, E5, E7, E8, E9 and E10 are in. The engine is compiled into the `Eggnog` assembly and package
  (namespace `Eggnog.Relations`, decision 13 in `docs/engine/decisions.md`), and every engine type is internal.
- **Library (`src/Eggnog`):** rule layer R1a-R1b at the root: definitions, typed relations, functions with lattice
  merges and constructors, rules, schedules, host reads and writes, queries, limits, diagnostics, extraction, and the
  Cosmos literal scenarios 01-07. Rule layer R2 (rule-layer spec §9 R2, notes §15.8, N60-N89): every scope runs on an
  `EGraphContext`, the root included (`RootEngineScope` and the engine lock are gone); scope trees with tags, frozen
  bases, per-query children and local rules; scoped reads through mirror tables synced before use; queries on frozen
  scopes in transient children; extraction over any scope; per-thread-domain guards, with deferred releases that never
  fail another thread's call; class constants equated in a rule body compared in each context the rule runs in, never
  refused at definition (N86); a scope's `Version` counts only what its own calls change, never the catch-up with an
  ancestor's writes (N87); while a `Write` batch is open, its writable ancestors refuse writes and runs (N88). R2
  changed no engine file.
- **Rule layer R3** (rule-layer spec §9 R3, notes §15.9, N90-N115): case splits and lift on colors M10.4. Split rules
  (`EGraph.DefineSplit`, and `DefineDomainSplit` with guards, D43) are taken by a split step (`Schedule.Split`): one
  cached engine `SplitDriver` per split node and run, whose cases are case scopes that run the in-case schedule, are
  lifted before they are split further, live until the run ends, and are disposed then. Host-driven cases
  (`Scope.OpenCases`, `CaseSet.Add`, `CaseSet.Lift`); the lift spec from the tables' `Lift` flags and the lattices'
  case joins; contradicted cases, and scopes under them, answering only what needs no model (D45,
  `ScopeContradictedException`); split and lift steps in run reports; `CosmosTheory.Cases.cs` and the scoped
  scenarios' split tests (02, 04, 07). R3 changed no engine file, and the public surface only gained members.
- **Reviews and fixes of R3:** four reviews (soundness, API, a mutation oracle review, completeness) added probes and a
  model-based split oracle, and three verification rounds followed; their fixes are N104-N114. Scopes under a stopped
  case answer as it; structural calls and extractor queries check the stop flag after a catch-up; a split assumption's
  conflict is an evaluation failure of the run; `DefineDomainSplit` needs a lattice and accepts scoped guards;
  `RunReport.Changed` follows the scope's `Version` over a split step; the `SplitsSkipped` and cancellation docs say
  what the run does; `CaseSet.Add` no longer races `Dispose`; a lattice merge that throws in the catch-up a call makes
  first fails that call and faults the scope, a read's or extraction's included (N113); and one that throws while a
  split step catches a case up fails the run (N114).
- **Deferred from R3** (rule-layer spec §15.5): three engine changes with no milestone (a memo-hit flag on the lift
  report so that `RunReport.Lifts` counts only computed lifts, N92; an engine hook reporting each lift of a split step,
  N93; a cancellation token for split-step lifts, N108); tags that only a split's cases carry (N109); a rule-layer
  benchmark of R2's and R3's paths; and the timed gates.
- **Tests:** one project, `src/Eggnog.Tests`, holds the library's tests and, under `Relations/` (namespace
  `Eggnog.Relations.Tests`), the engine's: 12,447 tests (11,811 engine and 636 library tests at the R3 merge), green
  on net10.0 and net8.0 (some engine theory rows are expanded at run time, so the totals vary between runs); 10 of the
  engine tests are opt-in soaks and seed replays, skipped unless their environment variable is set, and the tests that
  measure allocation run on their own, after the parallel ones. The M10.3 rule differentials run 400
  seeds per family, and the M10.4 lift property tests 400 seeds. The library tests include the scoped Cosmos
  scenarios' R2 and R3 tests, the scope differential and the inheritance equivalence (300 seeds), the split oracle
  against the prototype's split rules (300 seeds; 5000 were green once), the model-based split oracle (400 seeds), and
  the tests of R2's five reviews and three verification rounds and of R3's four reviews and three verification rounds.
  The storage spike's 816 tests, kept as a second oracle, pass on net10.0 and net8.0. Push/pop conformance uses egglog
  programs translated by hand; there is no `.egg` reader.
- **Benchmarks (`src/Eggnog.Benchmarks`):** ports of egglog's write, union-find and bridge example
  benchmarks, and the root benchmark set (M9.0). On an i7-7700K the warm C# runs egglog-bridge's `math` example about
  10% faster than the Rust (6.6 s against 7.4 s) and its `ac` example at parity (about 15.5-16 s).

## Next, in order

1. **Cosmos integration plan:** written (`docs/design/cosmos-integration-plan.md`). Eggnog replaces the adapter's
   closure over declared facts, not its rewriter; the two reasonings run side by side in shadow mode and switch per
   fact kind. Phases 0a-0c (correctness fixes and the seam, in the adapter) and 1 (the theory on R1b, offline) can
   start now; production use needs R2, and case splits need R3; both are in. Its library requests are in its §8.3.
2. **Performance (deferred):** after M10.2, the root benchmark `RuleSetRunBenchmarks.BasicMath` is 10-15% slower than
   the M9.0 baseline (`f9b76ab`) in all four configurations, with allocation unchanged; the other root benchmark
   classes are within noise (median -1.0% over 112 cases). Unreviewed experiments toward a fix are on the branch
   `wip/perf-basicmath`. Background applications on the benchmark machine add 5-10% noise. The M10.3 and M10.4 gates
   (the M8 bridge workloads on a store-backed root, `RuleSetRunBenchmarks`, `WritesBenchmarks`, extraction over the
   root context) are also still to be timed on an idle machine, as is the time half of the per-child and case-split
   measurements (L6), R2's per-iteration and per-write cost at the root, and the cost of R3's split steps and lifts;
   only dry runs, allocation counts and Release builds were done. A rule-layer benchmark of R2's and R3's paths has no
   milestone yet; `src/Eggnog.Benchmarks` now references the library and can hold it beside the engine's benchmarks.

Deviations of every colors milestone are recorded in the colors spec §21, what is still deferred in its §21.11, and the
L6 and L7 reports in `docs/design/colors-m10-reports.md`; what the rule layer still defers is in the rule-layer spec
§15.5.

## How work is done

Each milestone is implemented, reviewed from several angles (soundness, root cost, completeness, and an oracle-strength
review that plants bugs to check the tests catch them), and fixed, each round of fixes checked by verification probes,
then merged into main with every suite run on both target frameworks. See `docs/engine/conventions.md` and
`docs/engine/decisions.md`.
