<img src="https://raw.githubusercontent.com/alethic/eggnog/main/icon.png" alt="Eggnog" width="128" />

# Eggnog

Equality saturation for .NET, with egglog's capabilities and a typed C# API.

[![Build](https://github.com/alethic/eggnog/actions/workflows/Eggnog.yml/badge.svg)](https://github.com/alethic/eggnog/actions/workflows/Eggnog.yml)
[![NuGet](https://img.shields.io/nuget/v/Eggnog.svg)](https://www.nuget.org/packages/Eggnog)

An e-graph stores many equivalent terms at once: each class holds terms proven equal, and a term's arguments are
classes, so a few thousand nodes can represent exponentially many equivalent programs. Equality saturation applies
rewrite rules to an e-graph until no rule adds anything new, and then extracts the cheapest term of the class you
started from, so the order in which rules fire never loses a better answer. [egglog](https://github.com/egraphs-good/egglog)
combines this with Datalog: rules match facts and terms together, and functions merge their values with lattices.

Use it where a program must find the best of many equivalent forms: rewriting and optimizing expressions or queries,
simplifying symbolic math, or deriving facts by rules (Datalog) with equality built in.

Eggnog brings that to .NET:

- **egglog's model with a typed API.** Relations, functions with lattice merges, constructors (terms), rules with a
  join body and a head, rulesets, schedules (`Run`, `Repeat`, `Saturate`, `Sequence`, `Until`), queries and extraction,
  all as C# generics checked by the compiler. No text frontend to embed and no strings to parse.
- **Scopes.** A child scope starts as a copy of its parent and costs in proportion to what it touches. Freeze a scope
  that holds a schema or a theory, and any number of threads can query it and extend it in children of their own, one
  per request. Rules can be local to a scope, and can read another scope's facts through tags.
- **Case splits and lift.** Where a key can take only a few values, a split opens one case per value, runs rules in
  each, and keeps in the scope what every case proves. Cases that reach a contradiction drop out. The host can drive
  the same cases itself.
- **Extraction** of the cheapest equivalent term under a cost model you can change per call.
- **egglog's engine, in C#.** The engine is a faithful reimplementation of egglog's Rust engine (tables with
  functional dependencies, Free Join planning and execution, semi-naive evaluation, union-find with rebuilding).
  Benchmarks that port egglog's own and compare against it are in `src/Eggnog.Benchmarks`.
- **Deterministic by default.** Runs are serial and repeatable unless you ask for parallelism.

Eggnog targets .NET 8 and .NET 10, and is compatible with trimming and NativeAOT.

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

`x * 1 + (2 + 3)` and `5 + x` are now in one class, and the cheapest term of that class is `x + 5`.

## Scopes and case splits

Here a frozen root holds what every question shares, each question gets a child scope of its own, and a split
reasons by cases over the colors a signal can still show:

```csharp
var graph = new EGraph();
var signal = graph.DefineRelation<string>("signal");
var color = graph.DefineFunction<string, ValueSet<string>>("color", ValueLattice.Intersect<string>());
var stop = graph.DefineRelation<string>("stop");

var rules = graph.DefineRuleset("traffic");
foreach (var c in new[] { "red", "amber" })
{
    rules.DefineRule($"{c} means stop", r =>
    {
        var s = r.Var<string>("s");
        r.When(signal.Of(s), color.Of(s).Is(ValueSet.Of(c)));
        r.Then(stop.Of(s));
    });
}

// One case per color a signal can still show; each case assumes color(s) = {that color}.
var byColor = graph.DefineDomainSplit("by color", color, [signal]);
var schedule = Schedule.Saturate(rules, Schedule.Split(byColor, rules));

graph.Root.Assume(signal.Of("north"));
graph.Root.Freeze();                       // a shared base: read-only, children on any thread

using var q1 = graph.Root.CreateChild();
q1.Assume(color.Of("north").Is(ValueSet.Of("red", "amber")));
q1.Run(schedule);
bool mustStop = q1.Check(stop.Of("north"));            // true: both cases prove it

using var q2 = graph.Root.CreateChild();
q2.Assume(color.Of("north").Is(ValueSet.Of("red", "green")));
q2.Run(schedule);
bool mayGo = q2.Check(stop.Of("north")) == false;      // true: the green case does not prove it

bool atRoot = graph.Root.Check(stop.Of("north"));      // false: what a query learns stays in it
```

`color` is a function whose values merge by intersection, so each fact about a color narrows it. Neither rule fires
on `{red, amber}` directly; the split opens a red case and an amber case, each proves `stop`, and the lift writes
`stop(north)` into `q1`. In `q2` the green case proves nothing, so nothing is lifted. Both queries leave the root
unchanged, and could run on two threads at once.

Every example in this README is compiled and run by the test suite
([`ReadmeExampleTests`](src/Eggnog.Tests/Readme/ReadmeExampleTests.cs)).

## Documentation

- [Architecture](docs/architecture.md): the library's model, the engine, threading and lifetimes, and how Eggnog is
  tested.
- [Roadmap](docs/roadmap.md): what is open or planned.
- [Plans](docs/plans/README.md): each open item planned, with an evaluation of whether it is worth doing.
- The XML documentation of the public types, which your editor shows.
- [Design history](docs/history/README.md): the specifications and reports the code was built from.

## Building and testing

```
dotnet build src/Eggnog.Tests
dotnet src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.dll
```

A full run takes 10-15 minutes per target framework. The test project holds the library's tests (namespaces
`Eggnog.Tests.*`) and, under `Relations/`, the engine's (namespace `Eggnog.Relations.Tests`). Run one group by adding
`-namespace Eggnog.Relations.Tests` or `-namespace "Eggnog.Tests.*"`, or one class with `-class <FullName>`. The
benchmarks are in `src/Eggnog.Benchmarks` (see [its README](src/Eggnog.Benchmarks/README.md)). CI publishes
`src/Eggnog.AotCheck`, a small app that runs this README's examples, with NativeAOT, and fails on any trimming or AOT
warning. Clone with
`--recurse-submodules` to get egglog's sources in `ext/egglog`, which the engine's comments cite and its benchmark
comparisons build.

## Credits

Eggnog's engine is a reimplementation of [egglog](https://github.com/egraphs-good/egglog) by the egraphs-good
community, and its design follows egglog's closely. egglog itself grew out of
[egg](https://github.com/egraphs-good/egg), the same community's Rust library for e-graphs and equality saturation. The notices
of the work Eggnog derives from are in [NOTICE](NOTICE).

## License

MIT. See [LICENSE](LICENSE).
