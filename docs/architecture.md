# Eggnog architecture

This document describes Eggnog as it is: the model the public library offers, the engine underneath it, how the two
fit together, and how both are tested. It is written for contributors. Users of the package need only the
[README](../README.md) and the XML documentation of the public types.

The reasoning behind most decisions, with the alternatives that were rejected, is in the design record in
[`history/`](history/README.md). The engine's binding conventions are in
[`engine/conventions.md`](engine/conventions.md), and the decisions that still bind new engine code in
[`engine/decisions.md`](engine/decisions.md).

Contents:

1. [Overview](#1-overview)
2. [The library's model](#2-the-librarys-model)
3. [The engine](#3-the-engine)
4. [How the library sits on the engine](#4-how-the-library-sits-on-the-engine)
5. [Testing](#5-testing)
6. [Benchmarks](#6-benchmarks)

---

## 1. Overview

Eggnog is an equality-saturation library: a database of terms and facts, closed under equality (an e-graph), and a
Datalog-style rule engine that runs over it until nothing new can be derived. Its engine is a C# reimplementation of
[egglog](https://github.com/egraphs-good/egglog)'s Rust engine, and its public API is a typed C# API over that
engine. Two features have no counterpart in egglog: scopes (a tree of contexts that inherit their parent's contents
and store only what they change) and case splits with lift (reasoning by cases inside a scope, keeping what every case
proves).

### 1.1 Layers

```
consumer code (C#)
      |
      v
Eggnog (namespace Eggnog, public)
  EGraph, Scope, ScopeTag, typed tables (Relation, Function, Constructor), Term and Fact,
  rules and rulesets, queries, schedules, CaseSet, CostModel, Extractor, run reports
Eggnog (namespace Eggnog, internal)
  RuleCompiler -> RuleProgram -> RuleLowering, EngineScope / ContextEngineScope, ScopeGuard,
  ScopeOrigin, ScheduleExecutor, HostBatch, QueryRunner, mirror tables, RuleSplitSource,
  LiftSpecBuilder, ExtractionCatalog
      |   same assembly; engine types are internal and named as Relations.X
      v
Eggnog.Relations bridge (internal)
  EGraph, EGraphContext, EGraphRuleBuilder, RunRules / IterationReport, Extractor,
  CaseSet / Lifter / SplitDriver, BaseValues
      v
Eggnog.Relations core (internal)
  Database, SortedWritesTable, DisplacedTable, indexes, Free Join planner and executor,
  Store / Context, layers and hidden rows, layered union-find
```

The library and the engine are one assembly and one NuGet package, `Eggnog`. The engine lives in `src/Eggnog/Relations`
in the namespace `Eggnog.Relations`, and every engine type is `internal`, so the public `Eggnog` types are the only
supported API. A test (`Surface/PublicSurfaceTests`) pins the public surface (`Surface/PublicSurface.txt`) and asserts
that no engine type is public and that no public member's signature mentions one. Library code names engine types
through the child namespace (`Relations.EGraph`, `Relations.FunctionId`) and never writes `using Eggnog.Relations;`,
because several engine and library types share simple names.

### 1.2 Repository layout

| Path | Contents |
|---|---|
| `src/Eggnog` | The library (folders `Graph`, `Tables`, `Terms`, `Rules`, `Scheduling`, `Scopes`, `Cases`, `Host`, `Values`, `Extraction`, `Diagnostics`, `Rendering`) and the engine (`Relations/`) |
| `src/Eggnog.Tests` | The library's tests (namespaces `Eggnog.Tests.*`) and, under `Relations/`, the engine's (namespace `Eggnog.Relations.Tests`) |
| `src/Eggnog.Benchmarks` | BenchmarkDotNet benchmarks, run in process; the engine's are under `Relations/` |
| `src/Eggnog.AotCheck` | A small app, never packed, that CI publishes with NativeAOT to check the library stays trimming and AOT compatible |
| `src/dist-nuget`, `src/dist-tests` | Packaging projects driven by `Eggnog.dist.msbuildproj` in CI |
| `ext/egglog` | egglog's sources, as a git submodule: the reference for the port |
| `docs` | This document, the roadmap, the engine's conventions and decisions, and the design history |

---

## 2. The library's model

### 2.1 The graph and its definitions

An `EGraph` is a theory and its database. It holds the column types (`Values`), the tables, the rulesets and their
rules, the queries, the split rules, the scope tags, and the root of a tree of scopes (`Root`). It holds only managed
memory and is not disposable.

Definitions are never sealed. Rules, rulesets, queries, split rules and tags may be defined from any thread at any
time, and take part from the next iteration of every run. A table definition may be made from any thread once the
root is frozen; before that it throws `InvalidOperationException` if another thread is using the root at that moment.
Definitions are taken under the graph's definition lock and published copy-on-write to the engine.

`EGraphOptions` sets the root's degree of parallelism (default 1: serial and deterministic) and whether rule
statistics record each rule's query plan.

### 2.2 Column types and values

A column holds either a class or a value.

- **Sorts** are e-class types. A sort is a `readonly record struct` that implements `ISort<TSort>` over a `ClassId` and
  is registered with `EGraph.DefineSort<TSort>(name)`. A `ClassId` carries the scope that returned it (its origin): it
  is usable in that scope and its descendants, and every entry point that takes one checks this and throws
  `ArgumentException` otherwise. Disposing the scope does not change that: its classes stay usable in its live
  descendants and in its calls already in progress, which keep it from being released. Only the library makes
  `ClassId`s; `default(ClassId)` names no class. Two `ClassId`s of one class may differ (`Find` returns the canonical
  one), so classes are compared with `Scope.AreEqual`.
- **Value types** are plain .NET values. Built in: `long`, `int`, `bool`, `double` (egglog's `f64`, with its
  `OrderedFloat` semantics: both zeros are one value, and so are all NaNs, which equal each other and sort above every
  other value; each reads back as the one the graph interned first), `string`, `BigInteger` (egglog's `BigInt`, rendered as egglog renders it, `(from-string "123")`),
  `BigRational` (egglog's `BigRat`, in lowest terms, rendered as egglog renders it,
  `(bigrat (from-string "3") (from-string "4"))`), and `ValueSet<long>`, `ValueSet<int>` and `ValueSet<string>`. Others are registered through
  `ValueStore.Register<T>` (any `IEquatable<T>`, with an optional order and format), `RegisterEnum<TEnum>` and
  `RegisterSet<T>`.
- **`ValueSet<T>`** is an immutable ordered set of values. It is a value, interned as a whole, not a container of
  classes: there are no sets of classes.

In the engine every cell is a 32-bit `uint`. Classes are ids; small non-negative integers and booleans are stored
inline; every other value is interned and stored as its intern index. Interning pools are permanent for the life of
the graph (egglog's are too): `ValueStore.InternedCount` reports their growth, so a long-lived host can retire a graph
and build a new one. Host reads never intern, and answer as egglog's `check`, which interns: a value no write
interned, a constant or a computation's result, equals only itself and reads back as itself, and as a key or an output
it is in no row, as egglog's newly interned one is in none.

There is one pool per type for the whole graph, and every scope interns into it, a child scope included: the engine's
child databases share their root's base-value registry by reference, and `ColumnType<T>.Encode` takes no scope. So a
value has the same cell in every scope, sibling and case, which is what lets joins and not-equal tests compare raw
cells across scopes and lifts. What a scope interned stays after the scope is disposed. These operations intern, in
any scope:

| Operation | What it interns |
|---|---|
| A host write (`Make`, `Assume`, `Union`, `Write`) | each new constant that is not stored inline: strings, doubles, `BigInt`s, `BigRat`s, sets, registered values, and `long`s outside `[0, 2^31)` |
| A rule's primitive, in its body or head, during a run | each new result, such as a set that `Union` or `Intersect` computes |
| A lattice merge, in a run or in a child's catch-up | each new merged value |
| A lift's case join | each new joined value, in the shared pool for the parent |
| A split's case assumptions | each new value a case assumes, such as the singleton set of a domain split's case |
| `DefineRule` (local or global) and `EGraph.DefineQuery` | each new constant, when the rule or query is defined |

Reads, checks, query matches and `Until` conditions never intern (`TryEncodeExisting`). A host that serves requests
from long-lived scopes keeps the pools flat by reusing its literals: a query scope that names only values already
interned interns nothing, and one that names a new string interns the string and whatever the rules compute from it.
`EGraph.Usage` reports the pools' size beside the graph's ids and epochs (section 2.12).

### 2.3 Tables

There are three kinds of table, each typed by up to four key columns, or of any number (`TableHandle` is the untyped
base):

| Kind | Defined by | Rows | In the engine |
|---|---|---|---|
| Relation | `DefineRelation<T1, ...>(name)` | a set of key tuples | a function whose output is the unit value; merge: assert equal |
| Function | `DefineFunction<T1, ..., TOut>(name, lattice)` or `(name, merge)` | one output per key (a functional dependency) | a function with a value or class output; merge: the lattice's or the `FunctionMerge`'s, or assert equal without either |
| Constructor | `DefineConstructor<T1, ..., TSort>(name, cost)` | terms: one class per key | a function whose output is a class; default: a fresh id; merge: union the classes |

A table of more than four key columns, as egglog's take, is defined by its key types (`DefineRelationN(name, keys)`,
`DefineFunctionN<TOut>(name, keys, ...)`, `DefineConstructorN<TSort>(name, keys, cost)`), and its `RelationN`,
`FunctionN<TOut>` and `ConstructorN<TSort>` apply to one argument per column, a `params ReadOnlySpan<object>`
(`r.Of(x, 2L, 3L, 4L, "s")`): a term, or a value, which becomes a constant and must be an instance of its column's type.
The count and the types are checked at the call. Otherwise such a table is as a typed one: its output is typed, and its views (`In(tag)`), merges, writes and
reads are the same.

A function with a lattice (`ValueLattice<T>`) merges two outputs for one key with the lattice's `Merge`, which must be
associative, commutative and idempotent. Built in: `Max`, `Min` (over `IComparable<T>`), and `Intersect` and `Union`
over `ValueSet<T>`. A lattice also has a `CaseJoin`, the join a lift uses to combine the values of several cases
(`Max` merges, so its case join is `Min`; `Intersect` merges, so its case join is `Union`). `ValueLattice<T>.Verify`
checks the laws on samples. A function with neither a lattice nor a merge refuses two different outputs for one key: a
host write throws `FactConflictException` and writes nothing; a rule that derives a conflict fails with
`RuleEvaluationException`.

A function may output a class, as egglog's `function` of an e-class sort does (`DefineFunction<T1, ..., TSort>`): it
holds the class written for a key without making a term or a union, the rebuild keeps the output canonical, and
extraction leaves it out, as egglog's does. Its merge asserts the classes equal, or is `FunctionMerge.New` or `Old`, or
an expression (`FunctionMerge.Expression`); a lattice or `FunctionMerge.Of` decodes values, and is refused. A host
write's value may then be a constructor term too, made if absent, as egglog's `set` evaluates it, and the class written
again after a union that made it equal holds. Two rows that a union makes meet must hold one class by then: the rebuild
compares them in the pass that makes them meet, under the merges of the passes before it, and its assert-equal merge
panics on two classes that only that pass's congruences would make one, as egglog's does. A host batch replays those
passes when it commits and refuses that before anything is written; a rule's run fails with `RuleEvaluationException`. A
batch that merged classes refuses a later write of such a function without a merge, which a later call can make; a
split's case writes its functions before its unions. A rewrite's right side may not be a function application, which
egglog would look up in the head. Below the root a function with `New`, `Old` or an expression is root-only like any
other, and a union in which a class one of its rows outputs would lose is refused as for its keys.

A function with a merge that is not a lattice (`FunctionMerge<T>`) merges as egglog's `:merge new`, `:merge old` and
`:merge (f old new)` do: `FunctionMerge.New`, `Old` and `Of(name, f)`, where `f` need be neither associative,
commutative nor idempotent, and `Expression((old, new) => term)`, egglog's merge of any expression: primitives,
constants, and applications of functions and constructors, which the merge looks up or makes (`MergeFn::Function`), so
that a merge of classes may make terms, as `(Join old new)` does. An expression is checked and lowered when its function
is defined, and named by its rendering; a lookup with no row, or a primitive that throws or has no result, fails the
merge as egglog's merge panics, which fails a rule's run or a host write's flush and faults the scope. A host batch
works out an expression of primitives and constants itself, so its failure writes nothing and faults nothing; one that
calls tables only the engine evaluates, after every write of the batch, so the batch stages each write as it comes and
refuses a later read of the key, a later write of a table the expression reads, and the write itself after a merge of
classes, each of which a later call can make. Each write folds into the value before it, in the order the writes come,
so a host write of the value already there folds again (`(+ old new)` of 2 and 2 is 4), and a union that makes two keys
equal merges the row it keeps, the smaller class's, as the old value with the other as the new, as egglog's rebuild
does. A batch stages its unions after its writes, so after it merges classes it refuses a write of such a function whose
keys are classes; a later call gives egglog's result. A rule's matches reach the merge in the engine's match order,
which is not egglog's (the join-index key order item of [`egglog-3.0.0-todo.md`](egglog-3.0.0-todo.md)), so a rule that
writes one key more than once in an iteration may leave another value than egglog's. A parallel insert (a flush of many
rows with `DegreeOfParallelism` above 1) folds each key's incoming rows before merging them into the stored row, as
egglog's does, so its order is not the serial one, which a batch's own reads assume.

Only the root writes such a function (decision 2 of [`engine/decisions.md`](engine/decisions.md)). Below it the library
refuses, before anything is written: a host write of one; a host merge of classes in which a class that keys one of its
rows would be the larger, whose rows the engine would rewrite; a rule local to a child that writes one, and a split rule
that assumes one; and a run in which a rule that writes one would run below the root, in a child or in a split's cases.
No rule, query or split rule reads one in another scope (`In`), since its mirror would be written below the root; a host
read may. Two cases depend on the data, so only the engine refuses them, with its `InvalidOperationException`: a rule's
union below the root that would rewrite one of the function's rows fails the run (and faults a child); and a root write
or merge that a live child would have to rewrite at its catch-up, because the child merged away a class keying or held
by the row, faults that child at its next call, and not the root.

`TableOptions` sets a relation's or function's role (`Fact`, the default, or `Analysis`, a table derived from terms
alone), whether lifts copy the table into a case's parent (`Lift`; by default facts and constructors lift and analyses
do not), whether extraction may use a constructor's rows (`Extractable`), and whether a relation's or a constructor's
rows may be subsumed (`Subsumable`, section 2.5).

`EGraph.Contradiction` is a built-in nullary relation, an addition egglog lacks, named `$contradiction` so that it
takes no name a user's table may take (the `$` prefix is reserved). A rule that derives it says the scope has no model. In a case it
stops the case (section 2.9); elsewhere it is an ordinary fact, unless a run asks to stop on it
(`RunOptions.StopOnContradiction`).

### 2.4 Terms and facts

`Term<T>` is an expression of type `T`: a constant (any `T` converts implicitly), a variable (`Var<T>`), a table
application (`f.Of(a, b)` for a function or constructor), a primitive application (`Primitives.Add.Of(x, y)`, or a
user primitive made with `Primitive.Create` or `Primitive.CreatePartial`, of up to four typed parameters or, as egglog's
take, of any number given by their types, its function taking the arguments as a span of objects, valid only during the
call: `PrimitiveN<TResult>`), or a computation over a lambda (`Compute.Of(a, b, (a, b) => ...)`). The built-in primitives are egglog's, with egglog's names and results, one class per
value type: `Primitives` (`i64`, and the set primitives), `DoublePrimitives` (`f64`), `BigIntegerPrimitives`
(`BigInt`), `BigRationalPrimitives` (`BigRat`), `BooleanPrimitives` (`bool`) and `StringPrimitives` (`String`). A
comparison returns a `bool`: as a filter it is egglog's fallible `<` and renders so; as a value it is egglog's `bool-<`
for `i64` and `BigInt`, and an addition for `f64` and `BigRat`, which egglog compares only as filters; any other filter renders as egglog's `(guard …)`. `or`, string `+` and `set-of` take any number of arguments (`VariadicPrimitive`,
`Primitive.CreateVariadic`). What is not yet followed is in decision 22 of [`engine/decisions.md`](engine/decisions.md).
`Fact` is a statement: a relation row (`r.Of(a, b)`), an equality (`t.Is(u)`, which for a function application sets
or matches its output), a disequality (`t.IsNot(u)`), an existence (`Fact.Exists(term)`), a condition
(`Fact.True(term)`), or a scoped fact (`Fact.In(tag, facts)`, section 2.8). The same terms and facts serve rule
bodies, rule heads, host writes, host checks and queries; each use checks that a fact is allowed there.

### 2.5 Rules and rulesets

A ruleset (`DefineRuleset`) is a named group of rules that a schedule runs together. A rule is defined with a builder:

```csharp
rules.DefineRule("a singleton domain makes a path present", r =>
{
    var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
    r.When(d.Is(dom.Of(p)));          // body: atoms joined over the tables
    r.Where(d, d => d.Count == 1);    // filter, run after the join on every match
    r.Then(present.Of(p));            // head: writes
});
```

- `When` adds body facts, which become atoms of a join. `Where` adds filters (a boolean term or a lambda over bound
  variables). Primitives in the body bind new variables (lets).
- `Then` adds head facts: relation rows, function outputs, constructor terms (made if absent), and equalities
  between classes (unions). `Make` binds a head variable to a term made by get-or-add. `Rewrite(left, right)` is
  shorthand for "where `t = left`, make `t = right`". `Ruleset.DefineBiRewrite` (and `Scope.DefineBiRewrite` for local
  rules) is egglog's `birewrite`: the two rewrites `{name}=>` and `{name}<=`, under the same conditions.
- **Subsumption** is egglog's. `Subsume(term)` and `Subsume(rows)` in a head, and `Rewrite(left, right, subsume: true)`
  (egglog's `rewrite ... :subsume`, which also subsumes the matched left side), mark a row of a relation or
  constructor as subsumed; so do the host's `Scope.Subsume` and `ScopeWriter.Subsume` (section 2.7). The row stays:
  lookups, `Check`, `Get` and `Rows` (whose `TableRow.Subsumed` says so) still find it, and a head that makes the term
  gets its class. But no rule body, query or split rule matches it any more (from the next iteration, when a rule
  subsumed it), and extraction skips it. A missing row is inserted subsumed, with its subterms made as usual, and the
  rule's later head items still run. Subsumption is sticky: merges and the unions that rewrite the row keep it, and
  nothing undoes it. What earlier matches derived from the row stays. As in egglog, every relation and constructor is
  subsumable and no function is; `TableOptions.Subsumable = false` opts a table out, an addition that stores no
  subsumption cell, so that a table that is never subsumed pays no cell per row and no filter per atom.
- **Deletion** is egglog's `delete`. `Delete(application)` and `Delete(rows)` in a head, and the host's `Scope.Delete`
  (section 2.7), delete the row of a key of a relation, a function or a constructor, if there is one, and nothing else:
  a deleted constructor term's class stays, with its other members and the rows that name it, and making the term again
  mints a new class. The keys are evaluated as head terms, made if absent. Every deletion of an iteration lands before
  its writes, so a row the same iteration writes stays, and a deletion is no change for saturation, as in egglog. A
  scope deletes only when the engine's retraction guard lets it (section 3.8): not with live descendants, which would
  not see the deletion, and not under a writable parent; so not in a case either. `Run` refuses, before any work, a rule
  that deletes where the scope may not, and in a run that splits, whose cases stay live until it ends; one defined on
  another thread during the run takes part only in the iterations where it may run. `graph.Contradiction` is not
  deleted.
- **Heads never read tables.** A function value used in a head must be matched in the body. This keeps every match a
  function of the body alone, which is what lets a child scope inherit its ancestors' progress (section 3.8).
- `Naive()` makes a rule match in full every time instead of semi-naively.
- A primitive or lambda that throws fails the rule: the run throws `RuleEvaluationException` naming the rule and the
  bindings, and the scope is faulted (section 2.12). A primitive may be partial, as egglog's `-?>` primitives are
  (`Primitive.IsPartial`): it has no result for some arguments. The built-ins are partial where egglog's are (checked
  `i64` arithmetic, division by zero, `log`, `sqrt`, `from-string`); `Primitive.CreatePartial` makes a user's. No result drops the match in a body let, a
  filter and a query; in a head and in a split's case assumptions the primitive's function throws, which fails the
  rule, where egglog panics.

A rule definition is checked and compiled at once (`RuleCompiler`): errors are `RuleDefinitionException`s naming the
rule. The compiler normalizes the builder's draft into a `RuleProgram`, an engine-independent form with registers,
atoms, body steps and head operations, and lowers it (`RuleLowering`) to an engine rule through the bridge's rule
builder. Every primitive, lambda and lattice becomes an engine external function. A `CompiledRule` keeps the program
and its engine variants: the semi-naive one, and on demand a naive twin that differential tests use.

**Licences.** A ruleset defined with `RulesetOptions.Licence = tag` makes every rule in it *erasing*: every atom over a
fact table must read the facts of the scope tagged `tag` (`r.In(tag)`), not the running scope's own. This is for
rules that delete or replace something on the strength of facts (a rewrite that removes a redundant conjunct of a
query predicate must not use the query's own conjuncts as evidence). A rule that may judge on its own scope's facts
says why with `ReadsOwnFacts(reason)`, and `Rule.ToString` shows the reason. `Erasing(tag)` makes one rule erasing in
a ruleset without a licence.

### 2.6 Schedules and runs

`Scope.Run(schedule, options, cancellationToken)` runs a schedule in a scope:

| Schedule | Meaning |
|---|---|
| `Run(rulesets)` | one iteration over the union of the rulesets' rules active in the scope; a `Ruleset` converts to it |
| `RunWith(scheduler, ruleset)` | one step of the ruleset under a scheduler: egglog's `step_rules_with_scheduler`, egglog-experimental's `run-with` |
| `Sequence(steps)` | the steps in order, once each |
| `Repeat(n, steps)` | up to `n` passes of the steps, stopping after a pass with no change in which no scheduler held work back (egglog's `can_stop`) |
| `Saturate(steps)` | passes of the steps until one changes nothing and no scheduler held work back |
| `Until(body, facts)` | the body, unless every ground fact already holds (egglog's `:until`) |
| `Split(rule, inCase)` | a case split step (section 2.9) |

One iteration runs every active rule against the same snapshot of the scope, then merges what they staged and rebuilds
(Jacobi-style, as egglog does). A rule is active in a scope when it belongs to a scheduled ruleset and is either
global or local to the scope or one of its ancestors (section 2.8).

A scheduler (`IRuleScheduler`, added with `EGraph.AddScheduler`) is egglog's `Scheduler` (`src/scheduler.rs`). It
chooses which of a rule's matches fire in a step of `RunWith` (`FilterMatches`); the others wait for the next step, and
whether the rule's query runs again is its answer. As egglog does, each rule is split in two over a worklist: a
semi-naive query rule over the rule's body that collects the values of the variables the head uses, and a naive action
rule over the worklist that runs the head and deletes the row it fired. The two run in two engine iterations, which the
report counts as one. A step changes the scope when a head does; one that changes nothing ends a `Saturate` only when
the scheduler can stop (`CanStop`; `RunReport.CanStop`), as egglog-experimental's `saturate`, the only saturation over
scheduler steps, stops on `can_stop`, and so do the passes of a `Repeat`, as core egglog's `Repeat` stops on `can_stop`.
Upstream never combines the two: egglog-experimental's `repeat` always runs its passes, and core egglog's schedules have
no scheduler steps; a `Repeat` of `RunWith` steps is this library's. `BackOffScheduler` is egglog-experimental's
back-off scheduler (release 1.0.0, for egglog 3.0.0), without the node limit of its later versions. Each scope steps its
own clone of the scheduler, with its own matches left and its own worklists, which are context-private tables
(`FunctionConfig.ContextPrivate`): bookkeeping that descendants never read, that moves no `Version`, and that a scope
may delete from where a rule may not. A child starts from what its parent held when the child was made, as egglog's
`push` clones the e-graph with its schedulers: the matches its parent left wait in the child, whose queries continue
from its parent's progress (the query rules never seed a watermark from an ancestor's; the step sets the child's own).
This is where egglog's defect 10 goes ([`history/egglog-engine-map.md`](history/egglog-engine-map.md)): egglog's clone
and original share part of the buffers. Its defect 9 goes too: the matches offered hold classes as the scope's canonical
members, where egglog's left matches keep the ids they were found with. As at 5e655cae, the query matches subsumed rows
too, and a rule whose head uses no variable of its body cannot be scheduled (egglog panics; 3.0.0 fires it). A scheduler
runs on the scope's thread under the engine-call marker, so it cannot call into a graph; one that throws faults the
scope.

`RunOptions` bounds a run: `IterationLimit` (default 10,000 engine iterations, counted in every scope of the run,
cases included), `TimeLimit`, `RowLimit` (rows in the own layers of the scope and its live cases), and for splits
`CaseLimit`, `SplitDepthLimit` and `Lift` limits. Limits and cancellation are checked before every iteration and
every split step; an iteration or a lift cannot be interrupted, so a run overshoots by at most one of them. Reaching a
limit is not an error: the run stops soundly with fewer facts, and `RunReport.StopReason` says why.

There is no iteration budget per case. To bound each case of a split, pass `Repeat(n, rules)` as the in-case
schedule: `Split(rule, Repeat(n, horn, lattice))`. Under `Saturate` as the in-case schedule, one case that does not
terminate uses up the run's `IterationLimit`, and if it does so before the split step lifts, the scope gets nothing
from any of the step's cases. `Repeat(n, Saturate(...))` bounds nothing, since each of its passes is a full
saturation. The bound applies to each execution of the in-case schedule, not to the case over the run: a case runs it
before the step's lift, again when the step deepens after the lift, and again at each later execution of the step. A
case cut off this way stays live, its facts hold under its assumption, and the lift stays sound; a run that otherwise
ends normally reports `Completed`. A host that must know a case was cut off finds, in the steps (`RunReport.Steps`,
which stays empty unless `RunOptions.RecordSteps` is set, or an `Observer` that sees each step), an execution of the
in-case schedule that ran all `n` passes in that case and still changed it in the last. A host sizes `n` from the
passes its cases take when they do terminate, read from the same steps.

`RunReport` gives the iterations, whether the run saturated and whether it changed the scope, search, merge and
rebuild times, per-rule statistics (matches, search time, iterations with matches, and the plan when
`EGraphOptions.RecordPlans` is set), and the split counts. With `RecordSteps` it keeps one `RunStep` per iteration,
mirror sync, split step and lift; an `Observer` receives each step as it happens and may read the scopes of the run.
There are no proofs or per-row explanations, and no record of which rule wrote a row. Two routes find where a fact came
from:

- **The first matching step.** Run with `RecordSteps` and `EGraphOptions.RecordPlans`, find the first step whose
  `Rules` lists a rule that derives the fact, and read the rule (`Rule.ToString()`), the facts it reads
  (`Scope.Rows`) and its plan (`RuleStatistics.Plan`).
- **The observer diff.** An `Observer` snapshots `Scope.Rows` of every table in the step's scope after each step and
  compares the snapshot with the previous one of the same scope. Each row that appeared, and each lattice value that
  changed, is then tied to the step that made it, and the step's `Rules` are its candidate rules. Three caveats: a
  case's first snapshot is compared with its parent's, since the case starts from the parent's facts; changes seen in
  the split scope after a split step or a lift came from the cases and are set aside as lifted, not derived there; and
  the candidates are every rule that matched in the step, not only those whose head writes the row's table, so the
  list can be longer than the rules that could have written the row.

Neither route says that leaving a rule out would remove the fact: a fact can come from a chain of rules, or from two
rules at once. To decide that, run again without the rule's ruleset, on a fresh graph, and compare.

### 2.7 Host writes, reads and queries

A scope is written through `Assume(facts)`, `Make(term)` (returns the term's class), `Union(a, b)`, `Subsume(term)`
and `Subsume(rows)`, and `Delete(application)` and `Delete(rows)` (section 2.5), which a `ScopeWriter` does not offer:
a deletion is a call of its own. Each call is one atomic batch; `Write(w => ...)` makes several calls one batch
through a `ScopeWriter`. A batch is planned first, reading only: it validates the facts, checks class origins, interns
constants, evaluates primitives and reads function values as egglog's top-level actions do (a read sees the batch's
earlier writes, merged by the function's lattice, as egglog's next command sees the last; the batch stages its unions
after its writes, so after a union it refuses a read of a function whose keys are classes, which a later call can make),
resolves existing terms, mints ids for new ones, keeps only the rows, subsumptions and unions that do not already hold,
and checks every conflict a function without a lattice would see, including conflicts a union in the batch would cause
through congruence. An exception discards the whole batch: a function read with no value, or a primitive with no result
or that throws, refuses it without faulting the scope. The batch is then staged and flushed: merged, rebuilt, and
visible when the call returns. A batch whose facts all hold already stages nothing, so it moves neither the scope's
`Version` nor any extractor.

Reads never fail for want of a rebuild: `Check(facts)`, `Get(term)`, `TryGet(term, out value)`, `Find(class)`,
`AreEqual(a, b)`, `CountRows(table)` and `Rows(table)`. They see subsumed rows, as egglog's `check` and lookups do, and
compute primitives as `check` does, over values, never interning: a result no write interned is a value all the same,
which `Get` returns and an equality compares as egglog's interned one, and which no row holds. `Version` counts the
effective changes the scope's own calls made to its own contents. `Serialize(options)` is egglog's `EGraph::serialize`:
the scope's view as the graph of the egraph-serialize crate (`SerializedGraph`, and its JSON from `ToJson`), which
egglog's visualizer reads; its departures are decision 23 of [`engine/decisions.md`](engine/decisions.md).

A query (`EGraph.DefineQuery`) is a conjunctive body with variables, defined once and run in any scope with `Any`,
`Count` or `ForEachMatch`, which delivers the distinct bindings in content order. Its atoms skip subsumed rows, as a
rule body's do, since a query is what a host loop matches with in place of a rule. A query is `IDisposable`: dispose a
query made for one request to free its engine rule. A query that fails throws `RuleEvaluationException` without faulting
the scope, since it wrote nothing.

### 2.8 Scopes, tags, local rules and scoped reads

Scopes form a tree under `EGraph.Root`. `CreateChild(tag, options)` makes a child that starts as a copy of its parent
and costs in proportion to what it touches: it stores only its own writes, and reads everything else from its
ancestors. A child sees what its ancestors write later, too; it catches up lazily at its next call.

```
root                      empty and frozen, or a shared theory
 +- schema#1  (schema)    frozen once its facts and rules are settled
 |   +- query#1 (query)   a writable child per question, disposed after it
 |   |   +- case#1        made by a split inside a run, disposed when the run ends
 |   +- query#2 (query)   on another thread: children of a frozen scope run concurrently
 +- schema#2  (schema)
```

- **Freeze.** `Freeze()` makes a scope read-only for good. Its parent must be frozen (the root has none). Freezing
  rebuilds the scope and, below the root, plans the rules its descendants can run on its statistics and builds the
  indexes those plans probe. Reads, queries and extractors of a frozen scope are thread-safe, and each child of a frozen
  scope is independent of its siblings, so a frozen scope is a base that many threads can query and extend at once.
- **Freeze before creating children.** A table keeps the rows that later writes replaced until it compacts, which it
  does when they outnumber the rows still current and number more than 16, and when its scope freezes. Compaction
  renumbers rows, and live descendants hold the numbers of the rows they have seen, so the tables of a scope with live
  descendants do not compact; a disposed child stays live until its own children are disposed. Once the last
  descendant is disposed, the tables compact again at the next write that calls for it. Results never depend on it,
  but memory and full scans do. So freeze a scope before creating children of it, and do not hold descendants of a
  large writable scope across heavy work in it. A scope frozen with live descendants keeps its replaced rows for good,
  and `Scope.CompactionsBlockedAtFreeze` counts the tables that kept them. A split run at a writable scope holds its
  cases until the top-level run ends (section 2.9), so the scope keeps its replaced rows for the length of the run, and
  a large writable scope or root that splits pays for it in memory.
- **Tags.** A `ScopeTag` (`EGraph.DefineScopeTag`) labels scopes. Tags belong to one graph and are absolute: a tag
  resolves, from a scope, to the nearest ancestor-or-self that carries it. The root carries `EGraph.RootTag`.
- **Local rules.** `Scope.DefineRule(ruleset, name, define)` defines a rule that is active only in that scope and its
  descendants, for the rulesets a schedule runs. Local rules may be added until the scope is frozen, and are freed when
  it is released. A schema can therefore carry rules compiled from its own declarations.
- **Scoped reads.** `table.In(tag)` (and `Fact.In(tag, facts)`) reads a table as the scope tagged `tag` sees it, from a
  rule running in a descendant. Only value-only tables (every column a value type, never a sort) can be read this way,
  so no class id crosses a scope boundary inside a rule. A scoped read compiles to an ordinary atom over a hidden
  *mirror table* `T@tag`, which copy rules keep equal to the tagged scope's own view of `T`. The copy rules run in the
  tagged scope just before anything reads the mirror, when that scope is writable and has changed since its last sync,
  and once more when it freezes. Creating a mirror is a table definition, and is refused while a live frozen scope
  carries the tag (its mirror would stay empty there). A subsumable relation's mirror is subsumable, and a second copy
  rule subsumes in it the rows the tagged scope subsumed, so scoped atoms skip exactly the rows the tagged scope's own
  atoms skip. A deletion in the tagged scope deletes the mirror's row too: a host deletion in the same batch, a rule's
  after the iteration, which removes every mirror row the table no longer holds as it is (its key gone, or written
  again with another value or subsumption), for the next sync to copy afresh. A scope with live descendants deletes
  nothing, so a mirror's readers below the tagged scope never miss a deletion.
- **Subsumption in scopes.** A subsumption is a sticky change of the row's subsumption cell, written in the scope that
  makes it, like any other write (decision 1 in [`engine/decisions.md`](engine/decisions.md)). A child's shadows its
  ancestors' row: the child and its descendants stop matching the row, while the ancestors and the siblings still match
  it. An ancestor's reaches its descendants when they catch up, and a frozen scope's stays for its children.
- **Queries on frozen scopes** run in a transient child created for the call and disposed after it, so the frozen
  scope stays read-only. In a writable scope a query runs as an iteration that writes nothing.

### 2.9 Case splits and lift

A case split reasons by cases inside a scope: if a key can take only the values in a set, open one case per value,
run rules in each, and keep in the scope what holds in every case. Cases that reach a contradiction are dropped, since
they have no model.

- **Split rules.** `DefineDomainSplit(name, domain, guards, caseTag)` splits, wherever every guard relation holds of a
  key `k` and the lattice-valued function `domain(k)` has more than one element, into one case per element `v`, each
  assuming `domain(k) = {v}`. `DefineSplit(name, define)` takes any body, a key, a set variable to split on
  (`Cases`), and the assumptions each case makes. Which keys are worth splitting is part of the split rule's body:
  more guards split less, and never unsoundly. The coverage obligation is the theory's: the cases must cover the
  scope, which a domain that only narrows guarantees.
- **Split steps.** `Schedule.Split(rule, inCase)` is a step of a run. Where the split rule's body matches, it opens
  the cases, runs `inCase` in each, and lifts. Cases live until the top-level run ends: a later execution of the step
  skips a key that a live split already covers (in the scope or an ancestor), runs `inCase` again in the cases it
  opened so they take in the scope's new facts, and lifts again. Nested split steps in `inCase` split cases further,
  after the lift of the level above (lift before deepening), up to `SplitDepthLimit`; a split with more than
  `CaseLimit` cases is skipped and counted. A split step is best placed after the facts its rule reads have settled:
  `Saturate(Saturate(facts...), Split(rule, inCase), rewrites...)`.
- **Host-driven cases.** `Scope.OpenCases()` returns a `CaseSet`. `Add()` makes a case, the host writes its
  assumptions and runs schedules in it, and `Lift()` writes into the parent what holds in every live case. Disposing the
  set disposes its cases.
- **What a lift writes.** Equalities between the parent's classes that hold in every live case (the meet of the cases'
  partitions); relation rows present in every live case; constructor terms over the parent's classes; and function
  values, joined over the cases with the lattice's case join (or, for a function without a lattice, only where every
  case agrees). Tables with `Lift = false` are left out. A row arrives in the parent not subsumed, unless the parent
  subsumed it: a lift writes what every case holds, not what the cases chose to stop matching. A lift runs in rounds
  until a round writes nothing, because a term lifted in one round can make the next round's facts expressible over the
  parent's classes. If every case is contradictory, the lift writes the contradiction into the parent.
- **Guarded terms by hand.** A lift writes only what holds in every case, so it builds no term that differs by case. A
  host that needs one for a few if-then-else guards lifts it by hand: it opens a child per distinct condition and
  assumes the condition, runs the theory there, and extracts the then-branch's best term; it then `Make`s that term in
  the parent inside the theory's if-then-else constructor and `Union`s it with the original term.
- **Contradicted cases.** A case whose rules derive `Contradiction` stops: it answers `IsContradictory` and identity
  members, `Run` returns at once with `StopReason = Contradiction`, and other reads and writes throw
  `ScopeContradictedException`. Scopes beneath a stopped case answer as it does.

### 2.10 Extraction

`Scope.Extract(class, costs)` returns the cheapest term of a class as an `ExtractedTerm` (head, arguments, child
terms, cost, and an s-expression `ToString`). `Scope.ExtractVariants(class, count, costs)` returns up to `count`
variants of a class, egglog's `(extract x n)`: one node of the class each, with its arguments' cheapest terms, cheapest
first, the node `Extract` chooses leading; its departures from egglog are decision 21 of
[`engine/decisions.md`](engine/decisions.md). `CreateExtractor(costs)` returns an `Extractor` that answers several
questions over one state of the scope. A `CostModel` gives each constructor a head cost (its declared cost by
default), optionally a per-row cost function, and value costs (1 by default); a term costs the sum over its nodes.
Unextractable heads and subsumed rows are skipped.

Costs are computed with Knuth's generalization of Dijkstra's algorithm over the classes reachable from the ones asked
about, so the chosen terms are always finite, even with zero costs. Ties are broken by content (cost, then
constructor declaration order, then arguments), so the result depends only on the scope's contents. An extractor
reads the scope as it was when it was made; using it after a write or run that changed the tables it reads throws, and
so does using it once a constructor defined after it has rows extraction may use.
Extractors over a frozen scope may run concurrently, one per thread.

### 2.11 Threading

- **Thread domains.** A writable scope and every scope created under it while it is writable form one thread domain.
  A domain is used by one thread at a time: every scope call (a batch, a run, a read, a query) holds the domain's
  guard for its whole length, and a second thread gets `InvalidOperationException` at once instead of waiting or
  interleaving. Freezing a scope gives each child subtree its own domain. Each child of a frozen scope has a domain of
  its own, so siblings under a frozen scope run concurrently.
- **Frozen scopes** are immutable, and their reads, queries and extractors are safe from any number of threads.
- **Parallelism inside one operation** is opt-in. `EGraphOptions.DegreeOfParallelism` sets the root's (default 1:
  serial and deterministic, extraction ties included); with more, the facts are the same but fresh ids, and so
  extraction ties, may differ between runs. `ScopeOptions.DegreeOfParallelism` sets one scope's; children of a frozen
  scope are serial unless it is set. Concurrency across scopes is the caller's: children of a frozen scope run on
  whatever threads the host calls them from.
- **User delegates** (primitives, lambdas, lattices, filters, cost functions) run inside engine calls, possibly on
  several threads at once. They must be pure and must not call into any graph; such a call throws.
- A split's cases run on their parent's thread.

### 2.12 Lifetimes and failures

- `Scope.Dispose()` never throws. It makes the scope unusable at once (`ObjectDisposedException` on new calls); its
  calls already in progress complete, and its class values stay usable in its live descendants (section 2.2). A run on
  the scope whose split step opens cases directly under it after the disposal is refused there with
  `ObjectDisposedException`, after earlier steps may have written; a split step that opens no case is not refused. Its
  engine state and local rules are released when it has no live children (transient query children included) and no
  call is in progress in its domain on another thread; whichever thread removes the last obstacle releases it.
  Disposing the root does nothing. So a frozen schema can be retired while queries beneath it finish.
- A `RuleEvaluationException` from a run or a write faults the scope: `Failure` keeps the exception, and every later
  call except `Dispose` throws `InvalidOperationException` with it as the inner exception. A faulted root makes the
  graph unusable. There is no rollback. Lattice merges that throw while a call catches its scope up with an ancestor's
  writes fault the scope too.
- Refusals made before anything changes (`ArgumentException`, `FactConflictException`, a definition error) do not
  fault.
- A disposed scope gives back its own state, but not what it drew from the graph:

  | State | Left behind after the scope is released |
  |---|---|
  | Its rows, layers, union-find overlay, journal and cursors | nothing |
  | Its local rules, their engine rules, variants and call-site external functions | nothing; freed rule and external-function ids are reused |
  | Values it interned: constants, primitive and merge results, case assumptions, joins | all of them, in the graph's pools (section 2.2) |
  | Ids it reserved (one block of 16 for its first term, then blocks doubling up to 4,096) | all of them: ids are never handed out again |
  | Epochs its writes, run iterations, rebuild passes and cases took | all of them: the clock never goes back |
  | Scope tags and the mirror tables of scoped reads | all of them: they are graph definitions, so a host defines tags with its theory, not per request |

- Ids and epochs are finite: about 2.1 billion ids (`Array.MaxLength`, the largest id a union-find holds) and about 4.3
  billion epochs, shared by every scope of the graph. `EGraph.Usage` returns a `GraphUsage`: `InternedValues`,
  `IdsReserved` against `IdLimit`, `Epochs` against `EpochLimit`, and `FractionUsed`, the larger share. A workload
  that splits usually runs out of epochs first. When either runs out, every call that needs one throws
  `OverflowException`: a write that makes a term or merges, a run whose rule makes a term or that needs another
  iteration, pass or case, `CreateChild`, `CaseSet.Add` and `CaseSet.Lift`, a freeze that must catch up, a query read
  (`Any`, `Count` and `ForEachMatch` run the query as a rule, which takes an epoch even on a frozen scope), and any
  other read of a writable scope that must catch up first (`Check`, `TryGet`, `AreEqual`, `CountRows` and the rest,
  when an ancestor changed since the scope last caught up, or its own rebuild ran out before it finished). A frozen
  scope, and a writable one with nothing to catch up, reads facts and terms without an epoch.
- Running out is not a fault, and the scope can be disposed. A write that ran out before its merge is dropped, so the
  scope reads as before. One that ran out in the rebuild after its merge stays written, with the rebuild's finished
  passes committed, so the scope reads it without an epoch unless that rebuild left work undone. A lift round that ran
  out before its merge writes nothing. Every later call that needs an id or an epoch throws the same way, and once the
  epochs are out the graph creates no more children. A long-lived host therefore retires a graph before
  `FractionUsed` reaches 1: it defines a new graph from the same definitions, sends new work to it, and lets the old
  one go once its last scope is disposed. The library has no helper for this; the host owns its definitions.

### 2.13 Differences from egglog

The library offers egglog's core (tables with functional dependencies and merges, union-find with congruence closure,
semi-naive Datalog rules, schedules, primitives, lattices and extraction) and adds scopes and case splits. Fidelity with
egglog comes first, so each departure below is to be removed, or confined to the additions, as the roadmap's "Fidelity
with egglog" lists:

1. Queries and split rules skip subsumed rows as rule bodies do, where egglog's `check` sees them; the other
   departures of subsumption are listed in decision 20 of [`engine/decisions.md`](engine/decisions.md).
2. Sets hold values only, and are values themselves, not containers of classes.
3. Rule heads never read tables, and functions have no defaults: a lookup without a row fails.
4. No `.egg` frontend: programs are written in C#. egglog's `push` and `pop` become child scopes.
5. No proofs or explanations, and no rule callbacks. Host code that must react to matches runs a host loop instead: it
   runs a schedule, reads the matches with a query's `ForEachMatch`, writes through `Scope.Write`, and repeats until the
   scope's `Version` stops changing. Queries match naively, so each pass matches in full.
6. Relations are unit-valued functions rather than constructors over a fresh sort; this is invisible to users.

---

## 3. The engine

The engine reimplements egglog's Rust crates `core-relations` (tables, indexes, the Free Join planner and executor, the
database), `union-find`, `numeric-id` and `concurrency`, the bridge `egglog-bridge` (functions, rules, semi-naive
evaluation, rebuilding) and egglog's extractor, file by file, keeping their algorithms, invariants, fast paths and
complexity. Where egglog has a defect, the port fixes it and the comment cites the Rust file and line; the defects are
listed under "egglog defects: what the port does instead" in
[`history/egglog-engine-map.md`](history/egglog-engine-map.md). On top of that port sit colored contexts, which egglog
does not have.

The engine is written for speed: no allocation per row, match or probe on hot paths; no interfaces, virtual calls or
delegates in per-row loops (struct-constrained generic sinks stand in for Rust's monomorphized closures); pooled
buffers; and bounds checks dropped only after an explicit range check. The rules are in
[`engine/conventions.md`](engine/conventions.md).

### 3.1 Values and ids

A table cell is a `uint` (`Value`). What it means is decided by its column: a class id, a base value (inline or
interned, `BaseValues` with one codec per type, egglog's encodings exactly), a container id, or a timestamp.
`uint.MaxValue` in a row's first cell marks the row deleted. Ids of tables, columns, variables, atoms, rules and so on
are record structs in `Relations/Ids`, held in `DenseIdMap` and `IdVec` collections. Hashing is egglog's FxHash, and
shard selection takes bits below the top seven of the hash.

### 3.2 Tables

`SortedWritesTable` is the table behind every function: egglog's table of the same name.

- **Layout.** Rows are row-major in one append-only buffer. A sharded SwissTable maps each live key to its single live
  row: the key columns form a functional dependency. A deleted or superseded row becomes a tombstone until the table
  compacts (when more than half its rows are tombstones).
- **Writes are staged.** Writers stage rows and removals into mutation buffers, pre-sharded by key hash, possibly from
  many threads. `Merge` applies the removals, then the inserts. A new key is appended; an existing key goes through the
  table's `MergeFunction`, which either returns the new row (appended, the old one tombstoned) or keeps the current row
  (nothing written, so no change is reported and saturation can end).
- **Timestamps.** Each function table is sorted by its timestamp column: stamps are nondecreasing in row order, and a
  run-length index over them turns "rows stamped at or after `W`" into a dense row range in O(log runs). This is what
  makes semi-naive evaluation cheap.
- **Versions.** A table's minor version is its physical row count, so the rows appended since a version are a suffix;
  compaction and clearing start a new generation, which invalidates row ids and every index built on them.
- **Visibility.** Every path that yields rows to a caller skips tombstones (and, in a child context, hidden rows), on
  scans with and without constraints, index builds, refinements, probes and point lookups. egglog misses some of
  these.
- **Parallelism.** Above 20,000 rows, deletes, inserts and compaction run one task per shard. The parallel insert
  stores the merge's output and reports only real changes.

`DisplacedTable` exposes the union-find as a table (section 3.4), and `Table` is the abstract base the catalog and
the planner hold tables by. Scans dispatch on the table kind once per call and then run a non-virtual generic loop.

### 3.3 The database

`Database` owns a catalog of tables with their cached indexes, the merge-dependency strata, the list of tables with
staged writes, the counters (ids, the clock) and the external functions. `MergeAll` merges every notified table, in
dependency-stratum order, and repeats while merges stage more writes (a constructor merge stages unions into the
union-find). Merge functions and external functions report failures through the `ExecutionState`, and the database
keeps the first failure for the caller. Above 10,000 rows in total, database-level operations go parallel.

Indexes (`ColumnIndex`, `TupleIndex`) are built lazily per table and per column set, cached in the catalog, and
refreshed incrementally from the rows appended since their version, or rebuilt in full after a new generation.

### 3.4 Union-find and rebuilding

The union-find is egglog's: `DisplacedUnionFind` keeps the smaller id of two classes as the canonical one, and
`DisplacedTable` exposes one logical row per effective union (`[displaced id, its canonical id, timestamp]`) whose
canonical column is computed on read. To union two ids, stage a row into it; redundant unions are ignored.

After unions, the e-graph is rebuilt to restore congruence: every function table with id columns rewrites the rows
that mention ids displaced since its last rebuild, which may merge rows that become equal, which may stage more
unions, until nothing changes. A table takes the incremental path, probing a rebuild index for the rows that mention
newly displaced ids, when the delta is small against its size, and a full scan otherwise. The bridge alternates this
with refreshing container values until neither changes anything.

### 3.5 Queries: planning and execution

A rule's left-hand side is a conjunctive query over table atoms. The planner (`QueryPlanner`, egglog's
`free_join/plan.rs`) turns it into an immutable plan template in two phases:

1. An optional tree decomposition: min-fill variable elimination produces bags, redundant bags are merged, and the rest
   are chained and evaluated Yannakakis-style, each bag materialized by its message variables.
2. Join planning inside each bag with one of three strategies: Generic Join (`Gj`, one variable per stage,
   intersecting every column that holds it), Free Join by size (`PureSize`), or Free Join over a greedy minimum cover of
   the variables (`MinCover`). The bridge uses `Gj` for rules with more than one atom and `MinCover` for single-atom
   and rebuild rules, as egglog's bridge does.

Planning is deterministic for given statistics. A rule is planned once, on the statistics of the context it first runs
in, or of the frozen context that pre-plans it.

The executor (`JoinRun`, egglog's `free_join/execute.rs`) runs a rule set against one snapshot of the tables. Each
rule's atoms start as trie nodes over their row sets narrowed by the plan's header constraints (timestamp ranges,
constants); each stage either intersects the columns that hold a variable or scans a cover atom and probes the others
through indexes, and the stage order can be adjusted at run time by the sizes it meets. Matches go into batches of the
rule's action program (`Bindings`, `Instr`): lookups, inserts, unions, external calls, assertions. Actions stage
writes; the writes become visible at the merge that ends the run. Serially, the rules run in order on one thread and
the run is deterministic; in parallel, each rule is a task, frames fork into morsels at join positions 0 and 1, and full
action batches run as tasks.

### 3.6 The bridge: functions, rules and iterations

The bridge (`Relations/Bridge`, egglog-bridge) lays an e-graph out on the database: the union-find is table 0, fresh
ids come from counter 0 and timestamps from the clock, counter 1. Each function is a `SortedWritesTable` laid out as
`[keys..., output, timestamp, subsumed?]`, sorted by the timestamp, rebuilding its id-typed columns, with a merge
built from the function's `MergeFn` (assert equal, union ids, a lattice primitive, and so on) and a default for
lookups that miss (fail, a fresh id, a constant).

`EGraphRuleBuilder` records a rule: atoms over functions, left-hand-side primitives, and right-hand-side actions.
**Semi-naive evaluation**: a rule with `n` atoms runs as `n` variants; in variant `i`, atom `i` reads only rows stamped
at or after the rule's watermark `W` (its last successful run), the atoms before it only rows stamped before `W`, and
the atoms after it every row. Each new match is then found exactly once per iteration. A naive rule runs one variant
over every row.

`RunRules` is one iteration: build the rule set, run it, merge, rebuild, and report (`IterationReport`: whether the
rules changed anything, whether the rebuild did, whether a case stopped, and the timings). Every row written in a run,
a flush or a rebuild pass is stamped with the clock's value when it started, and the clock advances after it. A
conflict in a function without a lattice, a failed lookup, or a failing primitive calls a panic function, which
records the failure and stops the run early; the context then throws `PanicException` after merging what it wrote.
A rule's progress is committed only after a run that recorded no failure.

### 3.7 Extraction

`Relations/Extraction` ports egglog's extractor with two changes: costs come from Knuth's algorithm instead of a
Bellman-Ford fixpoint, and ties are broken by content instead of scan order. The on-demand form, which the library
uses, discovers only the classes reachable from what it is asked about, finding each class's nodes through the cached
index on its constructors' output columns, and extends its graph as later questions reach further, replaying earlier
decisions so that every answer equals a from-scratch run's. Over a context, a class's nodes are the rows the context
sees. An `ExtractionWatch` records the versions of what the extractor read, so a stale extractor throws instead of
answering.

### 3.8 Colored contexts

A colored context is a view of a store: its parent's view plus what was asserted in it. The root context is egglog's
database, unchanged; a child context is a copy of its parent that stores only what differs and costs in proportion to
what it touches.

```
Store
  schema (every table, added at any time), union-find table, id counter, clock
  Root Context ---------- Database (egglog's), DisplacedTable
    Child Context ------- Database of its own layers, UnionFindOverlay + DisplacedJournal,
      |                   HiddenSets over ancestor layers, cursors into its ancestors
      Grandchild ...
```

**Ownership.** Each context owns a `Database`. A child's database has its own catalog of layer tables, notification
list, pending queues and size estimate, and shares the store's counters, external functions, strata, containers and
base values by reference. It answers schema questions from the store's schema and planning questions from its view,
so rules are built, planned and run in a child exactly as at the root. Because the registry and the container
environments are shared, a value a child interns has one cell in every context and stays after the child is
disposed, as egglog's push and pop leave interned values behind; a container over base values interned from a child
takes its id from the shared counter, not from the child's blocks, so it is never local to the child. A disposed
child frees its own layers, overlay, journal and cursors once it has no live children; nothing else.

**Ids and time.** The store has one id counter and one clock. The root mints ids directly; a child mints from blocks
reserved on the shared counter (`IdBlockAllocator`), so ids are unique store-wide and "is this id local to the child"
is an exact, allocation-free test. A child that mints nothing reserves nothing; its first block is 16 ids, and each
later one twice the ids reserved so far, up to 4,096. A context captures the clock at the start of each run, rebuild
pass and write batch, stamps that batch's rows with it, and advances it at the end. Neither counter is reclaimed: a
query scope that makes one term uses 16 ids, and on the predicate test theory a plain query with its snapshot read
takes 12 to 21 epochs, depending on its shape, and a split query 21 + 9 x *cases* (`ScopeChurnTests` pins the
split counts).

Every id mint applies one bound, `UnionFind.MaxId` (`Array.MaxLength - 1`), the largest id a union-find array can
hold: the root's own mints (`Store.CheckId`), a child's blocks, a rule's mint at the root (`Database.IncrementCounter`
bounds the id counter there), and container ids. The clock's bound is `Value.StaleRep`, which is never an epoch
(`Store.CheckEpoch`). Past either, the mint or the clock read throws `OverflowException`, and every path leaves the
context consistent, so running out does not fault a child, unlike other exceptions that escape its runs and
rebuilds:

- host writes mint while they are planned, and a flush that runs out before its merge drops its staged host writes;
- a run reads the clock before its iteration, and a rebuild before each pass; a rebuild that runs out after its first
  pass first ends the passes that ran, as its loop ends (cursors committed, hidden sets sealed, views dropped), so the
  context is clean if they left nothing to do, and a later rebuild goes on from the journal mark otherwise;
- a rule search that runs out of ids discards everything it staged, in the layers it created and the ones it found;
- a lift round that fails while it stages drops what it staged;
- a child's constructor reads the clock before it is published.

`Store.IdsReserved` and `Store.EpochsUsed` report the counters, capped at `Store.IdLimit` and `Store.EpochLimit`; the
library's `EGraph.Usage` is built on them. A read of a writable child that must catch up (its parent changed, or its
own rebuild stopped short) takes an epoch for the catch-up's passes; a frozen context's reads take none, and a query,
which runs as a rule, always takes one.

**The layered union-find.** A child's union-find is a `UnionFindOverlay`, a class table over ids that are canonical in
its parent, with each class's member list, plus a `DisplacedJournal` of the ids that stopped being canonical in it.
`find_child = overlay ∘ find_parent`, one hash probe per level that has merged something. When the parent's union-find
grows, the child re-keys its overlay at its next call through one cursor into the parent's journal.

**Layered tables and hidden rows.** A child creates its layer of a table lazily, on its first write or hide of that
table; the layer is an ordinary single-shard `SortedWritesTable`. What a context sees of a table is a `TableView`: one
layer per depth from the root to the context, each with the set of rows that the context hides in it. A row is visible
when it is live in its layer and not hidden. The first layer from the top that holds a key decides that key's row.
When the child's own unions make a class lose (only losers are marked), the ancestor rows that mention the loser are
hidden and copied, canonicalized, into the child's layer. Hidden sets only grow, are sealed at the end of every merge,
and are never read unsealed.

**Catch-up.** A writable ancestor may keep writing after its children exist. Each child keeps cursors into its
ancestors' layers and journals, and at its next call (a write, a run, a read) catches up: it re-keys its union-find,
absorbs the ancestor rows appended since its cursors that mention its own losers, and then rebuilds its own rows.
`Context.Rebuild` is the one rebuild loop at every level: egglog's passes at the root; in a child, the catch-up in the
first pass, then per pass the rewrite of its own rows, the hide-and-copy of ancestor rows that name its new losers, and
the merge. Before the root is first shared (a child created from it, or a freeze), it settles: the rows written since
its last rebuild are canonicalized.

**Freeze and thread domains.** `Freeze` requires a frozen parent (or none). It rebuilds, compacts, builds rebuild and
plan indexes, seals staging, compresses or flattens the union-find, builds every view, and publishes. A context that is
not frozen shares one thread domain (`ThreadAffinityGuard`) with the descendants created under it; each child of a
frozen context gets its own. A second thread entering a domain is refused rather than made to wait.
`ConcurrentChildren` runs children of frozen contexts side by side within the graph's `ParallelismBudget`. Depth is
limited to 31.

**Compaction under live descendants.** A table compacts on egglog's trigger (more tombstones than half its physical
rows, and more than 16), and at freeze whenever it has a tombstone. Compaction renumbers rows and starts a new
generation, while every live descendant holds cursors and hidden sets over its ancestors' row ids of the current
generation, which `ContextTables.CheckGenerations` and the invariant checker verify. So a table does not compact while
its gate has live descendants: the owner's `DescendantGate` for a child's layer, the store root's for a shared root
table; context-private tables are never gated. `SortedWritesTable.CompactionsBlocked` counts the skips. The gate opens
when the last descendant is disposed, and the next merge that meets the trigger compacts. A frozen table never changes
again, so a context frozen with live descendants keeps the tombstones of every gated table that had one;
`Database.Freeze` counts those tables, which the context reports (`Context.CompactionsBlockedAtFreeze`) and the library
surfaces as `Scope.CompactionsBlockedAtFreeze`. A split's cases live until the top-level run ends, so a split run gates
its scope for the run's whole length.

**Queries in a child.** A rule set snapshots the views of the tables it reads when it is built (`ViewSnapshot`), and
the search reads only that snapshot. An atom's rows are a `LayeredSubset` over the layers of its view; each prober has
one piece per layer, drivers enumerate their pieces in turn, and probes of several pieces join their hits across
layers. A root's view has one layer, so a root run takes egglog's paths behind a handful of per-stage tests and
allocates nothing more. Layers that a rule task creates during a search are adopted at the next merge round; an empty
layer changes no view.

**Watermarks.** Semi-naive progress is kept per rule and per context (`RuleProgress`), with the rule's id constants as
they were when it last ran. Every id constant of a rule runs in its canonical form in the context that runs it, and a
watermark applies only while those forms are unchanged. A child's first run of a rule may inherit its watermark from an
ancestor's progress (the minimum of the ancestor's watermark and the creation time of the ancestor's child on the path),
provided the rule's actions read no table whose contents can change; then the child matches only what is new to it. A
rule that subsumes reads the row's subsumption cell, so it never inherits. Inheritance changes the work, not the result,
which the tests check, except after a deletion, in the child or in an ancestor before the child's first run: an
inheriting rule, as egglog's semi-naive rules after `push`, does not derive again what the deletion removed, while a
rule that matches in full there (a naive rule, or one that subsumes and so cannot inherit) may.

**Retraction.** The engine supports deletion under strict rules ("Deletion across contexts" in
[`engine/decisions.md`](engine/decisions.md)): a context with live descendants may not retract rows, and a child may
delete its own rows and hide visible ancestor rows. A merge that is not a lattice merge may be used only at the root.
The library's deletion (section 2.5) is the engine's, and the library refuses before any work what the guard would
refuse. Subsumption is not a retraction: a sticky value change of the row's subsumption cell, merged like an output
(decision 1).

**Contradiction.** A context created as a case stops when the contradiction relation gets a row: its stop flag is set,
the rebuild loop checks it, and a stopped case is skipped by the lift and disposed.

**What the root pays.** A few per-call, per-batch, per-probe and per-refinement branches, freeze-time and
child-creation work, and one cheap path compression per merge that no result can observe. Nothing is added per scanned
row, per staged row or per find, and the root's allocation probes pin this.

### 3.9 Cases and lift

`Relations/Cases` holds the engine side of case splits.

- `MemberMap`: for a case, each class it merged over its parent's ids (its group root) with those members. Built from
  the overlay's member lists and cached by the overlay's version.
- `Lifter`: lifts what holds in every live case into the parent, in rounds. Each round brings the parent and the cases
  up to date, drops contradictory cases, takes the live case with the fewest groups as the seed, stages the meet of the
  partitions (the unions every case agrees on) and the facts of every table in the lift spec (`LiftSpec`), and flushes
  them into the parent at one epoch. To be complete, it also looks up ancestor rows by each merged class's winner, since
  with loser-only marking those rows are never copied into a case. A memo (`LiftSignature`) makes a lift over cases
  and a parent that have not changed return at once.
- `CaseSet`: the cases of one split, created as cases, run, lifted and disposed together.
- `SplitDriver`: one step of a split schedule. It asks its sources (`ISplitSource`) for split requests, skips requests a
  live record in the scope or an ancestor covers and requests over the limits, opens the cases, runs the in-case
  schedule (`ICaseSchedule`) in each with nested splits disabled and lifts (phase A), then runs it again with nested
  splits enabled and lifts again if that changed something (phase B). Records live until the driver disposes them.

### 3.10 Parallelism

Parallelism follows egglog's thresholds (table operations above 20,000 rows, rebuilds above 10,000, database-level
operations above 10,000) with a configurable degree of parallelism in place of rayon's pool (`ParallelHeuristics`),
using `Parallel.For` and the thread pool. A graph's degree of parallelism also fixes its tables' shard counts. Children
of frozen contexts add concurrency across contexts. The engine's `ConcurrentChildren` runs several of them at once
within the graph's `ParallelismBudget`, so that they do not oversubscribe the machine; the library leaves that
concurrency to its caller's threads.

---

## 4. How the library sits on the engine

| Library | Engine |
|---|---|
| `EGraph` | one bridge `Relations.EGraph` on a `Store` |
| `Scope` | one `EGraphContext` (the root included), reached through `ContextEngineScope`, plus a `ScopeGuard` for its thread domain and a `ScopeOrigin` for its class values |
| `ClassId` | a class id cell plus the `ScopeOrigin` of the scope that returned it |
| `Relation<...>` | a bridge function `[keys..., unit]`, default the unit value, merge assert-equal |
| `Function<..., TOut>` | a bridge function `[keys..., TOut]`, no default, merge the lattice (flagged as a lattice merge, so children may write it) or assert-equal |
| `Constructor<..., TSort>` | a bridge function `[keys..., id]`, default a fresh id, merge union |
| A rule | a `CompiledRule` (`RuleProgram`) and its engine variants, each one `EGraphRuleBuilder` rule |
| Primitives, lambdas, lattices, query collectors | engine external functions registered by the library |
| `ScopeTag`, `In(tag)` | library labels; mirror tables and copy rules are ordinary engine functions and rules |
| One iteration | `EGraphContext.RunRules(rules)` and its `IterationReport` |
| Host writes | `StageRow` (subsumed or not), `StageUnion`, `FreshId`, then `FlushUpdates`, which canonicalizes and rebuilds |
| Host reads | `TryLookup`, `Find`, `TableSize`, `ForEach` on the context |
| Queries | a naive engine rule whose only action is a collector; in a frozen scope, run in a transient child |
| `Freeze`, `CreateChild`, `Dispose` | the context's, with the plans the library asks the freeze to make |
| Split steps, `CaseSet` | `SplitDriver` with a `RuleSplitSource` per split rule and a `ScheduleCaseRunner`; `CaseSet` and `Lifter` with the graph's `LiftSpec` |
| Extraction | the on-demand `Extractor` over the context, through the graph's `ExtractionCatalog` |

The path of one `Scope.Run`:

```
Scope.Run(schedule)                     enters the scope's ScopeGuard for the whole run
  ScheduleExecutor                      walks the schedule; checks limits and cancellation
    for each Run(rulesets) node:
      MirrorSync                        syncs the mirrors the iteration's rules read
      rules active in the scope         global rules of the rulesets + local rules of the scope and its ancestors
      EGraphContext.RunRules            catch-up, then one iteration:
        RuleSetBuilder / QueryPlanner     plan each variant (once per rule), snapshot the views
        Database.RunRuleSet               join and run actions, staging writes
        Database.MergeAll                 merge in stratum order
        Context.Rebuild                   restore congruence (and, in a child, hide and copy)
      RunStep, statistics               recorded in the RunReport
    for each Split node:
      SplitDriver.Step                  find requests, open cases, run inCase in each, lift
```

A host write takes the same guard, plans its batch in `HostBatch`, stages it on the context and flushes; a read takes
the guard (on a writable scope), makes sure the scope is caught up, and reads the context's view.

---

## 5. Testing

Everything is in one test project, `src/Eggnog.Tests`, an xunit v3 executable with about 12,700 tests that runs in
10-15 minutes per target framework. The engine's tests are under `Relations/` (namespace `Eggnog.Relations.Tests`),
in folders that mirror the engine's; the library's are in the other folders (namespaces `Eggnog.Tests.*`). Both are run
on net8.0 and net10.0 before a change is merged. CI runs the net10.0 build on Windows x64, Linux x64 and arm64, and
macOS x64 and arm64, and the net8.0 build on Windows x64 and Linux x64. It also builds the benchmarks, and publishes
`src/Eggnog.AotCheck` with NativeAOT: the library declares itself AOT compatible, its trimming and AOT analyzer
warnings are errors, and the publish fails on any warning the AOT compiler reports for the whole library.

```
dotnet build src/Eggnog.Tests
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe                               all tests
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe -namespace Eggnog.Relations.Tests  the engine's
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe -namespace "Eggnog.Tests.*"        the library's
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe -class <FullName>                  one class
```

The engine's correctness rests on several independent kinds of evidence.

### 5.1 Ported tests and reference models

Each engine module carries the Rust unit tests of the module it ports, rewritten as xunit tests, plus property tests
against a simple reference implementation where the module has nontrivial invariants: union-find against a naive
partition, tables against a dictionary with fold semantics, indexes against a naive group-by, subsets against a sorted
set, the planner against golden stage lists from egglog's experiments, and the database's rebuild against a naive
congruence closure.

### 5.2 Differential tests

Random programs run two or three ways, and every observation must agree, modulo renaming of class ids where ids may
differ. A failing program prints itself (several harnesses shrink it first), and each family's seed count can be raised
with an environment variable (`EGGNOG_SEEDS`, `EGGNOG_CONTEXT_SEEDS` and others, named in each class's
documentation).

| Level | Programs | Compared against |
|---|---|---|
| Database (`Relations/Differential`) | random tables, merges, unions, rebuilds, rule sets, clones, layered semi-naive steps | a reference model of tables, union-find and joins |
| Bridge (`Relations/BridgeDifferential`) | random bridge programs, egglog test programs rewritten by hand, programs that panic | three ways: semi-naive bridge, naive bridge, and a naive reference oracle |
| Contexts (`Relations/Contexts`) | random storage programs over context trees (writes, unions, retractions, rebuilds, freeze, dispose, ancestors writing after children exist) | a reference context that replays every assertion of the chain into a naive e-graph; a reference union-find context |
| Rules in contexts (`Relations/Colors`) | random rule programs over context trees | an operational reference of rules in contexts, in lockstep; the *context = copy* property (a context's saturated view equals a fresh root that replays the chain's writes); seeded against unseeded watermarks |
| Lift (`Relations/Cases`) | random parents and cases (unions, rows, lattice values, terms, rule runs, vacuous cases) | the specification: after a lift the parent holds exactly what held in every live case, no more and no less |
| Rule layer (`Differential`) | random rule-layer programs with host writes, runs and queries | three ways: semi-naive, naive twins, and a reference interpreter |
| Scopes (`Differential`) | random scope trees with tags, frozen prefixes, local rules and scoped reads | the same program's literal encoding at one root, with contexts as data |
| Splits (`Differential`) | random discriminated schemas and queries | the literal encoding with splits written as rules, and a model-based split oracle |

Inheritance and parallelism have their own equivalence tests: watermark inheritance on and off must give equal views
with no more matches, and serial and parallel runs must give the same facts. Concurrent children of a frozen context
(rule runs, storage programs, library queries) must each equal a serial replay; the default tests use fixed seeds, and
an opt-in soak (`EGGNOG_CONCURRENT_SOAK`) draws fresh seeds, child counts and thread counts.

### 5.3 Conformance programs

- egglog's test and web-demo programs that use `push` and `pop`, translated by hand into programs over child contexts,
  must give egglog's answers. There is no `.egg` reader; the translations are C#.

### 5.4 Invariant checkers

`EGraphContext.CheckInvariants` checks a context after every step of the context differentials: overlay keys canonical
in the parent, a child's equalities including its parent's, every hidden row with a reason, no child-local id in an
ancestor, cursors valid, lookups agreeing along every path. Debug builds also assert the engine's internal invariants
(`Debug.Assert`).

### 5.5 Allocation probes and counters

The performance rules are tested, not only benchmarked. Allocation probes measure `GC.GetAllocatedBytesForCurrentThread`
around hot operations and fail when a root run, a probe or a child operation allocates more than its budget; they run
on their own, after the parallel tests. The executor's counters (`ExecutorCounters`, compiled in but free unless the
process sets `EGGNOG_COUNTERS`) count the ancestor rows a child's run touches, so tests can assert that a child's cost
does not grow with the root. They also count the execution states cloned, by site, the mutation buffers those states
make and the allocations and growths of their buffer maps; the benchmark app's `--executor-counts` mode prints them
per `BasicMath` run. The test suite turns the counters on for itself; CI also runs it once with `EGGNOG_COUNTERS=0`, as
production runs.

### 5.6 Other suites

- The public surface test fails on any change to the public API that `Surface/PublicSurface.txt` does not record.
- Opt-in soaks and seed replays (about a dozen tests) are skipped unless their environment variable is set.
- `Readme/ReadmeExampleTests` runs the code examples of the READMEs verbatim.
- A theory of predicates over document paths (presence, forms, domains, types, numeric bounds, and the simplification
  of predicates) is a shared test fixture under `Predicates/`, in two encodings. `PredicateTheory` uses scopes as the
  contexts: frozen schema scopes, local rules compiled from a schema's declarations, and engine splits.
  `ContextArgumentTheory` is the same theory in one flat graph, with contexts as data arguments, inheritance written as
  rules and splits written as rules. Tests of splits, scopes, schedules, rendering, concurrency and diagnostics run
  over it, and the split differentials use the context-argument encoding as the oracle for the scoped one.

---

## 6. Benchmarks

`src/Eggnog.Benchmarks` is a console app that uses BenchmarkDotNet as a library with its in-process toolchain. It
holds ports of egglog's benchmarks for tables and union-find, ports of egglog-bridge's `math` and `ac` example
programs (with an example mode that prints what the Rust prints, for side-by-side comparison), a set of root-engine
benchmarks over scans, indexes, the executor, actions and rebuilds, and benchmarks of what a child context costs.
Benchmarks with a result to check check it, against a reference model or against known counts, so a broken engine
fails the run instead of producing a number. See [its README](../src/Eggnog.Benchmarks/README.md).
