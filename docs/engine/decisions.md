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
| 12 | Primitives and capability contexts | As egglog: the engine has no capability contexts. External functions receive the raw `ExecutionState`, a trust boundary (`EGraph.RegisterExternalFunction`). The capability contexts (pure, read, write, full) belong to the layer above, which registers a primitive once per context it is valid in and picks the id per call site: a semi-naive rule uses pure on the left-hand side and write on the right, a naive rule read and full, merge functions write, one-shot commands full, `check` read (map §3.7). The bridge guarantees the discipline those contexts rely on: a left-hand-side primitive (`EGraphRuleBuilder.QueryPrim`) runs after the join on every match of every semi-naive variant and a result of nothing drops the match; a right-hand-side call (`CallExternalFunc`) that produces nothing panics; writes are staged and visible only after the run. With colors, the `ExecutionState` a primitive receives is bound to its context, which is what makes `TableAction`/`UnionAction` context-correct. |

## Requirements from the literature review

From `docs/design/colored-egraph-literature.md` (including its review corrections). These bind the colors milestones
(M9, M10) and the lift.

| # | Requirement | Why |
|---|---|---|
| L1 | The lift must find facts that hold in a case only through an ancestor row mentioning a class's **winner**. With losers-only marking such rows are never copied into the case, so the lift must also look up ancestor rows by each merged class's winner. | Review finding G1(b): under decision 3 the lift as written misses facts (verified example). |
| L2 | Each child keeps the member list of every class it merged (the parent-canonical ids in it), and `ScanMerges` is implemented from those lists, reporting a merged class's own canonical id paired with itself when it is a parent id. | G1(a) and B7; the spike iterated the marked set, which losers-only marking breaks. |
| L3 | A debug invariant checker for contexts, run in tests after every rebuild: overlay keys canonical in the parent once catch-up is done; a child's equalities include its parent's; every hidden row has a reason; child-local ids never appear in an ancestor; cursors valid. | B2, modelled on Easter Egg's checks. |
| L4 | Inserting a contradiction fact in a case sets the case's stop flag; the rebuild loop checks it; the case answers only `IsContradictory`, is skipped by the lift, and is disposed. | G4: vacuous cases still copy rows on every merge. |
| L5 | Split helpers support demand-driven splitting (split only when it unblocks a rule) and lifting before deepening (lift after each split depth saturates, before nested splits). | Missed ideas from TheSy/Easter Egg; the main defence against case counts growing exponentially. |
| L6 | Benchmarks: per-child cost stays flat as the root grows (20k to 2M rows); memory in bytes against full clones on a case-split workload; copy volume per union. | B4, B5, G3. |
| L7 | Measure the cost of smaller-id-wins when a parent that is not frozen lifts terms after cases created them (G2) before deciding whether parent-visible ids should win. | G2. |

## Engine-wide rules that follow

- Visibility (tombstones, and hidden sets once contexts exist) is checked on **every** path that yields rows to a
  caller: scans with and without constraints, index builds, refinements, probe results, header subsets, point lookups,
  zero-variable atoms. This fixes egglog's stale-row hole (map §8.1 row 1).
- Every merge path stores the merge function's output and reports only real changes (§8.1 rows 3, 4, 16, 17).
- `merge_all` always runs in dependency-stratum order (§8.1 row 11).
- Every egglog defect listed in §8.1 is fixed where it would be ported, with a comment citing the Rust file and line.
