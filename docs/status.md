# Status

Last updated 2026-10-13.

## Done (on main)

- **Engine (`src/Eggnog.Relations`):** a C# reimplementation of egglog's core-relations, union-find, bridge and
  extraction (milestones M0-M8), plus colored contexts M9.0-M10.4: stores and contexts, the shared clock and id blocks,
  the layered union-find, layered tables with hidden sets, catch-up and child rebuild, freeze and dispose, hidden-aware
  scans and index builds, layered query execution (rules run in child contexts), and per-context rules in the bridge:
  the `EGraph` on a store with `EGraphContext`s (watermarks seeded from ancestors, host writes, reads, freeze, children,
  invariant checks), concurrent children under a parallelism budget, extraction over a context, and the lift
  primitives and split hooks (member maps, the lift of what holds in every live case, case sets, the split driver,
  several drivers on one scope), with lighter children (lazy pending queues and notification flags). The rule-layer
  engine requests E4, E5, E7, E8, E9 and E10 are in. Every engine type is internal.
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
- **Tests:** 11,811 engine tests and 498 library tests, green on net10.0 and net8.0 in the R2 merge's full
  runs (some engine theory rows are expanded at run time, so the totals vary between runs); 10 of the engine tests are
  opt-in soaks and seed replays, skipped unless their environment variable is set. The M10.3 rule differentials run 400
  seeds per family, and the M10.4 lift property tests 400 seeds. The library tests include the scoped Cosmos
  scenarios' R2 tests, the scope differential and the inheritance equivalence (300 seeds), and the tests of R2's five
  reviews and three verification rounds. The storage spike's 816 tests, kept as a second oracle, pass on net10.0
  and net8.0. Push/pop conformance uses egglog programs translated by hand; there is no `.egg` reader.
- **Benchmarks (`src/Eggnog.Relations.Benchmarks`):** ports of egglog's write, union-find and bridge example
  benchmarks, and the root benchmark set (M9.0). On an i7-7700K the warm C# runs egglog-bridge's `math` example about
  10% faster than the Rust (6.6 s against 7.4 s) and its `ac` example at parity (about 15.5-16 s).

## Next, in order

1. **Rule layer R3** (case splits and lift, on colors M10.4), per the rule-layer spec §9. Every engine request it
   depends on is in. Deviations of every colors milestone are recorded in the colors spec §21, what is still deferred
   in its §21.11, and the L6 and L7 reports in `docs/design/colors-m10-reports.md`; what the rule layer still defers
   is in the rule-layer spec §15.5.
2. **Cosmos integration plan:** written (`docs/design/cosmos-integration-plan.md`). Eggnog replaces the adapter's
   closure over declared facts, not its rewriter; the two reasonings run side by side in shadow mode and switch per
   fact kind. Phases 0a-0c (correctness fixes and the seam, in the adapter) and 1 (the theory on R1b, offline) can
   start now; production use needs R2, which is in, and case splits need R3. Its library requests are in its §8.3.
3. **Performance (deferred):** after M10.2, the root benchmark `RuleSetRunBenchmarks.BasicMath` is 10-15% slower than
   the M9.0 baseline (`f9b76ab`) in all four configurations, with allocation unchanged; the other root benchmark
   classes are within noise (median -1.0% over 112 cases). Unreviewed experiments toward a fix are on the branch
   `wip/perf-basicmath`. Background applications on the benchmark machine add 5-10% noise. The M10.3 and M10.4 gates
   (the M8 bridge workloads on a store-backed root, `RuleSetRunBenchmarks`, `WritesBenchmarks`, extraction over the
   root context) are also still to be timed on an idle machine, as is the time half of the per-child and case-split
   measurements (L6), and R2's per-iteration and per-write cost at the root; only dry runs, allocation counts and
   Release builds were done. A rule-layer benchmark of R2's paths has no milestone yet (the benchmark project
   references only the engine).

## How work is done

Each milestone is implemented, reviewed from several angles (soundness, root cost, completeness, and an oracle-strength
review that plants bugs to check the tests catch them), and fixed, each round of fixes checked by verification probes,
then merged into main with every suite run on both target frameworks. See `docs/engine/conventions.md` and
`docs/engine/decisions.md`.
