# Cosmos pushdown facts in egglog

A prototype of the fact layer from `D:\calcite-cosmos` (`src/Apache.Calcite.Cosmos.Adapter/Metadata`)
written as an egglog program. It asks one question: does an equality-saturation engine with
lattices express what that adapter's definite Horn theory cannot, while still doing what it does?

## Running it

Build the egglog CLI from the vendored submodule (run `git submodule update --init ext/egglog`
first if it is empty), then run each scenario from this directory:

```bash
cargo build --release --bin egglog --manifest-path ../../ext/egglog/Cargo.toml
```

```bash
for f in 0*.egg; do ../../ext/egglog/target/release/egglog "$f" || echo "FAILED: $f"; done
```

Each file prints the cheapest equivalent predicate it extracts, and exits non-zero if any
`check` fails. `(fail (check ...))` asserts something must *not* be provable.

## Layout

[`prelude.egg`](prelude.egg) is the reusable theory. Each scenario adds what a schema compiler
would emit for one container, asserts one query, and checks the outcome. The prelude splits the
rules into four rulesets so a scenario can run with fewer of them and show the difference:

| Ruleset | Holds | Replaces in the adapter |
| --- | --- | --- |
| `horn` | inheritance between contexts, guarded schema rules, query conjuncts as facts | `CosmosFactTheory` forward chaining |
| `lattice` | domains intersect, a disequality narrows a domain, empty domain or crossed bounds is a contradiction | the `CosmosFact.Entails` / `Excludes` switch tables |
| `split` | case split on a finite domain; lift what every case proves | nothing: Horn has no equivalent |
| `rewrite` | lowering, guard deletion, tautology, contradiction, boolean simplification | `CosmosFactRewriter` and parts of the split rules |

The `horn` ruleset *approximates* the adapter; it is not the adapter. The "Adapter today" column
is confirmed by running it: see [Confirmed against the adapter](#confirmed-against-the-adapter).

## Scenarios

| File | Shows | Adapter today | Here |
| --- | --- | --- | --- |
| `01-discriminated-oneof` | Parity: `kind = 'B' AND CAST(ref AS UUID) = …` lowers; without the discriminator it does not | lowers / does not | same, with `horn` + `rewrite` only |
| `02-query-disjunction` | `kind IN ('order','invoice')`, each kind declaring `ref` a canonical UUID | `OneOf` entails neither guard; stays in process | lowers via a case split; the schema alone still proves nothing (a `note` has no `ref`) |
| `03-combining-claims` | enum `{order, invoice}` plus query `kind <> 'invoice'` proves the `order` guard | `Knows` checks one fact at a time; guard never fires | domain narrows to `{order}`; lowers |
| `04-undiscriminated-anyof` | `anyOf` of two `status` enums, query `status = 'purged'` | `WalkBranches` keeps only identical rule heads; nothing about `status` survives | branch domains are unioned to `{active, archived, draft}`; the predicate becomes `false` |
| `05-intervals` | `maximum: 100`, query `n > 500` | no claim kind for a bound | contradiction; `n > 50` is left alone |
| `06-guard-deletion` | Parity: `IS_DEFINED AND (NOT IS_STRING OR …)` collapses under a declared type; a required const deletes its tautology; a const that is not required does not | same | same, as rewrites plus extraction |
| `07-sweep-links` | sweep's real `links` container and its facade probe's queries; see below | the "park" query pushes only `IS_DEFINED` tests | every query reaches the service |

## A real container: sweep's `links`

[`07-sweep-links.egg`](07-sweep-links.egg) encodes `sweep-app/src/Sweep.Cosmos.Data/Schemas/links.schema.json`
(with `common.schema.json`) and the queries in `Sweep.Tests/Facade/Data/FacadeLinkPushdownLiveProbe.cs`.
`data.type` is a required enum `park | map | spot` discriminating a `oneOf`; `park` and `map` each
require a canonical-lowercase `data.data.parkId`, `spot` has none.

| Probe query | Here |
| --- | --- |
| "map": `data.type = 'map' AND mapId = X` | lowers (parity) |
| "park", as `data.type IN ('park','map') AND parkId = X` | lowers, by the split |
| "park", spelt per kind: `(type = 'park' AND parkId = X) OR (type = 'map' AND parkId = X)` | lowers in both disjuncts |
| "offline refresh": the above `AND offline AND changeUtcTime > since` | everything lowers, the instant range on the fixed-width form |
| the facade view's own filter, `type = 'Link' AND data.type IN ('park','map','spot')` | the `IN` is the whole required enum, so it is deleted; `type = 'Link'` stays (only declared a string) |
| `data.type <> 'map' AND parkId = X` | correctly not lowered: a `spot` may be among the rows |

**How much the split costs on a real schema: six contexts.** The "offline refresh" query makes
three cases under `(Schema)` and three under `(Query)`, and derives 40 stored-form rows across all
of them. The whole suite of seven files runs in about half a second. The other sweep containers
(`parks`, `hikes`, `users`, `complaints`) declare no `oneOf` and no enum on their `type`, so they
split on nothing. Scoped contexts are an optimisation for later, not a day-one requirement.

**The per-kind spelling needed no factoring rewrite.** Two analyses over the e-graph, `inset`
(the predicate *is* `p IN S`) and `admits` (the predicate *implies* `p IN S`), see through `AND`
and `OR`, so `(type = 'park' AND …) OR (type = 'map' AND …)` establishes `type IN (park, map)`
however it is written. They replaced the two-value `IN` special case the first version had.

**The schemas say less than the data does.** `parks`, `hikes` and `users` each hold several kinds
under `$.type` but declare it a plain string, so nothing can be conditioned on a kind. `hikes`
leaves `userId` out of `required` because tracks and waypoints lack it; a `oneOf` on `type` would
let `type = 'Hike'` prove it present. `complaints.status` is a closed C# enum declared as a string.
These are schema changes in sweep, not engine changes.

## Confirmed against the adapter

The gaps are now tests, on the adapter's branch `fact-gap-tests`, in
`src/Apache.Calcite.Cosmos.Adapter.Tests/EndToEnd/CosmosDeclaredFactPlanningTests.Gaps.cs`. Each gap
has a control differing in one respect that passes, so the failure is the reasoning's and not the
schema reader's. Run: 64 tests, 7 fail, all of them gaps; all controls and existing tests pass.

| Test | What the adapter sends instead |
| --- | --- |
| `Gap1_TwoKindsNamedTogetherLowerTheReference` | `(c.kind = @p0 OR c.kind = @p1) AND IS_DEFINED(c.ref)` |
| `Gap1Sweep_ParkAndMapLinksByParkLowerTheParkId` | `(c.data.type = @p0 OR c.data.type = @p1) AND IS_DEFINED(c.data.data.parkId)` |
| `Gap1Sweep_ParkAndMapLinksByParkSpeltPerKindLowerTheParkId` | `IS_DEFINED(c.data.type) AND IS_DEFINED(c.data.data.parkId)`: not even the kind test pushes |
| `Gap2_ADomainLessARefusedValueProvesTheGuard` | `… c.kind != @p0 … AND IS_DEFINED(c.ref)` |
| `Gap3_AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains` | the comparison, as written |
| `Gap4_AComparisonBeyondADeclaredBoundIsFalse` | a pushed range, not `false` |
| `Gap5_AnIntegerOutsideADeclaredDomainIsFalse` | `n > 499 AND n < 501`, rechecked in process |

Gap 5 was not predicted. It turned up as a failing control for gap 4: an integer `enum` is not
consulted when the comparison goes through a `CAST`, though the string form of the same
contradiction works.

## What it showed

**Horn is the wrong boundary, but disjunction did not need a SAT solver.** The adapter keeps every
rule head a single atom so that checking entailment stays linear. All four gaps closed without
giving that up. Each fact is held *relative to a context*: `(Schema)`, `(Query)`, or
`(Assume c p v)`, meaning "c, plus p is present and equals v". A split creates one child context
per value a discriminator can still hold. A fact is lifted back to the parent once every case
proves it, or for a domain, once every case has reported one and they are unioned. It still runs
as monotone rules over lattices.

The split is sound only because the cases cover the parent. That holds because a split happens
only where the path is *present*, and one case exists per remaining value. Domains only shrink,
so a stale case makes the requirement stricter, never looser. An undiscriminated `anyOf` becomes a
hidden discriminator over its branch indices, which is why 02 and 04 are the same mechanism.

**The Entails/Excludes tables became six rules.** Combining claims (03), the semantic join (04)
and bounds (05) come out of the merge functions instead of hand-written pairwise cases. Adding a
claim kind means adding a lattice and its contradiction rule, not a row in two tables.

**Keeping Schema and Query apart matters, and the prototype makes it explicit.** A conjunct is
lowered when the *query* (which still contains its guard) proves the form. It is deleted only
when the *schema* proves it. Otherwise a conjunct proves itself redundant: the last block of 06
is that trap, and it is caught.

**Rewriting and extraction fit naturally.** Guard deletion is four rewrites; the cheapest
predicate is extracted rather than produced by ordered rewriter code. The cost model here is a
toy (an in-process comparison costs 1000, a pushed one costs 1). A real one would come from the
RU measurements in the adapter's DESIGN.md.

## What it costs, and what it implies for a .NET library

- **Contexts copy facts by rule.** Every fact is re-derived in every child context. On sweep's
  schemas that is six contexts at most, so it can ship as is. Nested discriminators multiply,
  though, and the remedy is known: make scoped contexts first-class, with children reading
  through to the parent instead of copying (the "colored e-graph" idea), and split only on paths
  a query touches.
- **One soundness invariant has to be enforced, not assumed.** A lowering judged on `(Query)`
  relies on facts the predicate's own conjuncts proved, so it is sound only while those conjuncts
  stay in the rewritten predicate. The prototype keeps that by letting only `(Schema)` facts
  delete anything. A library should make this structural rather than a convention.
- **The host emits rules at run time.** Schema compilation produces rules per container, so the
  library needs a runtime API for adding rules and rulesets, not only compile-time languages.
  This argues for egglog's model over egg's `define_language!`.
- **The egglog subset used:** constructors and functions with `:merge`; `Set` containers;
  `i64` with `min`/`max`; rulesets and `saturate` schedules; `push`/`pop`; `check`/`fail`;
  cost-annotated extraction. No proofs, no parallelism, no floats or bigints.
- **Shortcuts the prototype takes:** domains hold strings only; nulls and stored numbers are not
  modelled; lifting covers forms, presence and domains but not bounds or types; a literal is
  assumed already in the stored spelling; `items` and spatial claims are absent.

## Next steps

1. Decide whether the adapter should move onto an engine like this, or take the cheaper wins
   in place first. Gap 2 (combining claims on one path) and gap 5 fit the current design; gaps 1
   and 3 need case splitting, which is the engine change.
2. Add a `oneOf` on `type` to sweep's multi-kind schemas, which pays with either design.
3. Use these files as the first conformance tests for the .NET engine.
