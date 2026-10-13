using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// The depth limit of a scope tree (colors X36): the root is at depth 0.
        /// </summary>
        const int MaxDepth = 31;

        readonly object childrenLock = new();
        readonly List<Scope> children = new();
        readonly Dictionary<ScopeTag, (long Stamp, int CopyRules)> mirrorSyncs = new(ReferenceEqualityComparer.Instance);
        int transientChildren;
        bool released;
        bool frozenWithTag;
        CompiledRule[] localCompiled = Array.Empty<CompiledRule>();
        ReadOnlyCollection<Rule> localRulesView = Array.AsReadOnly(Array.Empty<Rule>());

        /// <summary>
        /// Gets the parent scope; <c>null</c> for the root.
        /// </summary>
        public Scope? Parent { get; }

        /// <summary>
        /// Gets the scope's tag: <see cref="EGraph.RootTag"/> for the root, the one given to <see cref="CreateChild"/>
        /// otherwise (<c>null</c> for none). Scoped atoms resolve a tag to the nearest ancestor-or-self carrying it (§6.7).
        /// </summary>
        public ScopeTag? Tag { get; }

        /// <summary>
        /// Gets whether the scope is frozen (colors §10.8): it never changes again; reads, queries and extractors on it
        /// are thread-safe, and its children each have their own thread domain and may run concurrently.
        /// </summary>
        public bool IsFrozen => Engine.IsFrozen;

        /// <summary>
        /// Gets the live children made by <see cref="CreateChild"/>, in creation order (a snapshot). Disposed children
        /// are not listed, even while their release waits; neither are the transient children made for queries on a frozen
        /// scope (D34).
        /// </summary>
        public IReadOnlyList<Scope> Children
        {
            get
            {
                lock (childrenLock)
                {
                    var live = new List<Scope>(children.Count);
                    foreach (var child in children)
                        if (child.IsDisposed == false)
                            live.Add(child);

                    return live.AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Gets the rules defined on this scope with <see cref="DefineRule"/>, in definition order (a snapshot).
        /// </summary>
        public IReadOnlyList<Rule> LocalRules => Volatile.Read(ref localRulesView);

        /// <summary>
        /// Creates a child scope (§6.7): it starts as a copy of this scope and costs in proportion to what it touches.
        /// Thread-safe on a frozen scope, whose children each get their own thread domain; a child of a writable scope
        /// shares its thread domain.
        /// </summary>
        /// <param name="tag">The child's tag, which scoped atoms resolve; <c>null</c> for none.</param>
        /// <param name="options">The child's options; <c>null</c> for <see cref="ScopeOptions.Default"/>.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or it is the root's.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The options' degree of parallelism is less than 1.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted, at depth 31 (colors X36), or in use on another
        /// thread; or the call comes from a run observer (a structural call, §6.11), or from a primitive, lattice, filter or
        /// cost function.</exception>
        public Scope CreateChild(ScopeTag? tag = null, ScopeOptions? options = null)
        {
            if (tag is not null)
            {
                Graph.CheckTag(tag, nameof(tag));
                if (ReferenceEquals(tag, Graph.RootTag))
                    throw new ArgumentException("The root tag is the root's; tag a child with a tag of its own.", nameof(tag));
            }

            if (options?.DegreeOfParallelism is < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.DegreeOfParallelism, "The degree of parallelism must be at least 1.");

            var held = EnterRead();
            try
            {
                // Only a writable scope can be running, so only its guard can be inside a run observer (§6.11).
                if (held && Guard.InObserver)
                    throw new InvalidOperationException("Not allowed inside a run observer");

                if (Depth() >= MaxDepth)
                    throw new InvalidOperationException($"Scope '{Path}' is at depth {MaxDepth}, the deepest a scope tree goes (colors X36).");

                var contextOptions = new Relations.ContextOptions
                {
                    Parallelism = options?.DegreeOfParallelism is { } degree ? new Relations.ParallelHeuristics(degree) : null,
                    InheritWatermarks = Graph.InheritWatermarks,
                };

                lock (childrenLock)
                {
                    if (IsDisposed)
                        throw new ObjectDisposedException(Path, $"Scope '{Path}' is disposed.");

                    var engine = CreateEngineChild(contextOptions);
                    var path = Path + "/" + (tag?.Name ?? "scope") + "#" + Graph.NextScopeSerial().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var child = new Scope(this, tag, engine, IsFrozen ? new ScopeGuard() : Guard, path);
                    children.Add(child);
                    return child;
                }
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Brings this scope's mirrors current (D48), then freezes it (colors §10.8): the engine flushes, rebuilds, plans
        /// the rules this scope's descendants can run on its statistics (E10), and freezes storage. The parent must be
        /// frozen. Local rules are fixed from here on, and each live child subtree that shared this scope's thread domain
        /// gets its own; a child another thread created once the scope read as frozen keeps the domain it was given. A
        /// second call does nothing.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is writable; the scope is faulted or in use on another
        /// thread; or the call comes from a run observer ("Not allowed inside a run observer"), from inside another call of
        /// its thread domain (a query callback, a write), or from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A merge the freeze ran failed; the scope is then faulted.</exception>
        public void Freeze()
        {
            EngineCallMarker.ThrowIfActive();
            if (IsFrozen)
                return;

            if (Guard.Depth > 0)
            {
                if (Guard.InObserver)
                    throw new InvalidOperationException("Not allowed inside a run observer");

                throw new InvalidOperationException($"Scope '{Path}' cannot be frozen from inside another call of its thread domain.");
            }

            Enter();
            try
            {
                if (IsFrozen)
                    return;

                if (Parent is { IsFrozen: false })
                    throw new InvalidOperationException("A scope can be frozen only once its parent is");

                // A child catches up with what its parent wrote before it froze, left out of Version as at any entry (N87).
                // The root is left to the engine's freeze, which rebuilds it as before.
                if (IsRoot == false)
                {
                    Runtime.TakeFailure();
                    try
                    {
                        CatchUp();
                    }
                    catch (Relations.PanicException panic)
                    {
                        throw FaultWith(panic);
                    }
                }

                // The definition lock keeps a first scoped read of this scope's tag from creating a mirror between the sync
                // and the count of frozen scopes (§6.8, D38): such a read is refused from then on.
                lock (Graph.DefinitionLock)
                {
                    if (Tag is { } tag && Graph.Mirrors.CopyRules(tag).Length > 0)
                        MirrorSync.Ensure(this, [tag], $"Scope '{Path}'", null);

                    FreezeEngine();
                    if (Tag is { } frozenTag)
                    {
                        Graph.Mirrors.AddFrozen(frozenTag);
                        frozenWithTag = true;
                    }
                }

                // Each child subtree that shares this scope's guard gets a guard of its own, as the engine gives each a
                // domain (colors §8.1 step 7). This thread holds the shared guard and no other call of the domain is in
                // progress, so none of those subtrees is in use. A child that another thread created once the engine
                // context froze (IsFrozen was already true) has a guard of its own, which a call may hold: it keeps it.
                var shared = Guard;
                lock (childrenLock)
                    foreach (var child in children)
                        if (ReferenceEquals(child.Guard, shared))
                            child.AssignGuard(new ScopeGuard());
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Defines a rule local to this scope (§6.7): active in it and its descendants, scheduled with
        /// <paramref name="ruleset"/>, whose licence it takes (D12). The engine rule is global; only the schedule's choice
        /// of rules makes it local. Its class constants may come from this scope or its ancestors (D39). It is freed when
        /// the scope is released.
        /// </summary>
        /// <param name="ruleset">The ruleset that schedules it.</param>
        /// <param name="name">The rule's name.</param>
        /// <param name="define">Collects the rule.</param>
        /// <returns>The rule.</returns>
        /// <exception cref="ArgumentException">The ruleset belongs to another graph, or the name is empty.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen, faulted, or in use on another thread; or the
        /// call comes from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="RuleDefinitionException">The rule is not valid; nothing is registered.</exception>
        public Rule DefineRule(Ruleset ruleset, string name, Action<RuleBuilder> define)
        {
            ArgumentNullException.ThrowIfNull(ruleset);
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(define);
            if (ReferenceEquals(ruleset.Graph, Graph) == false)
                throw new ArgumentException($"Ruleset '{ruleset.Name}' belongs to another graph.", nameof(ruleset));

            EngineCallMarker.ThrowIfActive();
            ThrowIfNotUsable();
            if (IsFrozen)
                throw new InvalidOperationException($"Scope '{Path}' is frozen; its local rules are fixed");

            Enter();
            try
            {
                if (IsFrozen)
                    throw new InvalidOperationException($"Scope '{Path}' is frozen; its local rules are fixed");

                if (Guard.InObserver)
                    throw new InvalidOperationException("Not allowed inside a run observer");

                var builder = new RuleBuilder(Graph, name);
                try
                {
                    define(builder);
                }
                finally
                {
                    builder.Close();
                }

                lock (Graph.DefinitionLock)
                {
                    var rule = RuleCompiler.Compile(Graph, builder.Draft, ruleset, this);
                    var nextCompiled = new CompiledRule[localCompiled.Length + 1];
                    localCompiled.CopyTo(nextCompiled, 0);
                    nextCompiled[^1] = rule;
                    var nextRules = new Rule[nextCompiled.Length];
                    for (var i = 0; i < nextRules.Length; i++)
                        nextRules[i] = nextCompiled[i].Rule;

                    Volatile.Write(ref localCompiled, nextCompiled);
                    Volatile.Write(ref localRulesView, Array.AsReadOnly(nextRules));
                    return rule.Rule;
                }
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Gets the compiled local rules, in definition order (a snapshot read without locks).
        /// </summary>
        internal CompiledRule[] LocalCompiled => Volatile.Read(ref localCompiled);

        /// <summary>
        /// Gets whether the mirrors of <paramref name="tag"/> in this scope are current (§6.8): synced when the rows this
        /// scope's views show were as they are now, with every copy rule the tag has now. The rows are stamped with the
        /// view stamp (the change stamps of this scope and its ancestors), not <see cref="Version"/>: a write into a
        /// writable ancestor changes this scope's view of a table without moving its own version. Called on the scope's
        /// domain thread.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <param name="copyRules">The number of copy rules the tag has now.</param>
        /// <returns>Whether no sync is needed.</returns>
        internal bool IsMirrorCurrent(ScopeTag tag, int copyRules)
        {
            lock (mirrorSyncs)
                return mirrorSyncs.TryGetValue(tag, out var synced) && synced.Stamp == Engine.ViewStamp && synced.CopyRules == copyRules;
        }

        /// <summary>
        /// Records that the mirrors of <paramref name="tag"/> in this scope were synced at its current view stamp.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <param name="copyRules">The number of copy rules the sync ran.</param>
        internal void MarkMirrorSynced(ScopeTag tag, int copyRules)
        {
            lock (mirrorSyncs)
                mirrorSyncs[tag] = (Engine.ViewStamp, copyRules);
        }

        /// <summary>
        /// Creates a transient child for one query on this frozen scope (D34): a new engine child, counted in this scope's
        /// transient children so that it defers the release (D40), and not listed in <see cref="Children"/>. Its runtime's
        /// owner is this scope, so that the classes a query returns carry this scope's origin. Thread-safe.
        /// </summary>
        /// <returns>The child's engine side; give it back with <see cref="ReleaseTransient"/>.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        internal ContextEngineScope CreateTransient()
        {
            lock (childrenLock)
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(Path, $"Scope '{Path}' is disposed.");

                var child = CreateEngineChild(new Relations.ContextOptions { InheritWatermarks = Graph.InheritWatermarks });
                child.Runtime.Owner = this;
                transientChildren++;
                return child;
            }
        }

        /// <summary>
        /// Disposes a transient child made by <see cref="CreateTransient"/>, then releases this scope if it is disposed and
        /// nothing else defers it. Never throws.
        /// </summary>
        /// <param name="child">The transient child.</param>
        internal void ReleaseTransient(ContextEngineScope child)
        {
            try
            {
                child.Dispose();
            }
            catch (InvalidOperationException)
            {
                // A transient child has no children and runs nothing once its query ends; nothing can refuse it here.
            }

            lock (childrenLock)
                transientChildren--;

            if (IsDisposed)
                TryRelease();
        }

        /// <summary>
        /// Releases the scope once nothing defers it (D40): it must be disposed, have no live or transient children and no
        /// call in progress, and its thread domain must not be in use on another thread (then the release is handed to the
        /// domain's guard, whose last exit runs it). The release frees the local rules' variants, disposes the engine
        /// context, uncounts a frozen tag, leaves the parent, and releases the parent if it is disposed. Runs on whichever
        /// thread removes the last obstacle; never throws; idempotent.
        /// </summary>
        internal void TryRelease()
        {
            if (IsRoot || IsDisposed == false)
                return;

            // Inside an engine call of this scope's domain (a primitive that disposed it, say), the engine would refuse
            // the disposal as re-entrant: the domain's last exit runs it instead.
            if (Engine.Context.Context.Domain.Depth > 0)
            {
                Guard.DeferRelease(this);
                return;
            }

            ScopeGuard entered;
            var spinner = new SpinWait();
            while (true)
            {
                lock (childrenLock)
                {
                    if (released || children.Count > 0 || transientChildren > 0 || Volatile.Read(ref calls) > 0)
                        return;
                }

                entered = Guard;
                if (entered.TryEnterForRelease())
                {
                    // A freeze of the nearest writable ancestor may have given this scope a new guard meanwhile.
                    if (ReferenceEquals(entered, Guard))
                        break;

                    entered.ExitAfterRelease();
                    continue;
                }

                // The domain is busy on another thread, for a call or another release: its last exit runs this release. If
                // it was left meanwhile, the exit may already have taken the list, so try again.
                entered.DeferRelease(this);
                if (entered.IsHeldByAnotherThread)
                    return;

                spinner.SpinOnce();
            }

            try
            {
                lock (childrenLock)
                {
                    if (released || children.Count > 0 || transientChildren > 0 || Volatile.Read(ref calls) > 0)
                        return;

                    released = true;
                }

                foreach (var rule in LocalCompiled)
                    rule.Variants.Free(Graph);

                try
                {
                    Engine.Dispose();
                }
                catch (InvalidOperationException)
                {
                    // Disposal never throws (D40). The checks above leave nothing that can refuse it.
                }

                if (frozenWithTag)
                    Graph.Mirrors.RemoveFrozen(Tag!);
            }
            finally
            {
                entered.ExitAfterRelease();
            }

            Parent!.RemoveChild(this);
        }

        /// <summary>
        /// Gives this scope and its descendants a new guard: the subtree becomes its own thread domain, after its parent
        /// froze (colors §8.1 step 7).
        /// </summary>
        void AssignGuard(ScopeGuard next)
        {
            guard = next;
            lock (childrenLock)
                foreach (var child in children)
                    child.AssignGuard(next);
        }

        /// <summary>
        /// Removes a released child, then releases this scope if it is disposed and the child was the last obstacle.
        /// </summary>
        void RemoveChild(Scope child)
        {
            lock (childrenLock)
                children.Remove(child);

            if (IsDisposed)
                TryRelease();
        }

        /// <summary>
        /// Gets the scope's depth: 0 for the root.
        /// </summary>
        int Depth()
        {
            var depth = 0;
            for (var up = Parent; up is not null; up = up.Parent)
                depth++;

            return depth;
        }

        /// <summary>
        /// Creates an engine child, turning a failure recorded on this scope into a <see cref="RuleEvaluationException"/>
        /// that faults it (D18).
        /// </summary>
        ContextEngineScope CreateEngineChild(Relations.ContextOptions options)
        {
            Runtime.TakeFailure();
            try
            {
                return Engine.CreateChild(options);
            }
            catch (Relations.PanicException panic)
            {
                throw FaultWith(panic);
            }
        }

        /// <summary>
        /// Freezes the engine context with the plans of E10: none for the root, whose statistics are empty by design;
        /// otherwise the default variants of every rule a descendant can run: every global rule, the local rules of this
        /// scope and its ancestors, every live query, and every mirror copy rule. Other containers' local rules are left to
        /// be planned on their own statistics. The listed queries are held (<see cref="CompiledQuery.Acquire"/>) until the
        /// engine has frozen, so a query disposed on another thread meanwhile cannot free a listed variant; one disposed
        /// before the list is built is left out. Global rules, ancestors' local rules and copy rules are never freed while
        /// this scope lives.
        /// </summary>
        void FreezeEngine()
        {
            var held = new List<CompiledQuery>();
            try
            {
                var plans = IsRoot ? new List<Relations.EGraphRuleId>() : CollectPlanRules(held);
                Runtime.TakeFailure();
                try
                {
                    Engine.Freeze(plans);
                }
                catch (Relations.PanicException panic)
                {
                    throw FaultWith(panic);
                }
            }
            finally
            {
                foreach (var query in held)
                    query.Release();
            }
        }

        /// <summary>
        /// Lists the default variants of the rules this scope's descendants can run (§6.7, E10), holding each listed query in
        /// <paramref name="held"/>.
        /// </summary>
        List<Relations.EGraphRuleId> CollectPlanRules(List<CompiledQuery> held)
        {
            var plans = new List<Relations.EGraphRuleId>();
            var seen = new HashSet<Relations.EGraphRuleId>();
            void Add(LoweredRule? variant)
            {
                if (variant is not null && seen.Add(variant.Id))
                    plans.Add(variant.Id);
            }

            foreach (var ruleset in Graph.Rulesets)
                foreach (var rule in ruleset.Compiled)
                    Add(rule.Variants.Default);

            for (var scope = this; scope is not null; scope = scope.Parent)
                foreach (var rule in scope.LocalCompiled)
                    Add(rule.Variants.Default);

            foreach (var query in Graph.Queries)
            {
                var compiled = query.Compiled;
                try
                {
                    compiled.Acquire();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed meanwhile: its variants may be freed at any moment, and no descendant can run it.
                    continue;
                }

                held.Add(compiled);
                Add(compiled.Variants.Default);
            }

            foreach (var rule in Graph.Mirrors.AllCopyRules())
                Add(rule.Variants.Default);

            return plans;
        }

        /// <summary>
        /// Turns a failure the engine recorded on this scope into a <see cref="RuleEvaluationException"/>, with the
        /// runtime's record of it when there is one, and faults the scope (D18).
        /// </summary>
        /// <param name="panic">The engine's panic.</param>
        /// <returns>The exception, to throw.</returns>
        internal RuleEvaluationException FaultWith(Relations.PanicException panic)
        {
            var failure = Runtime.TakeFailure();
            var exception = new RuleEvaluationException(failure?.RuleName, failure?.Bindings, failure?.What ?? panic.PanicMessage, failure?.Exception ?? panic);
            Fault(exception);
            return exception;
        }

    }

}
