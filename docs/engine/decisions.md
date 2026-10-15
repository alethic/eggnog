# Eggnog.Relations design decisions

These decisions bind every change to the engine. They settled the open questions of the engine's design (recorded in
[`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), "Open questions"), whose cross-cutting decisions
for colored contexts ("Cross-cutting decisions" there) hold as written unless a line below refines them.

| # | Question | Decision |
|---|---|---|
| 1 | Deletion across contexts | A context with live descendants must not retract rows (delete, `clear_table`, subsumption of a visible row); the attempt throws. Tables declared *context-private* (for example scheduler bookkeeping) are exempt, because descendants never read them. A child may delete its own rows (tombstone) and hide visible ancestor rows. |
| 2 | Non-lattice merges | Merge functions declare whether they are lattice merges (associative, commutative, idempotent). A non-lattice merge (`new`, `old`, arbitrary primitives) is allowed only in a context that has no ancestors (the root). Writing such a table from a child throws. |
| 3 | Which classes to mark | Only losers: when a child's union makes a class lose, the child hides and copies the ancestor rows that mention the loser, and rows that mention the winner stay visible. Probes for a canonical value filter hidden rows instead of skipping the parent. |
| 4 | Determinism | Serial execution is deterministic for a given program. Parallel execution must produce the same view modulo renaming of ids and the same set of facts; numbering may differ. Golden tests run serially. |
| 5 | Fresh-id scope | Match egglog: the predicted-value cache lives per `ExecutionState` (per batch), and merges union duplicates. |
| 6 | Match-count parity | Not required. Counts are reported but may differ from egglog's. |
| 7 | Plan parity | Fix egglog's planner defects (rows 5-7 of the defect list, "egglog defects: what the port does instead" in `egglog-engine-map.md`) instead of reproducing them; golden plan tests encode the fixed behaviour. |
| 8 | Executor | The interpreter is the only executor: no plan, stage kernel, right-hand-side program or merge function is compiled at run time (no expression trees, emitted IL or generic structs composed by reflection), so the engine needs no dynamic code. The interpreter keeps the recursion and dynamic reordering. Specialization is closed-world C#: fast paths for a bounded set of shapes, chosen by the interpreter's switches and tested against the general path; whether to build them is decided by the ceiling prototype of [`../plans/compiled-plans.md`](../plans/compiled-plans.md). |
| 9 | Containers in children | Eq-sort containers in child contexts are not supported: interning an eq container from a child throws. Adding them is open (`docs/roadmap.md`). |
| 10 | Rule-based rebuild | Dropped. Rebuild is the native incremental/full path only. |
| 11 | `IterationReport.changed` | Report whether the rule application changed anything; rebuild changes are reported separately. The scheduler decides saturation from both. |
| 12 | Primitives and capability contexts | As egglog: the engine has no capability contexts. External functions receive the raw `ExecutionState`, a trust boundary (`EGraph.RegisterExternalFunction`). The capability contexts (pure, read, write, full) belong to the layer above, which registers a primitive once per context it is valid in and picks the id per call site: a semi-naive rule uses pure on the left-hand side and write on the right, a naive rule read and full, merge functions write, one-shot commands full, `check` read. The bridge guarantees the discipline those contexts rely on: a left-hand-side primitive (`EGraphRuleBuilder.QueryPrim`) runs after the join on every match of every semi-naive variant and a result of nothing drops the match; a right-hand-side call (`CallExternalFunc`) that produces nothing panics; writes are staged and visible only after the run. With colored contexts, the `ExecutionState` a primitive receives is bound to its context, which is what makes `TableAction`/`UnionAction` context-correct. |
| 13 | Packaging | The engine is part of the `Eggnog` assembly and package, under `src/Eggnog/Relations` in the namespace `Eggnog.Relations`; there is no separate engine package. Its types stay internal, so the package shows one API, and the namespace is what tells engine types from the library's. Its tests are part of `Eggnog.Tests` (under `Relations/`) and its benchmarks of `Eggnog.Benchmarks` (under `Relations/`); both keep the engine's namespaces (`Eggnog.Relations.Tests`, `Eggnog.Relations.Benchmarks`) and reach it through `InternalsVisibleTo`. |

## Requirements from the literature review

From the review of the literature on e-graphs with assumptions
([`../history/colored-egraph-literature.md`](../history/colored-egraph-literature.md), including its review
corrections). They bind colored contexts and the lift.

| Requirement | Why |
|---|---|
| The lift must find facts that hold in a case only through an ancestor row mentioning a class's **winner**. With losers-only marking such rows are never copied into the case, so the lift must also look up ancestor rows by each merged class's winner. | With losers-only marking ("Which classes to mark" above), a lift without this misses facts. |
| Each child keeps the member list of every class it merged (the parent-canonical ids in it), and `ScanMerges` is implemented from those lists, reporting a merged class's own canonical id paired with itself when it is a parent id. | Iterating the marked set instead breaks under losers-only marking. |
| A debug invariant checker for contexts, run in tests after every rebuild: overlay keys canonical in the parent once catch-up is done; a child's equalities include its parent's; every hidden row has a reason; child-local ids never appear in an ancestor; cursors valid. | Modelled on Easter Egg's invariants and checks. |
| Inserting a contradiction fact in a case sets the case's stop flag; the rebuild loop checks it; the case answers only `IsContradictory`, is skipped by the lift, and is disposed. | Vacuous cases still copy rows on every merge. |
| Split helpers support demand-driven splitting (split only when it unblocks a rule) and lifting before deepening (lift after each split depth saturates, before nested splits). | Missed ideas from TheSy and Easter Egg; the main defence against case counts growing exponentially. |
| Benchmarks: per-child cost stays flat as the root grows (20k to 2M rows); memory in bytes against full clones on a case-split workload; copy volume per union. | Easter Egg's per-color work never clearly beat clones, the papers count e-nodes only, and a union in a child copies the loser's uses across the ancestor layers. The byte figures are in the [colored-context benchmark reports](../history/colors-m10-reports.md); the timed half is open (`docs/roadmap.md`). |
| Measure the cost of smaller-id-wins when a parent that is not frozen lifts terms after cases created them before deciding whether parent-visible ids should win. | Under a parent that is not frozen, a case-local id can be smaller than the parent's id for the same term, so the parent's id loses in the case and the case hides and copies every parent row that mentions it. Measured in the [colored-context benchmark reports](../history/colors-m10-reports.md): linear in cases times terms per round and independent of the root. The decision stays open. |

## Engine-wide rules that follow

- Visibility (tombstones, and hidden sets in child contexts) is checked on **every** path that yields rows to a
  caller: scans with and without constraints, index builds, refinements, probe results, header subsets, point lookups,
  zero-variable atoms. This fixes egglog's stale-row hole (row 1 of the defect list).
- Every merge path stores the merge function's output and reports only real changes (rows 3, 4, 16 and 17).
- `merge_all` always runs in dependency-stratum order (row 11).
- Every egglog defect in the list ("egglog defects: what the port does instead" in `egglog-engine-map.md`) is fixed
  where it would be ported, with a comment citing the Rust file and line.
