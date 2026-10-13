# Design history

These documents are the design record from Eggnog's construction. They were written milestone by milestone, before
and while the code was built, and they keep the reasoning behind its decisions: the alternatives considered, the
reviews and their findings, and the deviations each milestone recorded. They do not describe the current code. Paths,
type names, line numbers and status lines in them are those of the time they were written, and some of the code they
cite has since been renamed, moved or removed (the storage prototype `src/Eggnog.Storage`, which they call the spike,
among it).

For the code as it is, read [`../architecture.md`](../architecture.md). For what is open, read
[`../roadmap.md`](../roadmap.md). The engine's living conventions and decisions are in [`../engine`](../engine).

| Document | What it records |
|---|---|
| [`egglog-engine-map.md`](egglog-engine-map.md) | A map of egglog's Rust engine (core-relations, union-find, egglog-bridge, the extractor), its defects (§8.1), the plan for reimplementing it in C#, and the build order of the engine's milestones (M0-M13) |
| [`colored-contexts-spec.md`](colored-contexts-spec.md) | The specification of colored contexts (milestones M9.0-M10.4): stores, contexts, the layered union-find, layered tables, catch-up, freeze, layered query execution, per-context rules, lift and split hooks; its invariants (I1-I35) and proof sketches; its implementation notes (§21) and what it still defers (§21.11) |
| [`rule-layer-spec.md`](rule-layer-spec.md) | The specification of the public library (milestones R1a-R3): its decisions (D1-D49), public and internal types, semantics, invariants, test plan, the engine requests it made (E1-E12), its implementation notes (§15) and what it still defers (§15.5) |
| [`colored-egraph-literature.md`](colored-egraph-literature.md) | A review of the literature on e-graphs with assumptions (colored e-graphs and related work) against Eggnog's design, and the requirements it produced (L1-L7 in `docs/engine/decisions.md`) |
| [`cosmos-integration.md`](cosmos-integration.md) | The plan (revision 2, 2026-10-12) for moving the declared-fact reasoning of the Apache Calcite Cosmos DB adapter onto Eggnog: what it found in the adapter, its phases, and the library requests it made of Eggnog (§8.3), of which the open ones are in `docs/roadmap.md` |
| [`colors-m10-reports.md`](colors-m10-reports.md) | The L6 and L7 measurement reports: what a child context costs as the root grows, memory against full clones on case splits, the cost of smaller-id-wins after a late parent mint, and the child footprint |

Labels these documents use, such as decision numbers (D12), invariants (I31), conflict resolutions (X19) or notes (N86),
are defined in them: `D`, `E` and `N` labels in the rule-layer spec; `I` and `X` labels in the colored-contexts spec;
`C` labels (the cross-cutting decisions for colored contexts) in the engine map; `B` and `G` labels (review findings) in
the literature review; and `L` labels in `docs/engine/decisions.md`. The code does not cite them: its comments state
what the cited text established.
