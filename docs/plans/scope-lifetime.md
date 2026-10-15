# Scope lifetime

This plan answers the owner's question: is there any reason to dispose a scope by hand, rather than rely on the
garbage collector?

Today there is a reason, and it is a defect. A child scope that is never disposed is never collected. Its parent and
the engine hold it, so dropping every reference to it changes nothing. It keeps its memory, about 3 KB to 30 KB per
scope. It keeps the live-descendant count of every ancestor raised, so a writable ancestor that freezes keeps its dead
rows for good. It keeps one engine rule per local rule it defined. None of this is an unmanaged resource. It is shared
bookkeeping in the parent and the graph, and only an explicit release undoes it. The owner is right that a .NET library
with no unmanaged resources should clean up after a dropped object. This plan shows how: the parent's bookkeeping
points at an internal core and never at the public `Scope`; when the `Scope` is collected, a small private finalizable
object queues the release, and the release code that `Dispose` uses today runs off the finalizer thread. `Dispose`
stays, as an optional way to release early and deterministically.

The owner also asked whether this replaces any of the decisions made for PR #2 (branch `fix-disposal-race`, merged
into `main` as `06207a2`). In part, yes. Phase 0 of this plan replaces PR #2's whole code change, the held and
owner exemptions, and reverses its two documented decisions: that a class of a disposed scope is still refused in a
live descendant, and that an `Until` in a descendant's run then throws mid-run. What survives is PR #2's goal: a call
in progress may still use its scope's classes after a concurrent `Dispose`. Phase 0 meets that goal with a broader
rule (a disposed scope's classes stay usable in its live descendants until it is released), and 2 of PR #2's 7 tests
stay as they are. The call counting that makes a release wait for calls in progress (`Scope.BeginCall` and `EndCall`,
and the calls check in `TryRelease`) is not PR #2's: it was on `main` before it, and the GC model depends on it
(section 3.1). Phase 0 does not need the GC work, so it can land first, as a change on `main` that removes PR #2's
code. No package includes PR #2 yet, so its two decisions are reversed before any release ships them.

The owner has answered the questions of section 9 as the plan recommended, and extended the requirement to queries: a
dropped `Query` is released after a collection like a scope, and a query's name becomes a label (section 3.5).

## 1. Summary

**Recommendation: Do, in three parts. Confidence: medium on the mechanism until the experiment of section 6 runs on
approach C itself (the prototypes used a different mechanism). The owner accepted the semantics change.**

- **Phase 0, now (small): the class rule.** A class is usable in a call on its scope or on a live descendant of that
  scope. `Dispose` refuses new calls on the disposed scope and no longer kills its classes in its descendants. The
  check in `ClassChecks.Check` becomes the ancestry test alone; the disposed flag only chooses the wording of a
  refusal. All of PR #2's code goes (its `held` parameter and owner exemptions), and so does its `Scope.Run` note on
  the mid-run `ArgumentException`. PR #2 is merged, so phase 0 is a change on `main` that removes its code.
- **Phases 1 to 3, and phase 5 for the documentation (medium): scopes managed by the GC, approach C.** Lifecycle state
  moves from `Scope` into an internal `ScopeCore`. The parent lists, the engine's runtime slot, the guard's deferred
  list and the rule-variant registry point at cores, not at the public `Scope`. A private `ScopeSentinel`, owned only by
  its `Scope`, has one of the library's two finalizers, the other being the query sentinel of phase 4. It reaches
  neither the core nor the graph; it finds the core through a graph slot table and queues it, and a thread-pool step
  runs today's `TryRelease`. The release, not `Dispose`, suppresses the sentinel, so a disposed and released scope
  leaves no finalization work. Both child lists (the library's and the engine's) get constant-time removal, because they
  now also hold dropped children not yet collected. About 800 to 1,000 changed lines, about half of them tests and
  documentation. The public surface does not change.
- **Phase 4 (small): queries managed by the GC.** The graph keeps compiled queries, not `Query` objects, in a table
  keyed by serial; `CompiledQuery` loses its back-reference to its `Query`. A private `QuerySentinel`, owned only by its
  `Query`, queues the compiled query on a queue of its own beside phase 3's, and phase 3's pool drain empties both,
  disposing the compiled query as `Query.Dispose` does today. `Dispose` suppresses the sentinel. `DefineQuery` no
  longer refuses a name a live query has. About 250 changed lines, more than half of them tests.
- **What it waits on.** Only the experiment of section 6: a full-suite run on net8.0 and net10.0, Release builds, with
  a hook that forces a collection at every run observer, split step and `CreateChild`. The owner accepted the class
  rule (question 1) and one finalizer per scope in the library (question 2). Approach B' (weak child lists swept on the
  pool after each full collection, signalled by one finalizable object per graph) and approach B (no finalizer at all)
  stay recorded as the alternatives that were not chosen.

What users will see:

- Dropping every reference to a scope is enough. It is released after a collection of the generation it reached finds
  it unreachable. A scope that did real work is promoted (section 2.2), so for it that is typically the next full
  collection, which a busy process may not run for a long time.
- `Dispose` is optional. It never throws, refuses new calls at once, and releases as early as the tree allows.
- A disposed scope's classes stay usable in its live descendants.
- `Children` lists only children that are still reachable.
- Two things that are deterministic today depend on collection timing for a scope that was dropped without
  `Dispose`: compaction of a writable ancestor (at its `Freeze` and in its later writes and runs), and the frozen-tag
  refusal of a first scoped read. For a writable parent or a tagged frozen scope, `Dispose` is the practical way to
  get compaction and the tag back (section 5).
- Dropping a `Query` is enough too. Its engine rule and call sites are freed after a collection finds it. `Dispose`
  stays, for an early release.
- `DefineQuery` accepts a name a live query already has. A query's name is a label for messages and exceptions.
- A delegate the graph holds (in a rule, split rule or query body, or a lattice) keeps everything it captures for as
  long as it is registered, which for a global definition is the graph's life. So it must not capture a `Scope` or a
  `Query`, or an object that holds one, that is meant to be collected. A query's own body and a local rule's body are
  unregistered at their release, so `Dispose` frees a query or scope that its own body captures. A global body is never
  unregistered: `Dispose` still releases the engine state of a scope or query it captures, but the object itself is
  never collected (sections 2.1 and 3.4).

## 2. Current state, from the code

### 2.1 What keeps a scope alive today

A child is reachable from its parent through two independent strong paths. Each is enough on its own.

1. **The library list.** `Scope.children` (`src/Eggnog/Graph/Scope.Tree.cs:18`), added in `CreateChild` (`:124`) and
   in `Scope.AdoptCase` (`src/Eggnog/Graph/Scope.Cases.cs:79`).
2. **The engine path.** `Context.children` (`src/Eggnog/Relations/Contexts/Context.cs:74`, added in
   `Context.Lifecycle.cs:561`), then the child context's `host` (`Context.cs:188`, an `EGraphContext`), its locals
   array (`src/Eggnog/Relations/Bridge/EGraphContext.Locals.cs:19`), the `ScopeRuntime` in the graph's runtime slot,
   and `ScopeRuntime.Owner` (`src/Eggnog/Graph/ScopeRuntime.cs:40`, set at `Scope.cs:69` and `:94`, and for a
   transient query child at `Scope.Tree.cs:343`, where the owner is the frozen scope), which is the `Scope`.

A third path appears once a scope defines a local rule: `DefineRule` lowers it at once (`RuleCompiler.Compile` calls
`variants.Get`, `src/Eggnog/Rules/RuleCompiler.cs:37`), whether or not the rule ever runs:

3. **The rule-variant registry.** `EGraph.VariantsByDescription` (`src/Eggnog/Graph/EGraph.cs:180`, written at
   `src/Eggnog/Rules/RuleLowering.cs:164`) holds a `LoweredRule`, whose `Rule` (`LoweredRule.cs:61`) has an `Owner`
   (`Rule.cs:48`), the scope.

Only `Scope.TryRelease` (`Scope.Tree.cs:379-454`, reached from an explicit `Dispose`, a case release or the end of a
run) and `Context.Dispose` remove a scope from these paths.

| Holder | Keeps the scope alive? | Notes |
|---|---|---|
| Parent's `Scope.children`, engine `Context.children` chain, `VariantsByDescription` | yes, until release | the three paths above; none is ever weak |
| `ClassId` | no | it holds a `ScopeOrigin` (`ClassId.cs:18`), which holds its parent origin and the graph, never a `Scope` (`ScopeOrigin.cs:40-45`) |
| `MirrorRegistry`, `ScopeTag`, `DescendantGate` | no | counts and the graph only |
| Engine stores, cursors, `AncestorCursor`, `ContextTables` | no | they point from a child to its ancestors only |
| A descendant | yes, it keeps its ancestors | `Scope.Parent` (`Scope.Tree.cs:29`), `Context.parent` and `ancestors` (`Context.cs:69-70`) |
| Call-scoped frames | for the length of a call | `HostBatch`, `ScopeWriter` (unless kept), `ScheduleExecutor.cases` and `Top`, `SplitStepFrame`, the split records on the parent `EGraphContext` (emptied when the top-level run ends), `ScopeGuard.deferred`, the transient query child (released in a `finally`), the `[ThreadStatic]` `ExtractionCatalog.currentScope` (restored in a `finally`), a `QueryMatch` (a `readonly ref struct`, valid only during the `QueryCallback` that receives it, `src/Eggnog/Rules/QueryMatch.cs`; made only at `Scope.Reads.cs:373`) |
| User-held objects | while the user holds them | `Extractor.Scope`, `ExtractedTerm`, `NodeView`, `RunStep.Scope` (so `RunReport.Steps` with `RecordSteps`), `RunReport.Rules` keyed by `Rule`, a local `Rule` and `LocalRules`, `CaseSet.Parent` and its cases |
| `Query` | no, except through a delegate of its body (last row) | its class constants hold `ScopeOrigin`s and its tags `ScopeTag`s, never a `Scope` (read). The `Query` itself is held by the graph's `queriesByName` (`src/Eggnog/Graph/EGraph.Queries.cs:11`) until its `Dispose`, so a forgotten query is never collected either (run; section 3.5) |
| A user delegate the graph holds: in a rule, split rule or query body (`Where`, `Compute.Of`, an inline primitive), or a lattice's merge | yes, for everything it captures: a `Scope`, a `Query`, or an object that holds one (a `this` with a `Scope` or `Query` field, say) | the engine's external-function registry (`Database.externalFunctions`, `src/Eggnog/Relations/Database/Database.cs:68`) holds a `PrimitiveCall` or `FilterCall` per call site (`RuleLowering.cs:80`, `:88`, `:151`) and a `LatticeMerge` per lattice function (`EGraph.Definitions.cs:468`), each holding the delegate, until it is freed; the compiled program holds it too. A local rule's or query's call sites are freed at its release (`RuleVariants.Free`), and that release waits for a collection that a capture of its own scope or query prevents (run, section 3.5). A global rule's, a split rule's and a lattice's are never freed, so what they capture lives as long as the graph (run, section 3.5). No approach of section 3 cuts this path |

So the holders that make a dropped scope immortal are all library-owned bookkeeping. Every holder a user can see
already behaves as "alive while referenced". The exception is a user delegate the graph holds: the user wrote the
capture, but the engine keeps it for as long as the delegate is registered, which for a global rule, split rule or
lattice is the graph's life.

### 2.2 What a forgotten scope costs

All figures below were measured by running probes in private clones at `main` (`291f7be`), Release builds on net10.0,
workstation non-concurrent collector, with `GC.Collect` and `WaitForPendingFinalizers`. No time was measured. The
probes are not in the repository; the tests of phase 3 turn them into tests.

**Reachability (run).** Every child dropped without `Dispose` stayed reachable after full collections, under a
writable root and under a frozen root, whether it was bare, had made a term, had run, or had run a local rule. Every
disposed child was collected. Removing only one of the two parent paths, by reflection, was not enough. Removing both
collected a child whose class the caller still held, which confirms that a `ClassId` does not root its scope. A child
that had defined a local rule needed its `VariantsByDescription` entries removed as well, even when it never ran the
rule (one entry was registered at definition).

**Generation reached (run).** Every figure in this plan's prototypes comes from full `GC.Collect` calls, which hide
that a dropped object is found only by a collection of the generation it reached. A probe (net8.0.32 and net10.0.13,
Release, workstation non-concurrent collector, counts only) made a child of a frozen 200-term root, made N terms in it,
ran `Saturate`, and read `GC.GetGeneration` of the `Scope` after the run. With N = 0 or 50 the scope was still in
generation 0; with N = 2,000 in generation 1 (1 gen0 collection during its life); with N = 20,000 and 60,000 in
generation 2 (15/3/2 and 43/2/2 gen0/1/2 collections during its life). Both runtimes gave the same counts. So a scope
that ran a while is found only by the next full collection.

**Retained memory per forgotten child (run).** Slope of `GC.GetTotalMemory(true)` over 2,000 children, on a theory of
1 sort, 2 constructors, 1 relation, 1 global rule and 200 root terms:

| The child did | Retained if forgotten | Retained if disposed |
|---|---:|---:|
| nothing | about 3.4 KB (3,423 B under a frozen root, 3,345 B under a writable root) | 0 B (within 16 B) |
| one `Make` | about 8.9 KB | 0 B |
| one `Make` and one `Run` | about 20.0 KB | 0 B |
| a local rule, a `Make` and a `Run` | about 30.0 KB, plus one engine rule registered graph-wide (2,551 rules after 2,550 children) | 0 B, and the rule is freed |

A second probe on another theory found 6.6-6.7 KB per child that made one term (6,701,208 B for 1,000 children under
a frozen root), and 304 B in all when the same children were disposed first.

**Compaction (run, and from the colored-context plan).** Every live descendant enters the `DescendantGate` of every
ancestor (`Context.Lifecycle.cs:565`) and leaves it only in `Context.Dispose` (`:136`). While the gate counts a live
descendant, a table skips compaction, both the ordinary rehash that a writable scope's writes and runs trigger
(`MaybeRehash`, `SortedWritesTable.Rehash.cs:28`) and the forced one at `Freeze` (`ForceRehash`, `:46`). A forgotten
child counts for ever:

- One forgotten child of a writable root made the root's `Freeze` keep all 602 of its tombstones, with 1 blocked
  compaction. Without the child, `Freeze` compacted to 0. The run before it was unaffected (602 tombstones, below the
  trigger).
- On egglog-bridge's `math` workload, one held child grows the root's physical rows 1.93 times
  ([colored-context-deferred-features](./colored-context-deferred-features.md), section 3).
- At engine level the same gate refuses retractions of shared rows (`RetractionGuard.cs:133`), refuses a root clone
  (`Database.cs:566`) and keeps the store's container dirty log from being cleared (`Context.Lifecycle.cs:145`). The
  library exposes none of these (read): it calls no retraction outside `src/Eggnog/Relations` and registers no
  container ([interning-and-value-lifetime](./interning-and-value-lifetime.md), section 3.3). They are engine-only and
  not user-visible effects.

**Other effects (run unless marked).**

- A forgotten frozen scope with a tag keeps that tag's frozen count in `MirrorRegistry`. A first scoped read of the tag
  is then refused with `RuleDefinitionException`. After a `Dispose` the same rule is defined.
- A disposed frozen parent with a forgotten grandchild is never released. The root's gate counts 2, while
  `root.Children` shows nothing, because disposed children are hidden. The leak is invisible from the public surface.
- `Children` lists every forgotten child (2,550 in one probe).
- Ids and epochs are the same whether a child is forgotten or disposed: 16 ids per child that mints, 1 or 2 epochs.
  Neither is reclaimed in either case ([interning-and-value-lifetime](./interning-and-value-lifetime.md), section 3.2).
- A forgotten child of a writable parent costs the parent no catch-up work: a child pulls its catch-up at its own next
  call (read).

### 2.3 How a release works today, and what `Dispose` does to descendants

`Scope.Dispose` (`Scope.cs:177-184`) sets `disposed` and `Origin.Disposed`, then calls `TryRelease`. `TryRelease`
(read):

- returns at once for the root, a scope that is not disposed, or a case still owned by its set or run (`:382`);
- defers to the guard if this thread is inside an engine operation of the scope's domain (`Scope.Tree.cs:387-391`; a
  primitive or lattice that disposed it, say); a thread that is only inside a library call of the domain releases
  inline;
- under `childrenLock`, returns if the scope is released or has children, transient children or calls in progress
  (`:399`);
- claims the domain without blocking (`ScopeGuard.TryEnterForRelease`), or hands the release to the thread that holds
  it (`DeferRelease`), whose last `Exit` runs it;
- then frees the local rules' variants, disposes the engine context (leaving every ancestor's gate), uncounts a frozen
  tag (`:446`), runs releases deferred meanwhile, and removes itself from its parent, which cascades to a disposed
  parent.

It runs no user code, never throws, and blocks only on short locks. It already runs on whichever thread removes the
last obstacle. That is what a GC-driven release needs.

**Disposing a scope with a live descendant (run).** The parent's release waits for the child. The child still reads
the parent's rows and can write. But `child.Find` of a class made in the parent throws `ArgumentException` ("class #48
came from scope 'root/scope#8', which is disposed"). If the child is then dropped, neither it nor the parent is ever
collected or released. PR #2 keeps this rule and adds one consequence to the `Scope.Run` documentation: a run on a
descendant whose `Until` names a class of an ancestor disposed during the run throws mid-run, after earlier steps may
have written (test `AnUntilInAChildsRunRefusesAClassOfAParentDisposedDuringTheRun`).

### 2.4 What .NET offers

There are five ways to learn that an object became unreachable (the facts marked "run" were measured on the
.NET 8.0.32 and 10.0.13 runtimes):

- **A finalizer on the object itself.** To queue a release it has to store `this`, which resurrects the object and
  everything it reaches. A finalizer on the public `Scope` would also appear on the public surface as
  `protected override void Finalize()`, which `PublicSurfaceTests.TheSurfaceIsExactlyTheListedOne` refuses: it
  compares the surface with protected members included (`src/Eggnog.Tests/Surface/PublicSurfaceTests.cs:61`,
  `:117-124`; run in a prototype, the test fails).
- **A handle and state split**, as `System.Threading.Timer` does it. The public `Timer` holds a finalizable
  `TimerHolder`, which holds the `TimerQueueTimer` that the timer queue references. The queue never points at the
  public object. A dropped `Timer` was collected at the first collection; its queue entry went after the holder's
  finalizer and a second collection (run).
- **A `ConditionalWeakTable` or `DependentHandle` sentinel**, whose finalizer runs when its key dies (run). It costs a
  GC handle per entry and attaches lifetime to an object whose layout one does not own.
- **Weak references from the parent, swept later**, as WPF's `WeakEventManager` and Roslyn's red-node children do
  (from knowledge). `WeakReference` is itself finalizable (run). A weak handle made with `GCHandle.Alloc(target,
  GCHandleType.Weak)` is not, but whoever made it must free it.
- **A self-re-registering notification object**, as CoreLib's `Gen2GcCallback` does for `ArrayPool` trimming (from
  knowledge). One finalizable object, reachable from nothing, calls `GC.ReRegisterForFinalize(this)` in its finalizer
  while its target lives, so it runs once per collection of the generation it reached (generation 2 after two
  collections). It tells a sweep when to look, not what died.

Facts that constrain any design (run unless marked):

- A finalizable object survives one extra collection, and so does everything it reaches: a finalizable object that
  reaches a dropped graph keeps the whole graph for one more full collection (run on net10.0 only, in a standalone
  probe that counts full collections: a dropped graph of 100 scope cores holding a 20 MB array in generation 2 was
  reclaimed after 1 full collection with no sentinel, after 2 with a sentinel that held its core, and after 1 with a
  sentinel that held only the graph's weak self-reference and a slot number). `GC.SuppressFinalize` removes that
  cost, but only for the object it names. `WeakReference` and `WeakReference<T>` both declare `Finalize` (run, by
  reflection with `DeclaredOnly`), so a weak reference a library creates per scope is a finalization registration and
  a GC handle that `Dispose` cannot suppress.
- All finalizers ran on one thread. From knowledge: their order is not guaranteed, they do not run at process exit on
  .NET, and an exception in one ends the process.
- In fully optimized code (tiered compilation off, which stands for Tier1 and ReadyToRun code; NativeAOT is expected
  to behave the same, from knowledge, and was not run), an object can be finalized while its own method is still
  running. `GC.KeepAlive(this)` prevents it. A `finally` block that makes an instance call usually does too, but not
  by guarantee: once the call is inlined and its body uses only a field loaded earlier, `this` can die before it
  (from knowledge). Eggnog's entry members already end with `Exit(held)` or `EndCall` in a `finally`
  (`Scope.cs:471-498`, `Scope.Engine.cs`, `Scope.Reads.cs`; read), so a `GC.KeepAlive(this)` at the end of `Exit`
  and `EndCall` covers every entry.
- A `CancellationTokenSource` linked to a live parent is never collected unless disposed. .NET accepted "dispose or
  leak" there; the owner's requirement rules it out here.
- The library has no finalizer today (run, by search). An engine choice recorded in
  [the engine map](../history/egglog-engine-map.md) (section 5.1) says "Never finalizers", for flushing a mutation
  buffer. That was about work that must happen; a release that may happen late is a different case, and
  [decisions](../engine/decisions.md) should say so.
- None of these mechanisms needs dynamic code, so none affects NativeAOT or trimming (from knowledge).

### 2.5 How the facts were obtained

Run: every reachability, byte, generation and count figure in sections 2.2 to 2.4, the three approach prototypes in
section 3, the class-rule check on PR #2 and the full suite with phase 0 applied (section 3.1), and the probe of a run
refused with `ObjectDisposedException` at a split step after its scope was disposed mid-run (section 3.1), and two
standalone probes of finalization: who can resurrect an object that has no finalizer (section 3.4) and how long a
finalizable object keeps a dropped graph (section 2.4), the reachability and retained memory of a dropped query
(section 3.5), the reachability of a query and a scope captured by a delegate of their own body or of a global rule
(section 3.5), and a tuple temporary that kept a dropped query reachable in an optimized caller (section 8). From
knowledge only: the `Gen2GcCallback` pattern, the JIT's treatment of `this` in inlined calls, what a Debug build does
to the lifetime of locals, and the internal locks of `ConcurrentDictionary` (section 3.5). Read only: the code paths
cited by line, the engine-level effects of the gate, the lists of call-scoped and user-held holders, the `TryRelease`
protocol, which code PR #2 changes (`git diff 291f7be 51a621d`), the cost of removing a child from the two child lists
(section 3.4), who holds a query or its compiled form (section 3.5), and which registered delegates are never freed
(section 2.1). The line citations in this plan were checked against `291f7be`; the files that section 3.5 and the
registered-delegate row of section 2.1 cite are the same at `06207a2`.

## 3. The approaches

Keeping `Dispose` required is not an option. It is today's state, listed in the table for comparison only.

All the approaches share six things:

- the class rule of section 3.1;
- `TryRelease` as the only release path, with "disposed or abandoned" in place of "disposed" as its condition, and
  "abandoned" overriding the ownership of a host case by its `CaseSet` (a set holds its cases strongly,
  `src/Eggnog/Cases/CaseSet.cs:31`, so a host case is abandoned only once its set is unreachable or has let it go);
  a disposed scope whose release is still waiting counts as abandoned too once nothing reaches it;
- user-held objects (section 2.1) keep their scope alive, and a `ClassId` does not;
- split cases stay owned by their run and are released when it ends, as today; the ownership override never applies
  to them;
- constant-time removal from both child lists, which now also hold dropped children that are not yet collected
  (section 3.4);
- a dropped scope waits for a collection of the generation it reached, typically a full one (section 2.2).

They differ in how the library learns that a scope was dropped, and in which thread then runs the release.

| | Today | A. Handle and finalizer | B. Weak lists and sweeps | B'. B with a per-graph notification | C. Core and sentinel (recommended) |
|---|---|---|---|---|---|
| Mechanism | Parent lists, runtime slot and rule registry hold the `Scope` strongly; only `Dispose` releases | `Scope` renamed `ScopeState` (internal); a new public `Scope` forwards about 38 members to it and has a finalizer that queues the state | Lifecycle state moves to an internal `ScopeNode` that holds its `Scope` weakly; parents hold nodes; later calls sweep nodes whose `Scope` was collected | B's nodes, plus one finalizable notification object per graph that re-registers itself while the graph lives and queues a sweep of the graph's nodes after each collection of its generation | Lifecycle state moves to an internal `ScopeCore`; parents hold cores; a private `ScopeSentinel` owned only by the `Scope` finalizes and queues its core through a graph slot table, without reaching the core or the graph |
| Who runs the release, when | The thread that removes the last obstacle after `Dispose` | A pool thread after the collection, or the busy domain's last exit | The next call that reaches the parent: its `CreateChild` (amortized), a writable entry after a collection, `Freeze`, `Dispose`, a refusal retry | A pool thread after each collection of the notification object's generation (generation 2 once promoted), or the busy domain's last exit; B's call-time sweeps too, if kept | A pool thread after the collection that found the scope, or the busy domain's last exit; only the sentinel's finalizer schedules a drain |
| What users observe | A dropped scope is never freed | Freed after a collection. `Children` may list a dropped child until collected | Freed after a collection and a later call on the parent or an ancestor. An idle graph keeps dropped scopes until it is called or dropped | Freed after the next notification that follows the collection; a scope that died young waits for the next full collection unless a call-time sweep finds it first. An idle graph is covered | Freed after a collection of the generation the scope reached, typically a full one for a scope that ran. `Children` lists only reachable children |
| Changes | none | About 207 internal references in 78 files retyped; local `Rule` identity moved out of the public `Rule`; `CaseSet` split in two; public surface gains `Finalize` unless it moves to a private object | `ScopeNode` (about 350 lines, 230 moved); `ScopeRuntime.Owner` becomes a node; a run pins its own scope and the run cases it adopts (a strong node-to-`Scope` field, cleared when the run's cases are released); `LoweredRule.Rule` removed; sweep hooks at entries | B's, plus the notification object and a registry of the graph's weak handles that does not reach the graph, so the last notification frees them when the graph is dropped | `ScopeCore`, `ScopeSentinel` and `ScopeLink`; `ScopeRuntime.Owner` becomes a core; the same run pins as B; `LoweredRule.Rule` removed; a release queue and a graph slot table; constant-time child removal. About 800 to 1,000 lines in all |
| Main risks | the leak itself | a forwarder that misses `GC.KeepAlive` is a use-after-release in optimized code only; the largest diff | release needs a later call; a deep sweep per collection under a scope with many live descendants; a missed pin loses a case mid-run | B's missed pin; an O(tracked scopes) sweep per notification per live graph; a young dropped scope's engine state, held by its parent until the sweep, is promoted to generation 2 before it is freed (from knowledge, not measured) | a missed pin collects a split case mid-run; a pin left set is a silent leak; releases on pool threads concurrent with user calls (existing protocol) |
| Costs per scope | none, but a forgotten scope costs 3-30 KB for ever | two finalizable objects, about 88-96 B (prototype: 80 B for a handle with two fields and its weak reference) | one `WeakReference` (24 B, finalizable, one GC handle) plus a node header | one weak `GCHandle` and a node header; no finalizer per scope; one finalizable object per graph | one 24 B finalizable sentinel, suppressed when the scope is released, so a disposed and released scope leaves no finalization work; a small `ScopeLink` holding a weak `GCHandle`; a graph slot entry; a `ScopeCore` header; not measured. A resurrecting-finalizer prototype measured +64 B (+1.9%) per bare child |
| Deciding experiment | none needed | an abandon hook on today's `Scope`, run with the engine invariants in four settings | a prototype with the full suite and a background thread forcing collections | as C, plus the generation each dropped scope's engine state reached before its release | the full suite on both frameworks, Release builds, with a forced-collection hook at every run observer, split step and `CreateChild` (section 6) |

### 3.1 Shared by all approaches: what an explicit `Dispose` means, and PR #2

Under GC management a descendant keeps its ancestors alive (section 2.1). So the collector never releases an ancestor
of a live scope, and `ScopeOrigin.Disposed` can only be set by an explicit `Dispose` or a case release. PR #2's open
question becomes: what does an explicit `Dispose` of a scope with live descendants mean? There are three readings.

- **Dispose the subtree.** It frees memory soonest, and `Scope.DisposeVacuous` (`Scope.Cases.cs:127`) already
  disposes a vacuous case's descendants, deepest first. But it disposes scopes a user still holds, possibly in use on
  another thread, which contradicts "alive while referenced". It also breaks working 1.0.0 code in which a child
  outlives a `using` on its parent. Rejected.
- **Stop new calls and kill the classes** (today, kept by PR #2). Descendants keep reading the disposed scope's rows
  but are refused its classes, and an `Until` can fail mid-run after writes. Rejected.
- **Stop new calls on the disposed scope only (recommended, and accepted by the owner).** `Dispose` refuses new calls on
  that scope with `ObjectDisposedException`, as today. Calls in progress go on using the scope's classes; a call that
  must create a scope under the disposed one (a split step of a run on it, a `CreateChild` racing the `Dispose`) is
  still refused with `ObjectDisposedException` at that point, after earlier steps may have written. That refusal is
  today's, from the `IsDisposed` rechecks in `Scope.AdoptCase` (`Scope.Cases.cs:74-75`), `CreateChild`
  (`Scope.Tree.cs:118-119`) and `CreateTransient` (`Scope.Tree.cs:339-340`), and phase 0 does not change it (run: a
  child of a frozen root ran `Sequence(Run(rules), Split(...))`, a lattice merge in the first step disposed the child,
  and the run threw `ObjectDisposedException` at the split step after the first step had written). The scope's classes
  stay usable in its live descendants, in calls already running and in new ones. The scope is released once no
  descendant, transient child or call remains, which is when `TryRelease` releases it today (`Scope.Tree.cs:399`; the
  engine refuses to dispose a context with live children, `Context.Lifecycle.cs:123-124`).

The recommended reading is safe without any disposed test in the class check. A class passes only when its scope is
the call's target or an ancestor of it. Calls on the disposed scope itself are refused at entry. A live descendant
proves the ancestor is not released. Ids are never reused, so a stale cell cannot alias a new class. The disposed
flag survives only to choose the message of an ancestry refusal ("which is disposed" rather than "which is not X or an
ancestor of it"), so the existing message tests stay as they are.

**What this does to PR #2.**

PR #2's code change (`git diff 291f7be 51a621d`) is the per-call exemption and nothing else: it touches
`ClassChecks.cs`, `HostBatch.cs`, `TermReader.cs`, `UntilSchedule.cs`, `RuleSplitSource.cs`, `ProgramBuilder.cs`, the
documentation of `ScopeOrigin.cs` and `Scope.Engine.cs`, architecture.md and the new `DisposalDuringACallTests`. It
does not touch `Scope.cs`, `Scope.Tree.cs` or `ScopeGuard.cs`.

- **Stays:** nothing of PR #2's code. What the GC model depends on is main's call counting, which PR #2 does not touch
  and its commit message describes as existing behaviour: a call counts itself (`Scope.BeginCall` and `EndCall`,
  `Scope.cs:454-462`, bracketed by `Enter` and `Exit`, `:471-498`), and a release waits for the calls in progress
  (`TryRelease`'s calls check, `Scope.Tree.cs:399` and `:427`) and runs at the last `EndCall` or the guard's last
  exit. It came with the scopes themselves (`d8b0349`). It is also what makes a release requested by the collector
  safe mid-call.
- **Replaced:** all of PR #2's code, the per-call exemption. That is the `held` parameter of `ClassChecks.Require` and
  `Check`, threaded through `HostBatch`, `TermReader`, `UntilSchedule` (`executor.Top.Origin`) and `RuleSplitSource`
  (`parent.Origin`), and the owner exemptions in `ClassChecks.AllowedInRule` and `ProgramBuilder.ConstantOperand`.
  Every `held` origin is an ancestor-or-self of its target, so the ancestry test covers it, including the CI failure
  PR #2 fixed (a held call's own class has its origin equal to the target).
- **Reversed:** two documented decisions. First, "a class of a disposed scope used in a live descendant is still
  refused", from PR #2's commit message and its wording of architecture.md sections 2.2 and 2.12 (main's
  `DisposeTests.ADisposedScopesClassesDie` pins the same rule). Second, the `Scope.Run` note that an `Until` throws
  mid-run when an ancestor is disposed during the run; that refusal goes.
- **Kept:** PR #2's goal, that a call in progress may still use its scope's classes after a concurrent `Dispose`, and
  its diagnosis of the CI failure. Of its 7 tests, `AnExtractionWhoseScopeIsDisposedAfterItsEntryCompletes` and
  `ASplitAssumptionHoldsAClassOfTheRunsScopeDisposedWhileItIsGrounded` pass unchanged; the other 5 keep their
  assertions that a held call uses its own classes and flip their descendant refusals.

PR #2 merged before this plan was finished, so phase 0 is a change on `main` that removes its code. No package
includes PR #2, so its two decisions are reversed before any release ships them.

Verified by running, on PR #2 in private clones, net10.0, Release: the ancestry walk first, the "which is disposed"
message kept for an ancestry failure in `ClassChecks.Check`, and the disposed tests removed from `AllowedInRule` and
`ConstantOperand`. Done that literally, which also drops the disposed wording for a rule constant, the full suite
gave 12,415 tests, 7 failed, 10 skipped (soaks); the seventh was
`RuleDefinitionTests.ClassConstantsFromOtherScopesAreChecked` (`RuleDefinitionTests.cs:420`), on that wording alone.
With "which is disposed" kept in `ConstantOperand` too, for a constant that `AllowedInRule` refuses and whose scope is
disposed (phase 0 as section 8 specifies it), a rerun of `RuleDefinitionTests`, `DisposeTests` and
`DisposalDuringACallTests` gave 56 tests and 6 failed; the full suite was not rerun with that branch. The 6 failures
are `DisposeTests.ADisposedScopesClassesDie` and 5 of the 7 `DisposalDuringACallTests`
(`AClassOfTheScopeACallHoldsIsUsableAfterTheScopeIsDisposed`,
`ALocalRuleDefinedWhileItsOwnerIsDisposedHoldsTheOwnersClass`, `AWriteThatDisposesItsScopeStillUsesTheScopesClasses`,
`AnUntilInACaseReadsAClassOfTheRunsScopeDisposedDuringTheRun`,
`AnUntilInAChildsRunRefusesAClassOfAParentDisposedDuringTheRun`), each on an assertion of a reversed refusal, for
example the descendant `child.Find(a)` in the first. net8.0 was not run with this patch.

### 3.2 A. Handle and finalizer

Today's `Scope` (about 2,230 lines in six partials) becomes the internal `ScopeState` with its logic unchanged. A new
public `Scope` holds the state, its parent handle, the local rule list and, for a case of a `CaseSet`, the set. Each of
its about 38 public members forwards and ends with `GC.KeepAlive(this)`. Everything the library owns points at states.
The handle's finalizer flags the state as abandoned and queues it on the graph; a pool step runs `TryRelease`. A weak
self-reference on `EGraph` lets the finalizer skip a graph that was dropped as a whole.

Two side splits are required. A public `Rule` whose owner is a handle, held from the state through `localCompiled` or
from the graph through `LoweredRule.Rule`, roots the handle (run in a standalone prototype). So the local rule's
identity moves into `CompiledRule` or an internal definition, and the public `Rule` becomes a pair of that and the
owner handle. `CaseSet` gets the same handle and state split.

Verified by running, in a standalone prototype on both runtimes: a dropped handle is collected while its state stays
listed, and the pool step releases it; dropped scopes cascade deepest first; a temporary handle such as
`root.CreateChild().Run(...)` was finalized during its own call with tiered compilation off unless the forwarder ended
with `GC.KeepAlive(this)`; a dropped graph enqueued nothing. Not run on the real engine.

Its weakness is size and fragility: about 207 internal references in 78 files change type, and every forwarder must
keep its handle alive. As specified, the finalizer sits on the public type, which changes the public surface. It
converges on approach C once the finalizer moves to a private object.

### 3.3 B. Weak child lists and sweeps

`Scope` stays the public object. A new internal `ScopeNode` holds the lifecycle state, a `WeakReference<Scope>` to its
scope, its parent node, its children nodes and the engine variants of its local rules. Nothing reachable from the
graph, a parent or the engine references a `Scope`. `TryRelease` and the case methods move to the node unchanged.

There is no finalizer of the library's own. A sweep releases the nodes whose `Scope` was collected, deepest first, on
the calling user thread:

- at `CreateChild` and `CaseSet.Add`, when the list has doubled since the last sweep (amortized constant time);
- at a top-level entry of a writable scope, before its guard is taken, when the scope has live descendants and
  `GC.CollectionCount(0)` has moved since its last sweep (a weak reference is cleared only by a collection, so an
  unchanged count proves nothing died);
- at `Freeze` and `Dispose`;
- on a retry of the two frozen-count refusals in `MirrorRegistry`.

A run-made case is adopted in `RuleSplitSource.Assume` and held only in a local until `ScheduleExecutor.AddCase`
holds it, and `RuleSplitSource` has no path to the run. So a run pins its own scope and every case it adopts (a strong
node-to-`Scope` field), and clears the pins where it releases its cases, as in C (section 3.4).

Verified by running, by emulation through reflection on `main`: a `Scope` was collected while its engine state lived;
a release from the node alone brought the gate to 0, removed the engine child and freed the engine rule; the graph
then worked normally. Retained memory waiting for the sweep was 3,133 B, 19,760 B and 27,634 B per child for the three
shapes; after it, 37 B, 16 B and 102 B.

Its weakness: a release needs both a collection and a later call that reaches the parent. A graph that is never called
again keeps its dropped scopes until the graph itself is dropped. That is bounded, because nothing grows without calls,
but it is not what the owner described.

**B'. B with a per-graph collection notification.** B's weakness goes away without a finalizer per scope by the
pattern of section 2.4: one finalizable notification object per graph, reachable from nothing, holding the graph
weakly. Its finalizer re-registers it while the graph lives and queues a sweep of the graph's nodes on the pool, which
releases the dropped ones through the same `TryRelease`, with the same domain claim and deferral as C. Each node holds
its `Scope` through a weak `GCHandle` (not a finalizable `WeakReference`), freed at release; the notification object
also holds a registry of those handles that does not reach the graph, and frees the rest once it finds the graph gone.
The run pins and constant-time removal are the same as in C. Not prototyped.

Against C (section 3.4), on the same footing:

- Finalizable objects per scope: none in B'; one sentinel in C, suppressed when the scope is released. Both have one
  weak handle per scope. B' has one finalizable object per graph.
- Sweep cost: B' walks every tracked scope of the graph per notification, O(tracked), because a notification says
  only that a collection happened; it cannot keep a dirty hint without a per-scope signal. C walks nothing: each
  dropped scope's sentinel names it, so a drain is O(dropped).
- Delay: the notification object soon reaches generation 2, so it runs once per full collection. A scope that died in
  generation 0 or 1 is found by C after that collection, but by B' only at the next full one, unless B's call-time
  sweeps are kept as well. Meanwhile its node and engine state (3-30 KB, section 2.2) stay reachable from the parent
  list, so they are promoted, typically to generation 2, before the release frees them (from knowledge; not
  measured). On the per-request path, a child per request under a frozen schema, that makes every forgotten child
  generation-2 garbage. C releases such a child after the young collection that found it, so its engine state is
  promoted at most once more before it dies.
- Threads: the same in both; the sweep or the drain runs on the pool through `TryRelease`.

So B' is cheaper per scope, and C releases young scopes sooner and without walking the live ones. C stays recommended
for the delay and the sweep cost. B' was the fallback for a refusal of one finalizer per scope, and B for a refusal of
any finalizer; the owner accepted C's sentinel (question 2).

### 3.4 C. Core and sentinel (recommended)

The owner's rule is that a scope lives while anything referencing it is reachable (question 6), and a holder keeps the
scope's ancestors alive too, through `Parent`. Every edge that represents a use already points at the public `Scope`: a
descendant's `Parent`, the user-held objects of section 2.1, call frames. Approach C redirects the bookkeeping edges,
and only those, to an internal object, so that only those references stop counting. It cannot redirect an edge the user
writes into a delegate the graph holds. The engine's external-function registry holds every body delegate and lattice
merge the graph registers until it is freed (section 2.1): a local rule's and a query's at their release, a global
rule's, a split rule's and a lattice's never. So a delegate that captures a scope, or an object that holds one, keeps
that scope reachable from the graph for as long as the delegate is registered. If it is the scope's own local rule, the
scope is never collected and never released, and nothing reports it (run, section 3.5); `Dispose` releases it and
unregisters the rule, and it is collected after that. If it is a global rule, a split rule, another scope's local rule
or a query, `Dispose` still releases the scope's engine state, but the `Scope` object stays reachable until that
delegate is freed, which for a global definition is the graph's life (run, section 3.5). The documentation says that a
delegate the graph holds must not capture a scope meant to be collected (phase 5), and the reachability matrix pins both
cases (section 8).

- **`Scope`** (public, sealed, surface unchanged) keeps `Parent`, the tag, path and graph, the per-call state
  (`openBatch`, `batchesBelow`, `upkeepStamps`, `mirrorSyncs`), its local compiled rules and public `Rule`s, a
  `core` and a `sentinel` (none for the root). Its internal members (`Engine`, `Runtime`, `Guard`, `Origin`,
  `IsDisposed`, `BeginCall`, `EndCall`) forward to the core in one line, so callers do not change. `Exit` and
  `EndCall` end with `GC.KeepAlive(this)` (section 5, early finalization).
- **`ScopeCore`** (internal, not finalizable) never references a `Scope` except through the run's pin (below). It holds
  the graph, origin, engine, runtime, guard, parent core, `childrenLock`, a `List<ScopeCore>` of children, the transient
  count, the call count, the flags (disposed, abandoned, owned by cases, adopted for a run, released, frozen with tag),
  the failure, the local rules' variants without their `Rule`, the frozen tag that `RemoveFrozen` needs, its
  `ScopeLink`, the pin, its slot in the parent's list and its slot in the graph's table (below). `RemoveChild`,
  `AssignGuard`, the case release methods, `DisposeVacuous`, the transient methods and `EndCall` move to it with their
  bodies unchanged, except that "disposed" becomes "disposed or abandoned". `TryRelease` moves with three more changes:
  it frees the variants the core keeps rather than walking `LocalCompiled` (`Scope.Tree.cs:433`); it uncounts the tag
  the core keeps rather than `Scope.Tag` (`:446`); and where it sets `released` (`:430`) it ends the sentinel's duty. It
  takes the link's weak handle, and if its target is still a `Scope`, calls `GC.SuppressFinalize` on that scope's
  sentinel; then it frees the handle and leaves its graph slot. So a scope that is disposed and released leaves no
  finalization work, and a scope that is disposed but not yet released (waiting for a descendant, a call or its case
  set) keeps its sentinel armed.
- **`ScopeLink`** (internal, sealed, not finalizable) holds the `Scope`'s weak `GCHandle` (type `Weak`, not a
  finalizable `WeakReference<Scope>`), the core's graph slot and creation serial, and a lock. The core and the sentinel
  both hold it; it references neither, nor the graph. `Children` reads the `Scope` through it. Whoever frees the handle
  first, the release or the sentinel, takes it under the link's lock, so it is freed exactly once and never read after.
- **`ScopeSentinel`** (internal, sealed) holds the graph's shared `WeakReference<EGraph>` (one per graph, made with
  it) and the link. It reaches neither the core nor the graph, so a dropped graph is reclaimed in one full collection
  whatever its scopes' sentinels do (section 2.4); a sentinel that held its core would keep the whole graph for one
  more. It is reachable only from its `Scope`, so it dies in the same collection. Its finalizer frees the link's
  handle if the release has not (its target is already null: a `Weak` handle is cleared before finalizers run). Then,
  if the graph is alive, it calls `graph.Abandon(slot, serial)`, which under the slot table's lock finds the core only
  if the slot still holds that serial (a released core has left it), sets the abandoned flag, pushes the core on a
  per-graph `ConcurrentQueue<ScopeCore>` and, if no drain is pending, queues one with
  `ThreadPool.UnsafeQueueUserWorkItem`. It marks a disposed core abandoned too, which is what releases a disposed
  host case whose set was dropped (below). It takes only the link's and the table's short locks, runs no engine code
  and cannot throw. The graph's slot table (an array of cores and a free list) is the cost of keeping the sentinel
  away from the graph.

The three strong paths of section 2.1 become:

1. The parent's list holds cores. Every walk over it (the guard reassignment at `Freeze`, the case code, the release
   checks, the engine invariants) still sees every child until it is released, collected or not.
2. `ScopeRuntime.Owner` holds a core; its three writers are `Scope.cs:69` and `:94` and, for a transient query
   child, `Scope.Tree.cs:343`. The two readers that need a `Scope` from an engine context are
   `RuleSplitSource.OwnerOf` (`RuleSplitSource.cs:161`, used by `FindRequests` at `:57` and `Assume` at `:117-118`)
   and `ScheduleCaseRunner.Run` (`ScheduleCaseRunner.cs:40`). The run cannot hand a map to `RuleSplitSource`: there is
   one source per rule and position, kept for the graph's life and shared by every split step of every run
   (`SplitRule.SourceAt`, `SplitRule.cs:70-99`), concurrent runs on children of a frozen scope included, and its
   fields are only the graph, rule and index (`RuleSplitSource.cs:30-32`, set in the constructor at `:40-45`); the
   executor reaches only the driver's `ScheduleCaseRunner` (`ScheduleExecutor.cs:366`). So the `Scope` comes from the
   core, through a pin: a strong core-to-`Scope` field that only duplicates a reference the run holds anyway.
   - A run pins its own scope immediately before `schedule.Execute` (`Scope.Engine.cs:101`, inside the `try` at
     `:99`), and clears that pin in a `finally` after `DisposeRecords` and `ReleaseDisposedCases` (`:115-121`). Not
     where the run enters: between `EnterWrite` (`:62`) and that `try`, `Run` returns for a stopped case (`:66-67`)
     and throws when it refuses its schedule (`ArgumentException` at `:76-77`, `:80-81` and `:84-85`,
     `InvalidOperationException` from `ScopeResolver` at `:89-95`). Only the outer `finally` (`:125-128`) runs on
     those paths, so a pin set at entry would stay set for good, and a scope with a pin left set is immortal again.
     If a run can nest in another on the same scope (not checked), the pin is a count.
   - `Scope.AdoptCase` takes the pin as a parameter, set only from `RuleSplitSource.Assume`
     (`RuleSplitSource.cs:117-118`). `CaseSet` adopts its host cases through the same method (`CaseSet.cs:127`), and
     a host case pinned there would never be collected. A run case's pin is cleared by its `ReleaseCase`, which the
     run's `ReleaseDisposedCases` reaches for each of them after `DisposeRecords` has disposed their contexts (and
     `DisposeVacuous` for a vacuous one).

   `OwnerOf`, `ScheduleCaseRunner.Run` and `TakeCaseCatchUpFailure` (`Scope.Cases.cs:151-171`, which walks the child
   list and calls `FaultWith`) read the pin. B uses the same pin.
3. `LoweredRule.Rule` and `RuleVariants.rule` are dropped. The one reader, the statistics loop in
   `ScheduleExecutor.RunIteration` (around line 210), takes the rule from the compiled list it lowered from.

The release runs on the pool thread through today's `TryRelease`. If the domain is busy, `TryEnterForRelease` fails and
the release goes to that guard's last exit, as it does today for a `Dispose` from another thread. Only the sentinel's
finalizer schedules a drain; no call entry reads the queue or drains it. An inline drain at `Freeze` was considered and
dropped. The library does not know which guards a thread holds: each `ScopeGuard` knows only its own depth
(`ScopeGuard.cs:121`), and `Freeze` checks only its own (`Scope.Tree.cs:155`), so it would need a `[ThreadStatic]`
count kept in every guard entry and exit, a cost on every call. And nothing can be drained before the sentinel's
finalizer has run, which has already scheduled the pool drain, so an inline drain would only race it. The graph has
two release queues and one pending flag: this one of cores, and phase 4's of compiled queries (section 3.5). The drain
loop takes cores and compiled queries from them and releases each; when both are empty it resets the pending flag,
then reads `IsEmpty` on both again and, if anything arrived meanwhile and it wins the flag back, goes on. Without that
re-check, a core or compiled query enqueued just as a drain ends would wait for the next abandonment. Compaction
resumes at the parent's next write, run or `Freeze` once the release has run; writes before that compact against a
gate that is still raised.

`TryRelease` never throws by construction, as today. A drain still catches an exception, so that a defect cannot end
the process from a pool thread, but it does not hide it. A failed release leaves its core listed and its ancestors'
gates raised, the same invisible leak this plan removes. So the drain records the first failure on the graph and
calls `Debug.Fail` in Debug builds. The graph's next `CreateChild` or `Freeze`, on any of its scopes, throws
`InvalidOperationException` ("a scope release failed") once, with the recorded exception as its inner exception. The
library has no `EventSource` today; if the owner prefers one, it replaces the throw.

`Dispose` sets the disposed flag and runs `TryRelease`, as today. It does not suppress the sentinel: the release does,
when it happens (above). A scope disposed with a live descendant keeps its sentinel, but the descendant's `Parent`
keeps the scope reachable, so the sentinel cannot run before the descendant is gone.

`ThrowIfNotUsable` (`Scope.cs:232-239`) and `Children`'s filter treat an abandoned or released core as disposed. A user
object with a finalizer that references the `Scope` can resurrect it in the collection that queued its sentinel (both
are queued together). A weak reference, short or long, cannot: the `Scope` itself is not finalizable and the sentinel
does not reference it, so the collection that queues the sentinel reclaims the `Scope` (run on net10.0 in a standalone
probe of that shape: a long weak reference to the non-finalizable holder read null after one collection while the
sentinel's finalizer ran; a finalizable user object holding it resurrected it, and both finalizers ran after the same
collection). A call on a resurrected scope after the sentinel's finalizer has run gets `ObjectDisposedException`
rather than reaching a released engine context. A call made from the user's finalizer may run before the sentinel's,
since their order is not guaranteed; it then proceeds normally, which is also safe, because the core is released only
after it is abandoned and drained and the call counts itself. The public `IsDisposed` stays false for an abandoned
scope.

Both child lists get constant-time removal. Today each release does `List.Remove` in the library list
(`Scope.Tree.cs:474`) and the engine list (`Context.Lifecycle.cs:133`), O(n) each, under the parent's `childrenLock`
and the engine parent's lock, which `CreateChild` on that parent also takes (`Scope.Tree.cs:116`). Under `Dispose` the
lists hold only live children; under GC management they also hold every dropped child not yet collected, which under a
frozen schema that makes a child per request can be tens of thousands once they reach generation 2. A drain that finds
n of them in creation order would do O(n^2) element moves under those locks. So the core and the engine context keep
their slot index, removal swaps the last element in, and `Children` sorts its snapshot by a creation serial (read; not
measured).

No `CaseSet` sentinel is needed. A set holds its cases strongly (`CaseSet.cs:31`), and a case `Scope` has no field to
its set (`Scope.cs:44-52`), so a host case becomes unreachable in the same collection as its set or after the set let
it go. "Abandoned" overrides the ownership of a host case in `TryRelease` (`Scope.Tree.cs:382`), so a dropped case is
released without a second finalizable type; a case the user still holds stays owned until it is dropped or its set is
disposed. The override applies to host cases only, never to a core adopted for a run: a split case is owned by its run
until `DisposeRecords`, and the pool must not release a context the split driver still owns. Two orders depend on the
sentinel staying armed after `Dispose`: the user disposes a held case and then drops its set, or drops the set, keeps a
case and disposes it later. Either way nobody calls `ReleaseCase` (it is reached only from the set itself, for its
`Dispose`, an add that lost a race with it and its vacuous cases, `CaseSet.cs:141`, `:243`, `:281`, and from
`ReleaseDisposedCases` and `DisposeVacuous`; the public `CaseSet` has no finalizer), so the case's sentinel releases it
once the case is dropped too.

Queries get the same treatment with less machinery (section 3.5).

Verified by running, in a prototype on `main` (9 files, +103/-25; it used a resurrecting finalizer and weak lists in
place of the core and sentinel, and kept split cases strong):

- a dropped child, bare, after a `Make`, after a run, and with a local rule that ran, under a writable and a frozen
  root, was collected and released: gate 0, no engine child, its engine rule and variants freed (8 of 8 cases);
- parent and child dropped together were released in either finalization order;
- a disposed parent with a dropped grandchild was released once the grandchild was collected;
- a collection forced inside a run observer deferred the release to the run's last exit, which then completed;
- a run on a child referenced only by the call finalized nothing mid-call;
- a held `ClassId` alone did not keep its scope; using it afterwards in the parent was refused with the ancestry
  message;
- a disposed ancestor's classes were usable in a live child once the class gate was removed;
- allocation per bare `CreateChild` and `Dispose` rose from 3,437.5 B to 3,501.5 B under a writable root and from
  3,487.4 B to 3,551.4 B under a frozen root (+64 B, +1.9%);
- a dropped child that made a term and ran retained 43.3 B (writable root) and 31.7 B (frozen root) per child over the
  first 1,000, and 8.2 B and 0.0 B over the next 1,000, against about 20 KB never freed today;
- compaction at `Freeze`: with a forgotten child and no collection before `Freeze`, 602 tombstones and 1 blocked
  compaction; with a collection and the drain before `Freeze`, 0 and 0;
- the full suite on net10.0: 12,396 passed, 10 skipped (soaks), 2 failed by construction (`Finalize` on the public
  surface, and a test that reads `LoweredRule.Rule`);
- over 10 runs of the scope, case, concurrency and scheduling tests, one test failed intermittently (in 8 of the 10
  runs): `ScopeThreadingTests.ChildrenDisposedWhileTheirParentFreezesAreAllReleased` drops grandchildren and finds
  them later through `Children`. It must hold them instead.

Not run: net8.0, and every part that makes C different from that prototype: the core split, the sentinel and link in
place of the resurrecting finalizer, the graph slot table, the suppression at release, the run's pins, the host-case
override, the drain and its failure reporting, and constant-time removal. So the cost and retained-memory figures
above belong to the prototype, not to C.

### 3.5 Queries

The owner extended the requirement to queries (question 7): a dropped `Query` is released after a collection, like a
scope, and a query's name becomes a label.

**What holds a query today.** `EGraph.DefineQuery` (`src/Eggnog/Graph/EGraph.Queries.cs:30-54`) compiles the query
(`RuleCompiler.CompileQuery`, `src/Eggnog/Rules/RuleCompiler.cs:58-87`), which lowers its one naive variant at once: an
engine rule, a `MatchCollector` call site and one call site per primitive or filter (`RuleLowering.cs:141-156`), and
an entry in `VariantsByDescription` under the description `$query/<name>#<serial>` (`RuleCompiler.cs:62`,
`RuleLowering.cs:164`). It then refuses the name if a live query has it (`EGraph.Queries.cs:47-48`) and stores the
`Query` in `queriesByName` (`:11`, `:51`). `Query.Dispose` (`src/Eggnog/Rules/Query.cs:56-60`) disposes the compiled
query and, the first time, removes the name (`EGraph.ForgetQuery`, `EGraph.Queries.cs:65`). The objects (read):

- `Query` holds the graph, its name and variables, its `CompiledQuery` (`Compiled`, `Query.cs:76`) and its
  `RuleDraft`, for rendering.
- `CompiledQuery` (`src/Eggnog/Rules/CompiledQuery.cs`) holds the program, the `RuleVariants`, the map from variables
  to columns, the count of calls using it, and a back-reference to its `Query` (`Query`, `:62`). The back-reference
  serves only the name in messages (`:129`, `:141`, `:203`, and `QueryRunner.cs:52`, `:57`, `:63`) and the
  variables in the constructor (`:48-49`).
- The engine side (the rule, its call sites, and the `LoweredRule` in `VariantsByDescription`, whose `Rule` is null for
  a query) references neither. A query's variables reference their builder and its draft, never the `Query`
  (`src/Eggnog/Terms/Var{T}.cs`, `owner`).

A `CompiledQuery` is held, besides by its `Query`, by the calls using it, which acquire it (`Scope.ForEachMatch`,
`Scope.Reads.cs:367-379`, and `QueryRunner.Run`, `QueryRunner.cs:42-92`); by a freeze's planning list
(`Scope.CollectPlanRules`, `Scope.Tree.cs:546-588`, which reads `EGraph.Queries` at `:564`, the only reader of that
property outside tests, and releases what it acquired when the freeze ends); and by a `QueryMatch`, which lives only
during its callback. No public object other than the `Query` refers to a query.

So `queriesByName` is the one strong path from the graph to a query (run, on net8.0.32 and net10.0.13, Release
builds: with the table cleared by reflection, a dropped `Query` and its `CompiledQuery` were collected in all five
shapes tried, never run, run in a writable root, run in a frozen scope's transient child, planned by a freeze, and read
through `ForEachMatch`; with the table kept, neither was). Clearing the table is not enough, though. In the same probe
the engine rule and its `VariantsByDescription` entry stayed after the `Query` was collected, and nothing could free
them any more, because the `RuleVariants` that frees them went with the compiled query. Retained memory per query that
had one filter and ran once, as the slope over 2,000 queries (run, both runtimes, names of 33 characters): about
6.1 KB forgotten today, about 4.1 KB once only the name table lets go, and 0 to 41 B disposed.

**What a query's release needs.** Less than a scope's. A query belongs to no domain, and has no children, no gate
entry, no frozen tag and no case. `CompiledQuery.Dispose` (`:168-184`) is the whole release: it never throws and never
waits, frees the variants at once or at the end of the last call using them (`Release`, `:150-162`), and hands them to
the graph while an engine call that may run user delegates is in progress (`Free`, `:191-200`), which frees them before
its next iteration or query (`EGraph.FreeDeferred`). So queries reuse phase 3's pool drain, its pending flag and the
graph's shared weak self-reference, with a queue of their own beside the queue of cores, and need no `ScopeLink`, no
weak handle, no slot table and no pins.

- **The graph holds compiled queries, not queries.** `queriesByName` is replaced by a table of live compiled queries
  keyed by serial, a `ConcurrentDictionary<int, CompiledQuery>` (`CompiledQuery.Serial`, from `EGraph.NextSerial`, never
  reused). `DefineQuery` adds to it under the definition lock, as it adds to `queriesByName` today. `Query.Dispose` and
  `AbandonQuery` remove from it with `TryRemove` and never take the definition lock, which `DefineQuery` holds across a
  whole compilation (`EGraph.Queries.cs:45-53`). `TryRemove` is not lock-free: it takes the dictionary's internal lock
  for its bucket, held only for that short operation, and an add that resizes the table and the snapshot of
  `EGraph.Queries` take every bucket lock (from knowledge). So a disposal or a finalizer can wait only for another short
  dictionary operation, never for a definition or a compilation; today's `ForgetQuery` (`:61-65`), which calls itself
  lock-free, has the same property and its comment is corrected. `CompiledQuery` loses its `Query` back-reference and
  keeps the name, as a `Name` property, and the variables it needs. `EGraph.Queries` returns a snapshot of the compiled
  queries, and `CollectPlanRules` iterates them directly, keeping its handling of a query disposed meanwhile
  (`Scope.Tree.cs:566-575`). No weak list is needed: the freeze needs the variants, which the table keeps, and the
  table's entries no longer reach a `Query`.
- **`RuleCompiler.CompileQuery` makes no `Query`.** Today it makes the `Query` and the `CompiledQuery` before it
  lowers the variant (`RuleCompiler.cs:73-84`), and `DefineQuery` returns `compiled.Query` (`EGraph.Queries.cs:50-52`).
  After phase 4 it returns the `CompiledQuery` alone, built from the draft's name and variables. `DefineQuery` adds it
  to the table, then makes the `Query` over it with its draft and its sentinel. A query the engine rejects throws
  before any `Query` or sentinel exists, so it leaves no finalization work.
- **A private `QuerySentinel`** (internal, sealed, finalizable, a file of its own), owned only by its `Query`, holds
  the graph's shared `WeakReference<EGraph>` and the serial. Like the scope sentinel, it reaches neither the compiled
  query nor the graph. Its finalizer, if the graph is alive, calls `graph.AbandonQuery(serial)`, which removes the
  serial from the table with `TryRemove` if it is still there, pushes the compiled query on the graph's queue of
  compiled queries and, if no drain is pending, queues one. It takes no lock of the library's, only the dictionary's
  short bucket lock inside `TryRemove`, and never the definition lock. The drain calls `CompiledQuery.Dispose`,
  as `Query.Dispose` does today. A pool thread is not inside an engine call, so the drain frees the variants at once,
  unless another thread is in an engine call that may run user delegates; then the graph frees them before its next
  iteration or query, as it does today for a `Dispose` from another thread. Handing the variants to the graph's
  deferred list from the finalizer, with no pool step, was considered and dropped: a graph that runs no further
  iteration or query would keep them. Unlike a scope's sentinel, which marks its core abandoned in the finalizer, the
  query sentinel sets no flag: until the drain disposes the compiled query, a resurrected `Query` still runs normally.
  That is safe, because its call acquires the compiled query and a drain meanwhile leaves the free to the call's
  `Release`. Use throws `ObjectDisposedException` once the drain has run.
- **`Query.Dispose`** stays, as the early and deterministic release. It suppresses the sentinel, disposes the compiled
  query and removes it from the table. A scope's sentinel stays armed after `Dispose` because its release may wait for
  descendants, calls and its case set; a query's release waits on nothing reachability decides, since the compiled
  query frees its variants at the end of the last call using them. So `Dispose` itself suppresses the sentinel, and a
  disposed query leaves no finalization work. Later use throws `ObjectDisposedException`, as today. `IsDisposed` reads
  a flag the `Query` sets in `Dispose` rather than the compiled query's, so that, as for a scope, it stays false for an
  abandoned query (which only a resurrected `Query` can show).
- **Calls in progress.** A call acquires the compiled query (`CompiledQuery.Acquire`, `:136-145`), and a drain that
  disposes it meanwhile leaves the free to the call's `Release`, as a `Dispose` does today. In optimized code the
  `Query` argument can die once `query.Compiled` is loaded and before `Acquire`; the drain could then dispose the
  compiled query, and the call would throw `ObjectDisposedException` for a query its caller never disposed. So
  `Scope.ForEachMatch` and `Scope.RunQuery` (which serves `Any` and `Count`), the two places that load the compiled
  query (`Scope.Reads.cs:367`, `:398`), end with `GC.KeepAlive(query)`.
- **Names become labels.** `DefineQuery` still refuses a null or empty name (`NameRules.ValidateName`), and no longer
  throws when a live query has it. Nothing looks a query up by name. The name appears in the engine description
  `$query/<name>#<serial>`, which keys `VariantsByDescription` and the engine's statistics and stays unique through its
  serial; in `RuleDefinitionException.RuleName` for a refused definition and `RuleEvaluationException.RuleName` for a
  primitive or filter that failed in a query; and in the messages of the `ObjectDisposedException` of a disposed query,
  the `ArgumentException`s for a query of another graph, a class constant the scope cannot use and a variable of
  another query, and the `InvalidOperationException` for a tag that does not resolve. Two live queries with one name
  give the same text there, which is ambiguous to a reader and to nothing in the library. `Query.ToString` renders the
  body and variables without the name, and no run report lists queries. So duplicates do not matter.
- **What keeps a query alive.** The `Query`, as the owner's answer to question 7 extends question 6's rule to queries,
  and one kind of holder the user writes but the engine keeps. Its variables do not keep it, and a `QueryMatch` exists
  only during a callback whose call holds the compiled query. But every delegate the graph registers is held
  graph-wide by the engine's external-function registry, through a call site object per call site or a `LatticeMerge`,
  and by the compiled programs, until it is freed (section 2.1): a query's own body delegates (`Where`, `Compute.Of`,
  an inline primitive) and a local rule's at their release, a global rule's, a split rule's and a lattice's never. A
  delegate that captures a `Query`, or an object that holds one (typically the `this` of a service with a `Query` field
  and a filter that reads one of its fields), keeps that `Query` reachable from the graph for as long as the delegate is
  registered. If it is the query's own body, the query is never collected and never released, and nothing reports it;
  `Dispose` releases it, and it is collected after that. If it is a global rule, a split rule, a lattice, another
  query or a scope's local rule, `Dispose` still releases the query's compiled form, but the `Query` object stays
  reachable until that delegate is freed, which for a global definition is the graph's life. A scope captured the same
  way is kept the same way (section 3.4). Run, on net8.0.32 and net10.0.13, Release builds with tiered compilation off:
  a query whose filter read the query through a captured array was not collected with the name table cleared, whether
  or not the compiled query was held, and a walk of fields from the graph that skipped the name table found it through
  the registry, the call site, its delegate and the delegate's target; a query whose own filter read a field of a
  service holding it was reached the same way, and was collected once its compiled query was disposed; a child whose
  local rule's filter captured the child was reached the same way with its parent lists, runtime owner, rule and local
  compiled rules skipped; and a child held by a service whose field a global rule's filter read was reached the same
  way, through the global rule's call site, after the child was disposed. So the documentation says that a delegate the
  graph holds must not capture a query or scope meant to be collected, or anything that holds one (phase 5), and both
  reachability matrices pin these cases as ones that are not collected (section 8). A query's own definition keeps no
  scope alive (section 2.1), but its body's delegates keep whatever they capture, a `Scope` included.

A dropped query not yet collected stays in the table, so a freeze meanwhile still plans its variant: a planning cost,
not a leak. The sentinel costs one small finalizable object per query, suppressed by `Dispose` (not measured). Not run:
any of this design; only the reachability and memory probe above and the capture probe, which also found that a
query whose delegates capture nothing was collected in all five shapes with the name table cleared, and that, with the
back-reference cut as well, `CompiledQuery.Dispose` then removed the `VariantsByDescription` entry and the graph stayed
usable.

## 4. Value

**For every user.** The library behaves as .NET users expect of a library without unmanaged resources: dropping a
scope frees it. Today a forgotten `Dispose` is a leak that never ends, and it is invisible. `Children` hides disposed
scopes, nothing reports gate counts, and a disposed parent with a forgotten grandchild stays for ever. Forgetting
`Dispose` is easy: a scope made in a helper, an exception path without `using`, a scope stored in a cache that is
later cleared.

**For long-lived hosts.** A host that makes a child per request under a frozen schema and misses one `Dispose` per
thousand requests leaks 20 KB to 30 KB per miss, and one engine rule per local rule the child defined. With the
change that becomes a delay until a collection of the generation the child reached: for a child that did real work,
typically the next full collection (section 2.2).

**For writable parents.** A forgotten child of a writable scope stops that scope's compaction for good; on `math` that
is 1.93 times the physical rows. With the change, compaction resumes after the child is collected and released,
typically after the next full collection, and `Freeze` keeps nothing if that came first. Until then the gate stays
raised, so for a writable parent `Dispose` remains the practical way to get compaction back.

**For tagged scopes.** A forgotten frozen tagged scope blocks new scoped reads of its tag for good. With the change it
blocks them until it is collected, typically at the next full collection; `Dispose` gives the tag back at once.

**For the semantics (phase 0).** `Dispose` of a scope with live descendants stops being a trap. Today a descendant can
read a disposed ancestor's rows but not name its classes, and a run can fail halfway after it has written. The new
rule is one sentence, and it replaces all of PR #2's per-call exemption. A run on the disposed scope itself can still
be refused with `ObjectDisposedException` at a split step, as today (section 3.1).

**For queries (phase 4).** A query made per request and never disposed keeps about 6 KB and an engine rule for the
graph's life today, and its name blocks any later definition of that name with `ArgumentException`. With the change a
dropped query is freed after a collection, and names cannot collide.

**If it is never done.** The owner's requirement is unmet, and every user must dispose every child, including in error
paths. The leak stays undetectable from the public surface.

## 5. Cost and risk

| Part | Effort | What dominates it | Risk to correctness, and its guard | Risk to performance | Maintenance |
|---|---|---|---|---|---|
| Phase 0. The class rule | small (about -60/+15 lines against PR #2; 6 tests change, confirmed on net10.0) | a semantics decision | low: it only accepts calls that threw before. Guard: the full suite, plus new tests that a disposed ancestor's class works in a descendant mid-run and that the ancestor is released after the descendant | none | lower: one rule instead of a per-call exemption |
| Phase 1. `ScopeCore`, move only | medium | moving the lifecycle fields and methods of `Scope.cs`, `Scope.Tree.cs` and `Scope.Cases.cs` (about -250/+180) | medium: the move touches main's call counting and release deferral, which the threading tests and the surviving PR #2 tests pin; an ordering slip could reopen the race between a release and a call in progress. Guard: a move-only commit with no behaviour change and the whole suite on both frameworks | one forwarding call per internal member, inlined | one-type-per-file holds: `ScopeCore`, `ScopeLink` and `ScopeSentinel` are files of their own |
| Phase 2. Cut the runtime-owner and rule-variant paths | small | the run's pins (its own set just before `schedule.Execute` inside the run's `try`, never at entry, and cleared after `DisposeRecords` and `ReleaseDisposedCases`; a case's set through an `AdoptCase` parameter from `RuleSplitSource.Assume` only, and cleared by its `ReleaseCase`; no new channel into the shared `RuleSplitSource`); the three `Owner` writers; dropping `LoweredRule.Rule` | medium: a missed pin collects a split case mid-run ("A case has no rule-layer scope"); a pin left set, by an early exit from `Run` or on a host case, is a silent leak. Guard: the forced-collection hook in split steps, and the early-exit and host-case rows of the reachability matrix | none | low |
| Phase 3. Sentinel, link, slot table, release queue, host-case override, constant-time removal | small to medium (about 300 lines) | the sentinel and link protocol (suppression at release, the handle freed once), the graph slot table, the drain and its failure reporting, `Children` through weak handles, slot-indexed removal in both child lists | medium: releases on pool threads beside user calls. They use the existing non-blocking claim and deferral, which `Dispose` from another thread already exercises. Guard: forced-collection and stress runs in Release builds with tiered compilation off, the finalize-while-a-drain-ends stress and the drain fault-injection test | nothing on the call path; per scope one finalizable sentinel (suppressed at release, so none is left for a disposed and released scope), a `ScopeLink` with a weak `GCHandle`, a slot-table entry and a core header, not measured on C; without slot-indexed removal, O(n) per release and O(n^2) per drain under the parent's locks | low; any new strong edge from library state to a `Scope` brings the leak back silently, so the reachability matrix of section 8 is the guard |
| Phase 4. Queries managed by the GC | small (about 250 lines, more than half tests) | the table of compiled queries keyed by serial in place of `queriesByName`; `CompiledQuery` without its `Query`; `QuerySentinel` and `AbandonQuery` on a queue of compiled queries that phase 3's drain also empties, with its end-of-drain re-check covering both queues; `GC.KeepAlive(query)` in the two query entries; `DefineQuery` without the name check | low: the release is today's `CompiledQuery.Dispose`, which any thread may already call. A missed `GC.KeepAlive(query)` gives a spurious `ObjectDisposedException` in optimized code only. Guard: the forced-collection hook before `Acquire`, in Release builds with tiered compilation off | per query one small finalizable sentinel, suppressed by `Dispose`; a dropped query not yet collected is still planned by a freeze | low; a new strong edge from the graph or the compiled query to a `Query` brings the leak back silently, so the query rows of the reachability matrix are the guard |
| Phase 5. Documentation | small | architecture.md 2.1, 2.2, 2.7, 2.8, 2.11, 2.12; XML docs; a decision entry | none | none | low |

Risks that remain after the guards:

- **Collection timing becomes visible.** For a scope dropped without `Dispose`, two things depend on when the
  collector runs: compaction of a writable ancestor (at its `Freeze` and in its later writes and runs), and the
  frozen-tag refusal of a first scoped read. (Engine retractions and the container dirty log depend on it too, but the
  library exposes neither.) If a writable parent freezes before its dropped child is collected, its tombstones stay
  for good. The library must not call `GC.Collect` itself. The remedy is `Dispose`, which stays deterministic. The
  owner chose to document this rather than wait for the suffix compaction that the colored-context plan proposes,
  which would make compaction resume once the gate drops (question 4).
- **`Children` becomes nondeterministic** for dropped children. A dropped child leaves the list at the collection that
  finds it, though its release may still be queued. Code that drops children and finds them later through `Children`
  breaks intermittently, as one existing test did.
- **Release can be late.** A dropped scope is found only by a collection of the generation it reached, and a scope
  that ran a while is in generation 2 (section 2.2), so it waits for the next full collection, which a busy process
  may not run for a long time. Until then the parent's gate stays raised, the frozen tag stays counted, its 3-30 KB of
  engine state stays, and the parent's child lists keep it. A drain before `Freeze` cannot help, because no collection
  has found the scope. This is the same for every GC-managed object, and `Dispose` is the remedy; B and B' wait on the
  same collection, B' for a young scope even longer (section 3.3).
- **A failed release is a leak.** `TryRelease` never throws by construction, but a defect in a release on the pool
  would leave its core listed and its ancestors' gates raised. The drain records it and the graph's next `CreateChild`
  or `Freeze` reports it (section 3.4), so it is seen rather than swallowed.
- **Early finalization in optimized code.** An entry member that loads the core and does not use `this` afterwards
  could let the sentinel finalize before `BeginCall`, and a drain could then release the core under the call. The
  `finally` that every entry member ends with calls `Exit` or `EndCall` on the `Scope`, but once those forward to the
  core, whether that keeps `this` alive depends on what the JIT does with an inlined forwarder, which no IL scan can
  see. So `Exit` and `EndCall` end with `GC.KeepAlive(this)`: one line that covers every entry by construction and
  costs nothing at run time. Members outside that bracket (`Version`, `IsFrozen`, `IsDisposed`, `IsFaulted`,
  `Failure` and `Children`) need their own. A query call has the same hazard for its `Query` argument, and
  `GC.KeepAlive(query)` covers it (section 3.5). The hazard shows only in optimized code, so the guard is the whole
  suite and the stress runs in Release builds with tiered compilation off (section 8). NativeAOT is untested.
- **A finalizer in the library** departs from the engine's recorded "Never finalizers" choice. The case differs (a
  release that may come late, not a flush that must happen). The owner accepted it for scopes, one sentinel per
  non-root scope (question 2). The query sentinel follows from question 7's answer, that a dropped `Query` is released
  after a collection like a scope, and uses the same mechanism. A decision entry must say both.
- **A delegate the graph holds keeps what it captures.** The engine's external-function registry holds every
  delegate the graph registers, in a rule, split rule or query body or a lattice, until it is freed: a query's and a
  local rule's at their release, a global rule's, a split rule's and a lattice's never (section 2.1). A delegate that
  captures a `Query` or `Scope`, or an object that holds one (a service with such a field, read by a filter), keeps
  that object reachable from the graph for as long as the delegate is registered, and nothing reports it. If the
  delegate is the object's own body, the object is never collected or released; `Dispose` releases it and unregisters
  the body, and it is collected after that. If the delegate is global, `Dispose` still releases the object's engine
  state, but the object itself is never collected. No approach can cut that edge, since the engine must call the
  delegate while it is registered. The guard is documentation (phase 5) and the matrix rows that pin both cases
  (section 8).
- **Duplicate query names.** Two live queries with one name give the same text in exceptions and messages. Nothing in
  the library looks a query up by name, and engine descriptions stay unique through the serial (section 3.5).
- **Behaviour change on a stable package.** Calls that threw `ArgumentException` now succeed (a disposed ancestor's
  class in a descendant, a second query with a live query's name), and `Children` changes. The commits carry
  `+semver: minor`.

## 6. Worth-it evaluation

| Question | Cheapest decisive experiment | Metric and threshold | Verdict now |
|---|---|---|---|
| Is a forgotten scope a real leak today? | Done (section 2.2) | Collected after full collections, yes or no | **Yes**: never collected; 3.4-30 KB, gate entries and engine rules held for ever |
| Is the class rule safe, and what does it change? | Done on net10.0 with exactly phase 0 on PR #2 (section 3.1). Still to do: net8.0 | Only tests that pin the reversed refusals fail: assertions in 5 of the 7 `DisposalDuringACallTests` and `DisposeTests.ADisposedScopesClassesDie`. Anything else failing is a no-go until explained | **Do now**, as a change on `main` that removes PR #2's code (accepted, question 1): on net10.0 exactly those 6 failed (the affected test classes rerun with the wording branch of section 8) |
| Can a dropped scope be released from internal state alone? | Done twice: B's emulation and C's prototype (sections 3.3, 3.4) | Gate 0, no engine child, rule count back, graph usable after | **Yes** |
| Does C hold on the real engine with the sentinel and the run's pins? | Implement phases 1 to 3 in a branch. Run the full suite on both frameworks, built with `-c Release` (a Debug build runs the JIT unoptimized, which keeps locals and `this` alive to the end of each method and would hide a missed pin or `GC.KeepAlive`), with an internal hook that calls `GC.Collect` and `WaitForPendingFinalizers` at every run observer, split step and `CreateChild`, once with default tiering and once with tiered compilation off. Add the reachability matrix of section 8 | Go if no test fails beyond the listed by-construction edits, no drain failure is recorded, and every kind of scope in the matrix is collected and released. No-go if some kind of scope cannot be made collectable without a strong path from the graph | **Do after this experiment** |
| Is the per-scope cost acceptable? | Measured only on the resurrecting-finalizer prototype; to be re-measured on C (sentinel, link, weak handle, slot entry, core) in the experiment above, with a count-based test of a drain of 100,000 dropped children of one frozen parent, and a finalization count after N children created and disposed (`GC.GetGCMemoryInfo().FinalizationPendingCount` after one full collection, and a test-only counter of sentinel finalizers after `WaitForPendingFinalizers`) | A disposed and released scope leaves no finalizer to run: for N disposed children the sentinel counter stays 0 and `FinalizationPendingCount` does not grow with N. Beside it, at most 5% more allocation per bare child (about 170 B), at most 64 B retained per dropped child after the drain, and bounded element moves per release | **Likely, by the prototype** (+64 B, +1.9%; 0-43 B retained); **to be re-measured on C** |
| Is collection timing acceptable? | Done in C's prototype (section 3.4) and the generation probe (section 2.2) | Documented, with `Dispose` as the remedy for writable parents and tagged scopes | **Accepted: document it** (question 4), with no suffix compaction now; for a scope that ran, release typically waits for a full collection |
| A finalizer per scope (C), one per graph (B') or none (B)? | No experiment for the choice of guarantee, which was the owner's | C frees a dropped scope after the collection of the generation it reached, by an O(dropped) drain, with no later call. B' needs no finalizer per scope, but finds a scope that died young only at the next full collection (its engine state promoted meanwhile) and walks every tracked scope per full collection. B needs a later call. All wait for the collection that finds the scope | **C, accepted** (question 2), for the earlier release of young scopes and the drain that walks nothing live |
| Does a dropped query need a release, or only a weak name table? | Done (section 3.5): the name table cleared by reflection, five shapes, both runtimes | Query and compiled query collected; engine rule and `VariantsByDescription` entry freed, yes or no | **A release is needed**: the query is collected but its engine rule stays (about 4.1 KB per query, never freed). Phase 4 releases it through phase 3's drain, and its tests run with the same forced-collection hook |

No step needs an idle machine. If the owner wants the per-call cost of the forwarders timed, the per-child latency
benchmarks of [idle-machine measurement](./idle-machine-measurement.md) cover it in the existing campaign.

## 7. Recommendation

The owner's answers (section 9) settle every choice below; what remains is the deciding experiment of section 6.

1. **Land phase 0 first** (the owner accepted the class rule), removing PR #2's code from `main`. It is independent
   of the GC work, it only accepts calls that threw before, it still fixes the CI failure PR #2 diagnosed, and it
   removes a documented mid-run exception. No package includes PR #2, so nothing released is reversed.
2. **Build approach C** (phases 1 to 3, documented in phase 5) after the forced-collection run of section 6 passes. It
   is the handle and state split .NET uses for `Timer`, with one difference that matters here: `Timer`'s queue is static
   and never dropped, while a graph often is, so the sentinel reaches neither the core nor the graph and finds the core
   through the graph's slot table, and a dropped graph is reclaimed in one full collection. Plain fields carry every
   use; three bookkeeping edges point at an internal core; one private sentinel per scope only queues the release, and
   the release suppresses it, so a disposed scope leaves no finalization work; today's `TryRelease` runs on the pool.
   Nothing is resurrected, no finalizer touches engine state, and the public surface does not change. Against B', which
   needs no finalizer per scope, C releases young dropped scopes after the collection that found them rather than at the
   next full one, and its drain walks only the dropped scopes (section 3.3).
3. **Manage queries the same way** (phase 4, after phase 3): the graph keeps compiled queries, a private sentinel per
   `Query` queues a dropped one on the same drain, and names become labels (section 3.5).
4. **Keep `Dispose`** on scopes and queries, documented as optional and early, and as the practical tool for a
   writable parent's compaction and a frozen tag, which otherwise wait for a collection of the generation the dropped
   scope reached.
5. **Drop approach A** as specified: it is C with a larger diff and a finalizer on the public type. B' and B are not
   needed: the owner accepted a finalizer per scope.

## 8. Plan if it goes ahead

The project's practice applies to every phase: implement; review from several angles, including a mutation review of
the new tests; fix; verify on net8.0 and net10.0.

**Phase 0: the class rule (small).** Entry: none; the owner accepted the class rule (question 1). A change on `main`
that deletes PR #2's code and keeps its diagnosis and its tests, adjusted as below.

- `ClassChecks.Check`: run the ancestry walk first; a class passes when its origin is the target or an ancestor.
  `ScopeOrigin.Disposed` only chooses "which is disposed" for a class that fails the walk. Remove `held` from
  `Require` and `Check`, `HostBatch`, `TermReader`, `UntilSchedule` and `RuleSplitSource`. Remove the disposed test
  and the owner exemption from `AllowedInRule`. In `ProgramBuilder.ConstantOperand`, remove the disposed refusal
  before `AllowedInRule` and the owner exemption; when `AllowedInRule` refuses a constant whose scope is disposed,
  keep the message "... which is disposed" (as `ClassChecks.Check` does), so
  `RuleDefinitionTests.ClassConstantsFromOtherScopesAreChecked` stays unchanged. Without that branch a seventh test
  fails on its wording (run on PR #2, net10.0: 7 failures without it, the 6 of section 3.1 with it).
- Replace the `Scope.Run` note on an `Until` refusing a class of an ancestor disposed during the run with today's
  remaining mid-run refusal: a run on a scope disposed during the run is refused with `ObjectDisposedException` at its
  next split step, after earlier steps may have written (section 3.1).
- Tests: flip `DisposeTests.ADisposedScopesClassesDie` and the descendant-refusal assertions in
  `DisposalDuringACallTests` (its `AnUntilInAChildsRunRefusesAClassOfAParentDisposedDuringTheRun` becomes "the run
  completes and the `Until` sees the class"). `AnExtractionWhoseScopeIsDisposedAfterItsEntryCompletes` and
  `ASplitAssumptionHoldsAClassOfTheRunsScopeDisposedWhileItIsGrounded` stay as they are. Add: a descendant `Find`,
  `Union`, local-rule constant and `Until` with a disposed ancestor's class; the ancestor released only after the
  descendant is disposed; a class of a disposed scope used outside its subtree still refused with "which is disposed".
- Exit: the full suite on both frameworks, Release builds, with only those tests changed (on net10.0 already
  confirmed for the affected test classes: exactly the 6 tests of section 3.1 fail before they are changed).

**Phase 1: `ScopeCore`, move only (medium).** Entry: phase 0.

- New `src/Eggnog/Graph/ScopeCore.cs` (partials as needed). Move the lifecycle fields and `TryRelease`, `RemoveChild`,
  `AssignGuard`, `BeginCall`, `EndCall`, the transient methods, `ReleaseCase`, `ReleaseDisposedCases`,
  `DisposeVacuous` and `TakeCaseCatchUpFailure` into it. The core keeps the local rules' variants and the frozen tag
  that `TryRelease` needs. The parent list becomes `List<ScopeCore>`; `ScopeGuard.deferred` holds cores. `Scope`'s
  internal members forward.
- No behaviour change. Exit: the whole suite on both frameworks, Release builds, then a review of the moved race code
  (main's call counting and release deferral) against the threading tests and the surviving PR #2 tests.

**Phase 2: cut the other two paths (small).** Entry: phase 1.

- `ScopeRuntime.Owner` becomes a `ScopeCore`, at its three writers: `Scope.cs:69`, `:94` and, for a transient query
  child, `Scope.Tree.cs:343`. A run pins its own scope on its core immediately before `schedule.Execute`
  (`Scope.Engine.cs:101`, inside the `try` at `:99`), never at entry, and clears that pin in a `finally` after
  `DisposeRecords` and `ReleaseDisposedCases` (`:115-121`); the early exits before that `try` (`:66-67`, `:76-95`)
  must leave nothing set. `Scope.AdoptCase` takes the pin as a parameter, set only from `RuleSplitSource.Assume`, never
  from `CaseSet`; a run case's `ReleaseCase` clears it, and the core records that it was adopted for a run, so the
  host-case override of phase 3 never applies to it. `RuleSplitSource.OwnerOf`, `ScheduleCaseRunner.Run` and
  `TakeCaseCatchUpFailure` read the pin. No new channel into the shared, per-graph `RuleSplitSource` is needed.
- Drop `LoweredRule.Rule` and `RuleVariants.rule`; the statistics loop reads the compiled list. Update
  `LoweringTests.DescriptionsNameTheRulesetTheRuleAndItsSerial`.
- An internal `Scope.Abandon()` hook for tests, standing in for the sentinel.
- Exit: with the hook, every kind of scope in the reachability matrix below is collected after `Abandon`, a drain and
  a collection, with the engine invariant checkers on.

**Phase 3: sentinel and release queue (small to medium).** Entry: phase 2.

- New `src/Eggnog/Graph/ScopeSentinel.cs` and `src/Eggnog/Graph/ScopeLink.cs`. A new `EGraph.Releases.cs` partial:
  the slot table, the queue, the pending flag, the pool drain, the shared weak self-reference that the sentinels hold,
  an internal `DrainReleases` for tests, and the record of the first drain failure, reported by the next `CreateChild`
  or `Freeze` and by `Debug.Fail` in Debug builds.
- Only the sentinel's finalizer schedules a drain; no call entry reads the queue, and nothing drains inline. The drain
  resets its pending flag, then re-checks `IsEmpty` and goes on if a core arrived. Phase 4 adds a second queue, and
  the re-check then reads both.
- `TryRelease`, where it sets `released`, suppresses the sentinel (through the link's handle, if the `Scope` is still
  alive), frees the handle and leaves the slot table. `Dispose` does not suppress anything. The sentinel's finalizer
  frees the handle if the release has not, and marks a disposed but unreleased core abandoned too.
- "Abandoned" overrides a host case's ownership by its `CaseSet` in `TryRelease`, never a run case's; no `CaseSet`
  sentinel.
- `ThrowIfNotUsable` and `Children` treat an abandoned or released core as disposed.
- Slot-indexed, swap-remove removal in the library child list and the engine `Context.children`
  (`Context.Lifecycle.cs:133`); `Children` orders its snapshot by a creation serial.
- `Children` filters through each core's weak handle. Hold the grandchildren in
  `ScopeThreadingTests.ChildrenDisposedWhileTheirParentFreezesAreAllReleased`.
- `GC.KeepAlive(this)` at the end of `Scope.Exit` and `Scope.EndCall`, and in members outside the entry bracket.
- Run the experiment of section 6. Exit: its thresholds.

**Tests** (a new `src/Eggnog.Tests/Scopes/ScopeCollectionTests.cs`, in a non-parallel collection):

- How a test drops a scope: it creates, uses and drops the scope in a `[MethodImpl(MethodImplOptions.NoInlining)]`
  helper that returns only a `WeakReference` (and any probe data as primitives or strings), and the caller holds no
  tuple or struct temporary over the dropped object. Otherwise optimized code can keep the object reachable from the
  caller's frame, and the test fails for a reason unrelated to the library (run: a deconstructed tuple temporary in an
  optimized caller kept a dropped query reachable; the same steps inside such a helper collected it).
- A reachability matrix. Each kind of scope is collected and released once dropped: a `CreateChild` child under a
  writable and a frozen root (bare, after a `Make`, after a run); a scope with a local rule, run and never run (its
  engine rule and `VariantsByDescription` entry freed); a host case of a dropped `CaseSet`; a split case after its
  run; a scope with a frozen tag; a scope extracted from and then dropped; a parent and child dropped together, in
  either order; a disposed parent with a dropped grandchild; a held case disposed, then its set dropped, then the case
  dropped; a set dropped while a case is held, the case disposed later and dropped; a scope whose `Run` returned early
  on a stopped case, and one whose `Run` was refused (an `Until` that cannot be checked, a ruleset or split rule of
  another graph, an unresolvable tag), each then dropped. Each case records `GC.GetGeneration` of the scope before it
  is dropped and the collection that found it, so the matrix also shows which scopes wait for a full collection.
- A documented case that is not collected: a child whose local rule's filter captures the child (or an object that
  holds it) stays reachable across forced collections and drains, with its engine rule registered; `Dispose` then
  releases it and frees the rule, and the child is collected after that.
- A documented case that is never collected: a child held by a service whose field a global rule's filter reads stays
  reachable across forced collections and drains, even after `Dispose`; `Dispose` still releases its engine state (its
  gate entries leave, its engine context and local rules are freed, and a writable parent compacts at `Freeze`).
- Holders keep their scope and its ancestors: a held `Extractor`, `ExtractedTerm`, `NodeView`, `RunReport` with
  `RecordSteps`, local `Rule`, `CaseSet`, case, `ScopeWriter` or descendant keeps the scope and its parent alive across
  a forced collection, and `Rule.Owner` is the same object as the scope.
- A held `ClassId` alone does not keep its scope; using it after collection outside its subtree gives the ancestry
  message, never "disposed".
- A busy domain: a dropped child of a writable scope collected while another thread is in a call on its domain is
  released at that call's exit.
- In flight: `root.CreateChild().Run(...)` with a collection forced in the observer completes, and the release comes
  after it.
- A resurrected scope: a finalizable holder's finalizer stores the dropped scope in a static field; after
  `GC.Collect` and `WaitForPendingFinalizers` (so the sentinel's finalizer has run too) the test calls it, and the call
  throws `ObjectDisposedException` and never reaches a released engine context. The holder's finalizer must not call
  the scope itself: the two finalizers run after the same collection in no guaranteed order.
- Release cost: 100,000 dropped children of one frozen parent released in one drain; the element moves (or lock
  acquisitions) per release stay bounded (a count, not a time).
- Split cases survive a collection forced in the observer and in a split step, and are released when the run ends.
- `Freeze` of a writable parent after a dropped child was collected compacts (`StaleCount` 0, `CompactionsBlocked` 0).
- A dropped frozen tagged scope gives back its frozen count after collection and drain.
- A dropped `EGraph` with undisposed scopes is reclaimed after one full collection: a long (`trackResurrection`) weak
  reference to the graph reads null after a single `GC.Collect`, not a collect-until-clear loop; no release is queued,
  and every link's weak handle is freed.
- Disposal leaves no finalization: N children created and disposed run no sentinel finalizer (a test-only counter
  stays 0).
- A drain ending: a stress test that finalizes dropped scopes while a drain is ending, on several threads; every core
  is released, none waits for a later abandonment.
- A failed release: an injected exception in a release on the pool is recorded, the next `CreateChild` or `Freeze`
  reports it with the exception as the inner exception, and the process survives.
- Retained memory: 1,000 dropped children retain at most 64 B each after collection and drain.
- Optimized code: the whole suite run once more on both frameworks, built with `-c Release` (as CI builds), with
  `DOTNET_TieredCompilation=0` and `DOTNET_TieredPGO=0` set in the environment, which needs no new test infrastructure
  (the repository has no out-of-process test harness). A Debug build would keep locals and `this` alive to the end of
  each method and pass for the wrong reason, so the hook test carries a self-check: a scope referenced only by a local
  that is dead after a call is collected inside that call. `GC.KeepAlive(this)` at the end of `Exit` and `EndCall`
  replaces any IL scan, which could not see what the JIT does with an inlined forwarder. NativeAOT stays untested
  unless a published smoke program for the in-flight case is added.
- `PublicSurfaceTests.TheSurfaceIsExactlyTheListedOne` stays green: no `Finalize` on a public type.
- Mutation review: the tests must fail if a parent list holds `Scope`s again, if `ScopeRuntime.Owner` holds a `Scope`,
  if `LoweredRule.Rule` comes back, if a drain skips `TryRelease`, if a run stops pinning its cases, if a run leaves
  its own pin set (a `Run` on a stopped case, or one refused for an `Until` or a ruleset of another graph, then a drop
  and a collection), if the pin survives any early exit, if `CaseSet` adoption pins its cases, if the release stops
  suppressing the sentinel, if `Dispose` suppresses it before the release, if the sentinel references its core or the
  graph, or if the drain loses its re-check.

**Phase 4: queries managed by the GC (small).** Entry: phase 3, whose pool drain, pending flag and shared weak
self-reference it uses, with a queue of its own. Section 3.5 gives the design.

- `EGraph.Queries.cs`: replace `queriesByName` with a `ConcurrentDictionary<int, CompiledQuery>` of live compiled
  queries keyed by serial. `DefineQuery` drops the name check and adds the compiled query to the table under the
  definition lock; `ForgetQuery` takes the serial and removes it with `TryRemove`, which takes only the dictionary's
  short bucket lock, never the definition lock (its "lock-free" comment is corrected); `Queries` returns the compiled
  queries. `EGraph.Releases.cs` gains `AbandonQuery(serial)`, which takes no lock of the library's, and a
  `ConcurrentQueue<CompiledQuery>` beside the queue of cores. The same drain empties it by calling
  `CompiledQuery.Dispose` on each, and its end-of-drain re-check reads both queues.
- `CompiledQuery`: remove the `Query` property; take the name and the variables in the constructor and keep the name
  as a `Name` property; its messages and `ToString` use it. `QueryRunner` reads the name from the compiled query, and
  `CollectPlanRules` iterates `EGraph.Queries` directly.
- `RuleCompiler.CompileQuery` makes no `Query` and returns the `CompiledQuery` alone. `DefineQuery` adds it to the
  table, then makes the `Query` over it, with its draft and its sentinel, so a query the engine rejects leaves no armed
  sentinel.
- New `src/Eggnog/Rules/QuerySentinel.cs`. `Query.Dispose` suppresses the sentinel, then disposes the compiled query
  and forgets its serial. `Query.IsDisposed` reads a flag of the `Query`.
- `GC.KeepAlive(query)` at the end of `Scope.ForEachMatch` and `Scope.RunQuery`.
- The forced-collection hook of section 6 also fires in each query call between loading the compiled query and
  `Acquire`, and drains there (`DrainReleases`), so that a missing `GC.KeepAlive(query)` shows as an
  `ObjectDisposedException`.
- Exit: the whole suite and the tests below on both frameworks, Release builds, with default tiering and with tiered
  compilation off, the hook on.

**Query tests** (a new `src/Eggnog.Tests/Rules/QueryCollectionTests.cs`, in the non-parallel collection, and changes to
`QueryTests`):

- How a test drops a query: as for scopes, it creates, uses and drops the query in a
  `[MethodImpl(MethodImplOptions.NoInlining)]` helper that returns only a `WeakReference` (and any probe data as
  primitives or strings), and the caller holds no tuple or struct temporary over the dropped query or its compiled form.
- A dropped query is collected and released in each shape of section 3.5 (never run, run in a writable root, run in a
  frozen scope's transient child, planned by a freeze, read through `ForEachMatch`): its engine rule is freed
  (`Engine.GetRuleInfo` throws), and so are its call sites and its `VariantsByDescription` entry; its serial has left
  the table.
- A held variable of a dropped query does not keep the query.
- In flight: `ForEachMatch` on a query referenced only by the call, with a collection and a drain forced in the
  callback, delivers every match with its engine rule still registered; the rule is freed after a collection that
  follows the call.
- A freeze after a dropped query was released does not plan its variant.
- Names: `DefineQuery` with a live query's name returns a second query; both run and count their own matches,
  disposing one leaves the other usable, and their descriptions differ. In
  `QueryTests.DefinitionErrorsAreRuleDefinitionExceptions` the duplicate-name refusal becomes that success, and the
  comments on a name that is "still free" there and "may be defined again" in
  `QueryTests.DisposeFreesTheRuleAndLaterUseThrows` go. `QueryTests.QueriesAreRefusedInsideAWriteAndFromAPrimitive`
  reads `t.Graph.Queries` and `q.Name` (`QueryTests.cs:688`); it keeps its assertion, which now reads
  `CompiledQuery.Name`.
- Disposal leaves no finalization: N queries defined and disposed run no sentinel finalizer (a test-only counter stays
  0), and neither do N definitions the engine rejects.
- A documented case that is not collected: a query whose filter captures the query (or an object that holds it, such
  as a service with a `Query` field) stays reachable across forced collections and drains, with its engine rule
  registered; `Dispose` then frees the rule, and the query is collected after that.
- A documented case that is never collected: a query held by a service whose field a global rule's filter reads stays
  reachable across forced collections and drains, even after `Dispose`; `Dispose` still frees its engine rule, call
  sites and `VariantsByDescription` entry.
- A drain ending: the stress test of phase 3 with dropped queries and dropped scopes finalized together while a drain
  is ending; every compiled query is disposed and every core released, and none waits for a later abandonment.
- A dropped `EGraph` with undisposed queries is reclaimed after one full collection, and no release is queued.
- Retained memory: 1,000 dropped queries that ran retain at most 64 B each after collection and drain.
- A resurrected query, stored by a finalizable holder as for scopes: after `GC.Collect`, `WaitForPendingFinalizers`
  and `DrainReleases` (the sentinel only queues the compiled query, and the drain disposes it), it throws
  `ObjectDisposedException` on use, and its `IsDisposed` reads false.
- Mutation review: the tests must fail if `CompiledQuery` references its `Query` again, if the table holds `Query`
  objects, if `Dispose` stops suppressing the sentinel, if the drain skips `CompiledQuery.Dispose`, if a
  `GC.KeepAlive(query)` goes (under the hook, with tiered compilation off), if `DefineQuery` refuses a live query's
  name again, or if the drain's re-check stops reading the queue of compiled queries.

**Phase 5: documentation (small).** Entry: phases 3 and 4.

architecture.md section 2.12, first bullet, becomes:

> Scopes are managed by the garbage collector. A scope lives while anything referencing it is reachable: the `Scope`
> itself, a descendant, or an object over it (an extractor and its results, a run report that records its steps, a local
> rule it defined, a `CaseSet` or one of its cases, a `ScopeWriter`). Such a holder keeps the scope's ancestors alive
> too. A `ClassId` alone does not keep its scope: it references the scope's origin, not the scope. Once nothing reaches
> it, a collection of the generation it reached finds it (for a scope that did real work, typically a full collection)
> and the library releases its engine state and local rules on a pool thread, or at the end of the current call in its
> thread domain. `Scope.Dispose()` is optional. It never throws, refuses new calls on the scope at once
> (`ObjectDisposedException`), lets calls in progress use its classes (such a call completes or is refused with
> `ObjectDisposedException`), and releases the scope as soon as it has no live descendants (transient query children
> included) and no call in progress. Its classes stay usable in its live descendants until then. Disposing the root does
> nothing. A scope dropped without `Dispose` keeps its ancestors' live-descendant count until it is collected: a
> writable ancestor does not compact, at its `Freeze` or in its writes and runs, and a scoped read of its frozen tag is
> refused. Dispose such scopes when that matters. A delegate the graph holds (in a rule, split rule or query body, or a
> lattice) keeps everything it captures for as long as it is registered, which for a global definition is the graph's
> life. So it must not capture a scope or query meant to be collected, or anything that holds one. A scope captured by
> its own local rule is released by `Dispose` and collected after it; one captured by a global rule is released by
> `Dispose` but never collected.

Section 2.1's "the rulesets and their rules, the queries" becomes "the rulesets and their rules, the queries in use"
(the graph keeps each query's compiled form until the query is released, never the `Query`). Section 2.2 ("A
`ClassId` carries the scope that returned it") becomes: usable in that scope and its live descendants; calls on a
disposed scope are refused at entry. Section 2.11 adds that a release can run on a pool thread, through the
same domain claim a `Dispose` from another thread uses. Section 2.8's "a writable child per question, disposed after
it" becomes "disposed after it or dropped". Section 2.7's "A query is `IDisposable`: dispose a query made for one
request to free its engine rule" becomes: a query lives while the `Query` is reachable, and a dropped query's engine
rule is freed after a collection finds it; `Dispose` frees it at once. A delegate the graph holds keeps what it
captures (section 2.12): a query captured by its own body lives as long as the graph unless it is disposed, and one
captured by a global rule, split rule or lattice is never collected, though `Dispose` frees its engine rule. A query's
name is a label for messages and need not be unique.

XML documentation:

- `Scope` class remarks: the lifetime paragraph above, shortened, with its rule in the owner's words, "a scope lives
  while anything referencing it is reachable", and its holders keeping the scope's ancestors alive too; `Dispose` as
  early, deterministic release; a delegate the graph holds keeps what it captures for as long as it is registered, so
  a scope captured by a global rule's delegate is never collected, even after `Dispose`.
- `Scope.DefineRule`, `Ruleset.DefineRule`, `EGraph.DefineSplit` and the `ValueLattice` constructor: a delegate
  there is held while it is registered (a local rule's until its scope is released; a global rule's, a split rule's
  and a lattice's for the graph's life), so it must not capture a scope or query meant to be collected, or anything
  that holds one.
- `Scope.Dispose`: optional; what it refuses; classes stay usable in live descendants; when the release happens.
- `Scope.IsDisposed`: true only after `Dispose` or a case release, never because of collection.
- `Scope.Children`: lists children that are not disposed and are still reachable; a child nobody holds may leave it
  at any collection.
- `Scope.CreateChild`: the child lives while referenced; dispose it for prompt release.
- `Scope.Run`: replace the clause on an `Until` refusing a disposed ancestor's class with the split-step
  `ObjectDisposedException` of a run whose own scope is disposed mid-run.
- `CaseSet`: "which the caller disposes" becomes optional; a dropped set's cases are released once dropped too, and a
  case still held lives on.
- `ScopeOrigin` and `ClassChecks` remarks: the class rule, and the disposed flag as wording only.
- `Query` class summary: a query lives while referenced; `Dispose` is the early release, which frees its engine rule
  and call sites at once, and a dropped query's are freed after a collection finds it; a delegate in its body that
  captures the query keeps it until it is disposed, and a global rule's delegate that captures it keeps it for the
  graph's life, even after `Dispose`.
- `Query.Name`: a label for messages and exceptions, not necessarily unique ("unique among the graph's live queries"
  goes).
- `Query.Dispose`: optional; "its name may be defined again" goes.
- `Query.IsDisposed`: true only after `Dispose`.
- `EGraph.DefineQuery`: "dispose one made for a single request" becomes "drop or dispose"; the `name` parameter is a
  label that need not be unique; the `ArgumentException` covers an empty name only; a delegate in the body is held
  until the query is released, so it must not capture the query, or anything that holds it, or the query lives as
  long as the graph unless it is disposed, nor a scope or another query meant to be collected.

A [decisions](../engine/decisions.md) entry records, as an exception beside the "Never finalizers" flush choice, one
private, queue-only finalizable sentinel per non-root scope, which the owner accepted (question 2), and the same
mechanism per query, which follows from the owner's answer that a dropped `Query` is released after a collection like
a scope (question 7). The roadmap line for this plan is removed when phase 5 lands.

**Dependencies.**

- PR #2 (merged): phase 0 removes its code. Phases 1 to 3 build on main's call counting and release deferral
  (`Scope.BeginCall`, `EndCall`, `TryRelease`) and do not depend on PR #2.
- [Colored-context deferred features](./colored-context-deferred-features.md): its suffix compaction would remove the
  permanent cost of a writable parent that freezes before a dropped child is collected; this plan does not wait for it
  (question 4). Its question on holding children of a large writable scope interacts with this plan's documentation of
  `Dispose`.
- [Interning and value lifetime](./interning-and-value-lifetime.md): its churn tests (phase B) should churn dropped
  scopes as well as disposed ones once this lands.
- [Parallelism test plan](./parallelism-test-plan.md): its concurrent-children stress can run with the forced
  collection hook.

## 9. Questions for the owner

1. **The class rule.** Do you accept that `Dispose` stops new calls on the disposed scope only, and that its classes
   stay usable in its live descendants until it is released? This reverses PR #2's documented refusal in a live
   descendant and its mid-run `Until` note, and flips `DisposeTests.ADisposedScopesClassesDie`. One mid-run failure
   remains, as today: a run on a scope disposed during the run is refused with `ObjectDisposedException` at its next
   split step, after earlier steps may have written. Is documenting that enough?

   Answered: the class rule is accepted. `Dispose` stops new calls on the disposed scope only; its classes stay usable
   in its live descendants until it is released; phase 0 removes PR #2's per-call exemption. As recommended,
   documenting the remaining split-step refusal is enough (phase 0 and phase 5).
2. **A finalizer.** Do you accept one private, queue-only finalizable sentinel per non-root scope, suppressed when
   the scope is released (so a disposed and released scope leaves no finalization work), plus one shared weak
   reference per graph, recorded in [decisions](../engine/decisions.md) beside the "Never finalizers" flush choice?
   If not, approach B' needs one finalizable object per graph and finds a scope that died young only at the next full
   collection; if no finalizer at all, approach B applies, and a graph that is not called again keeps its dropped
   scopes until it is dropped.

   Answered: approach C is accepted: one private, queue-only finalizable sentinel per non-root scope, suppressed by the
   release, plus one shared weak reference per graph, recorded in decisions as an exception beside the "Never
   finalizers" flush choice.
3. **The release thread.** May releases run on a thread-pool work item? The alternative is B's: sweeps at the
   library's own later calls on the graph, at the price of an idle graph keeping its dropped scopes until its next
   call. Draining C's queue at call entries instead is not offered: the library cannot tell whether a thread holds
   another domain's guard without a per-call thread-static count (section 3.4).

   Answered: yes, releases run on a thread-pool work item.
4. **Collection timing.** Is it enough to document that a dropped child can stop a writable parent's compaction (at
   `Freeze` and in its writes and runs) and can block a frozen tag's scoped reads until it is collected, which for a
   scope that ran is typically the next full collection, with `Dispose` as the remedy? Or should this wait for the
   colored-context plan's suffix compaction?

   Answered: document it, with `Dispose` as the remedy; no suffix compaction now.
5. **`Children`.** Should it list only children that are still reachable (recommended), knowing that a dropped child
   leaves it at an unpredictable collection?

   Answered: yes, `Children` lists only children that are still reachable.
6. **Holders.** Should the documented rule be "alive while referenced", including a held `Rule`, `RunReport` with
   steps, `Extractor`, `ExtractedTerm` or `NodeView` keeping its scope and that scope's ancestors alive? (A
   `QueryMatch` is a ref struct and cannot outlive its callback.)

   Answered: yes, "a scope lives while anything referencing it is reachable", with those holders.
7. **`Query`.** A defined query is held by name by its graph, like a global rule. Should it stay released by `Dispose`
   or with the graph, or should the requirement extend to it, which needs a weak name table?

   Answered: queries are managed by the GC too, and query names become labels. A dropped `Query` is released after
   collection like a scope, and `DefineQuery` no longer requires a name that is unique among live queries. Section 3.5
   shows that no weak name table is needed: the graph keeps compiled queries keyed by serial, which never reach a
   `Query`.

The commits carry `+semver: minor`: the public surface does not change, but documented behaviour of 1.0.0 relaxes
(calls that threw now succeed, and a dropped scope or query is released).
