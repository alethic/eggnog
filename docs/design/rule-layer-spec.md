# Eggnog rule layer: specification, version 2

> **Status (2026-10-11).** This is version 2 of the rule-layer specification, revised to answer two design reviews
> (§14 maps every finding to its resolution). It replaces version 1, which is in git history at commit `5acd00e` and was
> written against the API of the `Eggnog.Storage` spike. Version 2 specifies the public `Eggnog` library on top of the
> real engine, `Eggnog.Relations`:
> - rules compile to the bridge's rule builder (`EGraphRuleBuilder`);
> - semi-naive evaluation and watermarks belong to the bridge and to `EGraphContext`;
> - scopes are contexts (`Store`, `Context`, `EGraphContext`);
> - case splits and lift use the M10.4 primitives;
> - extraction is `Eggnog.Relations`' extractor.
>
> Section 1 re-decides every v1 decision. D1–D26 keep their v1 numbers, D27–D37 are version 2's own, and D38–D49 were
> added in answer to the reviews. The colored-contexts spec cites D3, D4, D6, D11, D16, D18, D20 and D21; the rows of
> D20 and D21 changed, and §13 lists the edits that spec needs.
>
> **First landing.** Milestone R1a (§9) is small on purpose. It turns `src/Eggnog` from an empty project into the
> library (definitions, rules, runs, host writes and reads, and extraction, at the root on today's engine), drops the
> spike from the project and from the package, and merges to main before any other rule-layer work.
>
> **Implemented (2026-10-13).** R1a is on main (`edd1fa9`, merged with colors M9.3–M9.4), and so are R1b (`80675d8`,
> merged with colors M10.1–M10.2 at `3b9dda1`) and R2, merged with colors M10.4. R2 runs every scope, the root
> included, on colors M10.3's `EGraphContext`, with no engine change. Colors M10.3 and M10.4 delivered the engine
> requests R2 and R3 depend on (E4, E5, E7–E10; §15.6, §15.7); R3 is next. Where the implementation found the better
> design, the text below now says what was built. §15 records every deviation from the earlier text (what it said,
> what was built, and why) and every item still deferred, with its milestone.

**Binding inputs.**
- `docs/design/colored-contexts-spec.md` (cited as *colors*): §1, §3, §10, §11 and §19, and the rest where cited.
- `docs/engine/conventions.md` and `docs/engine/decisions.md` (decisions 1–12, L1–L7).
- `docs/design/egglog-engine-map.md` §3 (the bridge and what the frontend assumes).
- `prototypes/cosmos-facts` (`prelude.egg`, `01`–`07`, `README.md`), and the adapter's gap tests in `D:\calcite-cosmos`
  (`src/Apache.Calcite.Cosmos.Adapter.Tests/EndToEnd/CosmosDeclaredFactPlanningTests.Gaps.cs`, on the adapter's
  fact-gap-tests branch; see the prototype README, "Confirmed against the adapter").

**Code baseline.** Worktree `project-overview-1f1e82` at `5acd00e`:
- The bridge (`src/Eggnog.Relations/Bridge`) and extraction (`src/Eggnog.Relations/Extraction`) are ported (M4).
- Colors M9.0–M9.2 are in (`Store`, `Context`, the clock, id blocks, the layered union-find).
- M9.3–M10.4 are not. Today's bridge `EGraph` runs on a bare `Database` (no `Store`) and has only a root.
- `src/Eggnog` is an empty project that references the spike `src/Eggnog.Storage`; `src/dist-nuget` packs the spike
  beside `Eggnog` and `Eggnog.Relations`.
- `Eggnog.Relations` declares about 95 public types (the bridge, extraction, codecs, ids and collections).

**Paths.**
- Public and internal rule-layer files: `src/Eggnog/<Folder>/<Type>.cs`, namespace `Eggnog`.
- Tests: `src/Eggnog.Tests/<Folder>/<Type>Tests.cs`, namespace `Eggnog.Tests`.
- Engine paths are relative to `src/Eggnog.Relations/`.

**Labels.**
- Decisions **D1–D26** (re-decided), **D27–D37** (version 2) and **D38–D49** (answers to the reviews), §1.
- Engine members this layer needs that do not exist yet: **E1–E12**, §5.2 (E2, E3 and E6 are withdrawn; R1b added
  E12, §15).
- Invariants owned by this layer: **V, C, R, S, M, Q, E, A, O**; invariants delegated to the engine are cited by their
  colors number (I19–I33), §7.
- Milestones **R1a, R1b, R2, R3**, §9. Every public member in §3 carries the milestone that adds it.

---

## Contents

1. [Decisions](#1-decisions)
2. [Overview](#2-overview)
3. [Public types](#3-public-types)
4. [Internal types](#4-internal-types)
5. [The engine contract](#5-the-engine-contract)
6. [Semantics and algorithms](#6-semantics-and-algorithms)
7. [Invariants](#7-invariants)
8. [Test plan](#8-test-plan)
9. [Milestones](#9-milestones)
10. [Scenario 02 against the v2 API](#10-scenario-02-against-the-v2-api)
11. [Residual risks](#11-residual-risks)
12. [Appendix: porting an egglog program by hand](#12-appendix-porting-an-egglog-program-by-hand)
13. [Edits this spec asks of the colored-contexts spec](#13-edits-this-spec-asks-of-the-colored-contexts-spec)
14. [Review responses](#14-review-responses)
15. [Implementation notes (R1a-R1b, the engine requests of colors M10.3, and R2)](#15-implementation-notes-r1a-r1b-the-engine-requests-of-colors-m103-and-r2)

---

## 1. Decisions

**Status key.**
- **Kept**: the v1 decision stands; the mechanism may now be the engine's.
- **Changed**: the decision is different, and the row says how and why.
- **Engine**: the engine now provides it; the row says where. The rule layer adds nothing but configuration.
- **Deferred**: not in version 2; the row says what replaces it and when it may return.

### 1.1 The v1 decisions, re-decided

| # | v1 question | Status | v2 decision |
|---|---|---|---|
| D1 | How do class values join across scopes? | **Changed** | A scoped atom (`handle.In(tag)`, `Fact.In(tag, …)`) may read only a **value-only table**: every key and the output are value types, never a sort. It compiles to an ordinary atom over a hidden **mirror table** `T@tag`. Copy rules, run in the tagged scope just before anything reads the mirror (D48), keep it equal to the tagged scope's own view of `T` (§6.8, invariant M). No class value ever crosses a scope boundary inside a rule, so v1's invariant H holds by construction and is no longer checked per variable. **Why:** the engine runs a rule against exactly one context's view (colors §9, §10.3). Reading an ancestor's view in the ancestor's own identifiers would need a per-atom view source in the executor, which no milestone provides. Value cells are untouched by equalities, so a mirror is exact. The Cosmos theory reads only value-keyed scoped facts. |
| D2 | Semi-naive windows | **Engine** | The bridge's variants (map §3.4; colors I27, Lemma 1 of I31): the focus atom reads rows with `ts ≥ W`, earlier atoms rows with `ts < W`. Each match is therefore found once per iteration, so v1's per-iteration `MatchBuffer` deduplication is gone. The rule layer passes no windows. |
| D3 | An Id constant loses a union | **Engine** from R2; **rule layer** in R1 | Colors X19/I30 (M10.2, M10.3): every Store-backed context, the root included, runs each Id constant of a rule in its canonical form there, and a watermark is used only when the stored constants equal the current ones. The bridge lowers Id constants through `QueryEntry.IdConst`. R1 runs on a bare `Database`, where constants run as written (as in egglog). So the R1 root adapter compares, before every iteration, the canonical forms of a rule's Id constants with those its engine variant was built with, and re-lowers the rule when they differ. The new variant starts at watermark 0, which is the full run D3 requires (§6.4.5). R2 deletes this path. Which class constants a rule may hold at all is D39's. |
| D4 | Inheriting watermarks | **Engine** | Colors §10.2: lazy seeding `min(W_a, CreatedAt)` of the ancestor's child on the path, refused when the constants differ, generations against `RuleId` reuse. `AllowInheritance` comes from `ActionReadAnalysis` (X41). v1's tag check ("refuse when the scope carries a tag the rule reads") is no longer needed: mirror tables are ordinary tables (D1), so a child tagged with a read tag sees its ancestors' mirror rows as old rows of its own view, which is exactly I31 Case 2. Rule-layer actions never read tables (invariant Q), so every semi-naive rule may inherit. Naive rules, queries and split matchers are naive bridge rules and never inherit. |
| D5 | Cost of delta scans | **Engine** | Tables are sorted by their timestamp column. `GeConst`/`LtConst` cost one binary search per layer (I19). An untouched frozen layer contributes nothing. Planner statistics are per context (colors §9.7). |
| D6 | Sealing definitions | **Kept** | Definitions are never sealed. Rule, query, split and tag definitions may be made from any thread at any time and take part from the next iteration of every run. A table definition (and, from R2, a rule whose first scoped read of a table creates a mirror table) follows the engine's rule (colors §4.3): from any thread once the root is frozen; before that, it throws `InvalidOperationException` while another thread is using the root. R1's root guard enforces the same rule, so no milestone changes it (§6.12, D47). Local rules are a rule-layer notion: every engine rule is global, and the rule layer chooses which rules each iteration passes to `RunRules` (§6.7). The recommended topology stays: containers are children of an empty, frozen root. |
| D7 | Re-splitting | **Changed** (mostly **Engine**) | Split records, the B6 skip of keys already covered by a live record in the scope or an ancestor, the limits, lift before deepening, and disposal belong to M10.4's `SplitDriver` (colors §11.3). Split matchers are **naive** bridge rules, re-matched at every split step, so there is no split watermark to reset. Records live until the top-level `Run` ends. Which keys a split considers is part of the split rule's body (D43); the rule layer passes no `ISplitDemand`. |
| D8 | Lift algorithm | **Engine** | M10.4's `Lifter` (colors §11.2, I33): a full partition meet over `MemberMap`s, term lift in rounds, the `IsLocalId` filter, a signature memo, and the L1 winner probe (`ScanAncestorsMentioning`), which v1's lift lacked. The rule layer supplies only the `LiftSpec`: which tables lift, and their case joins (§6.9). |
| D9 | Zero cases | **Kept** | A host lift with no cases throws (the engine also throws, colors §13 #34). A split match whose element set is empty produces no split request and counts in `RunReport.SplitsSkipped`; the theory's own rules derive the contradiction. |
| D10 | Extraction | **Engine** | `Eggnog.Relations.Extractor` (Knuth's algorithm over the classes reachable from the root; content tie-breaks, culture-invariant; acyclic with zero costs). |
| D11 | `StopOnContradiction` | **Kept** (mechanism partly **Engine**) | `false` by default for host runs and `true` inside cases. In cases the engine does it: `ContextOptions.Case`, the storage stop flag (X11, colors §6.9), `IterationReport.Stopped`, and the disposal of vacuous cases (L4). For a host run the option stays a rule-layer schedule check. What a stopped case still answers is D45's. |
| D12 | "A conjunct never proves itself redundant" | **Kept**, now structural by default | A ruleset defined with a licence tag (`RulesetOptions.Licence`) makes every rule in it **erasing**: every atom over a Fact table must read the licence scope. A rule that may judge on the running scope's facts says so with `RuleBuilder.ReadsOwnFacts(reason)`, which `Rule.ToString` renders; `RuleBuilder.Erasing(licence)` makes one rule erasing in a ruleset without a licence. Invariants R (writes are never scoped) and A (writers of analysis tables read only term and analysis tables) hold as before. Invariant H is gone (D1). The residue is unchanged: an erasing rule can still see class equalities its running scope derived (§11, risk 2). |
| D13 | Arity limit and lambdas | **Kept**, narrowed | Typed handles go up to four key columns. The untyped layer (`Apply`, `Term.Constant`, `Fact.Equal`, `Fact.NotEqual`, `Compute.Of(Primitive, …)`, `Var(ColumnType, …)` and the untyped `Define*` overloads) is **internal** until a consumer needs more than four columns; the differential generator reaches it through `InternalsVisibleTo`. Named primitives render by name; lambdas render as `<lambda>`. |
| D14 | `.egg` frontend | **Changed** | Never. Consumers use the C# API only, and no milestone adds a parser. v1's frontend mapping (§3.14) is dropped; §12 keeps a correspondence table for porting egglog programs by hand, which the Cosmos tests use. |
| D15 | Interner growth | **Kept** | Pools are permanent per `EGraph` (the engine's `BaseValues` is append-only). Constants used only for reading never intern: host reads use `BaseValues.TryGetExisting` (E1). `ValueStore.InternedCount` reports growth (E1) so a host can recycle a graph. Constants of rules and queries intern at definition. |
| D16 | Storage surface | **Changed** | No storage surface is public. What v1 added to the spike exists in the engine and stays internal: `Version` is `Database.ChangeStamp` (X30). It moves on every effective change a call of the scope makes to its own layer, including (R2) the catch-up copies that hide an ancestor's rows when one of the scope's own classes merges in that call; queries, reads and writes that change nothing never move it. The upkeep a call makes first is left out (R2): the sync of the internal mirror tables (N62), and the catch-up that brings the scope's layer, and each writable ancestor's, in line with what an ancestor wrote since, which a descendant makes lazily at its next call (N87); a write into an ancestor therefore never moves a descendant's `Version`. `ScanOwn`, `ScanMerges`, `IsLocalId` and `ScanAncestorsMentioning` come with M9.2–M9.4 and M10.4. `LatestStamp` and `ApproximateCount` are not needed, because the engine plans. |
| D17 | Parallel cases | **Kept** (**Engine**-enforced) | Sequential. A writable parent shares its thread domain with every case under it (colors §8.3). Children of a frozen scope may run concurrently. |
| D18 | Rollback | **Changed** | No rollback, and no re-run guarantee. In Store-backed contexts the engine commits watermarks only after a run without a recorded failure (X18), and a child that throws is faulted (X12). The rule layer **faults the scope** after any `RuleEvaluationException` from a run or a write, the root included. `Scope.Failure` keeps the exception, and every later call except `Dispose` throws `InvalidOperationException` with it as `InnerException`; a faulted root makes the graph unusable. A failed query is the exception: it wrote nothing, so it throws without faulting (D49). Limits and cancellation stop between iterations and are not failures. |
| D19 | Cost type | **Engine** | `long`, saturating addition, negative costs rejected (`Extraction/CostMath.cs`). |
| D20 | Built-in value types | **Changed** | Values are the engine's 32-bit base values (`Value`, `IBaseValueCodec<T>`). Built in: `long` (`Int64Codec`), `int` (stored as `i64`), `bool`, `double` (`DoubleCodec`, with −0.0 normalized to +0.0 and NaN rejected by the rule layer before encoding), `string`, registered enums (stored as `i64`), and `ValueSet<T>` (an **interned base value**, not a container; the rule layer registers no container, §13). User types use `ValueStore.Register<T>` over `InternedCodec<T>`. v1's `IValueCodec<T>` is dropped, because engine codecs are static struct types. There are no sets of classes. `int` and enums are stored as `i64` because `Int32Codec` boxes −1 to `Value.StaleRep`, which must never appear in a table's first column. |
| D21 | Callbacks | **Deferred** | Version 2 has no rule callbacks. Observation after a run is a read or a query (D34, D49); a run observer (D41) sees progress during a run but cannot write. Callbacks that write return only when a consumer needs them; they would then opt out of watermark inheritance by overriding `ExternalFunction.ReadsTables` (colors §10.2) on their collector, which needs no new engine member. |
| D22 | Naming | **Kept** | Host writes are `Scope.Assume(facts)`, `Scope.Make(term)` and `Scope.Union(a, b)`, and `Scope.Write(batch)` makes several of them one batch. `Scope` (a node of the tree) and `ScopeTag` (a label that scoped atoms resolve) stay separate types. |
| D23 | Relative scopes | **Kept** | Not supported. Tags are absolute labels, owned by one graph (D38), that resolve to the nearest ancestor-or-self. |
| D24 | `:until` | **Kept** | `Schedule.Until(body, facts)` (R1b): at least one ground fact, checked as a host check (§6.6.3) each time the node executes. `Schedule.Until` refuses filters and terms with a variable or a computation, and `Scope.Run` refuses, before any work, a fact its scope cannot read (§6.5.2, §6.5.3). |
| D25 | Lattice laws | **Kept** | `ValueLattice<T>` bundles `Merge` with its `CaseJoin` and has `Verify(samples)`. `Merge` lowers to `MergeFn.Primitive(merge, MergeFn.Old, MergeFn.New).AsLattice()` (decision 2 needs the lattice flag to let children write); `CaseJoin` lowers to `LiftTable`'s case join (§6.9). |
| D26 | First atom of a full run | **Engine** | The planner chooses (decision 7). A rule is planned once, on the statistics of the context it first runs in, or of the frozen context that pre-plans it (colors §9.7, §10.8, E10). |

### 1.2 Version 2 decisions

| # | Question | Decision |
|---|---|---|
| D27 | How is a relation represented? | As a bridge function whose output column is the engine's unit base value: `Default = DefaultVal.Const(unit)`, `Merge = MergeFn.AssertEq`, no subsumption. egglog's frontend instead desugars a relation into a constructor with a fresh sort (map §3.1). Ours mints no id per row, adds no rebuild column, and its merge is a lattice merge, so children may write it (decision 2). The contradiction relation is the nullary case, schema `[Unit]`, which is what `Store.DesignateContradiction` expects. |
| D28 | How does R2 add contexts without changing the public API? | Through an internal **engine-scope** abstraction (`EngineScope`, §4.1). R1 implements it for the root over the bridge's egglog-compatible root API (`RootEngineScope`). R2 implements it over `EGraphContext` for every scope, the root included (`ContextEngineScope`), and deletes `RootEngineScope`. Public types only gain members (and overloads) in later milestones; no member changes signature or meaning once it has shipped. |
| D29 | Engine types in the public API | None in public signatures: they use only `Eggnog` types and BCL types. The package must not show two APIs either, so the engine's own public types become internal (D44, E11). Rule-layer files never write `using Eggnog.Relations;`; they name engine types through the child namespace (`Relations.EGraph`, `Relations.FunctionId`). Engine internals are reached through `InternalsVisibleTo("Eggnog")`, which `Eggnog.Relations.csproj` already declares. |
| D30 | When are host writes visible? | Every host write call (`Make`, `Assume`, `Union`) is one atomic batch, and `Scope.Write(Action<ScopeWriter>)` makes several calls one batch: validate, stage, flush and rebuild before it returns. A batch whose facts all hold already stages nothing and flushes nothing, so `Version`, the clock and live extractors are untouched. A batch with a rejected call writes nothing, even when `write` caught the rejection (`Write` then returns normally), and while a `Write` batch is open the scope's own members throw `InvalidOperationException`: the batch's calls go through its `ScopeWriter`; its writable ancestors refuse writes and runs until it ends (N88). A read first brings its scope current (`EngineScope.EnsureClean`, a no-op when clean). So v1's cleanliness contract (`Rebuild()`, `IsClean`, reads that throw while dirty) is gone from the public API. |
| D31 | Primitive failures | A primitive or lattice delegate that throws is caught inside its external function. The failure (rule, primitive, decoded arguments, exception) is recorded in the scope's runtime, and `ExecutionState.ReportFailure` is called, so the engine stops the run early and treats it as a panic (in Store-backed contexts no watermark commits, X18). The rule layer then throws `RuleEvaluationException` and faults the scope (D18). One outcome is not a failure: the built-in `+` and `-` (`Primitives.Add`, `Subtract`) are **partial**, as egglog's checked arithmetic is. An overflow in a body let means "no result" and drops the match without recording anything; in a head it fails the rule as any exception does. User primitives have no "no result" outcome (§15). |
| D32 | Host writes to a function without a lattice | Checked before anything is staged: a value that conflicts with the stored one, two different values for one key in one batch, or a merge of the batch that would give such a function two values for one key, throw `FactConflictException` (an `InvalidOperationException` carrying the table, the key, the stored value and the new one) and write nothing. A merge is a union, a sort equality, or a constructor write that gives an existing key another class; phase 1 of the batch simulates the congruence closure the rebuild will compute (§6.6.1). Lattice merges that a host union causes still run at the rebuild, so a lattice delegate that throws there faults the scope (D18). A conflicting rule write is an engine panic (D31). |
| D33 | Deletion and subsumption | Not exposed. The engine has them under decision 1 and refinement R1 (colors X28). Watermark inheritance (I31), lift soundness (I33) and mirror exactness (M) all rest on monotone contents. |
| D34 | Host conjunctive queries (`Any`, `Count`, `ForEachMatch`) | A query compiles to a naive bridge rule whose only action is a collector. R1 runs it without merging and without advancing the clock (D49). From R2, in a writable scope it runs as an iteration of that scope that writes nothing; in a frozen scope it runs in a transient child context created for the call and disposed after it (cheap, colors §14.2, and thread-safe on a frozen parent), so a frozen scope stays read-only and can be queried from many threads. A transient child is not listed in `Children`, but it defers its parent's release like any child (D40). |
| D35 | Cosmos conformance | In two encodings. The **literal** encoding ports `prelude.egg` and all seven scenario files as written (contexts as data at the root, splits written as rules): files 01–03 in R1a, 04–07 in R1b. The **scoped** encoding uses scopes, frozen schemas, local rules and engine splits (R2 for the scenarios without a split, R3 for the rest). The literal encoding is also the oracle for scoped programs (§8.2). |
| D36 | Rule-layer ids for engine rules | One `CompiledRule` per rule-layer rule, with up to two engine variants: semi-naive (or naive for `Naive()` rules) and, on demand, a naive twin for the internal `RunOptions.SemiNaive = false` that differential tests use. In R1 a variant is re-lowered when its Id constants change (D3). Each variant has a unique description, which keys its statistics. |
| D37 | Gap 5 | The adapter's fifth gap (an integer enum against a bound) is expressed as a new scoped scenario (`Scenario08IntegerDomainTests`, §8.1.2) with an integer-domain lattice. It is not in the prototype. |

### 1.3 Decisions added in answer to the reviews

| # | Question | Decision |
|---|---|---|
| D38 | Who owns a scope tag? | **The graph.** `EGraph.DefineScopeTag(name)` makes one (names are unique per graph; there is no public constructor), and `EGraph.RootTag` is the root's. Every member that takes a tag (`CreateChild`, `In`, `Fact.In`, `RuleBuilder.Erasing`, `RulesetOptions.Licence`, split case tags, `CaseSet.Add`) throws `ArgumentException` for another graph's tag. The count of live frozen scopes per tag, which guards mirror creation (§6.8), lives in the graph's `MirrorRegistry`. **Why:** v2 kept that count on a static tag object, so one graph's frozen schema made every other graph's theory fail to compile, and two tags of the same name never resolved to each other. |
| D39 | What may a class value be used for? | **Class values carry the scope that returned them** (their *origin*). Only the library makes a `ClassId`; `default(ClassId)` has no origin and every scope rejects it. Every host entry point, and every rule and query, checks that a class value is of this graph, that its origin is the scope or an ancestor of it, and that its origin is not disposed; otherwise `ArgumentException` naming both scopes. Global and split rules accept class constants only from the root; local rules from their owner or its ancestors; queries are checked against the scope they run in. A value made inside a `Write` batch is valid only once the batch commits. Values die with the scope that returned them, which covers every value obtained in a case. **Why:** the adapter runs many queries under one schema at once; using one query's predicate in another must be an error, not a silently wrong answer. |
| D40 | What does `Dispose` do with live children? | **`Dispose` never throws.** It makes the scope unusable at once (`IsDisposed`; `ObjectDisposedException` on use) and kills its class values (D39). Its local rules and engine context are *released* when it has no live children (transient query children included) and no call is in progress in its thread domain on another thread; whichever thread removes the last obstacle releases it. A second call does nothing; the root's `Dispose` does nothing (the root lives as long as the graph, which holds only managed memory and is not disposable). **Why:** the .NET contract (CA1065), `using` blocks that unwind while a child is still alive, and the adapter's retirement of a container's frozen schema while queries still run beneath it on other threads. |
| D41 | What does a host see of a run, and why a rule fired? | `RunReport` carries per-rule statistics (matches, search time, iterations with matches, and the plan when `EGraphOptions.RecordPlans` is set) and the run's search, merge and rebuild times. `RunOptions.RecordSteps` keeps one `RunStep` per iteration, mirror sync, split step and lift; `RunOptions.Observer` receives each step as it happens and may read the scope. `Scope.CountRows` and `Scope.Rows` read a table's rows; `Scope.Failure` keeps a fault's cause. **Proofs and per-row explanations are out of version 2.** To find why a conjunct was lowered or deleted: run with `RecordSteps` and `RecordPlans`, find the first step in which the lowering or deleting rule matched, read the rule with `Rule.ToString()`, and read the facts it reads with `Rows`. A provenance mode (the first rule and iteration that derived each row) needs per-row write attribution in the engine's merge, which no milestone provides; it is deferred. |
| D42 | What do run limits bound? | Work a user can reason about. `IterationLimit` (engine iterations in every scope of the run), `TimeLimit`, and `RowLimit` (rows in the own layers of the top-level scope and of the run's live cases, the engine's `TotalSizeEstimate`) replace v2's `ChangeLimit`, which counted table merges. Limits are per top-level `Run`, cases included, and are checked before every iteration and every split step. An engine iteration, or a lift, cannot be interrupted, so a run overshoots a limit or a cancellation by at most one iteration or one lift. |
| D43 | Which keys does a split consider, and when? | **The split rule's body decides.** `DefineDomainSplit` takes a list of guard relations, and `DefineSplit` any body. The Cosmos theory splits a schema on every present discriminator, and a query only on the present discriminators its predicate mentions (an analysis relation `Mentioned`, derived from comparison terms). A split step should run once the facts its rules read have settled: `Saturate(Saturate(facts…), Split(rule, inCase), rewrites…)`, which `Schedule.Split`'s documentation recommends. **Why:** otherwise every query re-splits every discriminator the frozen schema already split on; and a split made before its domain narrowed opens cases that only later go vacuous, while its record keeps the key until the run ends. |
| D44 | Two APIs in one package? | **No: the engine's public types become internal** (E11). The conventions say to default to `internal` and make a type public only when a consumer of the engine needs it; `InternalsVisibleTo` covers `Eggnog`, the tests and the benchmarks. `Surface/PublicSurfaceTests` asserts that no public type of another assembly in the package shares a simple name with a public `Eggnog` type. If the engine owner instead keeps `Eggnog.Relations` a supported standalone API, its public types are marked `[EditorBrowsable(EditorBrowsableState.Never)]`, documented as unsupported for `Eggnog` consumers, and the surface test exempts the types so marked. |
| D45 | What does a contradicted case answer? | A case whose contradiction set its stop flag (D11) has no model, and the engine refuses most operations on it (colors §6.9, §10.3, §10.4). The rule layer checks first: `IsContradictory` and the identity members answer; `Assume` does nothing; `Union` returns `false`; `Run` returns at once with `StopReason = Contradiction`; every other read and write, queries, extraction, `CreateChild` and `OpenCases` throw `ScopeContradictedException`; `Dispose` works. Only case scopes have the stop flag: any other contradictory scope stays fully usable (contradiction is an ordinary fact there). |
| D46 | Parallelism | **Opt-in.** `EGraphOptions.DegreeOfParallelism` defaults to 1: serial and deterministic, extraction ties included (decision 4). From R2, `ScopeOptions.DegreeOfParallelism` sets one scope's (`ContextOptions.Parallelism`); children of a frozen scope are serial unless it is set (colors §10.9). |
| D47 | Threads | Every milestone keeps a **rule-layer guard per thread domain** (`ScopeGuard`, over a `ThreadAffinityGuard`), shared and reassigned on freeze exactly as colors §8.3 shares and reassigns the engine's. Every scope call enters it for the whole call (a host batch, a run, a query), so a second thread fails fast instead of interleaving between engine calls. Every `ScopeWriter` call enters its scope's guard too; a call refused there, like any rejected call, rejects the whole batch. The rule layer never holds the engine's own domain across calls. User delegates the engine invokes (primitives, lattice merges, filters, cost functions) must not call into a graph, and such a call throws. `Scope.DefineRule` enters the scope's domain; global definitions follow D6. |
| D48 | When are mirrors brought current? | **Just before use.** Before any iteration, query or split matcher in scope `C` reads `T@t`, the tagged scope `S = resolve(C, t)` runs its copy rules for `t` once if it is writable and has changed since its last sync; and once more when it freezes. This replaces v2's three triggers, covers host queries, and removes the one-iteration lag within the tagged scope. |
| D49 | Queries and clock ticks | R1 runs a query's rule without merging and without advancing the clock, and a batch that stages nothing does not flush (D30). So queries, reads and writes that change nothing never invalidate an extractor; a `Run` may. A query that fails throws `RuleEvaluationException` without faulting the scope. `Query` is `IDisposable`: define a reusable query once, and dispose one made for a single request, which frees its engine rule and call sites. Query names are unique among live queries, so a disposed query's name may be defined again. `Dispose` never waits: when it cannot free at once (an engine call holds the R1 engine lock, or it is called from a user delegate), the graph frees the rule before it next runs an iteration or a query. |

---

## 2. Overview

### 2.1 The API in one screen

```csharp
var graph = new EGraph();                                                    // serial by default: deterministic (D46)
graph.DefineSort<Pred>("Pred");
var present = graph.DefineRelation<string>("present");
var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
var strEq = graph.DefineConstructor<string, string, Pred>("StrEq");
var horn = graph.DefineRuleset("horn");
horn.DefineRule("a singleton domain makes a path present", r =>
{
    var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
    r.When(d.Is(dom.Of(p)));
    r.Where(d, d => d.Count == 1);
    r.Then(present.Of(p));
});
```

In R1 everything runs at the root:

```csharp
graph.Root.Assume(dom.Of("kind").Is(ValueSet.Of("A")));
graph.Root.Run(Schedule.Saturate(horn));
bool known = graph.Root.Check(present.Of("kind"));                           // true
```

From R2 on, a graph defined the same way, with nothing written at its root, serves an empty frozen root, a frozen
schema per container, and a layer per query. Tags belong to the graph (D38):

```csharp
var schemaTag = graph.DefineScopeTag("schema");
var queryTag = graph.DefineScopeTag("query");
graph.Root.Freeze();                                                         // empty; containers are its children
var schema = graph.Root.CreateChild(schemaTag);
schema.Assume(dom.Of("kind").Is(ValueSet.Of("A")));
schema.Run(Schedule.Saturate(horn));
schema.Freeze();
using var query = schema.CreateChild(queryTag);
Pred q = query.Make(strEq.Of("kind", "A"));                                  // q is usable in query and below (D39)
query.Run(Schedule.Saturate(horn));
bool inherited = query.Check(present.Of("kind"));                            // true; reads never need a rebuild (D30)
```

### 2.2 Layers

```
consumer code (C# only)
        │
        ▼
Eggnog (this spec, public)        EGraph, Scope, ScopeTag, typed handles, Term/Fact, rules, queries, schedules,
                                  CaseSet, CostModel/Extractor/ExtractedTerm, run reports and steps
Eggnog (this spec, internal)      RuleCompiler → RuleProgram → RuleLowering, EngineScope, ScopeGuard, ScopeOrigin,
                                  ScheduleExecutor, HostBatch/TermReader, MirrorRegistry, RuleSplitSource,
                                  ExtractionCatalog
        │  InternalsVisibleTo("Eggnog")
        ▼
Eggnog.Relations bridge           EGraph, EGraphRuleBuilder, RunRules/IterationReport, EGraphContext (M10.3),
(internal, E11)                   CaseSet/Lifter/SplitDriver (M10.4), Extractor, BaseValues
        ▼
Eggnog.Relations core             Database, tables, Free Join planner and executor, union-find,
                                  Store/Context/layers (M9–M10)
```

### 2.3 How the layer maps onto the engine

| Rule layer | Engine |
|---|---|
| `EGraph`: definitions, `ValueStore`, scope tags, the root scope | One bridge `Relations.EGraph`. R1: on a bare `Database`. R2: on a `Store`, with `Root` an `EGraphContext` (colors §10.1). |
| `Scope` | One `EngineScope`: R1 `RootEngineScope` (bridge root API); R2 `ContextEngineScope` (one `EGraphContext`, colors §10). Plus a `ScopeGuard` per thread domain (D47) and a `ScopeOrigin` (D39). |
| `ScopeTag`, `In(tag)` (R2) | Rule-layer labels owned by the graph; scoped atoms read mirror tables (§6.8). The engine has no tags. |
| `ClassId` | A class cell plus the `ScopeOrigin` of the scope that returned it (D39) |
| `Relation<…>` | Bridge function `[keys…, Unit]`, `DefaultVal.Const(unit)`, `MergeFn.AssertEq` (D27) |
| `Function<…, TOut>` | Bridge function `[keys…, TOut]`, `DefaultVal.Fail`, lattice merge `MergeFn.Primitive(…).AsLattice()` or `MergeFn.AssertEq` |
| `Constructor<…, TSort>` | Bridge function `[keys…, Id]`, `DefaultVal.FreshId`, `MergeFn.UnionId` |
| Value columns, sort columns | `ColumnTy.Base(type)`, `ColumnTy.Id` |
| A rule | One `CompiledRule` (engine-independent `RuleProgram`) and its engine variants, each one `EGraphRuleBuilder` rule (D36, §6.4) |
| Body atoms, filters and lets, head writes | `QueryTable`; `QueryPrim`; `Set`, `Lookup`, `Union`, `CallExternalFunc` (§6.4) |
| Primitives, lattice merges, collectors | Internal `Relations.ExternalFunction` subclasses registered with `EGraph.RegisterExternalFunction` |
| Semi-naive evaluation, watermarks, inheritance, Id constants | Bridge `RunRules` and `EGraphContext` (colors §10.2, §10.3, X19). R1: the root adapter also re-lowers rules whose Id constants changed (D3). |
| One iteration | `RunRules(ids)` → `IterationReport` (`Changed`, `RebuildChanged`, `Stopped`, `RuleSetReport`, `RebuildTime`) |
| Change detection | `IterationReport` flags (decision 11); `Version` = `Database.ChangeStamp` (X30), less, from R2, the stamp moves of the upkeep a call makes first: mirror syncs (N62) and the catch-up with an ancestor's writes (N87) |
| Host writes | R1: `ExecutionState` + `TableAction`/`UnionAction`, then `FlushUpdates`. R2: `EGraphContext.FreshId`/`StageRow`/`StageUnion`/`FlushUpdates` (colors §10.4). |
| Host reads | R1: `LookupId`, `GetCanonInUf`, `TableSize`, `ForEach`. R2: `EGraphContext.TryLookup`, `Find`, `TableSize`, `ForEach` (colors §10.5). |
| Queries | R1: `Database.ExecuteRuleSet` without a merge (D49). R2: `EGraphContext.RunRules`, in a transient child for a frozen scope (D34). |
| Freeze, children, dispose (R2) | `EGraphContext.Freeze(planRules)`/`CreateChild`/`Dispose` (colors §8, §10.8; E7, E10) |
| Case splits and lift (R3) | `SplitDriver`, `ISplitSource`, `ICaseSchedule`, `CaseSet`, `Lifter`, `LiftSpec` (colors §11; E8) |
| Extraction | `ExtractionSchema`, `Extractor.CreateOnDemand`, `Relations.CostModel` (R2: over a context, E5) |

### 2.4 Object model, lifetime and threading in brief

- **`EGraph`**: one per process, or one per family of containers. It holds only managed memory and is not disposable.
  Definitions follow D6 and §6.12.
- **`Scope`**: one engine context with the rule layer's state for it (local rules, tag, guard, origin, runtime
  buffers).
  - Not thread-safe. Scopes that share a thread domain (a writable scope and everything created under it) are used from
    one thread at a time; a second thread gets `InvalidOperationException` from the rule-layer guard (D47), in every
    milestone.
  - Children of a frozen scope each have their own thread domain. Reads of a frozen scope, queries on it, and
    extractors over it are thread-safe. A split's cases run on their parent's thread.
  - Typical tree (R2): `root` (empty, frozen) → `schema#k` (one per container, tag `schema`, frozen after compilation) →
    `query#n` (tag `query`) → `case#m` (made by splits, disposed when the `Run` ends).
- **Lifetime** (D40). `Dispose` never throws and makes a scope unusable at once; its engine state is released once it
  has no live children and no call runs in its domain on another thread. A container's frozen schema can therefore be
  retired while its queries finish.
- **Local rules** (R2) belong to the scope they are defined on. They are active in it and its descendants, can be added
  only before it is frozen, and are freed when it is released.
- **Class values** (D39) are usable in the scope that returned them and its descendants, until that scope is disposed.
- **Visibility.** Host writes are atomic batches (D30); while a `Write` batch is open, the writable scopes above it
  refuse writes and runs (R2, N88). Reads never fail for want of a rebuild. Engine operations (`Run`, splits, lifts)
  leave the scope current.

### 2.5 Departures from egglog, stated once

1. No deletion and no subsumption in the API (D33).
2. Merges are lattices (associative, commutative, idempotent) or `AssertEq`; there is no `:merge old`/`new`.
3. Sets hold values only, never classes; sets are base values, not containers (D20).
4. Actions never read. A function value must be matched in the body; reading `dom` in a head is a definition error
   (invariant Q).
5. Scoped atoms (value-only, D1) and erasing rulesets (D12) are additions.
6. `push`/`pop` become child scopes (R2).
7. No proofs or explanations (D41), no function defaults (a function lookup without a row fails), no global `let`, no
   `.egg` frontend (D14), no rule callbacks (D21).
8. Relations are unit-valued functions, not constructors over a fresh sort (D27); this is invisible to users.

---

## 3. Public types

House style (conventions): one type per file (partial classes may span files), no nested types, `net8.0;net10.0`, C#
13, nullable enabled, implicit usings disabled, 4-space indent, braces on new lines, `== false` rather than `!`, sealed
classes unless they are a base for typed handles, XML docs on every public and internal member. All files are in
namespace `Eggnog`; folders only organise files.

The listings below give every public member with its signature, a one-line summary where the name does not say enough,
and its milestone in a trailing comment. A member without a milestone comment belongs to its type's milestone, given
after the file name.

### 3.1 Graph/

```csharp
// Graph/EGraph.cs  [R1a]
/// A theory and its database: column types, tables, rulesets, scope tags, split rules, queries, and the root of a tree
/// of scopes. Holds only managed memory; not disposable.
/// Threads (§6.12): rule, query, split and tag definitions may be made from any thread at any time and take part from
/// the next iteration of every run. A table definition may be made from any thread once the root is frozen (R2); while
/// it is not, it throws InvalidOperationException if another thread is using the root at that moment. From R2, a rule
/// definition whose first scoped read of a table creates a mirror table counts as a table definition.
public sealed class EGraph
{
    public EGraph(EGraphOptions? options = null);
    public ValueStore Values { get; }
    /// The root scope. In R2 and later, freeze it empty so that containers can be sibling children.
    public Scope Root { get; }
    /// The built-in nullary relation "this scope has no model". Role Fact; a lift never copies it as a row; a case
    /// holding it is vacuous.
    public Relation Contradiction { get; }
    /// Every table, in declaration order (snapshot). Mirror tables are not listed.
    public IReadOnlyList<TableHandle> Tables { get; }
    public IReadOnlyList<Ruleset> Rulesets { get; }
    /// The root's tag, named "root". Every graph has its own.
    public ScopeTag RootTag { get; }                                                                         // R2
    /// Every tag of this graph, the root's first (snapshot).
    public IReadOnlyList<ScopeTag> ScopeTags { get; }                                                        // R2
    public IReadOnlyList<SplitRule> SplitRules { get; }                                                      // R3

    /// Registers TSort as an e-class sort. ArgumentException if TSort or name is taken.
    public ColumnType<TSort> DefineSort<TSort>(string name) where TSort : struct, ISort<TSort>;

    // Relations. Names are unique per graph, non-empty, contain no '@' and do not start with '$' (ArgumentException).
    public Relation DefineRelation(string name, TableOptions? options = null);
    public Relation<T1> DefineRelation<T1>(string name, TableOptions? options = null);
    public Relation<T1, T2> DefineRelation<T1, T2>(string name, TableOptions? options = null);
    public Relation<T1, T2, T3> DefineRelation<T1, T2, T3>(string name, TableOptions? options = null);
    public Relation<T1, T2, T3, T4> DefineRelation<T1, T2, T3, T4>(string name, TableOptions? options = null);

    // Functions with a value output. TOut must not be a sort (ArgumentException: "use DefineConstructor"). With no
    // lattice, two different values for one key are an error (D32 for host writes, D31 for rules).
    public Function<TOut> DefineFunction<TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, TOut> DefineFunction<T1, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, TOut> DefineFunction<T1, T2, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, T3, TOut> DefineFunction<T1, T2, T3, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, T3, T4, TOut> DefineFunction<T1, T2, T3, T4, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);

    // Constructors (terms). cost >= 0 (ArgumentOutOfRangeException). TSort must have been defined with DefineSort.
    public Constructor<TSort> DefineConstructor<TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, TSort> DefineConstructor<T1, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, TSort> DefineConstructor<T1, T2, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, T3, TSort> DefineConstructor<T1, T2, T3, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, T3, T4, TSort> DefineConstructor<T1, T2, T3, T4, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;

    /// A named ruleset; names are unique per graph.
    public Ruleset DefineRuleset(string name);
    /// A ruleset whose rules are erasing under options.Licence unless they call ReadsOwnFacts (D12).
    public Ruleset DefineRuleset(string name, RulesetOptions options);                                      // R2
    /// A compiled conjunctive query for host reads (Scope.Any, Count, ForEachMatch). RuleDefinitionException on errors,
    /// a class constant that is default(ClassId) or of another graph included. Names are unique among live queries
    /// (ArgumentException); a disposed query's name may be defined again. Define reusable queries once; dispose a query
    /// made for one request (D49).
    public Query DefineQuery(string name, Action<QueryBuilder> define);                                    // R1b
    /// A scope tag of this graph. Names are unique per graph and non-empty; "root" is the root's (ArgumentException).
    public ScopeTag DefineScopeTag(string name);                                                            // R2
    /// A split rule, compiled immediately (RuleDefinitionException). caseTag is the tag of the cases it makes (null:
    /// untagged); ArgumentException for another graph's tag.
    public SplitRule DefineSplit(string name, Action<SplitBuilder> define, ScopeTag? caseTag = null);        // R3
    /// The domain split: where every guard holds of k and domain(k) = d with |d| > 1, one case per v in d assuming
    /// domain(k) = {v}; key k. At least one guard (ArgumentException). Coverage obligation: domain(k) = S must mean
    /// "wherever every guard holds of k, k's value is one of S". More guards split less: fewer cases and fewer facts,
    /// never an unsound one (D43).
    public SplitRule DefineDomainSplit<TKey, TElement>(string name, Function<TKey, ValueSet<TElement>> domain,
        IReadOnlyList<Relation<TKey>> guards, ScopeTag? caseTag = null) where TElement : notnull;             // R3
}

// Graph/EGraphOptions.cs  [R1a]
public sealed class EGraphOptions
{
    public static EGraphOptions Default { get; }
    /// Rule tasks and rebuilds of the root, and of children of writable scopes, which inherit it (D46). 1, the default:
    /// serial and deterministic, extraction ties included (decision 4). More: facts are equal modulo renaming of classes,
    /// but fresh ids, and so extraction ties, may differ between runs. Must be >= 1 (ArgumentOutOfRangeException at
    /// EGraph construction).
    public int DegreeOfParallelism { get; init; } = 1;
    /// Records each rule's query plan in RuleStatistics.Plan (the engine's plan report level). Off by default.
    public bool RecordPlans { get; init; }                                                                   // R1b
}

// Graph/Scope.cs  [R1a]  (partial: identity and lifetime)
/// One engine context with the rule layer's state for it. Not thread-safe (§2.4, D47).
public sealed partial class Scope : IDisposable
{
    public EGraph Graph { get; }
    public bool IsRoot { get; }
    /// Set after a RuleEvaluationException from a run or a write, or a fault the engine recorded (D18).
    public bool IsFaulted { get; }
    /// What faulted the scope; null while it is not faulted. Every later call except Dispose throws
    /// InvalidOperationException with this as InnerException.
    public Exception? Failure { get; }
    /// True once Dispose has been called, even while the release waits for live children (D40). Never true for the root.
    public bool IsDisposed { get; }
    /// The engine's change stamp (X30): moves on every effective change a call of this scope makes to its own layer,
    /// including (R2) the catch-up copies that hide an ancestor's rows when one of this scope's classes merges in that
    /// call. Queries, reads and writes that change nothing never move it; neither does the upkeep a call makes first:
    /// a mirror sync (N62), or the catch-up with what a writable ancestor wrote since (N87).
    public long Version { get; }
    /// "root", "root/schema#2/query#7": for messages.
    public string Path { get; }
    /// Never throws (D40). Makes the scope unusable at once and invalidates the class values it returned; releases its
    /// local rules and engine context when it has no live children and no call runs in its thread domain on another
    /// thread. A second call does nothing; on the root it does nothing.
    public void Dispose();
    public override string ToString();
}

// Graph/Scope.Tree.cs  [R2]  (partial: the tree and local rules)
public sealed partial class Scope
{
    public Scope? Parent { get; }
    public ScopeTag? Tag { get; }
    public bool IsFrozen { get; }
    /// Live children made by CreateChild, a CaseSet or a split, in creation order (snapshot). Transient children made
    /// for queries on a frozen scope are not listed (D34).
    public IReadOnlyList<Scope> Children { get; }
    /// Rules defined on this scope, in definition order (snapshot).
    public IReadOnlyList<Rule> LocalRules { get; }
    /// A child scope. Thread-safe on a frozen scope. ArgumentException for another graph's tag or for RootTag (N72);
    /// InvalidOperationException at depth 31 (colors X36), and from a run observer (a structural call, N81).
    public Scope CreateChild(ScopeTag? tag = null, ScopeOptions? options = null);
    /// Brings this scope's mirrors current, then freezes it (colors §10.8). The parent must be frozen. Local rules
    /// are fixed from here on. A second call does nothing. InvalidOperationException from a run observer or from
    /// inside another call of the scope's thread domain (N79).
    public void Freeze();
    /// A rule active in this scope and its descendants, scheduled with ruleset, whose licence it takes (D12). Enters
    /// this scope's thread domain: InvalidOperationException from another thread while that domain is in use, or if
    /// the scope is frozen; RuleDefinitionException on definition errors.
    public Rule DefineRule(Ruleset ruleset, string name, Action<RuleBuilder> define);
}

// Graph/Scope.Writes.cs  [R1a]  (partial: host writes; each call is one atomic batch, D30)
public sealed partial class Scope
{
    /// Builds a ground term with get-or-add and returns its class, canonical after the batch. Constants are interned.
    /// ArgumentException if a subterm reads a function (Get it first), uses a computation or a variable, is scoped, or
    /// holds a class value this scope cannot use (D39).
    public TSort Make<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>;
    /// Writes ground facts (§6.6.2): relation rows; F.Of(k).Is(v) (merged by F's lattice); C.Of(k).Is(x) (unions
    /// outputs); a.Is(b) of a sort (union). FactConflictException when F has no lattice and holds another value, or
    /// when a merge the call causes would give such a function two values for one key (D32); ArgumentException for
    /// value equalities that are not function writes, scoped handles, filters, function reads and computations. Nothing
    /// is written when a fact fails.
    public void Assume(params Fact[] facts);
    /// true where the classes were distinct before the call. FactConflictException as for Assume (D32).
    public bool Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
    /// Several writes as one atomic batch (§6.6.1): each call is checked as it is made, and the batch is staged,
    /// flushed and rebuilt once, after write returns. If write throws or a call is rejected, nothing is written and the
    /// class values the batch's Make calls returned are invalid (D39). An exception write lets escape propagates; a
    /// rejection write caught still rejects the batch, and Write then returns normally without writing (Version does
    /// not move). While write runs, this scope's own members throw InvalidOperationException: use the ScopeWriter; and
    /// its writable ancestors refuse writes and runs, which would change what the batch's calls were checked against
    /// (N88). Other calls of the domain are allowed; the batch catches up with them before each of its calls and before
    /// it commits, without moving Version (N87).
    public void Write(Action<ScopeWriter> write);
}

// Graph/Scope.Reads.cs  [R1a]  (partial: host reads; never create terms, never intern, D15)
public sealed partial class Scope
{
    /// Whether Contradiction holds here.
    public bool IsContradictory { get; }
    /// Whether every ground fact holds. Scoped handles (R2) read the resolved ancestor directly. A constant never
    /// interned makes its fact false.
    public bool Check(params Fact[] facts);
    /// The value of a ground term: a function's value, or the class of a constructor term. InvalidOperationException
    /// if absent.
    public T Get<T>(Term<T> term);
    public bool TryGet<T>(Term<T> term, [MaybeNullWhen(false)] out T value);
    /// The canonical member of value's class here.
    public TSort Find<TSort>(TSort value) where TSort : struct, ISort<TSort>;
    public bool AreEqual<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
    /// false if b's term does not exist here.
    public bool AreEqual<TSort>(TSort a, Term<TSort> b) where TSort : struct, ISort<TSort>;
    /// The number of rows of table in this scope's view (its own and inherited ones). Replaces egglog's print-size.
    public long CountRows(TableHandle table);                                                                 // R1b
    /// The rows of table in this scope's view, decoded and in content order: for debugging and tests (§6.11).
    public IReadOnlyList<TableRow> Rows(TableHandle table);                                                   // R1b
    public bool Any(Query query);                                                                             // R1b
    /// Distinct full bindings.
    public long Count(Query query);                                                                           // R1b
    /// Matches in content order (§6.6.4); return false from the callback to stop. The matches are collected and ordered
    /// before the first callback, so the callback may call the scope, writes and runs included. A class a match returns
    /// is canonical when it is read, so after a union an earlier callback made, two matches may name one class.
    public void ForEachMatch(Query query, QueryCallback callback);                                            // R1b
}

// Graph/Scope.Engine.cs  [R1a]  (partial: runs and extraction)
public sealed partial class Scope
{
    /// Runs a schedule in this scope (§6.5). Validates first, before any work: ArgumentOutOfRangeException for a
    /// negative IterationLimit, TimeLimit or RowLimit; ArgumentException for a ruleset of another graph, or an Until
    /// fact this scope cannot read (a table or column type of another graph, a class it cannot use, D39); and (R2) that
    /// every tag read by a reachable rule resolves. InvalidOperationException if frozen (R2), faulted, or called from a
    /// run observer; OperationCanceledException on cancellation; RuleEvaluationException on an evaluation failure (the
    /// scope is then faulted).
    public RunReport Run(Schedule schedule, RunOptions? options = null, CancellationToken cancellationToken = default);
    /// An extractor over this scope as it is now (§6.10).
    public Extractor CreateExtractor(CostModel? costs = null);
    public ExtractedTerm Extract<TSort>(TSort root, CostModel? costs = null) where TSort : struct, ISort<TSort>;
}

// Graph/Scope.Cases.cs  [R3]  (partial: cases)
public sealed partial class Scope
{
    /// Made by a split or a CaseSet.
    public bool IsCase { get; }
    /// Host-driven cases. Obligation: the cases added must cover this scope (every model of it satisfies some case's
    /// assumptions). InvalidOperationException on a frozen scope (open the cases in a child of it).
    public CaseSet OpenCases();
}

// Graph/ScopeWriter.cs  [R1a]
/// The writes of one Scope.Write batch (D30). Reads are not available. Valid only during the batch
/// (ObjectDisposedException afterwards). Each call enters the scope's guard (D47) and checks EngineCallMarker; a call
/// that throws for any reason, another thread's included, rejects the whole batch (later calls are still checked, but
/// nothing commits).
public sealed class ScopeWriter
{
    public Scope Scope { get; }
    public void Assume(params Fact[] facts);
    /// The class of the term: an existing class, or a fresh id that names the new class once the batch commits. Usable
    /// in later calls of the same batch; use Scope.Find for the canonical member afterwards.
    public TSort Make<TSort>(ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>;
    public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
}

// Graph/ScopeTag.cs  [R2]
/// A label a scope carries; a scoped atom names its ancestor-or-self by tag. Made by EGraph.DefineScopeTag and owned by
/// that graph (D38); compared by reference.
public sealed class ScopeTag
{
    public EGraph Graph { get; }
    public string Name { get; }
    public override string ToString();
}

// Graph/ScopeOptions.cs  [R2]
public sealed class ScopeOptions
{
    public static ScopeOptions Default { get; }
    /// The scope's degree of parallelism (ContextOptions.Parallelism, D46). null: serial for a child of a frozen scope,
    /// the parent's for a child of a writable one (colors §10.9). Must be >= 1.
    public int? DegreeOfParallelism { get; init; }
}

// Graph/ISort{TSort}.cs  [R1a]
/// An e-class sort: a struct wrapping a class, e.g. `readonly record struct Pred(ClassId Id) : ISort<Pred>`. The library
/// calls FromId only with a ClassId it made.
public interface ISort<TSort> where TSort : struct, ISort<TSort>
{
    ClassId Id { get; }
    static abstract TSort FromId(ClassId id);
}

// Graph/ClassId.cs  [R1a]
/// An e-class identifier, as a scope returned it (D39). Only the library makes one: default(ClassId) names no class and
/// every scope rejects it. Usable in the scope that returned it and that scope's descendants, until that scope is
/// disposed. Two ClassIds are equal when they belong to one graph and hold the same identifier; members of one class
/// may differ, so compare classes with Scope.AreEqual. Renders as "#17".
public readonly struct ClassId : IEquatable<ClassId>
{
    /// true for default(ClassId).
    public bool IsDefault { get; }
    public bool Equals(ClassId other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public static bool operator ==(ClassId left, ClassId right);
    public static bool operator !=(ClassId left, ClassId right);
    public override string ToString();
}
```

### 3.2 Values/

```csharp
// Values/ColumnKind.cs  [R1a]
public enum ColumnKind { Sort, Value }

// Values/ColumnType.cs  [R1a]
/// The type of a column: a sort (its cells are classes) or a value type (its cells are base values). Invariant V:
/// equal values have equal cells in every scope.
public abstract class ColumnType
{
    private protected ColumnType(string name, ColumnKind kind, Type clrType);
    public string Name { get; }
    public ColumnKind Kind { get; }
    public Type ClrType { get; }
    /// The s-expression literal: "\"kind\"", "42", "true", "1.5", "(set-of \"a\" \"b\")", "#17".
    /// ArgumentException if value is not of ClrType.
    public string Format(object value);
    public override string ToString();
}

// Values/ColumnType{T}.cs  [R1a]
public abstract class ColumnType<T> : ColumnType
{
    private protected ColumnType(string name, ColumnKind kind);
    public string Format(T value);
    /// Content order: strings ordinal, numbers numeric, sets lexicographic, classes by id; a registered type by its
    /// order (§6.1). Never interns (D15).
    public int Compare(T x, T y);
}

// Values/ValueStore.cs  [R1a]
/// Column types by CLR type. Built in: long "i64", int "i32", bool "bool", double "f64", string "String",
/// ValueSet<string> "Set<String>", ValueSet<long> "Set<i64>", ValueSet<int> "Set<i32>". Thread-safe.
public sealed class ValueStore
{
    /// ArgumentException naming the Register method to call when T is unknown.
    public ColumnType<T> TypeOf<T>();
    public bool TryGetType<T>([NotNullWhen(true)] out ColumnType<T>? type);
    /// An interned value type over IEquatable<T> (the engine's InternedCodec<T>). order is extraction's tie-break
    /// order and Compare; it must not depend on the culture. ArgumentException if T or name is taken, and for ClassId,
    /// a sort type, or a ValueSet<> type (use RegisterSet).
    public ColumnType<T> Register<T>(string name, IComparer<T>? order = null, Func<T, string>? format = null)
        where T : notnull, IEquatable<T>;
    /// "Set<{element name}>". The element type must be registered and must not be a sort (no sets of classes); it must
    /// be string or implement IComparable<T> or IComparable, the order ValueSet<T> keeps (ArgumentException).
    /// Idempotent.
    public ColumnType<ValueSet<T>> RegisterSet<T>() where T : notnull;
    /// Named typeof(TEnum).Name; stored as i64 (D20). Idempotent.
    public ColumnType<TEnum> RegisterEnum<TEnum>() where TEnum : struct, Enum;
    /// Values interned in every pool of the graph; pools never shrink (E1).
    public long InternedCount { get; }
}

// Values/ValueSet{T}.cs  [R1a]
/// An immutable set in ascending element order (ordinal for strings, Comparer<T>.Default otherwise), equal by
/// content. Operations return this instance when nothing changes.
public sealed class ValueSet<T> : IReadOnlyList<T>, IEquatable<ValueSet<T>> where T : notnull
{
    public static ValueSet<T> Empty { get; }
    public int Count { get; }
    public bool IsEmpty { get; }
    public T this[int index] { get; }
    public bool Contains(T item);
    public bool IsSubsetOf(ValueSet<T> other);
    public bool Overlaps(ValueSet<T> other);
    public ValueSet<T> Add(T item);
    public ValueSet<T> Remove(T item);
    public ValueSet<T> Union(ValueSet<T> other);
    public ValueSet<T> Intersect(ValueSet<T> other);
    public ValueSet<T> Except(ValueSet<T> other);
    public ValueSet<T> Where(Func<T, bool> predicate);
    public bool Equals(ValueSet<T>? other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public IEnumerator<T> GetEnumerator();
    /// {a, b}
    public override string ToString();
}

// Values/ValueSet.cs  [R1a]
public static class ValueSet
{
    /// ValueSet.Of("abc") is {"abc"}: there is deliberately no IEnumerable overload named Of.
    public static ValueSet<T> Of<T>(params T[] items) where T : notnull;
    public static ValueSet<T> From<T>(IEnumerable<T> items) where T : notnull;
}

// Values/ValueLattice.cs  [R1a]
/// The untyped base of a function's lattice, and the built-in lattices.
public abstract class ValueLattice
{
    private protected ValueLattice(string name, Type valueType);
    public string Name { get; }
    public Type ValueType { get; }
    /// Merge ∩, CaseJoin ∪ (dom, types).
    public static ValueLattice<ValueSet<T>> Intersect<T>() where T : notnull;
    /// Merge ∪, CaseJoin ∩.
    public static ValueLattice<ValueSet<T>> Union<T>() where T : notnull;
    /// Merge max, CaseJoin min (a lower bound).
    public static ValueLattice<T> Max<T>() where T : IComparable<T>;
    /// Merge min, CaseJoin max (an upper bound).
    public static ValueLattice<T> Min<T>() where T : IComparable<T>;
    public override string ToString();
}

// Values/ValueLattice{T}.cs  [R1a]
/// A merge for a function's values, with the case join a lift uses. Contract: Merge is associative, commutative and
/// idempotent; Merge(a, CaseJoin(a, b)) == a (absorption: the join claims no more than either case). Both must be
/// pure and thread-safe, and must not call into any graph (D47).
public sealed class ValueLattice<T> : ValueLattice
{
    public ValueLattice(string name, Func<T, T, T> merge, Func<T, T, T>? caseJoin = null);
    public Func<T, T, T> Merge { get; }
    /// null: a lift requires every live case to agree.
    public Func<T, T, T>? CaseJoin { get; }
    /// Checks the laws on every pair and triple of samples; InvalidOperationException naming the law and the values.
    public void Verify(IEnumerable<T> samples);
}
```

### 3.3 Tables/

```csharp
// Tables/TableKind.cs  [R1a]
public enum TableKind { Relation, Function, Constructor }

// Tables/TableRole.cs  [R1a]
/// What rows mean. Term: constructor rows. Fact: claims (lifted by default; erasing rules must read them scoped).
/// Analysis: derived from terms only (never lifted by default; writers may read only Term and Analysis tables).
public enum TableRole { Term, Fact, Analysis }

// Tables/TableOptions.cs  [R1a]
public sealed class TableOptions
{
    /// Relations and functions: Fact or Analysis (Term is rejected). Ignored for constructors, which are Term.
    public TableRole Role { get; init; } = TableRole.Fact;
    /// Whether lifts copy this table into the parent. null: Fact true, Analysis false, constructors true (term lift).
    public bool? Lift { get; init; }
    /// Constructors only: false makes extraction skip its rows.
    public bool Extractable { get; init; } = true;
}

// Tables/TableHandle.cs  [R1a]
public abstract class TableHandle
{
    private protected TableHandle();
    public EGraph Graph { get; }
    public string Name { get; }
    /// Declaration order; extraction breaks ties by it.
    public int Index { get; }
    public TableKind Kind { get; }
    public TableRole Role { get; }
    public IReadOnlyList<ColumnType> Keys { get; }
    /// null for a relation.
    public ColumnType? Output { get; }
    public bool Lift { get; }
    /// Every key, and the output, is a value type: the table may be read scoped (D1).
    public bool IsValueOnly { get; }
    /// Set on a read-only scoped view made by In(tag); such a handle is rejected in any write.
    public ScopeTag? ReadScope { get; }                                                                       // R2
    /// "dom", or "dom@schema" for a scoped view.
    public override string ToString();
}

// Tables/RelationHandle.cs  [R1a]
/// The base of the typed relations. Its untyped members are internal (D13).
public abstract class RelationHandle : TableHandle { private protected RelationHandle(); }

// Tables/FunctionHandle.cs  [R1a]
public abstract class FunctionHandle : TableHandle
{
    private protected FunctionHandle();
    /// null when the function has no lattice.
    public ValueLattice? Lattice { get; }
}

// Tables/ConstructorHandle.cs  [R1a]
public abstract class ConstructorHandle : TableHandle
{
    private protected ConstructorHandle();
    public long Cost { get; }
    public bool Extractable { get; }
}
```

Typed handles, one file each. Every relation and function type also has `In(ScopeTag scope)` [R2], the one name for a
scoped view: it returns the same type with `ReadScope` set (`ArgumentException` unless `IsValueOnly`, or for another
graph's tag). A scoped view keeps its handle type, so it composes with helpers that take handles; a write through it is
a `RuleDefinitionException` at definition (§6.3 step 8) or an `ArgumentException` in a host write (§6.6.2). Constructors
have no `In`: their output is a sort (D1). A constructor's `Of` returns a `ConstructorTerm<TSort>`, which is what `Make`
takes.

```csharp
// Tables/Relation.cs                      [R1a]
public sealed class Relation : RelationHandle
{ public Fact Of(); public Relation In(ScopeTag scope); /* R2 */ }
// Tables/Relation{T1}.cs                  [R1a]
public sealed class Relation<T1> : RelationHandle
{ public Fact Of(Term<T1> a); public Relation<T1> In(ScopeTag scope); /* R2 */ }
// Tables/Relation{T1,T2}.cs               [R1a]
public sealed class Relation<T1, T2> : RelationHandle
{ public Fact Of(Term<T1> a, Term<T2> b); public Relation<T1, T2> In(ScopeTag scope); /* R2 */ }
// Tables/Relation{T1,T2,T3}.cs            [R1a]
public sealed class Relation<T1, T2, T3> : RelationHandle
{ public Fact Of(Term<T1> a, Term<T2> b, Term<T3> c); public Relation<T1, T2, T3> In(ScopeTag scope); /* R2 */ }
// Tables/Relation{T1,T2,T3,T4}.cs         [R1a]
public sealed class Relation<T1, T2, T3, T4> : RelationHandle
{ public Fact Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); public Relation<T1, T2, T3, T4> In(ScopeTag scope); /* R2 */ }

// Tables/Function{TOut}.cs                [R1a]
public sealed class Function<TOut> : FunctionHandle
{ public new ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(); public Function<TOut> In(ScopeTag scope); /* R2 */ }
// Tables/Function{T1,TOut}.cs             [R1a]
public sealed class Function<T1, TOut> : FunctionHandle
{ public new ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a); public Function<T1, TOut> In(ScopeTag scope); /* R2 */ }
// Tables/Function{T1,T2,TOut}.cs          [R1a]
public sealed class Function<T1, T2, TOut> : FunctionHandle
{ public new ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b); public Function<T1, T2, TOut> In(ScopeTag scope); /* R2 */ }
// Tables/Function{T1,T2,T3,TOut}.cs       [R1a]
public sealed class Function<T1, T2, T3, TOut> : FunctionHandle
{ public new ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b, Term<T3> c); public Function<T1, T2, T3, TOut> In(ScopeTag scope); /* R2 */ }
// Tables/Function{T1,T2,T3,T4,TOut}.cs    [R1a]
public sealed class Function<T1, T2, T3, T4, TOut> : FunctionHandle
{ public new ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); public Function<T1, T2, T3, T4, TOut> In(ScopeTag scope); /* R2 */ }

// Tables/Constructor{TSort}.cs ... Constructor{T1,T2,T3,T4,TSort}.cs   [R1a]   (all: where TSort : struct, ISort<TSort>)
public sealed class Constructor<TSort> : ConstructorHandle { public ConstructorTerm<TSort> Of(); }
public sealed class Constructor<T1, TSort> : ConstructorHandle { public ConstructorTerm<TSort> Of(Term<T1> a); }
public sealed class Constructor<T1, T2, TSort> : ConstructorHandle { public ConstructorTerm<TSort> Of(Term<T1> a, Term<T2> b); }
public sealed class Constructor<T1, T2, T3, TSort> : ConstructorHandle { public ConstructorTerm<TSort> Of(Term<T1> a, Term<T2> b, Term<T3> c); }
public sealed class Constructor<T1, T2, T3, T4, TSort> : ConstructorHandle { public ConstructorTerm<TSort> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); }
```

### 3.4 Terms/

```csharp
// Terms/Term.cs  [R1a]
/// An expression: a variable, a constant, a table application, or a computation. Immutable; renders as an
/// s-expression. The untyped constant (Term.Constant) is internal (D13).
public abstract class Term
{
    private protected Term();
    public abstract Type ClrType { get; }
    public override string ToString();
}

// Terms/Term{T}.cs  [R1a]
public abstract class Term<T> : Term
{
    private protected Term();
    /// Equality: a join or a check in a body, a write or a union in a head, a ground fact in a host write or check.
    public Fact Is(Term<T> other);
    /// Disequality: a filter, in a body only (RuleDefinitionException in a head; ArgumentException in a host write or
    /// check).
    public Fact IsNot(Term<T> other);
    /// A constant; for a sort, a class constant, checked where it is used (D39).
    public static implicit operator Term<T>(T value);
}

// Terms/ConstructorTerm{TSort}.cs  [R1a]
/// An application of a constructor: what Scope.Make, ScopeWriter.Make and RuleBuilder.Make take.
public sealed class ConstructorTerm<TSort> : Term<TSort> where TSort : struct, ISort<TSort>
{
    internal ConstructorTerm(ConstructorHandle head, Term[] arguments);
    public ConstructorHandle Head { get; }
}

// Terms/Var{T}.cs  [R1a]
/// A variable of one builder; using it in another builder is a RuleDefinitionException.
public sealed class Var<T> : Term<T>
{
    internal Var(object owner, string name);
    public string Name { get; }
}

// Terms/Fact.cs  [R1a]
public abstract class Fact
{
    private protected Fact();
    /// A function or constructor application has a row.
    public static Fact Exists(Term application);
    /// A filter on a boolean computation.
    public static Fact True(Term<bool> condition);
    /// Scopes every table application inside facts (nested ones included) to the ancestor-or-self tagged scope.
    /// Nesting, or a handle already scoped to a different tag, is a definition error; so is a constructor inside.
    /// ArgumentException for another graph's tag.
    public static Fact In(ScopeTag scope, params Fact[] facts);                                               // R2
    public override string ToString();
}

// Terms/Compute.cs  [R1a]
/// Computed values from C# lambdas (they render as <lambda>); prefer named primitives where rendering matters.
/// Lambdas must be pure, deterministic and thread-safe, and must not call into any graph.
public static class Compute
{
    public static Term<TResult> Of<T1, TResult>(Term<T1> a, Func<T1, TResult> function);
    public static Term<TResult> Of<T1, T2, TResult>(Term<T1> a, Term<T2> b, Func<T1, T2, TResult> function);
    public static Term<TResult> Of<T1, T2, T3, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, TResult> function);
    public static Term<TResult> Of<T1, T2, T3, T4, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, TResult> function);
}

// Terms/Primitive.cs  [R1a]
/// A pure, deterministic, thread-safe function over values (never over classes). An exception thrown by it fails the
/// rule (D31), except the "no result" of the built-in partial primitives in a body let; a call into any graph from it
/// throws (D47).
public abstract class Primitive
{
    private protected Primitive();
    public string Name { get; }
    public IReadOnlyList<Type> ParameterTypes { get; }
    public Type ResultType { get; }
    public static Primitive<T1, TResult> Create<T1, TResult>(string name, Func<T1, TResult> function);
    public static Primitive<T1, T2, TResult> Create<T1, T2, TResult>(string name, Func<T1, T2, TResult> function);
    public static Primitive<T1, T2, T3, TResult> Create<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> function);
    public static Primitive<T1, T2, T3, T4, TResult> Create<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> function);
    public override string ToString();
}

// Terms/Primitive{T1,TResult}.cs ... Primitive{T1,T2,T3,T4,TResult}.cs  [R1a]
public sealed class Primitive<T1, TResult> : Primitive { public Func<T1, TResult> Function { get; } public Term<TResult> Of(Term<T1> a); }
public sealed class Primitive<T1, T2, TResult> : Primitive { public Func<T1, T2, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b); }
public sealed class Primitive<T1, T2, T3, TResult> : Primitive { public Func<T1, T2, T3, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b, Term<T3> c); }
public sealed class Primitive<T1, T2, T3, T4, TResult> : Primitive { public Func<T1, T2, T3, T4, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); }

// Terms/Primitives.cs  [R1a]
/// Named built-ins (egglog names), memoised per T. Add and Subtract are partial, as egglog's checked + and - are: an
/// overflow in a body let drops the match and records nothing; in a head it fails the rule (D31).
public static class Primitives
{
    public static Primitive<T, ValueSet<T>> Singleton<T>() where T : notnull;                          // set-of
    public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Union<T>() where T : notnull;       // set-union
    public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Intersect<T>() where T : notnull;   // set-intersect
    public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Except<T>() where T : notnull;      // set-diff
    public static Primitive<ValueSet<T>, T, ValueSet<T>> Insert<T>() where T : notnull;                // set-insert
    public static Primitive<ValueSet<T>, T, ValueSet<T>> Remove<T>() where T : notnull;                // set-remove
    public static Primitive<ValueSet<T>, T, bool> Contains<T>() where T : notnull;                     // set-contains
    public static Primitive<ValueSet<T>, ValueSet<T>, bool> IsSubsetOf<T>() where T : notnull;         // set-subset
    public static Primitive<ValueSet<T>, bool> IsEmpty<T>() where T : notnull;                         // set-empty
    public static Primitive<ValueSet<T>, long> Count<T>() where T : notnull;                           // set-length
    public static Primitive<long, long, long> Add { get; }                                             // +
    public static Primitive<long, long, long> Subtract { get; }                                        // -
    public static Primitive<long, long, long> Max { get; }                                             // max
    public static Primitive<long, long, long> Min { get; }                                             // min
    public static Primitive<long, long, bool> Less { get; }                                            // <
    public static Primitive<long, long, bool> LessOrEqual { get; }                                     // <=
    public static Primitive<long, long, bool> Greater { get; }                                         // >
    public static Primitive<long, long, bool> GreaterOrEqual { get; }                                  // >=
}
```

### 3.5 Rules/

```csharp
// Rules/Ruleset.cs  [R1a]
public sealed class Ruleset
{
    public EGraph Graph { get; }
    public string Name { get; }
    /// The tag under which the ruleset's rules are erasing (D12); null for none.
    public ScopeTag? Licence { get; }                                                                         // R2
    /// Global rules, in definition order (snapshot).
    public IReadOnlyList<Rule> Rules { get; }
    /// A global rule, compiled immediately (RuleDefinitionException). Any thread, any time (D6).
    public Rule DefineRule(string name, Action<RuleBuilder> define);
    public static implicit operator Schedule(Ruleset ruleset);
    public override string ToString();
}

// Rules/RulesetOptions.cs  [R2]
public sealed class RulesetOptions
{
    /// Every rule of the ruleset is erasing under this tag unless it calls ReadsOwnFacts (D12). ArgumentException for
    /// another graph's tag.
    public ScopeTag? Licence { get; init; }
}

// Rules/Rule.cs  [R1a]
public sealed class Rule
{
    public string Name { get; }
    public Ruleset Ruleset { get; }
    /// null for a global rule (always null in R1).
    public Scope? Owner { get; }
    public bool IsNaive { get; }
    /// The rule's effective licence: its own Erasing tag, else its ruleset's; null when ReadsOwnFacts exempts it.
    public ScopeTag? Licence { get; }                                                                         // R2
    /// The reason given to ReadsOwnFacts; null when the rule does not use it.
    public string? OwnFactsReason { get; }                                                                    // R2
    /// Distinct tags the rule's scoped atoms read, in first-use order.
    public IReadOnlyList<ScopeTag> ReadScopes { get; }                                                        // R2
    /// (rule ((= s (inset e p)) (present@schema p) ...) ((union e (PTrue))) :ruleset rewrite :erasing schema), or
    /// ... :reads-own-facts "the guard stays in the predicate") for an exempt rule.
    public override string ToString();
}

// Rules/RuleBuilder.cs  [R1a]
/// Collects one rule: When/Where make the body; Then/Make/Rewrite make the head. Body calls are unordered; head items
/// are lowered in the order the builder calls were made (§6.3).
public sealed class RuleBuilder
{
    public string Name { get; }
    public Var<T> Var<T>(string name);
    public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2);
    public (Var<T1>, Var<T2>, Var<T3>) Vars<T1, T2, T3>(string name1, string name2, string name3);
    public (Var<T1>, Var<T2>, Var<T3>, Var<T4>) Vars<T1, T2, T3, T4>(string name1, string name2, string name3, string name4);
    public void When(params Fact[] facts);
    public void Where<T1>(Term<T1> a, Func<T1, bool> predicate);
    public void Where<T1, T2>(Term<T1> a, Term<T2> b, Func<T1, T2, bool> predicate);
    public void Where<T1, T2, T3>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, bool> predicate);
    public void Where<T1, T2, T3, T4>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, bool> predicate);
    /// e.g. Primitives.Contains<string>().Of(d, v)
    public void Where(Term<bool> condition);
    public void Then(params Fact[] facts);
    /// A head variable bound by get-or-add of term; it may not appear in the body.
    public Var<TSort> Make<TSort>(string name, ConstructorTerm<TSort> term) where TSort : struct, ISort<TSort>;
    /// Body: a fresh class variable t with t = left; head: t = right (union).
    public void Rewrite<TSort>(Term<TSort> left, Term<TSort> right) where TSort : struct, ISort<TSort>;
    /// Every run matches in full (a naive bridge rule; never inherits).
    public void Naive();
    /// This rule is erasing under licence, whatever its ruleset says (§6.3, step 6). ArgumentException for another
    /// graph's tag.
    public void Erasing(ScopeTag licence);                                                                    // R2
    /// Exempts this rule from its licence: it may read fact tables in the running scope. reason (non-empty) says why
    /// that is sound, and Rule.ToString renders it. RuleDefinitionException when the rule has no licence.
    public void ReadsOwnFacts(string reason);                                                                 // R2
}

// Rules/Query.cs  [R1b]
/// A compiled conjunctive query (D34, D49). Define a reusable query once; dispose one made for a single request.
public sealed class Query : IDisposable
{
    /// Unique among the graph's live queries.
    public string Name { get; }
    public IReadOnlyList<Term> Variables { get; }
    public bool IsDisposed { get; }
    /// Never throws and never waits. Frees the query's engine rule and call sites once no call is using it (handed to
    /// the graph, which frees them before it next runs an iteration or a query, when an engine call holds the R1 engine
    /// lock or the call comes from a user delegate); later use throws ObjectDisposedException; the name may be defined
    /// again.
    public void Dispose();
    /// (query ((present p) (= d (dom p))) (p d)): the body, then the variables.
    public override string ToString();
}

// Rules/QueryBuilder.cs  [R1b]
public sealed class QueryBuilder
{
    public Var<T> Var<T>(string name);
    public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2);
    public (Var<T1>, Var<T2>, Var<T3>) Vars<T1, T2, T3>(string name1, string name2, string name3);
    public void When(params Fact[] facts);
    public void Where<T1>(Term<T1> a, Func<T1, bool> predicate);
    public void Where<T1, T2>(Term<T1> a, Term<T2> b, Func<T1, T2, bool> predicate);
    public void Where(Term<bool> condition);
}

// Rules/QueryCallback.cs  [R1b]
/// Return false to stop receiving matches.
public delegate bool QueryCallback(QueryMatch match);

// Rules/QueryMatch.cs  [R1b]
public readonly ref struct QueryMatch
{
    /// Decoded; a class is canonical in the queried scope when it is read (one engine Find per class read, so a union
    /// an earlier callback made is seen) and has that scope as its origin (D39). Valid only during the callback.
    public T Get<T>(Var<T> variable);
}

// Rules/RuleDefinitionException.cs  [R1a]
public sealed class RuleDefinitionException : Exception
{
    public RuleDefinitionException(string ruleName, string message, Exception? inner = null);
    public string RuleName { get; }
}

// Rules/RuleEvaluationException.cs  [R1a]
/// An evaluation failure: a primitive, filter or lattice delegate threw, or a function without a lattice received two
/// values from rules. After a run or a write the scope is faulted (D18); after a query it is not (D49). For a
/// primitive, Message is "rule 'r': primitive 'p' threw {exception type}: {its message}".
public sealed class RuleEvaluationException : Exception
{
    public RuleEvaluationException(string? ruleName, string? bindings, string message, Exception? inner);
    /// null when the failure happened in a merge, which no single rule owns.
    public string? RuleName { get; }
    /// The primitive's arguments by source name, e.g. "d={a, b} v=\"b\"": a set as ValueSet<T>.ToString renders it,
    /// any other value as its s-expression literal; null for merge failures.
    public string? Bindings { get; }
}

// Rules/FactConflictException.cs  [R1a]
/// A host write that would give a function without a lattice a second value for one key, against the stored value,
/// another write of the same batch, or a row that a merge of the batch makes congruent with it (D32). Nothing is
/// written.
public sealed class FactConflictException : InvalidOperationException
{
    public FactConflictException(TableHandle table, IReadOnlyList<object> key, object storedValue, object newValue);
    public TableHandle Table { get; }
    /// Decoded keys.
    public IReadOnlyList<object> Key { get; }
    /// The stored value, or the batch's earlier one.
    public object StoredValue { get; }
    public object NewValue { get; }
}

// Rules/SplitRule.cs  [R3]
public sealed class SplitRule
{
    public string Name { get; }
    /// null: its cases are untagged.
    public ScopeTag? CaseTag { get; }
    public IReadOnlyList<ScopeTag> ReadScopes { get; }
    public override string ToString();
}

// Rules/SplitBuilder.cs  [R3]
/// Body (When/Where), the element set to split over (Cases), the key (default: every named body variable except the
/// set variable), and the assumptions written into each case. Obligation: whenever the body matches in a scope C, C
/// entails "for the elements of the set at this match, one of the cases' assumptions holds", and that stays true as C
/// grows (it does for a lattice-meet domain). The body is also the split's demand (D43): keys it does not match are
/// never split.
public sealed class SplitBuilder
{
    public Var<T> Var<T>(string name);
    public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2);
    public (Var<T1>, Var<T2>, Var<T3>) Vars<T1, T2, T3>(string name1, string name2, string name3);
    public void When(params Fact[] facts);
    public void Where<T1>(Term<T1> a, Func<T1, bool> predicate);
    public void Where<T1, T2>(Term<T1> a, Term<T2> b, Func<T1, T2, bool> predicate);
    public void Where(Term<bool> condition);
    /// One case per element of set; the returned variable is bound to that case's element in Assume.
    public Var<T> Cases<T>(string name, Var<ValueSet<T>> set) where T : notnull;
    public void Key(params Term[] variables);
    public void Assume(params Fact[] facts);
}
```

### 3.6 Scheduling/

```csharp
// Scheduling/Schedule.cs  [R1a]
/// egglog schedules, plus case splits.
public abstract class Schedule
{
    private protected Schedule();
    /// One iteration over the union of the rulesets' rules active in the scope.
    public static Schedule Run(params Ruleset[] rulesets);
    /// Up to times passes of Sequence(steps); stops after a pass with no change.
    public static Schedule Repeat(int times, params Schedule[] steps);
    /// Passes of Sequence(steps) until one changes nothing.
    public static Schedule Saturate(params Schedule[] steps);
    public static Schedule Sequence(params Schedule[] steps);
    /// Runs body unless every fact of condition already holds (egglog :until), checked as a host check (§6.6.3) each
    /// time the node executes. ArgumentException for no fact (a forgotten condition would make the step a silent
    /// no-op), or a fact that is not a ground host check: a filter (IsNot, Fact.True), or a term with a variable or a
    /// computation. Scope.Run checks, before any work, that every fact can be read in its scope.
    public static Schedule Until(Schedule body, params Fact[] condition);                                    // R1b
    /// A split step (§6.9.3). Run it once the facts its rules read have settled, as in
    /// Saturate(Saturate(horn, lattice), Split(rule, inCase), rewrite): a split made before its domain narrowed opens
    /// cases that go vacuous later, and its record keeps the key until the Run ends, so later steps do not split it
    /// again more narrowly (D43).
    public static Schedule Split(SplitRule rule, Schedule inCase);                                           // R3
    public static Schedule Split(IReadOnlyList<SplitRule> rules, Schedule inCase);                           // R3
    public override string ToString();
}

// Scheduling/RunOptions.cs  [R1a]
/// Limits are per top-level Run, cases included, checked before every iteration (the first included) and split step;
/// a run overshoots a limit or a cancellation by at most one engine iteration or one lift (D42). A negative limit is an
/// ArgumentOutOfRangeException from Run, before any work.
public sealed class RunOptions
{
    public static RunOptions Default { get; }
    /// Engine iterations in every scope of the run, cases included; mirror syncs do not count.
    public int IterationLimit { get; init; } = 10_000;
    /// Top-level scope only; cases always stop (D11).
    public bool StopOnContradiction { get; init; }
    public TimeSpan? TimeLimit { get; init; }                                                                // R1b
    /// Rows in the own layers of the top-level scope and of the run's live cases (the engine's row-count estimate).
    public long? RowLimit { get; init; }                                                                     // R1b
    /// Called on the run's thread after every step (§6.11). It may read the scopes of the run (reads, queries,
    /// CountRows, Rows, extraction); a write, a Run, or any other call that changes a scope throws
    /// InvalidOperationException ("Not allowed inside a run observer").
    public Action<RunStep>? Observer { get; init; }                                                          // R1b
    /// Keep every step in RunReport.Steps.
    public bool RecordSteps { get; init; }                                                                   // R1b
    public int CaseLimit { get; init; } = 64;                                                                // R3
    public int SplitDepthLimit { get; init; } = 4;                                                           // R3
    public LiftOptions Lift { get; init; } = LiftOptions.Default;                                            // R3
}

// Scheduling/RunReport.cs  [R1a]
public sealed class RunReport
{
    /// Engine iterations in every scope of the run, cases included; mirror syncs do not count.
    public int Iterations { get; }
    /// An iteration, split step or lift of the run changed the top-level scope (the engine's change flags, §6.5.1).
    public bool Changed { get; }
    /// Completed, and the run's last iteration or split step in the top-level scope changed nothing.
    public bool Saturated { get; }
    public RunStopReason StopReason { get; }
    public TimeSpan Elapsed { get; }
    /// Every rule that ran, cases included (decision 6: match multiplicity may differ from egglog's).
    public IReadOnlyDictionary<Rule, RuleStatistics> Rules { get; }
    /// The engine's search-and-apply, merge and rebuild times, summed over the run.
    public TimeSpan SearchTime { get; }
    public TimeSpan MergeTime { get; }
    public TimeSpan RebuildTime { get; }
    /// Every step, when RunOptions.RecordSteps is set; otherwise empty.
    public IReadOnlyList<RunStep> Steps { get; }                                                             // R1b
    public int CasesOpened { get; }                                                                          // R3
    /// Over CaseLimit, over SplitDepthLimit, or an empty element set (sound: fewer facts).
    public int SplitsSkipped { get; }                                                                        // R3
    /// Lifts computed (not skipped by the memo).
    public int Lifts { get; }                                                                                // R3
    /// Lift candidates dropped by CandidateLimit.
    public long LiftRowsSkipped { get; }                                                                     // R3
    public override string ToString();
}

// Scheduling/RunStopReason.cs  [R1a]
public enum RunStopReason
{
    Completed,
    IterationLimit,
    Contradiction,
    TimeLimit,                                                                                               // R1b
    RowLimit,                                                                                                // R1b
}
```

### 3.7 Cases/  [R3]

```csharp
// Cases/CaseSet.cs  [R3]
/// Host-driven cases under one parent. Obligation: the cases cover the parent.
public sealed class CaseSet : IDisposable
{
    public Scope Parent { get; }
    /// Live cases, in creation order. A case a lift finds contradictory is disposed and leaves the list.
    public IReadOnlyList<Scope> Cases { get; }
    /// A new case child (IsCase = true; it stops on contradiction). ArgumentException for another graph's tag.
    public Scope Add(ScopeTag? tag = null);
    /// Lifts into Parent what every non-vacuous case proves (§6.9.4), then disposes the vacuous cases.
    /// InvalidOperationException with no cases. May be called repeatedly. Cancellation is checked between lift rounds.
    public LiftReport Lift(LiftOptions? options = null, CancellationToken cancellationToken = default);
    /// Never throws; disposes every case (D40).
    public void Dispose();
}

// Cases/LiftOptions.cs  [R3]
public sealed class LiftOptions
{
    public static LiftOptions Default { get; }
    public int RoundLimit { get; init; } = 16;
    /// Parent-level candidates per case row (product over class columns).
    public int CandidateLimit { get; init; } = 64;
}

// Cases/LiftReport.cs  [R3]
public sealed class LiftReport
{
    public int Rounds { get; }
    public int LiveCases { get; }
    public int VacuousCases { get; }
    /// Every case was vacuous.
    public bool ParentContradicted { get; }
    public long Unions { get; }
    /// Relation rows.
    public long Rows { get; }
    /// Constructor keys made in the parent.
    public long Terms { get; }
    /// Function values written.
    public long Values { get; }
    /// Candidate rows dropped by CandidateLimit.
    public long Skipped { get; }
    public bool HitRoundLimit { get; }
    /// The parent's Version moved.
    public bool Changed { get; }
    public override string ToString();
}

// Cases/ScopeContradictedException.cs  [R3]
/// A read or write that needs a model, on a case whose contradiction stopped it (D45).
public sealed class ScopeContradictedException : InvalidOperationException
{
    public ScopeContradictedException(string scopePath);
    public string ScopePath { get; }
}
```

### 3.8 Extraction/

```csharp
// Extraction/CostModel.cs  [R1a]
/// Immutable. Head cost per constructor (default: its declared cost), an optional per-row cost function, value costs
/// (default 1, egglog's base_value_cost). All costs must be >= 0. Thread-safe.
public sealed class CostModel
{
    public static CostModel Default { get; }
    public CostModel With(ConstructorHandle head, long cost);
    public CostModel With(ConstructorHandle head, NodeCostFunction cost);
    public CostModel WithValueCost(ColumnType type, long cost);
    public CostModel WithValueCost<T>(ColumnType<T> type, Func<T, long> cost);
    /// Extraction may not use head's rows under this model.
    public CostModel Unextractable(ConstructorHandle head);
}

// Extraction/NodeCostFunction.cs  [R1a]
/// The cost of one row of a constructor, without its children. Pure; called once per row extraction reads; must not call
/// into any graph.
public delegate long NodeCostFunction(NodeView node);

// Extraction/NodeView.cs  [R1a]
public readonly ref struct NodeView
{
    public ConstructorHandle Head { get; }
    public int Arity { get; }
    /// A key column, decoded; for a sort column, the class (origin: the extractor's scope).
    public T Get<T>(int column);
    public object Get(int column);
}

// Extraction/Extractor.cs  [R1a]
/// Cheapest terms in one scope as it is now; reusable for many roots. Reads, queries and writes that change nothing
/// never invalidate it; a write that changes the scope, or a Run in it (or, from R2, in a non-frozen ancestor), may,
/// and it then throws InvalidOperationException. Not thread-safe on a writable scope; on a frozen scope (R2) any number
/// may run on any threads.
public sealed class Extractor
{
    public Scope Scope { get; }
    public CostModel Costs { get; }
    /// InvalidOperationException when no finite term exists.
    public ExtractedTerm Extract<TSort>(TSort root) where TSort : struct, ISort<TSort>;
    public bool TryExtract<TSort>(TSort root, [NotNullWhen(true)] out ExtractedTerm? term) where TSort : struct, ISort<TSort>;
    public bool TryGetCost<TSort>(TSort root, out long cost) where TSort : struct, ISort<TSort>;
}

// Extraction/ExtractedTerm.cs  [R1a]
/// One node of an extracted term; shared subterms are the same instance.
public sealed class ExtractedTerm
{
    public ConstructorHandle Head { get; }
    /// Canonical in the extractor's scope, whose origin it carries.
    public ClassId Class { get; }
    /// Tree cost.
    public long Cost { get; }
    public int Arity { get; }
    public bool Is(ConstructorHandle head);
    public bool IsChild(int index);
    public ExtractedTerm Child(int index);
    public T Value<T>(int index);
    /// ExtractedTerm, or the decoded value.
    public object Argument(int index);
    public bool Mentions(ConstructorHandle head);
    /// Whether the head is head, binding its arguments: for translating a term into another language.
    public bool TryMatch<TSort>(Constructor<TSort> head) where TSort : struct, ISort<TSort>;
    public bool TryMatch<T1, TSort>(Constructor<T1, TSort> head, out ExtractedArgument<T1> a) where TSort : struct, ISort<TSort>;
    public bool TryMatch<T1, T2, TSort>(Constructor<T1, T2, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b) where TSort : struct, ISort<TSort>;
    public bool TryMatch<T1, T2, T3, TSort>(Constructor<T1, T2, T3, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b, out ExtractedArgument<T3> c) where TSort : struct, ISort<TSort>;
    public bool TryMatch<T1, T2, T3, T4, TSort>(Constructor<T1, T2, T3, T4, TSort> head, out ExtractedArgument<T1> a, out ExtractedArgument<T2> b, out ExtractedArgument<T3> c, out ExtractedArgument<T4> d) where TSort : struct, ISort<TSort>;
    /// egglog's extraction output: (StrEq "kind" "order")
    public override string ToString();
}

// Extraction/ExtractedArgument{T}.cs  [R1a]
/// One argument of an extracted node: a decoded value, or for a sort column the class and its extracted subterm.
public readonly struct ExtractedArgument<T>
{
    /// The value, or for a sort column the class.
    public T Value { get; }
    /// The subterm for a sort column; null for a value column.
    public ExtractedTerm? Term { get; }
    public override string ToString();
}
```

### 3.9 Diagnostics/

```csharp
// Diagnostics/RuleStatistics.cs  [R1a]
/// What one rule did during a run or a step (D41).
public sealed class RuleStatistics
{
    public Rule Rule { get; }
    /// The engine's match count (decision 6).
    public long Matches { get; }
    /// The engine's search-and-apply time for the rule's variants.
    public TimeSpan SearchTime { get; }
    public int IterationsWithMatches { get; }
    /// The rule's query plan as the engine reports it, from the latest iteration that ran the rule; null unless
    /// EGraphOptions.RecordPlans is set.
    public string? Plan { get; }                                                                             // R1b
    /// "lower uuid: 2 matches in 1 iterations, 0.1 ms"
    public override string ToString();
}

// Diagnostics/RunStepKind.cs  [R1b]
public enum RunStepKind
{
    Iteration,
    MirrorSync,                                                                                              // R2
    SplitStep,                                                                                               // R3
    Lift,                                                                                                    // R3
}

// Diagnostics/RunStep.cs  [R1b]
/// One step of a run, delivered to RunOptions.Observer and kept with RunOptions.RecordSteps (§6.11).
public sealed class RunStep
{
    public RunStepKind Kind { get; }
    /// The scope the step ran in; a case is disposed once the run ends.
    public Scope Scope { get; }
    public string ScopePath { get; }
    /// Position among the run's steps, from 0.
    public int Index { get; }
    public bool Changed { get; }
    public TimeSpan Elapsed { get; }
    /// For an iteration: the rules that matched in it, in the order they ran, each with that iteration's figures
    /// (IterationsWithMatches = 1); a read-only copy. Empty for other kinds.
    public IReadOnlyList<RuleStatistics> Rules { get; }
    /// For a split step.
    public int CasesOpened { get; }                                                                          // R3
    /// "#3 iteration root/schema#1/query#2 changed: lower uuid 1, and commutes 4"; "unchanged" when nothing changed.
    public override string ToString();
}

// Diagnostics/TableRow.cs  [R1b]
/// One decoded row of a table, from Scope.Rows.
public sealed class TableRow
{
    public TableHandle Table { get; }
    /// Decoded keys; a sort cell is its ClassId (origin: the scope read).
    public IReadOnlyList<object> Keys { get; }
    /// The value or class; null for a relation.
    public object? Output { get; }
    /// (present "kind"), (dom "kind") = (set-of "a" "b"), (StrEq "kind" "a") = #17
    public override string ToString();
}
```

---

## 4. Internal types

One type per file, `internal`, namespace `Eggnog`, XML docs on every member (conventions). Engine types are written
`Relations.X` (D29). Milestones as in §3. A type tagged "R1, deleted in R2" exists only while the root runs on a bare
`Database`.

### 4.1 Graph/

```csharp
// Graph/EngineScope.cs  [R1a]
/// The engine side of one Scope (D28). Every member runs while the scope's ScopeGuard is held, or on a frozen scope.
internal abstract class EngineScope
{
    public abstract long Version { get; }
    public abstract bool IsContradictory { get; }
    /// A case whose stop flag is set (D45). Always false in R1.
    public abstract bool IsStopped { get; }                                                        // R3
    /// Rows in this scope's own layers (Database.TotalSizeEstimate), for RowLimit.
    public abstract long OwnRowCount { get; }                                                      // R1b
    /// Catches up with non-frozen ancestors and rebuilds when needed; a no-op when the scope is current.
    public abstract void EnsureClean();
    /// The engine variants to run for these rules in this scope, lowered on demand (§6.4.4).
    public abstract LoweredRule[] PrepareRules(IReadOnlyList<CompiledRule> rules, bool semiNaive);
    /// One variant of a program (a rule, a query, a split matcher or a copy rule) for this scope: R1 lowers it with the
    /// canonical forms of writtenIdConstants (§6.4.5); R2 with the written values.
    public abstract LoweredRule PrepareVariant(RuleVariants variants, uint[] writtenIdConstants, bool semiNaive);
    /// One engine iteration (§6.5.1). Lets Relations.PanicException through.
    public abstract EngineIteration RunRules(ReadOnlySpan<Relations.EGraphRuleId> rules);
    /// Mints a class id for a new term of a host batch (§6.6.1).
    public abstract uint FreshId();
    /// Stages a row (keys then output; relation rows end with the unit value). Sort cells must be canonical or fresh.
    public abstract void StageRow(Relations.FunctionId function, ReadOnlySpan<uint> row);
    public abstract void StageUnion(uint a, uint b);
    /// Merges what was staged and rebuilds. Returns whether the scope changed. Lets Relations.PanicException through
    /// for a failure a merge recorded.
    public abstract bool Flush();
    public abstract uint Find(uint id);
    /// Maps the given columns of row-major tuples through Find, in place, as one engine call (a query's sort columns,
    /// §6.6.4); returns whether a cell changed, so that two tuples may now be equal.
    public abstract bool CanonicalizeColumns(Span<uint> cells, int width, ReadOnlySpan<int> columns); // R1b
    /// Key sort cells are canonicalized here; the output, if a class, is canonical.
    public abstract bool TryLookup(Relations.FunctionId function, ReadOnlySpan<uint> key, out uint output);
    public abstract long TableSize(Relations.FunctionId function);                                 // R1b
    /// Every row of a table in this scope's view. R1a's host batches use it for the merge check of D32 (§6.6.1).
    public abstract void ForEach(Relations.FunctionId function, Relations.FunctionRowCallback visit);
    public abstract Relations.Extractor CreateExtractor(Relations.ExtractionSchema schema, Relations.CostModel costs);
    /// Extraction through an extractor this scope made, with the class canonicalized here (§6.10).
    public abstract bool TryExtract(Relations.Extractor extractor, Relations.Sort sort, uint id, out Relations.ExtractedTerm? term);
    public abstract bool TryGetCost(Relations.Extractor extractor, Relations.Sort sort, uint id, out long cost);
    /// Runs a query's naive engine rule against the scope's current contents (D34, D49).
    public abstract void RunQuery(LoweredRule query);                                              // R1b
    public abstract bool IsFrozen { get; }                                                         // R2
    public abstract bool IsDisposed { get; }                                                       // R2
    /// The engine context's fault flag (E7, X12), which Scope.IsFaulted reads with its own.
    public abstract bool IsFaulted { get; }                                                        // R2
    public abstract ContextEngineScope CreateChild(Relations.ContextOptions options);              // R2
    /// Freezes, pre-planning exactly planRules (E10).
    public abstract void Freeze(IReadOnlyCollection<Relations.EGraphRuleId> planRules);            // R2
    public abstract void Dispose();                                                                // R2
}

// Graph/RootEngineScope.cs  [R1a, deleted in R2]
/// The root over the bridge's egglog-compatible root API on a bare Database (§6.4.5, §6.6.5).
internal sealed class RootEngineScope : EngineScope
{
    public RootEngineScope(EGraph graph, Relations.EGraph engine);
    // fields: the graph's engine lock (§6.12), Relations.ExecutionState? batch, Relations.TableAction?[] actions
    // (by FunctionId index), Relations.UnionAction union.
}

// Graph/ContextEngineScope.cs  [R2]
/// One EGraphContext (colors §10), for every scope including the root.
internal sealed class ContextEngineScope : EngineScope
{
    public ContextEngineScope(EGraph graph, Relations.EGraphContext context);
    public Relations.EGraphContext Context { get; }
    /// The context's value of the graph's runtime slot (Context.GetLocal(graph.RuntimeSlot)).
    public ScopeRuntime Runtime { get; }
    /// The sum of the change stamps of the context and its ancestors (colors §9.1): the key of mirror syncs (§6.8, N61).
    /// O(depth).
    public long ViewStamp { get; }
}

// Graph/EngineIteration.cs  [R1a]
internal readonly record struct EngineIteration(bool Changed, bool RebuildChanged, bool Stopped,
    Relations.RuleSetReport Report, TimeSpan RebuildTime);

// Graph/ScopeGuard.cs  [R1a]
/// The rule layer's guard of one thread domain (D47): a Relations.ThreadAffinityGuard, the releases deferred until the
/// domain's current call ends (D40), and whether a run observer is running. A release holds the guard under a release
/// lock; it runs no user code (R2, N68).
internal sealed class ScopeGuard
{
    /// Re-entrant on the owning thread. When the guard is held, waits for a release in progress on another thread (the
    /// release lock) and tries once more; InvalidOperationException naming the scope when a call holds it (N68).
    public void Enter(Scope scope);
    /// Enters unless another thread holds the guard; never waits.
    public bool TryEnter();                                                                            // R2
    /// As TryEnter, under the release lock, for a release (D40); left with ExitAfterRelease.
    public bool TryEnterForRelease();                                                                  // R2
    public void ExitAfterRelease();                                                                    // R2
    /// At depth 0, leaves the domain, then runs the releases deferred meanwhile (N71).
    public void Exit();
    /// The calling thread's nesting depth; 0 when it does not hold the guard.
    public int Depth { get; }                                                                          // R2
    public bool IsHeldByAnotherThread { get; }
    public bool InObserver { get; set; }
    public void DeferRelease(Scope scope);
}

// Graph/EngineCallMarker.cs  [R1a]
/// Marks the current thread as running a user delegate ([ThreadStatic] depth): the call sites that invoke primitives,
/// filters, lattice merges and cost functions set it around each invocation, on whatever thread runs it, so a delegate
/// that calls into a graph is refused (D47).
internal static class EngineCallMarker
{
    public static bool IsActive { get; }
    public static void Enter();
    public static void Exit();
}

// Graph/ScopeOrigin.cs  [R1a]
/// Where a class value came from (D39): a scope, or one host batch of a scope.
internal sealed class ScopeOrigin
{
    public ScopeOrigin(EGraph graph, ScopeOrigin? parent, string path, bool isBatch);
    public EGraph Graph { get; }
    /// A scope's: its parent scope's origin (null at the root). A batch's: its scope's origin.
    public ScopeOrigin? Parent { get; }
    public string Path { get; }
    public bool IsBatch { get; }
    /// Batches: set when the batch commits.
    public bool Committed { get; set; }
    /// Scopes: set by Dispose.
    public bool Disposed { get; set; }
}

// Graph/ClassChecks.cs  [R1a]
internal static class ClassChecks
{
    /// The cell of value, or ArgumentException unless value may be used in scope (§6.6.7).
    public static uint Require(Scope scope, ClassId value, string parameter, ScopeOrigin? openBatch = null);
    /// Whether a rule owned by owner (null: global) may hold a class constant of origin (§6.3, step 3).
    public static bool AllowedInRule(Scope? owner, ScopeOrigin origin);
}

// Graph/ScopeRuntime.cs  [R1a]
/// Per-scope buffers that external functions write during engine calls: query and split-matcher tuples, and the first
/// evaluation failure. Thread-safe (rule tasks may run in parallel). R1: the root's, held by the graph. R2: one per
/// context, made by the graph's ContextLocalSlot<ScopeRuntime> (colors §10.1); Owner maps the context to its Scope.
internal sealed class ScopeRuntime
{
    /// R1: graph.Root's runtime. R2: (state.View?.Owner.Owner?.Host as EGraphContext)?.GetLocal(graph.RuntimeSlot) when
    /// that context belongs to this graph's engine, otherwise graph.Root's runtime (N75). Lock-free once the runtime exists.
    public static ScopeRuntime Of(EGraph graph, Relations.ExecutionState state);
    public Scope? Owner { get; set; }
    public bool TryRecord(EvaluationFailure failure);     // the first one wins
    public EvaluationFailure? TakeFailure();
    public TupleBuffer? QueryMatches { get; set; }        // set by QueryRunner and RuleSplitSource for one engine call
    public bool StopAtFirstQueryMatch { get; set; }
}

// Graph/TupleBuffer.cs  [R1a; pooled in R1b]
/// A set of fixed-width uint tuples: Add is thread-safe and drops duplicates. Every array is rented from
/// ArrayPool<T>.Shared and returned by Dispose, so filling a buffer allocates nothing per tuple (conventions). The
/// indexer reads in insertion order, or in the order of the last sort; sorting moves no cells.
internal sealed class TupleBuffer : IDisposable
{
    public TupleBuffer(int width);
    public int Width { get; }
    public int Count { get; }
    public bool Add(ReadOnlySpan<uint> tuple);            // false if already present
    public ReadOnlySpan<uint> this[int index] { get; }
    /// Row-major, in insertion order, to change in place (canonicalizing classes); then call Deduplicate.
    public Span<uint> Cells { get; }
    public void Deduplicate();
    /// Lexicographic by cells.
    public void SortByCells();
    /// Content order (§6.1), column by column; distinct cells a type compares equal fall back to cell order.
    public void SortByContent(IReadOnlyList<ColumnType> columns);
    public void Clear();
    public void Dispose();                                // returns the arrays; the buffer stays usable
}

// Graph/TupleOrder.cs  [R1b]
/// Compares two tuples of a TupleBuffer by number: by cells, or by content given the column types. A struct, so a sort
/// boxes it once per call.
internal readonly struct TupleOrder : IComparer<int>
{ public TupleOrder(uint[] cells, int width, IReadOnlyList<ColumnType>? columns); }

// Graph/EvaluationFailure.cs  [R1a]
internal sealed record EvaluationFailure(string? RuleName, string What, string? Bindings, Exception? Exception);

// Graph/NameRules.cs  [R1a]
internal static class NameRules
{
    /// Non-empty; no '@'; not starting with '$'. Uniqueness is checked by the caller under the definition lock.
    public static void ValidateTableName(string name, string parameter);
    public static void ValidateName(string name, string parameter);
}

// Graph/ScopeResolver.cs  [R2]
internal static class ScopeResolver
{
    public static Scope? TryResolve(Scope from, ScopeTag tag);
    /// InvalidOperationException: "Rule 'declared tautology' reads scope 'schema', but 'root/query#4' has no
    /// ancestor tagged 'schema'."
    public static Scope Resolve(Scope from, ScopeTag tag, string reader);
}
```

### 4.2 Values/

`ColumnType` gains internal members: `Relations.ColumnTy EngineType`, `Relations.Sort ExtractionSort`,
`uint EncodeObject(object)`, `bool TryEncodeExistingObject(object, out uint)` and `object DecodeObject(uint, Scope)`
(a sort cell decodes to a class with the scope as origin). `ColumnType<T>` gains internal abstract members:
`uint Encode(T)`, `bool TryEncodeExisting(T, out uint)` and `T Decode(uint, Scope)`.

```csharp
// Values/SortColumnType{TSort}.cs  [R1a]
/// A sort: cells are class ids (ColumnTy.Id); Encode is ClassChecks.Require in the scope at hand; Decode makes a
/// ClassId with the scope's origin; the extraction sort is Relations.EqSort(name).
internal sealed class SortColumnType<TSort> : ColumnType<TSort> where TSort : struct, ISort<TSort>
{ public SortColumnType(string name); }

// Values/BaseColumnType{TCodec,T}.cs  [R1a]
/// A value type over an engine codec: Encode = BaseValues.Get<TCodec, T>, Decode = Unwrap, TryEncodeExisting =
/// BaseValues.TryGetExisting (E1); the extraction sort is Relations.BaseSort<TCodec, T>(name, format, order).
/// Serves string, bool, long, ValueSet<T> (over InternedCodec<ValueSet<T>>) and registered types (InternedCodec<T>).
internal sealed class BaseColumnType<TCodec, T> : ColumnType<T>
    where TCodec : struct, Relations.IBaseValueCodec<T> where T : notnull, IEquatable<T>
{ public BaseColumnType(string name, Relations.BaseValues values, Func<T, string> format, IComparer<T> order); }

// Values/WidenedColumnType{T}.cs  [R1a]
/// int and enums, stored as the engine's i64 (Int64Codec) through widen and narrow functions (D20). Its extraction
/// sort is a Relations.BaseSort<Int64Codec, long> under this type's name, formatting through narrow.
internal sealed class WidenedColumnType<T> : ColumnType<T> where T : notnull
{ public WidenedColumnType(string name, Relations.BaseValues values, Func<T, long> widen, Func<long, T> narrow, Func<T, string> format); }

// Values/DoubleColumnType.cs  [R1a]
/// double over DoubleCodec; Encode maps −0.0 to +0.0 and throws ArgumentException for NaN (D20).
internal sealed class DoubleColumnType : ColumnType<double> { public DoubleColumnType(Relations.BaseValues values); }

// Values/UnitColumnType.cs  [R1a]
/// The unit value type of relation outputs (UnitCodec; its single value is 0). Not reachable through ValueStore.TypeOf.
internal sealed class UnitColumnType : ColumnType<ValueTuple> { public UnitColumnType(Relations.BaseValues values); public uint Unit { get; } }

// Values/LatticeMerge{T}.cs  [R1a]
/// The merge external of a function with a lattice: [old, new] → Encode(Merge(Decode(old), Decode(new))). An
/// exception is recorded (D31) and reported with ExecutionState.ReportFailure; the old value is returned.
internal sealed class LatticeMerge<T> : Relations.ExternalFunction
{ public LatticeMerge(EGraph graph, string functionName, ValueLattice<T> lattice, ColumnType<T> type); }

// Values/CaseJoinAdapter{T}.cs  [R3]
internal static class CaseJoinAdapter<T>
{ public static Func<uint, uint, uint>? Create(ValueLattice<T> lattice, ColumnType<T> type); }

// Values/ValueSetOrder{T}.cs  [R1a]
/// Ordinal for strings, Comparer<T>.Default otherwise.
internal static class ValueSetOrder<T> { public static IComparer<T> Comparer { get; } }
```

### 4.3 Terms/

These are v1's term and fact representations, unchanged except that constants hold CLR values instead of bits and the
untyped forms are internal (D13). Each is one file (`Terms/TermShapeKind.cs`, `Terms/TermShape.cs`, …).

```csharp
internal enum TermShapeKind { Variable, Constant, Application, Computation }
internal readonly struct TermShape
{
    public TermShapeKind Kind { get; }
    public Type ClrType { get; }
    public string? Name { get; }                // variables
    public object? Owner { get; }               // variables: the builder
    public ColumnType? ConstantType { get; }
    public object? Constant { get; }
    public TableHandle? Table { get; }          // applications (possibly scoped, R2)
    public Primitive? Primitive { get; }
    public Term[] Arguments { get; }
}
// every Term overrides: internal abstract TermShape Shape { get; }
internal sealed class ConstantTerm<T> : Term<T> { public ConstantTerm(T value); }
internal sealed class UntypedConstantTerm : Term { public UntypedConstantTerm(ColumnType type, object value); }
internal sealed class ApplicationTerm<T> : Term<T> { public ApplicationTerm(TableHandle table, Term[] arguments); }    // functions
internal sealed class UntypedApplicationTerm : Term { public UntypedApplicationTerm(TableHandle table, Term[] arguments); }
internal sealed class ComputeTerm<T> : Term<T> { public ComputeTerm(Primitive primitive, Term[] arguments); }
internal sealed class UntypedComputeTerm : Term { public UntypedComputeTerm(Primitive primitive, Term[] arguments); }
internal sealed class UntypedVar : Term { public UntypedVar(object owner, ColumnType type, string name); }
internal enum FactKind { Relation, Equality, NotEqual, Exists, Filter, Scoped }
// every Fact overrides: internal abstract FactKind Kind { get; }
internal sealed class RelationFact : Fact { public RelationHandle Table { get; } public Term[] Arguments { get; } }
internal sealed class EqualityFact : Fact { public Term Left { get; } public Term Right { get; } }
internal sealed class NotEqualFact : Fact { public Term Left { get; } public Term Right { get; } }
internal sealed class ExistsFact : Fact { public Term Application { get; } }
internal sealed class FilterFact : Fact { public Term Condition { get; } }
internal sealed class ScopedFact : Fact { public ScopeTag Scope { get; } public Fact[] Facts { get; } }        // R2
internal static class PrimitiveCache<T> where T : notnull { /* static readonly fields backing Primitives.*<T>() */ }
// Primitive declares: internal abstract uint InvokeCore(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result);
// internal bool IsLambda (it renders as <lambda>); and internal bool IsPartial { get; init; } (Primitives.Add and
// Subtract: an OverflowException in a body let is "no result", D31).
// Internal members of public types (D13): RelationHandle.Apply, RelationHandle.InScope, FunctionHandle.Apply,
// FunctionHandle.InScope, ConstructorHandle.Apply, Term.Constant, Fact.Equal, Fact.NotEqual, Compute.Of(Primitive, …),
// RuleBuilder.Var(ColumnType, …), QueryBuilder.Var(ColumnType, …), QueryMatch.Get(Term), and the untyped
// EGraph.DefineRelation/DefineFunction/DefineConstructor overloads taking IReadOnlyList<ColumnType>, which return
// these handles (Tables/, R1a):
internal sealed class UntypedRelation : RelationHandle { }
internal sealed class UntypedFunction : FunctionHandle { }
internal sealed class UntypedConstructor : ConstructorHandle { }
```

### 4.4 Rules/ (compilation, programs, lowering)

```csharp
// Rules/RuleDraft.cs  [R1a]
/// What a RuleBuilder, QueryBuilder or SplitBuilder collected, before any check.
internal sealed class RuleDraft
{
    public string Name { get; }
    public object Owner { get; }                              // the builder; its variables carry it
    public List<(string Name, ColumnType? Type, Term Variable)> Variables { get; }   // null: unregistered, §6.3 step 1
    public List<Fact> Body { get; }                           // When facts and Where filters, in call order
    public List<HeadItem> Head { get; }                       // in call order
    public bool Naive { get; set; }
    public ScopeTag? Erasing { get; set; }                    // R2
    public string? OwnFactsReason { get; set; }               // R2
}

// Rules/HeadItem.cs  [R1a]
internal readonly record struct HeadItem(HeadItemKind Kind, Fact? Fact, Term? Variable, Term? Left, Term? Right);

// Rules/HeadItemKind.cs  [R1a]
internal enum HeadItemKind { Then, Make, Rewrite }

// Rules/RuleCompiler.cs  [R1a]
/// Checks and normalizes a draft into a RuleProgram (§6.3). Every error is a RuleDefinitionException naming the rule.
internal static class RuleCompiler
{
    public static CompiledRule Compile(EGraph graph, RuleDraft draft, Ruleset ruleset, Scope? owner);
    public static CompiledQuery CompileQuery(EGraph graph, RuleDraft draft);                                  // R1b
    public static SplitProgram CompileSplit(EGraph graph, RuleDraft draft, Term setVariable, Term elementVariable,
        IReadOnlyList<Term> key, IReadOnlyList<Fact> assumptions);                                              // R3
}

// Rules/ProgramBuilder.cs  [R1a]
/// The mutable state of one compilation, which RuleCompiler drives (§6.3 steps 1–9): registers and their union-find,
/// the body's atoms and steps, the class constants, the head. Every error is a RuleDefinitionException naming the rule.
internal sealed class ProgramBuilder { }

// Rules/RuleProgram.cs  [R1a]
/// The engine-independent normal form of a rule: body registers, atoms, steps, head operations.
internal sealed class RuleProgram
{
    public string Name { get; }
    public ProgramRegister[] Registers { get; }               // body registers first, then head registers
    public ProgramAtom[] Atoms { get; }                       // in flattening order: the engine's variant order
    public ProgramStep[] Steps { get; }                       // lets, filters, not-equals and (R2) equals, in a valid order
    public HeadOp[] Head { get; }
    public uint[] WrittenIdConstants { get; }                 // class constants, by IdConstant operand index
    public ScopeOrigin[] IdConstantOrigins { get; }           // their origins (D39), same indexing
    public int[] NamedRegisters { get; }                      // declared body variables, then Make variables
    public ScopeTag[] ReadScopes { get; }                     // R2
    public bool WritesAnalysis { get; }
}

// Rules/ProgramRegister.cs  [R1a]
internal readonly record struct ProgramRegister(ColumnType Type, string Name, RegisterKind Kind);
// Rules/RegisterKind.cs  [R1a]
internal enum RegisterKind { Declared, Fresh, Head }
// Rules/Operand.cs  [R1a]
internal readonly record struct Operand(OperandKind Kind, int Index, ColumnType Type, uint Bits);
// Rules/OperandKind.cs  [R1a]
internal enum OperandKind { Register, Constant, IdConstant }
// Rules/ProgramAtom.cs  [R1a]
/// An atom over table. For a scoped atom (R2), Table is the mirror's internal handle and Scope the tag, kept for
/// rendering. Columns are the keys then the output; a relation has no output column here (lowering appends the unit
/// constant).
internal sealed record ProgramAtom(TableHandle Table, ScopeTag? Scope, Operand[] Columns);
// Rules/ProgramStep.cs  [R1a]
internal sealed record ProgramStep(ProgramStepKind Kind, Primitive? Primitive, Operand[] Arguments, Operand Target);
// Rules/ProgramStepKind.cs  [R1a]
/// Equal (R2): two class constants whose comparison the definition left to run time (§6.3 step 3, N63, N86).
internal enum ProgramStepKind { Let, Filter, NotEqual, Equal }
// Rules/HeadOp.cs  [R1a]
internal sealed record HeadOp(HeadOpKind Kind, TableHandle? Table, Operand[] Inputs, int Target, Primitive? Primitive);
// Rules/HeadOpKind.cs  [R1a]
internal enum HeadOpKind { Insert, Write, GetOrAdd, Union, Compute, Collect }    // Collect: queries and split matchers

// Rules/CompiledRule.cs  [R1a]
internal sealed class CompiledRule
{
    public Rule Rule { get; }
    public RuleProgram Program { get; }
    public RuleVariants Variants { get; }
    /// Graph-unique, for descriptions.
    public int Serial { get; }
}

// Rules/RuleVariants.cs  [R1a]
/// The engine variants of one compiled rule, query, split matcher or copy rule (D36). Thread-safe: double-checked under
/// a per-rule lock.
internal sealed class RuleVariants
{
    /// R1: the variant for the mode, lowered with constants when it does not exist or was lowered with other constants
    /// (the stale variant is freed first, D3). R2: lowered once with the written constants, which the engine
    /// canonicalizes per context (X19), so the constants parameter is gone; O(1) once lowered.
    public LoweredRule Get(EGraph graph, bool semiNaive);
    public IEnumerable<LoweredRule> All { get; }
    /// The default variant (semi-naive, or naive for a naive program), for E10's plan list; null before it exists.
    public LoweredRule? Default { get; }
    /// Frees every variant (FreeRule, then FreeExternalFunction of its owned functions).
    public void Free(EGraph graph);
}

// Rules/LoweredRule.cs  [R1a]
internal sealed class LoweredRule
{
    public Relations.EGraphRuleId Id { get; }
    /// Unique; keys the engine's statistics (§6.4.3).
    public string Description { get; }
    public bool SemiNaive { get; }
    /// The values IdConstant operands were lowered with.
    public uint[] Constants { get; }
    /// Call sites and collectors, freed with the variant.
    public Relations.ExternalFunctionId[] Owned { get; }
    /// The rule (null for a query, a split matcher or a copy rule).
    public Rule? Rule { get; }
    /// The collector of a query or split matcher.
    public MatchCollector? Collector { get; }
}

// Rules/RuleLowering.cs  [R1a]
/// Builds one engine rule from a program with EGraphRuleBuilder (§6.4.2).
internal static class RuleLowering
{
    public static LoweredRule Lower(EGraph graph, RuleProgram program, Rule? rule, bool semiNaive,
        ReadOnlySpan<uint> constants, string description);
}

// Rules/PrimitiveCall.cs  [R1a]
/// A let or head-compute call site: decode, invoke, encode. On an exception: record, ReportFailure, return false;
/// except a partial primitive's OverflowException in a body let (inBody), which returns false and records nothing.
internal sealed class PrimitiveCall : Relations.ExternalFunction
{ public PrimitiveCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, ColumnType result, string[] argumentNames, bool inBody); }

// Rules/FilterCall.cs  [R1a]
/// A boolean filter call site: produces the unit value when the primitive returns true, nothing when it returns false.
internal sealed class FilterCall : Relations.ExternalFunction
{ public FilterCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, string[] argumentNames); }

// Rules/NotEqualFunction.cs  [R1a]
/// [a, b] → unit when a != b as raw cells (canonical classes or canonical values), nothing otherwise. One per graph.
internal sealed class NotEqualFunction : Relations.ExternalFunction { public NotEqualFunction(uint unit); }

// Rules/EqualFunction.cs  [R2]
/// [a, b] → unit when the cells are equal, nothing otherwise: the Equal step. Its operands are class constants the
/// engine substitutes with their canonical forms in the running context (X19). One per graph (EGraph.Equal).
internal sealed class EqualFunction : Relations.ExternalFunction { public EqualFunction(uint unit); }

// Rules/MatchCollector.cs  [R1b]
/// The head action of a query or a split matcher: adds its arguments to ScopeRuntime.Of(graph, state).QueryMatches and
/// produces unit. For a query asked with Any, triggers early stop after the first match. Stages nothing.
internal sealed class MatchCollector : Relations.ExternalFunction
{ public MatchCollector(EGraph graph, int width, uint unit); }

// Rules/CompiledQuery.cs  [R1b]
internal sealed class CompiledQuery
{
    public Query Query { get; }
    public RuleProgram Program { get; }
    public RuleVariants Variants { get; }                     // the naive variant only
    public int Serial { get; }                                // of the description, §6.4.3
    /// The declared variables' types, in declaration order: the columns of a match.
    public ColumnType[] Columns { get; }
    public int Width { get; }
    /// The columns whose cells are classes, ascending: the ones a query canonicalizes (§6.6.4).
    public int[] SortColumns { get; }
    public int ColumnOf(Term variable);                       // ArgumentException for another query's variable
    /// Calls in progress; Query.Dispose frees the variants when it reaches zero (D49).
    public int Users { get; }
    public bool IsDisposed { get; }
    public void Acquire();                                    // ObjectDisposedException once disposed
    public void Release();
    /// Marks the query disposed; frees the variants now if unused, without waiting: when the call comes from a user
    /// delegate, or (R1) the engine lock is busy, or (R2) EGraph.ActiveEngineCalls is positive (N69), EGraph.DeferFree
    /// hands them to the graph. false when already disposed.
    public bool Dispose();
}
```

### 4.5 Host/

```csharp
// Host/HostBatch.cs, Host/HostBatch.Merges.cs  [R1a]  (partial; Merges: the congruence check of D32)
/// One host write batch (D30): Make, Assume and Union are batches of one call; Scope.Write and ScopeWriter make larger
/// ones. Each call is planned as it is made (reads only, plus fresh ids); Commit stages and flushes.
internal sealed partial class HostBatch
{
    public HostBatch(Scope scope);
    /// The batch's origin (D39): values its Make calls return are valid once it commits.
    public ScopeOrigin Origin { get; }
    /// A ground term's class: an existing class (TryLookup), or a fresh id minted now for each new subterm (FreshId),
    /// memoised by (head, keys). ArgumentException for a term that is not ground or not writable.
    public uint PlanTerm(Term term);
    /// The host-write fact table, §6.6.2. Facts that already hold plan nothing.
    public void PlanFact(Fact fact);
    /// Plans nothing when the classes are already equal. Every planned merge (a union, or a constructor write that
    /// gives an existing key another class) runs the D32 congruence check (§6.6.1).
    public void PlanUnion(uint a, uint b);
    /// Nothing to stage: every fact held already.
    public bool IsEmpty { get; }
    /// A call of the batch was rejected (Reject, set by the ScopeWriter): the batch must not commit, even when the
    /// callback caught the exception.
    public bool IsRejected { get; }
    public void Reject();
    /// Stages new terms' rows in post-order, then rows, then unions; flushes. Does nothing when IsEmpty. The caller
    /// then commits Origin, an empty batch's included.
    public bool Commit();
}

// Host/PendingTerm.cs  [R1a]
internal sealed record PendingTerm(ConstructorHandle Head, uint[] Keys, uint Id);

// Host/TermReader.cs  [R1a]
/// Host reads (§6.6.3): never create, never intern. Scoped handles read the resolved scope (R2).
internal static class TermReader
{
    public static bool TryRead(Scope scope, Term term, out uint cell);
    public static bool Check(Scope scope, Fact fact);
    /// Why fact is not a ground host check (a filter, a variable, a computation), or, given a scope, cannot be read in
    /// it (a table or column type of another graph, a class the scope cannot use); null when it can. For Until (R1b).
    public static string? Validate(Scope? scope, Fact fact);
}

// Host/QueryRunner.cs  [R1b]
internal static class QueryRunner
{
    /// Runs the query in scope (D34, D49); returns the distinct full bindings, canonical, and sorted by content when
    /// sort is set (ForEachMatch only). The caller disposes the buffer, which returns its arrays to the pool.
    public static TupleBuffer Run(Scope scope, CompiledQuery query, bool stopAtFirst, bool sort);
}

// Host/RowReader.cs  [R1b]
/// Scope.CountRows and Scope.Rows (§6.11).
internal static class RowReader
{
    public static long Count(Scope scope, TableHandle table);
    public static IReadOnlyList<TableRow> Read(Scope scope, TableHandle table);
}
```

### 4.6 Scheduling/

```csharp
// Each schedule node implements
//   internal abstract bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled);
//   internal abstract void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions);   // Until facts (R1b)
// R3 adds the split rules to Collect (ISet<SplitRule> splits).
// Scheduling/RunSchedule.cs, RepeatSchedule.cs, SaturateSchedule.cs, SequenceSchedule.cs  [R1a]; UntilSchedule.cs  [R1b]
internal sealed class RunSchedule : Schedule { public Ruleset[] Rulesets { get; } }
internal sealed class RepeatSchedule : Schedule { public int Times { get; } public Schedule[] Steps { get; } }
internal sealed class SaturateSchedule : Schedule { public Schedule[] Steps { get; } }
internal sealed class SequenceSchedule : Schedule { public Schedule[] Steps { get; } }
internal sealed class UntilSchedule : Schedule { public Schedule Body { get; } public Fact[] Condition { get; } }
// Scheduling/SplitSchedule.cs  [R3]
internal sealed class SplitSchedule : Schedule { public SplitRule[] Rules { get; } public Schedule InCase { get; } }

// Scheduling/ScheduleExecutor.cs  [R1a]
internal sealed class ScheduleExecutor
{
    public ScheduleExecutor(Scope top, RunOptions options, CancellationToken cancellationToken);
    public Scope Top { get; }
    public RunOptions Options { get; }
    public RunStatistics Statistics { get; }
    /// The run's stopwatch, started with the executor: TimeLimit and RunReport.Elapsed read it.
    public Stopwatch Clock { get; }
    /// Cases of this run that are alive, for RowLimit (R3).
    public IReadOnlyCollection<Scope> LiveCases { get; }
    /// Global rules of the rulesets, then local rules from the root down to scope (R2); deduplicated, in that order.
    /// Recomputed for every iteration (D6).
    public IReadOnlyList<CompiledRule> RulesFor(IReadOnlyList<Ruleset> rulesets, Scope scope);
    /// Cancellation, then IterationLimit, TimeLimit, RowLimit (ScheduleStopException).
    public void BeforeStep(Scope scope);
    /// §6.5.1. Returns whether the iteration changed the scope.
    public bool RunIteration(Scope scope, IReadOnlyList<CompiledRule> rules, bool inCase);
    /// Records a step, notifies the observer (§6.11). rules: for an iteration, the rules that matched in it.
    public void RecordStep(RunStepKind kind, Scope scope, bool changed, TimeSpan elapsed, IReadOnlyList<RuleStatistics>? rules);
    public RunReport Complete(RunStopReason reason, Stopwatch clock);
}

// Scheduling/RunStatistics.cs  [R1a]
internal sealed class RunStatistics
{
    public int Iterations;
    public bool TopChanged;
    public bool LastTopStepChanged;
    public RunStopReason StopReason;
    public Dictionary<Rule, RuleStatisticsBuilder> Rules = new();
    public TimeSpan SearchTime, MergeTime, RebuildTime;
    public List<RunStep>? Steps;                              // R1b, with RecordSteps
    public int CasesOpened;                                   // R3
    public int SplitsSkipped;                                 // R3
    public int Lifts;                                         // R3
    public long LiftRowsSkipped;                              // R3
}

// Scheduling/RuleStatisticsBuilder.cs  [R1a]
/// Accumulates one rule's RuleStatistics over a run.
internal sealed class RuleStatisticsBuilder
{ public void Add(long matches, TimeSpan searchTime, string? plan); public RuleStatistics Build(Rule rule); }

// Scheduling/ScheduleStopException.cs  [R1a]
internal sealed class ScheduleStopException : Exception
{ public ScheduleStopException(RunStopReason reason); public RunStopReason Reason { get; } }

// Scheduling/ScheduleCaseRunner.cs  [R3]
/// Runs a split node's in-case schedule in one case (Relations.ICaseSchedule, E8).
internal sealed class ScheduleCaseRunner : Relations.ICaseSchedule
{ public ScheduleCaseRunner(ScheduleExecutor executor, Schedule inCase); }
```

### 4.7 Scopes/  [R2]

```csharp
// Scopes/MirrorRegistry.cs  [R2]
/// The mirror tables and copy rules of scoped reads, and the count of live frozen scopes per tag (§6.8, D38).
/// Mutated under the graph's definition lock; read lock-free.
internal sealed class MirrorRegistry
{
    /// RuleDefinitionException wording of §6.3, step 2, when FrozenCount(tag) > 0 and the mirror is new.
    public MirrorTable GetOrCreate(TableHandle table, ScopeTag tag, string reader);
    /// One definition's mirrors, all or nothing, once its other checks passed (§6.3 step 2, N64).
    public MirrorTable[] GetOrCreateAll(IReadOnlyList<(TableHandle Table, ScopeTag Tag)> reads, string reader);
    public MirrorTable Get(TableHandle table, ScopeTag tag);
    /// The copy rules of tag's mirrors, in creation order (copy-on-write snapshot, read lock-free).
    public CompiledRule[] CopyRules(ScopeTag tag);
    /// Every copy rule of every tag (snapshot): part of a freeze's plan list (E10).
    public CompiledRule[] AllCopyRules();
    public int FrozenCount(ScopeTag tag);
    public void AddFrozen(ScopeTag tag);
    public void RemoveFrozen(ScopeTag tag);
}

// Scopes/MirrorTable.cs  [R2]
internal sealed class MirrorTable
{
    public TableHandle Source { get; }
    public ScopeTag Tag { get; }
    /// An internal handle of Source's kind over the mirror's engine function; never listed in EGraph.Tables.
    public TableHandle Handle { get; }
    public Relations.FunctionId Function { get; }
    /// "{source}@{tag name}": unique, since table names contain no '@' and tag names are unique per graph.
    public string Name { get; }
    public CompiledRule CopyRule { get; }
}

// Scopes/MirrorSync.cs  [R2]
/// The pre-use sync of D48 (§6.8).
internal static class MirrorSync
{
    /// Resolves every tag, then, for each, brings resolve(scope, tag)'s mirrors current if that scope is writable and its
    /// view changed since its last sync (N61), leaving the sync out of its Version (N62); records a MirrorSync step when
    /// executor is given.
    public static void Ensure(Scope scope, IReadOnlyList<ScopeTag> tags, string reader, ScheduleExecutor? executor);
}
```

### 4.8 Cases/  [R3]

```csharp
// Cases/SplitProgram.cs  [R3]
/// A split rule's matcher (a naive RuleProgram whose head is one collector of the key, set and assumption registers)
/// and its assumption template.
internal sealed class SplitProgram
{
    public RuleProgram Matcher { get; }
    public RuleVariants Variants { get; }
    /// Positions in the collected tuple.
    public int[] KeyColumns { get; }
    public int SetColumn { get; }
    /// The element variable's position in the assumption bindings.
    public int ElementColumn { get; }
    public ColumnType ElementType { get; }
    /// Ground once a tuple and an element are bound.
    public Fact[] Assumptions { get; }
}

// Cases/RuleSplitSource.cs  [R3]
/// Relations.ISplitSource for one SplitRule (§6.9.2, E8).
internal sealed class RuleSplitSource : Relations.ISplitSource
{ public RuleSplitSource(EGraph graph, SplitRule rule, int index); }

// Cases/LiftSpecBuilder.cs  [R3]
/// Relations.LiftSpec from TableHandle.Lift and the lattices' case joins; mirror tables and Contradiction excluded;
/// relations listed as functions whose output is unit (§6.9.1).
internal static class LiftSpecBuilder { public static Relations.LiftSpec Build(EGraph graph); }
```

### 4.9 Extraction/ and Rendering/

```csharp
// Extraction/ExtractionCatalog.cs  [R1a]
/// Declares every constructor in Relations.ExtractionSchemas (one per distinct set of heads a cost model marks
/// unextractable, cached), maps FunctionId to ConstructorHandle, and converts cost models.
internal sealed class ExtractionCatalog
{
    public ExtractionCatalog(EGraph graph);
    /// Under the definition lock. Declares the constructor in every cached schema.
    public void Declare(ConstructorHandle constructor);
    public Relations.ExtractionSchema SchemaFor(CostModel costs);
    /// Cached per CostModel instance (a ConditionalWeakTable).
    public Relations.CostModel EngineCosts(CostModel costs);
    public ConstructorHandle HeadOf(Relations.FunctionId function);
    public Relations.Sort SortOf(ColumnType type);
}

// Rendering/SExpression.cs  [R1a]
internal static class SExpression
{
    public static string Render(Term term);
    public static string Render(Fact fact);
    public static string Render(Rule rule);
    public static string Render(Schedule schedule);
    public static string Render(TableRow row);                                                                 // R1b
    public static string Render(SplitRule rule);                                                               // R3
}
```

### 4.10 Internal members of public types

| Type | Internal members |
|---|---|
| `EGraph` | `Relations.EGraph Engine`; `object DefinitionLock`; `object EngineLock` (R1, deleted in R2); `Relations.BaseValues BaseValues`; `UnitColumnType Unit`; `ExtractionCatalog Extraction`; `Relations.ExternalFunctionId NotEqual`; `ConcurrentDictionary<string, LoweredRule> VariantsByDescription`; `int NextSerial()`; `EGraphOptions Options`; `ScopeOrigin RootOrigin`; `TableHandle TableOf(Relations.FunctionId)`; `IReadOnlyCollection<Query> Queries` (live, R1b); `void ForgetQuery(Query)` (frees a disposed query's name, R1b); `void DeferFree(RuleVariants)` and `void FreeDeferred()` (variants a disposal could not free at once: R1b's root adapter freed them under the engine lock before it next prepared an iteration or a query; from R2 `ContextEngineScope.PrepareRules` and `PrepareVariant` free them, §6.4.6); `MirrorRegistry Mirrors` (R2); `Relations.ContextLocalSlot<ScopeRuntime> RuntimeSlot` (R2); `bool InheritWatermarks` (tests, R2); `Relations.ExternalFunctionId Equal` (the `Equal` step's `EqualFunction`, R2, N63); `int ActiveEngineCalls`, `EnterEngineCall()`, `ExitEngineCall()` (engine calls that may run user delegates, which defer a disposed query's free, R2, N69); `void CheckTag(ScopeTag, string)` and `void CheckFactTags(Fact, string)` (another graph's tag, `Fact.In` included, R2, N65); `TableHandle DefineMirrorTable(TableHandle, ScopeTag, int)` (R2, §6.8); `int NextScopeSerial()` (scope paths, R2) |
| `Scope` | `ContextEngineScope Engine` (an `EngineScope` in R1); `ScopeRuntime Runtime`; `ScopeGuard Guard` and `ScopeGuard EnterGuard()` (reads the guard again once entered, R2, N60); `ScopeOrigin Origin`; `void Fault(Exception cause)`; `void ThrowIfNotUsable()` (faulted → InvalidOperationException with `Failure` inner; disposed → ObjectDisposedException); `void ThrowIfNotWritable()` (also frozen, and inside a run observer); `Enter()`, `EnterRead()`, `EnterExtraction()` and `Exit(bool held)` (a call's entry and exit: the guard on a writable scope, the call count, and from R2 the catch-up of `CatchUp`); the open `Write` batch, which makes the scope's own members throw InvalidOperationException (D30), and the count of open batches below it, which makes its writes and runs throw (R2, N88); `void ThrowIfStopped()` (R3); `CompiledRule[] LocalCompiled` (R2); `void BeginCall()` and `void EndCall()` (calls in progress, which defer the release, R2, N71); `void CatchUp()` (brings the scope and each writable ancestor up to date, left out of `Version`, R2, N87); `void ExcludeMirrorSync(long)` (R2, N62); `bool IsMirrorCurrent(ScopeTag, int)` and `void MarkMirrorSynced(ScopeTag, int)` (the sync key: the view stamp and the tag's copy-rule count, R2, N61); `ContextEngineScope CreateTransient()` and `void ReleaseTransient(ContextEngineScope)` (a query's child of a frozen scope, R2, D34); live and transient child counts (R2, §6.7); `TryRelease()` (a no-op in R1, where only the root exists; R2, §6.7); `RuleEvaluationException FaultWith(Relations.PanicException)` (R2) |
| `ScopeTag` | `int Serial` (per graph) |
| `ClassId` | `internal ClassId(uint cell, ScopeOrigin origin)`; `uint Cell`; `ScopeOrigin? Origin` |
| `TableHandle` | `Relations.FunctionId Function`; `bool IsRelation`; `ColumnType[] Columns` (keys then output; a relation ends with the unit type); the public properties' internal setters, which the graph sets once at definition |
| `Primitive` | `InvokeCore`, `IsLambda`, `IsPartial` (§4.3) |
| `Rule` | `CompiledRule Compiled` |
| `RunOptions` | `bool SemiNaive { get; init; } = true` (differential tests, D36) |
| `Query` | `CompiledQuery Compiled` |
| `SplitRule` | `SplitProgram Program`; `RuleSplitSource Source` (R3) |

---

## 5. The engine contract

### 5.1 Engine members the layer uses

All are internal to `Eggnog.Relations` (after E11, without exception) and reached through `InternalsVisibleTo("Eggnog")`.
"Exists" means present at `5acd00e`.

| Member | Where | Used for | From |
|---|---|---|---|
| `EGraph(ParallelHeuristics)`, `ParallelHeuristics(int)`, `ParallelHeuristics.Default` | `Bridge/EGraph.cs`, `Tables/ParallelHeuristics.cs` | Graph construction (`EGraphOptions`) | Exists |
| `EGraph.AddTable(FunctionConfig)`; `FunctionConfig`, `ColumnTy`, `DefaultVal`, `MergeFn` (`AssertEq`, `UnionId`, `Old`, `New`, `Primitive`, `AsLattice`) | `Bridge/` | Definitions (§6.2) | Exists |
| `EGraph.BaseValues`: `RegisterType`, `GetTypeId`, `Get`, `Unwrap`; codecs `Int64Codec`, `BooleanCodec`, `DoubleCodec`, `StringCodec`, `UnitCodec`, `InternedCodec<T>` | `BaseValues/` | Values (§6.1) | Exists |
| `EGraph.RegisterExternalFunction`, `FreeExternalFunction`; `ExternalFunction` | `Bridge/EGraph.Panics.cs`, `Database/ExternalFunction.cs` | Primitives, filters, lattice merges, collectors | Exists |
| `ExecutionState.ReportFailure`, `TriggerEarlyStop`, `View`; `DatabaseView.Owner` | `Actions/ExecutionState*.cs`, `Database/DatabaseView.cs` | D31, `Any`, `ScopeRuntime.Of` | Exists |
| `EGraph.NewRule(string, bool)`; `EGraphRuleBuilder.NewVarNamed`, `NewVar`, `QueryTable`, `QueryPrim`, `Lookup`, `Set`, `Union`, `CallExternalFunc`, `Build`; `RuleEntry`, `RuleVariable` | `Bridge/EGraphRuleBuilder.cs` | Lowering (§6.4) | Exists |
| `EGraph.FreeRule` | `Bridge/EGraph.Rules.cs` | Re-lowering (R1), local rules (R2), disposed queries | Exists |
| `EGraph.RunRules(ReadOnlySpan<EGraphRuleId>)` → `IterationReport` (`Changed`, `RebuildChanged`, `RebuildTime`, `RuleSetReport`); `RuleSetReport.NumMatches`, `RuleSearchAndApplyTime`, `RuleReports` (`RuleReport.Plan`, `SearchAndApplyTime`), `SearchAndApplyTime`, `MergeTime`; `EGraph.ReportLevel`, `ReportLevel.WithPlan`; `PanicException` | `Bridge/`, `Execution/` | One iteration at the R1 root; statistics and plans (D41) | Exists |
| `EGraph.GetRuleInfo`, `RuleInfo.CachedPlan`, `RuleInfo.Query`; `BridgeQuery.BuildCachedPlan`, `AddRulesFromCached`; `Database.NewRuleSet`, `RuleSetBuilder.Build`, `Database.ExecuteRuleSet` (without `MergeRuleSet`) | `Bridge/`, `Execution/Database.Execution.cs` | R1 queries without a merge or a clock tick (D49) | Exists |
| `EGraph.Database`, `Database.CreateEngineState(uint)` (colors M10.4; R1a–R1b used `CreateExecutionState(uint?)`, N59), `Database.RecordFailure`, `Database.ChangeStamp`, `Database.TotalSizeEstimate`; `ExecutionState.Flush`; `EGraph.NextTimestamp`; `TableAction(EGraph, FunctionId)` (`TryLookup`, `Insert`); `UnionAction(EGraph)`; `EGraph.FreshId`; `EGraph.FlushUpdates`; `EGraph.TryTakePanic`; `EGraph.GetCanonInUf`; `EGraph.LookupId`; `EGraph.TableSize`; `EGraph.ForEach` | `Bridge/`, `Database/`, `Contexts/Database.Contexts.cs` | R1 host batches, reads, `Version`, `RowLimit`, `CountRows`/`Rows` (§6.6.5) | Exists |
| `ThreadAffinityGuard` | `Concurrency/ThreadAffinityGuard.cs` | `ScopeGuard` (D47) | Exists |
| `ExtractionSchema`, `FunctionSignature`, `EqSort`, `BaseSort<TCodec, T>`, `Relations.CostModel` (`WithFunctionCost`, `WithNodeCost`, `WithBaseValueCost`), `Extractor.CreateOnDemand`, `ExtractTerm`, `TryExtractTerm`, `TryGetCost`, `Relations.ExtractedTerm`, `ExtractionOptions` | `Extraction/` | Extraction (§6.10) | Exists |
| `EGraph` on a `Store`; `EGraph.Root` (`EGraphContext`); `EGraphContext.RunRules`, `EnsureClean`, `FlushUpdates`, `FreshId`, `StageRow`, `StageUnion`, `Find`, `TryLookup`, `TableSize`, `ForEach`, `IsContradictory`, `Version`, `IsFrozen`, `IsDisposed`, `IsStopped`, `Freeze`; `IterationReport.Stopped`; `ContextOptions` (`Default`, `Case`, `IsCase`, `StopOnContradiction`, `InheritWatermarks`, `Parallelism`); `ContextLocalSlot<T>`, `GetLocal`; copy-on-write `RuleTable` and definitions (X16) | colors §3.3, §10.1–§10.8 | R2 (`ContextEngineScope`) | Colors M10.3 (landed; N43–N50) |
| `CaseSet`, `Lifter`, `LiftSpec`, `LiftTable`, `LiftTableKind`, `LiftOptions`, `LiftReport`, `SplitDriver`, `ISplitSource`, `ICaseSchedule`, `SplitRequest`, `SplitKey`, `SplitOptions`, `SplitReport`, `SplitRecord` | colors §11 | R3 | Colors M10.4 (landed; N51–N58) |

From R2 the rows used only by R1's root adapter have no caller: one iteration through `EGraph.RunRules`, queries
through `GetRuleInfo` and `Database.ExecuteRuleSet`, and host batches and reads through `Database.CreateEngineState`,
`TableAction`, `UnionAction`, `EGraph.FlushUpdates`, `TryTakePanic`, `GetCanonInUf` and `LookupId`. Every scope, the
root included, goes through the `EGraphContext` row; the graph also calls `EGraph.DesignateContradiction` (E4) and
`EGraph.NewLocalSlot` (N48) at construction.

### 5.2 Engine members requested

Each request names the milestone that should provide it. Where the colors spec already implies a member without naming
it, the request names it; any one-to-one equivalent is acceptable, and this spec is then updated. §13 lists the matching
edits to the colors spec.

| # | Member | Why | Milestone |
|---|---|---|---|
| **E1** | `BaseValues.TryGetExisting<TCodec, T>(T value, out Value boxed)`: inline values succeed without the registry; interned values are looked up without inserting (needs `InternTable<TKey>.TryGetId(TKey, out uint)`). `long BaseValues.InternedCount`: the sum of the intern tables' counts. Tests in `BaseValues/BaseValuesTests`: an unseen value fails and leaves the count unchanged; a seen value round-trips; inline values never touch a table. As built: `TryGetExisting` throws as `Get`/`Unwrap` do (`ArgumentNullException` for a null reference key, `InvalidOperationException` for an unregistered type whose value would be interned), and `InternTable.TryGetId` takes the key's shard lock, because a shard can be growing concurrently. | D15: host reads never intern. | **With R1a** (landed, `0fe0477`) |
| ~~E2~~ | **Withdrawn.** A child `Database` sharing the root's `BaseValues` is a defect of colors M9.3 that the rule layer does not depend on (its externals decode with the graph's registry); §13 asks the colors spec to track it. | — | — |
| ~~E3~~ | **Withdrawn** with callbacks (D21). If callbacks return, their collector overrides `ExternalFunction.ReadsTables => true` (colors §10.2), which already makes `ActionReadAnalysis` refuse inheritance. | — | — |
| **E4** | `EGraph.DesignateContradiction(FunctionId function)`: the function must be nullary; maps to `Store.DesignateContradiction`; must be called before any child exists. As built: refused once the store has ever had a child, not only while one exists, with the check and the designation under the store's definition lock, so a first `CreateChild` on another thread cannot interleave; the early stop is appended to a rule's head inserts into the function when the rule is planned, so designate before planning (N43). | Colors §10.1 lists `Contradiction` on `EGraph` without a way to set it. The rule layer calls it at graph construction with its `Contradiction` relation. | Colors M10.3 (landed; N43) |
| **E5** | Extraction over a context: `Extractor.CreateOnDemand(ExtractionSchema schema, EGraphContext context, CostModel? costs = null, ExtractionOptions? options = null)`. Discovery reads the context's `TableView`s (output-column lookups per layer, hidden rows filtered). **Staleness** is keyed on changes to the extraction schema's tables and to the union-find, in the context and its non-frozen ancestors (the layered form of today's per-slot table versions), and never on the clock: it replaces today's `NextTimestamp` check. On a frozen context, any number of extractors may run on any threads. As built: also `Extractor.Context`; staleness is keyed on the tables of the extractor's slots (the schema functions usable under its options), a compaction counts as a change, and only creation requires the context clean; a context-form query runs as a callback of the context's domain, so a cost function may read the context but not write it; `ComputeCostsFromRootSorts` and `FunctionToDag` stay root-only (N44). | The colors spec does not mention extraction; today's extractor reads only root tables (`Database.GetTable`) and goes stale on any clock tick. Every scoped Cosmos scenario extracts, so E5 gates the **whole** R2 scoped suite, not only the extraction tests. | Colors M10.3 (landed, colors §10.10; N44) |
| ~~E6~~ | **Withdrawn.** The rule layer allocates one `ContextLocalSlot<ScopeRuntime>` per graph (colors §10.1) and reaches the running context through `state.View.Owner.Owner.Host` (colors §10.6). If `ContextLocalSlot` cannot construct a `ScopeRuntime` on first use, the only request is a factory parameter (or a setter) on it (§13). | — | — (the factory came with colors M10.3: `EGraph.NewLocalSlot<T>(Func<EGraphContext, T>)`; N48) |
| **E7** | On `EGraphContext`: `EGraphContext CreateChild(ContextOptions? options = null)` and `void Dispose()` with the storage semantics of colors §8.2; `bool IsFaulted`; `EGraphContext? Parent`; `long OwnRowCount` (`context.Database.TotalSizeEstimate`). `RunRules` reports at the graph's `ReportLevel` (colors §10.3's `level`). As built: `CreateChild` of a context that is not frozen flushes it and ensures it clean first, then takes a failure recorded meanwhile, disposing the new child and throwing `PanicException` while the parent stays usable; `EGraphContext.Parallelism` exposes a context's heuristics (N45). | Colors §3.5 has the bridge call `Context.CreateChild`/`Dispose` but names no bridge member; the rest serves faults (D18), case wrapping (§6.9.2), `RowLimit` (D42) and plans (D41). | Colors M10.3 (landed; N45) |
| **E8** | The M10.4 split hooks in this shape (any one-to-one equivalent is acceptable):<br>`interface ISplitSource { void FindRequests(EGraphContext scope, List<SplitRequest> requests, SplitReport report); void Assume(EGraphContext @case, in SplitRequest request, int caseIndex); }`<br>`readonly record struct SplitRequest(SplitKey Key, int CaseCount, object? Payload)`<br>`readonly struct SplitKey : IEquatable<SplitKey> { SplitKey(int source, uint[] values); }`<br>`interface ICaseSchedule { bool Run(EGraphContext @case, int depth, bool allowSplits); }`<br>`interface ISplitDemand { bool Wants(EGraphContext scope, in SplitRequest request); }` (the rule layer passes none, D43)<br>`sealed class SplitDriver { SplitDriver(IReadOnlyList<ISplitSource> sources, ICaseSchedule inCase, ISplitDemand? demand, LiftSpec spec, SplitOptions options); bool Step(EGraphContext scope, int depth, SplitReport report); static void DisposeRecords(EGraphContext scope); }`<br>`SplitOptions { int DepthLimit; int CaseLimit; LiftOptions Lift; ContextOptions CaseOptions; }` (`CaseOptions` must have `IsCase` and `StopOnContradiction`; default `ContextOptions.Case`)<br>`SplitReport { int CasesOpened; int SplitsSkipped; int Lifts; long LiftRowsSkipped; }`<br>`CaseSet { CaseSet(EGraphContext parent); EGraphContext AddCase(ContextOptions? options = null); IReadOnlyList<EGraphContext> Cases; LiftReport Lift(LiftSpec spec, LiftOptions options, CancellationToken cancellationToken = default); int DisposeVacuous(); void Dispose(); }`<br>Semantics: `Step` lets exceptions from an `ICaseSchedule` (cancellation, limits, evaluation failures) propagate and leaves the records it opened for `DisposeRecords`; `Step` does not hold the scope's thread domain while it calls an `ISplitSource` or an `ICaseSchedule`, because the schedule creates children (nested splits; colors §8.3 forbids `CreateChild` while the caller holds the domain); `Lift` checks the token between rounds; `DisposeVacuous` disposes the contradictory cases and returns their number.<br>As built: every member above exists with this shape. `CaseSet` also has `RunEach(schedule, depth, allowSplits)`, `VacuousCount`, `IsDisposed` and `Parent`; `AddCase` and `SplitOptions.CaseOptions` refuse options without `IsCase` and `StopOnContradiction`; a set refuses a frozen parent, and `RunEach`, `Lift` and `DisposeVacuous` refuse a set one of whose cases was disposed outside it. A lift over a set whose cases were all disposed as vacuous contradicts the parent. The lift memo is keyed on the spec, and a lift that its limits cut short leaves none. `Relation` lifts only an output every live case agrees on and refuses an id output; a case join is allowed only on a base-output `Function`. `SplitRecord` also names its `Driver`. `Step` runs and lifts every live record of the scope that its own driver opened, earlier steps' included, and returns true only when it opened cases or a lift changed the scope; B6 compares a record's source object by reference and the key cells, so several drivers can serve one scope (§6.9.3). `Step` refuses a frozen or faulted scope and returns false for a stopped or contradictory scope or at `DepthLimit` (N51–N58). | Colors §11.3 names the types but not their members. `Payload` carries the matched tuple from `FindRequests` to `Assume`; `report` lets a source count the requests it skipped; `CaseOptions` and `AddCase(options)` let `InheritWatermarks` reach cases; `DisposeVacuous` serves host-driven lifts, which never call `RunEach`. | Colors M10.4 (landed, colors §11; N51–N58) |
| **E9** | `ParallelHeuristics` reachable from the bridge `EGraph` constructor on a `Store` (`Store(ParallelHeuristics?)` exists). As built: `EGraph(ParallelHeuristics)` builds the `Store` with them, and `EGraph.ParallelismBudget` holds the root's degree of parallelism for `ConcurrentChildren` (N46). | The root of a Store-backed graph honours `EGraphOptions.DegreeOfParallelism` (D46); children of writable scopes inherit it. | Colors M10.3 (landed; N46) |
| **E10** | `EGraphContext.Freeze(IReadOnlyCollection<EGraphRuleId>? planRules = null)`: `null` keeps step 4 of colors §10.8 as written (plan every live rule without a cached plan); a collection plans exactly those rules that have no cached plan. As built: the freeze flushes only when host writes are staged, and throws a recorded failure as `PanicException`, leaving the context unfrozen (N47). | Plans are made once per rule (decision 7). Step 4 as written plans every live rule, including other containers' local rules, queries and split matchers, on this context's statistics. The rule layer passes the empty set for the root and, for any other scope, the default variants its descendants can run (§6.7). | Colors M10.3 (landed; N47) |
| **E11** | Every public type of `Eggnog.Relations` becomes `internal` (D44). `InternalsVisibleTo` already covers `Eggnog`, `Eggnog.Relations.Tests` and `Eggnog.Relations.Benchmarks`; a public test fixture or benchmark member whose signature mentions an engine type becomes internal or private. | One API in the package: about 95 public engine types include `EGraph`, `Term`, `Sort`, `CostModel`, `Extractor`, `ExtractedTerm` and `NodeCostFunction`, and a file with `using Eggnog; using Eggnog.Relations;` fails with CS0104. | **With R1a** (landed, `0fe0477`; the engine files of colors M9.3–M9.4 merged with it in `edd1fa9` declare no public type either) |
| **E12** | A serial join kept after an early stop. `JoinRun.ReleaseInterrupted()` (`Execution/JoinRun.Reuse.cs`) returns to their pools the sparse and per-stage tuple indexes that the interrupted stages never moved back, and clears every prober. `Database.RunRulesSerial` (`Execution/Database.Execution.cs`) calls it after an early stop (an action's `TriggerEarlyStop`, or a reported failure), then parks and keeps the join as after a full run. A run that throws still drops its join. Test: `AnEarlyStoppedRunKeepsItsJoin` (24.6 bytes per row with the old parking rule restored). | `Any` must stop the engine early (§6.6.4) and must not allocate per row (conventions: no allocation per match). Before E12 an early stop dropped the serial join, so the next run rebuilt it and its leaf-scan buffers. | **With R1b** (landed, `80675d8`; merged at `3b9dda1`, where the kept join releases what each of M10.2's prober pieces owns) |

Nothing else in the engine changes for this layer. In particular the rule layer needs no planner, matcher or index API:
every body is planned and executed by the engine.

---

## 6. Semantics and algorithms

### 6.1 Values, interning, primitives, lattices

**Invariant V (canonical cells).** Within one graph, equal values of one column type have equal cells in every scope,
and distinct values distinct cells. The engine's codecs are canonical (`IBaseValueCodec<T>` contract); the rule layer
adds the normalization the engine lacks: `DoubleColumnType.Encode` maps −0.0 to +0.0 and rejects NaN, because
`DoubleCodec` treats −0.0 and +0.0 as one value and decodes whichever was interned first. Value joins and
not-equals compare cells, which is sound only under V.

**Column types.**

| CLR type | `ColumnType` class | Engine codec | Name | Extraction sort | `Format` |
|---|---|---|---|---|---|
| sort `TSort` | `SortColumnType<TSort>` | none (`ColumnTy.Id`) | as defined | `EqSort(name)` | `#17` |
| `long` | `BaseColumnType<Int64Codec, long>` | `Int64Codec` | `i64` | `BaseSort<Int64Codec, long>` | `42` |
| `int` | `WidenedColumnType<int>` | `Int64Codec` | `i32` | `BaseSort<Int64Codec, long>("i32")` | `42` |
| `bool` | `BaseColumnType<BooleanCodec, bool>` | `BooleanCodec` | `bool` | `BaseSort<BooleanCodec, bool>` | `true` |
| `double` | `DoubleColumnType` | `DoubleCodec` | `f64` | `BaseSort<DoubleCodec, double>` | egglog's (`1.5`) |
| `string` | `BaseColumnType<StringCodec, string>` | `StringCodec` | `String` | `BaseSort<StringCodec, string>` | `"kind"` |
| `ValueSet<T>` | `BaseColumnType<InternedCodec<ValueSet<T>>, ValueSet<T>>` | `InternedCodec<ValueSet<T>>` | `Set<{T's name}>` | `BaseSort<…>` with the format shown | `(set-of "a" "b")` |
| enum `TEnum` | `WidenedColumnType<TEnum>` | `Int64Codec` | `typeof(TEnum).Name` | `BaseSort<Int64Codec, long>(name)` | the member name |
| registered `T` | `BaseColumnType<InternedCodec<T>, T>` | `InternedCodec<T>` | as registered | `BaseSort<InternedCodec<T>, T>` | the given format, else `ToString()` |
| unit (internal) | `UnitColumnType` | `UnitCodec` | `Unit` | none | `()` |

- Every codec is registered with `BaseValues.RegisterType` when the `ValueStore` is created (built-ins) or when its
  `Register*` method runs.
- `int` and enums are stored as `i64` because `Int32Codec` boxes −1 to `Value.StaleRep`, which marks a deleted row
  when it lands in a table's first column (`BaseValues` remarks). `Int64Codec` boxes `[0, 2^31)` inline and interns
  the rest with `InternedTag`, never producing `StaleRep`.
- `ValueSet<T>` is a base value, not a container (D20): its cells are never canonicalized, never rewritten by a
  rebuild, and lift as values. Equality is element-wise; `GetHashCode` combines element hashes in order; elements are
  kept sorted by `ValueSetOrder<T>`, so equal sets have one interned cell.
- Content order (`ColumnType<T>.Compare`, extraction's tie-break): ordinal for strings, numeric for numbers, `false <
  true`, lexicographic over sorted elements for sets, the given comparer for registered types (else
  `Comparer<T>.Default` when `T : IComparable<T>`, else the cell), class id for sorts. Comparing never interns (D15),
  so "the cell" is read with `TryEncodeExisting`: a value never interned sorts after every interned one, and two such
  values compare by hash code (zero when they are equal), a preorder that is stable within the process.
- `ValueStore.Register<T>` refuses `ClassId`, sort types and `ValueSet<>` types (`RegisterSet` makes those), and
  `RegisterSet<T>` refuses an element type that is not `string` and implements neither `IComparable<T>` nor
  `IComparable`, because `ValueSet<T>` keeps its elements in that order. `RegisterSet` and `RegisterEnum` are
  idempotent.

**Interning.**
- `Encode` interns (`BaseValues.Get`, thread-safe, one shard lock on the interned path). `Decode` is lock-free.
- Rule and query constants are encoded when they are compiled, so they intern at definition.
- Host reads use `TryEncodeExisting` (`BaseValues.TryGetExisting`, E1) and never intern. A constant that was never
  interned makes its fact false or its term absent.
- Pools never shrink (D15); `ValueStore.InternedCount` is `BaseValues.InternedCount` (E1).

**Primitives.**
- `Primitive.InvokeCore(arguments, parameters, result)` decodes each argument cell with its parameter's column type,
  calls the delegate and encodes the result (interning a new value). It allocates nothing beyond what the delegate
  allocates and what interning a new value costs.
- A primitive may not take or return a sort (definition error). Classes are compared only with `Is` and `IsNot`.
- Each use of a primitive in a rule is one call site: a `PrimitiveCall` (a let or a head computation) or a `FilterCall`
  (a boolean filter) registered as an engine external function (§6.4.2).
- A call site catches any exception from the delegate (D31):
  1. `ScopeRuntime.Of(graph, state).TryRecord(new EvaluationFailure(rule, what, bindings, exception))`, where `what`
     is `rule '{rule}': primitive '{p}' threw {type}: {message}` (it becomes `RuleEvaluationException.Message`), and
     `bindings` renders the decoded arguments with their source names, a set as `ValueSet<T>.ToString` renders it and
     any other value as its literal, e.g. `d={a, b} v="b"` (elsewhere sets render as `(set-of …)`);
  2. `state.ReportFailure($"rule '{rule}': primitive '{p}' threw {exception.GetType().Name}")`, which triggers the
     engine's early stop;
  3. `result = 0; return false`.
- **Partial primitives.** `Primitives.Add` and `Subtract` use checked arithmetic and are marked partial
  (`Primitive.IsPartial`), as egglog's `+` and `-` are (`checked_add`). An `OverflowException` from one in a body let
  (a `PrimitiveCall` built with `inBody: true`, §6.4.2) means "no result": the call returns false and records nothing,
  which drops the match, as egglog drops a body match whose primitive returns no result. In a head computation it fails
  the rule like any exception. User primitives have no such outcome; a user primitive that throws fails the rule
  (§15).
- Every call site that invokes a user delegate (`PrimitiveCall`, `FilterCall`, `LatticeMerge<T>`, the extraction cost
  adapters, and `HostBatch` when it calls a lattice's `Merge`) sets `EngineCallMarker` around the invocation, on
  whatever thread runs it, pool threads included. Every graph and scope entry point, `ScopeWriter` calls included,
  checks `EngineCallMarker.IsActive` first and throws `InvalidOperationException("Not allowed from a primitive,
  lattice, filter or cost function")` (D47). The identity members (`Graph`, `IsRoot`, `IsFaulted`, `Failure`,
  `IsDisposed`, `Version`, `Path`, `ToString`) do not check, because they only read and enter no guard; nor does
  `Dispose`, which never throws (D40). The cost is one thread-static increment and decrement per invocation.

**Lattices.**
- A function with a lattice gets one `LatticeMerge<T>` external, registered before its table. Its merge is
  `MergeFn.Primitive(mergeId, MergeFn.Old, MergeFn.New).AsLattice()`. `LatticeMerge<T>.TryInvoke` decodes `[old, new]`,
  calls `Merge`, encodes. On an exception it records the failure (rule name `null`), calls `ReportFailure`, and returns
  `old` (the engine keeps the old value on a failed merge).
- A function without a lattice has `MergeFn.AssertEq`: a conflicting rule write is an engine panic ("Illegal merge
  attempted for function f"), surfaced as `RuleEvaluationException` (D31). Host writes check first (D32), the merges
  a batch causes included (§6.6.1).
- `CaseJoin` is adapted to `Func<uint, uint, uint>` by `CaseJoinAdapter<T>` for `LiftTable` (R3).
- The laws are documented on `ValueLattice<T>` and checked by `Verify` in consumers' tests.

| Lattice | `Merge` | `CaseJoin` | Used by (Cosmos) |
|---|---|---|---|
| `ValueLattice.Intersect<T>()` | ∩ | ∪ | `dom`, `types`, `inset`, `admits`, `idom` |
| `ValueLattice.Union<T>()` | ∪ | ∩ | known-sets |
| `ValueLattice.Max<T>()` | max | min | `lo` |
| `ValueLattice.Min<T>()` | min | max | `hi` |

### 6.2 Definitions and their engine layout

Every `Define*` call checks `EngineCallMarker` (D47), takes the graph's definition lock, validates, then (R1) takes the
engine lock to call the bridge. A failed validation changes nothing. Table definitions follow D6's thread rule: while
the root is not frozen, the call enters the root's `ScopeGuard` for its duration (re-entrant on the root's thread,
`InvalidOperationException` from another thread while the root is in use).

**Validation.**
- Names: `NameRules.ValidateTableName` (non-empty, no `@`, not starting with `$`), unique among tables
  (`ArgumentException`). Ruleset and tag names are unique among their kind; `root` is the root tag's. Query names are
  unique among live queries: `Query.Dispose` releases the name, so a per-request query may reuse one.
- Column types: `ValueStore.TypeOf<T>()` for every typed parameter; an unknown type is an `ArgumentException` naming
  the `Register*` method.
- `DefineFunction` with a sort output: `ArgumentException("… use DefineConstructor")`. `TableOptions.Role = Term` on a
  relation or function: `ArgumentException`.
- A constructor's `TSort` must have been defined with `DefineSort`; `cost >= 0`.

**Engine layout** (`FunctionConfig` passed to `Relations.EGraph.AddTable`; `CanSubsume` is always `false`):

| Table | `Schema` | `Default` | `Merge` |
|---|---|---|---|
| `Relation(T1..Tn)` | `[ty(T1)..ty(Tn), unit]` | `DefaultVal.Const(unit)` | `MergeFn.AssertEq` |
| `Function(T1..Tn → TOut)` with lattice | `[ty(T1)..ty(Tn), ty(TOut)]` | `DefaultVal.Fail` | `MergeFn.Primitive(merge, Old, New).AsLattice()` |
| `Function(T1..Tn → TOut)` without | same | `DefaultVal.Fail` | `MergeFn.AssertEq` |
| `Constructor(T1..Tn → TSort)` | `[ty(T1)..ty(Tn), ColumnTy.Id]` | `DefaultVal.FreshId` | `MergeFn.UnionId` |
| mirror `T@tag` (R2) | as `T` | as `T` | as `T` (a lattice function gets its own `LatticeMerge<T>`) |

where `ty(T)` is `ColumnTy.Id` for a sort and `ColumnTy.Base(typeId)` for a value type, and `unit` is
`ColumnTy.Base(unit type)`. The engine function's name is the table's name; the graph's `Contradiction` is the
relation named `contradiction`, defined first by the `EGraph` constructor (so users cannot define a table of that
name).

**Afterwards.**
- The handle is created with `Index` = the number of tables defined before it, and appended to `Tables`
  (copy-on-write).
- A constructor is declared to `ExtractionCatalog`: `FunctionSignature { Inputs = key sorts, Output = EqSort of TSort,
  IsConstructor = true, Cost = cost, Unextractable = (Extractable == false) }`.
- R2: at construction, after defining `Contradiction`, the graph calls `Relations.EGraph.DesignateContradiction` (E4),
  defines the root tag, and allocates `RuntimeSlot`.
- `DefineScopeTag` (R2) takes the definition lock, checks the name, and appends the tag (serial = count) to
  `ScopeTags`.

### 6.3 Compiling a rule

`Ruleset.DefineRule`, `Scope.DefineRule` (R2), `EGraph.DefineQuery` (R1b) and `EGraph.DefineSplit` (R3) run `define`
on a fresh builder, then compile at once under the definition lock. A tag of another graph passed to `In`, `Fact.In`
or `Erasing` throws `ArgumentException` at that call, inside `define`; for `Fact.In`, whose facts may hold no table to
compare the tag with, the call is the builder's `When` or `Then` that receives it (N65). Every compilation error is a
`RuleDefinitionException` naming the rule; the messages below are exact, and `Rules/RuleDefinitionTests` (§8.3) tests
each one.

1. **Variables.** Each `Var<T>` records its owner builder. A variable from another builder:
   *"variable 'x' belongs to another rule"*. A duplicate name: *"variable 'x' is declared twice"*. Each variable gets a
   register typed `ValueStore.TypeOf<T>()`; an unknown `T`: *"type Foo is not registered; call ValueStore.Register"*
   (with the `ArgumentException` as inner exception).
2. **Scoped reads** (R2). The read scope of an application is `handle.ReadScope`, or the tag of the enclosing
   `Fact.In`.
   - Nested `Fact.In`: *"Fact.In cannot be nested"*.
   - A handle scoped to tag A inside `Fact.In(B, …)`: *"'dom@A' is inside Fact.In('B')"*.
   - An application of a table that is not value-only under a read scope:
     *"scoped reads are limited to tables whose columns are all values; 'And' has a sort column"*.
   - The first read of `(T, tag)` creates the mirror (`MirrorRegistry.GetOrCreate`, §6.8), which is a table
     definition (D6). If a live frozen scope tagged `tag` exists in this graph at that moment: *"'dom' is first read in
     scope 'schema' after a scope tagged 'schema' was frozen; define rules that read it before freezing such scopes"*.
   - A scoped atom becomes an atom over the mirror's internal handle, with the tag kept for rendering and
     `ReadScopes`. The mirrors are created last, once every other check of steps 1–9 has passed, and all at once
     (`MirrorRegistry.GetOrCreateAll`: the frozen counts of every new mirror are checked first, then the mirrors are
     made while the definition holds the writable root's guard), so a refused definition registers no mirror and does
     not count as a first read (N64).
3. **Normalization.** `flatten(t)` turns a term into an operand:
   - a variable becomes its register, after unification (a union-find over registers; equating registers of different
     types: *"variables 'a' (i64) and 'b' (String) are equated"*);
   - a value constant becomes `Operand.Constant(type, Encode(value))`;
   - a class constant becomes `Operand.IdConstant(k)` with `WrittenIdConstants[k]` its cell and
     `IdConstantOrigins[k]` its origin (D39). `default(ClassId)`: *"a class constant is default(ClassId), which names no
     class"*. A class of another graph: *"class #17 belongs to another graph"*. A global or split rule whose constant
     is not from the root: *"class #17 came from scope 'root/schema#1/query#2'; a global rule may hold only classes from
     the root; define the rule on that scope"*. A local rule whose constant's origin is not its owner or an ancestor:
     *"class #17 came from scope 'root/schema#1/query#2', which is not 'root/schema#1/query#3' or an ancestor of it"*.
     A disposed origin: *"class #17 came from scope 'root/schema#1/query#2', which is disposed"*. A query's constants
     get the first two checks at definition, as `RuleDefinitionException`s; the origin checks run at each call
     instead, against the scope it runs in (§6.6.4).
   - an application `h(args)` with read scope `s` becomes its output register. Applications with the same `(h, s,
     operands)` share one: functions are functional. On the first occurrence, a fresh register `o` and the atom
     `(h, s, [flatten(args)…, o])` are created.
   - a computation `p(args)` becomes a fresh register `t` bound by the step `Let(p, flatten(args), t)`; computations
     with the same `(p, operands)` share one (primitives are pure).

   Body facts normalize as follows:

   | Fact | Becomes |
   |---|---|
   | relation fact `R(args)` | atom `(R, s, flatten(args))` (no output column) |
   | `a.Is(b)`, two variables | unify `a` and `b` |
   | `x.Is(c)` or `c.Is(x)`, `x` a variable, `c` a constant | substitute `c` for `x` everywhere |
   | `x.Is(h(args))` either side | atom `(h, s, [flatten(args)…, x])`, or unify `x` with the shared output |
   | `h(args).Is(c)` either side | atom with the constant as output |
   | `h(…).Is(g(…))` | flatten both; unify their output registers |
   | `x.Is(p(args))` either side | `Let(p, args, x)` |
   | `p(args).Is(c)` either side | `Let(p, args, c)`: a check against the constant |
   | `p(args).Is(h(…))`, `p(args).Is(q(…))` | flatten the other side to `o`; `Let(p, args, o)` |
   | `c1.Is(c2)` | value constants: evaluated now, by cell (invariant V); false: *"never matches: c1 = c2 is false"*; true: dropped. Class constants are never refused: a global rule runs in every scope a run reaches and a local rule in its owner's descendants (§6.5.2), and a child may union two classes that differ where the rule's constants come from (context = copy). When the two are already one class there (D39: in the owner for a local rule, whose guard the definition holds; in the root for a global rule) and that can be read now (the root frozen, lock-free, or its guard free for this thread), the union holds in every scope the rule runs in: dropped, the rule keeping the first. Otherwise (different classes, a writable root another thread is using (D6, D47), and always for a query, whose classes may come from any scope and are checked against the scope it runs in) the rule keeps the first constant and an `Equal` step keeps only the matches where the two are one class in the running context, which the engine canonicalizes (X19). The outcome of a definition therefore never depends on another thread (N63, N86) |
   | `Fact.Exists(h(args))` | atom with a fresh output register that nothing else reads |
   | `a.IsNot(b)` | `NotEqual(flatten(a), flatten(b))` |
   | `Fact.True(p(args))`, `Where(p(args))` | `Filter(p, flatten(args))` |
   | `Fact.True(t)`, `Where(t)`, `t` not a computation | `Filter(is-true, [flatten(t)])`, with the internal named primitive `is-true` |
   | `Where(a…, lambda)` | `Filter(lambda primitive "<lambda>", flatten(a)…)` |
   | `Fact.In(tag, facts)` (R2) | each inner fact with read scope `tag` |
   | `Rewrite(left, right)` | the body fact `t.Is(left)` for a fresh class variable `$rw{n}` (and the head item of step 8) |

   Substituting two different value constants into one register (through unification) is *"never matches: …"*; two
   class constants are handled as in the `c1.Is(c2)` row.
4. **Range restriction and step order.**
   - Every register in an atom is bound.
   - Steps are placed in rounds. Each round scans the steps in declaration order and places every step whose inputs
     were bound when the round began (by the atoms or by steps of earlier rounds): a `Let` whose target register is
     unbound binds it; a `Let` whose target is bound or constant is a check; a `Filter` or `NotEqual` is placed as soon
     as its inputs are bound. A register a step binds counts from the next round, so the order does not depend on how
     steps were declared within a round. Rounds repeat until none places a step.
   - A step never placed: *"variable 'p' is not bound by any When clause"*, naming its first unbound input in
     declaration order.
   - The placement order is `RuleProgram.Steps`, which the bridge runs after the join in that order (its `Validate`
     then never fails).
5. **Values and sorts.** A primitive whose parameter or result type is a sort:
   *"primitive 'p' takes or returns a sort; classes are compared only with Is and IsNot"*.
6. **Erasing** (R2, D12). The rule's licence `L` is its own `Erasing(L)` tag, else its ruleset's `Licence`.
   - With a licence and no `ReadsOwnFacts`, every atom over a `TableRole.Fact` table (including `Contradiction`) must
     have read scope `L`: *"rule reads fact table 'form' outside scope 'schema'; read it through In(schema), or call
     ReadsOwnFacts with the reason that is sound"*. Atoms over Term and Analysis tables are unrestricted, so rules that
     read only those (boolean simplification, say) are unaffected.
   - `ReadsOwnFacts(reason)` exempts the rule; `Rule.Licence` is then `null` and `Rule.OwnFactsReason` the reason. An
     empty reason: *"ReadsOwnFacts needs a reason"*. Without a licence: *"rule calls ReadsOwnFacts but has no
     licence"*.
   - With R and S (§7), an erasing rule reads facts only from `L`'s view (through mirrors) and from term and analysis
     tables. The residue is stated in §11, risk 2.
7. **Analysis writers** (invariant A). A rule whose head writes an Analysis table may have no atom over a Fact table
   (*"rule writes analysis table 'inset' and may read only term and analysis tables; it reads 'present'"*) and no
   scoped atom.
8. **Head.** Head items are processed in the order the builder calls were made. `eval(t)` turns a head term into an
   operand:
   - a variable is its register; it must be bound by the body or by an earlier `Make`
     (*"head variable 'x' is used before its Make"*, or the range-restriction message);
   - a constant is a constant or `IdConstant` operand (class constants checked as in step 3);
   - an unscoped constructor application `C(args)` is `GetOrAdd(C, eval(args))`, a new head register; repeats of the
     same `(C, operands)` within the head share it;
   - a computation `p(args)` is `Compute(p, eval(args))`, a new head register;
   - a function application: *"actions do not read; match 'dom' in When"*;
   - a scoped application: *"'dom@schema' is read-only"* (invariant R).

   Head facts (`Then`):

   | Fact | Operation |
   |---|---|
   | `R(args)` | `Insert(R, eval(args))` |
   | `F.Of(k).Is(v)`, either side, `F` a function | `Write(F, eval(k) ++ [eval(v)])` |
   | `C.Of(k).Is(x)`, either side, `x` not a constructor application | `Write(C, eval(k) ++ [eval(x)])`: the engine's union-id merge unions an existing output with `x` |
   | `a.Is(b)` of a sort, otherwise | `Union(eval(a), eval(b))` |
   | `a.Is(b)` of a value type that is not a function write | *"equality of two values that is not a function write: a = b"* |
   | `a.IsNot(b)`, `Fact.Exists`, `Fact.True` | *"head facts are writes; 'IsNot' is a filter"* |

   `Make(name, term)`: the variable's register is `eval(term)`. A `Make` variable used in the body: *"head variable 'x'
   made by Make appears in When"*. `Rewrite(left, right)` adds the head fact `t.Is(right)` (so a constructor on the
   right is a `Write`, a variable a `Union`).
9. **Projection.** A query projects its declared variables in declaration order (`NamedRegisters`), through one
   `Collect` head operation; a split matcher projects its key, set and assumption registers.
10. **Output.** A `RuleProgram`, and a `CompiledRule` whose `RuleVariants` immediately lowers the default variant
    (semi-naive, or naive for a `Naive()` rule) with the written constants (§6.4). An engine exception while lowering
    (which would be a defect of the checks above) becomes a `RuleDefinitionException` wrapping it, and nothing is
    registered.

A body with no atoms is legal; the engine fires a semi-naive rule without atoms once (map §3.4), and a naive one at
every iteration.

### 6.4 Lowering a program to the bridge

#### 6.4.1 Entries

For a variant lowered with constants `K` (D36):

| Operand | `RuleEntry` |
|---|---|
| `Register(r)`, a body register | `RuleEntry.Var(var[r])`, where `var[r] = builder.NewVarNamed(type(r).EngineType, name(r))` was created before the first atom |
| `Register(r)`, a head register | the `RuleVariable` returned by the `Lookup` or `CallExternalFunc` that defined it |
| `Constant(type, bits)` | `RuleEntry.Const(new Value(bits), type.EngineType)` |
| `IdConstant(k)` | `RuleEntry.Const(new Value(K[k]), ColumnTy.Id)` (from R2 the bridge lowers it through `QueryEntry.IdConst`, colors §9.8) |

`unit` is `RuleEntry.Const(new Value(graph.Unit.Unit), graph.Unit.EngineType)`.

#### 6.4.2 The builder calls

```
Lower(graph, P, rule, semiNaive, K, description):
    b = graph.Engine.NewRule(description, semiNaive)
    for each body register r used by an atom or a step: var[r] = b.NewVarNamed(...)
    for a in P.Atoms:                                                       // order = the engine's variant order
        cols = a.Columns.Select(entry); if a.Table.IsRelation: cols += unit
        b.QueryTable(a.Table.Function, cols, isSubsumed: false)
    for s in P.Steps:
        Let:      f = register(new PrimitiveCall(graph, P.Name, s.Primitive, types(s.Arguments), type(s.Target), names, inBody: true))
                  b.QueryPrim(f, s.Arguments.Select(entry) ++ [entry(s.Target)], type(s.Target).EngineType)
        Filter:   f = register(new FilterCall(graph, P.Name, s.Primitive, types(s.Arguments), names))
                  b.QueryPrim(f, s.Arguments.Select(entry) ++ [RuleEntry.Var(b.NewVar(unitTy))], unitTy)
        NotEqual: b.QueryPrim(graph.NotEqual, [entry(l), entry(r), RuleEntry.Var(b.NewVar(unitTy))], unitTy)
    for h in P.Head:
        Insert:   b.Set(h.Table.Function, h.Inputs.Select(entry) ++ [unit])
        Write:    b.Set(h.Table.Function, h.Inputs.Select(entry))           // keys, then the value or output
        GetOrAdd: head[h.Target] = b.Lookup(h.Table.Function, h.Inputs.Select(entry), () => $"rule '{P.Name}': get-or-add of '{h.Table.Name}' failed")
        Union:    b.Union(entry(h.Inputs[0]), entry(h.Inputs[1]))
        Compute:  f = register(new PrimitiveCall(..., inBody: false))
                  head[h.Target] = b.CallExternalFunc(f, h.Inputs.Select(entry), type.EngineType, () => $"rule '{P.Name}': primitive '{p}' failed")
        Collect:  f = register(new MatchCollector(graph, h.Inputs.Length, graph.Unit.Unit))
                  b.CallExternalFunc(f, h.Inputs.Select(entry), unitTy, () => $"'{P.Name}': collector failed")
    id = b.Build()
    return new LoweredRule(id, description, semiNaive, K, owned = every f registered above, rule, collector)
```

`register(x)` is `graph.Engine.RegisterExternalFunction(x)`. Notes:
- `QueryPrim`'s last entry binds a register the join did not bind, or else checks equality (map §3.3), so a `Let` is a
  binding or a check exactly as §6.3 step 4 placed it. A result of nothing (a filter's false, a failed call) drops the
  match.
- The bridge runs every `QueryPrim` after the join, on every match of every variant, in recording order, then the head
  actions in recording order (decision 12).
- The contradiction relation is written by an `Insert`; from R2 the bridge appends its early-stop external after every
  head insert into it (colors §10.6).
- Every relation atom carries the unit constant in its output column, which the planner treats as an equality
  constraint.
- No rule-layer external function reads tables (`ReadsTables == false`), so every semi-naive rule may inherit (D4,
  invariant Q).

#### 6.4.3 Descriptions

A variant's description is unique in the graph and keys its statistics (`RuleSetReport.NumMatches`,
`RuleSearchAndApplyTime`, `RuleReports`):

| Program | Description |
|---|---|
| global rule | `{ruleset}/{name}#{serial}` |
| local rule (R2) | `{scope path}:{ruleset}/{name}#{serial}` |
| query | `$query/{name}#{serial}` |
| split matcher (R3) | `$split/{name}#{serial}` |
| mirror copy rule (R2) | `$mirror/{mirror name}#{serial}` |

followed by `/naive` for the naive twin of a semi-naive rule, and by `/k{generation}` for an R1 re-lowering (§6.4.5).
`EGraph.VariantsByDescription` maps each description to its `LoweredRule`.

#### 6.4.4 Choosing variants for an iteration

```
PrepareRules(rules, semiNaive):                     // R1: under the engine lock
    for each compiled rule r, in order:
        mode = semiNaive && r.Rule.IsNaive == false   // a Naive() rule always runs naive
        lowered[i] = PrepareVariant(r.Variants, r.Program.WrittenIdConstants, mode)
    return lowered

PrepareVariant(variants, written, mode):
    graph.FreeDeferred()                            // variants of queries disposed during an engine call (§6.4.6)
    R1: return variants.Get(graph, mode, K = map Find over written)
    R2: return variants.Get(graph, mode)            // lowered once, with the written constants
```

In R1, `RuleVariants.Get` returns the cached variant for the mode when its `Constants` sequence-equal `K`; otherwise it
lowers one (freeing a stale one first), under the rule's lock. From R2 it lowers each mode's variant once, under the
rule's lock, and then returns it with one volatile read: the engine canonicalizes the constants in each context (X19).

#### 6.4.5 Id constants in R1 (D3)

A bare `Database` runs Id constants as written. A rule whose constant `c` has lost a union would then stop matching
`c`'s class (the egglog defect of colors §19 item 4), and an action writing `c` would store a stale cell that no later
rebuild rewrites (colors X38). The R1 root adapter prevents both:
- before every iteration, `PrepareRules` computes `K = [GetCanonInUf(c) for c in WrittenIdConstants]`;
- if the cached variant was lowered with different constants, it is freed (`FreeRule`, then `FreeExternalFunction` for
  its owned functions) and the program is lowered again with `K` and description suffix `/k{generation}`;
- the new engine rule starts at watermark 0, so its first run is the full run D3 requires;
- `IdConstant` operands are the only place class constants appear, so every query and action constant is covered.

R2 deletes this path: Store-backed contexts canonicalize constants per run and gate watermarks on them (X19, I30).
R2's `PrepareRules` passes the written constants, and the variant never changes.

#### 6.4.6 Freeing

- Global rules, split matchers and mirror copy rules live as long as the graph.
- A query lives until it is disposed and no call uses it (D49). The call that leaves it disposed and unused frees it
  at once if it is not inside a user delegate and (R1) can take the engine lock, or (R2) no engine call that may run
  user delegates is in progress on any thread (`EGraph.ActiveEngineCalls`, N69). Otherwise `EGraph.DeferFree` hands the
  variants to the graph, which frees them (`FreeDeferred`) before it next prepares an iteration or a query: in R1 the
  root adapter (§6.6.5), from R2 `ContextEngineScope.PrepareRules` and `PrepareVariant`. A disposal never waits.
- A stale R1 variant is freed when it is re-lowered.
- A local rule's variants are freed when its scope is released (D40, §6.7). No descendant can be running it then,
  because a scope is released only once it has no live children.

### 6.5 Iterations and schedules

#### 6.5.1 One iteration

```
ScheduleExecutor.RunIteration(S, rules, inCase):
    BeforeStep(S)                                       // cancellation, then limits (§6.5.4)
    if rules is empty: return false
    S.Engine.EnsureClean()
    R2: MirrorSync.Ensure(S, tags read by rules, reader, this)        // D48, §6.8
    lowered = S.Engine.PrepareRules(rules, Options.SemiNaive)
    try:
        it = S.Engine.RunRules(lowered.Select(v => v.Id))
    catch Relations.PanicException e:
        failure = S.Runtime.TakeFailure()
        ex = new RuleEvaluationException(failure?.RuleName, failure?.Bindings,
                                         failure?.What ?? e.PanicMessage, failure?.Exception ?? e)
        S.Fault(ex); throw ex
    Statistics.Iterations++
    for v in lowered where v.Rule is not null:
        Statistics.Rules[v.Rule].Add(it.Report.NumMatches(v.Description), it.Report.RuleSearchAndApplyTime(v.Description),
                                     plan of v from it.Report.RuleReports when EGraphOptions.RecordPlans)
    Statistics.SearchTime += it.Report.SearchAndApplyTime; MergeTime += it.Report.MergeTime; RebuildTime += it.RebuildTime
    changed = it.Changed || it.RebuildChanged
    if S == Top: Statistics.LastTopStepChanged = changed; Statistics.TopChanged |= changed
    RecordStep(Iteration, S, changed, elapsed, matched) // §6.11; the rules that matched, with this iteration's figures
    return changed
```

- **Order.** Rules run in the order `RulesFor` returns them, and the engine's writes are idempotent monotone
  insertions (rows, lattice merges, unions, get-or-adds), so the facts after the rebuild do not depend on order; only
  fresh ids and representatives do. A serial graph (`DegreeOfParallelism = 1`, the default) is fully deterministic
  (decision 4).
- **Change.** `Changed` is the rule application's own change and `RebuildChanged` the rebuild's (decision 11); both
  count, because facts a rebuild merges are new to the next iteration. `RunReport.Changed` comes from these flags, not
  from `Version` deltas, so catch-up copies (D16) never count as the run's changes.
- **`Stopped`** (R3, `IterationReport.Stopped`) means the case's stop flag is set: the schedule treats the case as
  contradictory from then on (D45).

#### 6.5.2 Schedule nodes

`Execute(node, S, depth, inCase, splitsEnabled)` returns "changed in `S`". Changes inside cases count for their
parent only through lifts.

| Node | Behaviour |
|---|---|
| `Run(rs…)` | If `S.IsContradictory` and (`S` is a case, or `inCase`, or `Options.StopOnContradiction`): return false, and at the top level set `StopReason = Contradiction`. Otherwise one `RunIteration(S, RulesFor(rs, S), inCase)`. A host `Run` called on a case scope (R3) therefore stops on contradiction too (D11). |
| `Sequence(s…)` | Each step in order; the result is the OR of the steps. |
| `Repeat(n, s…)` | Up to `n` passes of `Sequence`; stops after a pass with no change. |
| `Saturate(s…)` | Passes of `Sequence` until one reports no change. |
| `Until(b, f…)` (R1b) | `EnsureClean`; if every fact of `f…` holds in `S` (`TermReader.Check`, §6.6.3), return false; otherwise `b`. The check is made each time the node executes, so `Saturate(Until(b, f…))` runs `b` until the facts hold or a pass changes nothing. The node takes no step of its own (no `BeforeStep`, no `RunStep`). |
| `Split(rules, inCase)` (R3) | §6.9.3. |

`RulesFor(rulesets, S)` returns, deduplicated by reference: the rulesets' global rules (snapshots, in definition order),
then each scope's local rules belonging to those rulesets from the root down to `S` (R2). Mirror copy rules are never
in it; they run only in syncs (D48). It is recomputed for every iteration, so rules defined concurrently join at the
next one (D6).

#### 6.5.3 `Scope.Run`

```
Run(schedule, options, ct):
    options ??= RunOptions.Default
    a negative IterationLimit, TimeLimit or RowLimit: ArgumentOutOfRangeException
    check EngineCallMarker; enter Guard (InvalidOperationException for a second thread)
    try:
        ThrowIfNotUsable(); ThrowIfNotWritable()        // faulted, disposed, open Write batch, frozen (R2), observer,
                                                        // and (N88) an open Write batch in a descendant
        R2: CatchUp()                                   // each writable ancestor, then this scope; no Version move (N87)
        R3: if Engine.IsStopped: return a report with no iteration and StopReason = Contradiction   // D45
        schedule.Collect(rulesets, conditions)          // R3: and splits
        a ruleset of another graph: ArgumentException   // before any work
        each Until fact: TermReader.Validate(this, fact), ArgumentException on an error   // before any work
        R2: for each tag t read by a reachable rule or split rule (Rule.ReadScopes, SplitRule.ReadScopes):
                ScopeResolver.Resolve(this, t, reader)  // throws before any work
            a rule defined on another thread during the run (D6) that reads a tag an iteration's scope cannot resolve
            is left out of that iteration (N67)
        executor = new ScheduleExecutor(this, options, ct)   // starts executor.Clock
        try:
            schedule.Execute(executor, this, depth: 0, inCase: false, splitsEnabled: true)
            reason = executor.Statistics.StopReason     // Completed unless a Run node recorded Contradiction
        catch ScheduleStopException e: reason = e.Reason
        finally:
            R3: Relations.SplitDriver.DisposeRecords(this context)   // cases made during the call
        return executor.Complete(reason, executor.Clock)
    finally: Guard.Exit()                               // runs releases deferred meanwhile (D40)
```

Everything `Run` refuses, it refuses before the first iteration: a condition that failed only when its `Until` node
executed would leave the earlier steps' writes behind and lose the run's report.

`OperationCanceledException` and `RuleEvaluationException` propagate after the `finally`. Stopping early is sound: the
scope holds fewer facts, each of them valid.

#### 6.5.4 Limits and the report (D42)

- `BeforeStep` runs before every iteration (in any scope of the run, the first included) and every split step, and
  checks, in order:
  `ct.ThrowIfCancellationRequested()`; `Iterations >= IterationLimit`; elapsed `> TimeLimit`; `Top.Engine.OwnRowCount
  + Σ OwnRowCount over LiveCases > RowLimit`. The last three throw `ScheduleStopException(reason)`.
- Limits are per top-level `Run`, across cases. An engine iteration and a lift cannot be interrupted, so a run
  overshoots a limit or a cancellation by at most one iteration or one lift.
- `RunReport.Changed` is `Statistics.TopChanged`: an iteration, split step or lift changed the top-level scope.
- `RunReport.Saturated` is `StopReason == Completed && LastTopStepChanged == false`. `LastTopStepChanged` is set by
  every iteration and split step executed in the top-level scope.
- `RunReport.Rules` holds the statistics of every rule that ran, cases included; mirror syncs, queries and split
  matchers are not rules and are not listed.

### 6.6 Host writes, host reads and queries

#### 6.6.1 Host write batches (D30)

`Make`, `Assume` and `Union` each run one batch of one call; `Write` runs one batch of many:

```
batch(S, body):
    check EngineCallMarker; enter S.Guard
    try:
        S.ThrowIfNotUsable(); S.ThrowIfNotWritable()      // also refused while S has an open Write batch, and (N88)
                                                          // while a descendant of S has one (S.batchesBelow > 0)
        R3: if S.Engine.IsStopped: Assume → return; Union → return false; Make, Write → throw ScopeContradictedException
        S.CatchUp()    // R1: S.Engine.EnsureClean(). R2: each writable ancestor outermost first, then S, every catch-up
                       // left out of its own Version (N87)
        b = new HostBatch(S)
        body(b)        // phase 1, per call as it is made: validate the facts, check class values (D39), encode constants
                       // (interning), resolve existing terms with TryLookup, mint fresh ids for new terms, record the
                       // rows and unions that do not hold already, check conflicts (D32), merges included. An exception
                       // discards the batch: nothing is staged, and b.Origin never commits. In Write, each ScopeWriter
                       // call enters S.Guard and checks EngineCallMarker; a call that throws marks b rejected
                       // (b.Reject()) before the exception leaves it, and S's own members throw while body runs.
                       // Write counts its batch in each writable ancestor of S (N88), and each ScopeWriter call first
                       // brings S up to date (S.CatchUp(), N87): the ancestors' reads and queries may have synced their
                       // mirrors, which writes nothing the batch read.
        if b.IsRejected: return                  // Write only: body caught a rejection; nothing written, no commit
        Write only, if b.IsEmpty == false: S.CatchUp()   // N88: the commit's flush then makes only the batch's changes
        if b.IsEmpty == false:
            try: b.Commit()                      // phase 2: stage new terms' rows in post-order, then rows, then
                                                 // unions; flush; rebuild
            catch Relations.PanicException e:
                f = S.Runtime.TakeFailure()
                ex = RuleEvaluationException(f?.RuleName, f?.Bindings, f?.What ?? e.PanicMessage, f?.Exception ?? e)
                S.Fault(ex); throw ex
        b.Origin.Committed = true                // an empty batch too: no flush, no clock tick, Version unchanged,
                                                 // and the classes its Make calls returned are valid
    finally: S.Guard.Exit()
```

Phase 1 completes before anything is staged, so a rejected fact writes nothing, and every lookup reads committed,
canonical contents.

**The merge check (D32).** A merge of the batch (a union, a sort equality, or a constructor write that gives an
existing key another class) makes the rebuild merge every pair of rows whose keys become equal; for a function without
a lattice, two such rows with different values would be an engine panic and fault the scope. Phase 1 therefore
simulates the closure the rebuild will compute (`Host/HostBatch.Merges.cs`): an overlay union-find over the batch's
cells (canonical or fresh), and a signature table of the rows, stored and planned, whose keys a merge can change. Two
rows of a constructor that meet merge their outputs, which cascades; two rows of a function without a lattice that meet
with different values throw `FactConflictException` before anything is staged. The check starts at the batch's first
merge, and runs only when the graph has a function without a lattice keyed by a sort, since otherwise no merge can
conflict (no Cosmos table qualifies). It then reads every row of the tables a merge can reach (those functions, and the
constructors whose outputs can reach their keys) through `EngineScope.ForEach`: O(rows of those tables), once per batch
that merges.
Lattice merges a union causes are not simulated; they run at the rebuild, so a lattice delegate that throws there
faults the scope (D18).

What "holds already" means, so that a batch of facts that hold stages nothing:
- a relation row: `TryLookup` hits;
- `F.Of(k).Is(v)` with a lattice: the stored `w` satisfies `Merge(w, v) == w` (the lattice's `Merge` runs on the
  calling thread; an exception from it is wrapped in `RuleEvaluationException` with `RuleName == null`, nothing is
  written, and the scope is not faulted);
- `F.Of(k).Is(v)` without a lattice: `w == v` (a different `w` is a `FactConflictException`);
- `C.Of(k).Is(x)`: the stored output and `x` have one canonical form;
- a union: `Find(a) == Find(b)`;
- a new term never holds; a term found by `TryLookup` plans nothing.

Rows and unions repeated within a batch are planned once.

`PlanTerm(t)`:
- a value constant: `Encode(value)`;
- a class constant: `ClassChecks.Require(S, value, …, openBatch: b.Origin)`, then `Find`;
- an unscoped constructor application `C(args)`: plan the arguments first. If they are all existing and
  `TryLookup(C, keys)` hits, the term is the (canonical) output. Otherwise it is new: `FreshId()` mints its id now, and
  `PendingTerm(C, keys, id)` is recorded, memoised by `(C, keys)`, so a batch mints each new term once. Pending terms
  are recorded children first, which is post-order.
- a function application (*"host terms do not read; Get 'dom' first"*), a computation, a variable or a scoped
  application: `ArgumentException`.

`Commit()` stages each pending term's row `keys ++ [id]`, then the rows, then the unions, and flushes. `Make` returns
`Find(id)` after the flush, as a class with `S`'s origin; `ScopeWriter.Make` returns the planned id at once, as a class
with `b.Origin` (valid in later calls of the batch, and once the batch commits, D39). `Union(a, b)` records
`distinct = Find(a) != Find(b)` in phase 1 and returns it.

#### 6.6.2 Host-write facts (`Assume`)

| Fact | Planned as |
|---|---|
| `R(args)`, `R` unscoped | row `R(plan(args) ++ [unit])`, unless it holds |
| `F.Of(k).Is(v)`, either side, `F` a function, `v` a constant | row `F(plan(k) ++ [plan(v)])`, unless it holds. Without a lattice (D32): when every key exists and `TryLookup(F, keys)` holds `w ≠ v`, `FactConflictException(F, keys, w, v)`; the same for two rows of one batch with one key and different values |
| `C.Of(k).Is(x)`, either side, `x` a class constant | an existing key with another class: row `C(plan(k) ++ [Find(x)])`, which the union-id merge unions with the stored output (a merge, checked as above); a key the batch already plans as a pending term: a union of that term's id with `x`; a new key: a pending term whose class is `Find(x)` (no fresh id, no union); nothing when it holds |
| `a.Is(b)` of a sort, otherwise (two constructor applications, or two class constants) | plan both as terms; union, unless equal |
| `a.Is(b)` of a value type that is not a function write | `ArgumentException("value equality is not a write: a = b")` |
| scoped handle, `IsNot`, `Fact.Exists`, `Fact.True`, `Fact.In` | `ArgumentException` |

A write into `Contradiction` is a relation row like any other.

#### 6.6.3 Host reads

Reads check `EngineCallMarker`, enter the guard (none on a frozen scope), check `ThrowIfNotUsable` (which also refuses
a read while the scope's `Write` batch is open, D30) and (R3) `ThrowIfStopped` (D45), bring a writable scope up to date
(`EnsureClean`; from R2 `Scope.CatchUp`, which brings each writable ancestor up to date first and leaves every
catch-up out of `Version`, N87), and never create terms or intern. A call in progress is counted on the scope
(`BeginCall`/`EndCall`), so a release waits for it (D40, N71).

`TryRead(S, t, out cell)`:
- a value constant: `TryEncodeExisting`; a miss means absent;
- a class constant: `ClassChecks.Require(S, …)`, then `Find(id)`;
- an application `h(args)`: read the arguments; a miss means absent. Then `TryLookup(h, args)` in the scope the
  application reads: `S` itself, or for a scoped handle (R2) `ScopeResolver.Resolve(S, tag)`, read directly (not
  through a mirror). A class output is mapped through `S`'s `Find`, which is valid because an id canonical in an
  ancestor names a class of `S`.
- a computation or a variable: `ArgumentException`.

`Check(S, fact)`:

| Fact | Holds when |
|---|---|
| `R(args)` | every argument reads and `TryLookup(R, args)` hits |
| `x.Is(y)` of a sort | both read and `Find` agrees |
| `x.Is(y)` of a value type (a function value against a constant, two function values) | both read and the cells are equal |
| `Fact.Exists(app)` | `app` reads |
| `IsNot`, `Fact.True` | `ArgumentException` (host checks are ground facts, not filters) |
| `Fact.In(tag, facts)` (R2) | every inner fact holds, reading in the resolved scope |

`IsContradictory` is `TryLookup(Contradiction, [])` (on a stopped case, the engine's flag). `Get<T>` and `TryGet<T>`
decode with the term's column type; for a sort they return `TSort.FromId(new ClassId(cell, S.Origin))`. `Find` and
`AreEqual` check every class argument with `ClassChecks.Require`.

#### 6.6.4 Queries (D34, D49)

`DefineQuery` compiles like a rule body (§6.3 steps 1–7) whose only head item is a collector of the declared
variables. Its program is lowered once as a naive variant. A class constant that is `default(ClassId)` or of another
graph is a `RuleDefinitionException` at `DefineQuery`; the origin checks run at each call. `QueryRunner.Run(S, query,
stopAtFirst, sort)`:

```
query.Compiled.Acquire()                                    // ObjectDisposedException if disposed
try:
    check every class constant against S (ClassChecks, D39): ArgumentException naming the query
    R2: ScopeResolver.Resolve(S, t, query.Name) for every t in query.ReadScopes     // before any work (N66)
    enter S.Guard unless S is frozen; ThrowIfNotUsable (an open Write batch included); R3: ThrowIfStopped
    unless S is frozen: S.CatchUp()          // R1: S.Engine.EnsureClean(); R2: no Version move (N87)
    R2: MirrorSync.Ensure(S, query.ReadScopes, query.Name, executor: null)          // D48; moves no Version (N62)
    target = S.Engine                                       // R1 always; R2 when S is writable
    R2, S frozen: target = S.Engine.CreateChild(ContextOptions.Default): a transient child with its own guard, counted
                  in S's transient children and not listed in Children; released in finally (D40)
    buffer = new TupleBuffer(width); runtime(target).QueryMatches = buffer; runtime(target).StopAtFirstQueryMatch = stopAtFirst
    try: target.RunQuery(target.PrepareVariant(query.Variants, query.Program.WrittenIdConstants, semiNaive: false))
    catch Relations.PanicException e: buffer.Dispose(); throw new RuleEvaluationException(...)   // no fault
    finally: runtime(target).QueryMatches = null; release the transient child
    if target.CanonicalizeColumns(buffer.Cells, width, query.SortColumns): buffer.Deduplicate()   // a no-op when clean
    if sort: buffer.SortByContent(query.Columns)            // ForEachMatch only; Any and Count skip it
    return buffer                                           // the caller disposes it
finally: query.Compiled.Release(); exit the guard if entered
```

- R1 runs the query without merging and without advancing the clock (§6.6.5), so neither `Version` nor any extractor
  is affected. From R2, in a writable scope the query is an iteration of that scope that writes nothing: it advances the
  clock, `Version` does not move, and E5's staleness ignores the clock. The mirror sync before it may write mirror rows
  into a writable tagged scope; mirror tables are internal and not extraction tables, so the sync moves neither that
  scope's `Version` (`Scope.Version` leaves the stamp moves of mirror syncs out) nor any extractor (N62). Nor does the
  catch-up the query's entry makes in the scope and its writable ancestors when an ancestor wrote since (N87).
- Allocation: matches go into one pooled `TupleBuffer`, canonicalized in place and ordered through a pooled order, so a
  call allocates nothing per match beyond what the engine run allocates (conventions).
- `Count` is the number of distinct tuples, which are distinct full bindings because the collector projects every
  declared variable.
- `Any` sets `stopAtFirst`; the collector triggers the engine's early stop after the first match. The engine keeps its
  serial join after the early stop (E12), so `Any` allocates no more per row than a full run.
- `ForEachMatch` collects and orders every match before the first callback, then delivers them in content order and
  stops when the callback returns false. The callback may call the scope, writes and runs included. `QueryMatch.Get`
  decodes a class with `S`'s origin and maps it through `S`'s `Find` when it is read (one engine call per class read),
  so a class is canonical even after a union an earlier callback made; two delivered matches may then name one class.
  The query counts as in use until `ForEachMatch` returns.
- In R1 a query whose class constants lost a union is re-lowered like a rule (§6.4.5), through `PrepareVariant`. From
  R2 the variant lowered at definition serves on: the engine canonicalizes the constants per context (X19).
- `Query.Dispose` marks the query disposed, releases its name, and frees its variants (`RuleVariants.Free`) when
  `Users` reaches zero, without waiting (§6.4.6).

#### 6.6.5 The R1 root adapter (`RootEngineScope`)

R2 deleted this adapter, with the engine lock; the table records R1.

Every member that touches the bridge runs under the graph's engine lock (§6.12), and only while the root's guard is
held.

| `EngineScope` member | R1 implementation |
|---|---|
| `Version` | `engine.Database.ChangeStamp` |
| `IsContradictory` | `TryLookup(Contradiction, [])` |
| `IsStopped` | `false` |
| `OwnRowCount` | `engine.Database.TotalSizeEstimate` |
| `EnsureClean()` | nothing: the root is rebuilt after every flush and every run |
| `PrepareRules` | §6.4.4 with `K` from `GetCanonInUf` (§6.4.5) |
| `RunRules(ids)` | `it = engine.RunRules(ids)` → `EngineIteration(it.Changed, it.RebuildChanged, false, it.RuleSetReport, it.RebuildTime)` |
| `FreshId()` | `engine.FreshId().Rep` |
| `StageRow(f, row)` | `batch ??= engine.Database.CreateEngineState(engine.NextTimestamp)` (N59); map the row's sort cells through `GetCanonInUf` (column types from the table handle); `TableAction(f).Insert(batch, row)` |
| `StageUnion(a, b)` | `engine.ActionRegistry.UnionAction.Union(batch, a, b)` |
| `Flush()` | `batch.Flush()`, `engine.Database.RecordFailure(batch)`, `batch = null`. Then `v0 = Version`; `engine.FlushUpdates()`; if `engine.TryTakePanic(out m)`: throw `new Relations.PanicException(m)`. Return `Version != v0`. Never called for an empty batch (§6.6.1). |
| `Find(id)` | `engine.GetCanonInUf(new Value(id)).Rep` |
| `CanonicalizeColumns(cells, width, columns)` | `GetCanonInUf` over the given columns of every tuple, under one hold of the engine lock |
| `TryLookup(f, key)` | map the key's sort cells through `Find`; `engine.LookupId(f, key)`; map a sort output through `Find` |
| `TableSize(f)` | `engine.TableSize(f)` |
| `ForEach(f, visit)` | `engine.ForEach(f, visit)` |
| `TryExtract`, `TryGetCost` | the extractor's `TryExtractTerm`/`TryGetCost` with the class mapped through `GetCanonInUf` |
| `RunQuery(variant)` | Without a merge and without a clock tick: `info = engine.GetRuleInfo(variant.Id)`; `info.CachedPlan ??= info.Query.BuildCachedPlan(engine.Database, info.Description)`; `builder = engine.Database.NewRuleSet()`; `info.Query.AddRulesFromCached(builder, lastRunAt: 0, info.CachedPlan)`; `using rs = builder.Build()`; `engine.Database.ExecuteRuleSet(rs, ReportLevel.TimeOnly, engine.NextTimestamp)`; the collector stages nothing (asserted in DEBUG), so no `MergeRuleSet` follows; if `engine.TryTakePanic(out m)`: throw `new Relations.PanicException(m)` |
| `CreateExtractor(schema, costs)` | `Relations.Extractor.CreateOnDemand(schema, costs, Relations.ExtractionOptions.Default)` |

`PrepareRules` and `PrepareVariant`, which precede every iteration and every query, first free the variants that
disposed queries handed to the graph (`EGraph.FreeDeferred`, §6.4.6), under the engine lock.

The batch is the same mechanism as `EGraph.AddValues`/`AddTerm`: rows stamped with the current timestamp, merged by
`FlushUpdates`, which rebuilds after any union. The difference is that every sort cell is canonical when staged
(invariant C), which the egglog-compatible API leaves to its caller.

#### 6.6.6 The R2 context adapter (`ContextEngineScope`)

| `EngineScope` member | `EGraphContext` member (colors §10) |
|---|---|
| `Version` | `Version` (X30); `Scope.Version` subtracts the stamp moves of the scope's mirror syncs (N62) and of the catch-up each call makes first (`Scope.CatchUp`, N87) |
| `ViewStamp` (internal) | the sum of the change stamps of the context and its ancestors, which keys mirror syncs (§6.8, N61) |
| `IsContradictory`, `IsStopped` (R3) | `IsContradictory`, `IsStopped` (§10.1, §10.5) |
| `IsFaulted` | `IsFaulted` (E7, X12) |
| `Runtime` (internal) | `GetLocal(graph.RuntimeSlot)` (N48) |
| `OwnRowCount` | `OwnRowCount` (E7) |
| `EnsureClean()` | `EnsureClean()` (§10.3) |
| `PrepareRules`, `PrepareVariant` | §6.4.4 with the written constants (X19 canonicalizes them in the engine), after `EGraph.FreeDeferred` |
| `RunRules(ids)` | `RunRules(ids)` → `EngineIteration(Changed, RebuildChanged, Stopped, RuleSetReport, RebuildTime)` |
| `FreshId`, `StageRow`, `StageUnion` | `FreshId`, `StageRow`, `StageUnion` (§10.4; Id cells canonicalized by `ReplayInto`) |
| `Flush()` | `v0 = Version`; `FlushUpdates()`; panics as `PanicException` (§10.4 `ThrowIfFailed`); return `Version != v0`. Flush, `RunRules`, `RunQuery` and `Freeze` count in `EGraph.ActiveEngineCalls`, which defers the free of a disposed query's variants (N69) |
| `Find`, `TryLookup`, `TableSize`, `ForEach` | `Find`, `TryLookup`, `TableSize`, `ForEach` (§10.5) |
| `RunQuery(variant)` | `RunRules([variant.Id])` |
| `CreateExtractor` | `Extractor.CreateOnDemand(schema, context, costs)` (E5) |
| `IsFrozen`, `IsDisposed` | `IsFrozen`, `IsDisposed` |
| `CreateChild(options)` | `CreateChild(options)` (E7); the child's runtime is `child.GetLocal(graph.RuntimeSlot)` |
| `Freeze(planRules)` | `Freeze(planRules)` (§10.8, E10) |
| `Dispose()` | `Dispose()` (E7) |

#### 6.6.7 Class values (D39)

`ClassChecks.Require(S, value, parameter, openBatch)` walks the value's origin:
1. no origin (`default(ClassId)`): `ArgumentException("default(ClassId) names no class")`;
2. another graph: `ArgumentException("class #17 belongs to another graph")`;
3. a batch origin: accepted if it is `openBatch` (a later call of the same batch) or `Committed`; otherwise
   `ArgumentException("class #17 was made by a Write batch that did not commit")`. The walk continues with the batch's
   scope origin;
4. a disposed scope origin: `ArgumentException("class #17 came from scope 'P', which is disposed")`;
5. the origin must be `S.Origin` or an ancestor of it (at most 32 links); otherwise `ArgumentException("class #17 came
   from scope 'P', which is not 'S' or an ancestor of it")`.

Then it returns the cell. Every value the library returns carries the origin of the scope that served the call: `Make`,
`Find`, `Get`/`TryGet` of a sort, `QueryMatch.Get` (the queried scope, even when a transient child ran it),
`ExtractedTerm.Class`, `ExtractedArgument.Value`, `NodeView.Get` (the extractor's scope), and `TableRow` cells. In R1
every origin is the root's, so only rules 1 and 2 can fail.

The checks are conservative: a value obtained from a child is refused in its parent even when its class was made in
the parent, and a value dies when the scope that returned it is disposed, even if an ancestor still holds the class.
The remedy is to obtain the value again from the scope where it is used (§11, risk 12).

#### 6.6.8 Contradicted cases (D45, R3)

`Scope.ThrowIfStopped()` is `if (Engine.IsStopped) throw new ScopeContradictedException(Path)`. Every scope entry point
checks it after `ThrowIfNotUsable`, except the members that still answer:

| Member | On a stopped case |
|---|---|
| `IsContradictory` | `true` |
| `Graph`, `IsRoot`, `IsCase`, `Path`, `Parent`, `Tag`, `IsFrozen`, `IsDisposed`, `IsFaulted`, `Failure`, `Version`, `Children`, `LocalRules` | answer as usual |
| `Assume` | does nothing |
| `Union` | returns `false` |
| `Run` | returns at once: no iteration, `StopReason = Contradiction` |
| `Dispose` | as usual (D40) |
| every other read and write, `Write`, `Make`, queries, `CountRows`, `Rows`, `CreateExtractor`, `Extract`, `CreateChild`, `OpenCases`, `DefineRule`, `Freeze` | `ScopeContradictedException` |

### 6.7 Scopes, tags and local rules (R2)

- **Creation.** `CreateChild(tag, options)`:
  1. `ArgumentException` for another graph's tag, or for `graph.RootTag` (the root's alone, N72); `ThrowIfNotUsable`;
     `ThrowIfStopped` (R3).
  2. A writable scope enters its guard, and refuses a call from a run observer (a structural call, §6.11); a frozen one
     takes none.
  3. Under the scope's children lock: `ObjectDisposedException` if it is disposed; then the engine child is created
     with `new ContextOptions { Parallelism = options?.DegreeOfParallelism is { } n ? new ParallelHeuristics(n) : null,
     InheritWatermarks = graph.InheritWatermarks }` (E7), and the new `Scope` is added to the live children.
  4. The child records its parent and tag, `Path = parent.Path + "/" + (tag?.Name ?? "scope") + "#" + serial`,
     `Origin = new ScopeOrigin(graph, parent.Origin, Path, isBatch: false)`, its runtime (`GetLocal`, with `Owner` set),
     and its guard: the parent's when the parent is writable, a new one when it is frozen (each child of a frozen scope
     is its own thread domain, colors §8.3). On a frozen parent this is thread-safe (colors §8.2).
- **Resolution.** `resolve(S, tag)` is the first scope in `S, S.Parent, …` whose `Tag` is `tag` (by reference);
  `graph.RootTag` is the root's tag. `Run` resolves every tag read by a reachable rule before any work (§6.5.3).
- **Local rules.** `DefineRule(ruleset, name, define)`: `ThrowIfNotUsable`; enter the guard; a frozen scope throws
  `InvalidOperationException("Scope 'root/schema#2' is frozen; its local rules are fixed")`; otherwise compile with
  `Owner = this` and append to the scope's local rules (copy-on-write, under the definition lock). The engine rule is
  global; only `RulesFor` makes it local.
- **Freeze.** `Freeze()`: return if frozen; refuse a call from inside another call of the scope's thread domain (from
  a run observer: `"Not allowed inside a run observer"`; otherwise naming the scope), since step 4 reassigns guards
  that outer call holds; enter the guard; `ThrowIfNotUsable`; `ThrowIfStopped` (R3); a writable parent throws
  `InvalidOperationException("A scope can be frozen only once its parent is")` (the engine would throw too). Then,
  under the definition lock (so that no first scoped read of the tag can create a mirror between steps 1 and 3):
  1. if the scope's tag has copy rules, `MirrorSync.Ensure(this, [Tag], …)` (§6.8);
  2. `Engine.Freeze(planRules)` (E10): the empty set for the root; otherwise the default variants of every global rule,
     every local rule of this scope and its ancestors, every live query, every split matcher and every mirror copy
     rule (the rules a descendant can run; other containers' local rules are excluded). The listed queries are held
     (`CompiledQuery.Acquire`) until the engine has frozen, so a query disposed on another thread meanwhile cannot free
     a listed variant (N70). The engine flushes, rebuilds, plans those rules that have no plan yet on this context's
     statistics, and freezes storage (colors §10.8);
  3. `graph.Mirrors.AddFrozen(Tag)` when the scope has a tag;
  4. each child subtree that shares this scope's guard gets a new `ScopeGuard`, as colors §8.1 step 7 does for the
     engine's domains. This thread holds that guard, so none of those subtrees is in use. From step 2 on, the scope
     reads as frozen, so another thread may already have created a child of it, with a guard of its own, and be using
     it: such a child keeps its guard. A call enters a guard by reading it, entering it, and reading it again
     (`Scope.EnterGuard`): if a freeze reassigned it in between, the call moves to the new one, so no call holds a guard
     its scope no longer has (N60).
- **Dispose and release** (D40). `Dispose()` never throws:
  1. on the root, or when already disposed: return;
  2. under the parent's children lock: set `IsDisposed`, set `Origin.Disposed` (the scope's class values die);
  3. `TryRelease()`.

  `TryRelease()` runs on whichever thread removes the last obstacle (the disposing thread, the thread releasing the last
  child, or the thread exiting the guard):
  - if live children or transient children remain: return (the last child's release calls `TryRelease` again);
  - if the guard is held by another thread: `Guard.DeferRelease(this)` and return (`Guard.Exit` at depth 0 calls
    `TryRelease`);
  - otherwise release: free the local rules' variants (§6.4.6); `Engine.Dispose()`; remove the scope from its parent's
    children; `graph.Mirrors.RemoveFrozen(Tag)` if it was frozen with a tag; then, if the parent is disposed,
    `parent.TryRelease()`.

  An open `CaseSet` and a split's cases are children, so they defer the release too. A call in progress on the scope
  (`Scope.BeginCall`/`EndCall`, counted for reads, queries and extractions, which take no guard on a frozen scope)
  defers it as well, and inside an engine call of the scope's domain the release is handed to the guard's last exit
  (N71). A `Dispose` called from a run observer releases a scope that has no call in progress at once (a child of the
  running scope, say); one of the running scope is deferred by its call.

  A release holds the guard for its engine work under the guard's release lock (`ScopeGuard.TryEnterForRelease`). That
  work runs no user code, so a call that finds the guard held waits on the release lock and tries once more: it fails
  only when a call holds the guard. A `Dispose` on one thread therefore never makes another thread's call of the domain
  fail (N68).
- **Faults.** `IsFaulted` is the rule layer's flag or the engine context's (X12); `Failure` is the rule layer's
  exception, or an `InvalidOperationException` describing the engine's fault. A faulted scope allows only `Dispose`.
- **Depth.** `CreateChild` at depth 31 throws (colors X36).

### 6.8 Scoped reads and mirror tables (R2)

**What a scoped atom means.** `T.In(tag).Of(…)` in scope `C` reads `resolve(C, tag)`'s view of `T`. `T` must be
value-only (D1).

**Mirrors.** For each `(T, tag)` some rule, query or split rule reads, `MirrorRegistry.GetOrCreate` defines:
1. an engine function `T@tag` with `T`'s `FunctionConfig` (for a lattice function, its own `LatticeMerge<T>`), and
   an internal handle of `T`'s kind over it, never listed in `EGraph.Tables`, never lifted, never declared to
   extraction;
2. a copy rule in the internal ruleset `$mirror`: for a relation, `T(x…) → T@tag(x…)`; for a function,
   `T(x…) = v → T@tag(x…) = v` (merged by `T`'s lattice). It is semi-naive and inheritable.

Creating a mirror is a table definition (D6), and it is refused while a live frozen scope of this graph carries `tag`
(the count in `MirrorRegistry`, D38; message in §6.3 step 2).

**Sync before use** (D48). Copy rules never run in `RulesFor`. They run only in syncs:

```
MirrorSync.Ensure(C, tags, reader, executor):
    for t in tags: ScopeResolver.Resolve(C, t, reader)           // every tag, before any sync (§6.13)
    for t in tags:
        S = ScopeResolver.Resolve(C, t, reader)
        if S.IsFrozen: continue                                  // current since its freeze
        rules = graph.Mirrors.CopyRules(t)
        if S.MirrorSyncs[t] == (S.Engine.ViewStamp, rules.Length): continue   // nothing changed since the last sync
        S.Engine.EnsureClean()
        lowered = S.Engine.PrepareRules(rules, semiNaive: true)
        v0 = S.Engine.Version
        S.Engine.RunRules(lowered ids)                           // one iteration of t's copy rules, in S
        S.ExcludeMirrorSync(S.Engine.Version - v0)               // Scope.Version leaves mirror syncs out
        S.MirrorSyncs[t] = (S.Engine.ViewStamp, rules.Length)
        executor?.RecordStep(MirrorSync, S, changed, …)
```

The key is the view stamp, the sum of the change stamps of `S` and its ancestors, not `S.Version`: a host write into
a writable ancestor of `S` changes `S`'s view of `T` without moving `S`'s own version. It also holds the number of the
tag's copy rules, so a mirror created after the tag's last sync forces a new one (N61).

It is called before every iteration in a scope (with the tags its rules read), before every query (§6.6.4), before
every split step's matcher (§6.9.2), and at `Freeze` (with the scope's own tag). `S` is `C` or a writable ancestor of
`C`, so it shares `C`'s thread domain, which the caller holds; nothing else can change `S` until `C`'s engine work
ends. A copy rule reads no mirror, so one iteration brings a mirror current. Syncs are not counted in
`RunReport.Iterations`; with `RecordSteps` they appear as `MirrorSync` steps.

**Host reads never use mirrors**: they read the resolved scope directly (§6.6.3).

**Why it is exact.** Invariant M (§7) states that `C`'s view of `T@tag` equals `resolve(C, tag)`'s view of `T` once
that scope's mirror is current, which the sync guarantees whenever a rule, query or split matcher in `C` reads it.
Within the tagged scope itself the mirror is synced before each iteration, so a scoped read there no longer lags.

**Inheritance.** Mirror tables are ordinary tables, so watermark inheritance needs no tag check (D4). A child tagged
`tag` under a parent tagged `tag` sees the parent's mirror rows as old rows of its own view. Its own copy rule inherits
the parent's watermark and copies only rows newer than it, which the semi-naive variants find.

**Cost.** One copied row per row of `T` in each scope tagged `tag`, and one extra engine iteration per sync, made only
when the tagged scope is writable and has changed. For Cosmos that is the schema's `present` and `dom` rows, a few
dozen per container, copied once at the schema's freeze; queries read a frozen schema and never sync.

### 6.9 Case splits and lift (R3)

#### 6.9.1 The lift spec

`LiftSpecBuilder.Build(graph)` lists, in declaration order, every table with `Lift = true` except `Contradiction`:
- a relation as `LiftTable(f, LiftTableKind.Function, caseJoin: null)`: its output is the unit value, which every live
  case holds, so "every live case agrees" lifts exactly the rows present in every live case, with their output;
- a constructor as `LiftTable(f, LiftTableKind.Constructor, null)`;
- a function as `LiftTable(f, LiftTableKind.Function, CaseJoinAdapter<T>.Create(lattice, type))`, where a missing
  lattice or case join means "every live case must agree".

Mirror tables and internal tables are never listed. The spec is rebuilt when a table is defined (a version check).

#### 6.9.2 Split rules as split sources

`DefineSplit(name, define, caseTag)` compiles the body like a rule (§6.3 steps 1–7) into a naive **matcher** whose
only head item is a collector of the key registers, the set register, and every other register an assumption uses.
`SplitBuilder.Cases(name, set)` names the element variable, bound per case. `Key` defaults to every named body
variable except the set. Assumptions are checked like host-write facts (§6.6.2) with the element and body variables
as parameters; a scoped handle or a function read in an assumption is a definition error. Class constants follow the
global-rule rule (§6.3 step 3). The body is the split's demand (D43): a key the body does not match is never split.

`RuleSplitSource : ISplitSource` (E8):
- **`FindRequests(context, requests, report)`**: `MirrorSync.Ensure` for the matcher's read scopes, then run the
  matcher in the scope (as a query, §6.6.4) and take its tuples in content order. For each tuple, decode the set, with
  elements in `ValueSetOrder` order. An empty set is skipped and counted in `report.SplitsSkipped`. Otherwise add
  `SplitRequest(new SplitKey(index, key cells), elements.Count, payload: (tuple, elements))`.
- **`Assume(case, request, i)`**: wrap the case context in a `Scope` (`IsCase = true`, `Tag = rule.CaseTag`, parent =
  the scope of `case.Parent`, found through its runtime's `Owner`; origin and guard as in §6.7), set the case runtime's
  `Owner`, bind the element variable to `elements[i]` and the other variables from the tuple, instantiate the
  assumptions as ground facts, and run them as one host batch in the case (§6.6.1).

`DefineDomainSplit(name, domain, guards, caseTag)` is a convenience. It builds the split whose body is
`g1(k), …, gn(k), d = domain(k), |d| > 1` (the last with the named primitives `set-length` and `>`), whose cases range
over `d`, whose key is `k`, and whose assumption is `domain(k) = {v}` (with `Primitives.Singleton`).

#### 6.9.3 The split schedule node

```
SplitSchedule.Execute(executor, S, depth, inCase, splitsEnabled):
    if splitsEnabled == false: return false                         // phase A of an enclosing split
    if S.IsContradictory: return false                              // every case would be vacuous
    executor.BeforeStep(S)
    driver = executor.DriverFor(this)          // cached per node: new SplitDriver(Rules.Select(r => r.Source),
                                               //   new ScheduleCaseRunner(executor, InCase), demand: null,
                                               //   LiftSpecBuilder.Build(graph),
                                               //   new SplitOptions { DepthLimit = SplitDepthLimit, CaseLimit = CaseLimit,
                                               //       Lift = Options.Lift, CaseOptions = new ContextOptions { IsCase = true,
                                               //       StopOnContradiction = true, InheritWatermarks = graph.InheritWatermarks } })
    report = new SplitReport()
    changed = driver.Step(context of S, depth, report)
    Statistics.CasesOpened += report.CasesOpened; SplitsSkipped += …; Lifts += …; LiftRowsSkipped += …
    if S == Top: Statistics.LastTopStepChanged = changed; Statistics.TopChanged |= changed
    RecordStep(SplitStep, S, changed, …)
    return changed
```

`ScheduleCaseRunner.Run(case, depth, allowSplits)` finds the case's `Scope` (its runtime's `Owner`), registers it in
`executor.LiveCases`, and executes `InCase` in it with `inCase: true` and `splitsEnabled: allowSplits`. This maps the
colors spec's two phases onto schedules (colors §11.3): phase A runs the in-case schedule with nested split nodes
disabled, then lifts; phase B runs it again in each live case with nested splits enabled (which recurses into `Step` at
`depth + 1`), then lifts again if anything changed. The driver does not hold the scope's thread domain while it calls
the source or the schedule (E8), so cases can be created.

The driver keeps a case's record until the top-level `Run` ends: later executions of the split step skip keys already
covered by a live record in the scope or an ancestor (B6). Coverage compares the record's source object by reference and
the key cells, so a record another split node opened covers a request only when both nodes split through the same rule's
source. Each node's driver runs and lifts only the records it opened, so `Saturate(Split(r1, inCaseA), Split(r2,
inCaseB))` runs `inCaseA` only in `r1`'s cases and `inCaseB` only in `r2`'s (colors §11.3). The cases take in the
parent's new facts through their inherited watermarks. `Run`'s `finally` calls `SplitDriver.DisposeRecords`, which
disposes depth-first in reverse creation order. The rule-layer case scopes then report `IsDisposed`, and their parent's
`Children` no longer lists them.

Because a record keeps its key until the run ends, the split step should come after the facts it reads have settled
(D43): `Saturate(Saturate(facts…), Split(rule, inCase), rewrites…)`. A split taken earlier opens cases for values the
facts later exclude; they go vacuous and are dropped, but their in-case work is done once per extra case.

#### 6.9.4 Host-driven cases

- `Scope.OpenCases()`: `ThrowIfNotUsable`; `ThrowIfStopped`; a frozen scope throws `InvalidOperationException("Scope
  'root/schema#2' is frozen; open cases in a child of it")`; then it wraps `new Relations.CaseSet(context)`.
- `CaseSet.Add(tag)` checks the tag's graph, calls `AddCase(new ContextOptions { IsCase = true, StopOnContradiction =
  true, InheritWatermarks = graph.InheritWatermarks })` (E8) and wraps it as a case `Scope` (origin and guard as in
  §6.7).
- `CaseSet.Lift(options, ct)` calls the engine's `Lift(LiftSpecBuilder.Build(graph), options, ct)`, then
  `DisposeVacuous()`, which disposes the contradictory cases (their `Scope`s report `IsDisposed` and leave `Cases`),
  and maps the report to the public `LiftReport`. With no cases it throws `InvalidOperationException("A lift needs at
  least one case; with none, the scope would be declared contradictory. Assume Contradiction explicitly if that is
  meant.")` (D9).
- `Dispose` never throws; it disposes the engine set, and so every case.

#### 6.9.5 Coverage

Coverage is the caller's obligation, stated on `SplitBuilder`, `DefineDomainSplit`, `OpenCases` and `CaseSet`. For the
domain split:
- the body establishes "`k` satisfies every guard and its value is in `d`";
- domains only narrow, so cases made for an older, wider `d` still cover the scope;
- a case for a value that has since been removed becomes vacuous once it catches up: its domain meets to ∅, and the
  theory's contradiction rule fires.

Extra guards only remove splits, so they never break coverage (D43). Lift soundness and completeness are the engine's
(I33).

### 6.10 Extraction

- **Schema.** `ExtractionCatalog` keeps one `Relations.ExtractionSchema` per distinct set of heads that a cost model
  marks with `Unextractable` (the empty set is the common case). Every schema declares every constructor (§6.2), with
  `Unextractable = true` for the constructor's own `Extractable == false` or the model's set. Relations, functions and
  mirrors are never declared.
- **Costs.** `EngineCosts(model)` starts from `Relations.CostModel.Default` and applies:
  - `With(head, cost)` → `WithFunctionCost(head.Function, cost)`;
  - `With(head, NodeCostFunction f)` → `WithNodeCost(head.Function, (fn, row) => f(new NodeView(head, row)))`;
  - `WithValueCost(type, cost)` → `WithBaseValueCost(catalog.SortOf(type), cost)`;
  - `WithValueCost<T>(type, f)` → `WithBaseValueCost(sort, (s, v) => f(type.Decode(v.Rep, scope)))`.

  Defaults: the declared constructor cost, and 1 per value (egglog's `TreeAdditiveCostModel` with `base_value_cost =
  1`), so `StrEq "a" "b"` costs 3. Negative costs throw `InvalidOperationException` (`CostMath`).
- **Creating.** `Scope.CreateExtractor(costs)`: `ThrowIfNotUsable`, `ThrowIfStopped` (R3), the guard on a writable
  scope, `EnsureClean` (from R2 `Scope.CatchUp` on a writable scope, N87), then
  `Engine.CreateExtractor(SchemaFor(costs), EngineCosts(costs))`. R1 uses `CreateOnDemand(schema, costs)` over the root;
  R2 uses the context form (E5). Each extraction through the extractor takes the guard on a writable scope and none on a
  frozen one, is counted so that a release waits for it (N71), and holds the extractor's own lock (N73); it does not
  catch up, since a scope that changed makes the extractor stale.
- **Extracting.** `Extract<TSort>(root)`: `ClassChecks.Require(scope, root.Id, …)`, then the engine's
  `ExtractTerm(catalog.SortOf(TSort), new Value(Find(root)))`, through `EngineScope.TryExtract` (and `TryGetCost`),
  which canonicalize the class and extract in one engine call. The result is wrapped into `ExtractedTerm`:
  - `Head` is `catalog.HeadOf(Function)` and `Class` the node's class (the extractor scope's origin);
  - children of kind `Node` become `ExtractedTerm`s, shared through a per-extractor map from engine terms;
  - children of kind `Base` become decoded values (`Value<T>(i)`, `Argument(i)`, `ExtractedArgument<T>.Value`).

  `ToString()` is the engine term's rendering: egglog's extraction output, with literals formatted by each column
  type's extraction sort. No finite term: `InvalidOperationException` (`TryExtract` returns false). `TryMatch` compares
  `Head` by reference and fills each `ExtractedArgument<T>` with the decoded value, or the child's class and term.
- **Ties** are the engine's content ties (D10).
- **Staleness.** The engine throws `InvalidOperationException("The e-graph changed since the extractor was created;
  create a new one.")` once the scope changed. In R1 that check includes the clock, so the rule layer never ticks it
  without a change outside a `Run`: queries run without a merge (§6.6.5) and empty batches do not flush (§6.6.1). A
  `Run` ticks the clock even when it changes nothing, so it may invalidate an extractor in R1. E5 keys R2's staleness on
  the extraction tables and the union-find instead. The public contract covers both: reads, queries and writes that
  change nothing never invalidate an extractor; a changing write or a `Run` may.

### 6.11 Diagnostics (D41)

- **Per-rule statistics.** Every iteration adds, for each rule-layer rule it ran, the engine's match count and
  search-and-apply time for the rule's variant (by description), and, with `EGraphOptions.RecordPlans` (which sets the
  engine's report level to `WithPlan` at graph construction), the plan text from `RuleSetReport.RuleReports`;
  `RuleStatistics.Plan` keeps the latest one recorded. The run's search, merge and rebuild times sum
  `RuleSetReport.SearchAndApplyTime`, `MergeTime` and `IterationReport.RebuildTime`.
- **Steps.** `ScheduleExecutor.RecordStep` builds a `RunStep` for every iteration, mirror sync (R2), split step and lift
  (R3), in any scope of the run. Every step takes an index, recorded or not. An iteration's `Rules` are the rules that
  matched in it, each with that iteration's figures (`IterationsWithMatches = 1`), copied into a read-only list. With
  `RecordSteps` the step is appended to `RunReport.Steps`; with an `Observer` it is passed to it at once, on the run's
  thread, between engine calls. While the observer runs, the guard's `InObserver` flag is set. Reads of the run's scopes
  are allowed, since the guard is re-entrant (`Check`, `Get`, `Find`, queries, `CountRows`, `Rows` and extraction).
  Writes, `Run` and every other call that changes a scope (`CreateChild`, `Freeze` and `DefineRule` included) throw
  `InvalidOperationException("Not allowed inside a run observer")`, except `Dispose`, whose release waits for any call
  in progress on the scope (§6.7). An exception thrown by the observer propagates out of `Run` after the `finally` and
  does not fault the scope.
- **Rows.** `CountRows(table)` is `Engine.TableSize(table.Function)`. `Rows(table)` visits `Engine.ForEach`, decodes
  each row (sort cells as classes with the scope's origin), and sorts by content. Both take the guard on a writable
  scope, check `ThrowIfStopped` (R3), and reject mirror handles (never reachable) and handles of another graph
  (`ArgumentException`).
- **Failures.** `Scope.Fault(cause)` sets `IsFaulted` and `Failure`; `ThrowIfNotUsable` throws
  `InvalidOperationException($"Scope '{Path}' is faulted: {Failure.Message}", Failure)`.
- **Explaining a rewrite** (the recipe of D41). With `RecordSteps = true` and `EGraphOptions.RecordPlans = true`, the
  first step whose `Rules` contains the deleting or lowering rule is the iteration that first applied it;
  `Rule.ToString()` gives its body and head, `Rows` the facts it can have read, and `RuleStatistics.Plan` how the engine
  joined them.

### 6.12 Concurrency

- **Guards** (D47).
  - Each thread domain has one rule-layer `ScopeGuard`. A writable scope's children share its guard; each child of a
    frozen scope gets a new one; freezing a scope gives each child subtree that shares its guard a new one (§6.7). This
    mirrors colors §8.3.
  - Every `Scope` member except the identity members (with R2's tree members `Parent`, `Tag`, `IsFrozen`, `Children`
    and `LocalRules`, which answer snapshots), the reads of a frozen scope, and `Dispose` enters the guard for the
    whole call: a host batch, a run, a query on a writable scope, a definition of a local rule. So does every
    `ScopeWriter` call, for its own duration; a refused one rejects its batch (D30). A second thread gets
    `InvalidOperationException("Scope 'root/schema#2' is in use on another thread; only children of a frozen scope may
    run concurrently")`. The root has the same guard in every milestone.
  - Within one domain, a `Write` batch's callback may call other scopes of the domain between the batch's calls. The
    writable ancestors of the batch's scope refuse writes and runs until the batch ends, since they would change what
    the batch's calls were checked against; their reads, queries, `CreateChild` and `DefineRule`, and writes and runs
    in descendants and siblings, are allowed, and the batch catches up with them before each call and before it commits
    (N88).
  - The rule layer never holds an engine thread domain across calls; each engine call enters the domain itself. So a
    split may create cases (colors §8.3 forbids `CreateChild` while the caller holds the domain), and E8 asks
    `SplitDriver.Step` not to hold it while calling sources and schedules.
- **Definitions.**
  - Every `Define*`, `Ruleset.DefineRule` and `Scope.DefineRule` call takes the graph's definition lock. Rule-layer
    collections (`Tables`, `Rulesets`, `ScopeTags`, `Ruleset.Rules`, local rules, mirror rules, queries) are published
    copy-on-write, and runs read snapshots without locks.
  - Rule, query, split and tag definitions: any thread, any time; they join the next iteration (D6).
  - Table definitions, and from R2 rule definitions whose first scoped read creates a mirror: while the root is not
    frozen, they enter the root's guard (re-entrant on the thread using the root; `InvalidOperationException` from
    another thread while the root is in use). This is what `Store.AddTable` does in R2 (colors §4.3), and R1 enforces
    it the same way. Once the root is frozen, any thread.
  - **R1**: bridge mutations (tables, external functions, rules) also take the graph's engine lock, which root engine
    calls hold one call at a time. A rule definition made during a run waits for the current engine call and joins the
    next iteration. Lock order: definition lock, then engine lock. No user code runs while the engine lock is held
    except the delegates the engine invokes, which may not call into a graph (below); observers run between engine
    calls, outside the lock. So no lock cycle exists.
  - **R2**: the engine lock is gone. The bridge's definitions are concurrent (X16, copy-on-write `RuleTable`,
    external-function registry).
- **Engine-invoked delegates.** Primitives, lattice merges, filters and cost functions run inside engine calls, possibly
  on pool threads. A call from one into any graph or scope throws `InvalidOperationException` (`EngineCallMarker`,
  §6.1).
- **Frozen scopes.** Reads take no guard. Queries run in transient children (D34), with `ContextOptions.Default` (the
  frozen scope's own `DegreeOfParallelism` does not apply to them), and extractors may run on any threads (E5), so a
  frozen schema can be read, queried and extracted from many threads. One `Extractor` instance is thread-safe too: its
  calls are serialized on a private lock, because the engine extractor's caches are shared; use one per thread for
  parallel extraction (N73).
- **Disposal.** `Dispose` may be called from any thread; the release waits for live children and for a call in
  progress on another thread (D40), and never makes another thread's call fail (§6.7, N68).
- **Values.** `BaseValues` interning is thread-safe. `ValueSet<T>` is immutable.
- **External functions** (call sites, lattice merges, collectors) may run on several rule tasks at once. Their state
  is immutable except the scope runtime's buffers, which lock.
- **Cases** run on their parent's thread (D17).

### 6.13 Errors at a glance

| Condition | Result |
|---|---|
| A definition error (§6.3) | `RuleDefinitionException` (rule name, exact message) |
| A tag, handle or class value of another graph; `default(ClassId)`; a class value from a scope that is not this one or an ancestor, or is disposed, or from an uncommitted batch (D38, D39) | `ArgumentException` |
| Bad host write or read (non-ground term, function read, scoped write, filter as a host fact, value equality that is not a write) | `ArgumentException`; nothing written |
| A host write giving a function without a lattice a second value, directly or through a merge of its batch (D32) | `FactConflictException`; nothing written |
| A rejected `ScopeWriter` call that `write` caught | `Write` returns normally; nothing written (D30) |
| A scope member called while that scope's `Write` batch is open | `InvalidOperationException` |
| A write or run into a writable ancestor of a scope whose `Write` batch is open (N88) | `InvalidOperationException`; nothing written, and the batch is not rejected |
| A `ScopeWriter` call after its batch ended (N82) | `ObjectDisposedException` |
| Unknown CLR type; `Register` of `ClassId`, a sort or a set; `RegisterSet` of an element type without an order | `ArgumentException` (naming the `Register*` method to call, where there is one) |
| A ruleset of another graph, or an `Until` fact the scope cannot read (an unresolved tag in it included), at `Run` | `ArgumentException`, before any work |
| `Schedule.Until` with no fact, or with a filter, a variable or a computation | `ArgumentException` |
| A negative `IterationLimit`, `TimeLimit` or `RowLimit` | `ArgumentOutOfRangeException` from `Run`, before any work |
| A query name a live query has | `ArgumentException` |
| A primitive or lattice merge throws, or a rule's write conflicts on a function without a lattice, during a run or a write | `RuleEvaluationException`; the scope is faulted (D18) |
| A built-in partial primitive (`+`, `-`) overflows in a body let | not an error: the match is dropped (D31) |
| The same during a query | `RuleEvaluationException`; the scope is not faulted (D49) |
| Any use of a faulted scope except `Dispose` | `InvalidOperationException` with `Failure` as `InnerException` |
| Use of a disposed scope or query | `ObjectDisposedException` |
| `Dispose` of anything | never throws (D40) |
| Two threads on one thread domain | `InvalidOperationException` (rule-layer guard, D47) |
| A table definition from another thread while the root is writable and in use | `InvalidOperationException` |
| A call into a graph from a primitive, lattice, filter or cost function | `InvalidOperationException` |
| A write, run or structural call (`CreateChild`, `Freeze`, `DefineRule`; R3 `OpenCases`) from a run observer | `InvalidOperationException("Not allowed inside a run observer")` |
| `Run`, a host write or `DefineRule` on a frozen scope (R2) | `InvalidOperationException` |
| Freezing under a writable parent, or from inside another call of the scope's thread domain (N79); `OpenCases` on a frozen scope (R2, R3) | `InvalidOperationException` |
| `CreateChild(graph.RootTag)` (R2, N72) | `ArgumentException` |
| An unresolved tag at `Run` or at a query (R2) | `InvalidOperationException`, before any work; a rule defined during the run whose tag the iteration's scope cannot resolve takes no part in it (N67) |
| A first scoped read after a scope of this graph with its tag was frozen (R2) | `RuleDefinitionException` |
| A read or write needing a model on a contradicted case (R3, D45) | `ScopeContradictedException` |
| A lift with no cases (R3) | `InvalidOperationException` |
| No finite term | `InvalidOperationException` (`TryExtract` returns false) |
| A stale extractor | `InvalidOperationException` (the engine's) |
| Cancellation | `OperationCanceledException` after cleanup |
| A limit | not an exception: `RunReport.StopReason` |

---

## 7. Invariants

### 7.1 Invariants this layer owns

- **V (canonical cells).** Within one graph, equal values of a column type have equal cells everywhere, and distinct
  values distinct cells (§6.1). Value joins, constants and not-equals compare cells.
- **C (canonical writes).** Every sort cell the layer stages is canonical in the target scope or freshly minted.
  - R1: `RootEngineScope.StageRow` maps every sort cell through `GetCanonInUf`.
  - R2: `EGraphContext.ReplayInto` canonicalizes (colors §10.4), and children canonicalize again at the merge (X38).
  - With the engine's guarantee that a clean context's rows are canonical (colors I13, I35), class joins compare cells.
  - v1's invariant C (canonical identifiers in registers) is the engine's executor's, over canonical rows.
- **R (writes are never scoped).** No head operation, host write or split assumption targets a scoped handle (§6.3
  step 8, §6.6.2); mirror tables are written only by their copy rules. Actions write only to the running scope, and an
  ancestor's view never contains its descendants' rows. So nothing a rule derives in `S` changes what an atom scoped to
  a strict ancestor of `S` reads.
- **S (scoped reads are value-only).** Every scoped atom reads a table with no sort column (§6.3 step 2). No class
  register is ever bound by a scoped atom, so v1's invariant H ("a class value is joined within one scope") holds
  without a per-variable check, and no class identifier crosses a scope boundary inside a rule.
- **M (mirrors are exact).** Let `T` be value-only, `t` a tag, `C` a scope and `S = resolve(C, t)`. Call `S`'s mirror
  *current* when no row of `S`'s view of `T` is newer than `S`'s last run of the copy rule. Then
  `view_C(T@t) = view_S(T)`, as row sets for a relation and as key-to-value maps for a function.

  *Proof sketch.*
  1. `T@t` is written only by copy rules, which run only in syncs of scopes tagged `t` (a sync of `t` runs in
     `resolve(·, t)`, which carries `t`). No scope strictly between `S` and `C` carries `t` (`S` is the nearest), so no
     layer below `S` on the path to `C` writes `T@t`.
  2. A descendant copies an ancestor row into its own layer only when the row mentions one of its own losers, or
     shares a key with one of its own rows (colors §7.2 branches (a) and (b), §7.3 step 4). `T@t` has no sort cell,
     and descendants below `S` hold no rows of it, so neither applies. Re-stamping (c′) changes no content. Hence
     `view_C(T@t) = view_S(T@t)`.
  3. In `S`, every row of `T@t` was copied, in `S` or in an ancestor `A` tagged `t`, from a row of `view_S(T)` or
     `view_A(T)`. Contexts see their ancestors' facts (colors I20), so `A`'s values are above `S`'s in the lattice
     order. Currency means `S`'s copy rule has matched every row of `view_S(T)` (the semi-naive variants find every row
     newer than the rule's watermark, I31). For a relation the row sets are therefore equal. For a function the
     mirror's value is the meet of `S`'s value with values above it, which is `S`'s value, by idempotence and
     absorption.

  The sync of D48 makes the mirror current whenever a rule, query or split matcher in `C` reads it: a writable `S`
  shares `C`'s thread domain, is synced when its view stamp (the change stamps of `S` and its ancestors) or its tag's
  number of copy rules changed since its last sync (N61), and cannot change during `C`'s engine work; a frozen `S` was
  synced at its freeze and never changes. ∎
- **Q (actions are query-determined).** Rule-layer actions never read a table that can change:
  - `GetOrAdd` lowers to the bridge's `Lookup` on a constructor (`LookupOrInsertDefault` with `UnionIdMerge`);
  - lets, filters, head computations and collectors are external functions with `ReadsTables == false`;
  - function reads in heads are definition errors.

  So `ActionReadAnalysis.IsQueryDetermined` holds for every rule-layer rule (colors §10.2), and I31's precondition is
  met by construction.
- **E (erasing).** A rule whose effective licence is `L` (its own, or its ruleset's, D12) reads fact tables only
  through `L` (§6.3 step 6); an exemption is explicit (`ReadsOwnFacts`) and visible in `Rule.ToString`. With R, S and M,
  an erasing rule reads facts only from `L`'s view and from term and analysis tables.
- **A (analysis writers).** A rule that writes an analysis table reads no fact table and no scoped atom (§6.3 step 7),
  so analyses depend only on terms.
- **O (class values are scope-checked).** Every class cell the layer passes to the engine from a host call, a rule
  constant or a query constant came from a `ClassId` whose origin is of the same graph, not disposed, committed (for a
  batch), and the scope or an ancestor of it (§6.6.7). An id visible in an ancestor names a class of every descendant
  (colors I20), so every such cell is meaningful where it is used.

### 7.2 Invariants delegated to the engine

| v1 invariant | Now | Where |
|---|---|---|
| K (Id constants) | Executed constants equal the canonical forms in the running context; a watermark is used only with equal constants | Colors I30, X19 (R2+); §6.4.5 in R1 |
| S1 (stamp completeness) | Rows visible with `ts < W` were visible to the run at `W`, unchanged | Colors I19–I21 |
| SN (watermark) | After a run at `W`, every match over rows older than `W` has been applied | Colors I31, Case 1 |
| INH (inheritance is sound) | A seeded watermark `min(W_A, CreatedAt)` misses no match | Colors I31, Case 2 (preconditions met by Q) |
| Deduplication | Variants partition the matches of an iteration | Colors I27, Lemma 1 of I31 |
| L1–L4 (lift) | Soundness; completeness of facts (with the L1 winner probe) and of equalities; termination | Colors I33, I32 |
| Stop in cases | A contradicted case answers only `IsContradictory` and is skipped and disposed | Colors I25, L4; the rule layer's answers are D45's |
| Extraction | Optimal, acyclic, content ties | `Extraction/Extractor.cs` (D10) |

---

## 8. Test plan

All tests are in `src/Eggnog.Tests`, xunit v3, one type per file (partial classes may span files), namespace
`Eggnog.Tests` (subfolders only organise). Golden and differential tests use `new EGraph()`, which is serial
(DegreeOfParallelism 1, D46). Every `check` of a prototype becomes `Assert.True`, and every `(fail (check …))` an
`Assert.False`. Milestones are in brackets.

### 8.1 Cosmos conformance

#### 8.1.1 The literal suite [R1a, R1b]

`Cosmos/Literal/` ports `prelude.egg` and the seven scenario files as written: contexts as data (`Ctx` with `Schema`,
`Query`, `Assume`), inheritance by `horn` rules, and splits and lifts written as rules in the `split` ruleset. It runs
entirely at the root on today's engine and needs no engine case split. Each `(push)`…`(pop)` block is a fresh graph
built from the prelude plus the file's top-level commands before the block. Files 01–03 land in R1a; 04–07 in R1b.

Files:
- `Cosmos/Pred.cs` (shared with the scoped suite: `readonly record struct Pred(ClassId Id) : ISort<Pred>`);
- `Cosmos/Literal/Ctx.cs` (`readonly record struct Ctx(ClassId Id) : ISort<Ctx>`);
- `Cosmos/Literal/PrototypeTheory.cs`, the prelude, one instance per graph: every relation, function, constructor and
  rule of `prelude.egg`, with the rulesets `Horn`, `Lattice`, `Split` and `Rewrite`, and an instance method `Ground()`
  that assumes `(parent (Query) (Schema))` at its graph's root;
- `Cosmos/Literal/PrintSize.cs` [R1b], which compares every table's `CountRows` with egglog's `(print-size)` at the same
  point of a scenario;
- `Cosmos/Literal/Literal01DiscriminatedOneOfTests.cs` … `Literal07SweepLinksTests.cs`.

Translation choices, which the porting table in §12 generalizes:
- `(relation contradiction (Ctx))` becomes `Relation<Ctx> Contradicts` named `contradicts`, because the graph's own
  `Contradiction` (nullary) owns the name `contradiction`.
- Sets are `ValueSet<string>`. `(set-of v)` is `Primitives.Singleton<string>()`, `set-union`/`set-diff`/`set-remove`/
  `set-contains`/`set-length` the named primitives, and `(= 0 (set-length (set-diff d s)))` is
  `Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, s)).Is(0L)`.
- A head that mentions `(Assume c p v)` is a constructor application in the head, which is a get-or-add.
- `(present (Query) p)` in a head writes through the get-or-add of `Query.Of()`.
- The literal suite keeps the prototype's schedules as written, `(saturate horn lattice split rewrite)` included, so
  its figures are egglog's.

For example, the split rule and one lift rule of the prelude:

```csharp
Split.DefineRule("split on a discriminator", r =>
{
    var (p, v, c, d) = r.Vars<string, string, Ctx, ValueSet<string>>("p", "v", "c", "d");
    r.When(Discriminator.Of(p, v), Present.Of(c, p), d.Is(Dom.Of(c, p)));
    r.Where(Primitives.Greater.Of(Primitives.Count<string>().Of(d), 1L));
    r.Where(Primitives.Contains<string>().Of(d, v));
    r.Then(
        Parent.Of(Assume.Of(c, p, v), c),
        Present.Of(Assume.Of(c, p, v), p),
        Dom.Of(Assume.Of(c, p, v), p).Is(Primitives.Singleton<string>().Of(v)));
});
Split.DefineRule("lift a stored form", r =>
{
    var (c, p, q, f) = r.Vars<Ctx, string, string, string>("c", "p", "q", "f");
    var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
    r.When(s.Is(FormCases.Of(c, p, q, f)), Present.Of(c, p), d.Is(Dom.Of(c, p)));
    r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, s)).Is(0L));
    r.Then(Form.Of(c, q, f));
});
```

Tests (`R` is `"123e4567-e89b-12d3-a456-426614174000"`; `Q` is the class of `Query.Of()`, `S` of `Schema.Of()`):

| Class | Test | Assertions |
|---|---|---|
| `Literal01…` [R1a] | `ADeclaredStoredFormPutsTheComparisonInTheStatement` | `Saturate(Horn, Rewrite)`; `form(Q, "ref", "uuid-lower")`; `AreEqual(q, And(StrEq(kind,B), StrEq(ref,R)))`; extraction `(And (StrEq "kind" "B") (StrEq "ref" "R"))`, cost 7 |
| | `WithoutTheDiscriminatorNothingIsProvenAndNothingLowers` | `Saturate(Horn, Lattice, Split, Rewrite)`; no `form(Q, ref, …)`; extraction `(UuidEq "ref" "R")`, cost 1002 |
| `Literal02…` [R1a] | `TwoKindsNamedTogetherLowerTheReference` | `Saturate(Horn, Lattice, Rewrite)`: no `form(Q, ref)`. Then `Saturate(Horn, Lattice, Split, Rewrite)`: `form(Q, ref)`; `AreEqual(q, And(Or(StrEq kind order, StrEq kind invoice), StrEq(ref,R)))`; no `form(S, ref)`; extraction cost 11 and the string of §10 |
| `Literal03…` [R1a] | `ADomainLessARefusedValueProvesTheGuard` | `Saturate(Horn, Rewrite)`: no `form`. `Saturate(Horn, Lattice, Rewrite)`: `Get(dom(Q, kind)) == {order}`, `form(Q, ref)`, `AreEqual` |
| `Literal04…` [R1b] | `AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains` | `Saturate(Horn, Lattice, Rewrite)`: not `dom(S, status) = {active, archived, draft}`, not `AreEqual(q, PFalse)`. With `Split`: `dom(S, status) = {active, archived, draft}`; `contradicts(Q)`; `AreEqual(q, PFalse)`; no `present(S, legacyId)`; no `present(S, id2)`; extraction `(PFalse)`, cost 0 |
| `Literal05…` [R1b] | `AComparisonBeyondADeclaredBoundIsFalse` | `contradicts(Q)`; `AreEqual(q, PFalse)` |
| | `ABoundInsideTheRangeIsLeftAlone` | not `contradicts(Q)`; `Get(lo(Q, n)) == 51`; extraction `(NumGt "n" 50)` |
| `Literal06…` [R1b] | `ADeclaredTypeMakesAnOrderingComparisonExact` | `AreEqual(q, StrGe(at, …))`; extraction cost 3 |
| | `WithoutADeclaredTypeTheComparisonIsStillWeakened` | not `AreEqual(q, StrGe(at, …))` |
| | `ADeclaredTautologyIsNotAsked` | `AreEqual(q, StrEq(id, x))`; extraction `(StrEq "id" "x")` |
| | `WithoutAPresenceClaimTheComparisonStays` | not `AreEqual(q, StrEq(id, x))` |
| `Literal07…` [R1b] | `TheMapQueryLowers` | `AreEqual` with `StrEq "data.data.mapId"` |
| | `ParkAndMapWrittenInLowerTheParkId` | `form(Q, data.data.parkId, uuid-lower)`; `AreEqual` |
| | `ParkAndMapSpeltPerKindLowerTheParkId` | `Get(dom(Q, data.type)) == {map, park}`; `form`; `AreEqual`; extraction cost 15, Eggnog's spelling `(Or (And (StrEq "data.type" "park") (StrEq "data.data.parkId" …)) (And (StrEq "data.data.parkId" …) (StrEq "data.type" "map")))`, and `AreEqual` with egglog's spelling, which keeps the second conjunction as written |
| | `TheOfflineRefreshLowersEverything` | `AreEqual` with `StrGt "data.metadata.changeUtcTime"`; `CountRows(Assume) == 6`, `CountRows(Parent) == 7`, `CountRows(Form) == 40` (the prototype's `print-size` figures: three cases under `(Schema)` and three under `(Query)`) |
| | `TheFacadeFilterDropsTheWholeEnum` | `AreEqual(q, StrEq(type, Link))` |
| | `ASpotMayBeAmongTheRowsSoNothingLowers` | no `form(Q, parkId)`; extraction mentions `UuidEq` |

The expected figures and strings were confirmed with the egglog CLI built from the vendored `ext/egglog` at commit
`5e655cae`: every file exits 0, `print-size` prints `Assume` 6, `parent` 7 and `form` 40 for the offline-refresh block
(and `PrintSize` compares every table's figure where a test calls it), and every extracted cost and string matches this
table and §10 (including the per-kind 07 term's cost 15, and `(UuidEq "ref" …)` for 01's second block), with one
exception below. `Literal07SweepLinksTests` names that commit in its class comment. Eggnog breaks ties by content and
class id (D10) where egglog breaks them by scan order, so where they extract different spellings of equal cost the test
keeps Eggnog's answer, says so in a comment, and asserts that egglog's spelling is in the same class. One string
differs: in the per-kind 07 term egglog prints `(And (StrEq "data.type" "map") (StrEq "data.data.parkId" …))` for the
second disjunct, and Eggnog commutes it, because the host made the `parkId` comparison's class before the `map`
equality's (§10, step 5).

#### 8.1.2 The scoped suite [R2, R3]

`Cosmos/Scoped/` ports the same scenarios with scopes as contexts: `CosmosTheory` (§10; `CosmosTheory.cs` [R2] and the
partial `CosmosTheory.Cases.cs` [R3]), `SweepLinks` (compiles sweep's `links` container), and one class per scenario,
split into a base file [R2] and a `.Cases.cs` partial [R3] where it has split tests. Each container is compiled with an
empty frozen root, a schema scope tagged `c.Schema` with its facts assumed and branch rules declared, a run, and a
freeze. Each query runs in its own child tagged `c.Query`.

| Class | Tests | Milestone |
|---|---|---|
| `Scenario01DiscriminatedOneOfTests` | `ADeclaredStoredFormPutsTheComparisonInTheStatement`, `WithoutTheDiscriminatorNothingIsProvenAndNothingLowers` | R2 |
| `Scenario02QueryDisjunctionTests` | `HornAndLatticesAloneDoNotLower` (R2, schema compiled without the split); `TwoKindsNamedTogetherLowerTheReference` (`CasesOpened == 2`), `TheSameSplitDrivenByTheHost`, `AQueryThatDoesNotMentionTheDiscriminatorDoesNotSplit` (`CasesOpened == 0`) (R3); code in §10 | R2, R3 |
| `Scenario03CombiningClaimsTests` | `ADomainLessARefusedValueProvesTheGuard` | R2 |
| `Scenario04UndiscriminatedAnyOfTests` | `WithoutTheSplitTheBranchesNeverMeet` (R2); `AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains` (R3): the schema's run opens 2 cases on `$anyOf/0`; `schema.Get(dom status) == {active, archived, draft}`, no `present(legacyId)`, no `present(id2)`; the query (which mentions `status`, not `$anyOf/0`) opens 0 cases, `IsContradictory`, `AreEqual(q, PFalse)`, `Extract(q)` is `(PFalse)` | R2, R3 |
| `Scenario05IntervalsTests` | `AComparisonBeyondADeclaredBoundIsFalse`, `ABoundInsideTheRangeIsLeftAlone` | R2 |
| `Scenario06GuardDeletionTests` | the four tests of 06, with `WithoutAPresenceClaimTheComparisonStays` asserting `query.Check(present kind)` and `schema.Check(present kind) == false`: the conjunct's own consequence in the query never deletes it (D12) | R2 |
| `Scenario07SweepLinksTests` | `TheMapQueryLowers`, `TheFacadeFilterDropsTheWholeEnum`, `ASpotMayBeAmongTheRowsSoNothingLowers` (R2, without the split); `SweepLinks.Compile` asserts 3 schema cases; `ParkAndMapWrittenInLowerTheParkId` (`CasesOpened == 2`); `ParkAndMapSpeltPerKindLowerTheParkId` (`CasesOpened == 2`, cost 15, the rendered s-expression: Eggnog's spelling, as in §8.1.1, `query.Children` empty); `TheOfflineRefreshLowersEverything` (R3) | R2, R3 |
| `Scenario08IntegerDomainTests` (gap 5, D37) | `AnIntegerOutsideADeclaredDomainIsFalse`: schema `present(n)`, `idom(n) = {100, 200, 300}`; query `And(NumGt(n, 499), NumLt(n, 501))`; `IsContradictory`, `AreEqual(q, PFalse)`. `AnIntegerInsideTheDomainIsLeftAlone`: `idom(n) = {100, 500}`; not contradictory; `Get(idom(n)) == {500}` | R2 |

The scoped suite's case counts follow `CosmosTheory.Saturation`, which settles facts before its split step (D43); the
literal suite's interleaved schedule opens the third case that later goes vacuous, as the prototype does.

Gap mapping (the adapter's `CosmosDeclaredFactPlanningTests.Gaps.cs`): Gap1 → scenarios 02 and 07, Gap2 → 03, Gap3 →
04, Gap4 → 05, Gap5 → 08.

### 8.2 Differential tests

**`Differential/RuleLayerDifferentialTests` [R1b].**
- `RandomProgram` generates, from a seed, through the internal untyped layer (D13):
  - **tables**: 2–4 relations over `i64` and `String`; 1–2 constructors over a sort `Node` with value and `Node` keys;
    1–2 functions with `ValueLattice.Max<long>()` or `ValueLattice.Intersect<int>()`, and one without a lattice,
    written only by host `Assume` with unique values;
  - **rules** (5–12): joins of 1–4 atoms; named primitives and lambdas as filters and lets; lets checked against
    constants (`p(args).Is(c)`), a checked computation never repeated as a let; rewrites; unions; `Make`; class
    constants from host `Make`; `Naive()`;
  - **queries**: defined before the first command, and up to two more between runs (`DiffCommandKind.DefineQuery`),
    which may hold the host's classes; lets are checked against constants in queries too;
  - **schedules**: `Saturate`, `Repeat(1..3)`, `Sequence`, `Until`, with host writes and queries between runs.
  - Every rule is valid, so that no seed hits a definition error, and matches stay few: no body repeats an application
    (equal applications share one output, §6.3 step 3), no body equates two variables that are keys of applications,
    and every atom after the first joins on a variable. Values come from small domains, and a program stops after a run
    that leaves more than 2500 reference rows (what it did so far was compared).
- Each program runs three ways: Eggnog with `SemiNaive = true`; Eggnog with the internal `SemiNaive = false`; and
  `ReferenceInterpreter`, which evaluates the same program naively over decoded values with its own union-find and
  congruence closure, Jacobi-style, with the same schedule semantics.
- After every `Run`, its report (iterations, stop reason, saturation, change) and every table's rows (`Scope.Rows`)
  are compared; after every host write, the rows; and every query's answer. Value cells are decoded, and sort cells
  replaced by `TermNamer` names. A class's name is its least ground term, by size, then head name (ordinal), then
  arguments in content order. The same algorithm runs on both sides, so equal facts give equal names whatever the ids.
- 300 seeds by default (`EGGNOG_SEEDS` raises it). A failing program prints itself as C# source. The 5000-seed run
  R1b's done-when asks for is recorded in the class remarks (green: 39.0 s on net10.0, 39.4 s on net8.0). Any change to
  the generator changes which programs each seed makes, so the record names the generator it ran.

**`Differential/ScopeDifferentialTests` [R2].**
- Random scope trees, up to 4 deep, with tags, frozen prefixes, host writes into writable ancestors between child runs,
  and host queries with scoped atoms from tagged scopes and their descendants. Programs are from the value-only family:
  relations and functions over values, scoped atoms, local rules, no constructors in children.
- The oracle is the program translated into the literal encoding and run at one root:
  - every table gains a leading `Ctx` key, and every scope becomes a `Ctx` constant;
  - inheritance rules `parent(k, c), T(c, x…) → T(k, x…)` are added;
  - every rule active in a scope is instantiated for that scope, with its scoped atoms resolved statically to the
    tagged scope's constant.
- Each scope's view, and every query's answer, must equal the oracle's facts for its constant. 300 seeds.

**`Differential/InheritanceOffEquivalenceTests` [R2].** Scoped programs with constructors in children, run with
`graph.InheritWatermarks = false` and with `true` (which reaches cases too, §6.9). Every scope's view must be equal at
every checkpoint, and the seeded match totals must be at most the unseeded ones.

**`Differential/SplitOracleTests` [R3].**
- Random discriminated schemas: 1–2 discriminators with 2–4 values; branch rules declaring presence, forms and domains;
  queries with `IN` lists and disequalities.
- Each runs in the scoped encoding with engine splits (the unguarded schema split for both schema and query, so both
  encodings split the same keys), and in the literal encoding with the prototype's split rules.
- In the query, `present`, `form`, `dom` and contradiction must agree for every path mentioned. Those are the kinds the
  prototype lifts; the engine lifts a superset.
- 300 seeds.

### 8.3 Targeted tests

| Class | Content | Milestone |
|---|---|---|
| `Values/ValueTests` | Codec round trips for every built-in type; −0.0 normalized, NaN throws; `int` −1 and `int.MinValue` as the first column of a relation round-trip (stored as `i64`); enum round trip; `ValueSet` operations and order; `ValueSet.Of("abc")` is `{"abc"}`; concurrent interning; `Check` with an unseen string leaves `InternedCount` unchanged (E1) | R1a |
| `Values/LatticeTests` | `Verify` accepts the built-ins and rejects a mismatched case join (∩ with ∩); a lattice delegate that throws during a rule's merge gives `RuleEvaluationException` with `RuleName == null` | R1a |
| `Definitions/DefinitionTests` | Duplicate names, `@`, `$`, `contradiction`; unknown types; a sort output to `DefineFunction`; `Role = Term` on a relation; the engine layout per kind (§6.2), read through `Relations.EGraph.GetFunctionInfo`; a table defined from another thread while the root runs throws; a rule defined from another thread during a run joins the next iteration | R1a |
| `Graph/ClassIdTests` | `default(ClassId)` is refused by `Find`, `AreEqual`, `Union`, `Make`, `Assume`, `Extract` and a rule constant; another graph's class is refused; a `Write` that throws leaves the values its `Make` returned unusable, and one that commits leaves them usable. R2: a sibling query's class is refused; a query's class is refused after the query is disposed; a global rule holding a non-root class is a `RuleDefinitionException`; a local rule may hold its owner's class. R3: a case's class is refused after the run that made the case | R1a, R2, R3 |
| `Rules/RuleDefinitionTests` | One test per message of §6.3: another builder's variable; duplicate name; unknown type; variables of different types equated; never-matching value constants; class constants never refused: different classes compared where the rule runs (they match in a child that unions them), one class merged (§6.3 step 3, N86); unbound variable; primitive over a sort; analysis writer reading a fact table; head function read; head value equality; head filter; `Make` variable in the body; head variable before its `Make`; class constants (default, another graph). R2 adds: nested `Fact.In`; a handle scoped to another tag inside `Fact.In`; a scoped read of a table with a sort column; a scoped write; an erasing violation; `ReadsOwnFacts` without a licence or a reason; a first scoped read after a tagged scope froze; class constants from other scopes | R1a, R2 |
| `Rules/LoweringTests` | The engine rule of a lowered program (through `Relations.BridgeQuery`): atom order; relation atoms end with the unit constant; steps as `QueryPrim` in placement order; head operations in call order; descriptions (§6.4.3); the naive twin lowered only when `SemiNaive = false` is first used | R1a |
| `Rules/ActionTests` | Get-or-add duplicates merged by the rebuild; `C.Of(k).Is(x)` unions; lattice writes merge; `Rewrite` with a variable right side unions; `Make` variables in heads; a rule's write conflicting on a function without a lattice gives `RuleEvaluationException`, after which the scope `IsFaulted`, `Failure` is that exception, and `Run` throws `InvalidOperationException` with it as `InnerException`; a primitive throwing in a filter, a let and a head gives `RuleEvaluationException` with the rule name and bindings, sets rendered as `{a, b}`; an overflow of the built-in `+` or `-` drops the match in a body and fails the rule in a head (D31) | R1a |
| `Rules/IdConstantTests` | Behaviour only: a rule with a class constant matches the class after the constant loses a union; an action writing such a constant writes the canonical cell. Runs unchanged in R2 | R1a, R2 |
| `Rules/RootRelowerTests` | R1's mechanism: the re-lowered variant's description ends with `/k1` and it runs in full; deleted with `RootEngineScope` | R1a (deleted in R2) |
| `Rules/ErasingTests` | A ruleset licence makes every rule erasing; a rewrite reading unscoped `present` without an exemption is rejected; `ReadsOwnFacts` exempts and renders; `Erasing` on one rule of a ruleset without a licence; rules reading only term and analysis tables are unaffected | R2 |
| `Rules/QueryTests` | `Any`, `Count`, `ForEachMatch`; early stop; distinct full bindings; `Version` unchanged by queries; an extractor created before `Count`, `Any` or `ForEachMatch` still extracts after it; a query whose filter throws gives `RuleEvaluationException` and leaves the scope usable; `Query.Dispose` frees the rule and later use throws `ObjectDisposedException`; a disposal never waits, and the free waits for a call on another thread to end, for a run holding the engine lock (the graph frees it at its next iteration), for a call from a primitive, and for the `ForEachMatch` whose callback disposed it; a disposed query's name may be defined again; class constants are checked at each call; a class read after a callback's union is canonical; queries allocate nothing per match beyond the engine run (`Any` included, E12); queries are refused inside a `Write` and from a primitive. R2: queries on a frozen scope from 8 threads; a query with scoped atoms after a host write into a writable tagged scope, from that scope and from a descendant | R1b, R2 |
| `Scheduling/ScheduleTests` | `Repeat` stops early; `Saturate`; `Sequence`; `Saturated`; `IterationLimit`; cancellation; `StopOnContradiction` at the top level; an empty ruleset does not iterate; a ruleset of another graph is refused (R1a). `Until`, checked before every pass; `Until` refuses no fact, filters, variables and computations, and `Run` refuses a condition of another graph before any work; `TimeLimit` overshoots by at most one iteration; `RowLimit` stops a growing program; negative limits throw (R1b). Limits count case iterations and case rows (R3) | R1a, R1b, R3 |
| `Diagnostics/DiagnosticsTests` | `RunReport.Rules` matches and search times; merge and rebuild times (R1a). Plans with `RecordPlans`; `RecordSteps` lists every iteration in order; the observer sees the same steps, may call `Check`, `CountRows` and queries (with or without `RecordSteps`), and gets `InvalidOperationException` for `Assume` and `Run`; an observer's exception propagates without faulting; `CountRows` and `Rows` match the prototype's `print-size` on Literal07; `Rows` renders s-expressions (R1b). `MirrorSync` steps (R2); split and lift steps (R3) | R1a, R1b, R2, R3 |
| `Host/HostWriteTests` | Atomicity (a rejected fact writes nothing); `Make` returns canonical classes and mints each new term once per batch; `Write` batches several calls with one flush, and a throw inside it writes nothing; no-lattice conflicts against the store, within a batch, and through a merge of the batch give `FactConflictException` with table, key and both values; a rejection the `Write` callback caught writes nothing and `Write` returns; a `ScopeWriter` call from another thread is refused and rejects the batch; `Union` returns distinctness; a class constant that lost a union, written in a row, lands canonical; writing facts that hold already leaves `Version` unchanged and a live extractor usable | R1a |
| `Host/HostReadTests` | Every row of the `Check` table (§6.6.3); `Get`/`TryGet`; `AreEqual` with an absent term; reads never intern; reads never throw for want of a rebuild (R1a). `CountRows`, `Rows` (R1b) | R1a, R1b |
| `Extraction/ExtractionTests` | No finite term; zero-cost nodes with children; ties keep the as-written spelling; per-row costs; unextractable heads (by definition and by cost model); value costs; DAG sharing; a stale extractor throws after a changing write; `Mentions`; `TryMatch` at every arity, with value and class arguments; `ToString` matches egglog's format for every built-in literal type | R1a |
| `Rendering/RenderingTests` | Rules render as s-expressions; named primitives by name, lambdas as `<lambda>`. R2: `present@schema`, `:erasing schema`, `:reads-own-facts "…"` | R1a, R2 |
| `Concurrency/RootThreadingTests` | Two threads on the root throw; a rule defined from another thread during a run joins the next iteration; a table defined from another thread during a run throws; a primitive, a lattice merge and a cost function that call into the graph get `InvalidOperationException` (also from a pool thread with `DegreeOfParallelism = 4`) | R1a |
| `Surface/PublicSurfaceTests` | By reflection: no public or protected member of `Eggnog` mentions a type of `Eggnog.Relations` or `Eggnog.Storage` (D29); no public type of another assembly in the package shares a simple name with a public `Eggnog` type (D44); `Eggnog` references no `Eggnog.Storage` assembly (R1a). The public surface is exactly the members the milestones so far tag, compared with the embedded listing `Surface/PublicSurface.txt` that `SurfaceListing` renders (R1b; each later milestone updates the listing). The spike's name is assembled from its parts, so a text search of the test project finds none | R1a, R1b |
| `Scopes/ScopeTreeTests` | `CreateChild`, `Path`, `Children` (transient query children not listed), `Freeze` under a writable parent throws, depth 31, tags resolve to the nearest ancestor-or-self, `ScopeOptions.DegreeOfParallelism` reaches the context | R2 |
| `Scopes/ScopeTagTests` | `DefineScopeTag` names are unique per graph and `root` is reserved; `RootTag`; another graph's tag is refused by `CreateChild`, `In`, `Fact.In`, `Erasing`, `RulesetOptions.Licence`, `DefineSplit` and `CaseSet.Add`; `TwoGraphsShareNoTagState`: two graphs in one process each build `CosmosTheory` and freeze schemas, in both orders and on parallel threads, and every theory compiles and every scenario query passes | R2 |
| `Scopes/DisposeTests` | `Dispose` never throws; a second call does nothing; the root's does nothing; `IsDisposed` is true at once and use throws `ObjectDisposedException`; a frozen schema disposed while 8 queries run beneath it on other threads is released after the last query, and its local rules are freed then; a frozen scope disposed during a query on another thread (a transient child) is released after the query; an open `CaseSet` defers its parent's release; a writable scope disposed from another thread while its domain is busy is released when that call ends | R2 |
| `Scopes/LocalRuleTests` | A rule local to container A never fires in sibling B; it is gone after A is released; `DefineRule` on a frozen scope throws; `DefineRule` from another thread while the scope runs throws; one `CosmosTheory` serves two containers | R2 |
| `Scopes/ScopedReadTests` | A mirror equals the tagged scope's view (relations and lattice functions); a writable tagged ancestor is synced before a descendant's run and before a query; a scoped read within the tagged scope sees the scope's own facts of the previous iteration with no lag, also under `Repeat(1)`; a frozen tagged scope is never synced; host reads of scoped facts read the resolved scope directly | R2 |
| `Scopes/ScopeIsolationTests` | A query's derived facts are visible neither in its schema nor in its siblings; `ConstantLosesRootUnionBeforeChildRuns` (a child that asserts nothing sees what its parent sees) | R2 |
| `Scopes/InheritanceTests` | A child's first run of a rule its frozen parent saturated matches only through new rows (fewer matches than a full run); naive rules never inherit | R2 |
| `Scopes/FaultTests` | A child is faulted after a `RuleEvaluationException` and only `Dispose` works; `Failure` and `InnerException`; its parent and siblings are unaffected | R2 |
| `Concurrency/ConcurrencyTests` | 32 queries under one frozen container on parallel threads give the extractions of serial runs; queries and extractors on a frozen schema from 8 threads | R2 |
| `Concurrency/ParallelEquivalenceTests` | The scoped Cosmos programs with `DegreeOfParallelism = 4` on the graph and on query scopes: every scope's facts equal the serial run's modulo renaming (`TermNamer`); extraction is compared at 1 only | R2 |
| `Extraction/ContextExtractionTests` | Extraction in a child sees inherited and own rows and the child's equalities; a parent change makes a child's extractor stale; a query in the child does not | R2 |
| `Scopes/MirrorSyncTests` | A host write into an ancestor of the tagged scope is synced before the next run and before the next query (the view-stamp key); a mirror created after the tag's last sync, by a rule or by a query, is synced before its first read (the copy-rule-count key, N61) | R2 |
| `Scopes/FreezePlanTests` | A `Freeze` plans every rule a descendant can run, and no other container's local rules; the root's `Freeze` plans nothing | R2 |
| `Scopes/LocalRuleScheduleTests` | A local rule runs only when its own ruleset is scheduled, in its owner and in a descendant | R2 |
| `Scopes/RunTagResolutionTests` | A run whose later step, or a local rule it reaches, reads a tag that does not resolve throws before its first step: the earlier steps write nothing, `Version` is unchanged and the scope is not faulted (§6.5.3, §6.7) | R2 |
| `Scopes/DeferredReleaseTests` | A frozen scope disposed during an extraction is released after the extraction (D40) | R2 |
| `Scopes/DisposeInsideARunTests` | A sibling disposed from a primitive of a run in its domain is released when the run ends | R2 |
| `Scopes/DisposedChildListingTests` | A disposed child waiting for its release is not listed in `Children` | R2 |
| `Scopes/ScopeDepthTests` | The scope layer refuses a child below depth 31 with its own message | R2 |
| `Scopes/CatchUpVersionTests` | The catch-up a call makes first moves no `Version` (N87): a read in the innermost of two writable children after a root write moves neither child's; a write that changes nothing, a run with nothing to do, and a child's `Freeze` after its parent wrote and froze keep it; a scope's own union over an ancestor's rows still moves it | R2 |
| `Scopes/OpenBatchDomainTests` | While a `Write` batch is open (N88), its writable ancestors, the root included, refuse writes, runs and nested batches, so an ancestor write that would conflict with the batch's plan is refused and the batch commits; the ancestors accept writes again once the batch ends, even after its callback threw; writes into a descendant, into a sibling and a nested batch in a descendant are allowed; an ancestor's query between the batch's calls (a mirror sync) leaves the writer usable and moves `Version` exactly as the same query before the batch | R2 |
| `Extraction/ExtractedClassOriginTests` | An extracted term's classes carry the extracting scope's origin (D39) | R2 |
| `Concurrency/FrozenScopeOverlapTests` | While an extraction is held inside its cost function, or a query inside its filter, on another thread, the same frozen scope serves reads, queries and extractions on this thread (no guard on a frozen scope, D47) | R2 |
| `Cases/LiftTests` | Case joins come from `ValueLattice.CaseJoin`; relation rows lift through the function kind with a unit output; `Lift = false` tables are not lifted; mirror tables and `Contradiction` are never lifted as rows; vacuous cases are disposed after `CaseSet.Lift` and leave `Cases`; zero cases throw; cancellation between rounds | R3 |
| `Cases/SplitTests` | Persistent cases absorb later parent facts within one `Run`; records are disposed when the `Run` ends and a second `Run` splits again; `CaseLimit` and `SplitDepthLimit` skip; an empty element set skips and is counted; a guarded split opens no case when the guard fails; a split step on a contradictory scope does nothing; `DefineDomainSplit` equals the hand-written rule; nested splits (phase A, then phase B); cases are disposed when a run stops early or throws; `Children` is empty after every `Run`; `OpenCases` on a frozen scope throws; `InheritWatermarks = false` reaches cases | R3 |
| `Cases/ContradictedCaseTests` | After a case derives `Contradiction`: `IsContradictory` is true; `Check`, `Get`, `Find`, `Count`, `Extract`, `Make`, `Write`, `CreateChild` and `OpenCases` throw `ScopeContradictedException`; `Assume` does nothing; `Union` returns false; `Run` returns `Contradiction` with no iteration; `Dispose` works | R3 |

Beside these classes, `src/Eggnog.Tests/Review/` keeps the tests the R1a and R1b reviews added (API, semantics, batch
atomicity, partial primitives, extraction and the literal suite's `print-size` figures; for example
`R1bApiCoverageTests.TheRowLimitIsCheckedBeforeTheFirstIteration`) and the R2 reviews' probes (`R2ApiReviewTests`,
`R2CompletenessReviewTests`, `R2ConcurrencyReviewTests`, `R2SoundnessProbeTests`, and `R2FixVerificationTests`, which
pins N86: class constants equated in a body match in a child that unions them, whether or not the root is busy, and
`R2FixVerification2Tests`, which pins N86 under semi-naive runs and N87: a descendant's lazy catch-up moves no `Version`,
and `R2FixVerification3Tests`, which pins N88: an ancestor's write inside a descendant's batch is refused and moves no
`Version`), and
`src/Eggnog.Tests/Support/` the small theories
they share (`NodeTheory`, `BlockingRun`, …). Both follow the same conventions and run in every milestone's baseline.

### 8.4 Running

```
dotnet build src/Eggnog.Tests -f net10.0
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe                 # add -class <FullName> to filter
```

---

## 9. Milestones

Every milestone has the same baseline definition of done:
- `dotnet build src/Eggnog.Tests -f net10.0` and `-f net8.0` have no errors, and no warnings in new files;
- every new test passes, and every existing suite (`Eggnog.Tests`, `Eggnog.Relations.Tests`, `Eggnog.Storage.Tests`)
  passes unchanged, except for the additions the milestone lists;
- the conventions hold: one type per file, no nested types, XML docs on every public and internal member, `== false`,
  no `using Eggnog.Relations;` in `src/Eggnog` (D29);
- `Surface/PublicSurfaceTests` passes;
- the only engine changes are the requests the milestone lists (§5.2).

### R1a: the library lands

The first rule-layer change, kept small so that `src/Eggnog` is a real library on main as soon as possible.

**Depends on:** the engine at `5acd00e`, plus E1 and E11, landed in `Eggnog.Relations` as the first changes of R1a.

**Project changes.**
- `src/Eggnog/Eggnog.csproj`: add `<ProjectReference Include="..\Eggnog.Relations\Eggnog.Relations.csproj" />` and
  remove the `Eggnog.Storage` reference (nothing in `src/Eggnog` uses it).
- `src/dist-nuget/dist-nuget.csproj`: remove the `Eggnog.Storage` `PackageProjectReference`, so that the package
  neither depends on nor ships the spike.
- `src/Eggnog.Relations/Eggnog.Relations.csproj`: add `<InternalsVisibleTo Include="Eggnog.Tests" />` (lowering and
  layout tests read bridge internals).
- `.github/workflows/Eggnog.yml`: add `Eggnog.Tests` to the test matrix (`src/dist-tests` already packs it).
- `README.md`: describe `Eggnog` as the library and `Eggnog.Relations` as its engine; `src/Eggnog.Storage` and
  `src/Eggnog.Storage.Tests` stay while `Eggnog.Relations.Tests` uses the spike as a second oracle (colors X32), and
  deleting them is a separate decision.
- `src/Eggnog.Relations/README.md` (new): `Eggnog.Relations.csproj` already set `PackageReadmeFile` to a file that did
  not exist, so `dotnet build src/dist-nuget` failed with NU5039; the package now has a short readme.
- `src/Eggnog/Eggnog.csproj` also declares `<InternalsVisibleTo Include="Eggnog.Tests" />`, and `src/Eggnog.Tests`
  suppresses xUnit1051 (§15).

**Content.**
- Public: `EGraph` (definitions of sorts, relations, functions, constructors and rulesets; `Root`, `Contradiction`,
  `Tables`, `Rulesets`, `Values`), `EGraphOptions.DegreeOfParallelism`; `Scope` identity and lifetime (`Failure`,
  `Version`, `Path`, `Dispose`), writes (`Make`, `Assume`, `Union`, `Write`), reads (`IsContradictory`, `Check`, `Get`,
  `TryGet`, `Find`, `AreEqual`), `Run`, `CreateExtractor`, `Extract`; `ScopeWriter`; `ISort`, `ClassId`; every
  `Values/` type; every `Tables/` type without `In`/`ReadScope`; every `Terms/` type without `Fact.In`; `Ruleset`,
  `Rule`, `RuleBuilder` without `Erasing`/`ReadsOwnFacts`, `RuleDefinitionException`, `RuleEvaluationException`,
  `FactConflictException`; `Schedule.Run/Repeat/Saturate/Sequence`, `RunOptions` (`IterationLimit`,
  `StopOnContradiction`), `RunReport` (without `Steps` and the R3 members), `RunStopReason` (`Completed`,
  `IterationLimit`, `Contradiction`), `RuleStatistics` without `Plan`; every `Extraction/` type.
- Internal: every type tagged R1a in §4, `RootEngineScope` with the Id-constant re-lowering of §6.4.5 included.
- §6.1–§6.5, §6.6.1–§6.6.3, §6.6.5, §6.6.7, §6.10 and §6.12–§6.13 for the root.
- `Scope` and `EGraph` already have their final shape for these members: later milestones only add (D28).

**Tests.**
- The R1a rows of §8.3.
- Literal 01–03 (§8.1.1).
- E1's tests in `Eggnog.Relations.Tests/BaseValues/BaseValuesTests`; E11 leaves every `Eggnog.Relations.Tests` test and
  the benchmarks building and passing.

**Done when** the baseline holds; Literal 01–03 are green; `dist-nuget` packs `Eggnog` and `Eggnog.Relations` only, and
the `Eggnog` package's only Eggnog dependency is `Eggnog.Relations`; a text search finds no `Eggnog.Storage` in
`src/Eggnog`, `src/Eggnog.Tests` or `src/dist-nuget`; `RootEngineScope` is the only file in `src/Eggnog` that calls the
bridge's root-only members (`Database.CreateExecutionState`, or `CreateEngineState` since colors M10.4 (N59),
`GetCanonInUf`, `LookupId`, `TableAction`, `UnionAction`, `FlushUpdates`, `TryTakePanic`); and the work is merged to
main. **Status:** done; merged to main at `edd1fa9` (with colors M9.3–M9.4). §15 lists what it changed in this spec.

### R1b: queries, limits, diagnostics, the rest of the literal suite

**Depends on:** R1a, plus E12, landed in `Eggnog.Relations` with R1b.

**Content.**
- `Query`, `QueryBuilder`, `QueryCallback`, `QueryMatch`; `EGraph.DefineQuery`; `Scope.Any`, `Count`, `ForEachMatch`;
  `QueryRunner`, `MatchCollector`, `CompiledQuery`, and R1's query path without a merge or a clock tick (§6.6.5).
- `Schedule.Until`; `RunOptions.TimeLimit`, `RowLimit`, `Observer`, `RecordSteps`; `RunStopReason.TimeLimit`,
  `RowLimit`; `RunReport.Steps`; `RunStep`, `RunStepKind`; `RuleStatistics.Plan`, `EGraphOptions.RecordPlans`.
- `Scope.CountRows`, `Scope.Rows`, `TableRow`, `RowReader`.
- `TupleBuffer` pooled, `TupleOrder`, `EngineScope.CanonicalizeColumns`, `TermReader.Validate`, `UntilSchedule`.
- §6.6.4 and §6.11.
- E12 in `Eggnog.Relations` (§5.2), with its engine test.

**Tests.**
- The R1b rows of §8.3.
- Literal 04–07 (§8.1.1).
- `Differential/RuleLayerDifferentialTests` at 300 seeds.

**Done when** the baseline holds, the literal suite is green in full, and the differential is green at 300 seeds with a
5000-seed run recorded with the change. **Status:** done; added at `80675d8` and merged to main at `3b9dda1` (with
colors M10.1–M10.2). The engine suite (8516 tests, E12's included) and the 5000-seed run (39.0 s on net10.0, 39.4 s on
net8.0, recorded in the `RuleLayerDifferentialTests` class remarks) were green. §15 lists what it changed in this spec,
and §15.5 the items still open.

### R2: scopes, frozen bases, per-query layers, local rules, concurrency

**Depends on:** colors M10.3 (the bridge on a `Store`, `EGraphContext`, watermarks, X19), plus E4, E5, E7, E9 and E10.

**Content.**
- `ContextEngineScope` for every scope, the root included. `RootEngineScope`, the engine lock, the re-lowering path of
  §6.4.5 and `Rules/RootRelowerTests` are deleted.
- `Scope.Tree` (`Parent`, `Tag`, `IsFrozen`, `Children`, `LocalRules`, `CreateChild`, `Freeze`, `DefineRule`), the
  deferred release of `Dispose` (D40), `ScopeOptions`; `ScopeTag`, `EGraph.DefineScopeTag`, `RootTag`, `ScopeTags`;
  `In(tag)`, `Fact.In`, `TableHandle.ReadScope`; `RulesetOptions`, the `DefineRuleset` overload, `Ruleset.Licence`,
  `Rule.Licence`, `Rule.OwnFactsReason`, `Rule.ReadScopes`, `RuleBuilder.Erasing`, `RuleBuilder.ReadsOwnFacts`.
- `MirrorRegistry` (with the per-tag frozen counts), `MirrorTable`, `MirrorSync`, `ScopeResolver`; the frozen-tag
  check (§6.8).
- Local rules (§6.7); queries on frozen scopes through transient children (D34); context extraction (E5); the origin
  checks of other scopes (D39); per-thread-domain guards beyond the root (D47).
- `Relations.EGraph.DesignateContradiction` at graph construction (E4); the per-graph `ContextLocalSlot<ScopeRuntime>`;
  the internal `EGraph.InheritWatermarks` switch.

**Tests.**
- The R2 rows of §8.3.
- The scoped suite's R2 tests: 01, 03, 05, 06, 08, and the R2 tests of 02, 04 and 07 (§8.1.2). In R2, `CosmosTheory`
  has no split members; `CosmosTheory.Cases.cs` adds them in R3.
- `ScopeDifferentialTests` and `InheritanceOffEquivalenceTests` at 300 seeds.
- Every R1 suite, now running on the Store-backed root, including the literal suite and `IdConstantTests`.

**Done when** the baseline holds; all of the above is green with the default `DegreeOfParallelism` of 1;
`ConcurrencyTests` is green on 8 threads and `ParallelEquivalenceTests` at 4; and the R1 suites pass unchanged in
source, with three exceptions: `Rules/RootRelowerTests` is deleted;
`QueryTests.AClassConstantThatLostAUnionIsMatchedCanonically`, whose `/k1` assertion tested R1's re-lowering, now
asserts the same matches through the variant lowered at definition (N74); and
`RuleDefinitionTests.ClassConstantsAreComparedByClassWhenTheRuleIsDefined`, which asserted the refusal of class
constants of different classes, is now `ClassConstantsAreComparedByClassWhereTheRuleRuns` (N86).

**Status:** done; merged to main with colors M10.4. §15.8 records how it was built (N60–N89). No engine file changed.
At the merge `Eggnog.Tests` (498 tests), `Eggnog.Relations.Tests` (11,811 tests, 10 of them opt-in and skipped)
and `Eggnog.Storage.Tests` (816) pass on net10.0 and net8.0, so the baseline holds. `ConcurrencyTests` runs on 8
dedicated threads and `ParallelEquivalenceTests` at degree 4; the scope differential and the inheritance equivalence
run 300 seeds. The benchmark project's Release build is clean. Not timed: R2's per-iteration cost at the root, left to
an idle-machine run (§15.5).

### R3: case splits and lift

**Depends on:** colors M10.4, plus E8 (both landed; §15.7).

**Content.**
- `SplitRule`, `SplitBuilder`, `EGraph.DefineSplit`, `EGraph.DefineDomainSplit`, `EGraph.SplitRules`,
  `Schedule.Split`, `SplitSchedule`.
- `SplitProgram`, `RuleSplitSource`, `ScheduleCaseRunner`, `LiftSpecBuilder`, `CaseJoinAdapter<T>`.
- `Scope.Cases` (`IsCase`, `OpenCases`), `CaseSet`, `LiftOptions`, `LiftReport`, `ScopeContradictedException` and the
  contradicted-case semantics (D45, §6.6.8).
- The R3 members of `RunOptions`, `RunReport`, `RunStep` and `RunStepKind`.
- `CosmosTheory.Cases.cs`: `Case`, `Mentioned`, `SchemaSplit`, `QuerySplit`, `Facts` and `Saturation` (§10).

**Tests.**
- The R3 rows of §8.3.
- The scoped suite's R3 tests of 02, 04 and 07, including `TheSameSplitDrivenByTheHost` and
  `AQueryThatDoesNotMentionTheDiscriminatorDoesNotSplit`.
- `SplitOracleTests` at 300 seeds.

**Done when** the baseline holds, all of the above is green, and every scoped Cosmos test asserts that the query scope
has no children after its `Run`.

Version 1's R4 (retiring the spike reference) is folded into R1a; there is no R4.

---

## 10. Scenario 02 against the v2 API

The shared sort, the theory and the scenario-02 tests. `CosmosTheory.cs` and `Scenario02QueryDisjunctionTests.cs` are
the R2 files; the partials `CosmosTheory.Cases.cs` and `Scenario02QueryDisjunctionTests.Cases.cs` are added in R3. At
R2 the partial method `DefineCases` has no implementation and its call compiles away.

```csharp
// src/Eggnog.Tests/Cosmos/Pred.cs  [R1a]
namespace Eggnog.Tests.Cosmos
{

    /// <summary>
    /// A predicate over a document: the e-class sort of the pushdown theory.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Pred(ClassId Id) : ISort<Pred>
    {

        /// <inheritdoc />
        public static Pred FromId(ClassId id) => new(id);

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/Scoped/CosmosTheory.cs  [R2]
namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// The theory of prototypes/cosmos-facts/prelude.egg, defined once per graph and shared by every container compiled
    /// into it. The prototype's context argument and its inheritance rules are gone: scopes are the contexts. One theory
    /// per graph: it defines the graph's scope tags.
    /// </summary>
    public sealed partial class CosmosTheory
    {

        static readonly ValueSet<string> StringOrNull = ValueSet.Of("string", "null");

        /// <summary>
        /// Defines the theory in a graph. Every rule that reads a scope is defined here, before any schema is frozen
        /// (§6.8).
        /// </summary>
        /// <param name="graph">The graph.</param>
        public CosmosTheory(EGraph graph)
        {
            Graph = graph;
            graph.DefineSort<Pred>("Pred");

            // The graph's own tags (D38): a container's schema scope, and a query scope.
            Schema = graph.DefineScopeTag("schema");
            Query = graph.DefineScopeTag("query");

            // Claims about a path. Value-only, so the schema's view of each can be read from a query (D1).
            Present = graph.DefineRelation<string>("present");
            Form = graph.DefineRelation<string, string>("form");
            Ne = graph.DefineRelation<string, string>("ne");
            Discriminator = graph.DefineRelation<string>("discriminator");
            Dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Types = graph.DefineFunction<string, ValueSet<string>>("types", ValueLattice.Intersect<string>());
            Lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            Hi = graph.DefineFunction<string, long>("hi", ValueLattice.Min<long>());
            IDom = graph.DefineFunction<string, ValueSet<long>>("idom", ValueLattice.Intersect<long>());

            // Predicates, costed as in the prototype.
            PTrue = graph.DefineConstructor<Pred>("PTrue", cost: 0);
            PFalse = graph.DefineConstructor<Pred>("PFalse", cost: 0);
            And = graph.DefineConstructor<Pred, Pred, Pred>("And");
            Or = graph.DefineConstructor<Pred, Pred, Pred>("Or");
            Not = graph.DefineConstructor<Pred, Pred>("Not");
            StrEq = graph.DefineConstructor<string, string, Pred>("StrEq");
            StrGe = graph.DefineConstructor<string, string, Pred>("StrGe");
            StrGt = graph.DefineConstructor<string, string, Pred>("StrGt");
            BoolTrue = graph.DefineConstructor<string, Pred>("BoolTrue");
            NumGt = graph.DefineConstructor<string, long, Pred>("NumGt");
            NumLt = graph.DefineConstructor<string, long, Pred>("NumLt");
            IsDefined = graph.DefineConstructor<string, Pred>("IsDefined");
            IsString = graph.DefineConstructor<string, Pred>("IsString");
            UuidEq = graph.DefineConstructor<string, string, Pred>("UuidEq", cost: 1000);
            InstantGt = graph.DefineConstructor<string, string, Pred>("InstantGt", cost: 1000);

            // Query bookkeeping is never lifted; the analyses are derived from terms alone.
            var queryOnly = new TableOptions { Lift = false };
            var analysis = new TableOptions { Role = TableRole.Analysis };
            WhereClause = graph.DefineRelation<Pred>("where", queryOnly);
            Conjunct = graph.DefineRelation<Pred>("conjunct", queryOnly);
            InSet = graph.DefineFunction<Pred, string, ValueSet<string>>("inset", ValueLattice.Intersect<string>(), analysis);
            Admits = graph.DefineFunction<Pred, string, ValueSet<string>>("admits", ValueLattice.Intersect<string>(), analysis);

            Horn = graph.DefineRuleset("horn");
            Lattice = graph.DefineRuleset("lattice");
            // Every rewrite reads facts only from the schema, unless it says why it may read the query's (D12).
            Rewrite = graph.DefineRuleset("rewrite", new RulesetOptions { Licence = Schema });
            DefineHorn();
            DefineLattice();
            DefineRewrite();
            DefineCases();
        }

        /// <summary>Defines the split members; implemented from R3 on (CosmosTheory.Cases.cs).</summary>
        partial void DefineCases();

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the tag of a container's schema scope.</summary>
        public ScopeTag Schema { get; }

        /// <summary>Gets the tag of a query scope.</summary>
        public ScopeTag Query { get; }

        /// <summary>Gets the relation "the path is present".</summary>
        public Relation<string> Present { get; }

        /// <summary>Gets the relation "the path's strings use this stored form".</summary>
        public Relation<string, string> Form { get; }

        /// <summary>Gets the relation "the path's value is not this".</summary>
        public Relation<string, string> Ne { get; }

        /// <summary>Gets the paths a split may discriminate on.</summary>
        public Relation<string> Discriminator { get; }

        /// <summary>Gets the values a path can hold, if present.</summary>
        public Function<string, ValueSet<string>> Dom { get; }

        /// <summary>Gets the JSON types a path can hold, if present.</summary>
        public Function<string, ValueSet<string>> Types { get; }

        /// <summary>Gets a lower bound on a numeric path.</summary>
        public Function<string, long> Lo { get; }

        /// <summary>Gets an upper bound on a numeric path.</summary>
        public Function<string, long> Hi { get; }

        /// <summary>Gets the integers a numeric path can hold, if present (gap 5).</summary>
        public Function<string, ValueSet<long>> IDom { get; }

        /// <summary>Gets the true predicate.</summary>
        public Constructor<Pred> PTrue { get; }

        /// <summary>Gets the false predicate.</summary>
        public Constructor<Pred> PFalse { get; }

        /// <summary>Gets conjunction.</summary>
        public Constructor<Pred, Pred, Pred> And { get; }

        /// <summary>Gets disjunction.</summary>
        public Constructor<Pred, Pred, Pred> Or { get; }

        /// <summary>Gets negation.</summary>
        public Constructor<Pred, Pred> Not { get; }

        /// <summary>Gets c.p = 'v'.</summary>
        public Constructor<string, string, Pred> StrEq { get; }

        /// <summary>Gets c.p &gt;= 'v'.</summary>
        public Constructor<string, string, Pred> StrGe { get; }

        /// <summary>Gets c.p &gt; 'v'.</summary>
        public Constructor<string, string, Pred> StrGt { get; }

        /// <summary>Gets c.p = true.</summary>
        public Constructor<string, Pred> BoolTrue { get; }

        /// <summary>Gets c.p &gt; n.</summary>
        public Constructor<string, long, Pred> NumGt { get; }

        /// <summary>Gets c.p &lt; n.</summary>
        public Constructor<string, long, Pred> NumLt { get; }

        /// <summary>Gets IS_DEFINED(c.p).</summary>
        public Constructor<string, Pred> IsDefined { get; }

        /// <summary>Gets IS_STRING(c.p).</summary>
        public Constructor<string, Pred> IsString { get; }

        /// <summary>Gets a UUID comparison evaluated in process.</summary>
        public Constructor<string, string, Pred> UuidEq { get; }

        /// <summary>Gets an instant comparison evaluated in process.</summary>
        public Constructor<string, string, Pred> InstantGt { get; }

        /// <summary>Gets the query's WHERE clause.</summary>
        public Relation<Pred> WhereClause { get; }

        /// <summary>Gets the WHERE clause's top-level conjuncts.</summary>
        public Relation<Pred> Conjunct { get; }

        /// <summary>Gets "the predicate is exactly p IN S".</summary>
        public Function<Pred, string, ValueSet<string>> InSet { get; }

        /// <summary>Gets "the predicate implies p IN S".</summary>
        public Function<Pred, string, ValueSet<string>> Admits { get; }

        /// <summary>Gets the Horn ruleset.</summary>
        public Ruleset Horn { get; }

        /// <summary>Gets the lattice ruleset.</summary>
        public Ruleset Lattice { get; }

        /// <summary>Gets the rewrite ruleset, erasing under <see cref="Schema"/>.</summary>
        public Ruleset Rewrite { get; }

        /// <summary>
        /// What a schema compiler emits for one discriminated branch: under path = value, the consequences hold. The rule
        /// is local to the container's schema scope, so containers sharing this theory never see each other's branches.
        /// </summary>
        /// <param name="schema">The container's schema scope, not yet frozen.</param>
        /// <param name="path">The discriminator path.</param>
        /// <param name="value">The branch's value.</param>
        /// <param name="consequences">What the branch declares.</param>
        /// <returns>The rule.</returns>
        public Rule DeclareBranch(Scope schema, string path, string value, params Fact[] consequences) =>
            schema.DefineRule(Horn, $"{path} = {value}", r =>
            {
                r.When(Present.Of(path), Dom.Of(path).Is(ValueSet.Of(value)));
                r.Then(consequences);
            });

        void DefineHorn()
        {
            Horn.DefineRule("a where clause is a conjunct", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(WhereClause.Of(e));
                r.Then(Conjunct.Of(e));
            });
            Horn.DefineRule("conjuncts of and", r =>
            {
                var (a, b) = r.Vars<Pred, Pred>("a", "b");
                r.When(Conjunct.Of(And.Of(a, b)));
                r.Then(Conjunct.Of(a), Conjunct.Of(b));
            });
            Horn.DefineRule("inset of equality", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(StrEq.Of(p, v)));
                r.Then(InSet.Of(e, p).Is(Primitives.Singleton<string>().Of(v)));
            });
            Horn.DefineRule("inset of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(InSet.Of(a, p)), sb.Is(InSet.Of(b, p)));
                r.Then(InSet.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });
            Horn.DefineRule("inset admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(s.Is(InSet.Of(e, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            foreach (var left in new[] { true, false })
            {
                Horn.DefineRule(left ? "and admits left" : "and admits right", r =>
                {
                    var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                    var s = r.Var<ValueSet<string>>("s");
                    r.When(e.Is(And.Of(a, b)), s.Is(Admits.Of(left ? a : b, p)));
                    r.Then(Admits.Of(e, p).Is(s));
                });
            }
            Horn.DefineRule("admits of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(Admits.Of(a, p)), sb.Is(Admits.Of(b, p)));
                r.Then(Admits.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });

            // What a conjunct establishes, written to the running scope: the query.
            Horn.DefineRule("a conjunct admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(Conjunct.Of(e), s.Is(Admits.Of(e, p)));
                r.Then(Present.Of(p), Dom.Of(p).Is(s));
            });
            Horn.DefineRule("a negated equality", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.When(Conjunct.Of(Not.Of(StrEq.Of(p, v))));
                r.Then(Present.Of(p), Ne.Of(p, v));
            });
            foreach (var comparison in new[] { UuidEq, StrGe, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} proves presence", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Conjunct.Of(comparison.Of(p, v)));
                    r.Then(Present.Of(p));
                });
            }
            Horn.DefineRule("a numeric lower bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumGt.Of(p, n)));
                r.Then(Present.Of(p), Lo.Of(p).Is(Primitives.Add.Of(n, 1L)), Types.Of(p).Is(ValueSet.Of("number")));
            });
            Horn.DefineRule("a numeric upper bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumLt.Of(p, n)));
                r.Then(Present.Of(p), Hi.Of(p).Is(Primitives.Subtract.Of(n, 1L)), Types.Of(p).Is(ValueSet.Of("number")));
            });
        }

        void DefineLattice()
        {
            Lattice.DefineRule("a domain settles the type", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Then(Types.Of(p).Is(ValueSet.Of("string")));
            });
            Lattice.DefineRule("a form describes strings", r =>
            {
                var (p, f) = r.Vars<string, string>("p", "f");
                r.When(Form.Of(p, f));
                r.Then(Types.Of(p).Is(StringOrNull));
            });
            Lattice.DefineRule("a disequality narrows a domain", r =>
            {
                var (p, v, d) = r.Vars<string, string, ValueSet<string>>("p", "v", "d");
                r.When(Ne.Of(p, v), d.Is(Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Lattice.DefineRule("a lower bound narrows an integer domain", r =>
            {
                var (p, d, l) = r.Vars<string, ValueSet<long>, long>("p", "d", "l");
                r.When(d.Is(IDom.Of(p)), l.Is(Lo.Of(p)));
                r.Then(IDom.Of(p).Is(Compute.Of(d, l, (d, l) => d.Where(x => x >= l))));
            });
            Lattice.DefineRule("an upper bound narrows an integer domain", r =>
            {
                var (p, d, h) = r.Vars<string, ValueSet<long>, long>("p", "d", "h");
                r.When(d.Is(IDom.Of(p)), h.Is(Hi.Of(p)));
                r.Then(IDom.Of(p).Is(Compute.Of(d, h, (d, h) => d.Where(x => x <= h))));
            });
            Lattice.DefineRule("an empty domain", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(Present.Of(p), d.Is(Dom.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(d));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("an empty integer domain", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<long>>("p", "d");
                r.When(Present.Of(p), d.Is(IDom.Of(p)));
                r.Where(Primitives.IsEmpty<long>().Of(d));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("no possible type", r =>
            {
                var (p, t) = r.Vars<string, ValueSet<string>>("p", "t");
                r.When(Present.Of(p), t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("crossed bounds", r =>
            {
                var (p, l, h) = r.Vars<string, long, long>("p", "l", "h");
                r.When(Present.Of(p), l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Graph.Contradiction.Of());
            });
        }

        void DefineRewrite()
        {
            // These four judge on the query's own facts, and say why that is sound (D12).
            Rewrite.DefineRule("lower uuid", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.ReadsOwnFacts("the guard that proved the form is a conjunct that stays in the predicate");
                r.When(e.Is(UuidEq.Of(p, v)), Form.Of(p, "uuid-lower"));
                r.Then(e.Is(StrEq.Of(p, v)));
            });
            Rewrite.DefineRule("lower instant", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.ReadsOwnFacts("the guard that proved the form is a conjunct that stays in the predicate");
                r.When(e.Is(InstantGt.Of(p, v)), Form.Of(p, "instant-ms"));
                r.Then(e.Is(StrGt.Of(p, v)));
            });
            Rewrite.DefineRule("contradicted predicate", r =>
            {
                var e = r.Var<Pred>("e");
                r.ReadsOwnFacts("a query with no documents makes every predicate equivalent to false");
                r.When(WhereClause.Of(e), Graph.Contradiction.Of());
                r.Then(e.Is(PFalse.Of()));
            });
            // NOT IS_STRING admits nothing a present string-or-null path holds: it is NOT IS_DEFINED.
            Rewrite.DefineRule("a string path's NOT IS_STRING", r =>
            {
                var (e, p, t) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "t");
                r.ReadsOwnFacts("an equivalence under type facts whose conjuncts stay in the predicate");
                r.When(e.Is(Not.Of(IsString.Of(p))), t.Is(Types.Of(p)));
                r.Where(t, t => t.IsSubsetOf(StringOrNull));
                r.Then(e.Is(Not.Of(IsDefined.Of(p))));
            });

            // Deletions read facts only from the schema, and the ruleset's licence checks it: a conjunct's own
            // consequences in the query (present(kind) from kind = 'A') can never delete it. present@schema and
            // dom@schema are mirrors.
            Rewrite.DefineRule("declared tautology", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(s.Is(InSet.Of(e, p)), Present.In(Schema).Of(p), d.Is(Dom.In(Schema).Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(d, s));
                r.Then(e.Is(PTrue.Of()));
            });
            Rewrite.DefineRule("declared presence", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                r.When(e.Is(IsDefined.Of(p)), Present.In(Schema).Of(p));
                r.Then(e.Is(PTrue.Of()));
            });

            // These read terms only, so the licence does not constrain them.
            Rewrite.DefineRule("a definedness test absorbs its negation", r =>
            {
                var (p, x) = r.Vars<string, Pred>("p", "x");
                r.Rewrite(And.Of(IsDefined.Of(p), Or.Of(Not.Of(IsDefined.Of(p)), x)), And.Of(IsDefined.Of(p), x));
            });
            foreach (var comparison in new[] { StrGe, StrEq })
            {
                Rewrite.DefineRule($"{comparison.Name} implies defined", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.Rewrite(And.Of(IsDefined.Of(p), comparison.Of(p, v)), comparison.Of(p, v));
                });
            }

            // Boolean simplification.
            Rewrite.DefineRule("and commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(And.Of(a, b), And.Of(b, a)); });
            Rewrite.DefineRule("or commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(Or.Of(a, b), Or.Of(b, a)); });
            Rewrite.DefineRule("true and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PTrue.Of(), a), a); });
            Rewrite.DefineRule("false and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PFalse.Of(), a), PFalse.Of()); });
            Rewrite.DefineRule("false or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PFalse.Of(), a), a); });
            Rewrite.DefineRule("true or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PTrue.Of(), a), PTrue.Of()); });
        }

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/Scoped/CosmosTheory.Cases.cs  [R3]
namespace Eggnog.Tests.Cosmos.Scoped
{

    public sealed partial class CosmosTheory
    {

        /// <summary>Gets the tag of a discriminator split's cases.</summary>
        public ScopeTag Case { get; private set; } = null!;

        /// <summary>Gets the paths the running scope's predicate terms mention: an analysis, derived from terms alone.</summary>
        public Relation<string> Mentioned { get; private set; } = null!;

        /// <summary>Gets the split a schema is compiled with: every present discriminator.</summary>
        public SplitRule SchemaSplit { get; private set; } = null!;

        /// <summary>Gets the split a query runs: the present discriminators its predicate mentions (D43).</summary>
        public SplitRule QuerySplit { get; private set; } = null!;

        /// <summary>Gets the schedule a schema is compiled with: facts and splits, no rewrites.</summary>
        public Schedule Facts { get; private set; } = null!;

        /// <summary>
        /// Gets the schedule a query runs: the prototype's (saturate horn lattice split rewrite), with the facts settled
        /// before each split step, so that the split sees the narrowest domain (D43).
        /// </summary>
        public Schedule Saturation { get; private set; } = null!;

        partial void DefineCases()
        {
            Case = Graph.DefineScopeTag("case");

            // A path is mentioned when a comparison or test over it is a term of the running scope.
            Mentioned = Graph.DefineRelation<string>("mentioned", new TableOptions { Role = TableRole.Analysis });
            foreach (var comparison in new[] { StrEq, StrGe, StrGt, UuidEq, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} mentions its path", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Fact.Exists(comparison.Of(p, v)));
                    r.Then(Mentioned.Of(p));
                });
            }
            foreach (var test in new[] { BoolTrue, IsDefined, IsString })
            {
                Horn.DefineRule($"{test.Name} mentions its path", r =>
                {
                    var p = r.Var<string>("p");
                    r.When(Fact.Exists(test.Of(p)));
                    r.Then(Mentioned.Of(p));
                });
            }
            foreach (var bound in new[] { NumGt, NumLt })
            {
                Horn.DefineRule($"{bound.Name} mentions its path", r =>
                {
                    var (p, n) = r.Vars<string, long>("p", "n");
                    r.When(Fact.Exists(bound.Of(p, n)));
                    r.Then(Mentioned.Of(p));
                });
            }

            // One case per value a present discriminator can still hold: the cases cover the scope. A query splits only
            // what it mentions; the schema's own split has already lifted what the schema alone implies.
            SchemaSplit = Graph.DefineDomainSplit("discriminator", Dom, [Discriminator, Present], Case);
            QuerySplit = Graph.DefineDomainSplit("mentioned discriminator", Dom, [Discriminator, Present, Mentioned], Case);

            var settle = Schedule.Saturate(Horn, Lattice);
            Facts = Schedule.Saturate(settle, Schedule.Split(SchemaSplit, settle));
            Saturation = Schedule.Saturate(settle, Schedule.Split(QuerySplit, settle), Rewrite);
        }

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/Scoped/Scenario02QueryDisjunctionTests.cs  [R2]
using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/02-query-disjunction.egg: kind IN ('order', 'invoice') lowers the UUID comparison on ref
    /// only through a case split, and the schema alone still proves nothing about ref, because a note carries none.
    /// </summary>
    public sealed partial class Scenario02QueryDisjunctionTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The container: kind is required and one of order, invoice, note; order and invoice declare ref a canonical
        /// lowercase UUID.
        /// </summary>
        static void DeclareContainer(CosmosTheory c, Scope schema)
        {
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind"));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
        }

        /// <summary>
        /// Compiles the container with horn and lattice rules only: what R2 can do.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainerWithoutSplit()
        {
            var graph = new EGraph();                               // serial: deterministic (D46)
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();                                    // empty and frozen: containers are siblings beneath it

            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);
            var compiled = schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            Assert.True(compiled.Saturated);
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// (let $q (And (Or (StrEq "kind" "order") (StrEq "kind" "invoice")) (UuidEq "ref" ...))) (where $q)
        /// </summary>
        static Pred AssertQuery(CosmosTheory c, Scope query)
        {
            var q = query.Make(c.And.Of(
                c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
                c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));
            return q;
        }

        static Term<Pred> Lowered(CosmosTheory c) => c.And.Of(
            c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
            c.StrEq.Of("ref", Ref));

        [Fact]
        public void HornAndLatticesAloneDoNotLower()
        {
            var (c, schema) = CompileContainerWithoutSplit();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);

            // (run-schedule (saturate horn lattice rewrite))
            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.Equal(ValueSet.Of("invoice", "order"), query.Get(c.Dom.Of("kind")));   // the domain narrows...
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));                     // ...but no single fact proves a guard
            Assert.False(query.AreEqual(q, Lowered(c)));
        }

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/Scoped/Scenario02QueryDisjunctionTests.Cases.cs  [R3]
using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    public sealed partial class Scenario02QueryDisjunctionTests
    {

        /// <summary>
        /// Compiles the container with its own split: the schema's cases are order, invoice and note.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();

            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);
            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(3, compiled.CasesOpened);                  // order, invoice, note
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));   // a note carries no ref
            Assert.Empty(schema.Children);
            schema.Freeze();
            return (c, schema);
        }

        [Fact]
        public void TwoKindsNamedTogetherLowerTheReference()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);

            // The prototype's (run-schedule (saturate horn lattice split rewrite)), facts settled before the split.
            var report = query.Run(c.Saturation);

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.Equal(2, report.CasesOpened);                    // one case per value kind can still hold
            Assert.Empty(query.Children);                           // the cases were disposed when the run ended

            // (check (form (Query) "ref" "uuid-lower")) and (check (= $q (And (Or ...) (StrEq "ref" ...))))
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, Lowered(c)));

            // (fail (check (form (Schema) "ref" "uuid-lower"))): read from the schema, and through the scoped handle.
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.False(query.Check(c.Form.In(c.Schema).Of("ref", "uuid-lower")));

            // (extract $q)
            var best = query.Extract(q);
            Assert.False(best.Mentions(c.UuidEq));
            Assert.Equal(1 + (1 + 3 + 3) + 3, best.Cost);           // And + Or of two StrEq + StrEq
            Assert.Equal(
                $"(And (Or (StrEq \"kind\" \"order\") (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))",
                best.ToString());
        }

        [Fact]
        public void TheSameSplitDrivenByTheHost()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);
            query.Run(Schedule.Saturate(c.Horn, c.Lattice));

            // The caller's coverage obligation: kind is present in the query and holds one of dom(kind).
            Assert.True(query.Check(c.Present.Of("kind")));
            var kinds = query.Get(c.Dom.Of("kind"));
            Assert.Equal(ValueSet.Of("invoice", "order"), kinds);

            LiftReport lifted;
            using (var cases = query.OpenCases())
            {
                foreach (var kind in kinds)
                {
                    var k = cases.Add(c.Case);
                    k.Assume(c.Dom.Of("kind").Is(ValueSet.Of(kind)));
                    k.Run(Schedule.Saturate(c.Horn, c.Lattice));    // a case always stops on contradiction (D11)
                    Assert.False(k.IsContradictory);                // reads below would throw on a stopped case (D45)
                    Assert.True(k.Check(c.Form.Of("ref", "uuid-lower")));
                }

                lifted = cases.Lift();
            }

            Assert.Equal(2, lifted.LiveCases);
            Assert.Equal(0, lifted.VacuousCases);
            Assert.True(lifted.Rows >= 1);                          // form(ref, uuid-lower)
            Assert.True(lifted.Changed);
            Assert.Empty(query.Children);

            query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, Lowered(c)));
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
        }

        [Fact]
        public void AQueryThatDoesNotMentionTheDiscriminatorDoesNotSplit()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.UuidEq.Of("ref", Ref));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(c.Saturation);

            Assert.True(report.Saturated);
            Assert.Equal(0, report.CasesOpened);                    // kind is not mentioned (D43)
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Equal($"(UuidEq \"ref\" \"{Ref}\")", query.Extract(q).ToString());
            Assert.Equal(1002, query.Extract(q).Cost);
        }

    }

}
```

**What happens when the scenario runs.**
1. **Theory.** `CosmosTheory` defines the graph's tags (`schema`, `query`, and in R3 `case`), every table and every
   rule before any scope exists. The two deletion rules read `present` and `dom` in `schema`, so the graph creates the
   mirrors `present@schema` and `dom@schema` and their copy rules. The rewrite ruleset is erasing under `schema`; its
   four rules that judge on the query's facts say why (`ReadsOwnFacts`), and `Rule.ToString` shows it.
2. **Schema compilation** (`Facts`).
   - The inner saturation runs the global rules and the two local branch rules. No branch fires: `dom(kind)` has three
     values.
   - The split step (`SchemaSplit`) finds one request: key `kind`, set `{order, invoice, note}`. The `SplitDriver`
     opens three cases, each assuming its singleton domain.
   - In each case the branch rules (local to the schema, so active in its descendants) fire for order and invoice.
     They inherit the schema's watermarks (colors §10.2), so each case matches only the rows its assumption added.
   - The lift finds `form(ref, uuid-lower)` missing in the note case, so it is not lifted. The case join of `dom`,
     `{order} ∪ {invoice} ∪ {note}`, is the schema's own domain, so absorption writes nothing. The next pass skips the
     covered key (B6) and changes nothing.
   - The records are disposed when `Run` returns. `Freeze` syncs the schema's mirrors (one copy iteration, D48) and
     freezes it, pre-planning the rules its descendants can run on the schema's statistics (E10).
3. **Query without the split** (R2). `admits` sees through `And` and `Or`, so `dom(kind)` narrows to
   `{order, invoice}` in the query. The branch rules need a singleton domain, so neither fires, and `lower uuid` has no
   `form` to match.
4. **Query with the split** (`Saturation`).
   - The inner saturation narrows `dom(kind)` to `{order, invoice}` (`admits` needs several iterations), and derives
     `mentioned(kind)` and `mentioned(ref)` from the comparison terms.
   - Only then does the split step run: `QuerySplit` matches `kind` (present, a discriminator, mentioned) with
     `{invoice, order}`, giving two cases tagged `case`. A split taken after a single iteration would still have seen
     `{order, invoice, note}` and opened a third case that later goes vacuous.
   - Each case derives `form(ref, uuid-lower)` and `types(ref) = {string, null}`; the lift writes both into the query.
   - The rewrite step's `lower uuid` unions `UuidEq(ref, R)` with `StrEq(ref, R)`. Later passes absorb the lifted facts
     and the commuted spellings; the split step skips the covered key (B6). A pass changes nothing, so the run is
     saturated with `CasesOpened == 2`.
   - A query that mentions only `ref` derives no `mentioned(kind)`, and its split step opens nothing.
5. **Extraction.** Ties among the commuted `And` and `Or` spellings go to the lower class id (D10). The host's `Make`
   built the subterms in post-order, so here the spelling as written wins. In the `UuidEq` class, `StrEq` costs 3
   against 1002 for `UuidEq`. The written spelling does not always win: when a lowered comparison sits in a class the
   host made before its sibling's, the commuted spelling has the lower ids. Scenario 07's per-kind term is such a case:
   in `(And (StrEq "data.type" "map") (UuidEq "data.data.parkId" …))` the `parkId` class was made first (by the first
   disjunct), so Eggnog extracts `(And (StrEq "data.data.parkId" …) (StrEq "data.type" "map"))`, of equal cost 15
   overall, where egglog keeps the written order (§8.1.1).

---

## 11. Residual risks

These are accepted. None is an open question: each has a decision above.

1. **Coverage cannot be checked.** It is stated on every split and case API, and a lift with no cases throws. A wrong
   domain encoding in a schema compiler is still a silent soundness bug in that theory.
2. **The erasing guarantee does not cover equalities.** It covers facts (§6.3 step 6), now by default for a ruleset
   with a licence. Equalities the running scope derived through lowerings rest on the justifying conjuncts surviving
   into the extracted predicate (the prototype's argument), and every rule that relies on that names it with
   `ReadsOwnFacts`. A debug auditor could re-extract the predicate and compare it on sample documents.
3. **Pools only grow** (D15). A long-lived service should recycle its `EGraph` once `InternedCount` passes a threshold;
   recompiling a container is cheap. Set-valued primitives intern a new set per distinct result, and so do the
   constants of per-request queries.
4. **Plans are made once per rule**, on the statistics of the first context that runs or pre-plans it (decision 7,
   colors §9.7), and per-context replanning is deferred (colors §19). With E10 the empty root plans nothing, and a
   container's freeze plans the rules its descendants can run on that container's statistics; later containers reuse
   those plans, which may suit them less well.
5. **Term lift costs rounds**, one per nesting level, and `CandidateLimit` bounds the product of member lists. Cosmos
   lifts value-keyed facts only, so term lift is exercised by `LiftTests` and the engine's own tests.
6. **Termination.** A lattice with infinite descending chains (i64 bounds combined with arithmetic rules) never
   saturates. The limits stop the run soundly (`RowLimit` bounds its size), and the report says why; the host decides
   with what has been proven.
7. **Purity cannot be enforced.** Primitives, lattices, cost functions and filters must be pure and thread-safe. This
   is documented on every parameter that takes one; calls back into a graph are refused (D47), and
   `ValueLattice<T>.Verify` covers the lattice laws in tests.
8. **No rollback** (D18). After a `RuleEvaluationException` from a run or a write the scope is faulted. A faulted root
   makes the graph unusable; the remedy is a new graph.
9. **Scoped reads must be defined before tagged scopes of the same graph freeze** (§6.8). A late first read is rejected
   with a message that says so. Theories that define all their global rules up front, as `CosmosTheory` does, never
   meet it.
10. **R1 re-lowering** (§6.4.5) re-planned a rule whenever one of its class constants lost a union. It ended with R2,
    which lowers each variant once and leaves the constants to the engine's per-context canonicalization (X19).
11. **Split demand trades completeness for cost** (D43). A query that constrains a path a discriminator's branches
    govern, without mentioning the discriminator, gets no case split on it, so facts only that split would prove are
    missed (soundly). The schema's own split has already lifted everything the schema alone implies. A theory that
    needs more can add a `governs(k, p)` fact per branch path and guard the query split on "mentions k, or mentions a
    path k governs".
12. **Class-origin checks are conservative** (D39). A value obtained from a child is refused in its parent even when its
    class was made in the parent, and a value dies with the scope that returned it. The remedy is to obtain the value
    again from the scope where it is used.
13. **Deferred release keeps memory alive** (D40). A retired frozen schema lives until its last query ends, and a child
    that is never disposed keeps its ancestors' engine state alive.
14. **Class constants equated in a body are compared where the rule runs** (§6.3 step 3, N86). A rule whose body
    equates two class constants of different classes is accepted, and an `Equal` step compares them in each context
    the rule runs in, so it matches in a child that unions them. The step costs one comparison per match; two
    constants already of one class where the rule's constants come from are merged at definition, and the rule keeps
    the first, with no step.
15. **The host merge check reads tables** (D32, §6.6.1). In a graph with a function without a lattice keyed by a sort, a
    host batch that merges reads every row of the tables a merge can reach, once per batch. Batch such merges together
    (`Scope.Write`), or give the function a lattice. Cosmos defines no such function.
16. **An open `Write` batch holds its ancestors' writes** (N88). While a batch is open on a scope, a write or run into
    one of its writable ancestors from the batch's callback throws `InvalidOperationException`, because it could make
    the batch's checked plan wrong. Write the ancestor before or after the batch; reads and queries of the ancestors,
    and writes into descendants and siblings, are allowed meanwhile.

---

## 12. Appendix: porting an egglog program by hand

The Cosmos tests port `.egg` files by hand, with this correspondence. There is no parser (D14).

| egglog | Eggnog |
|---|---|
| `(datatype S (C … :cost n) …)`, `(sort S)` | `DefineSort<S>("S")`, then `DefineConstructor<…, S>("C", cost: n)` |
| `(constructor C (A B) S :cost n)` | `DefineConstructor<A, B, S>("C", cost: n)` |
| `(relation R (A B))` | `DefineRelation<A, B>("R")` |
| `(function f (A) T :merge (set-intersect old new))` | `DefineFunction<A, T>("f", ValueLattice.Intersect<…>())` |
| `:merge (max old new)`, `(min old new)`, `(set-union old new)` | `ValueLattice.Max<T>()`, `Min<T>()`, `Union<T>()` |
| `(function f (A) T)` (no merge) | `DefineFunction<A, T>("f")`: conflicting values are an error |
| `(sort Strs (Set String))` | `ValueSet<string>`, built in |
| `(ruleset r)` | `var r = graph.DefineRuleset("r")` |
| `(rule (facts…) (actions…) :ruleset r)` | `r.DefineRule("name", b => { b.When(…); b.Then(…); })` |
| `(rewrite l r :ruleset rs)` | `rs.DefineRule("name", b => b.Rewrite(l, r))` |
| `(= x (f a))` in a body | `x.Is(F.Of(a))` |
| `(= (f a) (set-of "B"))` in a body | `F.Of(a).Is(ValueSet.Of("B"))` |
| `(!= a b)` in a body | `a.IsNot(b)` |
| `(set-contains d v)`, `(> a b)` in a body | `b.Where(Primitives.Contains<string>().Of(d, v))`, `b.Where(Primitives.Greater.Of(a, b))` |
| `(= 0 (set-length s))` in a body | `Primitives.Count<…>().Of(s).Is(0L)`, or `b.Where(Primitives.IsEmpty<…>().Of(s))` |
| `(set (f a) v)` in a head | `F.Of(a).Is(v)` |
| `(R a)` in a head | `R.Of(a)` |
| `(union a b)` | `a.Is(b)` |
| `(+ n 1)`, `(set-of v)`, `(set-remove d v)` | `Primitives.Add.Of(n, 1L)` (partial, as in egglog: an overflow drops a body match, D31), `Primitives.Singleton<…>().Of(v)`, `Primitives.Remove<…>().Of(d, v)` |
| `(let $q term)` | `var q = scope.Make(term)` |
| a top-level fact or `set` | `scope.Assume(…)` |
| `(run-schedule (saturate a b))`, `(run r n)`, `(seq …)` | `scope.Run(Schedule.Saturate(a, b))`, `Schedule.Repeat(n, r)`, `Schedule.Sequence(…)` |
| `(check fact)`, `(fail (check fact))` | `Assert.True(scope.Check(fact))`, `Assert.False(…)` |
| `(check (= $q term))` | `scope.AreEqual(q, term)` |
| `(extract $q)` | `scope.Extract(q)` |
| `(push)` … `(pop)` | a fresh graph (R1), or a child scope disposed afterwards (R2) |
| `(print-size f)` | `scope.CountRows(f)`; `scope.Rows(f)` lists them |
| `(include "prelude.egg")` | a C# theory class |
| `delete`, `subsume`, `:merge old`/`new` | not supported (D33, §2.5) |

---

## 13. Edits this spec asks of the colored-contexts spec

None changes a colors decision; they align its text with what the rule layer now does and asks.

1. **Citations.** The rule-layer decisions the header cites keep their numbers. D20 no longer registers a container,
   and D21 (callbacks) is deferred, so the "callback rules" of X41, §10.2 and I31 have no rule-layer instance in version
   2 (the text can stay). §5.7 cites "rule-layer §2.1" and I33 "rule-layer §3.9": those are version 1's section
   numbers; cite D8 and §6.9 instead.
2. **§10.7.** Drop "the rule layer's built-in `ValueSet<T>` (rule-layer D20) is one, registered by that layer": the
   rule layer registers no container; `ValueSet<T>` is an interned base value.
3. **§3.4 / M9.3.** A child `Database` should share the root's `BaseValues` by reference, as it shares counters and
   external functions (former E2). Engine code that reads base values through `ExecutionState.BaseValues` in a child
   would otherwise see an empty registry. Track it as an M9.3 defect; the rule layer does not depend on it.
4. **§10.1 `ContextLocalSlot<T>`.** The rule layer keeps one `ContextLocalSlot<ScopeRuntime>` per graph (former E6). If
   `GetLocal` cannot construct a value on first use, add a factory parameter (or a setter).
5. **§10.1 and §3.5.** `EGraph.DesignateContradiction` (E4); `EGraphContext.CreateChild`, `Dispose`, `IsFaulted`,
   `Parent` and `OwnRowCount`, and `RunRules` reporting at the graph's `ReportLevel` (E7); `ParallelHeuristics` on the
   Store-backed `EGraph` constructor (E9).
6. **§10.8 step 4.** Plan exactly the given rules when `Freeze(planRules)` receives a collection (E10).
7. **An extraction section** (E5): extraction over an `EGraphContext`, with staleness keyed on the extraction tables
   and the union-find of the context and its non-frozen ancestors, not on the clock.
8. **§11.2 `CaseSet`.** `AddCase(ContextOptions?)`, `DisposeVacuous()`, and `Lift` with a cancellation token checked
   between rounds (E8). The rule layer lifts its relations as `LiftTableKind.Function` with a unit output, so no change
   to `LiftTableKind.Relation` is needed.
9. **§11.3 split hooks.** `ISplitSource.FindRequests` receives the `SplitReport`; `SplitOptions.CaseOptions`;
   `SplitDriver.Step` does not hold the scope's thread domain while it calls sources and schedules (E8).

Items 1–3 are applied in colors §21.8 (item 3 with colors M10.1, colors §21.6). Items 4–7 were applied with colors
M10.3: items 4 and 5 in colors §3.5 and §10.1, item 6 in §10.8, item 7 in §10.10, recorded in colors §21.9 and here
in §15.6. Items 8–9 were applied with colors M10.4, in colors §11.2 and §11.3, recorded in colors §21.10 and here
in §15.7.

---

## 14. Review responses

Two reviews were answered. Every finding was accepted. Where a reviewer offered alternatives, the table says which was
taken and why.

**First review**

| # | Severity | Finding | Resolution |
|---|---|---|---|
| A1 | critical | `ScopeTag.FrozenCount` was process-wide and the Cosmos tags static, so every scoped test after the first failed; tags compared by reference never resolved across instances | Tags belong to a graph: `EGraph.DefineScopeTag`, `EGraph.RootTag`, no public constructor, unique names per graph; another graph's tag is refused everywhere a tag is taken; frozen counts live in the graph's `MirrorRegistry`; `CosmosTheory` holds instance tags. D38, §3.1, §6.7, §6.8, §10, `ScopeTagTests.TwoGraphsShareNoTagState` (both orders and parallel). |
| A2 | major | The split ran after one horn iteration, so `CasesOpened` was 3, not 2, in 02 and 07 | Facts settle before each split step: `Facts = Saturate(Saturate(Horn, Lattice), Split(...))`, `Saturation = Saturate(Saturate(Horn, Lattice), Split(...), Rewrite)`; `Schedule.Split`'s docs recommend it; narrative step 4 corrected; the literal suite keeps egglog's schedule and figures. D43, §3.6, §6.9.3, §8.1.2, §10. |
| A3 | major | No way to split only on what a query touches | Demand is the split rule's body: `DefineDomainSplit` takes a list of guards. `CosmosTheory` derives the analysis relation `Mentioned` from comparison terms, splits schemas unguarded and queries guarded by `Mentioned`. Tests: a query that does not mention the discriminator opens 0 cases (02, 04). Guards were taken over a delegate demand because they stay in the rule language, render, and are covered by the split oracle; the rule layer passes no `ISplitDemand`. D43, §3.1, §6.9.2, §10, §11 risk 11. |
| A4 | major | `Dispose` threw on the root and with live children; no way to retire a schema while queries run; transient children unspecified | `Dispose` never throws; deferred release when children (transient ones included) or a call on another thread remain; a second call and the root's do nothing; transient children are not listed but defer the release. The deferred release was taken over a cascade because a cascade would dispose queries running on other threads. D40, §3.1, §6.7, `DisposeTests`. |
| A5 | major | Class values unchecked: fabricable, `default` meaningful, mixable across queries and graphs | `ClassId` has no public constructor and carries its origin scope; `default` has none and is refused; every host entry point, rule and query checks graph, ancestry, disposal and batch commit; global rules hold only root classes. An origin was used instead of an offset-by-one cell: it rejects `default` the same way and also catches cross-scope use. D39, §3.1, §6.6.7, invariant O, `ClassIdTests`. |
| A6 | major | Thin diagnostics; explanations not decided; faults lose their cause | Per-rule statistics with search time and plans, merge and rebuild times; `RunStep`s with `RecordSteps` and an observer; `CountRows`, `Rows`; `Scope.Failure` as `InnerException`; explanations recorded as out of version 2 with a recipe, provenance deferred. D41, §3.6, §3.9, §6.11, `DiagnosticsTests`. |
| A7 | major | `ChangeLimit` counted table merges and bounded no size | `RowLimit` (own-layer rows of the top scope and live cases) replaces it, with `RunStopReason.RowLimit`; overshoot of at most one iteration or lift documented; limits per top-level `Run` across cases. D42, §3.6, §6.5.4. |
| A8 | major | "A conjunct never proves itself redundant" was opt-in | A ruleset licence makes every rule erasing; `ReadsOwnFacts(reason)` opts out explicitly and renders; the four query-judged Cosmos rewrites opt out; term-only rules are unaffected. D12, §3.5, §6.3 step 6, invariant E, §10, `ErasingTests`. |
| A9 | major | Nothing appears in `src/Eggnog` until all of R1 is done | R1 split: R1a lands the library (project reference, spike removal, values, definitions, handles, terms, compilation and lowering, `Run` with `Saturate`/`Repeat`/`Sequence`, host writes and reads, extraction, Literal 01–03, `PublicSurfaceTests`) and merges to main first; R1b adds queries, `Until`, limits, diagnostics, Literal 04–07 and the differential; callbacks are deferred. §9, status note. |
| A10 | major | The spike reference stayed until R4 and the package shipped it | Both removals moved into R1a (`src/Eggnog/Eggnog.csproj`, `src/dist-nuget/dist-nuget.csproj`); the README update too; R4 is gone. §9. |
| A11 | major | The engine's public look-alike types clash with the facade's (CS0104) | The engine's public types become internal (E11), as the conventions prescribe; `PublicSurfaceTests` asserts no simple-name clash across the package's assemblies; the `EditorBrowsable` fallback is recorded if the engine owner keeps a standalone API. D29, D44, E11. |
| A12 | minor | The threading contract of definitions changed between milestones; `DefineRule` thread rules unspecified | The R2 table rule is stated now and enforced by R1's root guard; rule definitions creating mirrors count as table definitions; `Scope.DefineRule` enters the scope's domain; global rule definitions any thread. D6, D47, §3.1, §6.12. |
| A13 | minor | `DegreeOfParallelism` default made ties nondeterministic in R1 and did nothing in R2 | Default 1; `ScopeOptions.DegreeOfParallelism` per scope from R2 (`ContextOptions.Parallelism`); E9 narrowed to the root; R2's done-when tests parallel equivalence explicitly. D46, §3.1, §9. |
| A14 | minor | Over-engineering: write callbacks, public `SemiNaive`, public untyped layer, `Scope.Split<T>` | Callbacks deferred (E3 withdrawn); `SemiNaive` internal; the untyped layer internal; `Scope.Split<T>` dropped in favour of `CaseSet`. D13, D21, D36, §3. |
| A15 | minor | Per-request queries leaked engine rules; no parameters | `Query : IDisposable`, freeing its rule once no call uses it; "define once" documented. Parameters were not added: the bridge runs primitive bindings after the join, so a per-call parameter would filter matches instead of restricting the join, and would hide that cost. D49, §3.5, §6.6.4. |
| A16 | minor | `In`/`InScope` names, write-through-scoped-handle typing, untyped `NotEqual`, `Rule` vs `DefineRule`, `Make` accepting any term | One name, `In` (the untyped `InScope` is internal); scoped views keep their type so they compose with helpers, and writes through them fail at definition with an exact message; `Term<T>.IsNot`; one verb, `DefineRule`; `Constructor.Of` returns `ConstructorTerm<TSort>`, which `Make` takes. §3.3–§3.5. |
| A17 | minor | Adapter tasks clumsy: one rebuild per `Make`, untyped term translation, counting needs a query | `Scope.Write(Action<ScopeWriter>)` batches with one flush; `ExtractedTerm.TryMatch` overloads with `ExtractedArgument<T>`; `Scope.CountRows`. §3.1, §3.8, §6.6.1. |
| A18 | minor | Conflicts reported as `ArgumentException`; no tokens on `Split`/`Lift`; `OpenCases` on frozen unspecified; splits of contradictory scopes | `FactConflictException : InvalidOperationException` with table, key and both values; a token on `CaseSet.Lift` (`Scope.Split` is gone); `OpenCases` throws on a frozen scope; the split node skips a contradictory scope. D32, §3.5, §3.7, §6.9.3, §6.9.4. |
| A19 | minor | The R2 Scenario02 test used R3 members | Partial classes: `Scenario02QueryDisjunctionTests.cs` (R2, with `CompileContainerWithoutSplit`) and `.Cases.cs` (R3); the same for `CosmosTheory` with a partial `DefineCases`. §10. |
| A20 | minor | (Informational) the unverified figures are correct at `ext/egglog` 5e655cae | Caveat dropped; the commit is named in `Literal07SweepLinksTests` and in §8.1.1; `CountRows(Parent) == 7` added. §8.1.1. |

**Second review**

| # | Severity | Finding | Resolution |
|---|---|---|---|
| B1 | major | R1 queries and no-op writes advanced the clock, making extractors stale | R1 queries run through `ExecuteRuleSet` without a merge or a clock tick; batches that stage nothing do not flush, and facts that hold stage nothing; the extractor contract says what may invalidate it; E5 states that context staleness ignores the clock and gates the whole R2 scoped suite; tests extract after queries and after no-op writes. D30, D49, §3.8, §6.6.1, §6.6.5, §6.10, E5. |
| B2 | major | Host queries over scoped atoms read mirrors that were never synced | Mirrors sync just before any iteration, query or split matcher reads them (the querying scope included), and at freeze; invariant M covers queries; `ScopedReadTests` and `QueryTests` cover host writes followed by queries. D48, §6.6.4, §6.8, §7.1. |
| B3 | major | Contradicted (stopped) cases were unmapped | Rule-layer semantics defined and checked first in every entry point: identity and `IsContradictory` answer, `Assume` no-op, `Union` false, `Run` returns `Contradiction`, the rest throw `ScopeContradictedException`; `TheSameSplitDrivenByTheHost` checks `IsContradictory` first; `ContradictedCaseTests`. D45, §6.6.8, §6.13. |
| B4 | minor | E6 duplicated `ContextLocalSlot` | E6 withdrawn: one `ContextLocalSlot<ScopeRuntime>` per graph, reached through `state.View.Owner.Owner.Host`; only a factory or setter is asked for if needed. §4.1, §5.2, §13. |
| B5 | minor | E3 duplicated `ReadsTables` | E3 withdrawn with callbacks; if they return, the collector overrides `ReadsTables`. D21, §5.2. |
| B6 | minor | E10's switch did not stop other containers' rules being planned on the wrong statistics | E10 restated as `Freeze(planRules)`; the rule layer passes nothing for the root and the rules a scope's descendants can run otherwise; the row-count heuristic is gone. E10, §6.7, §11 risk 4. |
| B7 | minor | Vacuous host-driven cases were never disposed; `InheritWatermarks` did not reach cases | `CaseSet.DisposeVacuous` after each lift; `AddCase(ContextOptions?)` and `SplitOptions.CaseOptions` carry `InheritWatermarks`. E8, §6.9.3, §6.9.4. |
| B8 | minor | `LiftTableKind.Relation` did not say which output to stage | Relations lift as `LiftTableKind.Function` with no case join: every live case holds unit, so agreement lifts the row with its output. §6.9.1. |
| B9 | minor | R2 relied on per-call engine domains; batches and runs could interleave; the engine domain cannot be held across a run | A rule-layer `ScopeGuard` per thread domain in every milestone, entered for whole calls and reassigned on freeze; the rule layer never holds an engine domain across calls; E8 asks `Step` not to hold the domain while calling sources and schedules. D47, §6.12, E8. |
| B10 | minor | A callback defining a rule could deadlock against a concurrent definition | Callbacks are deferred; no user code runs under the R1 engine lock except engine-invoked delegates, which may not call into a graph (enforced); observers run between engine calls. §6.12. |
| B11 | minor | Rule definitions creating mirrors enter the root's domain in R2 | Such rule definitions count as table definitions, documented on `EGraph`, `Ruleset.DefineRule` and in D6, and enforced the same way in R1. D6, §3.1, §6.3 step 2, §6.12. |
| B12 | minor | Catch-up copies move a child's `Version`; `Changed` and `ChangeLimit` saw them | `Version` reworded to include them; `RunReport.Changed` comes from iteration and lift flags; `ChangeLimit` replaced by `RowLimit`. D16, D42, §3.1, §6.5.1. |
| B13 | minor | `FindRequests` could not count skipped requests | `FindRequests` receives the `SplitReport`. E8, §6.9.2. |
| B14 | minor | The colors spec's §10.7 contradicts D20 | §13 lists the cross-spec edits, §10.7 and the former E2 among them. §13. |
| B15 | minor | R2 depended on E2, which it does not need | E2 withdrawn from this spec and tracked as a colors M9.3 defect; R2 depends on E4, E5, E7, E9 and E10. §5.2, §9, §13. |
| B16 | minor | `IdConstantTests` could not hold both in R1 (`/k1`) and unchanged in R2 | `IdConstantTests` asserts behaviour only; the `/k1` check moved to `RootRelowerTests`, deleted with `RootEngineScope` in R2. §8.3, §9. |

---

## 15. Implementation notes (R1a-R1b, the engine requests of colors M10.3-M10.4, and R2)

This section records how R1a, R1b and R2 were built against this spec, how colors M10.3 delivered this spec's engine
requests E4, E5, E7, E9 and E10 and §13 items 4–7, and how colors M10.4 delivered E8 and §13 items 8–9. Each note
says what the spec said, what was built, and why. Where the built design is the better one, the sections in the last
column now state it, and the note is the record of the change; where the spec was silent, those sections now fill the
gap. Notes are numbered **N1–N89**; what is still deferred, with its milestone, is in §15.5.

### 15.1 Where the work stands

- **R1a** is on main. `0fe0477` landed E1 and E11 in `Eggnog.Relations`; `63225f4` added the library; `edd1fa9` merged
  both with colors M9.3–M9.4, without conflicts. Every project change of §9 landed: `Eggnog.csproj` references
  `Eggnog.Relations` and not the spike; `dist-nuget` packs `Eggnog` and `Eggnog.Relations` only; the CI matrix lists
  `Eggnog.Tests`; the README describes the library. A search of `src/Eggnog.Relations` on main finds no public type, so
  E11 also holds for the engine files M9.3–M9.4 added.
- **R1b** is on main. `80675d8` added it with one engine change, E12 (N5); `3b9dda1` merged it with colors M10.1–M10.2,
  where the join kept after an early stop now releases what each of M10.2's prober pieces owns. The engine suite was
  green (8516 tests), and so was the 5000-seed differential run (39.0 s on net10.0, 39.4 s on net8.0, recorded in the
  `RuleLayerDifferentialTests` class remarks).
- **Colors M10.3 and M10.4** are on main. M10.3 delivered E4, E5, E7, E9 and E10 and §13 items 4–7 (§15.6); M10.4
  delivered E8 and §13 items 8–9 (§15.7). Every engine request R2 and R3 depend on is in.
- **R2** is on main, merged with colors M10.4 (§9 R2, §15.8). It changes no engine file. At the merge, `Eggnog.Tests`
  (498 tests), `Eggnog.Relations.Tests` (11,811 tests, 10 of them opt-in and skipped) and `Eggnog.Storage.Tests`
  (816) are green on net10.0 and net8.0.

### 15.2 Engine changes

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N1 | E1: `TryGetExisting` looks up without inserting; silent on a null key or an unregistered type | `TryGetExisting` throws as `Get` and `Unwrap` do: `ArgumentNullException` (from `InternTable.TryGetId`) for a null reference key, `InvalidOperationException` for an unregistered type whose value would be interned | One contract for the three lookups; either is a caller error | §5.2 E1 |
| N2 | E1: `InternTable<TKey>.TryGetId` looks up without inserting | It takes the key's shard lock; it is not lock-free | A shard table can be growing concurrently | §5.2 E1 |
| N3 | E1 lists the members to add, and their tests | An unrequested, uncalled and untested `BaseInternTable<TCodec, T>.TryGetExisting` that a first pass added was removed. The `OffsetEnumerator` remark, false under E11, was corrected (comment only). There are more tests than E1 lists (`TryGetExistingReviewTests`, `EngineSurfaceReviewTests`, `InternTableTests`) | Dead engine surface costs maintenance; the extra tests came from the reviews | — |
| N4 | §9 R1a: `dist-nuget` packs `Eggnog` and `Eggnog.Relations` | `src/Eggnog.Relations/README.md` was added | `Eggnog.Relations.csproj` set `PackageReadmeFile` to a file that did not exist, so packing failed with NU5039, a defect older than R1a | §9 R1a |
| N5 | §5.2, §9: R1b requests no engine change | **E12**: `JoinRun.ReleaseInterrupted` returns to their pools the sparse and per-stage tuple indexes that interrupted stages never moved back, then clears every prober. `Database.RunRulesSerial` calls it after an early stop (an action's `TriggerEarlyStop`, or a reported failure), then parks and keeps the join as after a full run. A run that throws still drops its join | §6.6.4 and its early-stop test (`QueryTests.AnyStopsTheEngineEarly`) require `Any` to stop the engine early; the conventions, and the review test `AnyAllocatesNoMoreThanAFullEngineRunPerRow`, require it not to allocate per row. Under the old engine both could not hold: an early stop dropped the serial join, so the next run rebuilt it and its leaf-scan buffers. The only rule-layer alternative, `Any` without an early stop, breaks §6.6.4. The new engine test `AnEarlyStoppedRunKeepsItsJoin` measures 24.6 bytes per row with the old parking rule restored, and passes with E12 | §5.2 E12, §6.6.4, §9 R1b |

### 15.3 R1a: the library

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N6 | D32: host conflicts are checked against the stored value and within a batch, so "merge conflicts can therefore only come from rules" | Merges are checked too. A union, a sort equality, or a constructor write that gives an existing key another class makes phase 1 simulate the rebuild's congruence closure (`Host/HostBatch.Merges.cs`, a partial of `HostBatch`): an overlay union-find and a signature table over the rows a merge can reach. A function without a lattice that would get two values for one key throws `FactConflictException` before anything is staged. The check runs only in a batch that merges, and only when the graph has a function without a lattice keyed by a sort; it then reads every row of the affected tables once. Lattice merges a union causes still run at the rebuild, so a lattice that throws there faults the scope | The spec's sentence was false: a host union can make two stored rows of such a function congruent, and the rebuild's panic would fault the scope instead of refusing the write | D32, §3.1 `Assume` and `Union`, §3.5 `FactConflictException`, §4.5, §6.1, §6.6.1, §6.6.2, §6.13, §8.3 `HostWriteTests`, §11 risk 15 |
| N7 | §4.1: `EngineScope.ForEach` is R1b (for `Rows`) | Added in R1a, with its `RootEngineScope` implementation | N6's merge check reads stored rows | §4.1 |
| N8 | §6.3: `c1.Is(c2)` is "evaluated now" | Value constants compare by cell. Class constants compare by their canonical classes in the root (`Find` under the engine lock) when the rule is defined: one class, dropped (the rule keeps the first constant); different classes, *"never matches"* | The root is where a global rule's class constants come from (D39); comparing written cells would refuse constants already unioned. Superseded: from R2 different classes are compared at run time by an `Equal` step and never refused (N63, N86) | §6.3 step 3, §11 risk 14 |
| N9 | §3.4, §6.1: an exception from a primitive fails the rule (R1a's first pass: overflow in `+` and `-` always fails it) | `Primitives.Add` and `Subtract` are partial, as egglog's `+` and `-` are (`checked_add`): an `OverflowException` in a body let drops the match without recording a failure; in a head it fails the rule (`RuleEvaluationException` with the rule name and bindings; the scope is faulted). Adds the internal `Primitive.IsPartial` and an `inBody` parameter to the `PrimitiveCall` constructor | egglog's semantics: a body match whose primitive has no result is no match. User primitives still have no "no result" outcome except throwing (§15.5) | D31, §3.4, §4.3, §4.4, §6.1, §6.4.2, §6.13 |
| N10 | §3.5: `Bindings` such as `d={a, b} v="b"`; `Message` unstated | A set argument renders as `ValueSet<T>.ToString` does (`{a, b}`), any other value as its s-expression literal (`"b"`, `42`); elsewhere sets render as `(set-of …)`. For a primitive, `Message` is `rule 'r': primitive 'p' threw {type}: {message}` | Matches the documented example; the message names the rule, the primitive and the cause | §3.5, §6.1 |
| N11 | §3.2: `Register<T>` refuses a taken type or name; `RegisterSet<T>` needs a registered element type that is not a sort | `Register<T>` also refuses `ClassId`, sort types and `ValueSet<>` types. `RegisterSet<T>` refuses an element type that is not `string` and implements neither `IComparable<T>` nor `IComparable`. `RegisterSet` and `RegisterEnum` are idempotent. `ValueTests.RegisteredTypesRoundTrip` now asserts the refusal for a non-comparable `Point` and uses `Set<Guid>` | Consistent with D20 (classes are never values; no sets of classes) and with the element order `ValueSet<T>` keeps | §3.2, §6.1 |
| N12 | §6.1: a registered type without an order compares by "the cell" | Interned values compare by cell; a value never interned sorts after every interned one; two never-interned values compare by hash code (zero when equal), a preorder stable within the process | `Compare` must not intern (D15), and a value never interned has no cell | §3.2, §6.1 |
| N13 | §3.1, §6.6.1: a rejected call writes nothing; silent on whether `Write` throws when the callback catches the rejection | `Write` returns normally and writes nothing: `Version` unchanged, the batch's `Make` classes invalid. An exception the callback lets escape propagates | Both reviews' tests require a normal return; atomicity holds either way | D30, §3.1, §4.5, §6.6.1, §6.13 |
| N14 | D47, §6.12: `Scope` members enter the guard | Every `ScopeWriter` call also enters the scope's guard and checks `EngineCallMarker`. Any call that throws, a refusal on another thread included, rejects the whole batch; later calls are still checked, but nothing commits | A writer used from another thread would otherwise interleave with the batch's thread | D47, §3.1, §6.1, §6.6.1, §6.12 |
| N15 | §6.6.1: silent on calls to the scope during its own `Write` | While a `Write` batch is open, the scope's own members, reads and queries included, throw `InvalidOperationException`; the batch's calls go through its `ScopeWriter`. N88 extends the refusal to writes and runs of the scope's writable ancestors | A read would see contents the batch is about to change, and a nested batch would interleave with it | D30, §3.1, §4.10, §6.6.1, §6.13 |
| N16 | §4.5, §6.6.1: an empty batch returns without committing | An all-holding batch still commits its origin, with no flush and no clock tick | The classes its `Make` calls returned (existing ones) stay valid (D39) | §4.5, §6.6.1 |
| N17 | §6.6.2: `C.Of(k).Is(x)` is a row `C(k ++ [Find(x)])` | A new key becomes a pending term whose class is `x` (no fresh id, no union); a key the batch already plans as a term is unioned with `x`; an existing key with another class is a row that the union-id merge unions (a merge, checked by N6) | One row and no union in the common case | §6.6.2 |
| N18 | §6.3 step 4: each round places "every step whose inputs are bound" | A step is placed when its inputs were bound when the round began; registers a round binds count from the next round | The placement does not depend on the declaration order within a round | §6.3 step 4 |
| N19 | §6.1: every graph and scope entry point checks `EngineCallMarker` | The identity members (`Graph`, `IsRoot`, `IsFaulted`, `Failure`, `IsDisposed`, `Version`, `Path`, `ToString`) and `Dispose` do not | They only read and enter no guard; `Dispose` never throws (D40) | §6.1 |
| N20 | §3.1, §6.5.3: silent on a ruleset of another graph | `Run` refuses one (`ArgumentException`) before any work | Its rules' variants belong to another engine | §3.1, §6.5.3, §6.13 |
| N21 | §4.1: the `EngineScope` members | Adds `TryExtract` and `TryGetCost` | Extraction canonicalizes the class and extracts in one engine call, under the engine lock | §4.1, §6.6.5, §6.10 |
| N22 | §4: the internal types | Adds `ProgramBuilder` (the state of one compilation) and the untyped handles `UntypedRelation`, `UntypedFunction` and `UntypedConstructor` (D13). `RuleDraft.Variables` is typed `ColumnType?` (null for an unregistered type, which compilation reports). `TableHandle`'s public properties have internal setters. `Scope.TryRelease` exists and is a no-op in R1 | Implementation structure; no behaviour changes | §4.3, §4.4, §4.10 |
| N23 | §8.1.1: a helper `Ground(EGraph)` | `PrototypeTheory.Ground()` is an instance method | One theory instance per graph | §8.1.1 |
| N24 | §8.3, §9: the test project's settings | `Eggnog.Tests` suppresses xUnit1051, which asks every call with an optional `CancellationToken` (`Scope.Run` included) to pass the test's token. `PublicSurfaceTests` assembles the spike's name from parts, so R1a's text search finds none. The review test `TryGetExistingRejectsANullKeyAsGetDoes` is kept as written (N1). Two review tests were rewritten: `ApiReviewTests.AScopeWriterCallFromAnotherThreadIsRefused` used `Task.Run(…).GetAwaiter().GetResult()`, which can run the task inline on the waiting thread, which holds the guard; it now uses a dedicated thread and asserts that the thread differs from the caller and that nothing was written. `ApiReviewTests.AWriteFilledFromSeveralThreadsFailsFast` used `Parallel.For`, which can run every iteration on the calling thread; it now calls from the batch's thread and seven dedicated threads, with the same assertions | Both tests failed whenever the thread pool was busy (the second on net8.0 in full-suite runs) | §8.3, §9 R1a |

### 15.4 R1b: queries, limits, diagnostics

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N25 | §4.5, §6.6.4: `QueryRunner.Run(scope, query, stopAtFirst)` returns sorted bindings; the pseudo-code sorts every call | `Run(scope, query, stopAtFirst, sort)` returns a pooled `TupleBuffer` the caller disposes; only `ForEachMatch` sorts | Nothing can observe the order of `Any` and `Count`; no allocation per match (conventions) | §4.5, §6.6.4 |
| N26 | §4.1: `TupleBuffer` with `SortByContent()`, lexicographic over cells | `TupleBuffer : IDisposable`, every array rented from `ArrayPool<T>.Shared`. Adds `Cells` (for canonicalizing in place) and `Deduplicate`. The raw-cell sort is `SortByCells`, and `SortByContent(columns)` sorts in content order (§6.1). A new `TupleOrder` struct compares tuples by number | No allocation per match; content order for `ForEachMatch` and `Rows` | §4.1 |
| N27 | §4.1: the `EngineScope` members | Adds `CanonicalizeColumns(cells, width, columns)`, implemented in `RootEngineScope` | Maps a query's sort columns through `Find` under one hold of the engine lock, not one per cell | §4.1, §6.6.4, §6.6.5 |
| N28 | §4.6: `Collect(rulesets, splits)` | `Collect(rulesets, conditions)` also collects the `Until` facts; R3 adds the split rules | `Run` validates every condition before any work (N32) | §4.6, §6.5.3 |
| N29 | §4.5: `TermReader` has `TryRead` and `Check` | Adds `Validate(scope?, fact)`: why a fact is not a readable ground host check, or null | Shared by `Schedule.Until` (without a scope) and `Run` (with its scope) | §4.5 |
| N30 | §4.4: the `CompiledQuery` members | Adds `SortColumns`, `Columns`, `Width`, `Serial`, `ColumnOf`, `IsDisposed` and `Dispose` | Canonicalization, decoding, and the disposal of N38 | §4.4 |
| N31 | §3.5, §6.6.4: `QueryMatch.Get` decodes a class canonical in the queried scope | A class is canonical when it is read: one engine `Find` per class read. Callbacks may write (`QueryTests.ACallbackMayCallTheScope`), so after a union an earlier callback made, two delivered matches may name one class. The matches are still collected and ordered before the first callback | The reviewers' other fix, refusing writes from `ForEachMatch` callbacks, was not taken: writing from a callback is allowed and tested, and reading canonically keeps every delivered class correct | §3.1 `ForEachMatch`, §3.5, §6.6.4 |
| N32 | §3.6, §6.5.2: `Until` "runs body unless every fact of condition already holds" | `Schedule.Until` throws `ArgumentException` for no fact, a filter, or a term with a variable or a computation. `Scope.Run` refuses, before any work, another graph's table or column type, `default(ClassId)`, and a class the scope cannot use (D39). An earlier choice, that no facts means the condition holds, was withdrawn | A forgotten condition would make the step a silent no-op; a condition that failed only when its node ran would leave the earlier steps' writes behind and lose the report | D24, §3.6, §6.5.2, §6.5.3, §6.13 |
| N33 | D42, §6.5.4: limits are checked before every iteration | As specified: `RowLimit` is checked before every iteration, the first included, against the root's `TotalSizeEstimate` (`R1bApiCoverageTests.TheRowLimitIsCheckedBeforeTheFirstIteration`). An earlier report said "from the second iteration on"; the report was wrong, not the code | — | §3.6, §6.5.4 (wording only) |
| N34 | §3.9: `RunStep.Rules` are "the rules that matched in it"; `RuleStatistics.Plan` is "the rule's query plan" | `RunStep.Rules` is a read-only copy of the rules that matched in the step, each with that iteration's figures and `IterationsWithMatches = 1`. `RuleStatistics.Plan` holds the plan from the latest iteration that ran the rule | A rule's plan can change when it is re-lowered (§6.4.5) | §3.9, §6.11 |
| N35 | §8.1.1, §10: every extracted string matches egglog's, the per-kind 07 term included | `ParkAndMapSpeltPerKindLowerTheParkId` keeps Eggnog's equal-cost spelling (cost 15, D10's tie-break), which commutes the second conjunction, and asserts that egglog's spelling is in the same class | The host made the `parkId` comparison's class before the `map` equality's, so the lower class id puts it first; egglog breaks the tie by scan order | §8.1.1, §8.1.2, §10 step 5 |
| N36 | §6.2: query names are unique among queries | Unique among live queries; `Dispose` releases the name | Per-request queries (D49) can reuse a name | D49, §3.1, §3.5, §6.2, §6.13 |
| N37 | §6.3 step 3: a query's class constants are checked at each call | A class constant that is `default(ClassId)` or of another graph is a `RuleDefinitionException` at `DefineQuery`; the origin checks run at each call | Those two errors do not depend on the scope, and failing at definition is earlier | §3.1, §6.3 step 3, §6.6.4 |
| N38 | §3.5, §8.3: `Dispose` frees once no call uses it; "disposal during a call on another thread waits for it" | `Query.Dispose` never waits. When it cannot free at once (an engine call holds the R1 engine lock, or the call comes from a user delegate the engine invoked), it hands the variants to the graph (`EGraph.DeferFree`), which frees them before it next prepares an iteration or a query | A disposal from a user delegate runs inside an engine call, where freeing the rule is unsafe; one during another thread's run would otherwise block until the run ended | D49, §3.5, §4.4, §4.10, §6.4.6, §6.6.5, §8.3 |
| N39 | §3.9: `RunStepKind` lists `Iteration`, `MirrorSync`, `SplitStep` and `Lift`; `RunStep.CasesOpened` | R1b ships `RunStepKind.Iteration` only, and no `CasesOpened` | As the milestone tags say; noted because the public listing (N42) fixes the surface | — |
| N40 | §3.6, §6.11: the observer "may read the scopes of the run" | It may read, query, count and list rows, and extract; writes and `Run` throw `InvalidOperationException("Not allowed inside a run observer")` | Says which calls count as reads | §3.6, §6.11, §8.3 |
| N41 | §3.6: limits unvalidated | A negative `IterationLimit`, `TimeLimit` or `RowLimit` throws `ArgumentOutOfRangeException` from `Run`, before any work | A negative limit is a caller error, not "stop at once" | §3.6, §6.5.3, §6.13 |
| N42 | §3.5, §3.9, §4.6, §8.2, §8.3: renderings, the run's clock, the surface test, the generator | `RunStep` renders `#3 iteration root changed: lower uuid 1, and commutes 4` (`unchanged` otherwise), `Query` `(query (body…) (variables…))`, `Until` `(until body fact…)`. `ScheduleExecutor` owns the run's `Stopwatch` (`Clock`). `PublicSurfaceTests` also compares the public surface with the embedded listing `Surface/PublicSurface.txt` that `SurfaceListing` renders. The differential generator goes beyond §8.2: lets checked against constants (a checked computation is never repeated as a let) and queries defined after the first command (`DiffCommandKind.DefineQuery`), both added on review, which changes the programs each seed makes; and, as before, no repeated body application, no equated key variables, every later atom joining on a variable, and a stop after a run that leaves more than 2500 reference rows. Run reports, the rows after host writes, and query answers are compared too | Readable diagnostics, a fixed public surface, and a generator that reaches more of §6.3 while every program stays valid | §3.5, §3.9, §4.6, §8.2, §8.3 |

### 15.5 Deferred

| Item | Milestone | Notes |
|---|---|---|
| Merge R1b to main, E12 included, with the 5000-seed figures of §15.1 in the commit message | Done: `80675d8`, merged at `3b9dda1` | The commit message says the 5000-seed run was made; its figures are in §15.1 and the `RuleLayerDifferentialTests` class remarks |
| Merge R2 to main | Done: merged with colors M10.4 | Its notes were renumbered N60–N89 at the merge, after colors M10.4's N51–N59 (§15.8) |
| Full net8.0 runs of `Eggnog.Relations.Tests` and `Eggnog.Storage.Tests`, which §9's baseline requires | Done at the R2 merge | Both pass on net10.0 and net8.0 with R2 and colors M10.4 (11,811 tests, 10 opt-in and skipped; 816 tests) |
| List `Eggnog.Tests` among the `InternalsVisibleTo` projects in `docs/engine/conventions.md` | Done at the R2 merge | Open since R1a; `Eggnog.Relations.csproj` and `Eggnog.csproj` already declared it |
| Confirm the `Eggnog.Tests` CI matrix entry on a CI run | The next CI run of main | The entry is in `.github/workflows/Eggnog.yml` on main since `edd1fa9`. It cannot run locally; the equivalent local builds and test runs pass on both TFMs |
| A "no result" outcome for user primitives (a partial-primitive API) | Unscheduled; additive (D28) when a consumer needs one | Only the built-in `+` and `-` are partial (N9); a user primitive that throws fails the rule |
| A runtime check for class constants equated in a body | Done in R2 (N63, N86): every comparison that is not already true where the rule's constants come from is an `Equal` step; class constants are never refused | §11 risk 14 (N8) |
| The R1 adapter's new members on contexts: `CanonicalizeColumns`, `TryExtract`, `TryGetCost`, `ForEach` for the merge check, and the deferred free of disposed queries | Done in R2 | `ContextEngineScope` (§6.6.6); N8 compares a local rule's constants in its owner; N6's merge check reads the scope's view, inherited rows included |
| `RunStepKind.MirrorSync` | Done in R2 | — |
| Engine request E8 and §13 items 8–9 (`CaseSet`, `SplitDriver` and the split hooks) | Done in colors M10.4 (§15.7) | R3 calls them (§9 R3) |
| `RunStepKind.SplitStep` and `Lift`; `RunStep.CasesOpened`; `EngineScope.IsStopped`, `ScopeContradictedException` and the D45 answers; the split and lift steps of `DiagnosticsTests`; `ScopeTagTests`' refusals of another graph's tag by `DefineSplit` and `CaseSet.Add`; `DisposeTests`' "an open `CaseSet` defers its parent's release"; `ClassIdTests`' "a case's class is refused after the run that made the case"; the scoped Cosmos R3 tests (02 `TwoKindsNamedTogetherLowerTheReference`, `TheSameSplitDrivenByTheHost`, `AQueryThatDoesNotMentionTheDiscriminatorDoesNotSplit`; 04 `AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains`; 07 `ParkAndMapWrittenInLowerTheParkId`, `ParkAndMapSpeltPerKindLowerTheParkId`, `TheOfflineRefreshLowersEverything`; `SweepLinks.Compile` asserting 3 schema cases) and `CosmosTheory.Cases.cs` | R3 | As tagged in §3.9, §6.6.8 and §8 |
| A rule-layer benchmark of R2's paths: queries on a frozen scope, transient children, mirror syncs, the `Equal` step's cost per match (N86), the catch-up each call makes first (N87), and the catch-up of each `ScopeWriter` call (N88) | No milestone | The benchmark project references only the engine, which R2 did not change; its Release build is clean |
| Timed gates: R2's per-iteration cost at the root (the call and engine-call counters, the deferred-free check, the local-rule path walk, the late-rule tag check of N67) and per write or run (N88's read of the open-batch count) | No milestone: an idle-machine run | R2 built the benchmarks in Release only and timed nothing. The colors gates are listed in colors §21.11 |
| A benchmark dry run after R1a's last engine fix | Done with colors M10.3 | That fix only removed an uncalled member (N3). Colors M10.3 dry-ran the benchmark project after it (colors §21.9) |

### 15.6 Engine requests delivered by colors M10.3

Colors M10.3 put the bridge `EGraph` on a `Store` and added `EGraphContext`, per-context rules, concurrent children and
extraction over a context. The colors spec records the whole milestone in its §21.9; these notes cover what this layer
asked for. The R1 library (`RootEngineScope`) ran unchanged on the store-backed root, and every `Eggnog.Tests` test
passed; one test changed with E5 (N44). R2 then deleted `RootEngineScope` (§15.8).

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N43 | E4: `DesignateContradiction` must be called before any child exists | Refused once the store has ever had a child, not only while one exists; the check and the store's designation run under the store's definition lock, atomically with a first `CreateChild` on another thread. The early stop (`StopOnContradictionFunction`) is appended to a rule's head inserts into the function when the rule is planned, so designate before planning, as the rule layer does at graph construction | A child created before the designation would have missed its creation-time check (colors §6.9 item 3), and the first form could interleave with a concurrent first child (found in review) | §5.2 E4; colors §10.1, §10.6 |
| N44 | E5: `Extractor.CreateOnDemand(schema, context, …)`, staleness on the extraction tables and the union-find of the context and its non-frozen ancestors | As asked, plus `Extractor.Context`. Staleness is keyed on the extractor's slots (the schema functions usable under its options), not every declared function; queries do not require the context clean again, only creation does; a compaction counts as a change; an empty layer a no-op write created marks as an absent one. Context-form queries enter the context's domain and mark it as running a caller's code, so a cost function may read the context but not write it; the root forms hold nothing. `ComputeCostsFromRootSorts` and `FunctionToDag` stay root-only. `Eggnog.Tests` `ExtractionTests.AStaleExtractorThrowsAfterAChangingWrite` now asserts that a write to a relation outside the extraction schema keeps an extractor current (D49) and that a term write makes it stale | The layered form of the per-slot versions, never the clock (D49) | §5.2 E5; colors §10.10 |
| N45 | E7: `CreateChild`, `Dispose`, `IsFaulted`, `Parent`, `OwnRowCount`; `RunRules` at the graph's `ReportLevel` | As asked. `CreateChild` of a context that is not frozen flushes and ensures it clean first, then takes a failure that flush, the rebuild or the root's settle recorded (or one pending): the new child is disposed and `PanicException` is thrown, and the parent stays usable. `EGraphContext.Parallelism` exposes a context's heuristics | A child starts from what the parent holds; a failure is observed by the operation that records it | §5.2 E7; colors §10.1 |
| N46 | E9: `ParallelHeuristics` on the store-backed `EGraph` constructor | `EGraph(ParallelHeuristics)` builds the `Store` with them (`EGraph()` passes `ParallelHeuristics.Default`); `EGraph.ParallelismBudget` has the root's degree of parallelism, which `ConcurrentChildren` leases from | D46 | §5.2 E9; colors §10.1, §10.9 |
| N47 | E10: `Freeze(planRules)` plans exactly the given rules | As asked. The freeze flushes only when host writes are staged, and throws a recorded failure as `PanicException`, leaving the context unfrozen | An empty flush would spend an epoch; the storage freeze merges raw pending writes | §5.2 E10; colors §10.8 |
| N48 | §13 item 4: a factory (or setter) for `ContextLocalSlot<T>` | `EGraph.NewLocalSlot<T>(Func<EGraphContext, T> factory)`; `GetLocal` builds a context's value on first use and installs it under the context's locals gate, so racing first calls keep one value | `ScopeRuntime` needs its context to be built | §13 item 4; colors §10.1 |
| N49 | §5.1: the root keeps egglog's semantics | The egglog-compatible root API that R1 uses keeps M8's behaviour: `EGraph.RunRules` does not rebuild first, `EGraph.FlushUpdates` leaves a recorded failure to the next run, and `AddValues` and `AddTerm` stage straight into database buffers. The `EGraphContext` API canonicalizes host ids at every level, the root included (invariant C), ensures clean before a run and throws failures itself. Progress after a failed run is not committed (X18), and an iteration finishes its bookkeeping before it throws (colors map §8.1 row 22) | R2's `ContextEngineScope` uses the context API; R1's `RootEngineScope` the root API, which M8's tests pin | colors §10.1, §10.3, §10.4 |
| N50 | §5.1: `RuleInfo.CachedPlan`, `BridgeQuery.BuildCachedPlan` | `RuleInfo.GetOrBuildCachedPlan(Database)` plans a rule once, under a per-rule gate, on the statistics of the context that first runs it; `CachedPlan` is getter-only. `RootEngineScope` calls it | Rules are shared by every context and may first run on any thread (colors §10.2) | colors §10.2 |

### 15.7 Engine request delivered by colors M10.4, and the R1 change it made

Colors M10.4 added the lift primitives and split hooks (colors §11). The colors spec records the whole milestone in its
§21.10; these notes cover E8 and §13 items 8–9, and one change M10.4 made to R1's `RootEngineScope` (N59), which R2 then deleted
(§15.8). Every member E8 lists exists with the shape it gives; the notes record what the engine adds or decides where E8 is silent. No
library code calls the hooks yet (R3).

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N51 | E8: `CaseSet { CaseSet(parent); AddCase(ContextOptions?); Cases; Lift(spec, options, ct); DisposeVacuous(); Dispose(); }` | As asked, plus `RunEach(schedule, depth, allowSplits)` (the driver's phase A), `VacuousCount`, `IsDisposed` and `Parent`. `AddCase` refuses options without `IsCase` and `StopOnContradiction` (`ArgumentException`), as `SplitOptions.CaseOptions` does; the constructor refuses a frozen parent. A lift over a set whose cases were all disposed as vacuous contradicts the parent (L4), as a lift over the same cases before they were disposed does: the set keeps a vacuous case's contradiction output when it disposes it, for a contradiction function whose output is a base value with no constant default; with no case and none found vacuous it throws `InvalidOperationException`, as §6.9.4 expects | A vacuous case must stop to be dropped; a frozen parent cannot be lifted into | §5.2 E8; colors §11.2 |
| N52 | E8: `Lift` checks the token between rounds | As asked: before every round, the first included | — | colors §11.2 |
| N53 | §6.9.4: `CaseSet.Lift(options, ct)` passes the spec and options on every call | The lift memo is keyed on the spec (by reference, then by content), so `LiftSpecBuilder.Build`'s rebuilt specs still hit it when nothing changed; a lift whose answer could depend on the limits (cut short by `RoundLimit`, or whose last round skipped rows over `CandidateLimit`) leaves no memo, so a retry with a larger limit runs | A memo on versions alone skipped a lift of a wider spec, or a retry with a larger `CandidateLimit` (found in review) | colors §11.2 |
| N54 | §6.9.4: `Dispose` disposes the engine set, and so every case | As asked. A case disposed by someone other than its set makes the set's `RunEach`, `Lift` and `DisposeVacuous` throw `InvalidOperationException`: only vacuous cases may be dropped, or the rest no longer cover the parent. The rule layer disposes cases only through the set (§6.9.4, `DisposeVacuous`) | The first form dropped such a case silently and lifted what held in the rest (found in review) | colors §11.2 |
| N55 | §13 item 8: `LiftTableKind.Relation` needs no change, since relations lift as `Function` with a unit output | `Relation` writes the output every live case holds, only where they agree, and refuses an id output and a case join (`ArgumentException`); a case join is refused on any kind but `Function`, and on an id output. §6.9.1's spec, with relations as `Function` and a null join, is unaffected | The first form wrote the seed's output unchecked, which was unsound for any output but unit (found in review) | colors §11.2 |
| N56 | E8: `Step` semantics; §6.9.3: the driver keeps records until the run ends, and "the cases take in the parent's new facts" | `Step` runs and lifts every live record of the scope in each step, not only those it opens, so what the cases derive from facts the scope learned after the split is lifted, and a case those facts exclude goes vacuous and is dropped (D43). `Step` returns true only when it opened cases or a lift changed the scope, so `SplitSchedule.Execute`'s `changed` is false once the split step has nothing left to do and `Saturate` ends. It does not hold the scope's domain while it calls sources, the demand or schedules; exceptions propagate and the records stay for `DisposeRecords` | The first form revisited nothing once every request was covered (found in review) | colors §11.3 |
| N57 | E8: `Step` | `Step` refuses a frozen or faulted scope (`InvalidOperationException`; §6.9.4 refuses a frozen scope before it), returns false for a stopped or contradictory scope (§6.9.3 checks `IsContradictory` first) or at `DepthLimit`, and skips a request with fewer than one case and a request whose key an earlier request of the same step took (counted in `SplitsSkipped`) | §6.9.2 already skips empty sets in the source; the engine checks again | colors §11.3 |
| N58 | §6.9.3: one cached `SplitDriver` per split node; colors §11.3 step 6, "every live record of the scope", and B6 over `SplitKey` | Each record names the driver that opened it; a step runs and lifts only its own driver's records, and B6 compares the source object by reference and the key cells. Two split nodes over one scope keep their in-case schedules, lift specs and reports apart, and a node's split is not skipped because another node's source at the same index split the same values | The revisited records did not say which driver opened them, and `SplitKey.Source` is an index into one driver's list (found in verification) | colors §11.3 |
| N59 | §5.1, §6.6.5: R1's host batches create their state with `Database.CreateExecutionState(engine.NextTimestamp)` | `RootEngineScope` creates it with the internal `Database.CreateEngineState(engine.NextTimestamp)`: the same state, stamped with the store clock, without the DEBUG mark of a raw write. The library's public API and every `Eggnog.Tests` test are unchanged | Colors M10.4 turned a re-stamped catch-up copy into a DEBUG assertion (colors §19 item 9), from which stores written through the raw execution-state API are exempt, since their rows carry whatever stamps the caller gives them. The library stamps every row with the clock, so its stores keep the assertion (found in verification) | §5.1, §6.6.5, §9 R1a; colors §19 item 9 |

### 15.8 R2: scopes, tags, local rules, concurrency

R2 was built on colors M10.3, and merged with colors M10.4, with no engine change: it changes no file under
`src/Eggnog.Relations`. Its notes were N51–N80 on its branch, written while colors M10.4 numbered its own N51–N59;
they are N60–N89 here. Every
`EngineScope` member maps onto one `EGraphContext` member for every scope, the root included (`ContextEngineScope`);
`RootEngineScope`, the engine lock, the re-lowering of §6.4.5 and `Rules/RootRelowerTests` are deleted, and each rule
variant is lowered once with its constants as written (X19 and I30 apply them per context). The public types only
gained members (`Surface/PublicSurface.txt` has additions only), and no file of `src/Eggnog` imports
`Eggnog.Relations`. Five reviews (soundness, API, an oracle review that planted 53 mutants, concurrency, completeness)
added tests; the fixes they called for are N60 and N62–N73. Three verification rounds followed: the first's fix is
N86, the second's N87, the third's N88. N89 records the internal members §4 now lists.

| # | The spec said | What was built | Why | Spec now |
|---|---|---|---|---|
| N60 | §6.7 Freeze: freeze the engine, then give each live child subtree a new guard; "no descendant is in use" | The scope reads as frozen from the engine freeze on, so another thread may create a child (with a guard of its own) and use it before the reassignment. Freeze reassigns only the children that share its guard, and every entry reads the guard, enters it and reads it again (`Scope.EnterGuard`), moving to the new guard if a freeze reassigned it meanwhile; `TryRelease` does the same | Reassigning a guard a call holds let a third thread into the domain and made the call's exit throw (`R2SoundnessProbeTests.P11`, `R2ConcurrencyReviewTests`) | §6.7, §6.12 |
| N61 | §6.8: a sync is skipped when `S.MirrorSyncedVersions[t] == S.Version` | The key is `(S.Engine.ViewStamp, the tag's copy-rule count)`. The view stamp sums the change stamps of `S` and its ancestors | A host write into a writable ancestor changes `S`'s view without moving `S.Version` (found by `ScopeDifferentialTests`, seed 219); a mirror created after the tag's last sync must be synced before its first read (`MirrorSyncTests`) | §4.10, §6.6.6, §6.8, §7.1 M |
| N62 | D16, D49, §6.6.4: a query moves no `Version`; D48: a query syncs the mirrors it reads | `Scope.Version` is the engine's stamp less the stamp moves of the scope's mirror syncs (`Scope.ExcludeMirrorSync`); a sync never moves it, before a query, an iteration or a freeze | Mirror tables are internal, and not extraction tables, so a sync changes nothing a caller sees; both rules hold. `ScopedReadTests.AFrozenTaggedScopeIsNeverSynced` and `R2CompletenessReviewTests.P11` asserted that a sync moves `Version` and were corrected | §3.1 `Version`, §6.6.4, §6.6.6, §6.8 |
| N63 | §6.3: `c1.Is(c2)` of class constants compared in the root under the engine lock (N8) | Compared at definition where it can be read now: a local rule in its owner, a global rule in the root when frozen or when this thread can take its guard. Otherwise the rule keeps the first constant and gains an `Equal` step (`ProgramStepKind.Equal`, the graph's `EqualFunction`), whose Id-constant operands the engine canonicalizes in the running context (X19). A query always compares at run time. N86 then removed the refusal of different classes: the comparison at definition only drops a constant already of one class | R2 removed the engine lock: reading the writable root from a definition on another thread raised the engine's domain error and could abort the run (D6, D47); a query whose classes come from a child was refused although they were one class where it ran (`R2CompletenessReviewTests.P02`–`P04`). `R2SoundnessProbeTests.P10` expected "never matches" for a global rule defined during a run; it now asserts the rule is accepted and never matches | §4.4, §6.3 step 3, §15.5 |
| N64 | §6.3 step 2: the first scoped read creates the mirror during normalization | Atoms keep their source table until every other check passed; then `MirrorRegistry.GetOrCreateAll` checks the frozen counts of every new mirror and creates them while holding the writable root's guard | A refused definition left a mirror, its copy rule and a first read behind (`R2SoundnessProbeTests.P06`, `P07`) | §6.3 step 2 |
| N65 | §6.3: a tag of another graph passed to `Fact.In` is an `ArgumentException` at that call | `Fact.In` compares the tag with the tables inside it; the builders' `When` and `Then` check every `Fact.In` tag (`EGraph.CheckFactTags`) | `Fact.In(tag, x.Is("a"))` holds no table, so compilation gave a `RuleDefinitionException` | §6.3 |
| N66 | §6.13: an unresolved tag at a query, before any work | `QueryRunner` resolves every tag before it enters the scope, and `MirrorSync.Ensure` resolves every tag before its first sync | The first tag's sync ran before the second tag failed | §6.6.4, §6.8 |
| N67 | §6.5.3 resolves tags at `Run` only; D6 lets a rule join the next iteration | A rule defined during the run whose read tags the iteration's scope cannot resolve is left out of that iteration (`ScheduleExecutor.Resolvable`, allocation-free when every rule resolves) | Failing mid-run would leave earlier iterations' writes and lose the report (`R2ConcurrencyReviewTests`) | §6.5.3, §6.13 |
| N68 | D40: a release runs on whichever thread removes the last obstacle, taking the guard | The release takes the guard under the guard's release lock (`TryEnterForRelease`); `ScopeGuard.Enter`, finding the guard held, waits on the release lock and tries once more, failing only when a call holds it | A `Dispose` on one thread made the domain owner's concurrent call (a `Freeze`, say) fail with "in use on another thread" (`ChildrenDisposedWhileTheirParentFreezesAreAllReleased`, flaky before) | §4.1, §6.7, §6.12 |
| N69 | D49, §6.4.6: free a disposed query's variants unless an engine call holds the R1 engine lock | `EGraph.ActiveEngineCalls`, an interlocked count `ContextEngineScope` keeps around calls that may run user delegates (flush, run, query, freeze); while it is positive, or from a user delegate, the variants go to the graph, which frees them in the next `PrepareRules` or `PrepareVariant` | No engine lock in R2; keeps `DisposalWhileAnotherThreadRunsDoesNotWaitForTheRun` | §4.4, §4.10, §6.4.4, §6.4.6, §6.6.6 |
| N70 | E10: `Freeze` plans every query's default variant | The listed queries are held until the engine froze; one disposed before the list is left out | A query disposed and freed on another thread made the engine refuse the list (`ArgumentException`) | §6.7 |
| N71 | D40: release waits for children and a busy domain | Also for a per-scope count of calls in progress (`BeginCall`/`EndCall`), and, inside an engine call of the scope's domain, for the guard's last exit. `ScopeGuard.Exit` leaves the domain before it takes the deferred list | Frozen-scope reads take no guard, so the count is what keeps a reader's scope alive (`DeferredReleaseTests`); a primitive that disposes a scope of its own domain would make the engine refuse a re-entrant disposal (`DisposeInsideARunTests`) | §6.7 |
| N72 | §6.7: silent on `CreateChild(graph.RootTag)` | `ArgumentException`: the root tag is the root's | `In(RootTag)` would otherwise resolve to a scope other than the root | §3.1, §6.7, §6.13 |
| N73 | §2.4, §6.12: extractors over a frozen scope are thread-safe | One `Extractor` instance serializes its calls on a private lock | Its engine caches are shared; eight threads on one extractor corrupted them (`R2ApiReviewTests`) | §6.12 |
| N74 | §9 R2: the R1 suites pass unchanged in source | `QueryTests.AClassConstantThatLostAUnionIsMatchedCanonically` asserted R1's re-lowered `/k1` variant; it asserts the same matches through the variant lowered at definition | The behaviour is unchanged; the mechanism it named is gone | §9 R2 |
| N75 | §4.1: `ScopeRuntime.Of` finds the context's runtime through `GetLocal` | It follows `state.View.Owner.Owner.Host`, and falls back to the root's runtime when the view has no context of this graph | A child's failure is reported in the child (`R2CompletenessReviewTests.P12`) | §4.1 |
| N76 | §6.8: copy rules in the internal ruleset `$mirror` | Each copy rule's `CompiledRule` carries an internal `Rule` of an unregistered `$mirror` ruleset; its `LoweredRule.Rule` is null, so copy rules never appear in `RunReport.Rules`. Mirror handles have negative unique `Index` values and are published in `TableOf`, not `Tables`; a scoped view handle shares its source's `Index`, `Function` and `Name` and renders as `dom@schema` | `CompiledRule.Rule` is not nullable; the memo keys of `ProgramBuilder` key on `(Index, read scope)` | §6.4.3, §6.8 |
| N77 | §6.11: `CountRows` and `Rows` | They accept a scoped view handle and read the resolved scope; a mirror handle is refused | The spec was silent on views | §3.1, §6.11 |
| N78 | §6.3 step 7 and head facts: no message for an analysis writer's scoped atom or a head `Fact.In` | *"rule writes analysis table 'X' and may not read scoped atoms; it reads 'T@tag'"*; *"head facts are writes; Fact.In('t') is read-only"*; a host write through `Fact.In` or a view is an `ArgumentException` naming it read-only | Messages the spec did not give | §6.3 |
| N79 | §6.7: `Freeze` steps | `Freeze` holds the definition lock from the mirror sync through `AddFrozen`, and refuses a call from inside another call of its domain (an observer gets the observer message) | A first scoped read on another thread could otherwise create a mirror that stays empty in the frozen scope (invariant M); step 4 reassigns guards an outer call holds | §3.1, §6.7, §6.13 |
| N80 | §8.2: the scope differential and the inheritance equivalence | The generator keeps lattice semantics deterministic: a function's value is read only into a fresh variable that heads write into a function merged the same way, with no joins, filters or relation rows on it. The driver uses every body variable (a filter that keeps every value, a disequality of each class variable with a class no row holds), so match counts are distinct full bindings as colors' `ColorSchema.Keep` makes them, and `InheritanceOffEquivalenceTests` asserts each seed's seeded total at most its unseeded one, and the sum below | Matching transient lattice values is order-dependent in egglog too; without the keep steps, four seeds counted more seeded matches than unseeded (`R2CompletenessReviewTests.P09`) | §8.2 |
| N81 | §6.11: an observer may make no structural call | `CreateChild` refuses a call from a run observer; `Freeze` gives the observer message | `CreateChild` entered through the read path, which did not check the observer flag | §3.1, §6.7, §6.11, §6.13 |
| N82 | §6.6.1: a `ScopeWriter` call after its batch ended throws `ObjectDisposedException` | The writer checks that the batch has not ended again once it holds the guard | A call from another thread that waited out the batch could plan into the committed batch | §6.6.1, §6.13 |
| N83 | §3.4: `Fact.In(tag)` with no facts | Allowed: true in a host check, no atom in a rule body, an empty rendering | Unspecified; recorded as built | — |
| N84 | §8.3: the R2 concurrency gates | `ConcurrencyTests`, `QueryTests`' eight-thread frozen query, `ScopeTagTests`, `ContextExtractionTests` and `ScopeTreeTests` run on dedicated threads that meet at a barrier (`Support/DedicatedThreads`) and assert that every thread ran; `TwoGraphsShareNoTagState` (both orders and on eight threads) and `ParallelEquivalenceTests` run every R2 scenario query (`Cosmos/Scoped/ScopedScenarioCases`) | `Parallel.For` may run every iteration on the calling thread when the pool is busy (N24); the spec asks for every scenario | §8.3 |
| N85 | §8.3: the R2 rows | The reviews added `MirrorSyncTests`, `FreezePlanTests`, `LocalRuleScheduleTests`, `RunTagResolutionTests`, `DeferredReleaseTests`, `DisposeInsideARunTests`, `DisposedChildListingTests`, `ScopeDepthTests`, `ExtractedClassOriginTests`, `FrozenScopeOverlapTests`, and the probes under `Review/R2*` | Each pins a behaviour that a planted mutant or a review found unpinned; §8.3 has a row for each class | §8.3 |
| N86 | §6.3 step 3: `c1.Is(c2)` of class constants of different classes refused as *"never matches"* when compared at definition (N8, N63) | Class constants are never refused. The definition-time comparison (in the owner for a local rule; in the root for a global rule, when frozen or when this thread can take its guard) is used only to drop a constant already of one class. Different classes, or a scope that cannot be read now, keep both constants and add the `Equal` step | A global rule runs in every scope a run reaches and a local rule in its owner's descendants (§6.5.2); a child may union classes that differ at the root or in the owner (context = copy), so the refusal was unsound there, and whether a definition was refused depended on whether another thread held the root's guard (D6). A union at the root or in the owner holds in every descendant, so dropping a constant already of one class stays sound. `R2FixVerificationTests.V01`–`V05` pin this; `RuleDefinitionTests.ClassConstantsAreComparedByClassWhenTheRuleIsDefined` asserted the refusal and is now `ClassConstantsAreComparedByClassWhereTheRuleRuns` | §6.3 step 3, §11 risk 14, §15.5 |
| N87 | D16, D49: `Version` moves on every effective change to the scope's own layer, catch-up copies included, and never on a query, a read or a write that changes nothing | `Scope.Version` also leaves out the catch-up each call makes first (`Scope.CatchUp`, used by every read, query, write, run, `CreateChild`, `CreateExtractor` and a child's `Freeze`): each writable scope from the first frozen ancestor or the root down to the called scope is brought up to date outermost first, and the stamp moves of its rebuild are subtracted from its own `Version`. The copies a scope's own union makes over rows already there (in that call's flush or run) still move it. The root is not measured and its catch-up is as before | A descendant catches up with a writable ancestor lazily, so when its own union came first and the ancestor's write later, the copies were made at its next call, even a read or a query, and moved its `Version` there; both halves of D16 could not hold in that order. Leaving that catch-up out keeps every read and query from moving `Version` and makes `Version` count only what the scope's own calls change: an ancestor's write never moves it, as it never did when no copies were needed. An eager catch-up at the ancestor's write was rejected: the ancestor would pay for every descendant (a child costs in proportion to what it touches). Cost: two reads of the change stamp per writable scope on the path, none at the root. `R2FixVerification2Tests.Q05`/`Q06` and `CatchUpVersionTests` pin this | D16, §2.3, §3.1 `Version`, §6.5.3, §6.6.1, §6.6.3, §6.6.4, §6.6.6, §6.10 |
| N88 | N15, §6.6.1, §6.13: silent on calls into other scopes of the thread domain while a `Write` batch is open; N87: the catch-up each call makes first is left out of `Version` | While a batch is open on `S`, every writable ancestor of `S` (they share its guard) refuses writes and runs with *"Scope 'X' cannot be written or run while a scope under it has a Write batch open; write it before or after the batch."*: `Write` counts its batch in each writable ancestor (`Scope.CountOpenBatch`) and `EnterWrite` reads that count. A refused call writes nothing and does not reject the batch. Every other call of the domain is allowed (reads, queries, `CreateChild`, extractors and `DefineRule` on the ancestors; writes, runs and nested batches in descendants and siblings), and each `ScopeWriter` call, and `Write` before it commits, brings `S` up to date first (`Scope.CatchUp`), left out of `Version` as at a call's entry | The batch's calls are checked against `S` as it stands when each is made, ancestors' rows included: which facts hold already, and the conflicts of D32, merges included. An ancestor write between them could make the plan wrong (a function without a lattice given another value would fault `S` at the commit instead of throwing `FactConflictException`), and its catch-up at the commit moved `S.Version` (`R2FixVerification3Tests.V03`), or a later `ScopeWriter` call failed with the engine's "call EnsureClean() first" (`V02`). Catching up per call cannot repair the plan; refusing only the writes that change what the batch read keeps everything else usable. An ancestor's query may still sync its mirrors, which writes only internal tables the batch cannot plan into: the per-call catch-up covers it. Cost: one read of a field per write or run, one increment per writable ancestor per `Write`, and two stamp reads per writable scope on the path per `ScopeWriter` call; none at the root beyond the field read. `R2FixVerification3Tests.V02`/`V03` asserted the ancestor write was allowed and now assert its refusal; `OpenBatchDomainTests` pins the rest | D30, §2.4, §3.1 `Write`, §6.5.3, §6.6.1, §6.12, §6.13, §11 risk 16 |
| N89 | §4.1, §4.4, §4.7, §4.10: the internal members | `EngineScope` adds `IsFaulted` (the context's fault flag, E7), and its `CreateChild` returns a `ContextEngineScope`, which adds `Runtime` and `ViewStamp`. `ScopeGuard` adds `TryEnter`, `TryEnterForRelease`, `ExitAfterRelease` and `Depth`. `RuleVariants.Get` takes no constants. `ProgramStepKind.Equal` and `EqualFunction` are new. `MirrorRegistry.CopyRules` returns an array, and `AllCopyRules` lists every copy rule for a freeze. `EGraph` and `Scope` gain the internal members §4.10 lists (among them `Equal`, `ActiveEngineCalls`, `CheckFactTags`, `CatchUp`, `EnterGuard`, `BeginCall`/`EndCall`, the count of open batches below a scope and the mirror-sync key), and from R2 the bridge root API rows of §5.1 have no caller | Implementation structure that N60–N88 call for; no behaviour beyond theirs | §4.1, §4.4, §4.7, §4.10, §5.1 |
