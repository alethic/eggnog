<img src="https://raw.githubusercontent.com/alethic/eggnog/main/icon.png" alt="Eggnog" width="128" />

# Eggnog

Equality saturation for .NET, with egglog's capabilities and a C# API.

**`Eggnog`** is the library. A program defines column types, relations, functions with lattice merges, and constructors
(e-class terms); writes rules with a typed builder; runs schedules (`Run`, `Repeat`, `Saturate`, `Sequence`); writes
and reads facts from the host; and extracts the cheapest equivalent term:

```csharp
var graph = new EGraph();                                    // serial by default: deterministic
graph.DefineSort<Pred>("Pred");
var present = graph.DefineRelation<string>("present");
var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
var horn = graph.DefineRuleset("horn");
horn.DefineRule("a singleton domain makes a path present", r =>
{
    var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
    r.When(d.Is(dom.Of(p)));
    r.Where(d, d => d.Count == 1);
    r.Then(present.Of(p));
});

graph.Root.Assume(dom.Of("kind").Is(ValueSet.Of("A")));
graph.Root.Run(Schedule.Saturate(horn));
bool known = graph.Root.Check(present.Of("kind"));          // true
```

The design is `docs/design/rule-layer-spec.md`. Milestones R1a and R1b run everything at the root of a graph and add
compiled queries, run limits and diagnostics. R2 adds scopes: a child scope starts as a copy of its parent and costs in
proportion to what it touches; a frozen scope is a base that many children, on many threads, can query and extend;
scopes carry tags that rules read through (`In(tag)`, `Fact.In`), and rules can be local to a scope. Case splits
follow in R3 without changing the API that has shipped.

**`Eggnog.Relations`** is its engine: a faithful C# reimplementation of egglog's `core-relations` (tables with
functional dependencies and merges, Free Join planning and execution, union-find and rebuilding), its bridge and its
extractor, plus colored contexts, which egglog does not have. Its types are internal: `Eggnog` is the one supported
API, and it reaches the engine through `InternalsVisibleTo`. The engine's conventions and decisions are in
`docs/engine`, and its design in `docs/design`.

`src/Eggnog.Storage` and `src/Eggnog.Storage.Tests` hold an earlier storage spike. Neither `Eggnog` nor the NuGet
packages use it; it stays only as a second oracle for the engine's context tests.

## Building and testing

```
dotnet build src/Eggnog.Tests
src/Eggnog.Tests/bin/Debug/net10.0/Eggnog.Tests.exe
dotnet build src/Eggnog.Relations.Tests
src/Eggnog.Relations.Tests/bin/Debug/net10.0/Eggnog.Relations.Tests.exe
```
