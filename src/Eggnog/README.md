# Eggnog

Equality saturation for .NET, with egglog's capabilities and a typed C# API.

An e-graph stores many equivalent terms at once, grouped into classes of terms proven equal. Equality saturation
applies rewrite rules to it until nothing new can be derived, then extracts the cheapest equivalent term.
Eggnog offers the model of [egglog](https://github.com/egraphs-good/egglog), which combines this with Datalog: typed
relations, functions with lattice merges, constructors, rules, schedules, queries and extraction. It adds scopes
(child contexts that inherit their parent's contents, frozen bases shared across threads, per-query layers) and case
splits with lift. Its engine is a C# reimplementation of egglog's Rust engine.

Runs are serial and deterministic unless you ask for parallelism. The package targets .NET 8 and .NET 10, and is
compatible with trimming and NativeAOT.

## Installation

```
dotnet add package Eggnog
```

## Quick start

Declare a sort, the type of a class of equivalent terms, in its own file (`Expr.cs`):

```csharp
using Eggnog;

public readonly record struct Expr(ClassId Id) : ISort<Expr>
{
    public static Expr FromId(ClassId id) => new(id);
}
```

Then, in `Program.cs`, define constructors and rewrite rules, add a term, saturate, and extract:

```csharp
using Eggnog;

var graph = new EGraph();
graph.DefineSort<Expr>("Expr");
var num = graph.DefineConstructor<long, Expr>("Num");
var name = graph.DefineConstructor<string, Expr>("Var");
var add = graph.DefineConstructor<Expr, Expr, Expr>("Add");
var mul = graph.DefineConstructor<Expr, Expr, Expr>("Mul");

var rules = graph.DefineRuleset("arith");
rules.DefineRule("commute add", r =>
{
    var (a, b) = r.Vars<Expr, Expr>("a", "b");
    r.Rewrite(add.Of(a, b), add.Of(b, a));
});
rules.DefineRule("times one", r =>
{
    var a = r.Var<Expr>("a");
    r.Rewrite(mul.Of(a, num.Of(1L)), a);
});
rules.DefineRule("fold add", r =>
{
    var (x, y) = r.Vars<long, long>("x", "y");
    r.Rewrite(add.Of(num.Of(x), num.Of(y)), num.Of(Primitives.Add.Of(x, y)));
});

var root = graph.Root;
Expr e = root.Make(add.Of(mul.Of(name.Of("x"), num.Of(1L)), add.Of(num.Of(2L), num.Of(3L))));
root.Run(Schedule.Saturate(rules));

bool same = root.AreEqual(e, add.Of(num.Of(5L), name.Of("x")));   // true
ExtractedTerm best = root.Extract(e);                              // (Add (Var "x") (Num 5))
```

## Main concepts

| Type | What it is |
|---|---|
| `EGraph` | A theory and its database: column types, tables, rulesets, queries, split rules, scope tags, and the root scope. |
| `ISort<TSort>`, `ClassId` | A sort is a record struct over a `ClassId`, the type of a class of equivalent terms. |
| `Relation<...>` | A set of tuples (`DefineRelation`). |
| `Function<..., TOut>`, `ValueLattice<T>` | One value per key (`DefineFunction`); two values for one key merge by the lattice (`Max`, `Min`, `Intersect`, `Union`, or your own). |
| `Constructor<..., TSort>` | Terms: one class per key (`DefineConstructor`), with a cost for extraction. |
| `Term<T>`, `Fact` | Expressions and statements, written with `Of`, `Is`, `IsNot`, primitives and lambdas; used in rules, writes, checks and queries. |
| `Ruleset`, `RuleBuilder` | Named groups of rules; a rule has a body (`When`, `Where`) and a head (`Then`, `Rewrite`). |
| `Schedule` | What a run does: `Run`, `Repeat`, `Saturate`, `Sequence`, `Until`, `Split`. |
| `Scope` | Where facts are written, rules run and reads answered: the root or a child (`CreateChild`), writable or frozen (`Freeze`). |
| `ScopeTag` | A label on scopes, through which rules read another scope's facts (`In(tag)`) and rulesets restrict what their rules may judge on. |
| `Query` | A reusable conjunctive query (`DefineQuery`), run with `Any`, `Count` or `ForEachMatch`. |
| `SplitRule`, `CaseSet` | Case splits: `Schedule.Split` opens one case per value of a key and lifts what every case proves; `Scope.OpenCases` lets the host drive cases. |
| `Extractor`, `CostModel` | The cheapest equivalent term of a class (`Scope.Extract`), under a cost model. |
| `RunOptions`, `RunReport` | Limits for a run (iterations, time, rows, cases), and what it did (iterations, per-rule statistics, steps). |

## Threads

A writable scope and the scopes created under it are used from one thread at a time; a second thread gets an
`InvalidOperationException` instead of interleaving. A frozen scope is read-only and thread-safe, and each of its
children can be used from its own thread.

## Links

- Repository and examples: https://github.com/alethic/eggnog
- Architecture: https://github.com/alethic/eggnog/blob/main/docs/architecture.md
- Roadmap: https://github.com/alethic/eggnog/blob/main/docs/roadmap.md
- License (MIT) and notices: https://github.com/alethic/eggnog/blob/main/LICENSE and https://github.com/alethic/eggnog/blob/main/NOTICE
- egglog: https://github.com/egraphs-good/egglog
