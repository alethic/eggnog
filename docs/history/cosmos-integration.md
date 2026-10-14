# Cosmos integration plan

**Status:** proposed, 2026-10-12 (revision 2). This plan moves the declared-fact reasoning of Apache.Calcite.Cosmos
onto Eggnog. The two reasonings are compared in shadow mode first, and the switch-over is made per fact kind.
Revision 2 answers a review of revision 1. Appendix B lists every problem raised and where this revision answers it.

**Eggnog's side (2026-10-13).** Everything this plan waits for in Eggnog is released: scopes, frozen scopes, local
rules, concurrent children, tags and mirrors (the plan's R2), and case splits and lift (its R3), on top of the
definitions, rules, schedules, queries and extraction it calls R1b. Every phase's Eggnog entry is therefore met, and
the R2a/R2b split of §8.3 request 1 is no longer needed. The plan still names Eggnog's features by the milestones
that delivered them (R1b, R2, R3). Its citations of the two specifications (RLS, CCS) are of their text at `b041e70`;
both are now in `docs/history`, as the design record, and so is this plan. The library requests of §8.3 that are still
open are listed in `docs/roadmap.md`.

## 0. Sources, refs and notation

### 0.1 What was read

**Adapter**
- Repository: the adapter's (Apache.Calcite.Cosmos) at `origin/main` = `7e6ae6a` (the merge of #206). It was read with
  `git show` and `git grep`. The main checkout is on the feature branch `fix/with-statistics-keeps-keys` and was not
  used.
- Abbreviations: `A/` = `src/Apache.Calcite.Cosmos.Adapter/` and `T/` = `src/Apache.Calcite.Cosmos.Adapter.Tests/`.
  Every `A/`, `T/`, `TODO.md` and `docs/` citation is at `origin/main` unless it names another ref.
- On this ref the fact types are `JsonFact`, `JsonClaim`, `JsonFactRule`, `JsonFactSet`, `JsonFactTheory` and
  `JsonSchemaFacts`, in `A/Facts/`. The names `CosmosFact*` are those of `a83fd2a` (#174). Commits `8244e58` and
  `f7fa006` moved and renamed them [facts §0].

**Gap tests**
- File: `T/EndToEnd/CosmosDeclaredFactPlanningTests.Gaps.cs`, plus a one-line `partial` edit.
- Both are uncommitted in a local working copy of the adapter, on a test branch at `a83fd2a`, 46 commits behind
  `origin/main` [gaps §0]. Line numbers below are of that
  working copy and are cited as `Gaps.cs:n`.

**Disjunction attempt**
- Uncommitted edits in another local working copy of the adapter, also at `a83fd2a`.
- Superseded on main by #196 (`27ba003`) [gaps §3].

**Eggnog**
- Repository: Eggnog at `b041e70`.
- Main held R1a–R1b and colors M10.1–M10.2 when this plan was written (`2fd4a18`; the status file of that date, since
  replaced by `docs/roadmap.md`).
- Abbreviations, all at `b041e70`:
  - **RLS** = `docs/history/rule-layer-spec.md`
  - **CCS** = `docs/history/colored-contexts-spec.md`
  - **PS** = `src/Eggnog.Tests/Surface/PublicSurface.txt`

**Study reports**
- The four reading reports of this study are cited as [facts §n], [planner §n], [gaps §n] and [eggnog §n]. They are
  not kept in either repository; every claim taken from them also cites the source it rests on.
- The study worked in throwaway copies that are not kept either: the 13 `A/Facts` files compiled alone, with a harness
  that reproduced F1–F4; read-only copies of the sources; and an R1b cost harness over a `git archive` of eggnog
  `b041e70` (§6.1). Phase 0a writes F1–F4 as tests, and Phase 1 repeats the measurements with the project's method.

**Provenance.** Three earlier candidate plans were written: shadow-first, value-first and architecture-first. This
plan starts from the shadow-first plan and takes in the others' strongest parts. Wherever they disagreed, the question
was settled against the sources (§0.2).

### 0.2 Points settled against the sources

| # | Point | Finding | Source |
|---|---|---|---|
| 1 | The "no process-wide cache" policy | "nothing here should grow a process-wide cache"; what a schema learns hangs off the schema | `TODO.md:135-141` |
| 2 | A numeric `CAST` also matches stored strings | "`= 30` matches a document storing `"30"`". Bounds read from a cast therefore never imply a numeric type. | `A/Rel/Convert/CosmosFilterSplitRule.cs:648-656` |
| 3 | Saturating casts | `CAST(x AS INTEGER) = 2147483647` is true of a document storing 1e30. The split rule leaves a side open **only for equality**, when the bound is at or beyond that side's limit; it argues that the inequalities "already admit everything past the limit". That argument covers `>` near `Max` and `<` near `Min`. It does not cover `>` with a bound below `Min` or `<` with a bound above `Max` (§2.8). | `CosmosFilterSplitRule.cs:703-719` |
| 4 | Eggnog publishing | A GitHub Packages push on every non-PR build (to the repository owner, `alethic`). A GitHub release and a nuget.org push on a tag or a manual publish. `src/dist-nuget` packs the packages; it is not a feed. | `.github/workflows/Eggnog.yml:182-241` @ `b041e70` |
| 5 | Adapter package sources | nuget.org and `ikvmnet` only. Credentials for a GitHub feed are supplied out of band. | `nuget.config` |
| 6 | Target frameworks | Eggnog targets `net8.0;net10.0` (`src/Eggnog/Eggnog.csproj:4`). The adapter targets `net8.0` (`A/Apache.Calcite.Cosmos.Adapter.csproj:4`). | — |
| 7 | R1b surface | `Scope.IsContradictory` is at PS:304 and `Scope.Rows` at PS:314. There is no `CreateChild` or `Freeze` (PS:299-327). `Scope.Run` takes a `CancellationToken` (PS:318), checked before every step (`src/Eggnog/Scheduling/ScheduleExecutor.cs:98`). | PS |
| 8 | Child memory figures | 98–101 KB per child (CCS:2826) is measured on a workload of 100 merges plus one rule run (CCS:2443, 2524). A bare child is 7.4–9 KB, plus 2.7–3.0 KB per touched table (CCS §14.2, l.2164-2182). Child time is not measured (CCS:2826). | CCS |
| 9 | Scalar equality | `JsonScalar.Equals` separates the Decimal and Double kinds and compares decimals by value (`A/Facts/JsonScalar.cs:190-205`). `Type` answers `Integer` for any whole number (l.161-167). | — |
| 10 | Stored-form identity | `IJsonStoredForm` promises value equality (`A/Facts/IJsonStoredForm.cs:27`). `CosmosRepresentation` is a record struct over its name, two bits and width (`A/Metadata/CosmosRepresentation.cs:47`). | — |
| 11 | Decimal bounds need no library change | `ValueLattice<T>` has a public constructor with a case join (PS:382), and `ValueStore.Register<T : IEquatable<T>>` takes an order (PS:407) | PS |
| 12 | `AdmitNull` and extensions | A head that entails `Present` is kept under an added `NotEqualTo(p, null)` guard (`A/Facts/JsonSchemaFacts.cs:627-628`); `CosmosGeography` entails `Present` (`A/Metadata/CosmosGeography.cs:55-60`). An extension head at the union's own path that does **not** entail `Present` is dropped (`_ => null`, l.633-644). | — |
| 13 | R1b status in the spec | RLS says R1b is "complete, not yet merged" (RLS:21-23, 3625, 4651). The status file of that date and `2fd4a18` show it on main. | — |
| 14 | When `WithFacts` runs | Inside `ReadContainers` (`A/CosmosSchemaFactory.cs:459-463`), before the schema object exists (l.235-236); once per database for an account schema (l.313), which is built afterwards (l.318). `WithFacts` returns `this` when nothing is added (`A/Metadata/CosmosContainerMetadata.cs:587-601`), so an undeclared container keeps the process-wide `static readonly ServiceFacts` theory (l.545-547). `Copy()` shares `_facts` (l.437-448). `CosmosSchema` has a public constructor over host-built metadata (`A/CosmosSchema.cs:39-48`). | — |
| 15 | `Lazy` and exceptions | `_stated` is a `Lazy<JsonFactSet>` with `ExecutionAndPublication` (`A/Facts/JsonFactTheory.cs:83`). Such a `Lazy` caches a factory exception and blocks other callers until the factory returns. | — |
| 16 | Limits | `TimeLimit` is checked before every iteration and overshoots by at most one (`src/Eggnog/Scheduling/RunOptions.cs:31-36`). Limits are per top-level `Run`, and `IterationLimit` counts iterations in every scope of the run, cases included (RLS:140, D42). | — |
| 17 | Freeze pre-plans | A non-root `Freeze` plans "every global rule, every local rule of this scope and its ancestors, every query, every split matcher" that has no plan yet, on that scope's statistics (RLS:2929-2933, E10 at RLS:2163). Plans are made once per rule (D26, RLS:114; risk 4, RLS:4478-4481). | — |
| 18 | Local rules on R2 | `RulesFor` returns the global rules plus every ancestor's local rules of the scheduled rulesets and "is recomputed for every iteration" (RLS:2605-2608). A frozen scope's local rules are fixed (RLS:2923-2928). A delta scan costs one binary search per layer (D5, RLS:93). | — |
| 19 | Host-driven cases | `Scope.OpenCases`, `CaseSet` and `ScopeContradictedException` are R3 members (RLS:497-502, 3666). `TheSameSplitDrivenByTheHost` (RLS:4363-4400) is an R3 test. On R2 the only children are `CreateChild` children (RLS:413-424). | — |
| 20 | Release of scopes | `Dispose` defers release only while live children exist or a call is in progress on another thread (D40, RLS:2943-2952). The graph "holds only managed memory and is not disposable" (D40, RLS:138). | — |
| 21 | Data-source lifetime | A `CalciteDataSource` is shared by every connection with an equivalent connection string, so `create` runs once per data source; a pooled data source is released only after `ConnectionIdleLifetime` with no open connection. | `TODO.md:135-148` |
| 22 | R2's dependencies | R2 depends on M10.3 plus E4, E5, E7, E9 and E10 (RLS:3631). E5 "gates the whole R2 scoped suite" (RLS:2159). | — |

---

## 1. Summary

### 1.1 Decisions

1. **Eggnog replaces the closure, not the rewriter.**
   - Eggnog replaces `JsonFactTheory.Derive` and the questions on `JsonFactSet`. This is architecture **A**.
   - The adapter keeps `CosmosFactExtractor`, `CosmosFactRewriter`, the translator and every consumer.
   - Predicate rewriting by extraction (architecture **B**) is an optional later phase.
   - The seam is about seven questions [planner §1, §10]. Each difference is therefore a statement about one answer to
     one question.
2. **Correctness first.** Findings F1–F5 [facts §10] are wrong or unsound answers today. They are fixed in Horn before
   any comparison, so that they do not fill the difference log. The fixes change pinned plans and unit results; Phase
   0a lists each such test and rewrites it deliberately (§5.1).
3. **One front end, two reasonings.**
   - The schema compiler (`JsonSchemaFacts`) and the extractor stay. They feed both engines, so every difference is a
     reasoning difference.
   - Bounds travel as a `JsonClaim.Extension`, which Horn ignores.
4. **Four modes per fact kind:** `horn`; `shadow`; `union` (Eggnog adds to Horn, and Horn stays authoritative);
   `eggnog` (Horn runs in reverse shadow). Every answer of either engine is sound, and consumers compose sound answers
   soundly, so `union` and any mix of kinds are sound.
5. **Determinism.** It rests on:
   - a mode snapshot, a fact budget and a memo per statement;
   - serial graphs (`DegreeOfParallelism` 1, D46);
   - memoising across statements only outcomes that do not depend on timing. A run stopped by `TimeLimit` is used by
     no one and is remembered only for the rest of its statement (§3.6).
6. **Ownership.** Container metadata carries only the immutable compiled declaration. Reasoning state (the Horn and
   Eggnog facts, memos, breakers, the Eggnog host) belongs to the schema object, which binds it to its tables (§3.2).
7. **R2 topology.**
   - One `EGraph` per schema object and generation, with an empty frozen root.
   - One frozen container scope per distinct declaration digest, holding local rules, shared across the databases of
     an account schema.
   - Undeclared containers are answered host-side; no near-empty scope ever freezes (§3.3).
   - Transient query children, with children for CASE arms and `OR` disjuncts.
   - Cases inside `Run` at R3. Fallback: one graph per container.
8. **On R1b today.** Offline shadow with one graph per derivation; the adapter's own conformance tests; optionally,
   sampled asynchronous production shadow (gate G1); optionally, a gated `union` route for gap 1 if R3 is late.
9. **Gap order follows user value.** Gap 1 turns scans into indexed lookups. Gaps 3–5 save one cheap round trip; they
   ship with a logical-level rule that turns a contradiction into an empty `Values`.
10. **Rollback at four levels**, each effective at the next statement: per kind (a runtime override beside the model
    operand), per process (`ForceHorn`, which also stops all background Eggnog work), per container (the breaker),
    and per release.
11. **Horn's closure is kept as the fallback engine** after the switch. It is removed only when the fallback fraction
    (budget B10) shows that the outright snapshot alone is an acceptable fallback (§5, Phase 4).

### 1.2 The gaps, by value

| Gap (`Gaps.cs` line @ `a83fd2a`) | Pushed today (`prototypes/cosmos-facts/README.md:91-99` @ `b041e70`) | After | Value | Route | First closes |
|---|---|---|---|---|---|
| 1, `IN` (l.64; Sweep l.257) | `(c.kind = @p0 OR c.kind = @p1) AND IS_DEFINED(c.ref)`. The UUID is compared in process over every order and invoice. | `c.ref = @p` | highest | query split on the discriminator, then lift of `form` | Phase 3 (R3); optional earlier route, track H |
| 1, per-kind spelling (l.269) | `IS_DEFINED(c.data.type) AND IS_DEFINED(c.data.data.parkId)` | both disjuncts lowered | highest | derivation per disjunct | Track D, Horn alone |
| 2 (l.106) | `c.kind != @p0 … AND IS_DEFINED(c.ref)` | `c.ref = @p` | high | a disequality narrows a domain (L3) | Phase 2 (R2) |
| 3 (l.139) | the comparison as written | empty `Values`, no statement | low | one `Entails` row; Eggnog schema split at R3 | Phase 0a |
| 4 (l.178) | a pushed range | empty `Values`, no statement | low | bounds (L6–L9) | Phase 2 |
| 5 (l.197) | `n > 499 AND n < 501`, rechecked in process | empty `Values`, no statement | low | bounds narrow a domain (L6) | Phase 2 |

A `FALSE` condition still costs one round trip today, because the contradiction is reached inside the converter and
lands on a `CosmosFilter` that `FILTER_REDUCE_EXPRESSIONS` does not match (`TODO.md:1457-1461`). The logical-level rule
(a `LogicalFilter` over a Cosmos scan whose condition the facts contradict becomes an empty `Values`) ships with gap 3
in Phase 0a and serves gaps 4 and 5 later. Where that rule cannot fire (a contradiction that only the converter's
rewrite reaches, for example inside a `CASE` arm), the converter-level `FALSE` remains, and a test keeps asserting it.

### 1.3 What each Eggnog milestone allows

| | R1b (on main) | R2a / R2 | R3 |
|---|---|---|---|
| Offline closure parity and conformance | yes, one graph per derivation | yes | yes |
| Sampled asynchronous production shadow | if gate G1 passes (§6.3) | yes | yes |
| Synchronous decisions on the planning thread | only the optional track H, behind the gate | yes | yes |
| Gaps 2, 4, 5 | in tests | decided | decided |
| Gap 1 `IN` | track H (case graphs, capped) | track H (plain children, host-side lift, capped) | engine splits |
| Gap 1 per kind | Horn (track D) | child per disjunct | same |

R2a is the tagged prerelease this plan requests (§8.3, request 1).

---

## 2. Mapping the adapter's reasoning onto Eggnog

### 2.1 Value encodings

| Adapter value | Eggnog encoding | Why |
|---|---|---|
| `JsonDocumentPath` (`A/Facts/JsonDocumentPath.cs:41`) | `string` as a JSON Pointer: `""` is the root, `/a/b`, with `~0`/`~1` escapes | Injective, including for names that contain `.` or `/`. Hidden discriminators (R3) use keys that start with `#`, which no pointer produces. |
| `JsonScalar` (`A/Facts/JsonScalar.cs:34`, `IEquatable` only) | Tagged `string` inside `ValueSet<string>`: `null`; `b:true`/`b:false`; `s:<text>`; `i:<integer>` for a whole Decimal; `f:<decimal>` for a fractional Decimal (invariant culture, trailing zeros trimmed); `d:<double "R">` for the Double kind | `RegisterSet` refuses a type without `IComparable` (`src/Eggnog/Values/ValueStore.cs:144-146`). Built-in string sets are ordinal (`ValueSetOrder{T}.cs:18-20`). The encoding follows `JsonScalar.Equals`: `1` and `1.0` both become `i:1`, and Decimal and Double stay apart (§0.2 #9). |
| `JsonType` + `OrNull` | Members of `types: ValueSet<string>` over `{string, integer, fraction, boolean, object, array, null}` | `"integer"` → `{integer}`; `"number"` → `{integer, fraction}`; `OrNull` adds `null`. This is JSON Schema's reading and removes F1 by construction. `typeOf(x)` reads the tag; for `d:` it reads the value, as `JsonScalar.Type` does. |
| `IJsonStoredForm` (`A/Facts/IJsonStoredForm.cs:27`) | Token `form:k` from a registry per generation, keyed by value equality | §0.2 #10. The adapter keeps the two bits and the ranking (`A/CosmosFactSetExtensions.cs:31-41`). |
| Numeric bounds (new) | A registered `NumericBound(decimal Value, bool Open)`. Tables `lo` and `hi` use user lattices: `lo` merges to the greater bound (open wins a tie) and joins cases to the lesser; `hi` mirrors that. | The built-in `Max`/`Min` lattices are closed and need `IComparable` (PS:374-375). The adapter needs decimal and exclusive bounds [gaps §6]. |
| A query literal outside the path's declared vocabulary | **Canonicalised before anything is written** (below) | Interning (D15, RLS:103; risk 3, RLS:4475-4477), memo keys, and redaction. |

**Literal canonicalisation.** Every derivation's literals are renamed, per path, before the established facts are
written or keyed. The renaming is injective on the derivation's literals and is undone when a snapshot is read back
(`TryGetConstant` returns the real literal).
- **Strings, booleans and non-numeric scalars** outside the declared vocabulary of every path become slots of the same
  kind (`?s1`, `?b1`, …), numbered by first appearance within the derivation. This is exact because no rule reads string
  contents: rules see such values only through equality, membership and the type tag.
- **Numbers** (equality literals and bound endpoints alike, after the conversion slack of §2.8 is applied on real
  values) that are not themselves declared breakpoints of their path are replaced by **order representatives**. A
  path's breakpoints are its declared bound values and its declared numeric domain members. Let `b₁ < … < bₙ` be the
  breakpoints, and let the derivation's non-breakpoint numbers on that path fall `m` to an interval; the `j`-th of them
  in ascending order becomes `bᵢ + j·(bᵢ₊₁ − bᵢ)/(m + 1)` inside `(bᵢ, bᵢ₊₁)`, `bₙ + j` above the last breakpoint,
  `b₁ − (m + 1 − j)` below the first, and `j` on a path with no breakpoints. The type tag (`i:`, `f:`, `d:`) is kept;
  only the value is replaced. This preserves equality and order among all numbers the path's rules can compare, and
  type tags, which are the only ways L1–L9 and conditional bodies read a number (§2.3, §2.5). It is therefore exact.
- The number of distinct values a schema can intern is bounded by its declared vocabulary times the largest number of
  literals per path in one predicate, not by the literals that requests carry. Memo keys depend on the literals' order
  type only, so a range query re-planned with new literals hits the memo.

### 2.2 Claims to tables

Every claim except `Present` and a presence-entailing extension means "the value, **if present**, …"
(`A/Facts/JsonClaim.cs:35-159`; `JsonFact.cs:100-105`). All tables are value-only, as RLS D1 requires of anything read
across scopes (RLS:89).

| `JsonClaim` case | Table | Merge |
|---|---|---|
| `Present` (l.44) | `present(path)`, relation | — |
| `OfType(T, OrNull)` (l.35) | `types(path) : ValueSet<string>` | `ValueLattice.Intersect` (case join ∪) |
| `EqualTo v` (l.92) / `OneOf S` (l.109) | `dom(path) : ValueSet<string>`, asserted as `{enc v}` / `enc S` | `Intersect` |
| `NotEqualTo v` (l.103) | `ne(path, enc v)`, relation | — |
| `Represents f` (l.159) | `form(path, token)`, relation | — |
| `Extension e` (l.68-86) | `ext(path, token)`, relation, plus probed rules (§2.7) | — |
| `JsonBounds` (new extension, §2.7) | `lo(path)`, `hi(path) : NumericBound` | user lattices (§2.1) |
| (new) "nothing can be there" | `absent(path)`, relation | — |
| (R3) split demand | `discriminator(path)`, `mentioned(path)`, relations | — |

### 2.3 The switch tables become rules (one ruleset per capability)

`JsonFact.Entails` (`A/Facts/JsonFact.cs:32-86`) and `Excludes` (l.113-143) become nine single-path rules plus set
tests in the questions (§2.4). The prototype found the same: "The Entails/Excludes tables became six rules"
(`prototypes/cosmos-facts/README.md:123-126` @ `b041e70`). Every rule touches one path, which §2.9 relies on.

Each capability is its own ruleset, so that the explainers can name and ablate it (§4.5): `L1` … `L9`, `extension` and
`horn`. They are scheduled together as `Saturate(horn, L1, …, L9, extension)` (`Schedule.Saturate(params Schedule[])`,
PS:296; a ruleset converts to a schedule, PS:253).

| Rule | Body ⇒ head | Replaces, or adds |
|---|---|---|
| L1 a domain settles the type | `dom(p) = d` ⇒ `types(p) ∩= typesOf(d)` (pure `Compute`) | Entails `(EqualTo, OfType)` and `(OneOf, OfType)`. It replaces `CosmosTheory`'s string-only rule (RLS:3995-4000; `prelude.egg:84`). |
| L2 a type narrows a domain | `types(p) = t`, `dom(p) = d`, `Where` some member's type ∉ t ⇒ `dom(p) := {x ∈ d : typeOf(x) ∈ t}` | Exclusive `(EqualTo, OfType)` and `(OneOf, OfType)`. Adds the missing row "OfType T ⇒ NotEqualTo v of another type" [facts §3.1 (c)]. |
| L3 a disequality narrows a domain | `ne(p, v)`, `dom(p) = d`, `Where Contains(d, v)` ⇒ `dom(p) := Remove(d, v)` (RLS:4007-4013, retyped) | Gap 2. Exclusive `(EqualTo, NotEqualTo)` and `(OneOf, NotEqualTo)`. |
| L4 a form describes strings | `form(p, f)` ⇒ `types(p) ∩= {string, null}` | Entails `(Represents, OfType(String, OrNull))` |
| L5 null rules | `ne(p, null)` ⇒ `types(p) ∩= all ∖ {null}`; `types(p) ⊆ {null}` ⇒ `dom(p) ∩= {null}` | The `OfType{Null}` rows (`JsonFact.cs:70-82`) |
| L6 bounds narrow a domain | `dom(p) = d` with `lo`/`hi` ⇒ remove the `i:`/`f:` members outside the bounds. Keep `s:`, `b:`, `null`, `d:` and slots. | Gap 5 (D37; RLS:4014-4025, retyped to decimal and open bounds) |
| L7 crossed bounds exclude numbers | `lo(p) > hi(p)` (an equal value with either side open also counts) ⇒ `types(p) ∩= all ∖ {integer, fraction}` | Gap 4. Bounds mean "if the value is a number". A numeric cast also keeps a stored string (§0.2 #2), so crossing excludes numbers, never the path. |
| L8 nothing can be there | `dom(p) = ∅` or `types(p) = ∅` ⇒ `absent(p)` | The value half of every `Exclusive` row |
| L9 contradiction | `present(p)`, `absent(p)` ⇒ `Contradiction` | `IsContradictory`. It requires presence, as the prototype does (`prelude.egg:95-98`). This fixes F3. |

**Worked checks.**
- **Gap 4.** The schema gives `types {integer}` and `hi 100`. The query `CAST(n AS INTEGER) > 500` gives `lo 499 open`
  and `present(n)`. L7 empties `types`, L8 gives `absent`, and L9 gives `Contradiction`. With no `type` declared,
  `types` stays `{string, …}` and no contradiction follows, which is correct: a stored `"600"` passes the cast.
  Canonicalisation keeps this: 499 lies above the breakpoint 100 and becomes `101` (`bₙ + 1`), still above `hi`.
- **Gap 5.** `dom {i:1, i:2, i:3}` against the window (499, 501) is emptied by L6, then L8 and L9 follow. A control
  with `enum ["600", 1]` and `CAST(n AS INTEGER) > 500` keeps `s:600` and is left alone.

**Rows with no counterpart, and why:**
- `(OfType{Null}, Represents)` and `(EqualTo{null}, Represents)` are vacuous. `FormsOf` lists only stated forms
  (`JsonFactSet.cs:62-73`), so no consumer can see them.
- `(OfType a, OfType b.OrNull)` becomes set inclusion in the question.
- The theory has **no arithmetic rule**. The query slack is computed host-side (§2.8), so no lattice has an infinite
  descending chain and saturation terminates. RLS risk 6 does not arise, and limits remain a safety net.

### 2.4 Questions as set tests

| Adapter row or question | Eggnog read |
|---|---|
| `EqualTo a ⇒ OneOf b`; `OneOf ⊆ OneOf` (gap 3, missing in Horn); `OneOf {v} ⇒ EqualTo v` (missing) | `dom ⊆ b` and `dom ≠ ∅` |
| `⇒ OfType(T, n)` | `types ⊆ U(T, n)` and `types ≠ ∅` |
| `⇒ NotEqualTo v` | `ne(p, v)`, or `v ∉ dom`, or `typeOf(v) ∉ types` |
| Integer and Number (missing in Horn) | by the `types` encoding |
| `Extension ⇒ …` | `ext(p, token)` and the probed rules |

### 2.5 Rules (`JsonFactRule`) into facts and rules

A rule `JsonFactRule(Body, Head)` (`A/Facts/JsonFactRule.cs:26`) is translated as follows:
- **Empty body.** One `Assume`. All of them go into the container's scope in one `Write` batch (D30, RLS:123).
- **Non-empty body.** A rule in ruleset `horn`. On R1b it is a global rule of the derivation's graph. From R2 it is a
  **local rule** of the container's scope (`Scope.DefineRule`, RLS §6.7), defined before `Freeze`, visible to that
  container's queries and cases and invisible to sibling containers (`LocalRuleTests`, RLS:3519). It generalises
  `CosmosTheory.DeclareBranch` (RLS:3890-3904).
- **Rules with identical bodies** are merged into one rule with several heads.

**Body atoms:**

| Body atom | Rule body |
|---|---|
| `Present(k)` | `present(k)` |
| `EqualTo(k, v)` | `d = dom(k)`, `Where(d ⊆ {v} ∧ d ≠ ∅)` |
| `OneOf(k, S)` | `d = dom(k)`, `Where(d ⊆ S ∧ d ≠ ∅)` (`Primitives.IsSubsetOf`, PS:178) |
| `OfType(k, T, n)` | `t = types(k)`, `Where(t ⊆ U(T, n) ∧ t ≠ ∅)` |
| `NotEqualTo(k, v)` | `excl(k, v)`. For each distinct guard atom, three materialising rules derive it from `ne(k, v)`, from `v ∉ dom(k)`, and from `typeOf(v) ∉ types(k)`. |
| `Represents(k, f)` | `form(k, token)` |

**Presence in guards (F4).** Phase 0a changes the compiler for both engines. Because the rule bodies carry the atoms,
the translation adds nothing, and the two engines read the same guards. The compiler gains four carriers:

| # | Site (`A/Facts/JsonSchemaFacts.cs`) | Change |
|---|---|---|
| 1 | Discriminator guards (l.307-333) | add `Present(disc)` |
| 2 | `else` guards (l.688-692) | add `Present(k)` |
| 3 | `AdmitNull`'s `NotEqualTo(p, null)` guard (l.625-629) | add `Present(p)` |
| 4 | `WalkNegation` (l.238-252) | at a non-root path, guard the `Present(child)` and `NotEqualTo` heads with `Present(path)`, exactly as `required` builds its carrier (l.128-136). Today `{properties:{o:{not:{properties:{k:{const:"A"}}}}}}` compiles to an unconditional `Present(o.k)`, false of a document with no `o`. The fix covers a `not` reached through `then`, `allOf` or `dependentSchemas` at a nested path, because the carrier is added inside `WalkNegation` from its own path. Consumers of an outright `Present` include `CosmosAggregate.cs:373`, `CosmosSortRule.cs:236` (through `IsAlwaysScalar`), `CosmosFactRewriter.cs:633` and `CosmosPartitionKeyExtractor.cs:566`. |

`if`/`then` guards stay as they are. A document lacking `k` satisfies the `if` vacuously, so firing `then` on a declared
`const` is sound there. An `if` that requires `k` already carries `Present(k)` (l.704-751).

### 2.6 Schema compiler constructs [facts §4.2]

| Construct | Phases 1–2 (translated rules) | Phase 3 (native emission for splits) |
|---|---|---|
| `type`, `nullable`, `["T","null"]` | `types`. Two or more non-null types read nothing today (l.916-953). | Emit the set; this is the E+ class `multi-type` |
| `const`, `enum`, `pattern` + recogniser, `required`, `properties`, `allOf`, `if`/`then`/`else`, `dependent*`, `not` | as translated (§2.5) | unchanged |
| Discriminated `oneOf`/`anyOf` | branch rules plus `Meet` under the parent guard (l.307-333) | also `discriminator(disc)` |
| Undiscriminated `anyOf`/`oneOf` | `Meet` only (l.348-357, 396-520) | a hidden discriminator `#anyOf:<schema location>`: `present(#…) ⇐ present(parent)` (unconditional at the root), `dom(#…) = {i:0 … i:k-1}`, `discriminator(#…)`, and each branch's rules under `present(#…) ∧ dom(#…) = {i}`. The coverage obligation (RLS risk 1) becomes the compiler's. Hand encoding: `src/Eggnog.Tests/Cosmos/Literal/Literal04UndiscriminatedAnyOfTests.cs:20-36` @ `b041e70`. The compiler also emits `governs(#…, p)` for every path `p` a branch's rules state (§2.8). |
| Nullable union | `AdmitNull` widening (l.625-647), with the presence guard of §2.5 | unchanged |
| `minimum`, `maximum`, `exclusive*` | Read as `JsonBounds` through the subschema recogniser (§2.7). `AdmitNull` gains a row that keeps `JsonBounds` at the union's own path, because bounds bind numbers only and `JsonBounds` does not entail `Present` (§0.2 #12). | unchanged |

### 2.7 Extensions

**Generic encoding by probing.**
- `Extension.Entails` (a virtual method the theory cannot call at run time, `JsonClaim.cs:68-86`) is probed against a
  finite set of value-free core claims: `Present`; `OfType(T, OrNull)` for the 7 × 2 combinations; the stored forms of
  the adapter's form catalogue (the forms its recognisers can return, `A/Metadata/CosmosNumericForms.cs`,
  `CosmosUuidForms`, the instant forms); and the other registered extensions.
- One rule `ext(p, token) ⇒ …` is emitted per positive answer. For `CosmosGeography` (`A/Metadata/CosmosGeography.cs:45-62`)
  this yields `present(p)` and `types(p) ∩= {object}`, as today.
- **When.** Every extension rule is a global rule defined once per generation, together with L1–L9, before the root
  freezes. No global rule is ever defined while a container compiles; a definition made then would join every other
  container's concurrent runs at their next iteration (D6, RLS:94; `RulesFor`, RLS:2605-2608) and be planned on
  whichever context first ran it (D26). An extension answer that depends on a container (a form outside the catalogue
  that only one declaration states) becomes a local rule of that container's scope.
- Entailments of value claims (`EqualTo`, `OneOf`, `NotEqualTo`) cannot be probed exhaustively and are not encoded.
  That loses facts and is sound.

**Bounds as an extension.**
- `JsonBounds(decimal? Minimum, bool MinimumExclusive, decimal? Maximum, bool MaximumExclusive)` is a new
  `JsonClaim.Extension` in the Facts namespace. Its `Entails` is the default `Equals`, and nothing excludes it. Horn
  therefore treats it as inert, and `JsonClaim`'s closed union does not change.
- **Schema side.** The subschema recogniser becomes "geography, else bounds" (`A/Metadata/CosmosSchemaRecognition.cs:27-29`).
  It is asked of every node and stated beside `type`, under the node's guard (`JsonSchemaFacts.cs:118-121`).
- **Query side.** The extractor emits `JsonBounds` from the numeric window (§2.8).
- **Saturation engine.** It recognises `JsonBounds` and asserts `lo`/`hi`. Values beyond decimal range are not read,
  which loses facts and is sound.

### 2.8 What a query establishes (`A/Metadata/CosmosFactExtractor.cs`)

The extractor stays in the adapter and serves both engines.

**Changes in Phase 0a.**
- **`Present` beside comparisons.** The extractor emits `Present` beside every comparison it resolves to a
  container-rooted path: `EqualTo`, `NotEqualTo` and an `IN`-shaped `OneOf` (l.112-145, 216-244), and also an order
  comparison or a numeric-cast comparison on a resolved path (`CosmosRexTranslator.TryNumericCastOperand`), for which it
  emits `Present` only. A true SQL comparison implies a value. With L9 this makes contradiction require presence on the
  query side, which is F3's other half. `Present` is added only beside a fact the extractor resolved: a literal that is
  no JSON scalar (a UUID, an instant) still proves nothing (`TrySide`, l.191-197).
- **`Present` is not inert under Horn.** It answers `Knows(Present)` on derived sets and satisfies rule bodies with a
  `Present` atom. Two plan changes follow, both sound, both recorded in Phase 0a with tests:
  1. `CosmosFilterSplitRule.Guarded` reads `present` from the derived facts (l.293-312, derived in `Establish`,
     l.234-252) and drops the `IS_DEFINED` guard. A sibling `x <> 'a'` now removes `IS_DEFINED(c.x)` from a weakened
     comparison on `x`.
  2. `dependentRequired` and `dependentSchemas` rules have body `Present(trigger)` (`JsonSchemaFacts.cs:186-196`), so
     `trigger = v` now fires them.
- **Rendered text (F2).** A bare `JSON_VALUE(...) = 'text'` is read as `EqualTo` only when
  `CosmosRexTranslator.IsUnambiguousTextFor` holds (`A/Sql/CosmosRexTranslator.cs:1178`) or the path is declared a
  string outright, the test the `CAST` shape already passes (`:1091-1110`). Otherwise it records `Present` only.

**Change in Phase 0b (the numeric window).**
- `CosmosFilterSplitRule.TryBoundNumericCast` (l.662-720) moves into a shared `CosmosNumericWindow` helper, used by both
  the split rule and the extractor. The helper resolves the path through `CosmosRexTranslator.TryNumericCastOperand` and
  applies the conversion slack ("moves it by less than one", l.641-647).
- It applies the saturation rule **as the code has it**: for **equality only**, a bound at or beyond the cast type's
  limit leaves that side open (l.704-719). The split rule's output does not change.

**Open hazard settled in Phase 0a, before the window feeds L6 or L7.** The code's argument covers inequalities near
their own limit only (§0.2 #3). `CAST(x AS INTEGER) > -3000000000` over a stored `-1e30` saturates the stored value to
`Min`, which passes the cast, while the pushed `x > -3000000001` refuses it. Phase 0a adds a test that plans and runs
this shape (and its mirror, `<` with a bound above `Max`) on the in-process path and on the emulator. If Calcite folds
the comparison before the split rule sees it, the test pins that. If not, the row is lost today; that is finding F10,
fixed in the window by opening the side whenever the bound lies beyond the **opposite** limit as well. L6 and L7 read
only windows that pass this test.

**Change in Phase 1.**
- The extractor reads `CAST(p AS <numeric>) op n` through the window as `JsonBounds` (its `Present` already landed in
  0a). Raw numeric comparisons, where the extractor already resolves a numeric literal (for example a VARIANT-promoted
  key [facts §10 F1]), are read as "if numeric" bounds.
- `JsonBounds` is inert under Horn, so Horn's plans do not change in Phase 1.

**What crosses into Eggnog.**
- One `Write` batch of the established facts after canonicalisation (§2.1).
- From R3, `mentioned(p)` for every path the predicate compares or tests, and `mentioned(#h)` for each hidden
  discriminator `#h` with `governs(#h, p)` for a mentioned `p`. This is RLS risk 11's `governs` remedy
  (RLS:4497-4501), done host-side because architecture A supplies its demand directly. The demand gate reads the same
  emission (§3.6).

### 2.9 Questions as snapshot reads

A derivation ends in an immutable, adapter-side **snapshot**. Scopes never leave the call that opened them (D39, D40).

| Question (`A/Facts/JsonFactSet.cs`, `A/CosmosFactSetExtensions.cs`) | Seam member (§3.2) | Snapshot answer |
|---|---|---|
| `Knows(Present)` (l.40-50) | `IsPresent(p)` | `present(p)` |
| `Knows(OfType …)`; `IsAlwaysScalar` (l.150-160); `IsOneScalarType` (`CosmosSelfJoinRule.cs:654-661`) | `AdmitsOnly(p, JsonTypeSet)` | `types(p) ⊆ S ∧ types(p) ≠ ∅`. `IsAlwaysScalar` becomes `IsPresent ∧` (`AdmitsOnly {string}` or `{integer, fraction}` or `{boolean}`); `IsOneScalarType` is the same check with `null` allowed. Both now go through entailment, which removes their order dependence [planner §9.3; facts §10 F8]. |
| `Knows(EqualTo v)`; `ClaimsFor` scanned by `CosmosPartitionKeyExtractor.Declared` (l.569-576) | `TryGetConstant(p, out v)` | `dom(p)` a singleton, mapped back through the derivation's renaming. Routing asks `IsPresent ∧ TryGetConstant`, including null. |
| `Knows(NotEqualTo v)` | `Excludes(p, v)` | `ne(p, v) ∨ v ∉ dom(p) ∨ typeOf(v) ∉ types(p)` |
| `FormsOf`, `RepresentationOf` (l.62-73; extensions l.31-41) | `FormsOf(p)` | `form(p, t)` for the tokens the container's rules state for `p`, ranked by the adapter as today |
| `IsAlwaysGeography` (l.73-76) | `Knows(p, Extension)` | `ext(p, token)` |
| `IsContradictory` (l.97-109) | `IsContradictory` | `Contradiction` (`Scope.IsContradictory`, PS:304) |

**Which paths a snapshot reads:**
- **Outright snapshot**, read once at freeze: every path in the container's rules, plus the service paths.
- **Derivation snapshot:** the established paths, the container's **volatile paths** (the heads of conditional rules)
  and (R3) the paths a case can write. It is stored as a **delta** from the outright snapshot, holding only paths whose
  answer differs; every other path answers as outright. Memo entries therefore cost bytes in proportion to what the
  derivation changed, not to the container's size (§3.6).
- **Why this is exact.** Every global rule (L1–L9, extension rules) is local to one path, and only conditional rules
  move facts across paths. This holds for split-free schedules; with splits (R3) the case paths are added explicitly.
- **How paths are read.** `Check` and `TryGet`, one hash probe per layer (CCS §14.2). `ne` rows come from one reusable
  variable query per graph (`ForEachMatch`, D49, RLS:147).
- **Test-build check.** Test builds compare each snapshot with a full `Scope.Rows` read of every claim table (PS:314).

### 2.10 The adapter's implicit contexts as scopes [facts §3.5; planner §4]

| Context | Built by | Scope class | Eggnog (R2) |
|---|---|---|---|
| Outright, `Derive(null)` (`JsonFactTheory.cs:83`) | schema plus service facts | outright | the frozen container scope; snapshot taken at freeze |
| Query, `Derive(Extract(condition))` | `CosmosFilterRule.cs:74`, `CosmosFactRewriter.cs:92`, `CosmosImplementor.cs:963` | derived | a transient child of the container scope |
| Restricted (`CosmosFilterSplitRule.Establish`, l.234-252) | the conjuncts that translate unaided | derived | a sibling child over that subset |
| CASE arm (`TryFlattenCase`, l.350-386, derive at 370) | query ∪ the arm condition | derived | `IJsonFactSession.Child`: a child of the open query scope |
| Projection CASE conditions (`CosmosProjectRule.cs:39` → `CosmosFactRewriter.RewriteConditions`, l.151-206 → `Rewrite`, which derives and may answer `FALSE`, l.91-98) | the WHEN condition | derived | as a query derivation, one per WHEN condition |
| Per disjunct (new; track D) | query ∪ the disjunct's conjuncts | derived | as for a CASE arm |
| Projection orderability, sort, aggregate, routing, joins, `IsDeclaredStringOutright` | outright only | outright | outright snapshot |

The scope class is what the `form:outright` and `form:derived` qualifiers of §3.9 select. The rule "outright, never
under a guard" for deletion and routing (`CosmosFactRewriter.cs:607-611`; `CosmosRexTranslator.cs:1257-1282`) stays
where it is enforced today: in the consumer's choice of snapshot. Eggnog's erasing licences (D12) matter only in
architecture B.

### 2.11 Rewrites (`A/Metadata/CosmosFactRewriter.cs`)

Architecture A keeps every rewrite in adapter code and points its questions at the seam. If architecture B (Phase 5,
optional) is taken up, the mapping is:

| Rewriter step | Eggnog form (B) | Licence |
|---|---|---|
| Contradiction → `FALSE` (l.97-98, 373) | "contradicted predicate" → `PFalse` (RLS:4073-4079) | `ReadsOwnFacts` |
| Tautology deletion, outright only (l.625-635) | "declared tautology" reading `Present.In(Schema)` and `Dom.In(Schema)` (RLS:4090-4106) | erasing under `schema` (D12) |
| `IS_DEFINED` and type-guard deletion (`CosmosFilterSplitRule.Guarded`, l.282-313) | "declared presence", "NOT IS_STRING" and a numeric twin (RLS:4081-4121) | erasing, or `ReadsOwnFacts` |
| UUID, instant and integer lowering (l.732-974) | One rule per form token and operator family. `PreservesEquality`/`PreservesOrder` are bits per token. `ParsesExactly(token, format, held)` and `EngineReads(token, held)` are **primitives over the query term** (the format string and the target temporal type are read from the term, l.878-903), not bits per token. Rendering the literal (`RenderUuid`, `RenderDateTime`) is a **partial primitive** in the rule body: when it refuses (l.798-799, 841-842), the rule does not fire, as the rewrite declines today. | `ReadsOwnFacts` |
| `TryFlattenCase`, `TryLiftStrictCase`, `SEARCH` expansion | stay as adapter pre-passes over the Rex tree | — |
| Cost | a `CostModel` from the measured request-unit ladder (`TODO.md:1634-1637`) | — |

B needs translation from `RexNode` to terms and back, with `Opaque` leaves (`ExtractedTerm.TryMatch`, PS:86-90). Typed
handles stop at four key columns (D13). A partial primitive needs the "no result" outcome RLS lists as unplanned
(RLS:4720), so B also depends on that request (§8.3, request 10). B is justified only by the planned cross-path `CHECK`
constraint (`A/DESIGN.md:962-965`) or by profiling.

### 2.12 Gaps: what closes each, and when

| Gap | Mechanism | Adapter work | Eggnog | Phase |
|---|---|---|---|---|
| 3 | The `(OneOf a, OneOf b) ⇒ a ⊆ b` row in `JsonFact.Entails`. `Meet` already offers the joined domain (`JsonSchemaFacts.cs:464-484`) and accepts candidates through `Entails`, so the shared compiler needs the row whichever engine reasons [facts §9; gaps §2]. | one row; logical empty-`Values` rule | none (the general case is the R3 schema split) | 0a |
| 1, per-kind spelling | Derivation per disjunct in `Apply` on `OR` (`CosmosFactRewriter.cs:245-265`), as `TryFlattenCase` does for a CASE arm (l.358-377) [gaps §6] | rewriter | none; a child session from R2 | D |
| 2 | L3 | none | R1b tests; R2 production | 1 (E+ control); switched in 2 |
| 4 | `lo`/`hi`, L7–L9 | `JsonBounds`, recogniser, window reading | R1b tests; R2 | 1; switched in 2 |
| 5 | L6, L8, L9 | window reading | R1b tests; R2 | 1; switched in 2 |
| 1, `IN` (adapter and Sweep) | Query split on present, mentioned discriminators; lift of `form` (RLS:4166-4207). Track H does the same by host-driven cases. | `discriminator`/`mentioned`/`governs` emission | R3 (track H: R1b or R2) | 3 |
| A fact outright in one undiscriminated branch and conditional in another (the horn branch's `AnUndiscriminatedBranchAgreesOnlyWhereTheConditionAgreesToo`) | hidden discriminator plus a query split with `governs` demand | compiler | R3 | 3 |

### 2.13 Findings, and how each enters the plan [facts §10; planner §9]

| Finding | Handling |
|---|---|
| F1: `"type":"number"` beside an integral `enum`/`const` makes every filter `FALSE` (`JsonSchemaFacts.cs:955-965`; `JsonType.cs:20-22`; `JsonFact.cs:134,140`) | 0a: `Integer ⊂ Number` in `Entails`/`Exclusive`; `JsonType.Number` documented as any number. Eggnog: removed by the encoding. |
| F2: a rendered-text equality read as a stored value, then refuted | 0a: a plan-level test first, then the extractor fix (§2.8) |
| F3: two declared claims flagged contradictory | 0a: Horn's `IsContradictory` (`JsonFactSet.cs:97-109`) pairs claims only on paths where `Present` is known. Eggnog: L8–L9. |
| F4: guards satisfied by "if present" claims; `not` at a nested path states presence without its parent | 0a: the four carriers of §2.5; `Present` from comparisons (§2.8) |
| F5: `ReferenceEquals` on a struct (`CosmosPartitionKeyExtractor.cs:600`) | 0a: `path.IsRoot` |
| F6: the "declares nothing" fast path never fires (`CosmosContainerMetadata.cs:545-547`) | 0b: `HasDeclarations` on the compiled declaration. The engine is skipped when the container declares nothing beyond the service facts and the established facts hold **at most one claim other than `Present` per path** and touch no service path. That case is exact: no conditional rule exists, and a lone `Present` beside one value claim excludes nothing (Horn pairs claims on one path for exclusion, `JsonFactSet.cs:97-109`). |
| F7: alias-blind consumers | 0a: tests over `CosmosUnnest` aliases. Both engines share this input, so the shadow cannot see it (risk R1). |
| F8: lossy rows; order-dependent `ClaimsFor` scans | 0b: questions go through entailment. Lossy rows other than gap 3 are left to Eggnog and serve as the shadow's positive controls (explainer `lossy-rows`). |
| F9: documentation drift (`TODO.md:1612-1617`; `DESIGN.md:682`; the doubled `<summary>` at `CosmosFactRewriter.cs:677-731`) | 0a |
| F10 (suspected): a numeric-cast inequality whose bound lies beyond the opposite saturation limit (§2.8) | 0a: a test first; a window fix if a row is lost |

---

## 3. Integration architecture

### 3.1 Projects, ownership and packaging

**Projects**
- **`src/Apache.Calcite.Cosmos.Facts`** (new). `A/Facts/` moves here; this is a recorded plan ("The fact theory as a
  package of its own", `TODO.md:1651-1664`; `DESIGN.md` "Planned Project Layout"). The namespace references nothing of
  the adapter, Java or IKVM [facts §0, §11]. It holds the seam (§3.2), the Horn engine, the host-side closure (§3.3)
  and `JsonBounds`.
- **`src/Apache.Calcite.Cosmos.Facts.Saturation`** (new). The Eggnog engine. It references Facts and the `Eggnog`
  package and holds the theory, translation, encodings, canonicalisation and snapshots.
- **Test projects:** `…Facts.Tests` and `…Facts.Saturation.Tests` (new). They are fast and need no IKVM.

**When the adapter depends on Eggnog.** The adapter references Facts from Phase 0b, and Facts.Saturation from Phase 1b
or 2, when production shadow starts. Before that, only tests consume the Eggnog engine. A test asserts that with every
kind in `horn` mode no `Eggnog` assembly is loaded.

**Ownership.** The production theory belongs to the adapter. Eggnog's `CosmosTheory` stays test code (RLS:3711) and the
library's conformance reference.

**Packaging.** The adapter consumes Eggnog as a tagged **nuget.org prerelease**, pinned (§0.2 #4–5). The GitHub
Packages feed would need a new source and out-of-band credentials (`nuget.config` remarks).

### 3.2 The seams and the wiring

**Engine seam, in `Apache.Calcite.Cosmos.Facts`.** Names are proposals; the conventions are one type per file and XML
documentation.

```csharp
public interface IJsonFactAnswers
{
    bool IsContradictory { get; }
    bool IsPresent(JsonDocumentPath path);
    bool AdmitsOnly(JsonDocumentPath path, JsonTypeSet types);
    bool TryGetConstant(JsonDocumentPath path, out JsonScalar value);
    bool Excludes(JsonDocumentPath path, JsonScalar value);
    IReadOnlyList<IJsonStoredForm> FormsOf(JsonDocumentPath path);
    bool Knows(JsonDocumentPath path, JsonClaim.Extension claim);
}

public interface IJsonContainerFacts
{
    bool HasDeclarations { get; }
    IJsonFactAnswers Outright { get; }
    IJsonFactSession Open(IReadOnlyList<JsonFact> established, JsonFactDemand demand = default);
}

public interface IJsonFactSession : IDisposable
{
    IJsonFactAnswers Answers { get; }
    IJsonFactSession Child(IReadOnlyList<JsonFact> established);   // a CASE arm or an OR disjunct
}

public interface IJsonFactEngine
{
    IJsonContainerFacts Compile(JsonFactDeclaration declaration);  // rules, digest, extension encodings
}
```

The Horn engine wraps `JsonFactTheory`; `Child` is `Derive(established ∪ more)`. The Saturation engine is described in
§3.3–§3.8.

**What the metadata holds.** `CosmosContainerMetadata` holds only the immutable **`JsonFactDeclaration`**: the combined
service and declared rule list and its canonical digest (SHA-256 of the canonical rule list). `WithFacts` builds it,
exactly where it builds the `JsonFactTheory` today (`A/Metadata/CosmosContainerMetadata.cs:587-601`); `Copy()` shares
it. An undeclared container holds the service declaration, which is immutable, so the existing `static` service theory
(l.545-547) carries no state that could become a cache. Nothing reasoning-related that can grow lives on metadata.

**What the schema holds.** `CosmosSchema` and `CosmosAccountSchema` each own one **`CosmosFactReasoning`** instance, a
schema-scoped context holding:
- the compiled container facts per declaration digest, for each engine (Horn always; Eggnog when any kind is not `horn`);
- the Eggnog `SaturationHost` (§3.3) and its generation;
- the container memos, keyed by digest, so the databases of an account schema share one (§3.6);
- the breakers, per digest;
- the configured modes.

The schema constructor binds each table's metadata to its reasoning: `CosmosTable` holds
`metadata.WithReasoning(reasoning)`, a copy that carries a reference to the schema's `CosmosFactReasoning`. Because the
binding is made per schema, metadata reused across schema instances or data-source recycles is never bound to another
schema's graph.

**Wiring at each entry point.**

| Entry | Reasoning |
|---|---|
| `CosmosSchemaFactory.create` / `CreateAccountSchema` | builds the `CosmosFactReasoning` from the `factReasoning` operand before `ReadContainers`, and passes it to the `CosmosSchema` or `CosmosAccountSchema` constructor (`A/CosmosSchemaFactory.cs:235-236, 313-318`). `ReadContainers` itself is unchanged: it still calls `WithFacts`, which now builds a declaration. |
| The public `CosmosSchema` constructor (`A/CosmosSchema.cs:39-48`) | gains an optional `CosmosFactReasoning? reasoning` parameter. When it is `null`, the schema creates its own context in `horn` mode. |
| Tests that build a schema (`T/EndToEnd/CosmosDeclaredFactPlanningTests.cs:572-574, 1303-1305`) | the harness passes an explicit test `CosmosFactReasoning` in the mode under test (§4.2). There is no static or ambient install. |
| Metadata used with no schema (unit tests calling the rewriter directly) | an unbound metadata answers through a stateless Horn closure of its declaration: no memo, no breaker, no Eggnog. |

**Statement context and signatures.** Each call site reaches the statement context (§3.6) through the planner, so the
entry points that today take only a `RexBuilder` change signature:
- `CosmosFactRewriter.Rewrite` (l.79) and `RewriteConditions` (l.151) take a `CosmosFactStatement statement` before
  `rexBuilder`; the `Conditions` visitor and every translator they build receive it.
- Callers obtain it with `CosmosFactStatement.For(rel.getCluster().getPlanner())`: `CosmosProjectRule.Pushed`
  (l.39, through `project`), `CosmosFilterRule`, `CosmosFilterSplitRule`, the implementor (through the rel), and the
  other sites of the checklist.
- Tests that call `Rewrite` directly pass `CosmosFactStatement.Detached()`, a context per call.

Each call site then asks `reasoning.Outright(statement, container, site)` or
`reasoning.Open(statement, container, established, site)`. Parameter types change from `JsonFactSet` to
`IJsonFactAnswers`: `CosmosRexTranslator`'s constructor (`A/Sql/CosmosRexTranslator.cs:118`), `CosmosProject.IsOrderable`
(`A/Rel/CosmosProject.cs:289-317`) and the `CosmosFactSetExtensions`.

**Call-site checklist for Phase 0b** (`git grep` at `origin/main`; paths under `A/`):

| Kind | Today | Sites |
|---|---|---|
| K1 contradiction | `IsContradictory` | `Metadata/CosmosFactRewriter.cs:97, 373` |
| K2 presence | `Knows(Present)` | `CosmosFactRewriter.cs:633`; `Rel/Convert/CosmosFilterSplitRule.cs:297`; `Rel/CosmosAggregate.cs:373`; `Metadata/CosmosPartitionKeyExtractor.cs:566` |
| K3 types | `Knows(OfType …)`, `IsAlwaysScalar`, a literal `OfType` scan | `Sql/CosmosRexTranslator.cs:1301, 1324, 3093`; `CosmosFilterSplitRule.cs:293` (claims at 427, 732-733); `Rel/Convert/CosmosSortRule.cs:236`; `Rel/CosmosProject.cs:316`; `Rel/Convert/CosmosSelfJoinRule.cs:656` (`ClaimsFor`) |
| K4 constants | `Knows(EqualTo)`, a `ClaimsFor` scan | `CosmosFactRewriter.cs:634`; `CosmosPartitionKeyExtractor.cs:569` |
| K5 stored forms | `RepresentationOf`/`FormsOf` | `CosmosFactRewriter.cs:755, 787, 890, 962`; `CosmosRexTranslator.cs:2000, 2051, 2115`; `CosmosProject.cs:297`; `Rel/CosmosSort.cs:297`; `CosmosSelfJoinRule.cs:636`; `Rel/Convert/CosmosLookupJoinRule.cs:212` |
| K6 geography | `IsAlwaysGeography` | `CosmosSortRule.cs:280` (through `CosmosFactSetExtensions.cs:75`) |
| Outright derivations | `Derive(null)` | `CosmosImplementor.cs:915`; `CosmosFactRewriter.cs:118`; `CosmosRexTranslator.cs:1299`; `CosmosPartitionKeyExtractor.cs:564`; `CosmosProjectRule.cs:79`; `CosmosProjectSplitRule.cs:213`; `CosmosSelfJoinRule.cs:419`; `CosmosSortRule.cs:150, 182`; `CosmosAggregate.cs:241, 315, 320`; `CosmosProject.cs:89`; `CosmosSort.cs:297`; `CosmosLookupJoinRule.cs:212` |
| Query derivations | `Derive(established)` | `CosmosFilterRule.cs:74`; `CosmosFilterSplitRule.cs:251`; `CosmosImplementor.cs:963`; `CosmosFactRewriter.cs:92`; CASE arm `CosmosFactRewriter.cs:370`; projection CASE conditions `Rel/Convert/CosmosProjectRule.cs:39` (through `RewriteConditions` → `Rewrite`) |

### 3.3 Scope topology (R2 onward)

```
EGraph            one per schema object and generation; DegreeOfParallelism 1
└─ root           theory and every global rule defined first (L1–L9, extension rules), then frozen; empty of facts
   ├─ decl#A      the representative container, compiled and frozen first (below)
   │  ├─ query    transient, one per derivation memo miss; disposed inside the call
   │  │  ├─ arm   a CASE arm or an OR disjunct (IJsonFactSession.Child)
   │  │  └─ case  R3 only: opened by a split inside Run, gone when Run returns (RLS:3073-3077)
   │  └─ query    the split rule's restricted derivation, as a sibling
   └─ decl#B …    one per further declaration digest
```

**Graph per schema object, not per process.** This keeps to the adapter's policy (`TODO.md:135-141`), is the spec's
"one per family of containers" (RLS:243), and amortises theory definition (about 1.3–3.0 ms and 815 KB for the
prototype theory on R1b, §6.1) across containers.

**Container scopes are keyed by declaration digest.** An account schema applies the same declarations to the same-named
container in every database (`A/CosmosSchemaFactory.cs:302-322`), so those containers share one frozen scope, one
outright snapshot and one memo.

**No near-empty scope ever freezes.** A non-root freeze plans every global rule that has no plan yet on that scope's
statistics (§0.2 #17). A frozen scope for the six service facts would therefore plan every global rule on near-empty
statistics for the whole graph. Instead:
- **Undeclared containers** never reach Eggnog. Their declaration is the service facts alone, all unconditional
  (`A/Metadata/CosmosServiceFacts.cs:44-73`). With no conditional rule, the closure of each path depends only on that
  path's facts, so the **host-side closure** (the same pure functions L1–L9 use, applied per path) is exact. It is the
  same code the demand gate's skip path uses (§3.6), and the gate's shadow checks it against Eggnog in every test run.
- **Service facts are assumed into each declared container scope**, not into a shared intermediate scope.
- **Warm-up order.** The first container to freeze fixes the plans of every global rule. Warm-up compiles first the
  declared container with the most rules (the representative), then the rest in declaration order. The order is
  recorded in the warm-up's diagnostics. Whether a later container suffers from those plans is measured (B3 per
  container) and is the trigger for the fallback below, and for library request 7 (§8.3).

**`WithFacts` on a container that already has facts** produces a new declaration from the concatenated rules, as the
adapter does today. Schema scopes are not nested [eggnog §3.7]. Depth stays at 4 or less, against a limit of 31
(RLS:416).

**Fault isolation.** A faulted root makes a graph unusable (risk 8, RLS:4490-4491); the root here never runs. A faulted
container scope affects one declaration: its breaker (§3.8) sends it to Horn and schedules a recompile.

**Fallback: one graph per container.** Taken if benchmarks show plans suffering from risk 4, or if fault isolation
proves insufficient.

**What architecture A needs of R2:** `ContextEngineScope`, `CreateChild`, `Freeze`, deferred `Dispose` (D40), local
rules, concurrent children of frozen scopes, thread-domain guards (D47), and E4, E7, E9 and E10 (RLS:2147-2172). It needs
no tags, mirrors, licences or E5. §8.3 request 1 asks for these as a tagged R2a prerelease.

### 3.4 Lifecycle

**At schema creation (Phase 2).**
- `create` builds the `CosmosFactReasoning` and its `SaturationHost` (a graph, the theory and every global rule, the
  frozen root) lazily on the background worker (§4.3), never on `create`'s path.
- Warm-up compiles every declared digest in the order of §3.3. For each, after compile, it runs **one representative
  derivation** (the container's discriminator, or its first conditional body path, as an established `EqualTo`), so the
  layered child path is JIT-compiled before the container is marked ready.
- A statement whose mode snapshot (§3.6) finds a container not ready uses Horn for it, and shadow records `SKIP`.

**Compile state.** Each digest's compile state is a volatile field with the states `NotStarted`, `Compiling`,
`Ready(facts)` and `Failed(retryAt)`, advanced only by the background worker. Planning threads read it and never wait.
A failure records a retry time for the breaker; nothing caches the exception. (`_stated`'s `Lazy` with
`ExecutionAndPublication`, `JsonFactTheory.cs:83`, would both cache a failure and block a planning thread for a whole
compile, §0.2 #15; Horn keeps it, because Horn's compile is synchronous today and stays so.)

**Compiling a container on R2** (RLS:4248-4260, 4313-4327):
1. `root.CreateChild()`. Thread-safe on the frozen root (RLS:414-416).
2. One `Write` with the unconditional facts.
3. `DefineRule` for each conditional rule (local, ruleset `horn`), and for any container-dependent extension rule.
4. `Run(Saturate(horn, L1, …, L9, extension))`, asserting `Saturated`. Saturating before freeze is required (CCS:2182).
   The `RunReport` is kept with the compiled facts (§4.5).
5. `Freeze()`.
6. Read the outright snapshot.

**One derivation on R2:**
1. Canonicalise the established facts: order, deduplicate, rename literals (§2.1).
2. Apply the gate (§3.6). If it skips, build the snapshot host-side from the outright snapshot plus the established
   facts.
3. Check the statement's fact budget (§3.8). If it is spent, answer from Horn (or, after Horn's removal, from the
   outright snapshot), and count `fallback{budget}`.
4. Look up the statement memo, then the container memo (§3.6).
5. On a miss: `using var q = schema.CreateChild()`; `q.Write(…)` with the facts in one batch;
   `q.Run(schedule, limits, cancellation)`. From R3 the schedule settles facts before the split step (D43, RLS:141).
6. Read the snapshot (§2.9) as a delta from outright.
7. `Dispose`. No `ClassId` is ever obtained, so D39's origin rules cannot bite.

**On R1b (Phases 1, 1b and track H).** The same theory code runs on a fresh graph per derivation: define the theory,
assume the container's unconditional facts and the established facts at the root, define the container's conditional
rules as global rules (merged and sliced by path relevance, §6.1), run, read, discard. The outright snapshot is computed
once per container in its own graph, which is then discarded. R1b has no child scopes (PS:299-327), and its facts are
global at the root, so a graph cannot be shared across derivations [eggnog §1]. The tables and rules are those R2 runs.

### 3.5 Thread safety

- **Calcite** plans one statement per thread, and statements plan concurrently against one schema [planner §2].
- **R2.** Frozen container scopes are read without a guard; their children are created concurrently (RLS §6.12; D17,
  RLS:105). Each query child and everything under it is one thread domain, created, run, read and disposed inside one
  call on the planning thread. Snapshots are immutable.
- **R1b.** A graph used from two threads throws `InvalidOperationException` (`src/Eggnog/Graph/ScopeGuard.cs:5-34`). A
  graph per derivation is confined to one thread by construction.
- **User delegates** never call into a graph (D47). These are the `Compute` functions of L1, L2 and L6, `typeOf` and the
  bound lattices. They are pure and total (risk 7).
- **Compile** runs only on the background worker, through the state field of §3.4.

### 3.6 Caching and the demand gate

**Statement context.**
- `CosmosFactStatement` is held in a `ConditionalWeakTable<RelOptPlanner, CosmosFactStatement>` (§3.2 says how call
  sites reach it).
- The first fact question of a statement fixes: the mode of each kind (from the operand and the runtime override,
  §3.9); whether `ForceHorn` is set; the readiness of each container; the generation of each schema's host; and the
  statement's fact budget (§3.8).
- Every later question uses that snapshot. Rule-time and implementation-time answers therefore always agree, which the
  adapter requires: "a rule admitting a projection the node then refuses is a plan that fails at implementation"
  (`A/Rel/Convert/CosmosProjectRule.cs:76-78`).
- The context memoises answers per (declaration digest, scope class, canonical key). This removes the repeated `Close`
  per match, `convert`, split `matches`/`onMatch` and implementation [planner §6]. Every outcome is memoised here,
  including a `TimeLimit` stop and an environmental exception, so answers never change within a statement.

**Container memo.**
- One per declaration digest, owned by the schema's `CosmosFactReasoning`, so the databases of an account schema share
  it.
- Bounded **in bytes**: proposed 1 MB per digest and 8 MB per schema, LRU. Values are snapshot deltas (§2.9) in
  canonical space. Keys are canonical established sets (plus the arm facts for a child), which depend on literals only
  through their order type (§2.1).
- **Only outcomes that do not depend on timing enter it:** a completed run, a stop on `IterationLimit` or `RowLimit`
  (deterministic with `DegreeOfParallelism` 1, D46), and a `RuleEvaluationException` (the delegates are pure). A
  `TimeLimit` stop, a cancellation, an `ObjectDisposedException` or an `InvalidOperationException` is kept in the
  statement memo only, and the next statement retries it.
- Because keys are independent of literals, the memo is reused across executions of a statement re-planned on every
  execution (`docs/19-performance.md:99-101`).

**Outright snapshots** are built once per digest and generation.

**Demand gate.** Eggnog runs for a derivation only when its answer can differ from the outright snapshot overlaid with
the established facts. Claims other than `Present` are what can interact; a `Present` beside one value claim cannot
change exclusion (`JsonFactSet.cs:97-109`). The gate fires when one of these holds:
- an established path appears in the body of a conditional container rule, directly or through the precomputed
  path-dependency closure of the rules;
- an established path has an outright claim other than `Present`;
- an established path carries **two or more established claims other than `Present`**;
- (R3) the host emits `mentioned(k)` for a discriminator `k` the container declares, directly or through `governs`
  (§2.8). This is the same demand the split rule's guards read, so the gate never withholds a split the engine would
  make.

For split-free schedules the gate is exact by the locality argument of §2.9. With splits, exactness rests on the gate
reading the split demand itself. In tests (`shadow-assert`), every derivation also runs ungated and is compared, so the
gate is shadowed.

**Neutral controls.**
- In the ThreeKinds container, `JSON_VALUE(c."DOC", '$.name') = 'x'` on an undeclared path establishes `EqualTo` and
  `Present` on `name`. No rule body mentions `name`, there is no outright claim, and only one non-`Present` claim, so
  Eggnog is not called (a counter assertion).
- `CAST(ref AS UUID) = …` establishes nothing at all: a UUID literal is no JSON scalar (`TrySide`, l.191-197).

**Track H (optional).** A host-driven case driver, used only when the gate finds a present discriminator `k` with a
finite query domain of **at most 8 values** and a path with a conditional `Represents` head and no Horn form. Above 8
values it is skipped and counted.
- On R1b it opens one fresh graph per value of `dom(k)`, each holding the facts plus `present(k)` and `dom(k) = {v}`.
- On R2 the cases are plain `CreateChild` children of the query scope (RLS:413-424), one per value, each with the same
  assumptions. There is no `OpenCases` or `CaseSet` before R3 (§0.2 #19), so the lift is host-side: each child's
  `IsContradictory` is read first; a contradicted child drops its value; a form is lifted when every remaining child
  proves it, and is written into the snapshot (not back into the query scope). The children are plain scopes, which
  stay readable when contradictory, so `ScopeContradictedException` cannot arise.
- Coverage holds because every kept document has `k` present with a value in `dom(k)`.
- At R3 `Schedule.Split` replaces the driver; it is retired, not ported to `OpenCases`.

### 3.7 Lifetime, generations and memory

- **Sessions** are disposed by `using`. `Dispose` never throws (D40).
- **Container scopes and generations are never disposed explicitly.** The graph holds only managed memory (D40,
  RLS:138). A swapped-out generation, and its scopes, are dropped by reference and collected once no statement holds
  them. A statement pins its generation through its mode snapshot (§3.6), so its next memo miss runs on a live
  generation, and answers within a statement never change.
- **Schema disposal.** `CosmosSchema` and `CosmosAccountSchema` become `IDisposable`, as TODO's client-disposal item
  plans (`TODO.md:143-148`). Their `Dispose` cancels the reasoning's background work, drops the host, and releases the
  client they created.
- **Generation swap.** Triggers: `ValueStore.InternedCount` (PS:411) passing a threshold (proposed 10⁶, set from the
  production slope); a theory change; a faulted scope that cannot be recompiled. The swap runs on the background worker:
  1. build the new generation and **compile and warm every digest** the old one had compiled;
  2. swap it in atomically;
  3. drop the old one.

  No statement ever sees a generation with containers not ready because of a swap.
- **Interning growth.** Canonicalisation (§2.1) bounds interning by the declared vocabulary, paths, form tokens and the
  representative values. New sets produced by `Intersect`, `Remove` and L2/L6 are bounded by the same vocabulary. Pools
  remain permanent per graph (D15); the swap is the remedy for drift, and B8 measures it on fresh literals.

### 3.8 Error handling and limits

**Nothing from Eggnog reaches the planner.** The adapter has no catch around fact calls [planner §7]. Every Eggnog call
is wrapped. Caught exceptions include `RuleEvaluationException` (D18), `InvalidOperationException` (guards),
`ObjectDisposedException`, `OperationCanceledException`, `ScopeContradictedException` (D45) and
`FactConflictException` (D32). Architecture A never touches cases outside a `Run`, track H uses plain children, and
every function has a lattice, so the last two are not expected. In `shadow` mode a failure is recorded as `ERR`. In
`union` and `eggnog` modes the answer falls back to Horn. Both are sound, because a derived answer is a superset of
the outright one.

**Breaker per digest.** After *k* failures, a `LIMIT` rate above threshold, or a compile failure, the container answers
from Horn until a retry time. The `fallback` counter records each episode, and the background worker recompiles at the
retry time.

**Limits per `Run`.** Limits are per top-level `Run`, cases included (D42).

| Profile | `IterationLimit` | `RowLimit` | `TimeLimit` | Splits |
|---|---|---|---|---|
| `derive` (split-free) | 64 | 50,000 | 50 ms on the planning thread; 500 ms on the worker | — |
| `split` (R3, kind `splits`) | `I_case × CaseLimit × SplitDepthLimit + I_parent`, with `I_case` and `I_parent` the p99 iteration counts measured on the corpora in Phase 3 | `R_case × CaseLimit + R_parent`, measured likewise | as `derive` | `CaseLimit` 64, `SplitDepthLimit` 4 (RLS:1151-1152) |

`IterationLimit` and `RowLimit` are deterministic with `DegreeOfParallelism` 1 (D46), and stopping on them is sound
(D42): a partial answer is used and counted as `LIMIT`. `TimeLimit` is only a safety net and overshoots by at most one
iteration (§0.2 #16). A run it stops is discarded and remembered only for the rest of its statement (§3.6).

**Statement fact budget.** Limits bound one run; a statement can incur many misses (the query derivation, the restricted
sibling, one child per CASE arm, WHEN condition and `OR` disjunct). Each `CosmosFactStatement` therefore carries a
budget, fixed in its snapshot: at most **32 Eggnog derivations** (deterministic) and, as a safety net, **100 ms** of
Eggnog time. Once either is spent, the statement's remaining misses answer from Horn (or from the outright snapshot
after Horn's removal) and are counted as `fallback{budget}`. Only the time trip depends on timing, and it is counted.

**Cancellation.** Every run receives the reasoning's `CancellationToken` (PS:318; checked before every step,
`ScheduleExecutor.cs:98`), cancelled by `ForceHorn`, a runtime override to `horn` and schema disposal (§3.9).

**Compile failures** keep that container on Horn and never fail the model read. `CosmosSchemaRecognition.ReadFrom` has
no catch today (`A/CosmosSchemaFactory.cs:580`). Horn compile errors keep today's behaviour.

### 3.9 Configuration and switches

**Model operand `factReasoning`.** Read in `CosmosSchemaFactory.create` beside `containers`
(`A/CosmosSchemaFactory.cs:111, 122, 211-239`):

```json
"factReasoning": {
  "modes": { "presence": "union", "type": "union", "form": "shadow", "contradiction": "horn" },
  "splits": "horn",
  "sample": 0.01,
  "limits": { "iterations": 64, "rows": 50000, "timeMs": 50 },
  "statementBudget": { "derivations": 32, "timeMs": 100 },
  "recordValues": false
}
```

- **Kinds:** `presence`, `type`, `geography`, `form`, `value`, `contradiction`.
- **Modes:** `horn`, `shadow`, `union`, `eggnog`. The default is `horn`.
- A kind may be qualified by scope class (`form:outright`, `form:derived`; §2.10). Unqualified, the mode covers both.

**Runtime switches,** in `CosmosFactReasoning`'s static members. They hold no answers and are not caches; they are
volatile references to immutable values, read into each statement's snapshot.
- **`ForceHorn`**, seeded from the `AppContext` switch `Apache.Calcite.Cosmos.Facts.HornOnly`. Setting it routes every
  answer to Horn **and stops Eggnog work**: it cancels the token every run, warm-up and swap receives; drains the
  shadow and reverse-shadow queues; and refuses new graph construction. Clearing it restarts warm-up.
- **`Override`**, an immutable map from kind (optionally qualified) to mode, applied over every schema's operand. It
  gives per-kind rollback at the next statement, without waiting for the data source to be rebuilt (§0.2 #21). An
  override to `horn` for every kind is equivalent to `ForceHorn`.

**`factRecorder` operand.** It names an `ICosmosFactRecorder` type, as `clientFactory` names a factory
(`A/CosmosSchemaFactory.cs:207`).

---

## 4. Shadow-mode migration

### 4.1 What is compared

| Tier | Compared | Where |
|---|---|---|
| Decision | each question a call site asks: (site, scope class, kind, path, claim) → Horn answer and Eggnog answer | tests (synchronous); production (asynchronous, sampled) |
| Surface | both closures normalised to a `ClosureSurface`: per path `present`, `absent`, types, domain, forms, `ne`, bounds and extensions; plus contradiction | tests, replay, production sample |
| Plan | `PlanText` and the Cosmos SQL with each engine deciding | the planner corpus with declared variants (`T/EndToEnd/Corpus/PlannerQueries.cs`, 78 statements; `PlannerCorpusTests.cs`); the gap tests; recorded statements |
| Rows | rows of the plan with Eggnog deciding, against the in-process result | the emulator differential (`T/EndToEnd/CosmosDifferentialTests.cs`; "Equal rows or a defect", `A/DESIGN.md:3690-3695`), over containers with declared schemas and valid documents |

### 4.2 The harness in the adapter's tests

**Mode for the whole run.** `T/Infrastructure/TestInitialization.cs` reads `COSMOS_FACTS_MODE` (`horn`,
`shadow-assert` or `decide-all`), as `CosmosEmulator` reads its environment (`T/Infrastructure/CosmosEmulator.cs:33-34`).
The test helpers that build schemas pass a `CosmosFactReasoning` in that mode to the `CosmosSchema` constructor
(§3.2). Each test gets its own instance; none is static.

**`shadow-assert`.** Decision-tier and surface-tier comparisons run synchronously, every derivation also runs ungated,
and differences go to the per-test reasoning's sink. An assembly-level xUnit v3 `BeforeAfterTestAttribute` fails any
test with an unexplained difference and prints the set.

**`decide-all`.** Every kind runs in `eggnog` mode. Expected plan changes (gap tests, F-fix tests) are recorded in a
ledger of asserted divergences, as `Divergences` is (`A/DESIGN.md:3722-3727`). An expectation that stops holding fails
the run.

**Golden plans in `horn` mode.** A snapshot of the declared-fact and planner suites, taken after Phase 0a, proves that
`horn` behaviour is unchanged by the seam (Phase 0b).

**CI.** One extra run per mode on net8.0. The default `horn` run remains the merge gate until Phase 4.

### 4.3 Production shadow and background work

**One background worker per process.** All background fact work (production shadow, reverse shadow, warm-up, recompiles
and generation swaps, for every schema) runs on one dedicated thread at below-normal priority, not on thread-pool
items. The adapter already blocks pool threads in sync-over-async reads (`A/CosmosSchemaFactory.cs:310, 462, 484`), and
long CPU-bound items on the pool would add to starvation of the Cosmos SDK's I/O continuations. The worker holds a
queue of work items that reference schema-owned state; it holds no answers.

**Token bucket.** The worker draws on a process-wide budget (B9): at most 10% of one core and 20 MB/s of allocation,
measured by `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread`. Warm-up and swaps have priority over shadow and
reverse shadow. Shadow items dropped for want of budget are counted in `shadow.dropped{reason=budget}`.

**Capture on the planning thread.** The planning thread records each derivation's key, scope class and site, and every
question with its Horn answer. Every Nth statement goes to a bounded queue per schema; so does the first occurrence of
a new memo key, **rate-limited** to 10 per second per schema. Records are dropped when the queue is full, and counted.

**Comparison on the worker.** The worker computes the Eggnog snapshot and compares both tiers. Planning pays only the
capture (budget B1). On R1b this runs only if gate G1 passes (§6.3).

**Reverse shadow.** Once a kind runs in `eggnog` mode, Horn is computed on the worker at a lower sampling rate, so a
rollback never starts cold.

### 4.4 Capture and replay

**Fact-level records**, through `ICosmosFactRecorder`:
- **Schema records:** the digest, the container name, the partition-key paths and the schema JSON (model data the host
  already supplies).
- **Derivation records:** the digest, site, scope class, canonical established facts, questions and Horn answers.
- **Redaction:** literals are canonical by construction (§2.1); UUIDs and instants become synthetic values of the same
  form; `recordValues: true` keeps real values for hosts that allow it.

**Fact-level replay.** A test in `…Facts.Saturation.Tests` reads a corpus directory from `COSMOS_FACT_CORPUS`, rebuilds
each container and runs every derivation through both engines at both tiers, with no Calcite, IKVM or service. It also
checks that Horn's recorded answer is reproduced on the canonical key, which makes the redaction self-checking. A
**fresh-literal** mode replaces every recorded literal by a new one of the same order type before replay (B8).

**Statement-level replay.** The host keeps its SQL texts; Sweep is one example, and the gap tests already copy its
`links` schema (`Gaps.cs:205-231`). A replay test plans them through `PlannerHarness`
(`T/EndToEnd/Corpus/PlannerHarness.cs`) over the captured declarations, with each engine deciding, and compares plan
text and SQL.

### 4.5 Classifying differences

**Outcome classes:** `agree`; `E+` (Eggnog proves strictly more); `H+` (Horn proves strictly more); `X`
(incomparable); `ERR` (an exception, fault or compile failure); `LIMIT` (a limit stop with weaker answers); `SKIP` (not
ready, over budget, or dropped).

**Explainers.** Each explainer is code: a predicate with an id. They are tried in order.

| Explainer | Recognises | Mechanism |
|---|---|---|
| `combine-ne` (gap 2), `bounds` (gaps 4–5), `type-narrows-domain`, `null-rules`, `multi-type`, `absent`, `case-lift` (gap 1, R3 or track H), `schema-split` (gap 3, R3) | E+ | **Signature first.** The capability's ruleset (§2.3) must have fired in the **container's compile report or the query's report**. `RunReport.Rules` (PS:266) covers only its own run, so the compile report is kept per digest (§3.4, step 4) and the two are checked together. **Ablation second.** The derivation is re-run with the capability's ruleset left out of **both** the compile schedule and the query schedule, on the graph-per-derivation path of §3.4 (a fresh graph, all facts at its root). Facts are monotone and cannot be removed from a frozen scope (D33, RLS:126), so ablation never reuses the shared container scope; and the fresh graph pre-plans nothing for the schema's graph (§0.2 #17). Ablation runs on the worker. The difference must disappear. |
| `lossy-rows` (F8) | E+ | the surfaces differ only in set inclusion that Horn has no row for |
| `F1`…`F4`, `F10` | H+ | matches the finding's pattern. Only replays of captures made before Phase 0a should produce these. |
| `limit` | H+ | `RunReport.StopReason` is not `Completed` (PS:269) |
| none | any | **Unexplained.** It blocks that kind and goes to the oracle. |

**The document oracle.**
- **Generate** documents by bounded enumeration over the paths that occur: declared constants and branch values; one
  fresh value per type; `null`, absent, and a stringly stored number; integers around each bound; values at, and beyond,
  both cast saturation limits; and, for every nested path, documents where **each ancestor is absent** (which would
  have found the `WalkNegation` defect of §2.5).
- **Keep** the documents valid under the schema, checked by an independent validator (a test-only dependency; open
  question 14). Declarations are trusted, so only valid documents count (`A/DESIGN.md:876`).
- **Check:** (1) every claimed fact holds on every document the established facts keep; (2) a claimed contradiction
  keeps no document; (3) the rewritten predicate P′ keeps exactly the documents P keeps, evaluated through the adapter's
  in-process (CLR cursor) path. This is the auditor RLS risk 2 asks for (RLS:4472-4474).
- **Thresholds:** at least 1,000 generated documents per distinct difference pattern; every distinct contradiction
  difference is checked, not sampled.
- **Outcomes:** an `E+` the oracle confirms becomes a new gap with a test; one it refutes is an engine or translation
  defect, and the kind returns to `horn`.
- Phase 0c runs the oracle against Horn alone first. It would have found F1–F4.

### 4.6 Telemetry

Instruments go on the existing meter and activity source `Apache.Calcite.Cosmos.Adapter`
(`A/Client/CosmosInstrumentation.cs:35-42`), which cost nothing when nobody listens (l.20-22). The monitoring chapter
(`docs/20-monitoring.md`) documents them.

| Instrument | Type | Tags |
|---|---|---|
| `cosmos.facts.derivations` | counter | `cosmos.container`, `facts.engine`, `facts.scope`, `facts.site`, `facts.memo` (statement, container, miss), `facts.gate` (skipped, run) |
| `cosmos.facts.derive.duration` | histogram (µs) | `facts.engine`, `facts.scope` |
| `cosmos.facts.derive.allocated` | histogram (bytes; the `GC.GetAllocatedBytesForCurrentThread` delta) | `facts.engine`, `facts.scope` |
| `cosmos.facts.compare` | counter | `facts.tier`, `facts.kind`, `facts.site`, `facts.outcome`, `facts.explainer` |
| `cosmos.facts.fallback` | counter | `cosmos.container`, `facts.reason` (exception, limit, time, compile, breaker, not-ready, budget, forced) |
| `cosmos.facts.compile.duration` | histogram (ms) | `facts.engine` |
| `cosmos.facts.interned` | observable gauge | `facts.generation` |
| `cosmos.facts.memo.bytes` | observable gauge | `facts.memo` (container), schema |
| `cosmos.facts.cases` | histogram | `facts.scope` (schema, query, host) |
| `cosmos.facts.background` | counter (µs, bytes) | `facts.work` (shadow, reverse, warmup, swap, ablation) |
| `cosmos.facts.shadow.dropped` | counter | `cosmos.container`, `reason` (queue, budget, rate) |
| `cosmos.facts.difference` | activity, started only when sampled | kind, site, outcome, explainer, schema digest, path, claim kind, both answers, key digest; values redacted unless `recordValues` |

### 4.7 Switch-over criteria, per kind

**Why per-kind switching is safe.** Every answer of either engine is sound by itself; a consumer acts on one answer at
a time; and the rewriter's compositions (lower, delete, `FALSE`) are sound for any sound inputs. So any mix of engines
across kinds, and `union` within a kind, is sound.

**A kind moves `shadow` → `union` when all of these hold:**
1. **No unexplained `E+`** for the kind, over: the adapter suite in `shadow-assert`; the planner corpus (declared
   variant); the fact-level differential at 300 seeds per CI run, with 5,000 recorded per change (RLS §8.2); the
   recorded corpus; and production shadow where a host opts in (at least two weeks and 10,000 distinct keys of that
   kind).
2. **Every `E+` class has an oracle pass** at the thresholds of §4.5, and a row-tier pass on the emulator.
3. **`ERR` is zero in tests**, and at most 0.01% of gated calls in production, with the breaker verified.
4. **Budgets B2–B12 hold** for the kind's sites.
5. **A rollback drill** has been run for the kind (§7, item 11).

**`union` → `eggnog` additionally needs:** no `H+` other than `limit`; a `limit` rate under the threshold; a `budget`
fallback rate under the threshold; one release in `union` without a rollback.

**Order.** Blast radius and user value agree:
1. `presence`, `type`, `geography` (outright first). These exercise the machinery.
2. `form`: outright, then derived. Gap 2 lands here, as do gap 1 per kind (track D, as child sessions) and track H.
3. `value`: tautology deletion and routing, where a wrong answer misses rows.
4. `contradiction`, last, because a wrong answer drops every row. Gaps 4 and 5 land here.
5. `splits` (R3): gap 1 `IN` and the general gap 3.

### 4.8 Rollback

| Level | Mechanism | Latency |
|---|---|---|
| Per kind, process-wide, at run time | `CosmosFactReasoning.Override` (§3.9) | next statement |
| Per kind, per schema | the `factReasoning.modes` operand | the next data source; a pooled data source is rebuilt only after `ConnectionIdleLifetime` with no open connection (§0.2 #21), so this is the configured default, not an incident lever |
| Whole process, at run time | `ForceHorn`, which also stops all Eggnog work (§3.9) | next statement; statements are planned on every execution (`docs/19-performance.md:99-101`) |
| Per container, automatic | the breaker (§3.8), for errors and limits | immediate |
| Release | Horn stays in the adapter: in reverse shadow for at least one release after the last switch, and as the fallback engine until B10 allows its removal | — |

The breaker reacts to errors and limits only. Wrong answers are caught by the shadow, the oracle and reverse shadow, and
are rolled back through `Override`.

---

## 5. Phases

### 5.1 Phase 0a in detail: deliberate changes to pinned tests

Every test below fails after Phase 0a by design. Each is rewritten in the PR that causes it, and each keeps a twin that
pins the old premise where it still matters.

| Test (ref) | Cause | New assertion | Twin |
|---|---|---|---|
| `AContradictedPredicateIsReducedToTheConstant` (`T/EndToEnd/CosmosDeclaredFactPlanningTests.cs:1336-1340` @ origin/main) | logical empty-`Values` rule | the plan is an empty `Values` with no Cosmos node; no `Query(FindCosmos(best))` | a contradiction inside a `CASE` arm still yields `condition=[false]` on the `CosmosFilter` |
| `AComparisonTheDeclarationDoesNotGuaranteeIsUntouched` (same file, l.1462) | same | empty `Values` | — |
| `Gap3Control_AValueOutsideADeclaredDomainIsFalse` (`Gaps.cs:125` @ `a83fd2a`, passing today) | same | empty `Values` | — |
| `Gap3_…` (`Gaps.cs:149`), `Gap4_…` (l.183), `Gap5_…` (l.202) | ported with the new expected shape | empty `Values` under the phase that closes each (§2.12); today's plan as a recorded divergence | — |
| `ADiscriminatedUnionKeepsItsPerBranchFacts` (`T/Facts/JsonSchemaFactsTests.cs:702-706` @ origin/main) | F4 carrier 1 (`data.type` is required only under `Present(data)`, l.656-658) | the established set adds `Present(data)` and `Present(data.type)` | a bare `EqualTo` no longer fires the branch |
| `AnElseIsReachableWhenTheConditionIsOneEquality` (l.865-866) | F4 carrier 2 (no `required`) | established set adds `Present(kind)` | a bare `EqualTo` no longer reaches `else` |
| `AnOpenApiDiscriminatorSelectsABranchThroughItsMapping` (l.916-918), `AnOpenApiDiscriminatorWithNoMappingUsesTheSchemaName` (l.939-941) | F4 carrier 1 (`kind` never required) | established set adds `Present(kind)` | a bare `EqualTo` yields no representation |
| Tests found by the 0a run that pin an `IS_DEFINED` guard beside a sibling comparison, or a `dependentRequired` trigger by equality | `Present` beside comparisons (§2.8) | the guard is gone, or the dependent fires | new plan tests pin both changes |

The helper `Equals` in `JsonSchemaFactsTests` (l.131) builds a bare `EqualTo`. It stays, and a helper
`EqualsPresent` is added for the rewritten tests.

### 5.2 Phase table

| Phase | Eggnog needed | Entry | Work | Exit |
|---|---|---|---|---|
| **0a. Correctness and gap tests** | none | now | (1) Port `Gaps.cs` to origin/main: crefs `CosmosFact` → `JsonFact` (l.20) and `CosmosFactSet.Knows` → `JsonFactSet.Knows` (l.102); class `partial`; every helper exists (`CosmosDeclaredFactPlanningTests.cs:572-574`). Gap tests assert today's plans as recorded divergences, with the expected plan per phase. New controls: gap 5 inside the domain; sweep `data.type <> 'map'` does not lower. (2) Plan-level tests reproducing F1–F4, then the fixes (§2.13), including the four F4 carriers (§2.5) and a nested-`not` test with an absent parent; F5; F7 tests. F3 and the extractor's `Present` land together, with the two Horn plan changes of §2.8 pinned. (3) The gap 3 `Entails` row. (4) The logical-level contradiction rule and the service-leg check of `CosmosConnectionFunctionTests` (`catalog`, l.133-200). (5) The saturation-inequality test, and F10 if it fails (§2.8). (6) F9. (7) The deliberate test changes of §5.1. | F-tests and gap 3 green; every existing suite green **except the tests of §5.1**, each rewritten with its twin; gap 3 plans an empty `Values`; release notes describe every plan change: F1, F4, `Present` beside comparisons (the `IS_DEFINED` deletion and dependent triggers), the logical rule, and F10 if found |
| **0b. Seam and determinism** | none | 0a | Move `A/Facts/` to the Facts project; seam and Horn engine (§3.2); `JsonFactDeclaration` on metadata and `CosmosFactReasoning` on the schema, with the constructor parameter; the `Rewrite`/`RewriteConditions` signature change; the call-site checklist; `ClaimsFor` consumers onto questions; `HasDeclarations` (F6); the host-side closure; `CosmosFactStatement` (mode snapshot, budget, statement memo); `CosmosNumericWindow` extracted, with no behaviour change; Horn-only telemetry; a BDN baseline | plans unchanged against golden plans taken after 0a; baseline recorded (§6); statement memo hit rate measured |
| **0c. Verification base** | none | 0b | Declared-schema variants of `PlannerSchema` and a declared corpus for `CosmosDifferentialTests` (absent paths and absent parents, nulls, mixed types, an absent discriminator, integral numbers under `number`, stringly stored numbers, values beyond the saturation limits); the document oracle against Horn; the capture recorder (Horn answers) | the oracle is green on Horn, and reverting any F-fix makes it fail |
| **D. Derivation per disjunct** (track) | none (Horn); R2 (Eggnog child) | 0b | `Apply` on `OR` derives per disjunct with `IJsonFactSession.Child`, as `TryFlattenCase` does for a CASE arm; each counts against the statement budget | gap 1 per kind green under Horn, with a row-tier pass |
| **1. Theory on R1b, offline** | R1b (on main) | 0b; a tagged Eggnog prerelease | `Facts.Saturation` (theory with one ruleset per capability, translation, encodings, canonicalisation, snapshots; graph per derivation, §3.4); `JsonBounds`, the recogniser and the window reading; test modes, explainers and the ledger; the fact-level differential; adapter conformance ports (§7, item 3); a BDN benchmark of R1b in the production encoding | zero unexplained differences on the suite and at 300 seeds (5,000 recorded); gaps 2, 4 and 5 green under `decide-all`; gate G1 decided (§6.3) |
| **1b. Sampled production shadow** (optional) | R1b | G1 passes | The adapter references Facts.Saturation; the background worker and bucket (§4.3); asynchronous shadow at a low rate; capture at a pilot host | a capture corpus and production difference rates exist; B9 holds |
| **H. Host-driven cases for gap 1 `IN`** (optional track) | R1b (case graphs) or R2a (plain children) | taken only if R3 is not expected within the adapter's horizon; Phase 1 exit | the case driver (§3.6), capped at 8 values, `form` in `union` only, behind the gate | gap 1 `IN` (adapter and Sweep) green in `union`; §4.7 criteria met; B3 for track H holds |
| **2. Production on R2** | R2a (§8.3, request 1), or R2 | R2a or R2 tagged; Phase 1 exit; the idle-local-rule measurement of §8.3 request 2 | `SaturationHost` per schema object; frozen root with global rules; container scopes with local rules, shared by digest, warmed in order; sessions and children; delta snapshots; statement memo and byte-bounded container memo; gate; statement budget; generation swap with full warm-up; breaker; runtime switches; asynchronous and reverse shadow; telemetry; switches in the order of §4.7 | gaps 2, 4 and 5 green in `union`, then `eggnog`; each switched kind met its criteria; B2–B12 hold; 32 concurrent plans equal serial plans, run **with no `TimeLimit`** (and a separate run asserting no stop on `TimeLimit` at the default) |
| **3. Splits** | R3 (M10.4 plus E8; RLS:3660) | R3 tagged; `form:derived` in `union` or `eggnog` | the compiler emits `discriminator`, hidden discriminators and `governs` (§2.6); schema split at compile; query split guarded `[present, discriminator, mentioned]` with host-side `governs` (§2.8); the R3 gate condition (§3.6); the `split` limit profile, sized from measured iteration counts (§3.8); a split oracle per encoding pattern; the `splits` switch; track H retired | gap 1 (`IN`, Sweep) and the horn-branch assertion green; no `CaseLimit` hit and **no `IterationLimit` or `RowLimit` stop caused by cases** on the corpora; split criteria met; `query.Children` empty after every `Run` (RLS:3680) |
| **4. Retire Horn as a decider** | — | every kind in `eggnog` for one release with no rollback | Eggnog becomes the default; Horn stays in reverse shadow for one more release, then reverse shadow is removed. **Horn's closure stays as the fallback engine.** It is removed (`JsonFactTheory.Close`, `Excludes` and their tests) only when B10, measured for a release with the fallback switched to the outright snapshot alone, holds. `Entails` stays while `Meet` uses it (`JsonSchemaFacts.cs:488-517`). `JsonSchemaFacts` and `JsonFactRule` stay as the compiler and its intermediate form. | Eggnog default merged; Horn's removal is a separate PR gated on B10 |
| **5. Predicate extraction** (optional) | R2 with E5, licences and mirrors; R3 for splits; a "no result" primitive outcome (§8.3, request 10) | a decision to take architecture B (open question 9) | §2.11; a predicate-tier shadow (`AreEqual` in the query scope, plus the P ≡ P′ oracle); every `In(Schema)` read defined up front, with a generation swap on any theory change (risk 9, RLS:4492-4494) | the same criteria, per rewrite family |

**Ordering in Eggnog** (as planned when this was written): M10.3, then M10.4, then R2 (after M10.3) and R3 (after
M10.4). All four are now released, so every phase's Eggnog entry is met; Phases 0a–0c, D and 1 never depended on them.

---

## 6. Performance budgets and how they are measured

### 6.1 What is known

**What the adapter states.** Planning is budgeted in registrations: under 2,000 for the five-view hierarchy
(`T/Planning/CosmosPlanningTests.cs:231-254`). A query over such a hierarchy "still takes seconds to plan" and is
planned on every execution (`docs/19-performance.md:99-101`). Horn's cost per derivation has never been measured.

**What the library states.** A bare child costs 7.4–9 KB plus 2.7–3.0 KB per touched table; lookups cost one probe per
layer (CCS §14.2). Child time is not measured (CCS:2826). On R2, every query child runs every local rule of its
container's scheduled rulesets on every iteration (§0.2 #18); an idle local rule costs at least one binary search per
layer per body table (D5). Nothing measures that cost at 100 or 1,000 local rules.

**R1b measured in this study.**
- **Method.** A `git archive` of eggnog `b041e70`, with a throwaway console harness compiling
  `src/Eggnog.Tests/Cosmos/{Pred.cs, Literal/Ctx.cs, Literal/PrototypeTheory.cs}`. net8.0 Release, workstation GC,
  serial graph, 20 warm-ups, then 300–1,000 timed runs, on a machine with 8 logical processors and background load.
- **How to read the figures.** The prototype theory includes rewrites and extraction, so the theory figures are an
  upper bound for architecture A. The figures are indicative; Phase 1 repeats them with the project's method.

| Operation | Warm median | Warm p90 | Allocated | First call in a process |
|---|---|---|---|---|
| `new EGraph()` + theory + `Ground()` | 1.3–3.0 ms | 3.1–7.9 ms | 815 KB | 190–530 ms cold JIT; about 108 ms with ReadyToRun on a warm disk (theory only) |
| Scenario 02 end to end (theory, facts, 2 branch rules, query, split, run, extract) | 6.0–9.8 ms | 12.6–18.4 ms | 2.9 MB | 80–290 ms after the theory's first call, without ReadyToRun |
| Sweep `links` per-kind `OR` (theory, container, 3 branch rules, query, split, run, extract) | 4.6–6.9 ms | 7.5–14.2 ms | 3.2 MB | 8–22 ms after the above |

**What follows.** On R1b, most of a derivation's cost is defining the theory and the container's rules; that cost moves
to once per graph and once per container at R2. On R1b it is cut by merging rules with identical bodies and by slicing
container rules by path relevance (from the established paths, adding the body paths of every rule whose head path is
relevant, to a fixpoint). **Slicing does not apply on R2**, where local rules are fixed at freeze; the R2 cost of idle
local rules is what §8.3 request 2 measures.

### 6.2 Budgets

Absolute targets are to be confirmed against the Phase 0b baseline. They are stated against Horn where possible.

| # | Budget | Target | Measured by |
|---|---|---|---|
| B1 | Shadow overhead on the planning thread | ≤ 2% of planning time at the production sampling rate; capture ≤ 5 µs per question | the planner corpus and `CosmosPlanningTests`, timed with shadow on and off |
| B2 | Statement planning time when deciding | ≤ 5% at the median and ≤ 10% at p95 over Horn, on the declared corpus and the five-view hierarchy; registrations unchanged unless an answer changed | the same runs, with `facts.engine = eggnog` |
| B3 | One derivation on a memo miss | Stated per container rule count (10, 100, 1,000) and established size (1, 4, 16). R2 at ≤ 100 rules: p50 ≤ 250 µs, p99 ≤ 2 ms; at 1,000 rules, set from the §8.3 request 2 measurement before Phase 2's entry; with splits, p99 ≤ 5 ms at ≤ 100 rules. Track H on R1b, at most 8 values: p50 ≤ 10 ms, p95 ≤ 25 ms, ≤ 4 MB transient. Each measured for the representative container and for a later-frozen container (risk 4). | BDN over `ThreeKinds`, `SweepLinks` and synthetic unions of 5, 50 and 500 branches × 20 paths |
| B4 | Memory | Per R2 derivation ≤ 128 KB allocated, and **0 retained after `Dispose` beyond values newly interned (D15)**, measured on a second pass over the same keys. From CCS §14.2, about 8–12 touched tables predicts roughly 35–45 KB plus rows (§0.2 #8). Per container scope ≤ 256 KB typical and ≤ 2 MB at 1,000 rules. Per generation (graph plus theory) ≤ 1 MB. | `derive.allocated`; the BDN memory diagnoser; heap delta after compile with forced collection |
| B5 | Cold start, without ReadyToRun | Never on `create`'s path. (a) From schema creation to every declared container ready, warm-up derivation included: ≤ 1 s for 10 containers, measured without ReadyToRun. (b) The first derivation after ready: within B3's p99. ReadyToRun is an option for hosts, never a requirement. | `compile.duration`; a fresh-process CI job measuring (a) and (b) |
| B6 | Memo effectiveness | ≥ 70% statement-memo hits on the corpus (both engines, Phase 0b); ≥ 50% container-memo hits on replay, and on fresh-literal replay | `derivations{facts.memo}` |
| B7 | Gate and memo hit | gate ≤ 50 µs at p95; memo hit ≤ 5 µs, key and canonicalisation included | BDN |
| B8 | Interning | ≤ 1,000 new interned values per 1,000 distinct statements on **fresh-literal replay** after the first pass, and zero on a same-literal second pass; swap at the threshold | `InternedCount` before and after |
| B9 | Background work | the worker's bucket: ≤ 10% of one core and ≤ 20 MB/s allocation per process, all schemas together | `cosmos.facts.background`; process CPU with shadow on and off |
| B10 | Fallback fraction | statements answered with any fallback (`not-ready`, `breaker`, `budget`, `time`, cached failure) ≤ 0.5% of deciding statements, measured per release; gates Horn's removal (Phase 4) | `cosmos.facts.fallback` over `derivations` |
| B11 | Statement fact budget | ≤ 32 derivations and ≤ 100 ms Eggnog time per statement; budget fallbacks ≤ 0.1% of statements on the corpora | `fallback{budget}` |
| B12 | Retained memo bytes | ≤ 1 MB per digest and ≤ 8 MB per schema, enforced by the byte bound; on replay, the hit rate of B6 at that bound | `memo.bytes` |

**Method.** BenchmarkDotNet as a library in a console app with the in-process toolchain only: server GC, the user's
power plan, an idle machine. The adapter's benchmarks are a new `src/Apache.Calcite.Cosmos.Adapter.Benchmarks`. The
library side is a new `src/Eggnog.Benchmarks` (§9.2).

### 6.3 Gate G1: production shadow on R1b

**Expected cost.** An architecture-A derivation on R1b costs about the theory definition (1.3–3.0 ms; 815 KB) plus the
container's sliced rules and a run, below scenario 02's 6–10 ms, which includes a split and extraction.

**G1 passes when** the Phase 1 benchmark in the production encoding shows p95 ≤ 20 ms and ≤ 4 MB per sampled
derivation, and B1 and B9 hold at the intended sampling rate (at 20 ms per derivation, B9's 10% of a core admits about
5 sampled derivations per second per process).

**Synchronous decisions on R1b** are allowed only for track H, behind the gate, both memos and the statement budget.

---

## 7. Testing

1. **The adapter's existing tests, in three modes** (§4.2): `T/Facts/*` (`JsonFactTheoryTests` (12),
   `JsonSchemaFactsTests` (41), `JsonScalarTests` (9)); `T/Metadata/CosmosFactRewriterTests.cs` (29);
   `T/EndToEnd/CosmosDeclaredFactPlanningTests.cs` (52); `CosmosPlannerTests` l.1235-1300 (text ambiguity); the sort,
   self-join and lookup-join rule tests; `CosmosPlanningTests`; the planner corpus. `horn` is checked against golden
   plans; `decide-all` passes apart from ledgered expectations.
2. **Gap tests.** Ported in 0a with their controls and the shapes of §5.1. A gap that closes moves into the ordinary
   suite. The mapping to Eggnog scenarios is RLS:3438-3439: Gap1 → 02 and 07, Gap2 → 03, Gap3 → 04, Gap4 → 05,
   Gap5 → 08.
3. **Eggnog conformance.** Eggnog's literal suite (R1, green) and scoped suite (R2/R3, RLS §8.1.2) remain the library's
   gate. `…Facts.Saturation.Tests` ports scenarios 01–08 to the production encoding (tagged scalars, the type set,
   decimal and open bounds, presence guards), adds what the prototype leaves out [gaps §6] (nulls and `OrNull`,
   `AdmitNull`, geography by probing, `type: number` with integral values, multi-type, the cast over a string,
   saturation), and adds a **compile-path suite**: scenarios 01–07 rebuilt from JSON Schemas through `JsonSchemaFacts`
   and the translation. Gap 5 stays out of Eggnog's literal suite (D35, D37; RLS:128, 130).
4. **Differential between the reasonings.** Fact level: generated schemas over the keywords read, crossed with generated
   established sets (`=`, `<>`, `IN`, `IS NOT NULL`, ranges, casts, CASE arms), compared at the surface tier; Eggnog ⊇
   Horn outside tagged `F*` patterns; 300 seeds in CI, raised by an environment variable as `EGGNOG_SEEDS` is; 5,000
   recorded per change; a failing seed prints its schema and facts. Oracle (§4.5) before any switch. Plan: the corpus
   with each engine deciding. Row: emulator corpora with declared schemas.
5. **Encodings and canonicalisation.** Scalar encoding is injective against `JsonScalar.Equals`. Path encoding is
   injective for names containing `/`, `~` and `.`. The form registry round-trips. The bound lattices pass
   `ValueLattice<T>.Verify`. **Canonicalisation is exact:** renaming literals by any order- and type-preserving
   injection that fixes declared breakpoints leaves every answer unchanged (a property test against un-canonicalised
   runs).
6. **Gate, host-side closure and snapshot exactness.** Gated against ungated in every test run; the host-side closure
   against Eggnog for undeclared containers; snapshots against `Scope.Rows` in test builds.
7. **Neutral and negative controls:** `name = 'x'` on an undeclared path of a declared container makes no Eggnog call;
   `CAST(ref AS UUID) = …` establishes nothing; sweep `data.type <> 'map'` does not lower; gap 5 inside the domain is
   left alone; a stored `"600"` under `CAST(n AS INTEGER) > 500` is not refuted; a nested `not` under an absent parent
   states nothing outright; the saturation-inequality shapes of §2.8.
8. **Concurrency.** 32 statements planned concurrently against one schema in `decide-all`, with no `TimeLimit`, against
   serial planning; a second run at the default limits asserts that no run stopped on `TimeLimit`. This mirrors
   `ConcurrencyTests` (RLS:3524).
9. **Fault injection:** a throwing `Compute`; `RowLimit` 1; a forced `TimeLimit` trip, which must stay in the statement
   memo and be retried by the next statement; a generation swapped mid-plan, after which every answer within the
   statement is unchanged; a compile that fails once and succeeds on retry. Each yields fallback answers, unchanged
   plans in `shadow`, metrics, and an open breaker after *k* failures.
10. **Lifetime and memory** (R2): 100,000 sessions with retained memory flat beyond newly interned values;
    `InternedCount` flat under fresh-literal replay within B8; a schema disposed while sessions run stops its background
    work and is collected after them.
11. **Rollback drills:** flipping `ForceHorn`, a per-kind `Override` and the operand mid-run. Every statement still plans
    and implements, and plans equal Horn's afterwards. After `ForceHorn` is set, no Eggnog `Run` starts (a run counter)
    and allocation attributed to Eggnog work on the worker drops to zero within one drain.
12. **Off means off:** with every kind `horn`, no `Eggnog` assembly loads.

---

## 8. Risks, open questions and library requests

### 8.1 Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | Common-mode defects: both engines share the extractor, the path mapping (F7) and the compiler, so the shadow cannot see them | the oracle (with absent ancestors) and the row tier check behaviour, not agreement; F7 tests in 0a |
| R2 | A wrong lowering drops rows (high severity) | the gate; `union` before `eggnog`; the P ≡ P′ oracle; per-kind runtime rollback |
| R3 | Split coverage cannot be checked (RLS risk 1, l.4469-4471) | an oracle pass per hidden-discriminator encoding before `splits` |
| R4 | Bounds semantics: casts over strings and saturation | crossing excludes numbers only (L7); one shared `CosmosNumericWindow`; the saturation-inequality test (F10); controls (§7, item 7) |
| R5 | Interning grows (D15) | canonicalisation; generation swap; gauge; B8 on fresh literals |
| R6 | Per-child latency and idle local-rule cost are unmeasured | B3 per rule count; §8.3 request 2 before Phase 2 |
| R7 | A mode flip mid-statement fails a plan at implementation | the mode snapshot per statement, with a test |
| R8 | Recorded literals leak | canonicalisation and redaction by default; `recordValues` is opt-in |
| R9 | An engine defect makes Eggnog unsound | Eggnog's differential suites (RLS §8.2); the shadow; the oracle; the breaker |
| R10 | Phase 0a moves existing plans (F1, F4, `Present` beside comparisons, the logical rule) | separate PRs, the deliberate changes of §5.1, plan-level tests and release notes |
| R11 | Plan tests depend on Calcite and IKVM versions | ledger entries name the version; re-baselined on upgrades |
| R12 | Prerelease churn (R2 and R3 only add members, D28) | pinned versions; Eggnog referenced only from 1b/2 |
| R13 | Split demand misses facts (RLS risk 11) | host-side `governs`; the gate reads the same demand; replay measures misses |
| R14 | Global rules are planned on the first frozen container's statistics (RLS risk 4; E10, §0.2 #17) | no near-empty scope freezes; a representative container freezes first; B3 measured on later containers; §8.3 request 7; fallback to one graph per container |
| R15 | Phase 0 delays user-visible wins | 0a ships the fixes, gap 3 and the logical rule first; track D needs only 0b |
| R16 | R1b cost variance (p90 at 2–3× the median) | only sampled shadow and the optional track H run on R1b; deterministic limits; memos; statement budget |
| R17 | Timing-dependent outcomes make plans differ between runs | `TimeLimit` outcomes stay in the statement memo; warm-up JIT-compiles the child path; concurrency test without `TimeLimit` |
| R18 | Fact work per statement is unbounded by per-run limits | the statement budget (B11); the track H cap |
| R19 | Background work competes with I/O continuations | one low-priority worker per process; the bucket (B9) |
| R20 | Fallbacks silently lose pushdowns that Horn gives today | Horn kept as the fallback engine; swaps warm before switching; B10 gates Horn's removal |

### 8.2 Open questions

1. **F2 at plan level.** Does Calcite simplify or retype `JSON_VALUE(c."DOC",'$._ts') = '1700000000'` before
   `CosmosFilterRule`, or does it plan `condition=[false]`? The first test of 0a settles this.
2. **F1 through queries.** Which shapes deliver an integer literal on a resolved path (VARIANT-promoted keys,
   `JSON_VALUE … RETURNING`)? The schema-only form is fixed regardless.
3. **Is `Integer` a subtype of `Number`?** The plan assumes yes (JSON Schema's reading). Needs deciding before 0a.
4. **Should `absent(p)` be read as `NOT IS_DEFINED` knowledge** by a consumer? Today it serves contradiction only.
5. **Unsatisfiable declarations.** Proposed: a diagnostic and a metric at load; the model read does not fail.
6. **Rendered-text premises.** Should they become their own claim kind (a pre-image set such as `{"30", 30}`)?
7. **Gap-test port.** Who ports the gap tests? The prototype README's statement that they are on the branch
   (`prototypes/cosmos-facts/README.md:86-89` @ `b041e70`) needs correcting.
8. **Subtree facts.** Should sort, projection, aggregate and lookup probes use facts proved by filters beneath them
   (`A/DESIGN.md:868-871`)? It is one more session per subtree, against the statement budget.
9. **Architecture B.** Is it wanted, and is the `CHECK` constraint its driver?
10. **Production shadow on R1b** (G1).
11. **Should main's `Meet` take the horn branch's subsumption** (`o.Body ⊆ rule.Body`) instead of `Same`
    (`JsonSchemaFacts.cs:412-422`)? It would recover a fact stated outright in one branch and conditionally in another,
    without R3.
12. **Canonicalisation bound.** What largest number of literals per path per derivation is canonicalised before the
    excess is dropped (lossy and sound)?
13. **Recording policy:** opt-in, redaction rules, and where records live.
14. **Which independent JSON Schema validator** the oracle uses (Jackson and networknt were removed [facts §0]).
15. **Long-term home of the theory:** the adapter, or a shared JSON-claims package over Eggnog. This plan keeps it in
    the adapter.
16. **Statement budget values.** 32 derivations and 100 ms are proposals; the Phase 0b baseline of misses per statement
    on the five-view hierarchy sets them.

### 8.3 Library requests (features the Eggnog specs do not plan)

1. **A tagged R2a prerelease.** Architecture A uses scopes, `Freeze`, deferred `Dispose`, local rules, concurrent
   children, thread-domain guards, E4, E7, E9 and E10, and no tags, mirrors, licences or E5. R2 as specified depends on
   E5 and is done only when the whole scoped suite is green (§0.2 #22; RLS:3646-3657), so an internal ordering alone
   would not let the adapter start earlier. The request is a milestone split: **R2a** (the scope tree, `Freeze`,
   deferred `Dispose`, local rules, concurrent children, E4/E7/E9/E10, with `LocalRuleTests`, `ConcurrencyTests`,
   `DisposeTests` and a consumer-profile test), tagged as a prerelease; **R2b** (E5, tags, mirrors, licences, context
   extraction and the rest of the scoped suite). Phase 2's entry is R2a.
2. **Latency figures for transient children, including idle local rules.** R2's done-when has no time figure, and
   CCS:2826 measures bytes only. The adapter needs create, write, run, read and dispose in the tens to hundreds of µs
   (B3), and a measurement of per-iteration overhead per idle local rule on a query child under a frozen scope with
   100 and 1,000 local rules (§0.2 #18). If that overhead is significant, the follow-up request is **demand-indexed
   local rules**: skip, in an iteration, every local rule none of whose body tables has rows newer than the parent's
   watermark.
3. **Values that die with a scope**, or interning scoped to a context. D15 makes pools permanent. Canonicalisation and
   generation swaps are the workaround.
4. **A tested user lattice over a registered struct with a case join** (`NumericBound`), checked with
   `ValueLattice<T>.Verify`. Feasible today (PS:382, 407) but untested.
5. **Provenance for triage and EXPLAIN** (D41). The workaround is `RunReport.Rules` signatures plus ablation on a fresh
   graph.
6. **Hidden-discriminator synthesis.** The spec encodes `$anyOf/0` by hand and does not say how a compiler synthesises
   it [gaps §6]. The adapter's compiler owns this; a worked note in RLS §10 would help.
7. **Control over what a non-root freeze pre-plans.** E10's rule-layer use is fixed: a non-root freeze plans every
   global rule without a plan (RLS:2929-2933), and per-context replanning is deferred (CCS §19; RLS:4479). Either a
   `ScopeOptions` or `Freeze` overload that pre-plans only the scope's own local rules (or nothing), or per-context
   replanning of global rules, would remove the first-frozen-container dependence (R14).
8. **A per-case iteration budget** (optional). D42 counts every case's iterations against one `IterationLimit`; the
   `split` profile of §3.8 works around it.
9. **Documentation:**
   - `CosmosTheory`'s numeric bound rules (RLS:3979-3990) assert `Types = {number}`. They model a raw comparison and must
     never model a numeric `CAST`, which also matches stored strings (§0.2 #2).
   - Its "a domain settles the type" rule is string-only (RLS:3995-4000) by the test theory's scope.
   - Ordinal `Max`/`Min` for strings: the built-ins use `CompareTo` (`ValueLattice.cs:55, 63`) while sets are ordinal.
   - The R1b status lines (§0.2 #13).
10. **A "no result" outcome for primitives** (RLS:4720), needed only by architecture B, for the partial rendering
    primitives of §2.11.

Not needed by architecture A: parameterised queries (A15, RLS:4607), because reads use known keys (§2.9); own-layer
reads (D16).

---

## 9. Changes by repository

### 9.1 The adapter (pull requests to `main`)

**Phase 0a**

| File | Change |
|---|---|
| `T/EndToEnd/CosmosDeclaredFactPlanningTests.Gaps.cs` (from the worktree), `CosmosDeclaredFactPlanningTests.cs` (`partial`; l.1336-1340 and l.1462 rewritten) | port; renamed crefs; recorded divergences; new controls; the shapes of §5.1 |
| `A/Facts/JsonType.cs` | `Number` documented as any JSON number (l.20-22) |
| `A/Facts/JsonFact.cs` | `Integer ⊂ Number` in `Entails`/`Exclusive` (l.50-85, 127-143); the `(OneOf, OneOf)` subset row |
| `A/Facts/JsonFactSet.cs` | `IsContradictory` pairs claims only where `Present` is known (F3) |
| `A/Facts/JsonSchemaFacts.cs` | the four F4 carriers (discriminator, `else`, `AdmitNull`, `WalkNegation`) |
| `A/Metadata/CosmosFactExtractor.cs` | `Present` beside resolved comparisons; the F2 test in `TrySide` (l.169-206) |
| `A/Metadata/CosmosPartitionKeyExtractor.cs` | `IsRoot` (F5, l.600) |
| `A/Rel/Convert/CosmosFilterSplitRule.cs` | F10, only if the saturation-inequality test fails |
| `A/Rel/Convert/CosmosContradictionRule.cs` (new), `A/CosmosRules.cs` | logical filter → empty `Values` |
| `T/Facts/JsonFactSetTests.cs` (new), `T/Facts/JsonSchemaFactsTests.cs` (the rewrites and twins of §5.1), `T/EndToEnd/CosmosPlannerTests.cs`, an F7 test file | the first unit tests of `Excludes`/`IsContradictory`; F1–F5, F7 and F10 tests; the joined-domain meet; the nested-`not` test |
| `A/DESIGN.md` (l.601-901, the keyword table at l.682), `TODO.md` §6, `docs/14-json-schema.md`, `A/Metadata/CosmosFactRewriter.cs` (l.677-731) | F1/F4 semantics; F9 drift |

**Phase 0b**

| File | Change |
|---|---|
| `src/Apache.Calcite.Cosmos.Facts/` (new project; `A/Facts/*` moved), `src/Apache.Calcite.Cosmos.Facts.Tests/` (`T/Facts/*` moved) | `TODO.md:1651-1664` |
| `…Facts/IJsonFactAnswers.cs`, `IJsonContainerFacts.cs`, `IJsonFactSession.cs`, `IJsonFactEngine.cs`, `JsonTypeSet.cs`, `JsonFactDeclaration.cs`, `JsonFactDemand.cs`, `HornFactEngine.cs`, `HornContainerFacts.cs`, `HornFactSession.cs`, `HostClosure.cs` (new) | the seam, the Horn engine, the host-side closure |
| `A/Metadata/CosmosFactReasoning.cs`, `CosmosFactStatement.cs`, `CosmosFactSite.cs`, `CosmosFactKind.cs`, `CosmosFactMode.cs`, `CosmosFactBudget.cs` (new) | schema-scoped reasoning, statement context, modes, budget, `ForceHorn`, `Override` |
| `A/Metadata/CosmosContainerMetadata.cs` | `JsonFactDeclaration` built in `WithFacts` and shared by `Copy()`; `WithReasoning`; `HasDeclarations`; stale remarks (l.568-571) |
| `A/CosmosSchema.cs`, `A/CosmosAccountSchema.cs`, `A/CosmosTable.cs`, `A/CosmosSchemaFactory.cs` | the optional `CosmosFactReasoning` constructor parameter; binding per table; `create` builds it before `ReadContainers` |
| `A/Metadata/CosmosFactRewriter.cs` (`Rewrite`, `RewriteConditions` take a `CosmosFactStatement`), `CosmosPartitionKeyExtractor.cs`; `A/Rel/Convert/CosmosFilterRule.cs`, `CosmosFilterSplitRule.cs`, `CosmosProjectRule.cs`, `CosmosProjectSplitRule.cs`, `CosmosSortRule.cs`, `CosmosSelfJoinRule.cs`, `CosmosLookupJoinRule.cs`; `A/Rel/CosmosProject.cs`, `CosmosSort.cs`, `CosmosAggregate.cs`, `CosmosFilter.cs`; `A/CosmosImplementor.cs`; `A/Sql/CosmosRexTranslator.cs`; `A/CosmosFactSetExtensions.cs` | the call-site checklist (§3.2); `IJsonFactAnswers` parameters; F6 remarks (`CosmosFactRewriter.cs:84-89`) |
| `A/Metadata/CosmosNumericWindow.cs` (new), `CosmosFilterSplitRule.cs` | the window extracted from `TryBoundNumericCast` (l.662-720) |
| `A/Client/CosmosInstrumentation.cs` | the instruments of §4.6 |
| `src/Apache.Calcite.Cosmos.Adapter.Benchmarks/` (new) | BDN baseline |
| `Apache.Calcite.Cosmos.slnx` | the new projects |

**Phases 0c and D**

| File | Change |
|---|---|
| `T/EndToEnd/Corpus/PlannerSchema.cs`, `T/EndToEnd/CosmosDifferentialTests.cs` | declared variants and valid documents |
| `…Facts.Tests/Oracle/*` (new) | document enumeration (absent ancestors, saturation limits), validation, checks |
| `A/Metadata/ICosmosFactRecorder.cs` (new), `A/CosmosSchemaFactory.cs` | the `factRecorder` operand |
| `A/Metadata/CosmosFactRewriter.cs` | derivation per disjunct in `Apply` (l.245-265) |

**Phases 1, 1b, H and 2**

| File | Change |
|---|---|
| `src/Apache.Calcite.Cosmos.Facts.Saturation/` (new): `SaturationFactEngine.cs`, `SaturationHost.cs`, `SaturationContainerFacts.cs`, `SaturationSession.cs`, `CompileState.cs`, `JsonClaimTheory.cs`, `JsonRuleTranslator.cs`, `SchemaScopeCompiler.cs`, `JsonFactSnapshot.cs`, `JsonFactSnapshotDelta.cs`, `JsonScalarEncoding.cs`, `JsonPathEncoding.cs`, `LiteralCanonicaliser.cs`, `NumericBound.cs`, `NumericBoundLattices.cs`, `StoredFormRegistry.cs`, `ExtensionRegistry.cs`, `DemandGate.cs`, `ContainerMemo.cs`, `HostCaseDriver.cs` (track H), `SaturationOptions.cs` | §2 and §3 |
| `src/Apache.Calcite.Cosmos.Facts.Saturation.Tests/` (new) | conformance ports, the compile-path suite, encodings, canonicalisation, differential, replay, memory, concurrency |
| `…Facts/JsonBounds.cs`, `…Facts/JsonBoundsRecogniser.cs` (new); `…Facts/JsonSchemaFacts.cs` | the bounds extension and its reader; the `AdmitNull` row keeping `JsonBounds` |
| `A/Metadata/CosmosSchemaRecognition.cs` | subschema: geography, else bounds (l.27-29) |
| `A/Metadata/CosmosFactExtractor.cs` | window and range reading as `JsonBounds` |
| `A/Metadata/CosmosFactShadow.cs`, `CosmosFactWorker.cs`, `CosmosFactDifference.cs`, `CosmosFactExplainer.cs`, `CosmosFactBreaker.cs` (new) | shadow, the background worker and bucket, classes, explainers, breaker |
| `A/CosmosSchemaFactory.cs` | the `factReasoning` operand; `SaturationHost` per schema object; warm-up |
| `A/CosmosSchema.cs`, `A/CosmosAccountSchema.cs` | own the reasoning and host; `IDisposable` |
| `A/Apache.Calcite.Cosmos.Adapter.csproj` | reference Facts.Saturation (from 1b or 2) and the pinned `Eggnog` prerelease |
| `T/Infrastructure/TestInitialization.cs`, `T/Infrastructure/FactShadowAttribute.cs` (new), a ledger file | modes, per-test reasoning, ledger |
| `A/DESIGN.md`, `docs/19-performance.md`, `docs/20-monitoring.md`, `docs/appendix-a-operands.md`, `docs/14-json-schema.md` | a section on reasoning over an equality-saturation engine and its shadow; budgets; instruments; operands and switches; bounds |

**Phases 3 and 4**

| File | Change |
|---|---|
| `…Facts/JsonSchemaFacts.cs`, `…Saturation/JsonRuleTranslator.cs`, `JsonClaimTheory.Cases.cs` (new) | `discriminator`, hidden discriminators, `governs`; splits; `mentioned`; the `split` limit profile |
| `…Saturation/HostCaseDriver.cs` | removed at R3 |
| `…Facts/JsonFactTheory.cs`, `JsonFact.cs` (Phase 4, gated on B10) | `Close` and `Excludes` removed; `Entails` kept while `Meet` uses it |

### 9.2 Eggnog

| File | Change | When |
|---|---|---|
| `docs/plans/cosmos-integration.md` (new) | this plan | now |
| `docs/roadmap.md` (was `docs/status.md`) | a link to this plan; its open library requests | done |
| `docs/history/rule-layer-spec.md` | the R1b status lines (l.21-23, 3625, 4651); a note in §10 that the numeric bound rules (l.3979-3990) model raw comparisons only; §9: the R2a/R2b split and the consumer-profile test (§8.3 request 1); freeze pre-planning control (request 7); `governs` as wanted, if Phase 3 confirms it | next docs pass |
| `prototypes/cosmos-facts/README.md` (l.86-90) | where the gap tests live, after the adapter commits them | after 0a |
| `src/Eggnog.Benchmarks/` (new) | BDN in-process over the public API: `TheoryDefinitionBenchmarks`, `SchemaCompileBenchmarks`, `QueryScopeBenchmarks` (create, write, run, read and dispose under a frozen scope, at 10–50 touched tables and 10, 100 and 1,000 local rules, idle and active), and a benchmark over many sibling containers frozen in different orders | with R2a |
| `src/Eggnog.Tests/Values/LatticeTests.cs` | a registered struct lattice with a case join (a `NumericBound`-style hull), checked with `Verify` | R2a or R3 |
| `src/Eggnog.Tests/Scopes/` | 100 sibling frozen scopes with local rules (memory and release); 100,000 query-scope churn with retained memory flat beyond new interning and `InternedCount` flat under reused constants | R2a |
| `src/Eggnog.Tests/Cosmos/Scoped/**` | as RLS §8.1.2; unchanged by this plan | R2, R3 |
| `src/Eggnog/**` | R2 and R3 as RLS §9 specifies; no API change for architecture A beyond requests 2, 7 and 8 if taken up | R2, R3 |

---

## Appendix A. Capability by milestone

| Capability | R1b | R2a / R2 | R3 |
|---|---|---|---|
| Theory, translation, encodings, canonicalisation, snapshots | yes (graph per derivation) | the same code; host wiring changes | + `Cases` |
| Container scopes with local rules, frozen and shared by digest | no | yes | yes |
| Concurrent transient query children | no (one thread domain per graph) | yes | yes |
| CASE-arm and disjunct children | separate graphs | children | children |
| Host-driven cases (track H, ≤ 8 values) | case graphs | plain children, host-side lift | retired; `Schedule.Split` |
| Engine splits and lift | no | no | yes |
| Gaps 2, 4, 5 | tests | decided | decided |
| Gap 3 | Horn row (0a) | Horn row | + schema split for nested cases |
| Gap 1 `IN` | track H (optional) | track H (optional) | decided |
| Gap 1 per kind | track D (Horn) | child per disjunct | same |

## Appendix B. Review problems and where this revision answers them

Every problem raised against revision 1 was checked against the sources cited in it. All were found valid; none was
rejected.

| Problem | Answered in |
|---|---|
| The logical contradiction rule changes pinned tests and gap controls | §1.2; §5.1; Phase 0a exit |
| `Present` beside comparisons defeats the gate and the F6 fast path; the UUID example misread the extractor | §2.13 F6; §3.6 gate and controls; §7 item 7 |
| `WalkNegation` states presence without the parent's carrier | §2.5 carrier 4; §4.5 oracle; §5.1 |
| The reasoner built in `WithFacts` cannot reach a per-schema host | §0.2 #14; §3.2 wiring; §4.2 |
| F4 changes unit tests that derive from a bare `EqualTo` | §5.1 |
| The added `Present` is not inert under Horn | §2.8; §5.1; Phase 0a exit |
| Projection CASE conditions are a derived-scope site; `Rewrite` has no planner | §2.10; §3.2 checklist and signatures |
| The saturation rule's scope was misstated, and inequalities beyond the opposite limit are unproven | §0.2 #3; §2.8; F10 |
| Instant gates and literal rendering are not bits per token | §2.11 |
| The `AdmitNull` point was too broad | §0.2 #12 |
| A `TimeLimit` stop memoised across statements breaks determinism | §1.1 #5; §3.6; §3.8; §3.4 warm-up; §7 items 8–9 |
| Numeric literals intern without bound and make memo keys unbounded | §2.1 canonicalisation; §4.3 rate limit; B4, B8 |
| On R2 every local rule runs every iteration; B3 is unsupported | §0.2 #18; §6.1; B3; §8.3 request 2 |
| No budget covers a statement's fact work; track H is uncapped | §3.8 statement budget; §3.6 track H cap; B11 |
| The container memo has no memory budget and is duplicated per database | §2.9 deltas; §3.6; B12 |
| No budget covers background work | §4.3 worker and bucket; B9 |
| `ForceHorn` does not stop Eggnog work | §3.9; §7 item 11 |
| Per-kind rollback waits for a data-source rebuild | §0.2 #21; §3.9 `Override`; §4.8 |
| Reasoner lifetime does not match the graph's; the static service theory would become a cache | §3.2 |
| After Horn's removal, fallbacks silently lose pushdowns | §1.1 #11; §3.7 swap; B10; Phase 4 |
| `Lazy` caches compile failures and blocks planning threads | §0.2 #15; §3.4 compile state |
| Extension rules defined during container compile change other runs | §2.7 "When" |
| No lease holds a swapped-out generation | §0.2 #20; §3.7 |
| B5 depended on ReadyToRun and was ambiguous | B5 |
| The frozen `service` sibling recreates risk 4 | §3.3; R14; §8.3 request 7 |
| Limits do not fit split runs | §3.8 profiles; Phase 3 exit; §8.3 request 8 |
| Explainer signature and ablation do not fit frozen, monotone scopes; one `lattice` ruleset conflicts with per-capability ablation | §2.3; §3.4 step 4; §4.5 |
| Track H on R2 cited R3 members | §0.2 #19; §3.6 track H; Appendix A |
| "Internal ordering" of R2 does not let the adapter start earlier | §0.2 #22; §8.3 request 1; Phase 2 entry |
| The R3 gate condition misses `governs` demand | §2.8; §3.6 gate |
| B4's "0 retained" conflicts with D15 | B4; §7 item 10 |
