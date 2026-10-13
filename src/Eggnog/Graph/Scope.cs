using System;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// One engine context with the rule layer's state for it: where facts are written, rules run, reads answered and
    /// terms extracted. The graph's <see cref="EGraph.Root"/> is one; <see cref="CreateChild"/> makes children, which
    /// start as copies of their parent and cost in proportion to what they touch (§2.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not thread-safe (D47): scopes that share a thread domain (a writable scope and everything created under it) are
    /// used from one thread at a time, and a second thread gets <see cref="InvalidOperationException"/> instead of
    /// interleaving. Children of a frozen scope each have their own thread domain, and reads, queries and extractors of a
    /// frozen scope are thread-safe.
    /// </para>
    /// <para>
    /// After a <see cref="RuleEvaluationException"/> from a run or a write the scope is faulted (D18), and every later call
    /// except <see cref="Dispose"/> throws; a faulted root makes its graph unusable. <see cref="Dispose"/> never throws
    /// (D40): it makes the scope unusable at once, and releases its engine state once nothing defers it.
    /// </para>
    /// </remarks>
    public sealed partial class Scope : IDisposable
    {

        volatile Exception? failure;
        Exception? engineFailure;
        int disposed;
        int calls;
        long upkeepStamps;
        HostBatch? openBatch;
        int batchesBelow;
        volatile ScopeGuard guard;

        /// <summary>
        /// Initializes the root scope of <paramref name="graph"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        internal Scope(EGraph graph)
        {
            Graph = graph;
            IsRoot = true;
            Path = "root";
            Tag = graph.RootTag;
            Origin = graph.RootOrigin;
            guard = new ScopeGuard();
            var engine = new ContextEngineScope(graph, graph.Engine.Root);
            Engine = engine;
            Runtime = engine.Runtime;
            Runtime.Owner = this;
        }

        /// <summary>
        /// Initializes a child scope over an engine child (§6.7).
        /// </summary>
        /// <param name="parent">The parent scope.</param>
        /// <param name="tag">The child's tag, of the parent's graph; <c>null</c> for none.</param>
        /// <param name="engine">The engine child.</param>
        /// <param name="guard">The parent's guard when the parent is writable, a new one when it is frozen.</param>
        /// <param name="path">The child's path.</param>
        internal Scope(Scope parent, ScopeTag? tag, ContextEngineScope engine, ScopeGuard guard, string path)
        {
            Graph = parent.Graph;
            Parent = parent;
            Tag = tag;
            Path = path;
            Origin = new ScopeOrigin(Graph, parent.Origin, path, false);
            this.guard = guard;
            Engine = engine;
            Runtime = engine.Runtime;
            Runtime.Owner = this;
        }

        /// <summary>
        /// Gets the graph the scope belongs to.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets whether this is the graph's root.
        /// </summary>
        public bool IsRoot { get; }

        /// <summary>
        /// Gets whether the scope is faulted: set after a <see cref="RuleEvaluationException"/> from a run or a write
        /// (D18), or after the engine faulted the scope's context (X12).
        /// </summary>
        public bool IsFaulted => Failure is not null;

        /// <summary>
        /// Gets what faulted the scope; <c>null</c> while it is not faulted. Every later call except
        /// <see cref="Dispose"/> throws <see cref="InvalidOperationException"/> with this as its inner exception. For a
        /// fault the engine recorded, an <see cref="InvalidOperationException"/> describing it.
        /// </summary>
        public Exception? Failure
        {
            get
            {
                if (failure is { } cause)
                    return cause;

                if (Volatile.Read(ref engineFailure) is { } described)
                    return described;

                if (IsDisposed || Engine.IsFaulted == false)
                    return null;

                var created = new InvalidOperationException($"The engine faulted scope '{Path}': an exception escaped one of its writes, runs or rebuilds.");
                return Interlocked.CompareExchange(ref engineFailure, created, null) ?? created;
            }
        }

        /// <summary>
        /// Gets whether <see cref="Dispose"/> has been called (D40), even while the release waits for live children or a
        /// call in progress. Never true for the root.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        /// <summary>
        /// Gets the scope's change stamp: the engine's (X30) without the moves of its upkeep (N62, N87). It moves on every
        /// effective change that a write, run or freeze of this scope makes to its own layer, including the catch-up copies
        /// that hide an ancestor's rows when one of this scope's classes merges in that call. Queries, reads and writes that
        /// change nothing never move it. Two kinds of upkeep are left out, because neither is a change of this scope's own:
        /// the sync of the internal mirror tables that a run or query reading this scope's tag makes first (§6.8), and the
        /// catch-up a call into this scope or a descendant makes first, which brings this scope's layer in line with what a
        /// writable ancestor wrote since (colors §10.3). A write into an ancestor therefore never moves it, whether or not
        /// this scope's layer needs copies to follow that write.
        /// </summary>
        /// <remarks>
        /// Read from another thread while the scope's domain is in use, the answer may be torn by an upkeep in progress; a
        /// frozen scope has none.
        /// </remarks>
        public long Version => Engine.Version - Volatile.Read(ref upkeepStamps);

        /// <summary>
        /// Gets the scope's path, for messages: <c>root</c>, <c>root/schema#2/query#7</c>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Disposes the scope (D40): never throws. It makes the scope unusable at once and invalidates the class values it
        /// returned (D39). Its local rules and engine context are released once it has no live children (transient query
        /// children included) and no call is in progress in it or, on another thread, in its thread domain; whichever thread
        /// removes the last obstacle releases it. A second call does nothing; on the root it does nothing, because the root
        /// lives as long as the graph.
        /// </summary>
        public void Dispose()
        {
            if (IsRoot || Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            Origin.Disposed = true;
            TryRelease();
        }

        /// <summary>
        /// Gets the scope's path.
        /// </summary>
        /// <returns>The path.</returns>
        public override string ToString() => Path;

        /// <summary>
        /// Gets the engine side of the scope.
        /// </summary>
        internal ContextEngineScope Engine { get; }

        /// <summary>
        /// Gets the buffers external functions write during this scope's engine calls: the scope's context's value of the
        /// graph's runtime slot.
        /// </summary>
        internal ScopeRuntime Runtime { get; }

        /// <summary>
        /// Gets the guard of the scope's thread domain (D47). Reassigned only when the scope's nearest writable ancestor
        /// freezes, on that domain's thread, while it holds the guard and no other call of the domain is in progress (see
        /// <see cref="Freeze"/>); so the guard of a scope a call has entered never changes until the call leaves it. Enter it
        /// through <see cref="EnterGuard"/>, which re-reads it once entered.
        /// </summary>
        internal ScopeGuard Guard => guard;

        /// <summary>
        /// Gets the origin of the class values the scope returns (D39).
        /// </summary>
        internal ScopeOrigin Origin { get; }

        /// <summary>
        /// Faults the scope (D18); the first cause is kept.
        /// </summary>
        /// <param name="cause">What faulted it.</param>
        internal void Fault(Exception cause)
        {
#pragma warning disable CS0420 // A volatile field passed by reference: Interlocked keeps its semantics.
            Interlocked.CompareExchange(ref failure, cause, null);
#pragma warning restore CS0420
        }

        /// <summary>
        /// Throws when the scope is disposed or faulted.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; the cause is the inner exception.</exception>
        internal void ThrowIfNotUsable()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(Path, $"Scope '{Path}' is disposed.");

            if (Failure is { } cause)
                throw new InvalidOperationException($"Scope '{Path}' is faulted: {cause.Message}", cause);
        }

        /// <summary>
        /// Throws when the scope may not be written or run: inside a run observer, or frozen.
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope may not be written.</exception>
        internal void ThrowIfNotWritable()
        {
            if (guard.InObserver)
                throw new InvalidOperationException("Not allowed inside a run observer");

            if (Engine.IsFrozen)
                throw new InvalidOperationException($"Scope '{Path}' is frozen; write and run in a child of it.");
        }

        /// <summary>
        /// Leaves the stamp moves of a mirror sync (D48) out of <see cref="Version"/>: mirror tables are internal, so a sync
        /// is not a change a caller can see. Called on the scope's domain thread, right after the sync.
        /// </summary>
        /// <param name="moved">How far the sync moved the engine's change stamp.</param>
        internal void ExcludeMirrorSync(long moved)
        {
            if (moved != 0)
                Interlocked.Add(ref upkeepStamps, moved);
        }

        /// <summary>
        /// Brings the scope up to date at the entry of a call (colors §10.3, X42), leaving the catch-up out of
        /// <see cref="Version"/> (N87). Each writable ancestor is brought up to date first, outermost first, and its
        /// catch-up is left out of its own <see cref="Version"/>; then this scope's staged writes are flushed (there are
        /// none between calls: every batch flushes before it returns, D30) and its layer is rebuilt against its ancestors.
        /// The root is brought up to date as before, its stamp untouched (colors §14.1). Called on the domain's thread,
        /// with its guard held. Cost: two reads of the change stamp per writable scope from here up to the first frozen one
        /// or the root, beyond the engine's own catch-up, which this call makes in the order the engine would.
        /// </summary>
        /// <exception cref="Relations.PanicException">The flush of host writes recorded a failure.</exception>
        internal void CatchUp()
        {
            if (IsRoot)
            {
                Engine.EnsureClean();
                return;
            }

            if (Parent is { IsFrozen: false } parent)
                parent.CatchUp();

            var before = Engine.Version;
            Engine.EnsureClean();
            var moved = Engine.Version - before;
            if (moved != 0)
                Interlocked.Add(ref upkeepStamps, moved);
        }

        /// <summary>
        /// Counts a <see cref="Write"/> batch opened (<paramref name="delta"/> 1) or ended (-1) on this scope in each of its
        /// writable ancestors, which share its thread domain and refuse writes and runs while their count is positive
        /// (N88). Called on the domain's thread, with its guard held. Cost: one increment per writable ancestor; none at the
        /// root.
        /// </summary>
        /// <param name="delta">1 when the batch opens, -1 when it ends.</param>
        void CountOpenBatch(int delta)
        {
            for (var up = Parent; up is { IsFrozen: false }; up = up.Parent)
                up.batchesBelow += delta;
        }

        /// <summary>
        /// Enters the guard of the scope's thread domain (D47) and returns it. The guard is read again once entered: a freeze
        /// of the nearest writable ancestor may have given this scope a new one between the read and the entry (§6.7
        /// Freeze), and the entry then moves to the new guard, so that no call ever holds a guard its scope no longer has.
        /// One compare-and-swap, and one read of the field.
        /// </summary>
        /// <returns>The guard entered; the caller leaves it with <see cref="ScopeGuard.Exit"/>.</returns>
        /// <exception cref="InvalidOperationException">Another thread is using the domain.</exception>
        internal ScopeGuard EnterGuard()
        {
            while (true)
            {
                var entered = guard;
                entered.Enter(this);
                if (ReferenceEquals(entered, guard))
                    return entered;

                entered.Exit();
            }
        }

        /// <summary>
        /// Counts a call in progress on the scope: a disposed scope is not released while one runs (D40). One interlocked
        /// increment.
        /// </summary>
        internal void BeginCall() => Interlocked.Increment(ref calls);

        /// <summary>
        /// Counts a call as ended; the last call of a disposed scope releases it.
        /// </summary>
        internal void EndCall()
        {
            if (Interlocked.Decrement(ref calls) == 0 && IsDisposed)
                TryRelease();
        }

        /// <summary>
        /// Enters the scope for a call: refuses user delegates, takes the guard, and checks the scope is usable and has no
        /// open <see cref="Write"/> batch. The caller ends it with <see cref="Exit"/>(<c>true</c>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The call is not allowed now.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        internal void Enter()
        {
            EngineCallMarker.ThrowIfActive();
            EnterGuard();
            BeginCall();
            try
            {
                ThrowIfNotUsable();
                if (openBatch is not null)
                    throw new InvalidOperationException($"Scope '{Path}' has a Write batch open; make its calls through the ScopeWriter.");
            }
            catch
            {
                Exit(true);
                throw;
            }
        }

        /// <summary>
        /// Ends a call: counts it as ended, then leaves the guard when the call took it.
        /// </summary>
        /// <param name="held">Whether the call took the guard.</param>
        internal void Exit(bool held)
        {
            EndCall();
            if (held)
                Guard.Exit();
        }

        /// <summary>
        /// Enters the scope for a read (§6.6.3): a frozen scope takes no guard, since its reads are thread-safe; a writable
        /// one is entered as by <see cref="Enter"/>, then brought current. The caller ends it with
        /// <see cref="Exit"/>(result).
        /// </summary>
        /// <returns>Whether the guard was taken.</returns>
        internal bool EnterRead()
        {
            if (Engine.IsFrozen)
            {
                EngineCallMarker.ThrowIfActive();
                BeginCall();
                try
                {
                    ThrowIfNotUsable();
                }
                catch
                {
                    EndCall();
                    throw;
                }

                return false;
            }

            Enter();
            try
            {
                CatchUp();
            }
            catch
            {
                Exit(true);
                throw;
            }

            return true;
        }

        /// <summary>
        /// Enters the scope for one query of an extractor (§6.10): a frozen scope takes no guard, so any number of extractors
        /// may run on it from any threads (E5); a writable one takes its guard. The caller ends it with
        /// <see cref="Exit"/>(result).
        /// </summary>
        /// <returns>Whether the guard was taken.</returns>
        internal bool EnterExtraction()
        {
            EngineCallMarker.ThrowIfActive();
            var held = Engine.IsFrozen == false;
            if (held)
                EnterGuard();

            BeginCall();
            try
            {
                ThrowIfNotUsable();
            }
            catch
            {
                Exit(held);
                throw;
            }

            return held;
        }

        /// <summary>
        /// Enters the scope for a write or a run: as <see cref="Enter"/>, and the scope must be writable and no descendant of
        /// it may have a <see cref="Write"/> batch open (N88). The caller ends it with <see cref="Exit"/>(<c>true</c>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope may not be written or run now.</exception>
        void EnterWrite()
        {
            if (Engine.IsFrozen)
            {
                // A frozen scope takes no guard (its reads are thread-safe), so it is refused before one is taken.
                EngineCallMarker.ThrowIfActive();
                ThrowIfNotUsable();
                ThrowIfNotWritable();
            }

            Enter();
            try
            {
                ThrowIfNotWritable();

                // An open batch's plan was checked against this scope as it stands (D32): a write or a run here would
                // change what the batch read, so it waits until the batch ends (N88). One read of a field.
                if (batchesBelow > 0)
                    throw new InvalidOperationException($"Scope '{Path}' cannot be written or run while a scope under it has a Write batch open; write it before or after the batch.");

                CatchUp();
            }
            catch
            {
                Exit(true);
                throw;
            }
        }

    }

}
