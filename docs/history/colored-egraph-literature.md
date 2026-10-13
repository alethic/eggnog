# E-graphs with assumptions: the literature against Eggnog's colored contexts

> **File:** `docs/history/colored-egraph-literature.md`.
> Written 2026-10-10.
>
> **Inputs.** Three papers, each read in full, together with the authors' code where code exists.
> This report works from those reading notes, not from the PDFs directly. On the Eggnog side it uses
> `docs/history/egglog-engine-map.md` (§0, §4, §6, §8), `docs/engine/decisions.md`, the lift and split sections of
> `docs/history/rule-layer-spec.md` (§0, §3.8, §3.9), and the spike (`src/Eggnog.Storage/README.md`,
> `Context.Maintenance.cs`).
>
> **Names used below.** *arXiv* = Singher and Itzhaky, "Colored E-Graph" (2023). *Easter Egg* = the same authors' FMCAD
> 2024 paper, the conference version of arXiv. *SAS* = Drewery, Jensen and Pichardie, "Contextual Equality Saturation"
> (SAS 2025). Decisions C1–C14 are in engine map §4.2. Decisions D1–D26 are in rule-layer spec §0. Milestones M0–M13
> are in engine map §6.

## Summary

- **The two Singher–Itzhaky papers describe the same design as Eggnog, but at a different layer.** Contexts form a
  tree over a frozen base. Each child keeps a sparse union-find over its parent's canonical ids, and the smaller id wins
  every union. `find` composes down the tree. Base unions re-key the child's union-find. Their code matches C2 almost
  line for line.
  - The main difference is **materialization**. Eggnog copies each touched ancestor row into the child, rewritten, and
    hides the original, so every child view is canonical and ordinary joins work on it.
  - Easter Egg never rewrites base nodes. A colored class stays a set of base classes. Matching therefore has to jump
    between members, and congruence repair re-collects the parents of every member.
- **SAS solves a different problem.** Its assumptions are the conditions of if-then-else nodes already in the term. It
  has one e-graph, a per-class index of the guards above each class, and a rewrite that copies the path up to the guard.
  Conclusions are global by construction, so it needs no lift. Its scope index is easy to express in Eggnog as ordinary
  rules. Its path copying is not worth taking.
- **Ideas to borrow:**
  - pruning child rows that an ancestor row has made redundant (C8);
  - an invariant checker built from Easter Egg's stated invariants (M9);
  - a "flatten this table" escape hatch for assumptions with very many consequences (C10/C14);
  - a benchmark that holds per-child cost flat as the root grows (M10/M13);
  - memory measured in bytes against a clone baseline (E1/E5);
  - not re-opening a split that an ancestor already opened (rule-layer §3.8);
  - inverse member sets in the child union-find (C2).
- **Gaps the comparison exposes:**
  - The rule-layer lift's completeness arguments (L3, L4) assume the spike's "mark both classes" behaviour. Decision 3
    ("only losers") breaks them as written (G1).
  - Union-by-min costs copying when a parent that is not frozen mints ids after its cases (G2).
  - A vacuous case is expensive in a design that materializes rows (G4).
  - Case parallelism is ruled out by D17 (G8).
  - No measurements exist yet for a workload of rules plus case splits (G6).
- **Possibly novel in Eggnog,** relative to these three papers only:
  - contexts over a relational engine with lattice merges and semi-naive evaluation;
  - materialized per-context views;
  - pull-based catch-up;
  - concurrent children of a frozen base;
  - a lift of facts and lattice values, not only equalities.

  An EGRAPHS 2024 paper on *relational* contextual equality saturation was not read and may overlap.

---

## 1. The papers in Eggnog's vocabulary

### 1.1 Colored E-Graph (arXiv:2305.19203, 2023)

**Problem.** Equality saturation cannot case-split. To use `filter p (x::xs) = if p x then … else …`, a prover must
assume `p x = true` in one branch and `p x = false` in another. The usual approach clones the e-graph per assumption. A
clone copies every unrelated term and re-runs every rewrite on it, and nested splits multiply the clones.

**Key observation.** An assumption only adds unions, so a child's congruence coarsens its parent's. This is exactly
Eggnog's semantics: a child sees every ancestor row, and its merges are invisible upward.

**Mapping of terms.**

| Paper | Eggnog |
|---|---|
| black e-graph | root (or frozen base) context |
| color | child context; a sub-color is a grandchild (a case inside a case) |
| colored union | `Union` in a child |
| colored union-find ("multi-level union-find") | the child's union-find overlay keyed by parent-canonical ids (C2) |
| colored e-node / colored e-class | a row in the child's own layer / a child-local id (`IsLocalId`) |
| colored hash-cons | the key index of the child's own layer |
| c-siblings, `equality_classes` | the members of a child class (the lift's `ScanMerges` groups) |
| TheSy `merge_conclusions` | the lift (equalities only) |
| `vacuity_ops` | rules that derive `Contradiction` |

**How it works.**
- **Black layer.** An ordinary egg e-graph with one hash-cons and one union-find, shared by every color.
- **Colors.**
  - *Basic ("monochrome") version.* A color owns only extra unions. Every term, including the right-hand side of a
    colored match, is inserted into black. This is sound because "the mere existence of a term asserts nothing".
  - *Optimized version.* Terms created under a color live in classes that the color owns, with a hash-cons holding only
    the difference from black.
- **No copy-on-touch.** A colored class is a union of black classes, and black e-nodes are never rewritten into the
  color.
  - Matching runs one VM pass over the shared graph for all colors. A `ColorJump` instruction forks to every colored
    sibling of a class, and each substitution carries its color.
  - Colored congruence collects the parents of every black member of a dirty colored class, canonicalizes them under the
    color, and deduplicates.
- **Base changes.** These are pushed eagerly. A black union calls `inner_base_union` on every child color, recursively.
  The call removes the stale representative from the child's union-find, merges the inverse sets, and marks the child
  dirty.
- **Case splits and their join.** These live in the TheSy prover, not in the paper. After saturation, every class is
  grouped by the vector `[find_c1(id), …, find_ck(id)]`, and each group is unioned in the parent color.
- **Evaluated code (May 2023).**
  - Colors were flat, and each one held a full copy of the black union-find, so memory was O(#ids) per color.
  - A sub-color copied its parent color's whole layer.
  - The sparse overlay with min-wins and the tree hierarchy came after the paper (July–September 2023) and are
    unevaluated.
- **Results.**
  - About 10× lower e-node overhead per assumption than separate clones. The metric counts e-nodes only; the per-color
    union-find copies are not counted.
  - Run time is similar or worse ("lower memory sizes at the expense of run-time").
  - The Table I totals are separate 211,014 s with 56 OOM and 60 timeouts, against optimized 31,297 s with 5 OOM and 121
    timeouts. The run-time column behaves as if OOM runs were charged 3600 s and timeouts were excluded, which is the
    opposite of the stated method, so treat these totals with caution.

### 1.2 Easter Egg (FMCAD 2024)

This is the conference version of the same system, using the same code (`eytans/egg`, branch `features/color_splits`).
It adds the following.

- **A formal model.**
  - Colors form a tree.
  - `≡c ⊆ E_p(c) × E_p(c)`: a color's relation is over its parent's representatives.
  - `find_c(e) = find_c(find_p(c)(e))`.
  - `≡c ⊇ ≡p(c)`.

  This is C2's `find_C(x) = local(find_P(x))`. The code's sparse union-find also keeps the smaller id as leader, which
  is C2's union-by-min.
- **Clone semantics.** Every operation must give the same result as running it on a per-color clone. This is the
  *context = copy* property that M9 tests.
- **Algorithms 1–4.**
  - REPAIR collects the parents of all base members of a colored class and deduplicates them under `find_c`.
  - Rebuild runs the finer relations first: root, then ancestors, then descendants.
  - **Algorithm 4** starts matching at the root and adds colors lazily. Each match carries the color closest to the root
    that suffices, so a match found in an ancestor is never repeated in a descendant. Eggnog gets the same effect from
    seeded semi-naive watermarks (C7).
- **Pruning.** A colored node already present in an ancestor is dropped. The authors found this ineffective, because
  their rules re-add the pruned nodes.
- **A stated limitation.** If an assumption causes very many unions, a clone may be better, and a clone can be used for
  that one assumption.
- **Results.**
  - About 10× lower e-node overhead per assumption.
  - 99 failures (6 OOM + 93 timeouts) against 114 for clones (53 OOM + 61 timeouts). Failures move from OOM to
    timeouts.
  - The meaning of the time column is unstated, and it contradicts the text for the monochrome configuration.

### 1.3 Contextual Equality Saturation (SAS 2025)

**Setting.** Translation validation over PEGs. `Φ(c, t, e)` is a gated if-then-else. A contextual rule `pc ⊢ p1 ⇝ p2`
rewrites `p1` wherever it sits inside the then-branch of a `Φ` whose condition matches `pc`.

**In Eggnog's terms, everything happens in one context (the root).**
- There are no child contexts and no overlay union-find. A "context" is just the class id of a `Φ` condition.
- **Scope index.** Each class carries a set of `(condition class, incoming edge)` pairs. A new `Φ` node adds a pair to
  its then-child, and the pair propagates down every child edge, stopping at loop operators. Maintenance is eager, inside
  every add and every merge.
  - In Eggnog this is a derived relation `Under(cond, class)` maintained by two Datalog rules: one fires on `Φ`, one
    propagates through constructors.
  - Eggnog's rebuild would keep `cond` and `class` canonical. The paper leaves that unspecified.
- **Rewrite.** Contextual matching is ordinary e-matching of `p1`, followed by a rooted match of `pc` at each annotated
  condition.
  - The rewrite never unions `p1` with `p2`. It copies the path from the occurrence up to the guarding `Φ`, with `p2`
    substituted, and unions the two `Φ` terms: `Φ(c, T[p1], E) ≡ Φ(c, T[p2], E)`.
  - Every conclusion is therefore a sound root fact, and no lift is needed.
- **Limits.**
  - There are no case splits.
  - Else-branches are reached only through a negation rewrite.
  - Conjunctions of contexts cannot be expressed.
  - Path enumeration is not memoized, so diamonds in the DAG can make it exponential.
  - Each copy rewrites one occurrence.
- **Results.** Synthetic, with one rule and no hardware or memory numbers reported.
  - With 500 guards sharing one then-branch: annotations 3.49 s, operator-swap rules 8.89 s, naive traversal 17.95 s,
    and an unsound baseline 1.85 s.
  - Naive traversal wins when there is a single guard (0.02 s against 1.34 s).

---

## 2. Side-by-side comparison

| Aspect | arXiv (2023) | Easter Egg (2024) | SAS (2025) | Eggnog design |
|---|---|---|---|---|
| **Representation of layers** | One shared egg e-graph. A color stores extra unions and, when optimized, its own colored e-nodes with a diff hash-cons. A colored class is a set of black classes; black nodes are never rewritten | Same, formalized: `EC: E → C`, `P_c`, `H_c` as diffs from the parent. Colored nodes subsumed by an ancestor are pruned | No layers. One e-graph plus a per-class annotation set `A(C)` | A stack of per-layer tables (egglog layout, layer-local row ids) with hidden sets per (context, table, ancestor layer). Ancestor rows that mention a child loser are copied in rewritten; everything else is read through (C3, C14) |
| **Union-find per layer** | Paper: a multi-level union-find. Evaluated code: a full copy per color. Later code: a sparse overlay, min wins | `≡c` over parent representatives, with `find_c = find_c ∘ find_p`. Code: a sparse overlay with min wins, path compression and inverse sets `equality_classes` | One global union-find. Contextual equalities never enter it | Root `DisplacedTable`. Each child has a dictionary overlay keyed by parent-canonical ids plus its own displaced log, with min wins; `find_C = local ∘ find_P` (C2) |
| **Congruence and rebuild per layer** | Black first, then each color. Repair collects the parents of every black member of a dirty colored class. The code builds a transient hash-cons of the whole black memo for each color on each round | Algorithms 2/3, the same scheme. Finer relations are rebuilt first | Global congruence only. Contextual congruence is materialized as path copies (RebuildPath), one per annotated path | egglog's incremental rebuild in each context. Own rows are found through the child's `rebuild_index`, and ancestor rows that mention child losers through the ancestor's shared `rebuild_index`. Collisions are found by a layered key probe. The parent is clean before a child catches up (C4, C5) |
| **Matching and queries** | One VM pass for all colors. `ColorJump` forks to colored siblings, and each substitution carries a color. Duplicate matches remain | Algorithm 1 runs per color. Algorithm 4 starts at the root, adds colors lazily, and reports the color closest to the root | Ordinary e-matching of `p1`, then a rooted match of `pc` at each annotation's condition | Free Join over each context's own canonical view, with per-layer subsets, indexes and prober pieces. Semi-naive per (rule, context), with watermarks seeded from ancestors (C7) |
| **Nesting** | The paper does not specify it. The evaluated code is flat, and a sub-color copies its parent color's layer. The later code is a tree. No DAGs | A tree: a child's union-find is over its parent's representatives, and its hash-cons stores diffs. No DAGs (`todo!`) | None. Independent `(cond, edge)` pairs; no conjunction of contexts | A tree: root → schema → query → case → sub-case, as a stack of layers. No DAGs or conjunction either |
| **Base changes after layers exist** | Supported and pushed eagerly: a black union calls `inner_base_union` on every color, recursively. Monotone only | Same in the code. New black terms are visible to every color | A single layer. Annotations are propagated eagerly on every add and merge | Pulled lazily. A child catches up at its next `Rebuild` through cursors into the ancestors' row buffers and displaced logs, and re-keys its overlay; reads throw until then (C2, C8). An ancestor with live descendants may not retract (C10) |
| **Combining conclusions across cases** | Not in the paper. TheSy groups every class by its vector of per-case finds and unions each group in the parent. Equalities only, O(classes × cases). Vacuity is used only for statistics | One sentence in the paper (≅b ∩ ≅r is added to the base). The same TheSy code | Not needed: each conclusion is a global equality of guard terms. Branches combine only through ordinary `Φ` rules | The rule-layer lift (D8, §3.9). A partition meet seeded from the case with the fewest groups. Facts, and lattice values through a case-join, for tables marked `Lift`. Terms are lifted by `GetOrAdd` in rounds. Vacuous cases are dropped, and case-local ids are filtered with `IsLocalId`. Arguments L1–L4 |
| **Deletion and disposal** | No deletion of facts. `delete_color` is unfinished ("do not use yet"), and colors are never deleted | Same | No retraction. Annotations are monotone | A child tombstones its own rows and hides ancestor rows. An ancestor with live descendants may not retract (C10, decision 1). `Dispose` releases the compaction gates. Cases are disposed at the end of each `Run` (D7) |
| **Concurrency** | None. `Cell`-based union-finds, shared `colored_parents`, colors processed in turn | None. A `concurrent_cufind` cfg exists, but no Cargo feature declares it | None. Maintenance runs eagerly inside every add and merge | Children of a frozen base run concurrently, one thread each (C9). The cases of a split run sequentially (D17) |
| **Measured costs** | About 10× lower e-node overhead than clones (e-nodes only). Run time similar or worse. Table I totals are internally inconsistent | About 10× lower e-node overhead. 99 failures against 114, moving from OOM to timeouts. Time semantics unstated | Synthetic, one rule. Annotations are best on shared then-branches (3.49 s against 8.89 s and 17.95 s); traversal is best with a single guard | Spike only: a few ms for a child with 100 merges on a 200k-row frozen root, and about 100 ms for 1000 single-merge children in parallel. Clone cost estimated at 20–40 MB and 10–30 ms each, not measured (E1) |

---

## 3. What to borrow, and what not to

### 3.1 Borrow

**B1. Prune child rows that an ancestor row has made redundant (Easter Egg pruning).** *Change C8 step 2, and state the
result as an invariant next to C4.*
- Today, when an ancestor row appended since the cursor has a key the child already holds, the child merges it into
  its own row and hides the ancestor row.
- Instead, compute `merge(ancestor value, child value)`. If the result equals the ancestor's value, tombstone the
  child's row and leave the ancestor row visible.
- **The lift is the main source of such rows.** Every fact it writes into the parent is, by construction, already held
  as an own row by every live case. Without pruning, each case keeps a duplicate for the rest of the `Run`.
- **This works better in Eggnog than in Easter Egg.** Easter Egg found pruning useless because rules re-add the pruned
  nodes. In Eggnog, the C4 insert path already refuses to store a row whose merge with the visible ancestor row changes
  nothing, so a pruned row stays pruned.

**B2. An invariant checker built from Easter Egg's invariants (M9).** Easter Egg states the coarsening invariant, and
its code asserts `assert_black_ids`, `no_two_colored_classes_in_ec` and `memo_classes_agree`. *Add a debug-only
`Context.CheckInvariants()`, run by the differential harness after every `Rebuild`, that checks:*
- every overlay key and target is canonical in the parent once catch-up is done;
- `find_C(find_P(x)) = find_C(x)` on sampled ids (coarsening);
- every hidden ancestor row either mentions a child-displaced id, has a key the child holds, was retracted in the child,
  or belongs to a cleared table (C3);
- every own row is canonical in the child, and no own row is equal, after merging, to the visible ancestor row with the
  same key (B1);
- every child-local id is recorded by `IsLocalId` and appears in no ancestor layer;
- every cursor is at most its ancestor's `TableVersion`, and no hidden set refers to a compacted generation (C12).

This extends the "debug assertion of INV after every rebuild loop" that M5 already plans.

**B3. A flatten escape hatch for assumptions with many consequences (Easter Egg's "use a clone for that one
assumption").** *Add to C14, with a flag next to C10's "cleared".*
- When a child's hidden set over an ancestor layer of a table passes a threshold (for example half of that layer's live
  rows), filtering costs more than owning the rows.
- **Flatten:** copy the still-visible ancestor rows into the child's layer and hide that ancestor layer for that table
  wholesale.
- C10's per-(context, table) `cleared` flag nearly does this, but it would also hide later ancestor appends. A
  `flattened` flag must instead make catch-up copy those appends in.
- M9 test: flattened and unflattened children must give equal views.

**B4. A benchmark gate on per-child cost as the root grows (M10, M13).**
- **The failure mode.** Easter Egg's code canonicalizes the entire root hash-cons for every color on every rebuild
  round, and recomputes `colored_equivalences` in O(classes × colors). Its run time never clearly beat clones.
- **Eggnog's design avoids this.** C5 forbids full scans of ancestor layers, and C6 shares the ancestor indexes. Nothing
  yet enforces it.
- *Add a benchmark:* the same child workload (100 merges plus one rule run) over roots of 20k, 200k and 2M rows. Child
  time and bytes must stay flat. Any per-child step that scans an ancestor will show up as growth, whether it is an
  index-selection fallback, the full rebuild path, or a lift that reads the parent.

**B5. Measure memory the way the papers should have (E1, E5, M13).**
- Both papers count e-nodes only. The arXiv evaluation thereby leaves out a union-find copy per color, and neither
  Table I says clearly what its time column contains.
- *Extend E1:*
  - Compare against a clone baseline on a **case-split** workload, not only on merge microbenchmarks.
  - Report bytes for every per-child structure: layer tables, hidden sets, overlay, cursors, watermarks and indexes.
  - Report time and failures separately, under a stated timeout policy.
- The Cosmos split workload is the first candidate. A TheSy-style inductive suite would allow direct comparison with
  Easter Egg; whether it is worth porting is an open question.

**B6. Do not re-open a split an ancestor already opened (TheSy).** *Change rule-layer §3.8.*
- TheSy propagates the splits already known in a color to its children, so they are not repeated.
- `SplitRunner` checks only the current scope's records ("if S has a live record with key: continue").
- A case's split rules start from watermark 0, because D4 excludes split rules from inheritance. They see the parent's
  match with the same key.
- Domain splits are protected, because a case narrows the domain to one element and the `|d| > 1` guard fails. A general
  `DefineSplit` rule is not protected, as far as the spec shows.
- *Fix:* while the scope is a case, walk up the tree and skip any key that an ancestor's live record already covers.

**B7. Keep inverse member sets in the child union-find (Easter Egg `equality_classes`).** *Change C2.*
- **The structure.** Each non-trivial child class keeps the list of its parent-level members, including its own
  canonical id. The list is updated on a child union and during re-keying, exactly as `inner_base_union` updates
  `equality_classes`.
- **What it gives.** `Members(c)` becomes O(group) for the lift and for any future explanation feature, and it directly
  fixes G1(a) below.
- **Cost.** One small list per merged group per child.

**B8. Guard-scoped reasoning without contexts (SAS). Guidance now, and an optional rule-layer feature later.**
- **Guidance.** When assumptions come from the term structure (the guards of if-then-else nodes in a query or a
  program), one child per guard is the wrong tool. SAS's second benchmark, 500 guards sharing one then-branch, is
  exactly where per-guard work dominates.
  - In Eggnog, the scope index is two ordinary rules over a relation `Under(cond, class)`.
  - Rebuild keeps that relation canonical for free.
- **Later rule-layer feature: a "guarded lift" (an addition to §3.9).**
  - Create one child per *distinct* condition `c`, assuming `c = true`, rather than one per `Φ` node.
  - Lift equalities only at `Φ` boundaries: if the child proves `t = t'`, where `t` is the then-child of a parent node
    `Φ(c, t, e)`, add `Φ(c, t, e) = Φ(c, t', e)` to the parent, with a term lift for `t'`.
  - The child's own congruence closure replaces SAS's path copies.
- **Caveat.** This is sound only when the guard is pointwise. With values carried across loop iterations (PEG θ nodes),
  "c holds at every iteration" does not license a conclusion about one iteration, which is why SAS stops propagation at
  loop operators. Not for v1.

### 3.2 Do not borrow

| Idea | Source | Reason |
|---|---|---|
| `ColorJump` matching, one VM pass for all colors, color-carrying substitutions | arXiv, Easter Egg | It exists only because their colored classes are not materialized. Eggnog's copy-on-touch makes each child view canonical, so plain Free Join works. Jumping produces duplicate matches and ∏\|[xᵢ]\| spellings, disables egg's ground-term optimization, and does not fit semi-naive evaluation or one thread per child |
| Colored classes as unmaterialized sets of base classes | arXiv, Easter Egg | It swaps a one-time copy of the loser's rows for repeated cost in matching and in every repair. The papers report run time equal to clones or worse. C3 is the core of Eggnog's plan |
| Pushing base unions eagerly into every child (`inner_base_union` called from the parent) | Easter Egg code | The parent must hold links to its children and write their state from its own thread, which conflicts with concurrent children. Pull-based catch-up (C8) is lazy, lets a child that is never read again skip the work, and is a no-op under a frozen parent |
| A transient hash-cons of the whole base per color per rebuild; recomputing `colored_equivalences` | Easter Egg code | O(colors × base) per rebuild. Eggnog shares frozen ancestor indexes (C6) and probes them per layer (C4). `colored_equivalences` serves only the one-pass matcher |
| A color writing into the shared union-find (black-merging two colored classes) | Easter Egg code | A child never writes the root array (C2), which a frozen, shared base requires |
| Sub-colors built by copying the parent color's layer | arXiv evaluated code | Eggnog's layer stack already shares the parent's layer |
| TheSy's lift: a full scan of every class, equalities only | TheSy | Eggnog's meet starts from the merged groups of the case with the fewest groups. It also lifts facts and lattice values, and drops vacuous cases instead of intersecting them in |
| Path copying (RebuildPath), eager annotation maintenance inside add and merge, `Φ` built into the engine | SAS | Path enumeration is unmemoized and exponential on diamonds, and it duplicates upper structure. Eager maintenance conflicts with egglog's staged `merge_all`. Annotations are better written as rules |
| Inserting child-derived terms into the base ("existence asserts nothing", used eagerly) | arXiv basic version | The base is frozen and shared, and terms in the parent would trigger the parent's rules. Eggnog uses the principle only in the term lift, where it belongs |
| Vacuity patterns used only for statistics | Easter Egg code | Eggnog already treats `Contradiction` as a fact and drops vacuous cases from the lift (D11) |

---

## 4. Risks and gaps the papers reveal

**G1. The lift's completeness arguments assume the spike's marking, not decision 3.**
- **Background.**
  - The rule-layer lift (§3.9) builds groups from `ScanMerges`, which iterates the context's `marked` set.
  - L4 relies on "both are marked by `UnionCore`". L3 relies on "an unshadowed row of P mentions only unmarked
    classes".
  - The spike does mark both classes (`MarkMerged(ra) | MarkMerged(rb)`, `Context.Maintenance.cs:211`).
  - `decisions.md` #3 marks only losers.
- **Consequence.** Ported onto the engine as written, the lift loses completeness, though not soundness, in two places.
  - **(a) Equalities.** If groups are built from marked ids, a group's winner is missing from its own group. Then
    `y ~ z`, holding in every case, never lands in one block.
    - *Fix:* build groups from the overlay (each loser maps to its `find`), and include the canonical id whenever it is
      parent-visible. B7 provides this directly.
  - **(b) Facts.** A fact `R(y)` can hold in the seed case r only through an ancestor row `R(z)`, where z is the winner
    of y's group. Under losers-only marking that row is never copied into r, so r's own rows do not yield the candidate.
    - *Example:* P holds `R(z)`. Case 1 assumes `y = z`, giving one group. Case 2 merges two unrelated pairs, giving two
      groups, and derives `R(y)` by a rule. r is case 1 (the fewest groups), and `R(y)` is never proposed.
    - *Fix:* also probe the ancestor rows that mention each group winner, through the ancestor's column index or
      `rebuild_index`, and expand the members as for own rows. The cost is O(uses of winners), paid only at lift time.
- **Status.** Neither paper lifts facts, so neither meets this. The comparison with TheSy's `merge_conclusions` is what
  brought it up. Fix §3.9 before porting the lift, and add the example as a test.

**G2. Union-by-min under a parent that is not frozen.**
- **Under a frozen parent there is no problem.** Every parent id predates every child id, because the counter is
  store-wide, so min always keeps the parent's id, and read-through is undisturbed.
- **Under a non-frozen parent there is.** Take a query scope running splits. Its term lift mints ids in P after the cases
  have minted theirs.
  - A case-local id x can then be *smaller* than the parent's id y for the same term.
  - In the case, y loses. The case hides and copies every parent row that mentions y, now and at every later catch-up.
- **Easter Egg pays nothing in this situation,** because it never materializes, and it prunes the duplicate.
- **Options.**
  - Measure it on M10 lift workloads.
  - Rank winners by (parent-visible first, then min). That needs `UnionId` to use the same order, and engine map §3.9
    item 3 ties `UnionId` to min, so the merge function would become aware of its context.

**G3. Assumptions with many consequences, and hub classes.**
- **Copy cost.** A union in a child copies O(uses of the loser) across all ancestor layers. Min usually keeps the older
  class, which is often the more heavily used one, but `x = y` between two heavily used classes can copy a large part of
  the base.
- **What the papers say.** Easter Egg says outright that clones may win then. Eggnog has no fallback.
- **Proposed fallback:** B3. Also extend open question §8.3 #3 to measure copy volume per union, not only probe cost.

**G4. Vacuous cases are expensive in a design that materializes rows.**
- **Easter Egg.** Intersecting with a vacuous color is harmless there: at worst the color equates everything, which
  costs union-find entries.
- **Eggnog.** A vacuous case keeps merging until `StopOnContradiction` is checked between iterations (D11), and every
  merge in a child copies the loser's rows.
- **Change C5 and C9:**
  - inserting `Contradiction` in a case sets that child's stop flag;
  - the rebuild loop checks the flag between merge rounds;
  - a stopped case answers `IsContradictory` and throws on every other read;
  - the lift treats it as vacuous;
  - it is disposed at once.

**G5. Case counts grow exponentially with depth.**
- **Evidence.** Easter Egg's optimized prover had 2676 assumptions at depth 3 on leon-heap, and all 12 of its timeouts
  there were at depth 4.
- **Eggnog's exposure.** The defaults are `SplitDepthLimit = 4`, cases that live for the whole `Run` (D7), and cases
  that run sequentially (D17). Every case keeps its layer tables, hidden sets, overlay and watermarks. This sharpens
  §8.2 "Memory with many children".
- **Suggestion.** Add a per-`Run` memory budget. Under that budget, dispose of settled split records first: those whose
  lift signature has not changed. Resetting their split progress lets them re-split if needed.

**G6. Run time is the unmeasured risk.**
- **What the papers found.** Both papers won on memory and roughly tied or lost on time. Failures shifted from OOM to
  timeouts.
- **What Eggnog has.** Its only evidence is merge microbenchmarks on the spike. No workload of rules plus splits has
  been timed against clones.
- **Action.** Run B5's benchmark before M10 is declared done.

**G7. Correctness arguments.**
- **The papers.** None of them proves anything. Easter Egg gives a model and implied invariants. SAS has definitions
  only.
- **Eggnog.** It has more: the C7 argument, L1–L4, and an oracle that replays from scratch. These are informal, though,
  and the storage layer has no list of invariants.
- **Actions.**
  - Add a short "model" paragraph at the head of engine map §4. State Easter Egg's `find` composition and coarsening
    over parent representatives, and define the view as the congruence closure of the replayed assertions.
  - Check the invariants as in B2.
- **Biggest unproven step.** Seeded watermarks (§8.2) remain that step. Easter Egg's lazy color addition rests on the
  same intuition ("found in an ancestor ⇒ holds in descendants") and is not proven either.

**G8. Concurrency does not reach case splits.**
- **Where it stands.** No paper addresses concurrency at all, so Eggnog's C9 is unvalidated by the literature. D17 also
  keeps the cases of a split sequential, because the lift writes into the parent. On the papers' workload, case work
  dominates, and C9 would not help.
- **Option for the rule layer.**
  1. Seal the parent for the in-case phase.
  2. Run the cases in parallel.
  3. After a barrier, thaw the parent and lift.
- **What thawing requires.** C9's freeze is one-way. A thaw would have to restore staging and path compression, and
  reset the parent's index locks on its next `merge_all`. That is safe only because no child runs after the barrier.

**G9. Siblings duplicate terms.**
- **The shared problem.** In both designs, a term derived in k sibling cases is minted k times. Eggnog's term lift
  reconciles only terms present in every case, and only by extra unions in each case.
- **A speculative idea, from neither paper.** Store-wide ids would allow a table on the frozen parent that gives one id
  per (constructor, key) when every key id is parent-visible. Siblings would then share that id while keeping the row
  local. This needs careful design around `IsLocalId` and lift.

---

## 5. What appears novel in Eggnog

These claims are made relative to the three papers read here, and only cautiously.
- **Not checked.** Hou, Laddad and Hellerstein, "Towards Relational Contextual Equality Saturation" (EGRAPHS 2024,
  arXiv:2507.11897), combines relational e-graphs with contexts by its title and may overlap with points 1 and 3. Read it
  next.
- **Not compared.** Backtracking congruence closure in SMT solvers (DPLL(T)) and incremental or versioned Datalog
  systems may have analogues of catch-up and hidden sets. Neither was compared.

The points:
1. **Contexts over a relational engine.** Eggnog adds contexts to an egglog-grade engine: tables with functional
   dependencies, lattice merges, semi-naive Datalog and Free Join. All three papers work on term e-graphs. A consequence:
   lattice values (analyses) are per context by construction, whereas Easter Egg's analysis data is not per color.
2. **Materialized views per context.** Copy-on-touch with hidden sets gives every child a canonical view that ordinary
   joins and indexes can serve. The papers keep colored classes as unions of base classes and pay for it in matching.
3. **Semi-naive evaluation across contexts,** with watermarks seeded from ancestors (C7). The papers re-match
   egg-style. Easter Egg's lazy color addition is the closest analogue.
4. **Pull-based catch-up** through cursors into append-only row buffers and the displaced log (C8). The papers' code
   pushes base unions into children eagerly.
5. **Concurrent children of a frozen base** (C9). No paper discusses concurrency.
6. **A lift of facts and lattice values** with case-joins, vacuous-case dropping and filtering of case-local ids, with
   stated soundness and completeness arguments, subject to G1. TheSy lifts equalities only, and the papers barely
   describe it.
7. **Retraction and disposal.** Rules for retraction in children (C10), and disposal of contexts. The papers have
   neither.
8. **Scoped atoms** (D1). Rules can read an ancestor's view in that ancestor's own ids. Easter Egg's "closest-to-root
   color" per match is related, but it is not exposed to rules.

---

## Sources

- E. Singher, S. Itzhaky. *Colored E-Graph: Equality Reasoning with Conditions.* arXiv:2305.19203v1, 2023.
  https://arxiv.org/abs/2305.19203
- E. Singher, S. Itzhaky. *Easter Egg: Equality Reasoning Based on E-Graphs with Multiple Assumptions.* FMCAD 2024,
  pp. 70–83, DOI 10.34727/2024/isbn.978-3-85448-065-5_13. Record: https://repositum.tuwien.at/handle/20.500.12708/200780
  (read from a Web Archive copy of the PDF).
- Code: https://github.com/eytans/egg/tree/features/color_splits (head `16dbd23`; paper-era `5757623`);
  https://github.com/eytans/TheSy/tree/features/color_splits (`3be040c`, `src/thesy/case_split.rs`);
  https://github.com/eytans/easter-egg.
- A. Drewery, T. P. Jensen, D. Pichardie. *Contextual Equality Saturation.* SAS 2025. HAL hal-05226543 (submission
  draft): https://inria.hal.science/hal-05226543/file/draft.pdf. No public code found.
- Eggnog: `docs/history/egglog-engine-map.md` (§0, §4, §6, §8), `docs/engine/decisions.md`,
  `docs/history/rule-layer-spec.md` (§0, §3.8, §3.9), `src/Eggnog.Storage/README.md`,
  `src/Eggnog.Storage/Context.Maintenance.cs`.
- Not read: Y. Hou, S. Laddad, J. M. Hellerstein. *Towards Relational Contextual Equality Saturation.* EGRAPHS 2024,
  arXiv:2507.11897.

---

## Review corrections

A second review checked this report against the papers' full texts and Eggnog's design documents. Where a correction below conflicts with the text above, the correction wins.

**Verdict.** The report gets the papers' mechanics mostly right, and its central Eggnog finding holds: G1(b), lifting facts, really does break under losers-only marking. But it needs revision before anyone relies on it. Problems: (1) Several paper claims are misattributed: code details are presented as things the papers say, and arXiv results are credited to Easter Egg. (2) One claim is very likely false: that the sparse union-find and the tree "were never evaluated". (3) Easter Egg's run-time claims are understated. (4) Two recommendations clash with Eggnog's own design: B1 conflicts with C4's rule that a tombstone means "absent", and B3 does not reduce the copying that G3 is about. One "do not borrow" item is argued on the wrong grounds (eager push and concurrency). (5) The novelty section ignores egglog's own push/pop, which already carries `last_run_at` into the clone, and it ignores classic work on joining congruence closures. I checked against the local full texts (colored_raw.txt, easter/easter_ur.txt, draft.txt in the scratchpad), the Hou et al. arXiv page, engine map §3.8, §3.9 and §4, decisions.md, and rule-layer spec §0, §2.1, §3.8 and §3.9.

1. **Claim:** Summary: "Easter Egg already describes C2's union-find almost exactly": a sparse per-child union-find, the smaller id winning every union, find composed down the tree, and re-keying when the parent merges. The fuller file adds: "Their code matches C2 almost line for line."
   - **Problem:** The paper itself gives only the layered model. It describes "a smaller union-find for merged representative e-classes of the parent layer" and find_c = find_c ∘ find_p. Min-wins, lazy insertion and stale-representative removal come only from the eytans/egg code. The code also differs from C2 in ways that matter. Re-keying is pushed eagerly by the parent (inner_base_union), whereas C2 re-keys on the child's catch-up. The code keeps inverse sets (equality_classes), which C2 does not. And a colored union can black-merge colored classes in the shared union-find, which C2 forbids.
   - **Correction:** Say that Easter Egg's model matches C2's find composition and its coarsening over parent representatives. Say that its code (not the paper) also uses min-wins and a sparse overlay. Then list the differences: eager push instead of pull, inverse sets, and writes into the shared union-find.
2. **Claim:** Fuller file, Summary: "Contexts form a tree over a frozen base" (said of the two Singher–Itzhaky papers).
   - **Problem:** In both papers the black base is not frozen. Black unions and inserts keep happening after colors exist, they are pushed into every color, and TheSy's merge_conclusions writes into black. A frozen base is Eggnog's C9 assumption, not theirs.
   - **Correction:** Say "a tree over a shared, still-changing base".
3. **Claim:** arXiv section: "The sparse overlay and the tree came after the paper and were never evaluated."
   - **Problem:** This is true of the arXiv paper only. Easter Egg (FMCAD 2024) describes the layered per-color union-find and the color tree, drops arXiv's caveat that "layered union-finds are not implemented directly… full copy of the original union-find for each new color", links the same branch (head Oct 2023), and reports a fresh evaluation whose Table I numbers are entirely different. Nothing supports "never evaluated". The newer code was most likely what FMCAD 2024 measured, though the paper does not name a commit.
   - **Correction:** Say that the arXiv evaluation copied the union-find per color, and that the tree/sparse design appears in Easter Egg, whose evaluation probably used it. The exact commit evaluated is unknown.
4. **Claim:** Comparison table, Matching row: "One pass with ColorJump (arXiv); root first, colors added lazily (Easter Egg)". Fuller file: Easter Egg "adds" Algorithm 4.
   - **Problem:** The arXiv paper already describes root-first matching with on-demand colored jumps: "starting with a standard top-down traversal for the root layer, but with the additional on demand colored e-matching… forked, 'jumping over' to that sibling". Easter Egg formalizes this as Alg. 4 and adds minimal-color selection. The two rows describe the same mechanism; they are not two different ones.
   - **Correction:** Say that both papers match root first with lazy colored forks. Easter Egg contributes pseudocode and the minimality rule (topmost or minimal colors).
5. **Claim:** G6 and the table: "Both papers won on memory and roughly tied or lost on time." "Run time similar or worse." Fuller-file B4: "Its run time never clearly beat clones."
   - **Problem:** Easter Egg claims more than that. Its abstract says "slightly improved performance", and §V-C says "The optimized e-graphs demonstrate enhancements over separate e-graphs in both run-time and success rate". The same paper also says "at the expense of run-time", so it contradicts itself. Only arXiv says plainly that the gain costs run-time.
   - **Correction:** Report Easter Egg's claimed run-time improvement and fewer failures (99 vs 114), and note that its own text is inconsistent on this. Keep the statement that time comparisons in both papers are unreliable.
6. **Claim:** Fuller file §1.2 and B5: Easter Egg's "time column meaning is unstated"; "neither Table I says clearly what its time column contains."
   - **Problem:** Both papers state the method: completed runs plus 1 h per timeout, with out-of-memory runs excluded. Easter Egg says "Run-time is measured as the total run-time for completed test cases, and 1 hour for cases that timed out. We do not include runs that did not finish due to out-of-memory". The problem is that both tables contradict that method. In Easter Egg, Separate leon-amortize-queue is 187356.4 s with 52 out-of-memory runs (about 52 × 3600), and Monochrome is 14.8 s despite 57 timeouts. This is the same inversion the report already identifies in arXiv.
   - **Correction:** Say that both papers state the time method and both tables appear to break it in the same way.
7. **Claim:** G5: "Easter Egg's timeouts came at depth 4". Fuller file: "Easter Egg's optimized prover had 2676 assumptions at depth 3 on leon-heap, and all 12 of its timeouts there were at depth 4."
   - **Problem:** These figures are from the arXiv paper, not Easter Egg. Easter Egg reports 13 extra leon-heap timeouts and never mentions split depth. The arXiv statement also covers only leon-heap's 12 timeouts, not "the timeouts" in general. And arXiv blames them on implementation differences that made the colored prover find more assumptions (2676 vs 1426 for clones). That is not evidence that case counts grow exponentially.
   - **Correction:** Credit these figures to arXiv, limit them to leon-heap, and give the paper's own explanation. G5 still stands as a general risk, but this is weak evidence for it.
8. **Claim:** G4 and the fuller file: "Intersecting with a vacuous color is harmless there [Easter Egg]: at worst the color equates everything, which costs union-find entries." Hence "Vacuous cases are expensive here", as a contrast.
   - **Problem:** Neither paper nor the extraction says this. The extraction notes only that intersecting with a vacuous color is sound. In the code, vacuity_ops is used only for statistics, so vacuous colors keep saturating until resource limits. Every colored union there also triggers colored congruence repair over all black members, and the per-color pass canonicalizes the whole memo every round. Vacuous colors are therefore costly in Easter Egg too.
   - **Correction:** Drop the contrast. G4's proposal (a stop flag on Contradiction) stands on its own merits.
9. **Claim:** SAS: "Path copying is not memoized and is exponential on shared sub-terms."
   - **Problem:** The paper does not say this. It says a contextual rewrite is "linear in the length of this path", that it visits exactly the e-nodes it needs, and that with several annotations every path is rewritten. Exponential growth is the reviewer's own inference. It is plausible, but the trigger is many distinct guard-to-occurrence paths (stacked diamonds), not shared sub-terms as such.
   - **Correction:** Label it as an inference: the number of copies equals the number of distinct paths from the guard to the occurrence, which can be exponential in the number of diamonds, and the paper does not bound it.
10. **Claim:** SAS results: "500 guards… 3.49 s against 8.89 s and 17.95 s"; "0.02 s against 1.34 s".
   - **Problem:** The paper's text gives no such numbers. They were read off the plots' vector data, accurate to about ±0.02 s. The paper's only quantitative statement is "around 30% better" on benchmark 3.
   - **Correction:** Note that the figures were digitized from the plots.
11. **Claim:** SAS guard index: "In Eggnog this would be an ordinary relation Under(cond, class) maintained by two rules."
   - **Problem:** SAS annotations are (condition, incoming edge) pairs. RebuildPath needs the edge, and Under(cond, class) throws it away. In an egglog-style engine each constructor is its own table and there is no generic child relation, so propagation needs one rule per constructor and argument position, not two. Propagation also skips the else child of the generating Φ.
   - **Correction:** Say that Under(cond, class) is a lossy version of SAS's index, adequate for B8's child-per-condition design, and that it costs one propagation rule per constructor argument (or a maintained generic Child relation).
12. **Claim:** B1: when merge(ancestor, child) == ancestor, "delete the child's row and leave the ancestor's row visible… the drop is stable: C4 already refuses to store a row that changes nothing."
   - **Problem:** This conflicts with Eggnog's own C4: "Probe the child's own hash. A hit on a tombstone means 'absent'." Tombstoning the child row would make lookups report the key absent while scans show the ancestor row. That tombstone also has delete semantics (C10, decision 1), not fall-through. And in the main target case, lifted facts where the case value equals the parent value, B1 replaces an old child row with an ancestor row carrying a newer timestamp, so every case gets a spurious semi-naive delta. Today's C8 step 2 merge creates none.
   - **Correction:** B1 needs a distinct pruning mechanism: remove the key from the child's hash, or add a fall-through marker. It also needs either an accepted re-match cost or a stamp rule that keeps the ancestor row old for the child. It also applies only to lifted function values equal to the case join, not to every lifted row.
13. **Claim:** G3: "Eggnog has no fallback yet (see B3)." B3 is presented as Easter Egg's "clone for that one assumption".
   - **Problem:** B3 copies the still-visible ancestor rows into the child, so it adds copying. The cost in G3 is copying the rows that mention a hub loser, and flattening does not reduce it. Hidden sets already become bitsets when large (C3), so filtering is cheap. B3's real benefit is avoiding ancestor index probes that return mostly hidden rows. A heavily touched child already degrades toward clone cost on its own, unlike Easter Egg's unmaterialized colored classes.
   - **Correction:** Present B3 as a probe-efficiency measure, not a fix for G3. For G3, measure copy volume per union (§8.3 #3) and treat heavy children as clones in the benchmarks.
14. **Claim:** Not to borrow: "Pushing parent unions eagerly into children. It conflicts with concurrent children."
   - **Problem:** Under Eggnog's own rules, concurrent children exist only under a frozen parent (C9), which makes no unions and so has nothing to push. A parent that is not frozen already runs sequentially with its children (C5, C7). Eager push therefore never meets concurrency.
   - **Correction:** Give the real reasons: pull is lazy (a child that is never read again skips the work), the parent needs no links to its children, and the cursor log is already present in egglog's row buffers.
15. **Claim:** G1(a): the lift's equality completeness depends on marking, because groups built from marked ids leave out the winner. "The lift as written will miss facts."
   - **Problem:** For equalities, the spec as written already requires the winner. The ScanMerges contract (rule-layer spec §2.1, line 170) says "A merged class's own canonical identifier is reported paired with itself when it is a parent identifier", and storage test 3 checks it. Only the spike's implementation note ("iterates marked") and the wording of L4 rely on mark-both. G1(b), on facts and L3, is a real defect in the lift as written. I verified the example.
   - **Correction:** Restate G1(a) as a porting requirement: implement ScanMerges's contract from the overlay (or from B7's member lists), not from the marked set. Keep G1(b) as the headline lift defect.
16. **Claim:** §5 novelty, points 1, 2, 3 and 7: contexts on a relational engine with lattice merges, materialized views per context, semi-naive evaluation across contexts, and disposal of contexts.
   - **Problem:** "Compared with these three papers only" leaves out Eggnog's own reference design. Per engine map §3.8, egglog's push/pop already gives contexts on a relational engine with lattice merges and per-context analysis values, through fully materialized clones. Its push copies rules "including last_run_at", so semi-naive evaluation already continues inside a clone from the parent's watermark, and pop disposes the clone. Hou, Laddad and Hellerstein (EGRAPHS 2024) propose contexts in egglog as a lattice of equivalence relations, with case-split lifting by intersection. That is a vision paper with no implementation, but it overlaps points 1 and 6 conceptually. Disposal is standard (egglog pop, SMT pop, colored egg's unfinished delete_color).
   - **Correction:** Narrow the claims to what is new. That is: partially materialized views on a shared base (copy on touch plus hidden sets), seeded watermarks over a layered view whose copied rows are re-stamped, pull catch-up from a changing parent, concurrent children of a frozen base, and lifting facts and lattice values. Drop disposal. Cite egglog push/pop and Hou et al. as the closest prior work.
17. **Claim:** G9: "Sibling cases create the same term separately. This is true in both designs."
   - **Problem:** In the papers' basic (monochrome) version, every term goes into black and siblings share it. Separate copies happen only in the optimized colored e-node version.
   - **Correction:** Say that this is true of the optimized Easter Egg and of Eggnog.
18. **Claim:** B2: "overlay keys are canonical in the parent", listed as an invariant (condensed version).
   - **Problem:** C2's alternating chase deliberately tolerates overlay targets that the parent later displaced, until the next catch-up. The invariant holds only after catch-up. The fuller file says so, but the condensed version drops the qualifier.
   - **Correction:** Keep "once catch-up is done".

### Ideas the report missed

- Demand-driven splitting. Easter Egg §V says its prover splits "only… when it enables additional rewrite rules that were previously blocked" (TheSy's potential_split markers). This is the most direct answer to G5's growth in case counts, and the report never mentions it. Eggnog split rules can already encode it in their bodies. Recommend it as guidance for SplitBuilder/DefineDomainSplit, or as a helper.
- ASSUME-node encoding of contexts: Coward, Constantinides and Drane, DAC 2023. Easter Egg cites it as [7] and Hou et al. review it. Contexts become terms that rewrites push down the tree. It is a pure-rule alternative that should be weighed next to B8 and SAS's Under index; its known cost is e-graph growth from the extra ASSUME nodes.
- Hou, Laddad and Hellerstein (arXiv:2507.11897), now checked. It models contexts as a lattice ordered by information, with an order-preserving map to equivalence relations; lifting across a case split is the intersection (meet) of the relations; and it suggests storing e-graphs only for a lower bound of contexts. This is a ready-made 'model paragraph' for G7. It also points to conjunction (DAG) contexts as a future extension, while the report says no work supports combining contexts.
- Join algorithms for congruence closures from abstract interpretation: Gulwani, Tiwari and Necula, FSTTCS 2004; Chang and Leino's congruence-closure abstract domain, VMCAI 2005. They compute the join of two e-graphs by pairing classes, which is exactly the lift's partition meet plus the term lift's bottom-up creation of new terms. They give a precise notion of completeness and known size blow-ups, which would sharpen L3/L4 and justify RoundLimit/CandidateLimit. They also weaken novelty point 6 for equalities.
- Use egglog push/pop itself as the in-house clone baseline for B5/E1. It is already planned as the differential oracle, and since it carries last_run_at it is the fair 'separate e-graphs' comparison.
- TheSy's lift-before-deepening order: merge_conclusions runs after saturating at each split depth before recursing, and again afterwards. Shallow lifts into the parent can make deeper splits unnecessary. Eggnog's §3.8 runs the whole in-case schedule, nested splits included, before lifting a record.
- Further prior art for the novelty section: hypothetical Datalog / embedded implications (Bonner, 1990) for assumptions inside a relational engine; persistent or backtrackable union-find (Conchon and Filliâtre, 2007) and SMT trail-based e-graphs for versioned union-find state. These are worth naming next to the DPLL(T) and versioned-Datalog items already listed as not compared.
