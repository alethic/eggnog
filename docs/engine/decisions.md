# Eggnog.Relations design decisions

These settle the open questions in `docs/design/egglog-engine-map.md` §8.3 and bind every milestone. The map's
cross-cutting decisions C1–C14 (§4.2) are adopted as written unless a line below refines them.

| # | Question | Decision |
|---|---|---|
| 1 | Deletion across contexts | A context with live descendants must not retract rows (delete, `clear_table`, subsumption of a visible row); the attempt throws. Tables declared *context-private* (for example scheduler bookkeeping) are exempt, because descendants never read them. A child may delete its own rows (tombstone) and hide visible ancestor rows. |
| 2 | Non-lattice merges | Merge functions declare whether they are lattice merges (associative, commutative, idempotent). A non-lattice merge (`new`, `old`, arbitrary primitives) is allowed only in a context that has no ancestors (the root). Writing such a table from a child throws. |
| 3 | Which classes to mark | Only losers (C3). Probes for a canonical value filter hidden rows instead of skipping the parent. |
| 4 | Determinism | Serial execution is deterministic for a given program. Parallel execution must produce the same view modulo renaming of ids and the same set of facts; numbering may differ. Golden tests run serially. |
| 5 | Fresh-id scope | Match egglog: the predicted-value cache lives per `ExecutionState` (per batch), and merges union duplicates. |
| 6 | Match-count parity | Not required. Counts are reported but may differ from egglog's. |
| 7 | Plan parity | Fix egglog's planner defects (§8.1 rows 5–7) instead of reproducing them; golden plan tests encode the fixed behaviour. |
| 8 | Dynamic reordering when compiled | Keep it: compile per-stage kernels; the interpreter keeps the recursion and reordering. |
| 9 | Containers in children | Eq-sort containers in children come after M10. Until then, interning an eq container from a child throws. |
| 10 | Rule-based rebuild | Dropped. Rebuild is the native incremental/full path only. |
| 11 | `IterationReport.changed` | Report whether the rule application changed anything; rebuild changes are reported separately. The scheduler decides saturation from both. |
| 12 | Primitives and capability contexts | Decided in the bridge milestone (M8). |

## Engine-wide rules that follow

- Visibility (tombstones, and hidden sets once contexts exist) is checked on **every** path that yields rows to a
  caller: scans with and without constraints, index builds, refinements, probe results, header subsets, point lookups,
  zero-variable atoms. This fixes egglog's stale-row hole (map §8.1 row 1).
- Every merge path stores the merge function's output and reports only real changes (§8.1 rows 3, 4, 16, 17).
- `merge_all` always runs in dependency-stratum order (§8.1 row 11).
- Every egglog defect listed in §8.1 is fixed where it would be ported, with a comment citing the Rust file and line.
