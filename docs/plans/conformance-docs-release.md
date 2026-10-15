# Conformance, documentation and release follow-ups

This plan covers these follow-ups:

- the ordering that the built-in `Max` and `Min` lattices use for strings;
- running the test suite on net8.0 in CI;
- whether the package declares AOT and trimming compatibility;
- a worked modelling note on hidden discriminators and on numeric bounds that stay sound, with the test fixture
  `PredicateTheory`'s own limits stated in its class remarks;
- the egglog push/pop programs that are still untranslated, and the one kind of coverage the most tractable of them
  (`math.egg`) would add: subsumption and deletion by rules inside a child.

Neither of the owner's two questions falls in this workstream. Interning is answered in
[`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md) and compiled plans in
[`./compiled-plans.md`](./compiled-plans.md). Section 3.5 gives the facts this workstream found about how the library
behaves without dynamic code, which bear on the compiled-plans question.

## 1. Summary and recommendation

**Recommendation: Do now, for five small items. Confidence: medium-high.**

| Item | Verdict | Confidence |
|---|---|---|
| A. Ordinal `Max`/`Min` for strings | **Do now** (a defect, not a documentation item) | high |
| H. Run `Eggnog.Tests` on net8.0 in CI, on linux-x64 (and optionally win-x64) | **Do now**, while the package targets net8.0 (section 9, question 2) | high |
| B-min. Clear the three trim warnings, declare `IsAotCompatible`, make IL2xxx and IL3xxx errors, and add a publish-only NativeAOT check in CI | **Do now** | medium-high |
| B-full. A NativeAOT smoke application with golden output | **Only if the owner names NativeAOT a supported target** (section 9, question 1). No demand for it is recorded | medium |
| C. Documentation: a modelling note on hidden discriminators and sound numeric bounds; the test fixture `PredicateTheory`'s limits in its class remarks | **Do now** (small) | medium-high |
| D. Correct a stale statement in a prototype's README | **Closed**: the prototype and its README are no longer in the repository | high |
| F. Directed tests of subsumption and deletion by rules in a child, and settling the unused `RetractionGuard.CheckSubsume` | **Do now** (small, low priority) | medium-high |
| E. Push/pop programs with eq-sort containers in a child | **Later**: blocked on eq containers in children, which [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md) parks | high |
| G. `math.egg`, and the programs that need `UnstableFn`, `Map`, `MultiSet` or `BigRat` | **Drop**, and take them off the roadmap. `math.egg` stays an owner option | medium-high |

The reasons:

- `ValueLattice.Max<string>()` and `Min<string>()` compare with the current culture. The same serial program then
  gives different answers on different machines: under ICU collation, `Max` of `"z"` and `"ä"` is `"z"` under en-US and
  `"ä"` under sv-SE. The merge is also not commutative on canonically equivalent strings: the precomposed and
  decomposed forms of "é" compare equal under the culture and differ ordinally, so `Verify` throws "violates
  commutativity". Everything else in the library orders strings ordinally.
- The package ships a net8.0 build that CI never runs. One more job closes that, for as long as the package targets
  net8.0.
- The library is one small step from declaring AOT and trimming compatibility: the analyzers report three trim warnings
  on net10.0 (two on net8.0) and no AOT warning. A declaration plus a publish-only check keeps that true at little
  cost. A golden-output smoke application costs more and has no recorded user, so it waits for the owner.
- The documentation item consolidates reasoning that exists today only in the history documents, which by their own
  index do not describe the current code.
- Subsumption and deletion in a child run through the engine's generic merge and layer paths, so a few directed tests
  cover them. One finding needs settling: `RetractionGuard.CheckSubsume` has no caller, although the colored-contexts
  spec says the bridge's subsume action calls it, and an existing test subsumes where that check would refuse.
- The push/pop programs cost more and buy less. The eq-container programs wait on a parked feature. `math.egg` would
  add a real-program oracle for root behaviour that the root bridge tests already cover. The rest need features the
  library will not offer (function values, `Map` type checking) or a `.egg` reader, which is not planned (rule-layer
  decision D14).

## 2. What it is and why it was planned

**Push/pop conformance.** The engine map planned egglog's push/pop programs as the gate for colored queries. Its
milestone table names "egglog push/pop equivalence on `.egg` programs"
([`../history/egglog-engine-map.md`](../history/egglog-engine-map.md), section 6, "Build order"). The colored-contexts
spec implemented it as `EggPushPopTests` and narrowed it: egglog's push is an oracle "only for programs whose rules all
inherit" ([`../history/colored-contexts-spec.md`](../history/colored-contexts-spec.md), section 17, "Milestones", and
section 19, "Points for the decisions owner, and deferred work", point 5). There is no `.egg` reader
([`../history/rule-layer-spec.md`](../history/rule-layer-spec.md), D14), so each program is translated by hand. Section
21.11 ("Still deferred") of the colored-contexts spec lists two groups of programs that are not translated:

- programs that intern eq-sort containers in a child, deferred to "eq containers in children (decision 9)";
- programs that need sorts the bridge lacks, plus `math.egg`. The deferral reads "No milestone: there is no `.egg`
  reader; `math.egg` when a reader exists, or by hand if R2 needs more push/pop coverage". R2 was the rule layer's
  scopes milestone ([`../history/rule-layer-spec.md`](../history/rule-layer-spec.md)); scopes shipped without asking
  for more push/pop coverage.

[`../roadmap.md`](../roadmap.md), "Push/pop conformance", repeats the first three eq-container programs, the
`UnstableFn` and `Map` programs, and `math.egg`. The other web-demo programs that need missing features are listed only
in the remarks of `src/Eggnog.Tests/Relations/Bridge/EggPushPopWebDemoTests.cs`. That class names `herbie.egg`,
`herbie-tutorial.egg` (`BigRat`), `lambda.egg`, `typeinfer.egg`, `fusion.egg` (`Set` containers of e-classes) and
`multiset.egg` (`MultiSet`, `UnstableFn`).

**Documentation.** A worked modelling note is missing, and the roadmap lists it under "Library work". It has two
parts:

- A worked note on how a schema compiler synthesizes a hidden discriminator for a disjunction that has none (JSON
  Schema's `anyOf` or `oneOf` without a property that tells the branches apart). The rule-layer spec encodes `$anyOf/0`
  by hand and does not say how a compiler would produce it.
- Numeric bounds that stay sound, which the test fixture `PredicateTheory` shows by its limits: a bound rule may prove
  a type only for a comparison that holds only between numbers, never for one that first converts a stored value to a
  number, which also matches stored strings; and a bound that steps by one is sound only over integers.

The fixture's own limits, including that its "a domain settles the type" rule is string-only, belong in its class
remarks. They are test code, not a roadmap item.

A related point, that the built-in `Max` and `Min` compare with `CompareTo` while sets compare ordinally, is a defect,
which this plan treats as item A. A point about the rule-layer spec's status lines is moot: the history documents are
archived, and their index says their status lines are those of the time they were written
([`../history/README.md`](../history/README.md)).

**net8.0 in CI and AOT.** No document plans either. The owner raised AOT with the compiled-plans question
(`RuntimeFeature.IsDynamicCodeSupported`, NativeAOT). What CI tests, and whether a package declares `IsAotCompatible`,
are release properties, so they belong here.

## 3. Current state

### 3.1 Push/pop translations, and subsumption and deletion in children

These programs are translated and compared:

- **`EggPushPopTests`** (`src/Eggnog.Tests/Relations/Bridge/EggPushPopTests.cs`): `calc.egg`, blocks 1-6 of
  `tricky-type-checking.egg`, and a program written so that a push inherits progress. Sizes, iteration counts and
  checks are compared with the egglog 2.0.0 CLI.
- **`EggPushPopWebDemoTests`**: `bdd.egg` (also compared with the same commands run at a fresh root), `push-pop.egg`,
  `array.egg` and `cyk.egg`. These are checked against the programs' own `check` and `(fail (check ...))` commands.
- **`EggPushPopProgram`** maps the commands: push is `CreateChild`, pop is `Dispose`, and `run :until` follows egglog's
  `run_schedule`. It pushes under a **writable** parent.

The remaining programs, from the submodule `ext/egglog` (tests and tests/web-demo), and what blocks each:

| Program | Size | What it needs | Blocked by |
|---|---|---|---|
| `container-rebuild.egg` | 79 lines, 3 pushes | `Vec` of an eq sort, interned and rebuilt inside a push | `ContainerEnv.GetOrInsert` throws in a child for a container that holds ids (`src/Eggnog/Relations/Containers/ContainerEnv.cs`, the message "cannot be interned in a child context"); `EqContainerGuard` faults a child whose own displacement is an element of one |
| `container-fail.egg` | 22 lines, 1 push | the same (egglog's regression test for its issue 831) | the same |
| `vec.egg` | 82 lines, 4 pushes | `Vec` of an eq sort, and `UnstableFn` | the same, plus `UnstableFn` |
| web `lambda.egg`, `typeinfer.egg`, `fusion.egg` | 242, 336 and 152 lines; 10, 11 and 10 pushes | `Set` containers of e-classes | the same |
| `python_array_optimize.egg`, `stresstest_large_expr.egg` | 1,767 and 444 lines, 1 push each | `UnstableFn`, `Vec`, `String` and frontend rulesets | no function values; size |
| `tricky-type-checking.egg`, the two issue #113 blocks | 6 lines each | declare `(Map i64 String)` and `(Map i64 i64)` sorts and let `(map-insert (map-empty) 1 "one")` | nothing at the engine level: the blocks test egglog's type checker choosing between two `Map` sorts. They have no check, so there is nothing to compare |
| web `multiset.egg` | 210 lines, 8 pushes | `MultiSet`, `UnstableFn` | no function values |
| web `herbie.egg`, `herbie-tutorial.egg` | 570 lines, 196 rules, 14 pushes; 143 lines, 4 pushes | `BigRat` values and their primitives | no `BigRat` codec. The tutorial's oracle is 3 `check` commands; its 7 `extract` commands print values that egglog's suite does not assert |
| web `math.egg` | 337 lines, 73 rule and rewrite forms, 21 pushes | `f64` and strings (`DoubleCodec`, `StringCodec` exist); `subsume` (11 prune rules) and a rule-action `delete` (`is-not-zero`); `run :until` inside `saturate` | not translated. Its `delete` rule runs inside a push, and a child may delete from a shared table only under a frozen parent (`RetractionGuard.Check`, `src/Eggnog/Relations/Layers/RetractionGuard.cs`, the message "a child context retracts a shared table's rows only under a frozen parent"; colored-contexts spec section 6.7, refinement R1). A translation would have to freeze the root before the first push. That is legal, because nothing in `math.egg` runs at the root after line 139 except push blocks. Each push then starts from a root that has run nothing, as with `bdd.egg` |

The tests already contain container types that a translation can reuse: `src/Eggnog.Tests/Relations/Containers/VecContainer.cs`,
`SetContainer.cs` and `src/Eggnog.Tests/Relations/Extraction/MapContainer.cs`. Nothing implements `UnstableFn`,
`MultiSet` or `BigRat`.

**Subsumption and deletion in children.** Neither is reachable from the public API:
[`../architecture.md`](../architecture.md), section 2.13, "Differences from egglog", point 1, "No deletion and no subsumption
in the API", and `src/Eggnog.Tests/Surface/PublicSurface.txt` has no subsume or delete member. They matter only to the
engine and its bridge, which the push/pop translations use.

How the engine handles them:

- Subsumption is a column of the function table, not a separate mechanism. `FunctionTableMerge`
  (`src/Eggnog/Relations/Bridge/FunctionTableMerge.cs`) merges it like an output: it keeps the larger cell, so
  subsumption is sticky. Rule atoms filter on it as a constant entry (`EGraphRuleBuilder.QueryTable`,
  `src/Eggnog/Relations/Bridge/EGraphRuleBuilder.cs`). The rule action `EGraphRuleBuilder.Subsume` lowers to a lookup
  and an insert of a subsumed copy (`RuleAction.LowerSubsume`); the host action `TableAction.Subsume` stages an insert.
  In a child, the subsumed copy reaches the child's layer through the same shadowing write as any value change.
- A rule-action delete (`RemoveInstr`) and a staged removal (`ExecutionState.StageRemove`) consult the retraction guard.
  `RetractionTests.EveryEnforcementPointConsultsTheGuard` (`src/Eggnog.Tests/Relations/Layers/RetractionTests.cs`)
  checks that a child under a writable parent is refused and that a child under a frozen parent may delete.
- **`RetractionGuard.CheckSubsume` has no caller.** The colored-contexts spec, section 6.7, gives it a column of the
  guard's table (refused at a root with live descendants and in a child with live descendants) and says "The bridge's
  subsume action calls `CheckSubsume`". Neither subsume path calls it; only `RetractionTests` calls it directly. It has
  had no caller since it was added in `2e130f7`. The code's behaviour contradicts the spec's table, and a test relies on
  the code: `ContextExtractionTests.ARelevantWriteOrUnionInANonFrozenAncestorMakesItStale`
  (`src/Eggnog.Tests/Relations/Extraction/ContextExtractionTests.cs`) subsumes at a writable root while a child and a
  grandchild are live, and passes. The spec's other claim about subsumption, that the guard marks the layer
  (`TableLayer.HasRetractions`) when it allows a subsume, is also not true of the code.

Tests that subsume in a child today: `RetractionTests` (the guard alone), `ContextExtractionTests` ("rows a child
subsumes or deletes under a frozen parent are not extracted", through `TableAction`), and
`LifterTests.LiftedRowsAreNotSubsumed` (`src/Eggnog.Tests/Relations/Cases/LifterTests.cs`), where each case runs a
subsuming rule and the test asserts the case's row is subsumed and the lifted row is not. The context rule differential
(`src/Eggnog.Tests/Relations/Colors/`) generates the rule actions `Set`, `Union`, `Mint` and `Read`
(`ColorActionKind.cs`) and host-level `Delete` and `Clear` commands (`ColorCommandKind.cs`), but no rule-action subsume
or delete. No test runs a subsuming rule in a child and then checks what later rules match.

### 3.2 Documentation

The hidden-discriminator encoding is written down only in the history documents and in the test fixture's code:

- the rule-layer spec, section 11, residual risk 11: split demand trades completeness for cost, and its remedy, a
  `governs(k, p)` fact per branch path, with the query split guarded on "mentions k, or mentions a path k governs";
- the rule-layer spec, D43;
- the test fixture `PredicateTheory` (`src/Eggnog.Tests/Predicates`), whose query cases encode `$anyOf/0` by hand: a
  synthesized key with presence, a domain of branch indices, marked as a discriminator, and branch rules guarded on the
  key's presence and on its domain narrowed to one index. That case (`ScopedQueryCases`) runs without a split, so it
  shows only that the branches never meet without one. The split over the key is run by
  `SplitExtractionTests.ASplitOnAHiddenDiscriminatorJoinsTheBranchDomains` (`src/Eggnog.Tests/Cases`), which compiles
  the schema with `PredicateTheory.Facts` and checks what the split lifts: the union of the branch domains, and neither
  branch's own field. A query that mentions a path the key governs, but not the key, opens no case on it.

[`../architecture.md`](../architecture.md), section 2.9, describes split rules and the coverage obligation, but not this
pattern or the `governs` remedy. No test uses `governs`. The history index
([`../history/README.md`](../history/README.md)) says those documents "do not describe the current code".

The limits of the test fixture, from `PredicateTheory` (`src/Eggnog.Tests/Predicates`):

- "a numeric lower bound" and "a numeric upper bound" (`DefineHorn`) derive `types(p) = {number}` from
  `NumGt(p, n)` and `NumLt(p, n)`. This is right for a comparison that holds only between numbers, and wrong for a
  comparison that first converts the stored value to a number, which also matches a stored `"30"`.
- The same rules step by one (`lo = n + 1`, `hi = n - 1`) over `long`. That is sound only when the path holds
  integers: a stored 2.5 satisfies `> 2` but not `>= 3`. A bound with an open flag over `decimal` avoids this; item 7
  of [`./rule-layer-split-features.md`](./rule-layer-split-features.md) tests a lattice of that shape.
- "a domain settles the type" (`DefineLattice`) sets `types(p) = {string}` from any `dom(p)`. Domains are
  `ValueSet<string>`, so an `enum` with a number or a `null` member (such as `enum ["600", 1]`) cannot be stated.
  Integer domains (`IDom`) settle no type.

Nothing in the class remarks states these limits.

### 3.3 `Max` and `Min` for strings

`src/Eggnog/Values/ValueLattice.cs` defines `Max<T>()` (lines 54-55) and `Min<T>()` (lines 62-63) with
`a.CompareTo(b)`. For `string` this compares with the current culture. Everything else in the library orders strings
ordinally:

- `ValueSetOrder<T>` uses `StringComparer.Ordinal`;
- the built-in `String` column type is registered with `StringComparer.Ordinal` (`src/Eggnog/Values/ValueStore.cs`,
  line 42);
- extraction's tie-break uses `string.CompareOrdinal` (`BaseSort.CompareValues`,
  `src/Eggnog/Relations/Extraction/BaseSort.cs`), whose remarks give the reason: the default comparer "would use the
  current culture's collation and make the extracted term depend on the machine and the thread".

Nothing in `src/` uses `Max<string>` or `Min<string>`: every use in `src/Eggnog.Tests` is over `long`. The package is a
pre-release (0.0.1-pre.46).

Dry runs for this plan used file-based programs against a private clone at `eda0c61`, on Windows with ICU collation
(the .NET default):

| Probe | Result |
|---|---|
| `Max<string>().Merge("z", "ä")` | `"z"` under en-US and tr-TR; `"ä"` under sv-SE |
| `Max<string>().Merge("I", "ı")` (dotless i) | `"ı"` under en-US and sv-SE; `"I"` under tr-TR |
| `"é"` precomposed (U+00E9) against `"e"` + U+0301 | `CompareTo` 0; ordinal 132 |
| `Max<string>().Verify` on that pair | throws "Lattice 'max' violates commutativity" |
| `"a".CompareTo("a­")` (soft hyphen, ignorable in culture comparison) | 0; ordinal -173. `Verify` on the pair throws the same violation |
| `Max<string>().Merge("a", "B")` | `"B"` under all three cultures; the ordinal maximum is `"a"`, and `ValueSet.Of("a", "B")` orders `"B"` first |

The first two rows mean the same serial program gives different answers on machines with different cultures, on
ordinary text. The next rows mean the merge is not commutative on pairs that compare equal under the culture but differ
ordinally: canonically equivalent Unicode forms, which arise whenever text from different sources is mixed, and
ignorable characters. A merge that is not commutative makes a function's value depend on the order in which rows merge,
which differs between serial and parallel runs and between a child and a copy. `README.md` promises "Deterministic by
default".

One related inaccuracy: `StringCodec` (`src/Eggnog/Relations/BaseValues/StringCodec.cs`) says strings "are compared
ordinally, as Rust compares them". .NET's ordinal comparison orders UTF-16 code units; Rust's `str` ordering is by
UTF-8 bytes, which is code-point order. They disagree when a supplementary character (above U+FFFF) meets a character
in U+E000-U+FFFF: a dry run gives `string.CompareOrdinal("\U0001F600", "�")` = -10176, while Rust orders U+1F600
after U+FFFD. This touches extraction tie-breaks compared with egglog on such strings only. It does not change the
choice for `Max`/`Min`: ordinal order is right there, for consistency with `ValueSet`.

### 3.4 net8.0, AOT and trimming

`src/Eggnog/Eggnog.csproj` targets net8.0 and net10.0 and sets neither `IsAotCompatible` nor `IsTrimmable`. The library
has no `#if NET…` conditionals, and uses no `Reflection.Emit`, `DynamicMethod`, `System.Linq.Expressions`,
`MakeGenericType` or `MakeGenericMethod`.

**CI.** `.github/workflows/Eggnog.yml` builds both frameworks and packs. Its test job runs `Eggnog.Tests` on five
platforms (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64), on **net10.0 only**: the `tfm` axis of the matrix
lists only net10.0, and the job installs only the .NET 10 runtime (`actions/setup-dotnet`, `10.0.x`).
`src/dist-tests/dist-tests.csproj` packages `Eggnog.Tests` for both net8.0 and net10.0, so the net8.0 test assembly is
built and shipped to the job but never run. A user on net8.0 therefore runs a build that CI never tests. With no
conditional source, the risk is in runtime and base-library behaviour, which only a run catches.

**Dry runs for AOT and trimming**, in a private clone at `eda0c61`, with packages already in the local cache:

1. **Library build with the analyzers on** (`-p:IsAotCompatible=true -p:IsTrimmable=true`, Release, both target
   frameworks). It produced three trim warnings on net10.0 and two on net8.0 (the net8.0 analyzer does not report
   line 486), and **no AOT (IL3xxx) warning** on either:
   - `src/Eggnog/Values/ValueStore.cs:253`, IL2070: `RegisterMethod(Type)` calls `GetInterfaces()` to find `ISort<>`.
     `Register<T>` uses it to refuse a sort (lines 105 and 108). Four other call sites use it only to word an error
     message: `ValueStore.cs:209`, `src/Eggnog/Cases/SplitAssumptionChecker.cs:230`,
     `src/Eggnog/Rules/ProgramBuilder.cs:870` and `src/Eggnog/Rules/RuleCompiler.cs:137`.
   - `src/Eggnog/Rendering/SExpression.cs:486` and `:488`, IL2075: `DefaultLiteral` finds `ISort<>` among a value's
     interfaces and reads its `Id` property by reflection, to render a sort value whose column type is unknown.
2. **NativeAOT compile of a smoke app.** The app ran the README's two examples (net10.0, win-x64). The ILC
   whole-program pass reported the same three warnings and no IL3xxx warning. The native link step then failed to
   locate the toolchain: `vswhere.exe` was not on the PATH, although an MSVC linker is installed. So the binary was not
   run; running the publish from a Visual Studio developer shell would likely complete it locally.
3. **Trimmed, self-contained publish** (`PublishTrimmed`, net10.0) of the same app. It produced the same three
   warnings. The trimmed app printed exactly what the JIT build printed: the rewrite example's equality and extraction,
   the split example's three answers, and a `ValueSet` of a sort value. `Register<Expr>` still refused the sort with
   its message.

Nothing checks AOT or trimming in CI. No issue, roadmap item or user asks for AOT or trimming support.

### 3.5 Facts that bear on the compiled-plans question

The library uses no dynamic code today, and the analyzers find none. A compiled-plans implementation would be the
first dynamic code in the package:

- Under NativeAOT, `DynamicMethod` is unsupported. `MakeGenericType` and `MakeGenericMethod` can only use
  instantiations generated ahead of time.
- `LambdaExpression.Compile` falls back to the expression interpreter.
- Each technique therefore needs a `RuntimeFeature.IsDynamicCodeSupported` (or `IsDynamicCodeCompiled`) branch that
  keeps the engine's interpreter.
- An `IsAotCompatible` declaration turns any unguarded use into a build warning, which B-min makes an error.

[`./compiled-plans.md`](./compiled-plans.md) recommends dropping compiled plans. If the owner accepts that, item B has
no interaction with it.

## 4. Value

**A. Ordinal `Max`/`Min`.**

- Benefits library users. A function declared with `ValueLattice.Max<string>()` or `Min<string>()` today gives
  different results on machines with different cultures, on ordinary text (section 3.3), and can merge differently
  between serial and parallel runs when canonically equivalent or ignorable-character strings meet.
- If the fix is never made, the library ships a built-in lattice that fails its own `Verify` and contradicts the
  README's "Deterministic by default".
- The behaviour change has no portable dependant: nothing in the repository uses `Max<string>` or `Min<string>`, the
  package is a 0.0.1 pre-release, and today's result depends on the machine's culture.

**H. net8.0 in CI.**

- Benefits every user on net8.0: the package ships a net8.0 build that CI never runs (section 3.4).
- The test assembly for net8.0 is already built and shipped to the job; only the run is missing.
- One platform (linux-x64) catches most runtime and base-library differences; win-x64 adds the NLS-or-ICU and file
  system differences of Windows machines.
- If it is never done, a net8.0-only regression reaches net8.0 users first.
- The value lasts only as long as the package targets net8.0. .NET 8's support ends in November 2026; if the package
  drops the target, H is moot (section 9, question 2).

**B. AOT and trimming.**

- No demand is recorded: no issue, roadmap item or user asks for it.
- Prospective beneficiaries are users who publish trimmed or NativeAOT applications: command-line tools, serverless
  functions, mobile.
- B-min turns the dry runs' "presumably works" into a property the build enforces: the analyzers flag the first
  unguarded reflection or dynamic-code path on every build, and the publish-only check runs ILC's whole-program pass
  over the whole library. The library has no reflection beyond the three sites and no dynamic code, so a runtime
  divergence that ILC does not flag is unlikely.
- B-full adds a runtime comparison. Its value is the residual risk B-min leaves, which is small, and it is brittle
  (section 5).
- If neither is done, nothing breaks for JIT users. AOT users get warnings and no guarantee.

**C. Documentation.**

- The beneficiary is anyone who compiles a schema with undiscriminated disjunctions into split rules. The
  hidden-discriminator pattern and the `governs` remedy for split demand are general modelling techniques for any user
  with such disjunctions. They are recorded today only in documents that say they do not describe the code.
- The numeric-bound part matters to anyone who models comparisons with bound lattices. Each point is a soundness trap,
  and the fixture shows both: a converting comparison modelled as a comparison between numbers proves a type that a
  stored string violates, and integer stepping proves a bound that a fraction violates. The fixture's class remarks
  record its own limits for anyone who reads or reuses it.
- If it is never done, a theory modelled the same way can carry the traps silently, and the pattern stays in documents
  that do not describe the code.

**D. The prototype README.** Closed: the prototype it described is no longer in the repository, so there is nothing
to correct.

**E. Eq-container push/pop programs.** Five programs, plus the eq-container half of `vec.egg`, would check eq
containers in children against egglog. The feature does not exist, so there is nothing to check yet. Their value
follows the feature's value, which [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md) puts at "the
only gain today is six conformance programs" and parks.

**F. Subsumption and deletion in children.**

- Engine-internal: the public API offers neither (section 3.1), so no library user can reach a defect here.
- The gap is narrow. Subsumption runs through the generic merge and layer write paths that the context differentials
  exercise with ordinary value changes, and the guard on deletes is tested. What is untested is the observable result
  of the pieces together: what a child's later rules match after it subsumes, and whether a deleted row stays hidden.
- The unused `CheckSubsume` is a contradiction between the spec, the code and a passing test. Settling it removes dead
  code or adds a missing check, and makes the colored-contexts record true.

**G. `math.egg` and the programs that need missing sorts.**

- `math.egg` is a real program, with egglog's `check` commands as the oracle. But each push would start from a root
  that has run nothing (section 3.1), so its pushes test root behaviour that the root bridge tests already cover. Its
  one child-specific feature, subsumption and deletion by rules, is covered more cheaply by item F. Translating 73
  rule forms by hand buys little beyond F.
- `UnstableFn` (four programs) needs function values: a container that names a function, and a primitive that calls a
  table from inside a rule. The library has no such concept (D20: no containers in the library).
- The issue #113 blocks test egglog's type checker. Eggnog has none.
- `herbie.egg` is 196 rules by hand. `herbie-tutorial.egg`'s oracle is three checks.
- None of them would test colored-context behaviour that `bdd.egg`, `cyk.egg` and the context differentials do not
  already test.

If they are never done, nothing is lost but a line in the roadmap.

## 5. Cost and risk

| Item | Effort | What dominates it | Risk to correctness, and its oracle | Risk to the root's performance | Maintenance after it lands |
|---|---|---|---|---|---|
| A | small | two lambdas and their tests | a behaviour change for string users, with no known dependant. Oracle: `Verify` over culture-sensitive samples, and agreement with `ValueSet<string>` order | none (`Max<long>` is unchanged; an ordinal compare is cheaper than a culture compare) | none |
| H | tiny | one or two `include` entries in the matrix and an 8.0 runtime in the test job | none. Oracle: the existing suite | none | one or two more test jobs per push (five for the full matrix) |
| B-min | small | the three code sites, and a publish step in CI (the native link needs clang, which GitHub's Ubuntu images have) | low: the code changes touch an error path and a rendering fallback. Oracle: zero analyzer and ILC warnings, and the published app exits with 0 | none: no runtime code changes on hot paths | every new reflection or dynamic-code use must be annotated or guarded, or the build fails; one publish step per push |
| B-full | medium | a smoke app over every public area and its golden file | the golden file is brittle: `RunReport.ToString()` prints the elapsed milliseconds (`src/Eggnog/Scheduling/RunReport.cs`), concurrent children finish in any order, and any change to rendering or report wording churns the file. Printing only deterministic fields and sorting children's results contains this | none | the golden file changes with every rendering change |
| C | small | writing | none (documentation and test-class remarks) | none | must be kept in step with split and scope APIs |
| F | small | two or three directed tests, and either wiring or deleting `CheckSubsume` | tests only, unless `CheckSubsume` is wired, which refuses a subsume the code allows today (the extraction test would need to change) | none if deleted; one guard call per subsume batch if wired | none |
| E | medium, after the feature | translating 5 programs (the container types exist in the tests) | tests only | none | five more hand translations |
| G | medium (`math.egg`, about 600-1,000 lines of C# judging by `bdd.egg`'s 19 rules) to large (function values, or a `.egg` reader) | translation, or features | — | — | — |

## 6. Worth-it evaluation

| Question | Cheapest decisive experiment | Metric and threshold | Idle machine? | Verdict now |
|---|---|---|---|---|
| A: is the current behaviour wrong? | Done: the probes in section 3.3 | results differ across cultures, or `Verify` fails on a two-string sample → go | no | **Go** |
| H: is net8.0 worth a CI job? | none needed: the untested shipped build is a fact (question 2 decides whether the target stays) | — | no | **Go** (linux-x64; win-x64 optional) |
| B1: does the package work trimmed? | Done: the trimmed, self-contained publish in section 3.4 | output identical to the JIT run → go | no | **Go** |
| B2: does the whole library pass ILC with no IL3xxx? | Publish a tiny app with `PublishAot=true` and `TrimmerRootAssembly=Eggnog` on a Linux CI runner, or locally from a developer shell | zero IL2xxx and IL3xxx warnings once the three sites are cleared, and the app exits with 0 → declare `IsAotCompatible`. An IL3xxx → declare `IsTrimmable` only, and record why | no | **Go** (B-min) |
| B3: is a runtime golden comparison needed? | Owner decision (section 9, question 1) | AOT named a supported target → B-full. Otherwise B-min suffices | no | ask |
| C: is the note worth writing now, with no user waiting for it? | none needed: the material exists, the cost is small, and its only record is in documents that do not describe the code | — | no | **Go** (small) |
| F: is `CheckSubsume` needed? | The directed test F1 below: subsume at a writable root with a live child, then catch the child up | the child sees the row subsumed and its rules stop matching it with no inconsistency → delete `CheckSubsume` and correct the record. An inconsistency → wire `CheckSubsume` at both subsume paths, as the spec says | no | **Go** (small) |
| E: build eq containers in children? | owned by [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md) (its Phase F trigger) | — | — | **Later** |
| G: any program worth translating by hand? | none needed | — | — | **Drop** (`math.egg` an owner option) |

A planted-defect experiment for F, considered in an earlier draft, was dropped: subsumption in a child has no
child-specific branch to plant a defect in. A defect in the generic merge or layer write path breaks many child tests
and says nothing about subsumption; a defect in a subsume-only branch would be artificial.

Nothing in this workstream needs timing, so nothing waits on [`./idle-machine-measurement.md`](./idle-machine-measurement.md).

## 7. Plan if it goes ahead

The project's practice applies to every phase: implement; review from several angles, including a mutation-testing
review of the new tests; fix; verify on net8.0 and net10.0.

**Phase 1: ordinal `Max`/`Min` (A).** Entry: none.

- Change both lambdas in `ValueLattice.Max<T>()` and `Min<T>()` to compare with `ValueSetOrder<T>.Comparer` (ordinal
  for `string`, `Comparer<T>.Default` otherwise, so every other type keeps its behaviour). State in their XML docs that
  strings compare ordinally, as sets do, and that a user type's `CompareTo` must be a total order consistent with
  `Equals`, which `Verify` checks. Offer `Max<T>(IComparer<T>)` only if a user asks for it.
- Tests (`src/Eggnog.Tests/Values/`):
  - `Max<string>().Verify` and `Min<string>().Verify` over `"z"`, `"ä"`, `"I"`, `"ı"`, precomposed and decomposed
    `"é"`, `"a"`, `"a­"` (soft hyphen), `"B"` and `""`, each under en-US, sv-SE and tr-TR set as the current culture;
  - the merge result for `("z", "ä")` and `("I", "ı")` is the same under all three cultures;
  - `Max("a", "B") == "a"`, and the merge's maximum equals the last element of `ValueSet.Of(...)` for random string
    samples;
  - a lift's case join over a `Min<string>` function.
- Mutation review: reverting to `CompareTo` must fail at least one test, on Windows and on Linux (ICU). The test
  project does not run in invariant-globalization mode, so the culture comparison is live there.
- Correct the remark in `StringCodec`: strings compare by UTF-16 code unit, which differs from Rust's order when a
  supplementary character meets U+E000-U+FFFF. Whether extraction tie-breaks should follow code-point order is left to
  [`./smaller-engine-items.md`](./smaller-engine-items.md); no test or user needs it today.
- Release note: string maxima and minima now compare ordinally.
- Exit: green on both frameworks; the reverted mutant is killed.

**Phase 2: net8.0 in CI (H).** Entry: none.

- In `.github/workflows/Eggnog.yml`, add a matrix `include` entry `run: Eggnog.Tests`, `sys: linux-x64`,
  `tfm: net8.0` (and the same for win-x64 if the owner wants it), and install the 8.0 runtime beside 10.0 in the test
  job's `actions/setup-dotnet` step, because `dotnet exec` of a net8.0 assembly does not roll forward to .NET 10.
- Check the first run actually executes tests: the job's script skips silently when it finds no test assembly for the
  framework.
- Exit: the net8.0 job green on the chosen platforms.

**Phase 3: AOT and trimming, minimum (B-min).** Entry: none.

- Clear the three warnings:
  - `ValueStore.Register<T>`: annotate `T` with
    `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]`, or find the sort test another way. The
    public surface file `src/Eggnog.Tests/Surface/PublicSurface.txt` changes if the attribute is public-visible.
  - the four error-message call sites of `RegisterMethod` (`ValueStore.cs:209`, `SplitAssumptionChecker.cs:230`,
    `ProgramBuilder.cs:870`, `RuleCompiler.cs:137`): if `RegisterMethod`'s parameter is annotated, the warning moves to
    each of them. Suppress each with `UnconditionalSuppressMessage` and a justification (they only word a hint), or
    suppress once inside `RegisterMethod` for a message-only overload.
  - `SExpression.DefaultLiteral`: suppress with a justification (a trimmed sort renders by its `ToString()`), or render
    sort values without reflection.
- Set `<IsAotCompatible>true</IsAotCompatible>` in `src/Eggnog/Eggnog.csproj`, and make IL2xxx and IL3xxx warnings
  errors in that project.
- Add a CI step on `ubuntu-24.04` that publishes a tiny app (the README's two examples are enough) with
  `PublishAot=true` and `TrimmerRootAssembly=Eggnog`, fails on any IL2xxx or IL3xxx warning, and runs the binary,
  checking only its exit code. Rooting the assembly makes ILC analyze all of it, not only what the app calls. The app is
  not packed and not part of `Eggnog.Tests`.
- Docs: one line in `README.md` and `src/Eggnog/README.md` (trimming and NativeAOT compatible).
- Exit: CI green with the publish step; no analyzer warning on either framework.

**Phase 3b: AOT smoke application (B-full).** Entry: the owner names NativeAOT a supported target.

- Extend the Phase 3 app to one call into each public area: registered value types, enums and sets; a lattice with a
  case join; host writes and queries; concurrent children of a frozen scope; a split and a host-driven `CaseSet`;
  extraction; rendering of rules and rows with a sort constant; a `RunReport`.
- Print only deterministic fields: no timings (not `RunReport.ToString()`, which prints elapsed milliseconds), and the
  results of concurrent children sorted. Diff the NativeAOT run's output against a JIT run of the same app in the same
  job rather than a checked-in golden file, so rendering changes do not churn a file.
- Whether xunit.v3 can run `Eggnog.Tests` itself under NativeAOT was not established here, and is not needed.

**Phase 4: documentation (C).** Entry: none.

- [`../architecture.md`](../architecture.md), section 2.9: a short subsection, "Splitting a disjunction that has no
  discriminator". It should cover:
  - the synthesized key (a name no user path can take; its presence follows the parent's; its domain is the branch
    indices; it is marked as a discriminator);
  - branch rules guarded on the key's presence and on its domain narrowed to one index;
  - the coverage obligation, which becomes the compiler's;
  - why a query split guarded on "mentioned" never splits on a hidden key;
  - the `governs(k, p)` remedy: the query split is guarded on "mentions k, or mentions a path k governs", and the host
    may supply that demand;
  - the cost: one schema case per branch, multiplied by nesting, against `CaseLimit` and `SplitDepthLimit`;
  - a reference, as the worked example, to `SplitExtractionTests.ASplitOnAHiddenDiscriminatorJoinsTheBranchDomains`,
    which compiles a hidden-discriminator schema with `PredicateTheory.Facts` and checks what the split lifts.
- In the same note, a paragraph on numeric bounds: a comparison that first converts a stored value proves no type, and
  a bound that steps by one is sound only over integers (a bound with an open flag over `decimal` avoids it), with
  `PredicateTheory`'s bound rules as the example of the integer case.
- If the owner wants the remedy shown working, add one scoped test with a `governs` relation. Today no test uses it.
- `PredicateTheory`'s class remarks (`src/Eggnog.Tests/Predicates`): its three limits, as in section 3.2: comparisons
  between numbers only, integer stepping, and string-only domains.
- Roadmap: remove the documentation bullet and rewrite "Push/pop conformance" (Phase 6). The roadmap lists only what is
  open, so items done here are recorded in the release notes, not in the roadmap.
- Exit: links checked; the review checks every statement against the code.

**Phase 5: subsumption and deletion in children (F).** Entry: none.

Directed tests in `src/Eggnog.Tests/Relations/Contexts/` (or `Relations/Bridge/`), through `EGraphRuleBuilder`:

1. A child of a writable root runs a subsuming rule, then a rule that matches only non-subsumed rows (`QueryTable`
   with `isSubsumed: false`). Assert that the second rule does not match the subsumed row in the child, and that the
   parent still sees its row unsubsumed and its rules still match it.
2. A rule-action delete in a child under a frozen root succeeds; the deleted row stays hidden from the child's later
   rule matches and from a grandchild, and the root still has it.
3. The same delete under a writable root is refused, at the bridge level. `RetractionTests` covers the engine-level
   refusal; this test pins how the refusal surfaces through a rule run (a throw, and the child faulted).

Then settle `CheckSubsume` with one more directed test: subsume at a writable root while a child is live, catch the
child up, and check that the child sees the row subsumed and that its rules over non-subsumed rows stop matching it.
If that holds, delete `RetractionGuard.CheckSubsume` and its rows in `RetractionTests`, and record in
[`../engine/decisions.md`](../engine/decisions.md) that a subsume is a sticky value change, allowed wherever a write
is. If it does not hold, wire `CheckSubsume` into `RuleAction.LowerSubsume`'s instruction and `TableAction.Subsume`, as
the colored-contexts spec says, and change `ContextExtractionTests.ARelevantWriteOrUnionInANonFrozenAncestorMakesItStale`.

- Mutation review: removing the `isSubsumed` constant from the second rule's atom, or letting the child's delete skip
  hiding the ancestor row, must fail a test.
- Exit: green on both frameworks.

**Phase 6: push/pop roadmap cleanup (E, G).** Entry: none.

- Rewrite the roadmap's "Push/pop conformance" item:
  - the eq-container programs (`container-rebuild.egg`, `container-fail.egg`, the eq half of `vec.egg`, and the web
    demo's `lambda.egg`, `typeinfer.egg` and `fusion.egg`) wait on eq containers in children, linked to the interning
    plan;
  - the `UnstableFn`, `Map`-type-checking, `MultiSet` and `BigRat` programs, and `math.egg`, are out of scope, with the
    reasons of section 4 (for `math.egg`: its pushes start from a root that has run nothing, and Phase 5 covers its
    child-specific feature).
- Update `EggPushPopTests`' and `EggPushPopWebDemoTests`' remarks to say the same, including that a translation of
  `math.egg` would have to freeze the root before its first push.
- When eq containers in children land, translate the six eq-container programs with `VecContainer` and `SetContainer`,
  in the interning plan's Phase F.

## 8. Dependencies and ordering

- [`./interning-and-value-lifetime.md`](./interning-and-value-lifetime.md): item E waits on its Phase F (eq containers
  in children), which it parks. Its documentation of interning in children should land in the same documentation pass
  as Phase 4 here.
- [`./compiled-plans.md`](./compiled-plans.md): item B and that plan constrain each other. If compiled plans are
  dropped, as that plan recommends, B adds no burden. If they are revived, every dynamic technique needs a guarded
  interpreter fallback, and B-min's analyzers, as errors, enforce the guard. That plan's conventions and roadmap edits
  can share Phase 4's documentation pass.
- [`./rule-layer-split-features.md`](./rule-layer-split-features.md): the split and rule API that workstream may change
  (per-case bounds, "no result" primitives), and the struct-lattice test that models an open bound. The
  hidden-discriminator note should describe the split API as that workstream leaves it, so write it after any change
  there to split demand, or amend it then.
- [`./colored-context-deferred-features.md`](./colored-context-deferred-features.md): retraction under a writable
  parent (R1) is why a `math.egg` translation would need a frozen root, and why Phase 5's third test expects a refusal.
  If that workstream relaxes R1, the third test changes with it.
- [`./parallelism-test-plan.md`](./parallelism-test-plan.md): a serial-against-parallel test with lattice merges that
  change values would also catch a non-commutative merge such as today's `Max<string>`. Phase 1 here removes that one
  source.
- [`./smaller-engine-items.md`](./smaller-engine-items.md): the code-point order question for extraction tie-breaks
  (section 3.3), if the owner wants it tracked.
- [`./idle-machine-measurement.md`](./idle-machine-measurement.md), [`./performance-parity.md`](./performance-parity.md):
  no dependency.

Order: Phases 1, 2, 3, 4, 5 and 6 can all start at once (all small); Phase 2 first, because it also verifies the
others on net8.0. Phase 3b only after the owner's answer. Item E with the interning plan's Phase F.

## 9. Questions for the owner

1. Is NativeAOT a supported target for the package? If yes, Phase 3b adds the runtime smoke comparison. If no, Phase 3's
   declaration and publish-only check stand alone.
2. Should the package keep targeting net8.0? .NET 8's support ends in November 2026. If the target stays, Phase 2 runs
   it in CI; if it goes, Phase 2 is dropped and the conventions' "net8.0 and net10.0" changes with it.
3. Should net8.0 run on all five CI platforms, at five more jobs per push, rather than on linux-x64 (and win-x64)?
4. Where should the hidden-discriminator note live: a subsection of `architecture.md` section 2.9 (proposed), or a
   separate modelling guide under `docs/`?
5. Do you want `math.egg` translated anyway, as a real-program oracle, although its pushes start from an empty root and
   Phase 5 covers its child-specific feature?
6. May the roadmap drop the `UnstableFn`, `Map`, `MultiSet` and `BigRat` programs outright, or should they stay listed
   as waiting on a `.egg` reader that is not planned?

## Review

A skeptical review of an earlier draft raised thirteen problems. Each was checked against the sources.

Changed:

- **Subsumption in children (F).** The planted-defect experiment and the differential extension are replaced by three
  directed tests (Phase 5): subsumption has no child-specific branch, as the review said (`FunctionTableMerge`,
  `EGraphRuleBuilder.QueryTable`). Checking this turned up a fact the review did not state: `RetractionGuard.CheckSubsume`
  has no caller, contrary to the colored-contexts spec, and an existing extraction test subsumes where it would refuse.
  Phase 5 settles it. Also added: neither subsumption nor deletion is reachable from the public API.
- **`math.egg`** is dropped with the other programs (item G) and kept as owner question 5.
- **net8.0 in CI** is a Do-now item of its own (H, Phase 2), on linux-x64 and optionally win-x64, citing the net8.0
  build the package ships and CI never runs. The plan also notes that the test job must install the 8.0 runtime.
- **AOT** is split into B-min (Do now: clear warnings, declare, errors, publish-only ILC check with
  `TrimmerRootAssembly`) and B-full (only if the owner names AOT a target). Section 4 states that no demand is recorded.
  Section 5 records why a golden file is brittle (`RunReport.ToString()` timings, concurrent children, rendering
  churn); B-full now diffs against a JIT run in the same job and prints only deterministic fields.
- **Native link failure:** the cause was `vswhere.exe` missing from the PATH, not a missing linker; a developer shell
  would likely complete it locally.
- **Warning counts:** three on net10.0, two on net8.0.
- **`RegisterMethod` callers:** four error-message call sites, all named in sections 3.4 and Phase 3.
- **`LifterTests.LiftedRowsAreNotSubsumed`** added to the tests that subsume in a child; the statement about later
  matches kept.
- **The documentation item** described in full; its point about the rule-layer spec's status lines is moot because the
  history documents are archived.
- **Evidence for A** now leads with cross-culture differences and canonically equivalent strings, with the soft hyphen
  second, and Phase 1's samples include them. New dry runs confirmed each probe.
- **`StringCodec`'s remark** is no longer cited as support; section 3.3 explains why it is wrong for supplementary
  characters, and Phase 1 corrects it.
- **Conventions:** section 17 is cited as "Milestones"; R2 is glossed; the roadmap edit no longer lists a done item
  (release notes record it).
- **Former question 3** (culture-aware lattices) is now a statement: switch to ordinal order with a release note, and
  offer `Max<T>(IComparer<T>)` only on request.

Rejected, in part:

- The review's example of a culture-dependent result, `"B"` against `"a"`, is not one: the dry run gives `"B"` under
  en-US, sv-SE and tr-TR alike. It differs from ordinal order, not across cultures. The plan uses `"z"`/`"ä"` and
  `"I"`/`"ı"`, which do differ across cultures.
- The review's directed test "the deleted row stays hidden after catch-up" under a frozen root has no catch-up to
  observe, since a frozen root does not change. Phase 5's second test checks the child's later matches and a grandchild
  instead.
