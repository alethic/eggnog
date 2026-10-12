# Status

Last updated 2026-10-12.

## Done (on main)

- **Engine (`src/Eggnog.Relations`):** a C# reimplementation of egglog's core-relations, union-find, bridge and
  extraction (milestones M0-M8), plus colored contexts M9.0-M10.2: stores and contexts, the shared clock and id blocks,
  the layered union-find, layered tables with hidden sets, catch-up and child rebuild, freeze and dispose, hidden-aware
  scans and index builds, and layered query execution (rules run in child contexts). Every engine type is internal.
- **Library (`src/Eggnog`):** rule layer R1a-R1b at the root: definitions, typed relations, functions with lattice
  merges and constructors, rules, schedules, host reads and writes, queries, limits, diagnostics, extraction, and the
  Cosmos literal scenarios 01-07.
- **Tests:** 9,054 engine tests and 307 library tests, green on net8.0 and net10.0.
- **Benchmarks (`src/Eggnog.Relations.Benchmarks`):** ports of egglog's write, union-find and bridge example
  benchmarks, and the root benchmark set (M9.0). On an i7-7700K the warm C# runs egglog-bridge's `math` example about
  10% faster than the Rust (6.6 s against 7.4 s) and its `ac` example at parity (about 15.5-16 s).

## Next, in order

1. **Colors M10.3, then M10.4** (`docs/design/colored-contexts-spec.md` §10, §11 and §17), including the engine
   requests of `docs/design/rule-layer-spec.md` §5.2 (E4, E5, E7, E8, E9, E10) and its §13 edits. Earlier deferrals
   and deviations are recorded in the colors spec §21. Push/pop conformance uses hand-translated egglog programs; there
   is no `.egg` reader.
2. **Rule layer R2** (scopes; needs M10.3) and **R3** (case splits and lift; needs M10.4), per the rule-layer spec §9.
3. **Cosmos integration plan:** written (`docs/design/cosmos-integration-plan.md`). Eggnog replaces the adapter's
   closure over declared facts, not its rewriter; the two reasonings run side by side in shadow mode and switch per
   fact kind. Phases 0a-0c (correctness fixes and the seam, in the adapter) and 1 (the theory on R1b, offline) can
   start now; production use needs R2, and case splits need R3. Its library requests are in its §8.3.
4. **Performance (deferred):** after M10.2, the root benchmark `RuleSetRunBenchmarks.BasicMath` is 10-15% slower than
   the M9.0 baseline (`f9b76ab`) in all four configurations, with allocation unchanged; the other root benchmark
   classes are within noise (median -1.0% over 112 cases). Unreviewed experiments toward a fix are on the branch
   `wip/perf-basicmath`. Background applications on the benchmark machine add 5-10% noise.

## How work is done

Each milestone is implemented, reviewed from several angles (soundness, root cost, and an oracle-strength review that
plants bugs to check the tests catch them), and fixed, then merged into main with every suite run on both target
frameworks. See `docs/engine/conventions.md` and `docs/engine/decisions.md`.
