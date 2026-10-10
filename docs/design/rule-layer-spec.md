> **Status (2026-10-09).** Produced by a design panel (four independent designs, three judges, one synthesis)
> against the API of the `Eggnog.Storage` spike. The spike is not the engine: the real storage and query engine will
> reproduce egglog's `core-relations` with colored contexts, and in that architecture the planner and executor live
> inside the engine. Treat this spec's matching, planning and storage-boundary sections as superseded. Its rule
> language, scheduling, case split and lift, extraction, interning and test plan are the reusable parts.

# Eggnog rule layer: final specification

This specification starts from the winning "dotnet-idiomatic" design. It fixes every flaw the three judges raised against that design, adds the ideas they recommended taking from the other three designs, and records a decision for every open question. I re-checked it against the storage source as it stands in `D:\eggnog\src\Eggnog.Storage` (commit `f950433`), against the prototype in `prototypes/cosmos-facts`, and against egglog's `run_schedule` and its extraction cost model.

---

## 0. Decisions

| # | Question | Decision |
|---|---|---|
| D1 | How do class values (Ids) join across scopes? | **Each class value lives in one scope (invariant H).** Every Id-typed variable has one *home scope*: the single read scope of every atom it appears in. A rule that joins an Id variable across two scopes is rejected when it is defined, with a message naming the variable. A scoped atom reads its ancestor's view in that ancestor's own canonical identifiers. There is no translation through the running scope's equalities, no member expansion, and no fallback to naive matching. Value variables are unrestricted. Reasons: (1) "the schema alone says" must exclude equalities the query derived; (2) semi-naive matching stays exact; (3) no index over several levels is needed, so no thread-safety question arises. The Cosmos theory uses only value-keyed scoped atoms, so it loses nothing. |
| D2 | Semi-naive windows | The focus atom reads rows changed since W; every other atom reads all rows. Matches are deduplicated per rule per iteration on the projected tuple. **No `until` parameter is added to `Scan`/`ScanColumn`**, and `TryLookup` gets no stamp. |
| D3 | An Id constant loses a union | The canonical Id constants are stored with each watermark (taken from egglog's I5 rule). Any change forces a full run. |
| D4 | Inheriting watermarks | Lazy. A rule's first run in scope S takes `min(parent W, S.CreatedAt)` from the nearest ancestor that ran it, walking up the chain. Inheritance is refused when the step changes how a tag resolves (S carries a tag the rule reads), and when the constants differ. It never applies to split rules, naive rules, or rules with callbacks. |
| D5 | Cost of delta scans | Storage gains a per-`RowSet` `MaxStamp` and a change log, so `Scan(since)` costs O(changes) and skips unchanged levels such as a frozen schema. Engine variants are skipped when `LatestStamp(table) <= W`. |
| D6 | Sealing definitions | **Never sealed.** Definitions are thread-safe and can be added at any time. A rule can be local to one scope (`Scope.DefineRule`), active in that scope and its descendants and gone when the scope is disposed. Containers are children of an empty, frozen root, so one `EGraph` and one compiled theory serve every container. |
| D7 | Re-splitting | Cases persist for the length of one `Run`. They are keyed by the split rule and its key registers, never by the domain. They absorb the parent's later facts through their own watermarks. When the cases are disposed at the end of the `Run`, the scope's split-rule progress is reset. |
| D8 | Lift algorithm | Not incremental. Each lift starts from scratch, so a change in the set of live cases is handled automatically. Equalities use a full partition meet. Case-local ids are filtered out with `IsLocalId`, so nothing is aliased into the parent even when only one case is live. Term lift (`GetOrAdd` in the parent, in rounds) is kept. A signature memo skips a lift when nothing has changed. |
| D9 | Zero cases | A host lift with no cases throws. A split rule whose element set is empty skips the split and counts it as skipped; the theory's own rules derive the contradiction. |
| D10 | Extraction | Knuth's algorithm over the classes reachable from the root. Ties are broken by content: cost, then function index, then columns (class columns by canonical id, value columns by content order). The result is acyclic even with zero costs. |
| D11 | `StopOnContradiction` | **`false` by default for host runs, always `true` inside cases.** Contradiction is a fact like any other, and rules such as "contradicted predicate becomes `PFalse`" react to it. Stopping early is an optimisation the host opts into. A vacuous case is dropped from the lift, so it never needs to saturate. |
| D12 | "A conjunct never proves itself redundant" | Three structural checks: invariant R (writes are never scoped), invariant H, and an opt-in **`RuleBuilder.Erasing(licence)`** checked at definition (every Fact-role atom must read the licence scope). Writers of `Analysis`-role tables may read only terms and analyses. There is no run-time `HasAssumptions` check, and no frame layout is imposed. The equality residue is stated in §3.3. |
| D13 | Arity limit and lambdas | Typed handles go up to 4 key columns. Beyond that, an **untyped layer** (`RelationHandle.Apply(params Term[])`, `Fact.Equal`, `Fact.In`, `Compute.Of(Primitive, params Term[])`) has no arity limit. **Named primitives** (`Primitive.Create`, the built-in `Primitives`) render by name. Lambdas render as `<lambda>`. |
| D14 | `.egg` frontend | Not in v1. The untyped layer and the schedule nodes map onto egglog one to one (§3.14). |
| D15 | Interner growth | Pools are permanent per `EGraph` in v1. Constants used only for reading never intern. `ValueStore.InternedCount` lets the host recycle a graph. |
| D16 | Storage surface | Public additions: `Version`, `ScanOwn`, `ScanMerges` (+ `MergeAction`), `IsLocalId`, `LatestStamp`, `ApproximateCount`. Internal changes: change log, `MaxStamp`, allocation-free probes and viewer lists, and an `IsClean` that costs O(unfrozen ancestors). |
| D17 | Parallel cases | Sequential. A lift writes to the parent, so the parent cannot be frozen. |
| D18 | Rollback | None. A failed `Run` commits no watermarks, and re-running finds the uncommitted matches again, because actions are idempotent. |
| D19 | Cost type | `long`, with saturating addition. Negative costs are rejected. |
| D20 | Built-in value types | `long`, `int`, `bool`, `double` (−0.0 normalised, NaN rejected), `string`, enums (registered), and `ValueSet<T>`. User types can be added as codecs or interned types. There are no sets of classes. |
| D21 | Callbacks | They run once per distinct projected match per iteration. A restamped copy may deliver a match again, so callbacks must be idempotent. Rules with callbacks never inherit watermarks. |
| D22 | Naming | Host writes are `Scope.Assume(facts)` and `Scope.Make(term)`. `Assert` is not used, because it clashes with xunit and `Debug`. `Scope` (a node of the tree) and `ScopeTag` (a label that scoped atoms resolve) are kept as two separate types. |
| D23 | Relative scopes | Not supported. Tags are absolute labels that resolve to the nearest ancestor-or-self. |
| D24 | `:until` | `Schedule.Until(body, facts)`. |
| D25 | Lattice laws | `ValueLattice<T>` bundles `Merge` with its `CaseJoin`, so the pair cannot be mismatched. `ValueLattice<T>.Verify(samples)` checks idempotence, commutativity, associativity and absorption, and is meant for consumers' tests. |
| D26 | First atom of a full run | Chosen at run time by `ApproximateCount`. Variant plans are static. |

---

## 1. Overview

### 1.1 The API in one screen

```csharp
var graph = new EGraph();
graph.DefineSort<Pred>("Pred");
var present = graph.DefineRelation<string>("present");
var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
var strEq = graph.DefineConstructor<string, string, Pred>("StrEq");
var horn = graph.DefineRuleset("horn");
horn.Rule("a domain makes a path present", r =>
{
    var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
    r.When(d.Is(dom.Of(p)));
    r.Where(d, d => d.Count == 1);
    r.Then(present.Of(p));
});
graph.Root.Freeze();                                   // empty; containers are its children
var schema = graph.Root.CreateChild(new ScopeTag("schema"));
schema.Assume(dom.Of("kind").Is(ValueSet.Of("A")));
schema.Run(Schedule.Saturate(horn));
schema.Freeze();
using var query = schema.CreateChild(new ScopeTag("query"));
Pred q = query.Make(strEq.Of("kind", "A"));
query.Run(Schedule.Saturate(horn));
bool known = query.Check(present.Of("kind"));          // reads need a clean scope; Run leaves it clean
```

### 1.2 How the layer maps onto Eggnog.Storage

| Rule layer | Storage |
|---|---|
| `EGraph`: definitions, `ValueStore`, the root scope | owns one `Store` |
| `Scope`: wraps one `Context`, plus engine state (tag, watermarks, local rules, split records) | `Context`, `CreateChild`, `Freeze`, `Dispose`, `Rebuild` |
| `Relation<…>` / `Function<…,TOut>` / `Constructor<…,TSort>` | `TableSchema.Relation` / `TableSchema.Lattice` (or no merge) / `TableSchema.Constructor` |
| Column types | `ColumnKind.Id` for sorts, `ColumnKind.Value` for everything else (interned or bit-packed) |
| Matching | `Scan(since)`, `ScanColumn(since)`, `TryLookup`, `Contains`, `Find`, `LatestStamp`, `ApproximateCount` |
| Actions | `Insert`, `GetOrAdd`, `Union`, followed by `Rebuild` |
| Semi-naive bookkeeping | `Store.Clock` and row stamps |
| Change detection | `Context.Version` |
| Lift | `ScanOwn`, `ScanMerges`, `IsLocalId`, `Contains`, `TryLookup`, `Find`, `GetOrAdd`, `Union`, `Insert` |
| Extraction | `ScanColumn` on the output column, `Find`, `Version` (staleness) |

### 1.3 Object model, lifetime and threading

- **`EGraph`**: one per process or per family of containers. Definitions are serialised under a lock and published copy-on-write, so they are thread-safe at any time.
- **`Scope`**:
  - It is not thread-safe, exactly like `Context`.
  - Children of a frozen scope may each be used on their own thread. A split's cases run on the same thread as their parent.
  - Typical tree: `root` (empty, frozen) → `schema#k` (one per container, tag `schema`, frozen after compilation) → `query#n` (tag `query`) → `case#m` (made by splits, disposed when the `Run` ends).
- **Local rules** belong to the scope they are defined on. They are active in that scope and its descendants, become unreachable when it is disposed, and can only be added before it is frozen.
- **Cleanliness contract.**
  - Writes (`Make`, `Assume`, `Union`) are legal while the scope is dirty.
  - Reads (`Check`, `Get`, `TryGet`, `Find`, `AreEqual`, `Any`, `Count`, `ForEachMatch`, `IsContradictory`, `CreateExtractor`) throw `InvalidOperationException` naming `Rebuild()` unless the scope is clean. They never rebuild implicitly.
  - Engine operations (`Run`, `Split`, `CaseSet.Lift`) rebuild on entry and leave the scope clean on success.

### 1.4 Departures from egglog, stated once

1. There is no `delete` or `subsume`, because storage has no deletion.
2. Merges must be associative, commutative and idempotent.
3. Containers hold values only, never classes.
4. Actions never read. A function value must be matched in the body; reading `dom` in a head is a definition error.
5. Scoped atoms and class-value isolation (D1) are additions.
6. `push`/`pop` become child scopes.
7. There are no proofs, no function defaults, and no global `let`; a frontend would keep its own table of names.

### 1.5 What changed from the winning design

| Judge's finding against dotnet-idiomatic | Fix |
|---|---|
| Id-constant hole | D3, invariant K. |
| `until` on scans, stamped lookups | Removed (D2). The storage surface is now D16. |
| Definitions sealed at root freeze; schema is the root | D6. `Schema` is an ordinary tag. |
| Split memo re-splits on any change of `Version` | D7 persistent cases plus the lift signature memo. |
| Single live case aliases case-local ids | `IsLocalId` filter (D8). |
| No deduplication of projected matches | `MatchBuffer` deduplicates projected tuples. |
| Extraction ties follow scan order | D10. |
| Arity capped at 4; lambdas print `<lambda>` | D13. |
| Cross-level class index; thread-safety of `CollectAncestorClasses` | Gone (D1). |
| `StopOnContradiction` default | Decided and justified (D11). |
| `Scope.Assert` name clash | D22. |
| `ValueSet.Of("abc")` trap | No `IEnumerable` overload named `Of`; `ValueSet.From` exists for sequences; a test covers it. |
| Truncated surface | Every type is given in §2. |

---

## 2. Files

House style everywhere:

- one type per file; partial classes may span files;
- `net8.0;net10.0`, `LangVersion` 13, nullable enabled, no implicit usings, explicit usings;
- 4-space indent, braces on new lines;
- `x == false` rather than `!x`; sealed classes unless they are a base for typed handles;
- XML docs on every public member.

All `src/Eggnog` types are in namespace `Eggnog`. Folders only organise files. `Eggnog.csproj` already has `InternalsVisibleTo("Eggnog.Tests")`.

### 2.1 Eggnog.Storage changes

**New file `MergeAction.cs`**

```csharp
/// <summary>Receives an identifier a context merged relative to its parent, and its canonical identifier in the context.</summary>
public delegate void MergeAction(Id member, Id canonical);
```

**New partial file `Context.Inspection.cs`**

```csharp
public sealed partial class Context
{
    /// Increases on every effective change to this context's own state: a row slot added (assertion, copy taken by
    /// marking or sync, row rewritten after a merge), an output changed, a successful merge, a re-key that retired an
    /// identifier. No-op inserts, equal merges and redundant unions leave it unchanged. Changes an ancestor makes that
    /// this context reads through without copying do not move it, so compare ancestors' versions too.
    public long Version { get; }

    /// Visits the live rows this context holds itself, canonical here: its assertions, copies of ancestor rows that
    /// mention a class merged here or share a key with one of its own rows, and rows rewritten by merges. Rows read
    /// through from ancestors are not visited. Requires IsClean.
    public void ScanOwn(Table table, RowAction action);

    /// For every identifier in a class this context merged (its marked set), excluding identifiers this context
    /// allocated: the identifier, which is canonical in the parent, and its canonical identifier here. A merged class's
    /// own canonical identifier is reported paired with itself when it is a parent identifier. Requires IsClean.
    /// Throws InvalidOperationException on the root.
    public void ScanMerges(MergeAction action);

    /// Whether NewId or GetOrAdd on this context allocated the identifier. Throws InvalidOperationException on the root.
    public bool IsLocalId(Id id);

    /// An upper bound on the stamp of every row of this context's view of the table. It may over-approximate
    /// (shadowed rows count). Requires IsClean.
    public long LatestStamp(Table table);

    /// An upper bound on the number of rows in this context's view of the table: the live rows every level holds.
    /// Requires IsClean.
    public long ApproximateCount(Table table);
}
```

**How the additions are implemented**

- **`Version`**: a `long version` field, incremented:
  - in `AddOrMerge` after `set.Add(row, store.Tick())`;
  - in `MergeInto` when `combined != current`;
  - in `UnionCore` before `return true`;
  - in `Rekey` when `retired.Count > 0`.
- **`IsLocalId`**: a `HashSet<ulong>? localIds` on non-root contexts. It is filled by `NewId` and by `GetOrAdd` when `created` is true.
- **`ScanMerges`**: iterates `marked`, skips `localIds`, and reports `(m, LocalFind(m))`. When the context is clean, `marked` holds only parent-canonical ids and local ids, because `Rekey` rebuilds it through `parent.FindRaw`. Nested reads inside the action are safe: path compression writes `local`, which is not being enumerated.
- **`LatestStamp`**: the maximum of `Own(table)?.MaxStamp` over this context and its ancestors.
- **`ApproximateCount`**: the sum of `Own(table)?.LiveCount` over the same levels.

**Internal changes, with no API change**

1. **`RowSet`**:
   - `long MaxStamp`, raised in `Add` and `SetOutput`.
   - `int LiveCount => keys.Count`.
   - **Change log**: parallel arrays `int[] logSlots`, `long[] logStamps`, `int logCount`, appended by `Add` and `SetOutput`. Stamps increase, because writes to one context are single-threaded and `Tick` is monotone.
   - `int FirstChangeAfter(long since)` is a binary search. An entry is current iff `alive[slot] && stamps[slot] == logStamps[i]`.
   - `Compact()` rebuilds the log from live slots sorted by stamp.
   - When `logCount >= 64 && logCount > 4 * LiveCount`, the log is rebuilt the same way.
2. **`ScanLevel`**:
   - Visits own rows only if `set.MaxStamp > since`.
   - With `since > 0` it walks the change log from `FirstChangeAfter(since)`, delivering current entries only. With `since == 0` it walks slots as today.
   - **`ScanColumnLevel`** also skips a level when `set.MaxStamp <= since`.
3. **`TryLookup` / `Contains`**:
   - Use a `[ThreadStatic] static ulong[]? keyProbe`, sized to the key count, instead of `key.ToArray()`.
   - `Canonicalize` gains a `Span<ulong>` overload bounded by the span length.
   - Safe because no user code runs during a lookup.
4. **`Scan` / `ScanColumn`**: rent the `viewers` list from a `[ThreadStatic]` pool indexed by scan nesting depth, with try/finally, instead of `new List<Context>()`.
5. **`IsClean` in O(unfrozen ancestors)**:
   - A field `int settled` counts leading ancestors, from the root, that are frozen and whose log this context has fully consumed.
   - It is set in the constructor and advanced at the end of `Sync`.
   - `IsClean` checks only `ancestors[settled..]`.
   - Correct because frozen ancestors form a prefix (a context freezes only after its parent), their queues are empty, and their `LogEnd` is constant.
6. **Documentation**:
   - `RowAction` and `Context` remarks gain a guarantee: *reads of any context (`TryLookup`, `Contains`, `Scan`, `ScanColumn`, `ScanOwn`, `ScanMerges`, `Find`, `AreEqual`, `LatestStamp`, `ApproximateCount`) may be made from inside a `RowAction`; writes may not*.
   - README "Not yet": lifting reads exist, and `since` scans skip unchanged levels.

**Storage tests to add (`Eggnog.Storage.Tests`)**

- `ContextTests`:
  - `NullaryRelationsWork`.
  - `ReadsNestInsideScans`: a lookup, a scan and a column scan of the same context and of an ancestor, from inside a scan.
  - `VersionIgnoresNoOps`.
  - `IsLocalIdTracksAllocations`.
  - `IsCleanSeesAFrozenAncestorsUnreadLog`: write a parent, create a child, freeze the parent, and check the child reports dirty until rebuilt.
- `DifferentialTests` gains checks at each checkpoint:
  1. `Scan(t, since)` delivers exactly the rows of the full scan stamped after `since`, plus at most rewritten copies. This compares the change-log path with the slot walk.
  2. Every `ScanOwn` row is in `Scan`, and every `Scan` row absent from the parent's canonicalized view is in `ScanOwn`.
  3. `ScanMerges`: for any two parent-visible ids x, y that are equal here and not in the parent, both are reported with one canonical identifier, and no local id is reported.
  4. `LatestStamp >= max stamp seen` and `ApproximateCount >= rows scanned`.

### 2.2 src/Eggnog public types

#### Graph/

```csharp
// Graph/EGraph.cs
/// A theory and its database: column types, tables, rulesets, split rules, queries, a value store, and the root of a
/// tree of scopes. Definitions may be added at any time, from any thread; a rule defined while a run is in progress
/// takes part from that run's next iteration.
public sealed class EGraph
{
    public EGraph();
    public Store Store { get; }
    public ValueStore Values { get; }
    /// The root scope (tag ScopeTag.Root). Freeze it empty so that containers can be sibling children.
    public Scope Root { get; }
    /// The built-in nullary relation meaning "this scope has no model". Role Fact, never lifted as a row: a lift treats a
    /// case holding it as vacuous.
    public Relation Contradiction { get; }
    public IReadOnlyList<TableHandle> Tables { get; }
    public IReadOnlyList<Ruleset> Rulesets { get; }
    public IReadOnlyList<SplitRule> SplitRules { get; }

    /// Registers TSort as an e-class sort (an Id column type). Throws ArgumentException if TSort or name is taken.
    public ColumnType<TSort> DefineSort<TSort>(string name) where TSort : struct, ISort<TSort>;

    // Relations. Names are unique per graph (ArgumentException otherwise).
    public Relation DefineRelation(string name, TableOptions? options = null);
    public Relation<T1> DefineRelation<T1>(string name, TableOptions? options = null);
    public Relation<T1, T2> DefineRelation<T1, T2>(string name, TableOptions? options = null);
    public Relation<T1, T2, T3> DefineRelation<T1, T2, T3>(string name, TableOptions? options = null);
    public Relation<T1, T2, T3, T4> DefineRelation<T1, T2, T3, T4>(string name, TableOptions? options = null);
    public RelationHandle DefineRelation(string name, IReadOnlyList<ColumnType> columns, TableOptions? options = null);

    // Functions with a value output. TOut must not be a sort (ArgumentException: "use DefineConstructor").
    // With no lattice, two different values for one key in one scope are an error.
    public Function<TOut> DefineFunction<TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, TOut> DefineFunction<T1, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, TOut> DefineFunction<T1, T2, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, T3, TOut> DefineFunction<T1, T2, T3, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public Function<T1, T2, T3, T4, TOut> DefineFunction<T1, T2, T3, T4, TOut>(string name, ValueLattice<TOut>? lattice = null, TableOptions? options = null);
    public FunctionHandle DefineFunction(string name, IReadOnlyList<ColumnType> keys, ColumnType output,
        MergeFunction? merge = null, MergeFunction? caseJoin = null, TableOptions? options = null);

    // Constructors (terms). cost >= 0 (ArgumentOutOfRangeException). TSort must have been defined.
    public Constructor<TSort> DefineConstructor<TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, TSort> DefineConstructor<T1, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, TSort> DefineConstructor<T1, T2, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, T3, TSort> DefineConstructor<T1, T2, T3, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public Constructor<T1, T2, T3, T4, TSort> DefineConstructor<T1, T2, T3, T4, TSort>(string name, long cost = 1, TableOptions? options = null) where TSort : struct, ISort<TSort>;
    public ConstructorHandle DefineConstructor(string name, IReadOnlyList<ColumnType> keys, ColumnType output, long cost = 1, TableOptions? options = null);

    /// A named ruleset; names are unique per graph.
    public Ruleset DefineRuleset(string name);
    /// A split rule; caseTag defaults to new ScopeTag(name). Compiled immediately; throws RuleDefinitionException.
    public SplitRule DefineSplit(string name, Action<SplitBuilder> define, ScopeTag? caseTag = null);
    /// The common discriminator split: where candidates(k) and guard(k) hold and domain(k) = d with |d| > 1, one case per
    /// v in d assuming domain(k) = {v}; key k. Coverage obligation (stated in the docs): domain(k) = S must mean "wherever
    /// guard(k) holds (always, if guard is null), k's value is one of S".
    public SplitRule DefineDomainSplit<TKey, TElement>(string name, Relation<TKey> candidates,
        Function<TKey, ValueSet<TElement>> domain, Relation<TKey>? guard = null, ScopeTag? caseTag = null) where TElement : notnull;
    /// A compiled conjunctive query for host reads (Scope.Any/Count/ForEachMatch).
    public Query DefineQuery(string name, Action<QueryBuilder> define);
}

// Graph/Scope.cs (partial: identity, tree, lifecycle, local rules)
/// One context of the graph's store with the engine's state for it. Not thread-safe; children of a frozen scope may be
/// used concurrently, one thread each.
public sealed partial class Scope : IDisposable
{
    public EGraph Graph { get; }
    /// The storage context. Direct writes through it are safe for the engine (stamps carry them); creating or disposing
    /// its children directly is not supported.
    public Context Context { get; }
    public Scope? Parent { get; }
    public ScopeTag? Tag { get; }
    /// Made by a split or a CaseSet.
    public bool IsCase { get; }
    public bool IsFrozen { get; }
    public bool IsDisposed { get; }
    public bool IsClean { get; }
    /// "root/schema#2/query#7", for messages.
    public string Path { get; }
    public IReadOnlyList<Scope> Children { get; }
    /// Rules defined on this scope, in definition order.
    public IReadOnlyList<Rule> LocalRules { get; }
    /// A child scope. Thread-safe on a frozen scope.
    public Scope CreateChild(ScopeTag? tag = null);
    public void Rebuild();
    /// Rebuilds and freezes. The parent must be frozen (storage rule). Local rules are fixed from here on.
    public void Freeze();
    /// Throws InvalidOperationException if live children remain (a CaseSet left open counts). The root cannot be disposed.
    public void Dispose();
    /// A rule active in this scope and its descendants only, scheduled with ruleset. Throws InvalidOperationException if
    /// the scope is frozen; RuleDefinitionException on definition errors.
    public Rule DefineRule(Ruleset ruleset, string name, Action<RuleBuilder> define);
    public override string ToString();
}

// Graph/Scope.Writes.cs (partial: host writes; legal while dirty; may leave the scope dirty)
public sealed partial class Scope
{
    /// Builds a ground term with get-or-add and returns its class. Constants are interned. Throws ArgumentException if the
    /// term reads a function (Get it first), uses a computation, or is scoped.
    public TSort Make<TSort>(Term<TSort> term) where TSort : struct, ISort<TSort>;
    /// Writes ground facts: a relation row; F.Of(k).Is(v) (merged by F's lattice); C.Of(k).Is(x) (unions outputs);
    /// a.Is(b) of a sort (union). Value equalities, scoped handles and function reads are rejected.
    public void Assume(params Fact[] facts);
    /// Storage Union; true where the classes were distinct.
    public bool Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
}

// Graph/Scope.Reads.cs (partial: host reads; require IsClean; never create terms, never intern)
public sealed partial class Scope
{
    /// Whether Contradiction holds in this scope.
    public bool IsContradictory { get; }
    /// Whether every ground fact holds. Scoped handles read the resolved ancestor; class results compare canonically here.
    /// A constant never interned makes its fact false.
    public bool Check(params Fact[] facts);
    /// The value of a ground term (a function value, or a class for a sort term). InvalidOperationException if absent.
    public T Get<T>(Term<T> term);
    public bool TryGet<T>(Term<T> term, [MaybeNullWhen(false)] out T value);
    public TSort Find<TSort>(TSort value) where TSort : struct, ISort<TSort>;
    public bool AreEqual<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
    /// False if b's term does not exist here.
    public bool AreEqual<TSort>(TSort a, Term<TSort> b) where TSort : struct, ISort<TSort>;
    public bool Any(Query query);
    /// Distinct full bindings.
    public long Count(Query query);
    public void ForEachMatch(Query query, QueryCallback callback);
}

// Graph/Scope.Engine.cs (partial: runs, cases, extraction; internal engine state lives here)
public sealed partial class Scope
{
    /// Runs a schedule in this scope (§3.7). Rebuilds first; validates that every tag read by a reachable rule resolves;
    /// leaves the scope clean on success; disposes cases made during the call. Throws InvalidOperationException if
    /// frozen, OperationCanceledException on cancellation, RuleEvaluationException on an action failure.
    public RunReport Run(Schedule schedule, RunOptions? options = null, CancellationToken cancellationToken = default);
    /// Host-driven cases. Obligation: the cases added must cover this scope (every model of it satisfies some case's
    /// assumptions).
    public CaseSet OpenCases();
    /// One case per value (assume writes it), each run with schedule under StopOnContradiction, lifted, disposed.
    /// ArgumentException when values is empty or exceeds options.CaseLimit. Same coverage obligation.
    public LiftReport Split<T>(IEnumerable<T> values, Action<Scope, T> assume, Schedule schedule,
        ScopeTag? caseTag = null, RunOptions? options = null);
    public Extractor CreateExtractor(CostModel? costs = null);
    public ExtractedTerm Extract<TSort>(TSort root, CostModel? costs = null) where TSort : struct, ISort<TSort>;
}

// Graph/ScopeTag.cs
/// A label a scope carries; a scoped atom names its ancestor by tag. Compared by reference.
public sealed class ScopeTag
{
    public ScopeTag(string name);
    public string Name { get; }
    /// Always resolves to EGraph.Root.
    public static ScopeTag Root { get; }
    public override string ToString();
}

// Graph/ISort{TSort}.cs
/// An e-class sort: a struct wrapping a class Id, e.g. `readonly record struct Pred(Id Id) : ISort<Pred>`.
public interface ISort<TSort> where TSort : struct, ISort<TSort>
{
    Id Id { get; }
    static abstract TSort FromId(Id id);
}
```

#### Values/

```csharp
// Values/ColumnType.cs
/// The type of a column: how a CLR value is encoded into 64 bits and printed. Invariant V: Encode is injective, so equal
/// bits mean equal values.
public abstract class ColumnType
{
    private protected ColumnType(string name, ColumnKind kind, Type clrType);
    public string Name { get; }
    public ColumnKind Kind { get; }
    public Type ClrType { get; }
    /// s-expression literal: "\"kind\"", "42", "true", "(set-of \"a\" \"b\")", "#17".
    public abstract string Format(ulong bits);
    /// Content order (strings ordinal, numbers numeric, sets lexicographic, classes by raw id).
    public abstract int Compare(ulong x, ulong y);
    public override string ToString();
}

// Values/ColumnType{T}.cs
public abstract class ColumnType<T> : ColumnType
{
    private protected ColumnType(string name, ColumnKind kind);
    /// Encodes, interning if needed (thread-safe).
    public abstract ulong Encode(T value);
    /// Encodes only if already interned; never grows a pool.
    public abstract bool TryEncodeExisting(T value, out ulong bits);
    public abstract T Decode(ulong bits);
}

// Values/IValueCodec{T}.cs
/// A user encoding for T: injective, thread-safe.
public interface IValueCodec<T>
{
    ulong Encode(T value);
    bool TryEncodeExisting(T value, out ulong bits);
    T Decode(ulong bits);
    int Compare(T x, T y);
    string Format(T value);
}

// Values/ValueStore.cs
/// Column types by CLR type, and the graph's interners. Built in: long "i64", int "i32", bool "bool", double "f64",
/// string "String", ValueSet<string>, ValueSet<long>, ValueSet<int>. Registration of a taken type throws.
public sealed class ValueStore
{
    /// ArgumentException naming the Register* method when T is unknown.
    public ColumnType<T> TypeOf<T>();
    public bool TryGetType<T>([NotNullWhen(true)] out ColumnType<T>? type);
    public ColumnType<T> Register<T>(string name, IValueCodec<T> codec);
    public ColumnType<T> RegisterInterned<T>(string name, IEqualityComparer<T>? equality = null, IComparer<T>? order = null) where T : notnull;
    /// Throws for a sort T (no sets of classes).
    public ColumnType<ValueSet<T>> RegisterSet<T>() where T : notnull;
    public ColumnType<TEnum> RegisterEnum<TEnum>() where TEnum : struct, Enum;
    /// Total handles interned in every pool; pools never shrink.
    public long InternedCount { get; }
}

// Values/ValueSet{T}.cs
/// An immutable set held in ascending element order (ordinal for strings, Comparer<T>.Default otherwise), equal by
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
    public bool Equals(ValueSet<T>? other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public IEnumerator<T> GetEnumerator();
    /// {a, b}
    public override string ToString();
}

// Values/ValueSet.cs
public static class ValueSet
{
    /// ValueSet.Of("abc") is {"abc"}: there is deliberately no IEnumerable overload named Of.
    public static ValueSet<T> Of<T>(params T[] items) where T : notnull;
    public static ValueSet<T> From<T>(IEnumerable<T> items) where T : notnull;
}

// Values/ValueLattice{T}.cs
/// A merge for a function's values, with the case-join a lift uses. Contract: Merge is associative, commutative,
/// idempotent (storage); Merge(a, CaseJoin(a, b)) == a (absorption: the join claims no more than either case).
public sealed class ValueLattice<T>
{
    public ValueLattice(string name, Func<T, T, T> merge, Func<T, T, T>? caseJoin = null);
    public string Name { get; }
    public Func<T, T, T> Merge { get; }
    /// null: a lift requires every case to agree.
    public Func<T, T, T>? CaseJoin { get; }
    /// Checks the laws on every pair and triple of samples; throws InvalidOperationException naming the law and values.
    public void Verify(IEnumerable<T> samples);
}

// Values/ValueLattice.cs
public static class ValueLattice
{
    /// Merge ∩, CaseJoin ∪ (dom, types).
    public static ValueLattice<ValueSet<T>> Intersect<T>() where T : notnull;
    /// Merge ∪, CaseJoin ∩.
    public static ValueLattice<ValueSet<T>> Union<T>() where T : notnull;
    /// Merge max, CaseJoin min (a lower bound).
    public static ValueLattice<T> Max<T>() where T : IComparable<T>;
    /// Merge min, CaseJoin max (an upper bound).
    public static ValueLattice<T> Min<T>() where T : IComparable<T>;
}
```

#### Tables/

```csharp
// Tables/TableKind.cs
public enum TableKind { Relation, Function, Constructor }

// Tables/TableRole.cs
/// What rows mean. Term: constructor rows (terms). Fact: claims (lifted by default; Erasing rules must read them scoped).
/// Analysis: derived from terms only (never lifted by default; writers may read only Term and Analysis tables).
public enum TableRole { Term, Fact, Analysis }

// Tables/TableOptions.cs
public sealed class TableOptions
{
    /// Relations and functions: Fact or Analysis (Term is rejected). Ignored for constructors.
    public TableRole Role { get; init; } = TableRole.Fact;
    /// Default: Fact true, Analysis false, constructors true (term lift).
    public bool? Lift { get; init; }
    /// Constructors only.
    public bool Extractable { get; init; } = true;
}

// Tables/TableHandle.cs
public abstract class TableHandle
{
    private protected TableHandle();
    public EGraph Graph { get; }
    public Table Table { get; }
    public string Name { get; }
    /// Declaration order; extraction tie-break.
    public int Index { get; }
    public TableKind Kind { get; }
    public TableRole Role { get; }
    public IReadOnlyList<ColumnType> Keys { get; }
    public ColumnType? Output { get; }
    public bool Lift { get; }
    /// Set on a read-only scoped view made by In(tag); such a handle is rejected in any write.
    public ScopeTag? ReadScope { get; }
    /// "dom" or "dom@schema"
    public override string ToString();
}

// Tables/RelationHandle.cs
public class RelationHandle : TableHandle
{
    internal RelationHandle();
    /// Untyped fact; argument types checked when the rule compiles.
    public Fact Apply(params Term[] row);
}

// Tables/FunctionHandle.cs
public class FunctionHandle : TableHandle
{
    internal FunctionHandle();
    public MergeFunction? Merge { get; }
    public MergeFunction? CaseJoin { get; }
    /// Untyped application; its value is the output.
    public Term Apply(params Term[] keys);
}

// Tables/ConstructorHandle.cs
public class ConstructorHandle : TableHandle
{
    internal ConstructorHandle();
    public long Cost { get; }
    public bool Extractable { get; }
    /// Untyped application; its value is the class.
    public Term Apply(params Term[] keys);
}

// Tables/Relation.cs                   (nullary)
public sealed class Relation : RelationHandle { public Fact Of(); public Relation In(ScopeTag scope); }
// Tables/Relation{T1}.cs
public sealed class Relation<T1> : RelationHandle { public Fact Of(Term<T1> a); public Relation<T1> In(ScopeTag scope); }
// Tables/Relation{T1,T2}.cs
public sealed class Relation<T1, T2> : RelationHandle { public Fact Of(Term<T1> a, Term<T2> b); public Relation<T1, T2> In(ScopeTag scope); }
// Tables/Relation{T1,T2,T3}.cs
public sealed class Relation<T1, T2, T3> : RelationHandle { public Fact Of(Term<T1> a, Term<T2> b, Term<T3> c); public Relation<T1, T2, T3> In(ScopeTag scope); }
// Tables/Relation{T1,T2,T3,T4}.cs
public sealed class Relation<T1, T2, T3, T4> : RelationHandle { public Fact Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); public Relation<T1, T2, T3, T4> In(ScopeTag scope); }

// Tables/Function{TOut}.cs ... Function{T1,T2,T3,T4,TOut}.cs (Lattice is null when none was given)
public sealed class Function<TOut> : FunctionHandle { public ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(); public Function<TOut> In(ScopeTag scope); }
public sealed class Function<T1, TOut> : FunctionHandle { public ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a); public Function<T1, TOut> In(ScopeTag scope); }
public sealed class Function<T1, T2, TOut> : FunctionHandle { public ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b); public Function<T1, T2, TOut> In(ScopeTag scope); }
public sealed class Function<T1, T2, T3, TOut> : FunctionHandle { public ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b, Term<T3> c); public Function<T1, T2, T3, TOut> In(ScopeTag scope); }
public sealed class Function<T1, T2, T3, T4, TOut> : FunctionHandle { public ValueLattice<TOut>? Lattice { get; } public Term<TOut> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); public Function<T1, T2, T3, T4, TOut> In(ScopeTag scope); }

// Tables/Constructor{TSort}.cs ... Constructor{T1,T2,T3,T4,TSort}.cs (all: where TSort : struct, ISort<TSort>)
public sealed class Constructor<TSort> : ConstructorHandle { public Term<TSort> Of(); public Constructor<TSort> In(ScopeTag scope); }
public sealed class Constructor<T1, TSort> : ConstructorHandle { public Term<TSort> Of(Term<T1> a); public Constructor<T1, TSort> In(ScopeTag scope); }
public sealed class Constructor<T1, T2, TSort> : ConstructorHandle { public Term<TSort> Of(Term<T1> a, Term<T2> b); public Constructor<T1, T2, TSort> In(ScopeTag scope); }
public sealed class Constructor<T1, T2, T3, TSort> : ConstructorHandle { public Term<TSort> Of(Term<T1> a, Term<T2> b, Term<T3> c); public Constructor<T1, T2, T3, TSort> In(ScopeTag scope); }
public sealed class Constructor<T1, T2, T3, T4, TSort> : ConstructorHandle { public Term<TSort> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); public Constructor<T1, T2, T3, T4, TSort> In(ScopeTag scope); }
```

#### Terms/

```csharp
// Terms/Term.cs
/// An expression: a variable, a constant, a table application, or a computation. Immutable; renders as an s-expression.
public abstract class Term
{
    private protected Term();
    public abstract Type ClrType { get; }
    /// An untyped constant from already encoded bits (frontends).
    public static Term Constant(ColumnType type, ulong bits);
    public override string ToString();
}

// Terms/Term{T}.cs
public abstract class Term<T> : Term
{
    private protected Term();
    public Fact Is(Term<T> other);
    /// A constant; for a sort, a class constant.
    public static implicit operator Term<T>(T value);
}

// Terms/Var{T}.cs
/// A variable of one builder; using it in another builder is a RuleDefinitionException.
public sealed class Var<T> : Term<T>
{
    internal Var();
    public string Name { get; }
}

// Terms/Fact.cs
public abstract class Fact
{
    private protected Fact();
    public static Fact Equal(Term left, Term right);
    /// A filter. Class values must share a home scope.
    public static Fact NotEqual(Term left, Term right);
    /// A function or constructor application has a row.
    public static Fact Exists(Term application);
    /// A filter on a boolean computation.
    public static Fact True(Term<bool> condition);
    /// Scopes every table application inside facts (nested ones included) to the ancestor tagged scope. Nesting, or a
    /// handle already scoped to a different tag, is a definition error.
    public static Fact In(ScopeTag scope, params Fact[] facts);
    public override string ToString();
}

// Terms/Compute.cs
/// Computed values from C# lambdas (they render as <lambda>); prefer named primitives where rendering matters.
public static class Compute
{
    public static Term<TResult> Of<T1, TResult>(Term<T1> a, Func<T1, TResult> function);
    public static Term<TResult> Of<T1, T2, TResult>(Term<T1> a, Term<T2> b, Func<T1, T2, TResult> function);
    public static Term<TResult> Of<T1, T2, T3, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, TResult> function);
    public static Term<TResult> Of<T1, T2, T3, T4, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, TResult> function);
    /// Untyped.
    public static Term Of(Primitive primitive, params Term[] arguments);
}

// Terms/Primitive.cs
/// A pure, deterministic, thread-safe function over values (never over classes). Semi-naive matching and lift rely on
/// purity. An exception thrown by it fails the rule.
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

// Terms/Primitive{T1,TResult}.cs ... Primitive{T1,T2,T3,T4,TResult}.cs
public sealed class Primitive<T1, TResult> : Primitive { public Func<T1, TResult> Function { get; } public Term<TResult> Of(Term<T1> a); }
public sealed class Primitive<T1, T2, TResult> : Primitive { public Func<T1, T2, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b); }
public sealed class Primitive<T1, T2, T3, TResult> : Primitive { public Func<T1, T2, T3, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b, Term<T3> c); }
public sealed class Primitive<T1, T2, T3, T4, TResult> : Primitive { public Func<T1, T2, T3, T4, TResult> Function { get; } public Term<TResult> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d); }

// Terms/Primitives.cs
/// Named built-ins (egglog names), memoised per T.
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

#### Rules/

```csharp
// Rules/Ruleset.cs
public sealed class Ruleset
{
    public EGraph Graph { get; }
    public string Name { get; }
    /// Global rules, definition order (snapshot).
    public IReadOnlyList<Rule> Rules { get; }
    /// A global rule; compiled immediately (RuleDefinitionException).
    public Rule Rule(string name, Action<RuleBuilder> define);
    public static implicit operator Schedule(Ruleset ruleset);
    public override string ToString();
}

// Rules/Rule.cs
public sealed class Rule
{
    public string Name { get; }
    public Ruleset Ruleset { get; }
    /// null for a global rule.
    public Scope? Owner { get; }
    public ScopeTag? Licence { get; }
    public bool IsNaive { get; }
    public bool HasCallback { get; }
    public IReadOnlyList<ScopeTag> ReadScopes { get; }
    /// (rule ((= s (inset e p)) (present@schema p) ...) ((union e (PTrue))) :ruleset rewrite :erasing schema)
    public override string ToString();
}

// Rules/RuleBuilder.cs
/// Collects one rule: When/Where make the body, Then/Make/Rewrite the head. Order of calls is free; head facts run in the
/// order given.
public sealed class RuleBuilder
{
    public string Name { get; }
    public Var<T> Var<T>(string name);
    public (Var<T1>, Var<T2>) Vars<T1, T2>(string name1, string name2);
    public (Var<T1>, Var<T2>, Var<T3>) Vars<T1, T2, T3>(string name1, string name2, string name3);
    public (Var<T1>, Var<T2>, Var<T3>, Var<T4>) Vars<T1, T2, T3, T4>(string name1, string name2, string name3, string name4);
    /// An untyped variable (frontends).
    public Term Var(ColumnType type, string name);
    public void When(params Fact[] facts);
    public void Where<T1>(Term<T1> a, Func<T1, bool> predicate);
    public void Where<T1, T2>(Term<T1> a, Term<T2> b, Func<T1, T2, bool> predicate);
    public void Where<T1, T2, T3>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, bool> predicate);
    public void Where<T1, T2, T3, T4>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, bool> predicate);
    /// e.g. Primitives.Contains<string>().Of(d, v)
    public void Where(Term<bool> condition);
    public void Then(params Fact[] facts);
    public void Then(RuleCallback callback);
    /// A head variable bound by get-or-add of term; it may not appear in the body.
    public Var<TSort> Make<TSort>(string name, Term<TSort> term) where TSort : struct, ISort<TSort>;
    /// Body: a fresh class variable t with t = left; head: t = right (union).
    public void Rewrite<TSort>(Term<TSort> left, Term<TSort> right) where TSort : struct, ISort<TSort>;
    /// Opt out of semi-naive matching (always a full run) and of inheritance.
    public void Naive();
    /// Checked at definition: every atom over a Fact-role table reads scope licence (§3.3).
    public void Erasing(ScopeTag licence);
}

// Rules/RuleCallback.cs
/// Runs once per distinct projected match per iteration, in the apply phase. Must be idempotent (§3.5).
public delegate void RuleCallback(RuleMatch match, ScopeWriter writer);

// Rules/RuleMatch.cs
public readonly ref struct RuleMatch
{
    public string RuleName { get; }
    /// Decoded; a class is canonical in the variable's home scope.
    public T Get<T>(Var<T> variable);
    public ulong GetBits(Term variable);
}

// Rules/ScopeWriter.cs
/// Writes available to a callback (the scope is dirty; nothing can be read). Valid only during the callback.
public sealed class ScopeWriter
{
    public Scope Scope { get; }
    public void Assume(params Fact[] facts);
    public TSort Make<TSort>(Term<TSort> term) where TSort : struct, ISort<TSort>;
    public bool Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>;
}

// Rules/SplitRule.cs
public sealed class SplitRule
{
    public string Name { get; }
    public ScopeTag CaseTag { get; }
    public IReadOnlyList<ScopeTag> ReadScopes { get; }
    public override string ToString();
}

// Rules/SplitBuilder.cs
/// Body (When/Where), the element set to split over (Cases), the key (default: every named body variable except the set
/// variable), and the assumptions written into each case. Obligation: whenever the body matches in a scope C, C entails
/// "the cases' assumptions for the elements of the set at this match, one of them holds", and that stays true as C grows
/// (it does for a lattice-meet domain).
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

// Rules/Query.cs
public sealed class Query { public string Name { get; } public IReadOnlyList<Term> Variables { get; } public override string ToString(); }

// Rules/QueryBuilder.cs
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

// Rules/QueryCallback.cs
/// Return false to stop receiving matches.
public delegate bool QueryCallback(QueryMatch match);

// Rules/QueryMatch.cs
public readonly ref struct QueryMatch { public T Get<T>(Var<T> variable); public ulong GetBits(Term variable); }

// Rules/RuleDefinitionException.cs
public sealed class RuleDefinitionException : Exception
{
    public RuleDefinitionException(string ruleName, string message);
    public string RuleName { get; }
}

// Rules/RuleEvaluationException.cs
public sealed class RuleEvaluationException : Exception
{
    public RuleEvaluationException(string ruleName, string bindings, string message, Exception? inner);
    public string RuleName { get; }
    /// "e=#17 p=\"kind\""
    public string Bindings { get; }
}
```

#### Scheduling/

```csharp
// Scheduling/Schedule.cs
/// egglog schedules, plus case splits.
public abstract class Schedule
{
    private protected Schedule();
    /// One iteration over the union of the rulesets' rules.
    public static Schedule Run(params Ruleset[] rulesets);
    /// Up to times passes of Sequence(steps); stops after a pass with no change.
    public static Schedule Repeat(int times, params Schedule[] steps);
    /// Passes of Sequence(steps) until one changes nothing.
    public static Schedule Saturate(params Schedule[] steps);
    public static Schedule Sequence(params Schedule[] steps);
    public static Schedule Split(SplitRule rule, Schedule inCase);
    public static Schedule Split(IReadOnlyList<SplitRule> rules, Schedule inCase);
    /// Runs body unless every fact of condition already holds (egglog :until).
    public static Schedule Until(Schedule body, params Fact[] condition);
    public override string ToString();
}

// Scheduling/RunOptions.cs
public sealed class RunOptions
{
    public static RunOptions Default { get; }
    /// Rule iterations in every scope, cases included.
    public int IterationLimit { get; init; } = 10_000;
    public TimeSpan? TimeLimit { get; init; }
    /// Sum of Version deltas over every scope the run iterates in.
    public long ChangeLimit { get; init; } = long.MaxValue;
    /// Top-level scope only; cases always stop.
    public bool StopOnContradiction { get; init; }
    /// false: every rule matches in full every iteration (differential tests).
    public bool SemiNaive { get; init; } = true;
    public int CaseLimit { get; init; } = 64;
    public int SplitDepthLimit { get; init; } = 4;
    public LiftOptions Lift { get; init; } = LiftOptions.Default;
}

// Scheduling/RunReport.cs
public sealed class RunReport
{
    public int Iterations { get; }
    /// The scope's Version moved.
    public bool Changed { get; }
    /// Completed, and the run's last iteration or split step in this scope changed nothing.
    public bool Saturated { get; }
    public RunStopReason StopReason { get; }
    public TimeSpan Elapsed { get; }
    public int CasesOpened { get; }
    /// Over CaseLimit, over SplitDepthLimit, or an empty element set (sound: fewer facts).
    public int SplitsSkipped { get; }
    /// Lifts computed (not skipped by the memo).
    public int Lifts { get; }
    /// Lift candidates dropped by CandidateLimit.
    public long LiftRowsSkipped { get; }
    /// Distinct projected matches applied, cases included.
    public IReadOnlyDictionary<Rule, long> Matches { get; }
    public override string ToString();
}

// Scheduling/RunStopReason.cs
public enum RunStopReason { Completed, IterationLimit, TimeLimit, ChangeLimit, Contradiction }
```

#### Cases/

```csharp
// Cases/CaseSet.cs
/// Host-driven cases under one parent. Obligation: the cases cover the parent. Disposing disposes every case.
public sealed class CaseSet : IDisposable
{
    public Scope Parent { get; }
    public IReadOnlyList<Scope> Cases { get; }
    /// A new case child (IsCase = true).
    public Scope Add(ScopeTag? tag = null);
    /// Lifts into Parent what every non-vacuous case proves (§3.9). InvalidOperationException with no cases. May be
    /// called repeatedly.
    public LiftReport Lift(LiftOptions? options = null);
    public void Dispose();
}

// Cases/LiftOptions.cs
public sealed class LiftOptions
{
    public static LiftOptions Default { get; }
    public int RoundLimit { get; init; } = 16;
    /// Parent-level candidates per case row (product over class columns).
    public int CandidateLimit { get; init; } = 64;
}

// Cases/LiftReport.cs
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
    /// The parent's Version moved.
    public bool Changed { get; }
    public override string ToString();
}
```

#### Extraction/

```csharp
// Extraction/CostModel.cs
/// Immutable. Head cost per constructor (default its declared Cost), optional per-row cost, value-column cost
/// (default 1, as egglog's base_value_cost). All costs must be >= 0.
public sealed class CostModel
{
    public static CostModel Default { get; }
    public CostModel With(ConstructorHandle head, long cost);
    public CostModel With(ConstructorHandle head, NodeCostFunction cost);
    public CostModel WithValueCost(Func<ColumnType, ulong, long> cost);
    public CostModel Unextractable(ConstructorHandle head);
}

// Extraction/NodeCostFunction.cs
public delegate long NodeCostFunction(NodeView node);

// Extraction/NodeView.cs
public readonly ref struct NodeView
{
    public ConstructorHandle Head { get; }
    public int Arity { get; }
    public T Get<T>(int column);
    public ulong GetBits(int column);
}

// Extraction/Extractor.cs
/// Cheapest terms in one clean scope snapshot; reusable for many roots. Throws InvalidOperationException once the scope
/// or any ancestor has changed (Version).
public sealed class Extractor
{
    public Scope Scope { get; }
    public CostModel Costs { get; }
    /// InvalidOperationException when no finite term exists.
    public ExtractedTerm Extract<TSort>(TSort root) where TSort : struct, ISort<TSort>;
    public bool TryExtract<TSort>(TSort root, [NotNullWhen(true)] out ExtractedTerm? term) where TSort : struct, ISort<TSort>;
    public bool TryGetCost<TSort>(TSort root, out long cost) where TSort : struct, ISort<TSort>;
}

// Extraction/ExtractedTerm.cs
/// One node of an extracted term; shared subterms are the same instance.
public sealed class ExtractedTerm
{
    public ConstructorHandle Head { get; }
    /// Canonical in the extractor's scope.
    public Id Class { get; }
    /// Tree cost.
    public long Cost { get; }
    public int Arity { get; }
    public bool Is(ConstructorHandle head);
    public bool IsChild(int index);
    public ExtractedTerm Child(int index);
    public T Value<T>(int index);
    /// ExtractedTerm or the decoded value.
    public object Argument(int index);
    public bool Mentions(ConstructorHandle head);
    /// (StrEq "kind" "order")
    public override string ToString();
}
```

### 2.3 src/Eggnog internal types (one per file)

```csharp
// Values/
internal sealed class CodecColumnType<T> : ColumnType<T> { public CodecColumnType(string name, IValueCodec<T> codec); }
internal sealed class SortColumnType<TSort> : ColumnType<TSort> where TSort : struct, ISort<TSort> { public SortColumnType(string name); } // Kind Id
internal sealed class Int64Codec : IValueCodec<long> { }        // unchecked((ulong)v)
internal sealed class Int32Codec : IValueCodec<int> { }         // sign-extended
internal sealed class BooleanCodec : IValueCodec<bool> { }      // 0 / 1
internal sealed class DoubleCodec : IValueCodec<double> { }     // -0.0 -> +0.0; NaN -> ArgumentException; raw bits
internal sealed class StringCodec : IValueCodec<string> { }     // ordinal interning
internal sealed class EnumCodec<TEnum> : IValueCodec<TEnum> where TEnum : struct, Enum { }
internal sealed class InternedCodec<T> : IValueCodec<T> where T : notnull { public InternedCodec(IEqualityComparer<T>? equality, IComparer<T>? order); }
internal sealed class ValueSetCodec<T> : IValueCodec<ValueSet<T>> where T : notnull { }
internal sealed class ValueInterner<T> where T : notnull
{
    public ValueInterner(IEqualityComparer<T> comparer);
    public ulong Intern(T value);
    public bool TryGet(T value, out ulong handle);
    public T this[ulong handle] { get; }
    public int Count { get; }
}
internal sealed class ChunkedList<T> { public int Count { get; } public T this[int index] { get; } public int Add(T item); } // lock-free reads
internal static class ValueSetOrder<T> { public static IComparer<T> Comparer { get; } }
internal static class LatticeAdapter<T>
{
    public static MergeFunction Merge(ValueLattice<T> lattice, ColumnType<T> type);
    public static MergeFunction? CaseJoin(ValueLattice<T> lattice, ColumnType<T> type);
}

// Terms/
internal enum TermShapeKind { Variable, Constant, Application, Computation }
internal readonly struct TermShape
{
    public TermShapeKind Kind { get; }
    public Type ClrType { get; }
    public string? Name { get; }
    public object? Owner { get; }
    public object? Constant { get; }
    public ColumnType? ConstantType { get; }
    public ulong ConstantBits { get; }
    public TableHandle? Table { get; }
    public Primitive? Primitive { get; }
    public Term[] Arguments { get; }
}
// every Term overrides: internal abstract TermShape Shape { get; }
internal sealed class ConstantTerm<T> : Term<T> { public ConstantTerm(T value); }
internal sealed class BitsConstantTerm : Term { public BitsConstantTerm(ColumnType type, ulong bits); }
internal sealed class ApplicationTerm<T> : Term<T> { public ApplicationTerm(TableHandle table, Term[] arguments); }
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
internal sealed class ScopedFact : Fact { public ScopeTag Scope { get; } public Fact[] Facts { get; } }
internal static class PrimitiveCache<T> where T : notnull { /* static readonly fields backing Primitives.*<T>() */ }
// Primitive declares: internal abstract ulong InvokeCore(ReadOnlySpan<ulong> arguments, ColumnType[] parameters, ColumnType result);

// Engine/
internal sealed class BoundPrimitive
{
    public BoundPrimitive(Primitive primitive, ColumnType[] parameters, ColumnType result);
    public Primitive Primitive { get; }
    /// Decode, call, encode (interns); a bool result is 0/1.
    public ulong Invoke(ReadOnlySpan<ulong> arguments);
}
internal enum TermRefKind { Register, Constant }
internal readonly record struct TermRef(TermRefKind Kind, int Index);
internal readonly record struct ConstantSpec(ColumnType Type, ulong Bits, int Slot); // Id constants canonicalized per run in Slot
internal sealed class AtomSpec { public int Ordinal; public TableHandle Table; public int Slot; public TermRef[] Columns; }
internal sealed class ComputeSpec { public BoundPrimitive Primitive; public TermRef[] Arguments; public int Target; } // Target -1: filter
internal sealed class BodyDraft
{
    public BodyDraft(EGraph graph, string name, object owner);
    public Var<T> AddVariable<T>(string name);
    public Term AddVariable(ColumnType type, string name);
    public void AddClauses(IEnumerable<Fact> facts);
    public void AddFilter(Term condition);
}
internal static class BodyCompiler
{
    /// Normalises, checks (§3.3), plans (§3.4).
    public static CompiledBody Compile(BodyDraft draft, ISet<int> headRegisters);
}
internal sealed class CompiledBody
{
    public string Name { get; }
    public int RegisterCount { get; }
    public ColumnType[] RegisterTypes { get; }
    public string[] RegisterNames { get; }
    public int[] NamedRegisters { get; }
    public int[] RegisterHomeSlot { get; }         // -1 for value registers
    public AtomSpec[] Atoms { get; }
    public ComputeSpec[] Computes { get; }
    public ConstantSpec[] Constants { get; }
    public ScopeTag?[] Slots { get; }               // slot 0 = null = the running scope
    public int[] IdConstantIndexes { get; }
    public JoinPlan[] Plans { get; }                // Plans[i] focuses Atoms[i]; one plan (FocusAtom -1) when there are no atoms
}
internal enum StepKind { Scan, ScanColumn, Lookup, Contains, Filter, LetBind, LetCheck, NotEqual }
internal enum ColumnOpKind { Skip, Bind, Check, CheckConstant }
internal readonly record struct ColumnOp(ColumnOpKind Kind, int Index);
internal sealed class PlanStep
{
    public StepKind Kind;
    public int Atom;
    public int Slot;
    public TableHandle? Table;
    public int IndexColumn;
    public TermRef IndexSource;
    public TermRef[] KeySources;
    public ColumnOp[] Columns;
    public ColumnOp Output;
    public ComputeSpec? Compute;
    public TermRef Left;
    public TermRef Right;
}
internal sealed class JoinPlan { public int FocusAtom { get; } public PlanStep[] Steps { get; } }
internal static class JoinPlanner { public static JoinPlan[] PlanAll(CompiledBody body); }
internal sealed class Matcher
{
    public Matcher(CompiledBody body);  // allocates registers, key/arg buffers, one RowAction per step, once
    public void Run(JoinPlan plan, Context[] slots, ulong[] constants, long since, MatchSink sink); // since applies to the focus atom
}
internal abstract class MatchSink { public abstract void Accept(ReadOnlySpan<ulong> registers); public virtual bool IsSatisfied => false; }
internal sealed class MatchBuffer : MatchSink
{
    public MatchBuffer(int[] projection);   // deduplicates projected tuples
    public int Count { get; }
    public ReadOnlySpan<ulong> this[int index] { get; }
    public void Clear();
}
internal sealed class CountingSink : MatchSink { public CountingSink(int[] projection, bool stopAtFirst); public long Count { get; } }
internal sealed class CallbackSink : MatchSink { public CallbackSink(CompiledBody body, QueryCallback callback); }
internal sealed class RowArena
{
    public RowArena(int width);
    public int Count { get; }
    public bool TryAdd(ReadOnlySpan<ulong> row, out int index);
    public ReadOnlySpan<ulong> this[int index] { get; }
    public void Clear();
}
internal enum ActionKind { Compute, GetOrAdd, Insert, InsertWithOutput, Union, Callback }
internal sealed class ActionStep
{
    public ActionKind Kind;
    public TableHandle? Table;
    public TermRef[] Inputs;
    public int Output;
    public BoundPrimitive? Primitive;
    public RuleCallback? Callback;
}
internal sealed class ActionProgram
{
    public int RegisterCount { get; }
    public int[] Projection { get; }
    public ActionStep[] Steps { get; }
    public bool HasCallback { get; }
    public bool WritesAnalysis { get; }
    public void Execute(Scope target, ReadOnlySpan<ulong> match, ulong[] registers, string ruleName);
}
internal static class ActionCompiler { public static ActionProgram Compile(CompiledBody body, IReadOnlyList<object> headClauses, string ruleName); }
internal readonly record struct RuleProgress(long Since, ulong[] Constants);
internal static class RuleRunner { public static bool RunIteration(ScheduleExecutor executor, Scope scope, IReadOnlyList<Rule> rules); }
internal static class ScopeResolver
{
    public static Scope? TryResolve(Scope from, ScopeTag tag);
    public static Scope Resolve(Scope from, ScopeTag tag, string owner);
}
internal static class TermEvaluator
{
    public static ulong Write(Scope scope, Term term);
    public static void Assume(Scope scope, Fact fact);
    public static bool TryRead(Scope scope, Term term, out ulong bits);  // never creates, never interns; classes canonical in scope
    public static bool Check(Scope scope, Fact fact);
}
internal static class SExpression
{
    public static string Render(Term term);
    public static string Render(Fact fact);
    public static string Render(Rule rule);
    public static string Render(SplitRule rule);
    public static string Render(Schedule schedule);
}

// Scheduling/ (each node implements:
//   internal abstract bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase);
//   internal abstract void Collect(ISet<Ruleset> rulesets, ISet<SplitRule> splits);)
internal sealed class RunSchedule : Schedule { public Ruleset[] Rulesets { get; } }
internal sealed class RepeatSchedule : Schedule { public int Times { get; } public Schedule[] Steps { get; } }
internal sealed class SaturateSchedule : Schedule { public Schedule[] Steps { get; } }
internal sealed class SequenceSchedule : Schedule { public Schedule[] Steps { get; } }
internal sealed class SplitSchedule : Schedule { public SplitRule[] Rules { get; } public Schedule InCase { get; } }
internal sealed class UntilSchedule : Schedule { public Schedule Body { get; } public Fact[] Condition { get; } }
internal sealed class ScheduleExecutor
{
    public ScheduleExecutor(Scope top, RunOptions options, CancellationToken cancellationToken);
    public RunOptions Options { get; }
    public RunStatistics Statistics { get; }
    public Matcher MatcherFor(Rule rule);
    public MatchBuffer BufferFor(Rule rule);
    public IReadOnlyList<Rule> RulesFor(Ruleset ruleset, Scope scope);
    public void BeforeIteration(Scope scope);   // limits; throws ScheduleStopException
    public void Track(SplitRecord record);
    public void DisposeSplits();                // depth-first, reverse creation order; resets split progress of their parents
    public RunReport Complete(RunStopReason reason, Stopwatch clock);
}
internal sealed class ScheduleStopException : Exception { public ScheduleStopException(RunStopReason reason); public RunStopReason Reason { get; } }
internal sealed class RunStatistics
{
    public int Iterations;
    public int CasesOpened;
    public int SplitsSkipped;
    public int Lifts;
    public long LiftRowsSkipped;
    public long Changes;
    public bool LastTopStepChanged;
    public RunStopReason StopReason;
    public Dictionary<Rule, long> Matches;
}

// Cases/
internal readonly struct SplitKey : IEquatable<SplitKey>
{
    public SplitKey(int splitRuleIndex, ulong[] values);
    public bool Equals(SplitKey other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
}
internal sealed class SplitRecord
{
    public SplitRecord(SplitRule rule, SplitKey key, Scope parent, Scope[] cases, int depth);
    public SplitRule Rule { get; }
    public SplitKey Key { get; }
    public Scope Parent { get; }
    public IReadOnlyList<Scope> Cases { get; }
    public int Depth { get; }
    public long[]? LastSignature { get; set; }
    public void Dispose();
}
internal static class SplitRunner { public static bool Execute(ScheduleExecutor executor, SplitSchedule node, Scope scope, int depth); }
internal sealed class MemberMap
{
    public static MemberMap Build(Context @case);
    public int GroupCount { get; }
    public IEnumerable<ulong[]> Groups { get; }                // parent ids per case class, groups of size >= 2
    public ReadOnlySpan<ulong> Members(ulong caseCanonical);   // empty for a case-local class
}
internal static class ClassPartition { public static List<ulong[]> Meet(MemberMap reference, IReadOnlyList<Context> others); }
internal static class Lifter
{
    public static LiftReport Lift(Scope parent, IReadOnlyList<Scope> cases, LiftOptions options, long[]? lastSignature, out long[] signature);
}

// Extraction/
internal sealed class ExtractionGraph
{
    public int Discover(Scope scope, ColumnType rootType, ulong root, CostModel costs);   // returns the root class index
    public int ClassCount { get; }
    public int NodeCount { get; }
    public ulong ClassId(int @class);
    public int ClassOf(int node);
    public ConstructorHandle HeadOf(int node);
    public ReadOnlySpan<ulong> RowOf(int node);
    public ReadOnlySpan<int> ParentsOf(int @class);
}
internal readonly record struct NodeKey(long Cost, int Node);
internal sealed class NodeOrder : IComparer<NodeKey> { public NodeOrder(ExtractionGraph graph); public int Compare(NodeKey x, NodeKey y); }
```

Engine state held in `Scope` (private fields in `Scope.Engine.cs`):

```csharp
Dictionary<int, RuleProgress> progress;
Dictionary<int, RuleProgress> splitProgress;
List<SplitRecord> splitRecords;
long createdAt;            // Store.Clock read right after Context.CreateChild returns
Rule[] localRules;         // copy-on-write
int depth;                 // case nesting
long serial;
```

### 2.4 src/Eggnog.Tests files

- `Cosmos/Pred.cs`, `Cosmos/CosmosTheory.cs`, `Cosmos/CosmosSql.cs`, `Cosmos/SweepLinks.cs`
- `Cosmos/Scenario01DiscriminatedOneOfTests.cs` … `Cosmos/Scenario07SweepLinksTests.cs`
- `Engine/SemiNaiveDifferentialTests.cs`, `Engine/RandomProgram.cs`, `Engine/ViewSnapshot.cs`
- `Engine/RuleDefinitionTests.cs`, `Engine/ScopeIsolationTests.cs`, `Engine/IdConstantTests.cs`, `Engine/InheritanceTests.cs`, `Engine/ActionTests.cs`, `Engine/ScheduleTests.cs`, `Engine/LocalRuleTests.cs`, `Engine/CleanlinessTests.cs`, `Engine/RenderingTests.cs`, `Engine/ConcurrencyTests.cs`
- `Cases/LiftTests.cs`, `Cases/SplitTests.cs`
- `Extraction/ExtractionTests.cs`
- `Values/ValueTests.cs`, `Values/LatticeTests.cs`

---

## 3. Semantics and algorithms

### 3.1 Values, interning, primitives, lattices

- **Invariant V.** For every column type, `Encode` is injective, so equal bits mean equal values in every context. Value joins compare bits, and that is sound only under V.
  - Strings and other interned types: one `ValueInterner<T>` per type per graph. Handles are dense and never reused.
  - `ValueSet<T>`: interned by structural equality. A set's elements are sorted, so equal content gives the same handle.
- **Interning.**
  - `Intern`: check the dictionary. On a miss, take the lock, check again, append to the `ChunkedList`, publish, then add to the dictionary.
  - Decoding is lock-free and does not allocate.
  - Rule constants are interned when the rule is defined.
  - **Host reads use `TryEncodeExisting` and never intern.** A constant that was never interned makes its fact false or its term absent.
- **Primitives.** `BoundPrimitive.Invoke` decodes each argument register with the column type resolved at compile time, calls the delegate, and encodes the result, which interns a new value.
  - Allocation per candidate is none beyond what the lambda allocates and what a new interned value costs.
  - A primitive may not take or return a sort (definition error). Classes are compared only with `Fact.Equal` and `Fact.NotEqual`.
- **Lattice adapters.**
  - `LatticeAdapter<T>.Merge` gives `(x, y) => type.Encode(lattice.Merge(type.Decode(x), type.Decode(y)))`, passed to `TableSchema.Lattice`. Storage calls it only when `x != y`.
  - `CaseJoin` is adapted the same way for the lifter.
  - The laws are documented and checked by `ValueLattice<T>.Verify`.

| Function | `Merge` | `CaseJoin` |
|---|---|---|
| `dom`, `types` (`ValueLattice.Intersect`) | ∩ | ∪ |
| known-set (`ValueLattice.Union`) | ∪ | ∩ |
| `lo` (`ValueLattice.Max`) | max | min |
| `hi` (`ValueLattice.Min`) | min | max |

### 3.2 Scopes, tags, resolution, local rules

- **Resolution.** `resolve(S, tag)` is the first scope in `S, S.Parent, …` whose `Tag` is `tag` (by reference). `ScopeTag.Root` always resolves to `EGraph.Root`.
  - `Run` resolves the read scopes of every rule and split rule reachable from the schedule, from the scope it was called on, before doing any work.
  - An unresolved tag throws `InvalidOperationException`: *"Rule 'declared tautology' reads scope 'schema', but 'root/query#4' has no ancestor tagged 'schema'."*
  - A case resolves every tag its parent resolves, unless the case's own tag shadows one; then it resolves to the case.
- **Rules a run sees.** For each ruleset step, `RulesFor(ruleset, S)` returns:
  1. the ruleset's global rules (a snapshot, in definition order);
  2. then, for each scope from the root down to `S`, its local rules belonging to that ruleset.

  Rules are recomputed every iteration, so rules defined concurrently join at the next iteration.
- **Invariant R (writes are never scoped).**
  - A scoped handle in `Then`, `Make`, `Rewrite`, `Assume` (split or host) or `ScopeWriter` is a definition error (or an `ArgumentException` for host writes).
  - Actions write only to the running scope, and an ancestor's view never contains its descendants' rows.
  - So nothing a rule derives in `S` can change what an atom scoped to a strict ancestor of `S` reads.

### 3.3 Compiling a rule

`Ruleset.Rule`, `Scope.DefineRule`, `EGraph.DefineSplit` and `DefineQuery` run `define` on a fresh builder and compile immediately. Every error is a `RuleDefinitionException` that names the rule.

1. **Variables.**
   - Each `Var<T>` records its owner builder; a variable from another builder is an error.
   - Duplicate names are an error.
   - Each variable gets a register with `ValueStore.TypeOf<T>()`. An unknown T is an error naming `Register*`.
2. **Read scopes.**
   - Slot 0 is the running scope.
   - Each distinct tag, from `handle.In(tag)` or `Fact.In(tag, …)`, gets a slot.
   - `Fact.In` gives its tag to every application inside it.
   - Nesting `Fact.In`, or a handle scoped to a different tag inside `Fact.In`, is an error.
3. **Normalisation.** `flatten(t)` turns a term into an operand:
   - a variable becomes its register (after unification);
   - a constant becomes `TermRef.Constant` (encoded, with the slot recorded for class constants);
   - an application `h(args)` in slot `s` becomes a fresh register `o` plus the atom `h@s(flatten(args)…, o)`. Applications with the same `(h, s, operands)` share `o`: functions are functional, so common subexpressions are shared.
   - a computation becomes a fresh register bound by a `Let`.

   Facts normalise as follows:

   | Fact | Becomes |
   |---|---|
   | relation fact | atom |
   | `a.Is(b)`, two variables | unify `a` and `b` (a register union-find) |
   | `x.Is(constant)` | substitute the constant |
   | `x.Is(h(args))`, either side | atom with output `flatten(x)` |
   | `h(…).Is(g(…))` | shared output register |
   | `x.Is(compute)` | `Let` |
   | `compute.Is(constant)` | equality filter |
   | `constant.Is(constant)` | evaluated now; false is an error ("never matches") |
   | `Exists(h(args))` | atom with a fresh, unused output register |
   | `NotEqual`, `True`, `Where` | filters |

4. **Range restriction.** Every register used by a filter, a `Let`, an action, or a split key or set must be bound by an atom or by a `Let` whose inputs are bound. Otherwise: *"variable 'p' is not bound by any When clause"*. `Make` variables may not appear in the body.
5. **Invariant H (class values stay in their home scope).**
   - For every Id-typed register, collect the slots of the atoms it appears in. More than one slot is an error: *"variable 'e' (Pred) is read in scope 'schema' and in the running scope; a class value can be joined only within one scope"*.
   - That slot is the register's home slot.
   - `Fact.Equal` and `Fact.NotEqual` on classes require the same home slot.
   - Id constants are canonicalized per run in the slot of the atom that uses them.

   Why H is necessary, the counterexample (also a regression test, §4.3): `C` merges `y1` and `y2`, and `y1` wins. A C-own row `S(y1)` keeps its stamp, and the ancestor row `R(y2)` keeps its stamp. If `R@A(y)` could join `S(y)` through `C`'s equality, the match `R@A(y2) ⋈ S(y1)` would be new, yet no row would have been restamped, so semi-naive matching would miss it. Worse, it would join schema facts through an equality the query derived, which is exactly what "the schema alone" has to exclude.
6. **Erasing (opt-in).** With `Erasing(L)`, every atom over a `TableRole.Fact` table (including `Contradiction`) must be in a slot tagged `L`. Atoms over `Term` and `Analysis` tables are unrestricted.
   - Combined with R and H, an erasing rule reads facts only from `L`'s view and from term and analysis tables.
   - Residue, documented: it can still see class equalities its running scope derived. In the Cosmos theory those equalities are lowerings justified by conjuncts that survive extraction. That is the prototype's argument, now confined to equalities.
7. **Analysis writers.** A rule whose actions write an `Analysis` table may contain no atoms over `Fact` tables, no scoped atoms, and no callbacks. So analyses depend only on terms.
8. **Actions.** Evaluating a head term into a register:
   - a variable or constant is a register or a constant reference;
   - a constructor application is `GetOrAdd`;
   - a computation is `Compute`;
   - a function application is an error (*"actions do not read; match 'dom' in When"*);
   - a scoped handle is an error (R).

   Head facts:

   | Fact | Op |
   |---|---|
   | relation fact | `Insert` |
   | `F.Of(k).Is(v)`, either side | `InsertWithOutput` (lattice merge) |
   | `C.Of(k).Is(x)` | `InsertWithOutput` (storage unions the outputs) |
   | `a.Is(b)` on classes | `Union` |
   | equality of two values that is not a function write | error |

   Ops run in declaration order, post-order within a fact.
9. **Projection.**
   - The buffer stores the registers the actions read.
   - With a callback it stores every register.
   - For a query, it stores every named register.
10. **Plans.** `JoinPlanner.PlanAll` (§3.4).

### 3.4 Planning and matching

**Plans.**

- There is one plan per atom: `Plans[i]` starts with `Atoms[i]` as the focus.
- A body with no atoms gets one plan with `FocusAtom = -1`, and matches once, when `W == 0`.
- The step order is greedy:

```
B := ∅
place filters, LetBinds and LetChecks whose inputs are constants
place focus atom i:
    Scan, or ScanColumn on its first constant column
    (preference: Id key column, then Value key column, then the output)
loop:
    place every ready compute
        a filter            -> Filter / NotEqual
        a Let, target ∉ B   -> LetBind
        a Let, target ∈ B   -> LetCheck
    if every atom is placed: break
    next := argmax over unplaced atoms of
            (lookupable ? 2 : anyBound ? 1 : 0,
             bound Id key columns, bound columns, −arity, −ordinal)
        lookupable: a function or constructor with every key column bound,
                    or a relation with every column bound
    access: Lookup (TryLookup, then bind or check the output) or Contains (relation)
            if lookupable;
            else ScanColumn on the preferred bound column;
            else Scan
    column ops: constant -> CheckConstant; register in B (or earlier in this row) -> Check;
                new register -> Bind
```

**Which plan runs.**

- A semi-naive variant `i` runs `Plans[i]` with `since = W` on the focus.
- A full run uses `Plans[argmin est(i)]` with `since = 0`, where `est(i)`:
  - is `ApproximateCount(slot context, table) / 16` when the atom has a constant column;
  - is `ApproximateCount` otherwise;
  - breaks ties by ordinal.

**Matcher.** Allocated once per rule per `Run`, and cached by the executor:

- `ulong[] registers`, a key buffer and an argument buffer sized to the maximum arity;
- `RowAction[] onRow`, with `onRow[k] = row => Step(k, row)`.

```
Run(plan, slots, constants, since, sink):
    store the arguments in fields; Continue(0)

Continue(k):
    if k == steps.Length: sink.Accept(registers); return
    if sink.IsSatisfied: return
    step := steps[k]; ctx := slots[step.Slot]; s := (step.Atom == plan.FocusAtom) ? since : 0
    Scan:       ctx.Scan(table, onRow[k], s)
    ScanColumn: ctx.ScanColumn(table, col, value(IndexSource), onRow[k], s)
    Lookup:     fill the key buffer; if ctx.TryLookup(table, key, out o): apply the Output op to o; Continue(k+1)
    Contains:   fill the key buffer; if ctx.Contains(table, key): Continue(k+1)
    Filter:     if Invoke(args) != 0: Continue(k+1)
    NotEqual:   if value(Left) != value(Right): Continue(k+1)
    LetBind:    registers[t] = Invoke(args); Continue(k+1)
    LetCheck:   if registers[t] == Invoke(args): Continue(k+1)

Step(k, row):
    for each column op: Bind writes; Check / CheckConstant compare (return on mismatch); Skip does nothing
    Continue(k+1)
```

- Registers bound at step `k` are read only by later steps, so backtracking needs no undo.
- Nested reads inside the callbacks rely on the documented storage guarantee.
- Nothing writes during matching. Path compression in `Find` touches only union-find maps, never a `RowSet` being iterated.

**Invariant C (canonical identifiers).** Every Id written to a register is canonical in its home slot's context, and constants are canonicalized at the start of the run in their slot. So class joins compare bits.

- Rows of a context's view are canonical in that context: storage invariant 1, plus the fact that unshadowed ancestor rows mention only classes the context left alone.
- `TryLookup` returns canonical outputs.
- When a class flows into an action in the running scope `S`, it is a valid identifier there without translation. An id canonical in an ancestor names a class of `S`, and `Insert`, `Union` and `GetOrAdd` canonicalize through `FindRaw`.

### 3.5 Semi-naive bookkeeping

**State.**

- `progress[rule.Index] = RuleProgress(Since W, Constants K)` per scope.
- Split rules use `splitProgress`, which is never inherited.
- An iteration reads `T = Store.Clock` after `S.Rebuild()` and before any matching.

**Matching rule `r` in clean scope `S`.**

```
slots[0] = S.Context; slots[k] = resolve(S, tag_k).Context
K' = canonical Id constants (slots[slot].Find(c))
P = progress[r] ?? Inherit(S, r) ?? (0, K')
W = (P.Constants sequence-equals K') ? P.Since : 0          // invariant K
if (Options.SemiNaive == false || r.IsNaive || W == 0):
    run the full plan with since 0
else:
    for each atom i:
        if slots[slot_i].LatestStamp(table_i) <= W: continue // nothing new anywhere in that view
        run Plans[i] with since W                            // focus: changed since W; others: everything
pending[r] = (T, K')            // committed only after the apply phase and Rebuild succeed
```

**Inherit(S, r)**, for plain rules that are not naive and have no callback:

```
if S.Parent is null → null
if S.Tag ≠ null and r.ReadScopes contains S.Tag → null       // a tag would resolve differently
pp = S.Parent.progress[r] ?? Inherit(S.Parent, r); if pp is null → null
if pp.Constants ≠ K' → null
return (min(pp.Since, S.createdAt), K')
```

**Invariants and why they hold.**

- **S1 (stamp completeness, from storage).** If context `X` is clean at `t0` and again at `t1 > t0`, every row of `X`'s view at `t1` with stamp ≤ `t0` was in `X`'s view at `t0` with the same content.
  - Rows enter a view only through `RowSet.Add` or `SetOutput`, each with a fresh `Tick()`. Re-keying kills the row and adds it again.
  - Shadowing only grows: marks only grow and `Rekey` maps them forward. If an own row with key `k` is killed, `k` mentions a merged loser. That loser is either marked, which keeps the ancestor row shadowed, or local, in which case no ancestor row has it.
  - Compaction keeps stamps.
- **K (constants).** If an Id constant's canonical id changed, rows that now match it may be old. Hence the full run.
- **SN (watermark).** After `progress[r] = (W, K)` is committed, every match of `r` in `S`'s current view whose rows are all stamped ≤ `W` (with `K` unchanged) has been applied in `S` or an ancestor, and its effects are visible in `S`.
  - *Proof, by induction on runs.* The run that committed `W` searched a clean view at clock `W`. By S1, applied to `S` and to every slot context, a match whose rows are all ≤ `W` existed then with the same rows; Id canonical forms are unchanged because the rows are unchanged and `K` is equal.
  - That run was either full, and found everything, or ran variants against `W_prev`. In the second case the match either had every row ≤ `W_prev` (applied earlier, by induction) or had some atom `i` with a row > `W_prev`. Variant `i` reads atom `i` since `W_prev` and every other atom in full, so it is complete.
  - Its actions were applied and `S` was rebuilt before `W` was committed. Contexts are monotone, so the effects persist.
- **INH (inheritance is sound).** Let `W0 = min(pp.Since, c)`, where `c = S.createdAt`.
  - Every row of `S`'s view stamped ≤ `c` is not an own row of `S`, because own rows are stamped after `c`. So it is an unshadowed row of the parent's view, with the same content and the same canonical ids.
  - Scoped slots resolve to the same contexts from the parent (the tag check).
  - So a match in `S` over rows ≤ `W0` is a match in the parent over rows ≤ `pp.Since`, with the same `K` (checked). By SN in the parent (or recursively up the chain) it was applied there, and the parent's facts are in `S`'s view.
- **Duplicates.**
  - A match with several changed rows is found by several variants. `MatchBuffer` deduplicates projected tuples within the iteration, and actions depend only on the projection.
  - In a later iteration a restamped copy (marking, re-keying) can deliver an old match again. Built-in actions are idempotent; callbacks are documented to be idempotent too (D21).
- **Concurrency.** Sibling threads tick the shared clock only for rows outside `S`'s view, so `T` remains an upper bound for `S`'s view.

### 3.6 One iteration: actions and change detection

```
RuleRunner.RunIteration(executor, S, rules):
    executor.BeforeIteration(S)            // limits, cancellation
    S.Context.Rebuild(); T = Store.Clock; v0 = S.Context.Version
    for r in rules (in the order given): clear buffer[r]; match r (§3.5) into buffer[r]
    for r in rules:
        for m in buffer[r] (discovery order):
            r.Actions.Execute(S, m, scratch, r.Name)
            // a storage InvalidOperationException becomes RuleEvaluationException(r, bindings)
    S.Context.Rebuild()                    // a conflict found during congruence propagates as storage's InvalidOperationException
    commit pending progress for every rule; Statistics.Matches[r] += buffer[r].Count
    Statistics.Changes += S.Context.Version − v0
    return S.Context.Version != v0
```

- **Order.** Rules, then matches, then ops, in declaration or discovery order, so runs are deterministic. Every built-in action is a monotone insertion: a row, a lattice merge, a union, or a get-or-add. So the facts after `Rebuild` do not depend on order; only fresh ids and representatives do, through storage's min-id winner.
- **Get-or-add before rebuild** may miss a key that is not yet canonical and create a duplicate class. `Rebuild` merges the two (storage: "late rather than wrong").
- **Change detection uses `Version`, never `Store.Clock`.** Siblings tick the clock, and no-op writes do not move `Version`.
- **Failure.** If anything throws, nothing is committed. The scope may be dirty and partly written. Re-running after `Rebuild()` finds the same matches again (D18).

### 3.7 Schedules and limits

`Execute(node, S, depth, inCase)` returns "changed in `S`". Changes inside cases count for the parent only through lifts.

| Node | Behaviour |
|---|---|
| `Run(rs…)` | If `(inCase or Options.StopOnContradiction) and S.IsContradictory`: return false; at the top level also record `StopReason = Contradiction`. Otherwise one `RunIteration` over the concatenated `RulesFor`. |
| `Sequence` | Each step in order; the result is the OR of the steps. |
| `Repeat(n, s…)` | Up to `n` passes of `Sequence`; stop after a pass with no change. |
| `Saturate(s…)` | Passes until one reports no change. |
| `Until(b, f)` | If every fact of `f` holds (checked like `Check`), return false; otherwise `b`. |
| `Split(rules, inCase)` | §3.8. |

- **Limits.** `BeforeIteration` checks, in order:
  1. cancellation, which throws `OperationCanceledException` after cleanup;
  2. `IterationLimit`, `TimeLimit`, `ChangeLimit`, which throw `ScheduleStopException(reason)`.

  `Scope.Run` catches the stop exception. In `finally`, it calls `DisposeSplits()` and rebuilds the scope, then returns the report. Stopping early is sound: the scope has fewer facts, each of them valid.
- **Saturated** = `StopReason == Completed && !LastTopStepChanged`. `LastTopStepChanged` is set by every iteration and split step executed in the top-level scope.

### 3.8 Case splits

**Rule-driven: `SplitRunner.Execute(executor, node, S, depth)`.**

```
require S writable; S.Context.Rebuild()
for each split rule s in node.Rules:
    match s semi-naively in S with splitProgress (no inheritance) into a deduplicating buffer
    for each match m:
        key = SplitKey(s.Index, m[key registers])
        if S has a live record with key: continue                   // the existing cases still cover S
        elements = decode(m[set register]), in element order
        if elements.Count == 0 or elements.Count > CaseLimit or depth >= SplitDepthLimit:
            SplitsSkipped++; continue
        cases = for each element v:
            K = S.CreateChild(s.CaseTag) as a case (depth + 1)
            registers = m plus the element register := encode(v)
            execute s.Assumptions in K; K.Rebuild()
        record = new SplitRecord(s, key, S, cases, depth + 1); S.splitRecords.Add(record); executor.Track(record)
        CasesOpened += cases.Length
    commit splitProgress[s] = (T, K')        // no action touches S
changed = false
for each record of S, in creation order:
    for each case K: K.Rebuild(); if K.IsContradictory: continue
        Execute(node.InCase, K, depth + 1, inCase: true)
    report = Lifter.Lift(S, record.Cases, Options.Lift, record.LastSignature, out sig)
    record.LastSignature = sig; changed |= report.Changed
return changed
```

- **Coverage** is the split rule's obligation (stated on `SplitBuilder` and `DefineDomainSplit`). For the domain split:
  - the body establishes "`k` is present and its value ∈ `d`";
  - domains only narrow, so cases made for an older, wider `d` still cover `S`;
  - a case for a value since removed becomes vacuous once it syncs, because its domain meets to ∅ and the contradiction rule fires.
- **Persistence.** A record lives until its `Run` ends. On every later execution of the split step, the in-case schedule takes in the parent's new facts through the case's watermarks, which are inherited lazily from the parent and then the case's own.
- **Disposal.** `DisposeSplits()` disposes records depth-first in reverse creation order: a case first disposes its own records. It then clears `splitProgress` in each parent, so a later `Run` matches every split again from scratch.
- **Host-driven.** `OpenCases()` returns a `CaseSet`. `CaseSet.Lift` calls `Lifter.Lift` without a memo. `Scope.Split<T>` is open, add, assume, run with `StopOnContradiction`, lift, dispose. An empty or over-limit `values` throws `ArgumentException`.

### 3.9 Lift

**Inputs.**

- A writable parent `P`.
- Cases `K1..Kn`, children of `P`, n ≥ 1 (otherwise `InvalidOperationException`: *"a lift needs at least one case; with none, the scope would be declared contradictory — assume Contradiction explicitly if that is meant"*).
- Options.

**Precondition (Cov, the caller's obligation).** Every model of `P` satisfies some case's assumptions.

**Algorithm.**

```
rounds = 0
loop:
    P.Rebuild(); each K.Rebuild()
    signature = [P.Version, K1.Version, …]
    if rounds == 0 and signature == lastSignature: return unchanged
    Live = { K : ¬K.Contains(Contradiction) }
    if Live is empty:
        if ¬P.Contains(Contradiction): P.Insert(Contradiction); P.Rebuild()
        ParentContradicted = true; break
    maps = MemberMap.Build(K) for each K in Live:
        groups: c ↦ sorted { m | K.ScanMerges reports (m, c) }   // local ids are already excluded
        Members(c) = groups[c] if present; else [c] if ¬K.IsLocalId(c); else []
    r = the live case with the fewest groups
    // 1. equalities: a full partition meet
    blocks = ClassPartition.Meet(maps[r], Live \ {r})
        // split each group of maps[r] by the signature (Kj.Find(m))_{j≠r}; keep blocks of size ≥ 2
    pending unions: block[0] with each other member
    // 2. rows: candidates from r's own rows
    for each table h with h.Lift, h ≠ Contradiction, in declaration order:
        copy the rows of r.ScanOwn(h) into an arena
        for each row: candidate parent rows φ = product over Id columns (keys only for functions and constructors)
                      of maps[r].Members(column)
            if some column has no members: skip (it mentions a case-local class)
            if the product exceeds CandidateLimit: Skipped++, skip
            deduplicate φ in a RowArena
        for each candidate φ (key k for functions and constructors):
            relation:    if ¬P.Contains(h, φ) and ∀ K ∈ Live: K.Contains(h, φ)   → pending Insert(h, φ)
            constructor: if ¬P.Contains(h, k) and ∀ K ∈ Live: K.Contains(h, k)   → pending GetOrAdd(h, k)
            function:    if ∀ K ∈ Live: K.TryLookup(h, k, out v_K):
                             J = h.CaseJoin ? fold(CaseJoin, v_K) : (all v_K equal ? v : skip)
                             if ¬(P.TryLookup(h, k, out p) and Merge(p, J) == p) → pending Insert(h, k, J)
    if no pending writes: break
    apply the unions, then the inserts and get-or-adds; P.Rebuild(); rounds++
    if rounds == RoundLimit: break
signature = the final [P.Version, case versions…] after rebuilding
```

`K.Contains(h, φ)` and `K.TryLookup` canonicalize φ's parent ids in `K` (storage `Canonicalize` through `FindRaw`).

**Why it is right.**

- **L1 (soundness).** A lifted fact holds in every live case. Vacuous cases have no models. By Cov, every model of `P` is a model of a live case, so the fact holds in `P`.
  - For a case-joined value: the true value satisfies `v_K` in its case, and by absorption `v_K ⊑ J`, so `J` holds in `P`.
  - If every case is vacuous, `P` has no models.
  - Creating terms in `P` (`GetOrAdd`) asserts nothing, so it is always sound.
- **L2 (translation).** `K` entails the parent-level fact `R(Y)` iff `R(Find_K(Y))` is in `K`'s view. A view is closed under congruence and contains `P`'s facts canonicalized. The `IsLocalId` filter keeps classes that exist only in a case out of `P`, even when only one case is live.
- **L3 (completeness of candidates).** If a parent-level relation fact holds in `r` but not in `P`, then its translation is an own row of `r`.
  - An unshadowed row of `P` mentions only unmarked classes, whose canonical id is the same in `r`.
  - For a function key that `r` does not hold, `r`'s value is `P`'s value, and the join adds nothing to it, by absorption.
  - The candidate limit may lose completeness, never soundness.
- **L4 (completeness of equalities).** Take parent-canonical `y1 ≠ y2` that are equal in every live case.
  - In `r`, each is either marked (merged) or the canonical id of a merged class. Both are marked by `UnionCore`.
  - So both are in one group of `maps[r]`, and they survive refinement.
  - The partition is fully refined, not tested in representative pairs.
- **Term lift.** A constructor key present in every case gets a fresh class `x` in `P`.
  - Each case holds its own row with that key, so when it syncs, `Absorb` sends it to `MergeInto` and congruence merges `x` with the case's output.
  - In the next round, the meet lifts `x ~ e` iff every case had its output equal to `e`.
  - Terms nested over case-local classes therefore lift bottom-up, one level per round.
- **Termination.** Each round adds rows, classes or merges to `P` that come from the cases' finite contents. `RoundLimit` is a backstop.

### 3.10 Extraction

**Discovery** from `r = S.Find(root)`, a breadth-first search over `(class, sort)`:

- For each extractable constructor `h` whose output sort is the class's sort, call `S.ScanColumn(h.Table, outputColumn, x, …)` and copy each row into the arena as a node, keeping its head.
- Each Id key column of a node names a child class, whose sort is the column's type.
- Discovery is closed downward, so a later root extends the graph without invalidating classes already final.

**Costs.**

- `head(n)` = `CostModel` head cost (constant or `NodeCostFunction`).
- `values(n)` = Σ `ValueCost` over the node's value key columns.
- Saturating addition; `long.MaxValue` means unreachable.
- A negative cost throws `InvalidOperationException`.

**Knuth's algorithm.**

```
pending[n] = the number of class-column occurrences of node n
parents[class] = the occurrences of that class
queue = a priority queue of NodeKey, ordered by NodeOrder; seed every node with pending == 0 at head + values
while queue not empty:
    pop (cost, n); x = class(n); if final[x]: continue
    final[x] = true; best[x] = n; cost[x] = cost; rank[x] = next++
    for each occurrence of x as a child of node p:
        if --pending[p] == 0 and ¬final[class(p)]:
            push (head(p) + values(p) + Σ cost[child classes of p], p)
```

- **Optimal.** Sums of non-negative costs are superior functions (Knuth 1977).
- **Acyclic, even with zero costs.** A node is pushed only after all its children are final, so the finalization order is a topological order of the chosen nodes.
- **Cycles.** A class whose only nodes depend on itself never finalizes. `TryExtract` returns false, and `Extract` throws *"class #41 has no finite term in 'root/query#2'"*.
- **Tie-break (`NodeOrder`).** Compare cost, then `head.Index`, then the columns left to right: class columns by canonical id (older classes first), value columns by `ColumnType.Compare` (content order). Two nodes equal in all of these are the same row.
  - With positive head costs this yields the lexicographically least spelling among equal-cost alternatives.
  - With zero head costs it is still deterministic for a given history.
  - In both 02 and 07, the spelling as written wins over its commuted twins, because the host made its subterms first.
- **Defaults.** Declared cost, value columns cost 1, tree cost. This is egglog's `TreeAdditiveCostModel` with `base_value_cost = 1`, so `StrEq "a" "b"` costs 3.
- **Term reconstruction** is iterative and memoised per class into shared `ExtractedTerm` instances.
- **Staleness.** The extractor records the `Version` of `S` and of each ancestor context. Any later use after a change throws.

### 3.11 Host reads and writes

`TermEvaluator`:

- **Write** works post-order:
  - a constant is `Encode` (interns);
  - a constructor application is `GetOrAdd`;
  - a function application, a computation, or a scoped handle is an `ArgumentException`.

  `Assume` follows the head-fact table of §3.3, step 8.
- **TryRead** never creates and never interns:
  - a constant is `TryEncodeExisting` (a miss means absent);
  - a constructor or function application is `TryLookup` on the resolved slot context (`In(tag)` is honoured; the slot is resolved from the reading scope);
  - a class result is mapped through `S.Context.Find`, which is valid because an id canonical in an ancestor names a class of `S`;
  - a computation is an `ArgumentException`.
- **Check**:
  - a relation fact uses `Contains`;
  - an equality fact means both sides exist and their bits are equal (classes canonical in `S`);
  - `Exists(app)` means the application exists;
  - filters are rejected.

### 3.12 Concurrency

- Definitions are serialised under a lock and published copy-on-write; compiled rules and plans are immutable.
- `ValueStore` and the interners are thread-safe.
- `Scope` is single-threaded; executors, matchers and buffers are per `Run`.
- Lazy inheritance reads a parent's `progress` from a child's thread only when that parent is frozen, and a frozen scope cannot run, so nothing writes it. An unfrozen parent shares its child's thread.
- Local rules are fixed once a scope is frozen.
- User lambdas (primitives, lattices, cost functions, callbacks) must be pure and thread-safe. This is documented on every parameter that takes one.

### 3.13 Errors at a glance

| Condition | Result |
|---|---|
| Read while not clean | `InvalidOperationException` naming `Rebuild()` and `Scope.Path` |
| Write, `Run`, split or lift on a frozen scope; `DefineRule` on a frozen scope | `InvalidOperationException` |
| Unresolved tag at `Run` | `InvalidOperationException` (before any work) |
| Definition errors (§3.3) | `RuleDefinitionException` (rule name) |
| Merge conflict or primitive exception in an action | `RuleEvaluationException` (rule name, bindings) |
| Merge conflict found during the congruence pass | storage `InvalidOperationException` |
| Lift with no cases; `Split` with no values or over the limit | `InvalidOperationException` / `ArgumentException` |
| No finite term | `InvalidOperationException` (`TryExtract` returns false) |

### 3.14 Mapping a future `.egg` frontend (not in v1)

| egglog | Eggnog |
|---|---|
| `sort` / `datatype` | `DefineSort` (a frontend-generated `ISort`), `DefineConstructor` (untyped) |
| `relation`, `function :merge` | untyped `Define*` with `MergeFunction` |
| `rule`, `rewrite` | `RuleBuilder` with `Var(ColumnType, name)`, `Fact.Equal`, `RelationHandle.Apply`, `Compute.Of(Primitive, …)`, `Rewrite` |
| `run` / `run-schedule` | `Schedule.*` |
| `check`, `fail (check …)` | `Scope.Check` |
| `push` / `pop` | `CreateChild` / `Dispose` |
| `extract` | `Extract` |

Proposed extensions: `(in tag facts…)` maps to `Fact.In`; `:case-join` maps to `caseJoin`; `split-rule` maps to `DefineSplit`. `delete` and `subsume` are rejected.

---

## 4. Test plan

### 4.1 Prototype ports (`Cosmos/`)

Every `check` becomes an `Assert.True`, and every `(fail (check …))` an `Assert.False`. Each container is compiled with `SweepLinks.Compile` or with a helper like the one in §5:

1. `new EGraph()`, then `new CosmosTheory(graph)`;
2. `graph.Root.Freeze()`;
3. `schema = Root.CreateChild(Schema)`;
4. `Assume` the schema facts and `DeclareBranch` its rules;
5. `schema.Run(c.Facts)`, then `schema.Freeze()`.

Each query runs in its own `schema.CreateChild(Query)`.

**`Scenario01DiscriminatedOneOfTests`**

- `ADeclaredStoredFormPutsTheComparisonInTheStatement`: branch rule kind = B ⇒ `form(ref)`. Run `Saturate(Horn, Rewrite)`. Assert `form(ref)`, `AreEqual(q, And(StrEq(kind,B), StrEq(ref,R)))`, and the extracted string.
- `WithoutTheDiscriminatorNothingIsProvenAndNothingLowers`: `q = UuidEq(ref,R)` with `Saturation`. Assert no `form`, and that the extraction mentions `UuidEq`.

**`Scenario02QueryDisjunctionTests`**: §5.

**`Scenario03CombiningClaimsTests.ADomainLessARefusedValueProvesTheGuard`**: `Saturate(Horn, Rewrite)` gives no `form`. `Saturate(Horn, Lattice, Rewrite)` gives `Get(dom kind) == {order}`, `form`, and `AreEqual`.

**`Scenario04UndiscriminatedAnyOfTests`**

- `WithoutTheSplitTheBranchesNeverMeet`: schema compiled with `Saturate(Horn, Lattice)`. Assert `¬schema.Check(dom(status) = {active, archived, draft})` and `¬AreEqual(q, PFalse)`.
- `AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains`: schema compiled with `Facts`. Assert `schema.Get(dom status) == {active, archived, draft}`, no `present(legacyId)`, and no `present(id2)`. For the query with `Saturation`: `IsContradictory`, `AreEqual(q, PFalse)`, and `Extract(q)` is `"(PFalse)"`.

**`Scenario05IntervalsTests`**

- `AComparisonBeyondADeclaredBoundIsFalse`: `IsContradictory`, `AreEqual(q, PFalse)`.
- `ABoundInsideTheRangeIsLeftAlone`: `¬IsContradictory`, `Get(lo n) == 51`.

**`Scenario06GuardDeletionTests`**

- `ADeclaredTypeMakesAnOrderingComparisonExact`: `AreEqual(q, StrGe)`, cost 3.
- `WithoutADeclaredTypeTheComparisonIsStillWeakened`.
- `ADeclaredTautologyIsNotAsked`: `AreEqual(q, StrEq(id,x))`, extracted string.
- `WithoutAPresenceClaimTheComparisonStays`: `query.Check(present kind)`, `¬schema.Check(present kind)`, `¬AreEqual(q, StrEq(id,x))`.

**`Scenario07SweepLinksTests`**

- `SweepLinks.Compile` asserts 3 schema cases.
- `TheMapQueryLowers`.
- `ParkAndMapWrittenInLowerTheParkId`: `form`, `AreEqual`.
- `ParkAndMapSpeltPerKindLowerTheParkId`: `Get(dom data.type) == {map, park}`, `CasesOpened == 2`, `form`, `AreEqual`, cost 15, the rendered SQL and the s-expression, `query.Children` empty.
- `TheOfflineRefreshLowersEverything`: `AreEqual` with `StrGt` on `changeUtcTime`.
- `TheFacadeFilterDropsTheWholeEnum`: `AreEqual(q, StrEq(type, Link))`.
- `ASpotMayBeAmongTheRowsSoNothingLowers`: dom `{park, spot}`, no `form`, extraction mentions `UuidEq`.

### 4.2 Differential test: semi-naive against naive (`Engine/SemiNaiveDifferentialTests.cs`)

`RandomProgram` generates, from a seed:

- **Tables**: 2–4 relations, 1–2 constructors over sort `Node`, and 1–2 functions with `ValueLattice.Max<long>` or `Intersect<int>`. Small pools: 6 strings, 8 longs.
- **Rules (5–12)**: joins of 1–4 atoms; value-keyed scoped atoms (`In(tagA)`); class variables that respect H; Id constants taken from host `Make`; filters and lets (lambdas and named primitives); rewrites; occasional `Naive()`; local rules on random scopes.
- **Scopes**: up to 4 deep with tags; ancestors written between child runs; frozen prefixes; nested `Run` calls with `Saturate` and `Repeat`.

Each program runs three times:

1. `SemiNaive = true`;
2. `SemiNaive = false`;
3. `SemiNaive = true` with the internal `InheritProgress = false`.

After every run, every scope's `ViewSnapshot` is compared: relation rows, function values, and the equalities among all visible ids, modulo canonical id choice. 300 seeds run by default; `EGGNOG_SEEDS` sets more. A failing program prints itself.

### 4.3 Targeted tests

- **`ScopeIsolationTests`**
  - `ACrossScopeClassJoinIsRejected`: the H counterexample `R@A(y) ⋈ S(y)` fails at definition, naming `y`.
  - `AScopedJoinUsesTheAncestorsEqualities`: `R@A(y), T@A(y)` with `C` merging `a` and `b` does not match. The unscoped rule does.
  - `ValueKeyedScopedAtomsRunIncrementally`: watermarks advance, and variants over a frozen ancestor are skipped (a counter on `LatestStamp` skips).
  - `WritesToAScopedHandleAreRejected`.
- **`IdConstantTests.AConstantThatLosesAUnionSeesTheWinnersOldRows`**: invariant K.
- **`InheritanceTests`**
  - `ChildRowsWrittenBeforeTheParentRunsAgainAreNotSkipped`: the regression for the bug judges found in consumer-first's inheritance.
  - `AChildTaggedWithAReadScopeStartsAfresh`.
  - `CallbackRulesDoNotInherit`.
  - `InheritanceChainsThroughUnrunAncestors`.
- **`RuleDefinitionTests`**: one test per error message. Unbound variable; another builder's variable; scoped write; function read in a head; primitive over a sort; `Erasing` violation; analysis writer violation; cross-scope class join; never-matching constant equality; `Make` variable in `When`; unknown CLR type; `Fact.In` nested.
- **`ActionTests`**
  - Get-or-add duplicates merged by rebuild.
  - `C.Of(k).Is(x)` unions.
  - Lattice writes merge.
  - A merge conflict raises `RuleEvaluationException` with the rule name and bindings.
  - Callbacks see full matches and are deduplicated within an iteration.
  - A failed run commits no progress, and a re-run finds the matches again.
- **`ScheduleTests`**: `Repeat` early stop; `Saturate`; `Sequence`; `Until`; `Saturated`; `IterationLimit`, `TimeLimit` and `ChangeLimit` stop reasons; cancellation; `StopOnContradiction` at top level and forced in cases; tags validated before work.
- **`LocalRuleTests`**: a rule local to container A never fires in sibling B; it is gone after disposal; `DefineRule` on a frozen scope throws; one `CosmosTheory` serves two containers.
- **`CleanlinessTests`**: every read throws after a union and names `Rebuild()`; `Run` leaves the scope clean; reads never rebuild.
- **`RenderingTests`**: the tautology rule's `ToString` includes `present@schema` and `:erasing schema`; named primitives render by name, lambdas as `<lambda>`.
- **`ConcurrencyTests`**: 32 queries under one frozen container on parallel threads give the same extractions as sequential runs.
- **`LiftTests`**
  - `PartitionMeetIsFull`: `b ~ c` in all cases while `a` separates in one, so `b ~ c` lifts.
  - Case-joins.
  - Absorption skip.
  - Vacuous cases skipped.
  - All vacuous means the parent is contradicted.
  - Zero cases throw.
  - `OneLiveCaseDoesNotAliasCaseLocalClasses`.
  - `TermsMadeOnlyInCasesLiftInRounds`.
  - `CandidateLimitSkipsSoundly`.
  - Lift memo skips when nothing changed.
- **`SplitTests`**
  - `PersistentCasesAbsorbLaterParentFacts`.
  - `NarrowingMakesAStaleCaseVacuous`.
  - `SplitProgressResetsAfterTheRun`: a second `Run` splits again.
  - `CaseLimitAndDepthLimitSkip`.
  - `EmptyElementSetSkips`.
  - `DefineDomainSplitMatchesTheHandWrittenRule`.
  - `NestedSplits`.
  - `CasesAreDisposedWhenARunStopsEarly`.
- **`ExtractionTests`**: no finite term; zero-cost nodes with children; ties keep the spelling as written; per-row costs; unextractable heads; value costs; DAG sharing; stale extractor throws; `Mentions`.
- **`ValueTests`**: codec round trips; double normalisation (−0.0, NaN throws); `ValueSet` operations and order; `ValueSet.Of("abc")` is `{"abc"}`; concurrent interning; `Check` with an unseen string leaves `InternedCount` unchanged.
- **`LatticeTests`**: `Verify` accepts the built-ins and rejects a mismatched case-join (for example ∩ with ∩).

---

## 5. Scenario 02 against the final API

```csharp
// src/Eggnog.Tests/Cosmos/Pred.cs
using Eggnog.Storage;

namespace Eggnog.Tests.Cosmos
{

    /// <summary>
    /// A predicate over a document: the e-class sort of the pushdown theory.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Pred(Id Id) : ISort<Pred>
    {

        /// <inheritdoc />
        public static Pred FromId(Id id) => new(id);

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/CosmosTheory.cs
using System;

namespace Eggnog.Tests.Cosmos
{

    /// <summary>
    /// The theory of prototypes/cosmos-facts/prelude.egg, defined once per graph and shared by every container compiled
    /// into it. The prototype's context argument and its inheritance rules are gone: scopes are the contexts.
    /// </summary>
    public sealed class CosmosTheory
    {

        /// <summary>Tags a container's schema scope.</summary>
        public static readonly ScopeTag Schema = new("schema");

        /// <summary>Tags a query scope.</summary>
        public static readonly ScopeTag Query = new("query");

        /// <summary>Tags one case of a discriminator split.</summary>
        public static readonly ScopeTag Case = new("case");

        static readonly ValueSet<string> StringOrNull = ValueSet.Of("string", "null");

        /// <summary>
        /// Defines the theory in a graph.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public CosmosTheory(EGraph graph)
        {
            Graph = graph;
            graph.DefineSort<Pred>("Pred");

            // Claims about a path.
            Present = graph.DefineRelation<string>("present");
            Form = graph.DefineRelation<string, string>("form");
            Ne = graph.DefineRelation<string, string>("ne");
            Discriminator = graph.DefineRelation<string>("discriminator");
            Dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Types = graph.DefineFunction<string, ValueSet<string>>("types", ValueLattice.Intersect<string>());
            Lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            Hi = graph.DefineFunction<string, long>("hi", ValueLattice.Min<long>());

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
            Rewrite = graph.DefineRuleset("rewrite");
            DefineHorn();
            DefineLattice();
            DefineRewrite();

            // One case per value a present discriminator can still hold: the cases cover the scope.
            DiscriminatorSplit = graph.DefineDomainSplit("discriminator", Discriminator, Dom, guard: Present, caseTag: Case);

            var split = Schedule.Split(DiscriminatorSplit, Schedule.Saturate(Horn, Lattice));
            Facts = Schedule.Saturate(Horn, Lattice, split);
            Saturation = Schedule.Saturate(Horn, Lattice, split, Rewrite);
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

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

        /// <summary>Gets the rewrite ruleset.</summary>
        public Ruleset Rewrite { get; }

        /// <summary>Gets the discriminator split.</summary>
        public SplitRule DiscriminatorSplit { get; }

        /// <summary>Gets the schedule a schema is compiled with: facts and splits, no rewrites.</summary>
        public Schedule Facts { get; }

        /// <summary>Gets the schedule a query runs: everything, interleaved, as the prototype's (saturate horn lattice split rewrite).</summary>
        public Schedule Saturation { get; }

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
            Horn.Rule("a where clause is a conjunct", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(WhereClause.Of(e));
                r.Then(Conjunct.Of(e));
            });
            Horn.Rule("conjuncts of and", r =>
            {
                var (a, b) = r.Vars<Pred, Pred>("a", "b");
                r.When(Conjunct.Of(And.Of(a, b)));
                r.Then(Conjunct.Of(a), Conjunct.Of(b));
            });
            Horn.Rule("inset of equality", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(StrEq.Of(p, v)));
                r.Then(InSet.Of(e, p).Is(Primitives.Singleton<string>().Of(v)));
            });
            Horn.Rule("inset of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(InSet.Of(a, p)), sb.Is(InSet.Of(b, p)));
                r.Then(InSet.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });
            Horn.Rule("inset admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(s.Is(InSet.Of(e, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            foreach (var left in new[] { true, false })
            {
                Horn.Rule(left ? "and admits left" : "and admits right", r =>
                {
                    var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                    var s = r.Var<ValueSet<string>>("s");
                    r.When(e.Is(And.Of(a, b)), s.Is(Admits.Of(left ? a : b, p)));
                    r.Then(Admits.Of(e, p).Is(s));
                });
            }
            Horn.Rule("admits of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(Admits.Of(a, p)), sb.Is(Admits.Of(b, p)));
                r.Then(Admits.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });

            // What a conjunct establishes, written to the running scope: the query.
            Horn.Rule("a conjunct admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(Conjunct.Of(e), s.Is(Admits.Of(e, p)));
                r.Then(Present.Of(p), Dom.Of(p).Is(s));
            });
            Horn.Rule("a negated equality", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.When(Conjunct.Of(Not.Of(StrEq.Of(p, v))));
                r.Then(Present.Of(p), Ne.Of(p, v));
            });
            foreach (var comparison in new[] { UuidEq, StrGe, InstantGt })
            {
                Horn.Rule($"{comparison.Name} proves presence", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Conjunct.Of(comparison.Of(p, v)));
                    r.Then(Present.Of(p));
                });
            }
            Horn.Rule("a numeric bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumGt.Of(p, n)));
                r.Then(Present.Of(p), Lo.Of(p).Is(Primitives.Add.Of(n, 1L)), Types.Of(p).Is(ValueSet.Of("number")));
            });
        }

        void DefineLattice()
        {
            Lattice.Rule("a domain settles the type", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Then(Types.Of(p).Is(ValueSet.Of("string")));
            });
            Lattice.Rule("a form describes strings", r =>
            {
                var (p, f) = r.Vars<string, string>("p", "f");
                r.When(Form.Of(p, f));
                r.Then(Types.Of(p).Is(StringOrNull));
            });
            Lattice.Rule("a disequality narrows a domain", r =>
            {
                var (p, v, d) = r.Vars<string, string, ValueSet<string>>("p", "v", "d");
                r.When(Ne.Of(p, v), d.Is(Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Lattice.Rule("an empty domain", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(Present.Of(p), d.Is(Dom.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(d));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.Rule("no possible type", r =>
            {
                var (p, t) = r.Vars<string, ValueSet<string>>("p", "t");
                r.When(Present.Of(p), t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.Rule("crossed bounds", r =>
            {
                var (p, l, h) = r.Vars<string, long, long>("p", "l", "h");
                r.When(Present.Of(p), l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Graph.Contradiction.Of());
            });
        }

        void DefineRewrite()
        {
            // Judged in the running scope: the guard that proved the form is a conjunct that stays in the predicate.
            Rewrite.Rule("lower uuid", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(UuidEq.Of(p, v)), Form.Of(p, "uuid-lower"));
                r.Then(e.Is(StrEq.Of(p, v)));
            });
            Rewrite.Rule("lower instant", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(InstantGt.Of(p, v)), Form.Of(p, "instant-ms"));
                r.Then(e.Is(StrGt.Of(p, v)));
            });
            Rewrite.Rule("contradicted predicate", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(WhereClause.Of(e), Graph.Contradiction.Of());
                r.Then(e.Is(PFalse.Of()));
            });

            // Deletions read facts only from the schema, and the definition checks it: a conjunct's own consequences in
            // the query (present(kind) from kind = 'A') can never delete it.
            Rewrite.Rule("declared tautology", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.Erasing(Schema);
                r.When(s.Is(InSet.Of(e, p)), Present.In(Schema).Of(p), d.Is(Dom.In(Schema).Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(d, s));
                r.Then(e.Is(PTrue.Of()));
            });
            Rewrite.Rule("declared presence", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                r.Erasing(Schema);
                r.When(e.Is(IsDefined.Of(p)), Present.In(Schema).Of(p));
                r.Then(e.Is(PTrue.Of()));
            });

            // NOT IS_STRING admits nothing a present string-or-null path holds: it is NOT IS_DEFINED.
            Rewrite.Rule("a string path's NOT IS_STRING", r =>
            {
                var (e, p, t) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "t");
                r.When(e.Is(Not.Of(IsString.Of(p))), t.Is(Types.Of(p)));
                r.Where(t, t => t.IsSubsetOf(StringOrNull));
                r.Then(e.Is(Not.Of(IsDefined.Of(p))));
            });
            Rewrite.Rule("a definedness test absorbs its negation", r =>
            {
                var (p, x) = r.Vars<string, Pred>("p", "x");
                r.Rewrite(And.Of(IsDefined.Of(p), Or.Of(Not.Of(IsDefined.Of(p)), x)), And.Of(IsDefined.Of(p), x));
            });
            foreach (var comparison in new[] { StrGe, StrEq })
            {
                Rewrite.Rule($"{comparison.Name} implies defined", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.Rewrite(And.Of(IsDefined.Of(p), comparison.Of(p, v)), comparison.Of(p, v));
                });
            }

            // Boolean simplification.
            Rewrite.Rule("and commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(And.Of(a, b), And.Of(b, a)); });
            Rewrite.Rule("or commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(Or.Of(a, b), Or.Of(b, a)); });
            Rewrite.Rule("true and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PTrue.Of(), a), a); });
            Rewrite.Rule("false and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PFalse.Of(), a), PFalse.Of()); });
            Rewrite.Rule("false or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PFalse.Of(), a), a); });
            Rewrite.Rule("true or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PTrue.Of(), a), PTrue.Of()); });
        }

    }

}
```

```csharp
// src/Eggnog.Tests/Cosmos/Scenario02QueryDisjunctionTests.cs
using Xunit;

namespace Eggnog.Tests.Cosmos
{

    /// <summary>
    /// prototypes/cosmos-facts/02-query-disjunction.egg: kind IN ('order', 'invoice') lowers the UUID comparison on ref
    /// only through a case split, and the schema alone still proves nothing about ref, because a note carries none.
    /// </summary>
    public sealed class Scenario02QueryDisjunctionTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// Compiles the container: kind is required and one of order, invoice, note; order and invoice declare ref a
        /// canonical lowercase UUID.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();                                    // empty and frozen: containers are siblings beneath it

            var schema = graph.Root.CreateChild(CosmosTheory.Schema);
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind"));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));

            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(3, compiled.CasesOpened);                  // the schema's own split: order, invoice, note
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Empty(schema.Children);
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
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(CosmosTheory.Query);
            var q = AssertQuery(c, query);

            // (run-schedule (saturate horn lattice rewrite))
            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.Equal(0, report.CasesOpened);
            Assert.Equal(ValueSet.Of("invoice", "order"), query.Get(c.Dom.Of("kind")));   // the domain narrows...
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));                     // ...but no single fact proves a guard
            Assert.False(query.AreEqual(q, Lowered(c)));
        }

        [Fact]
        public void TwoKindsNamedTogetherLowerTheReference()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(CosmosTheory.Query);
            var q = AssertQuery(c, query);

            // (run-schedule (saturate horn lattice split rewrite))
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
            Assert.False(query.Check(c.Form.In(CosmosTheory.Schema).Of("ref", "uuid-lower")));

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
            using var query = schema.CreateChild(CosmosTheory.Query);
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
                    var k = cases.Add(CosmosTheory.Case);
                    k.Assume(c.Dom.Of("kind").Is(ValueSet.Of(kind)));
                    k.Run(Schedule.Saturate(c.Horn, c.Lattice), new RunOptions { StopOnContradiction = true });
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

    }

}
```

**What happens when the scenario runs.**

1. **Schema compilation.** It splits three ways. The note case lacks `form(ref)`, so nothing about `ref` lifts. The domain join `{order} ∪ {invoice} ∪ {note}` equals the schema's own domain, so the lift is skipped by absorption. The three cases are disposed when `Run` returns, and the schema's split progress is reset.
2. **Query without the split.** `admits` sees through `And` and `Or`, so `dom(kind)` narrows to `{order, invoice}` in the query. The branch rules need a singleton domain, so neither fires, and `lower uuid` has no `form` to match.
3. **Query with the split.**
   - The domain split matches `(kind)`, which yields two persistent cases.
   - Each case asserts its singleton domain, inherits the query's watermarks, and fires its branch rule, which is local to the schema and active in every descendant. Each therefore derives `form(ref, uuid-lower)` and `types(ref) = {string, null}`.
   - The lift writes both rows into the query.
   - `lower uuid` then unions `UuidEq(ref, R)` with `StrEq(ref, R)`.
   - The next pass changes nothing: the lift memo skips, so the run is saturated.
4. **Extraction.** Ties among the commuted `And` and `Or` spellings go to the lower canonical child id. The host's `Make` built the subterms in post-order, so the spelling as written wins. In the `UuidEq` class, `StrEq` costs 3 against 1002 for `UuidEq`.

---

## 6. Residual risks

These are accepted for v1. None is an open question: each has a decision above.

1. **Coverage cannot be checked.** It is stated on `SplitBuilder`, `DefineDomainSplit`, `OpenCases`, `CaseSet` and `Scope.Split`, and a lift with no cases throws. A wrong domain encoding in a schema compiler is still a silent soundness bug in that theory.
2. **The erasing guarantee does not cover equalities.** It covers facts (§3.3, step 6). Equalities the running scope derived through lowerings rest on the justifying conjuncts surviving into the extracted predicate. A debug auditor could check this later: re-extract the predicate and compare it on sample documents.
3. **Pools only grow** (D15). A long-lived service should recycle its `EGraph` once `InternedCount` passes a threshold. Recompiling a container is cheap.
4. **Static planning has no statistics** beyond the first atom of a full run (D26). A pathological body may need its atoms reordered by hand, since ties follow rule order.
5. **Term lift costs rounds.** It costs one round per nesting level, and `CandidateLimit` bounds the product of member lists. Cosmos lifts only value-keyed facts, so term lift is exercised only by `LiftTests`.
6. **Termination.** A lattice with infinite descending chains, such as i64 bounds combined with arithmetic rules, never saturates. `IterationLimit`, `TimeLimit` and `ChangeLimit` stop the run soundly, and the report says why. The host should then decide with what has been proven so far.
7. **Purity cannot be enforced.** Primitives, lattices, cost functions and callbacks must be pure and thread-safe, and callbacks idempotent. This is documented on every parameter that takes one, and `ValueLattice<T>.Verify` covers the lattice laws in tests.
8. **No rollback** (D18). After an exception the scope's contents are unspecified, but no watermark was committed. The guidance is to call `Rebuild()` and run again, or to dispose the scope.
