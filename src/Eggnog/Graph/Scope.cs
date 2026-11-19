using System;
using System.Buffers;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// One context of the graph: where facts are written, rules run, reads answered and terms extracted. The graph's
    /// <see cref="EGraph.Root"/> is one; <see cref="CreateChild"/> makes children, which start as copies of their
    /// parent and cost in proportion to what they touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not thread-safe: scopes that share a thread domain (a writable scope and everything created under it) are
    /// used from one thread at a time, and a second thread gets <see cref="InvalidOperationException"/> instead of
    /// interleaving. Children of a frozen scope each have their own thread domain, and reads, queries and extractors of a
    /// frozen scope are thread-safe.
    /// </para>
    /// <para>
    /// A <see cref="RuleEvaluationException"/> from a run or a write leaves the scope usable, as egglog's failed run or
    /// command leaves its e-graph: what the run's matches or the write's calls staged before they failed stands, merged
    /// and rebuilt. A failure with no egglog counterpart faults the scope, and every later call except
    /// <see cref="Dispose"/> throws; a faulted root makes its graph unusable. So it is after a lattice merge that
    /// throws in the catch-up a call makes first, a read's, query's or extraction's included, and after a scheduler
    /// that throws. <see cref="Dispose"/> never throws: it makes the scope unusable at once, and releases its engine
    /// state once nothing defers it.
    /// </para>
    /// <para>
    /// A child starts as a copy of its parent and keeps seeing what its writable ancestors write later. A call into a
    /// scope first catches it up: it brings the rows the scope holds itself (its own rows, as against those it reads
    /// from its ancestors) in line with what a writable ancestor wrote since. The catch-up merges into the scope, so a
    /// lattice merge can run in it, and an exception it throws faults the scope and the ancestor: this is the
    /// "catch-up the call makes first" of the members' exception clauses. <see cref="DefineRule"/>,
    /// <see cref="DefineBiRewrite{TSort}"/> and the queries of an <see cref="Extractor"/> make it only while the
    /// scope's parent is writable: a child of a frozen scope that still owes the catch-up for what its ancestors wrote
    /// before they froze makes it at its next read, write, run, <see cref="CreateChild"/> or <see cref="Freeze"/>.
    /// </para>
    /// <para>
    /// Every member that enters the scope (its reads, writes, runs, extraction and tree operations) throws
    /// <see cref="ObjectDisposedException"/> once the scope is disposed, and <see cref="InvalidOperationException"/>
    /// once it is faulted, with the cause as the inner exception (<see cref="Failure"/>).
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
            Origin = graph.RootOrigin;
            guard = new ScopeGuard();
            var engine = new ContextEngineScope(graph, graph.Engine.Root);
            Engine = engine;
            ContainerView = Relations.ContainerView.Of(engine.Context.Context.Database);
            Runtime = engine.Runtime;
            Runtime.Owner = this;
        }

        /// <summary>
        /// Initializes a child scope over an engine child.
        /// </summary>
        /// <param name="parent">The parent scope.</param>
        /// <param name="engine">The engine child.</param>
        /// <param name="guard">The parent's guard when the parent is writable, a new one when it is frozen.</param>
        /// <param name="path">The child's path.</param>
        internal Scope(Scope parent, ContextEngineScope engine, ScopeGuard guard, string path)
        {
            Graph = parent.Graph;
            Parent = parent;
            Path = path;
            Origin = new ScopeOrigin(Graph, parent.Origin, path, false);
            this.guard = guard;
            Engine = engine;
            ContainerView = Relations.ContainerView.Of(engine.Context.Context.Database);
            Runtime = engine.Runtime;
            Runtime.Owner = this;

            // A child starts from its parent's schedulers as they are now, as egglog's push clones them.
            schedulerSnapshot = parent.CaptureSchedulerStates();
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
        /// Gets whether the scope is faulted: set after a <see cref="RuleEvaluationException"/> with no egglog
        /// counterpart (a lattice merge that threw in the catch-up of any call into this scope or a descendant, or a
        /// scheduler that threw), or after the engine faulted the scope's context because an exception escaped one of its
        /// writes, runs or rebuilds (an ancestor row the scope stopped seeing before its rewritten copy was merged would
        /// stay lost, so the scope cannot be repaired).
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
        /// Gets whether <see cref="Dispose"/> has been called, even while the release waits for live children or a
        /// call in progress. Never true for the root.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        /// <summary>
        /// Gets the scope's change stamp. It moves on every effective change that a write, run or freeze of this scope
        /// makes to its own rows, including the rewritten copies of ancestor rows that a merge of one of this scope's
        /// classes makes in that call (the copies replace those rows in this scope's view). Queries, reads and writes
        /// that change nothing never move it. The catch-up a call into this scope or a descendant makes first is left
        /// out, because it is no change of this scope's own (see the remarks on <see cref="Scope"/>). A write into an
        /// ancestor therefore never moves it, whether or not this scope needs copies to follow that write.
        /// </summary>
        /// <remarks>
        /// Read from another thread while the scope's domain is in use, the answer may be torn by maintenance in
        /// progress; a frozen scope has none.
        /// </remarks>
        public long Version => Engine.Version - Volatile.Read(ref upkeepStamps);

        /// <summary>
        /// Gets the scope's path, for messages: <c>root</c>, <c>root/scope#2/scope#7</c>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Disposes the scope: never throws. It refuses new calls on the scope at once. The class values it returned stay
        /// usable in its calls already in progress and in its live descendants; outside its subtree they are refused as
        /// disposed. Its local rules and engine state are released once it has no live children (transient query
        /// children included) and no call is in progress in it or, on another thread, in its thread domain; whichever thread
        /// removes the last obstacle releases it. A second call does nothing; on the root it does nothing, because the root
        /// lives as long as the graph.
        /// </summary>
        /// <remarks>
        /// What the scope drew from the graph stays with the graph: the values it interned (its constants, and the
        /// results of primitives and lattice merges its runs computed) stay in the graph's pools, and the ids and
        /// epochs it used are never handed out again (<see cref="EGraph.Usage"/>). Its own rows, local rules and engine
        /// state are what the release frees.
        /// </remarks>
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
        /// Gets the containers the scope sees: its context's container view, made once with the scope, through which the
        /// scope's reads, writes and primitives decode, compare and look up containers of classes. The root's view at the
        /// root, which costs one null test over the root's environments.
        /// </summary>
        internal Relations.ContainerView ContainerView { get; }

        /// <summary>
        /// Gets the container view of <paramref name="scope"/>, or the root's for none: what a container type reads its
        /// cells through when a call that may carry no scope serves one, such as a decode.
        /// </summary>
        /// <param name="scope">The scope, or <c>null</c>.</param>
        /// <returns>The view.</returns>
        internal static Relations.ContainerView ViewOf(Scope? scope) => scope is null ? default : scope.ContainerView;

        /// <summary>
        /// Gets the buffers external functions write during this scope's engine calls: the scope's context's value of the
        /// graph's runtime slot.
        /// </summary>
        internal ScopeRuntime Runtime { get; }

        /// <summary>
        /// Gets the guard of the scope's thread domain. Reassigned only when the scope's nearest writable ancestor
        /// freezes, on that domain's thread, while it holds the guard and no other call of the domain is in progress (see
        /// <see cref="Freeze"/>); so the guard of a scope a call has entered never changes until the call leaves it. Enter it
        /// through <see cref="EnterGuard"/>, which re-reads it once entered.
        /// </summary>
        internal ScopeGuard Guard => guard;

        /// <summary>
        /// Gets the origin of the class values the scope returns.
        /// </summary>
        internal ScopeOrigin Origin { get; }

        /// <summary>
        /// Faults the scope; the first cause is kept.
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
        /// Brings a scope whose parent is writable up to date, for the entry points that enter without a catch-up
        /// (<see cref="DefineRule"/>, <see cref="DefineBiRewrite{TSort}"/>, <see cref="Freeze"/>, and the queries of an
        /// <see cref="Extractor"/> of a writable scope), so that they report a failure of the catch-up as a read does.
        /// The root, and a scope whose parent is frozen, are not caught up here: <see cref="DefineRule"/>,
        /// <see cref="DefineBiRewrite{TSort}"/> and an extractor's queries leave the catch-up a child of a frozen scope
        /// owes for what its ancestors wrote before they froze, and its failure, to the child's next read, write, run,
        /// <see cref="CreateChild"/> or <see cref="Freeze"/>. Called on the domain's thread, with its guard held.
        /// </summary>
        /// <exception cref="RuleEvaluationException">A lattice merge the catch-up ran threw, in this scope or a writable
        /// ancestor, or a failure the catch-up's flush recorded; the scope is faulted.</exception>
        internal void CatchUpUnderWritableParent()
        {
            if (Parent is not { IsFrozen: false })
                return;

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
        /// Brings the scope up to date at the entry of a call, leaving the catch-up out of <see cref="Version"/>. Each
        /// writable ancestor is brought up to date first, outermost first, and its catch-up is left out of its own
        /// <see cref="Version"/>; then this scope's staged writes are flushed (there are none between calls: every
        /// batch flushes before it returns) and its layer is rebuilt against its ancestors. The root is only made
        /// clean, and nothing is left out of its <see cref="Version"/>. Called on the domain's thread, with its guard
        /// held. Cost: two reads of the change stamp and one volatile read of the recorded failure per writable scope
        /// from here up to the first frozen one or the root, beyond the engine's own catch-up, which this call makes in
        /// the order the engine would.
        /// </summary>
        /// <exception cref="Relations.PanicException">The flush of host writes recorded a failure.</exception>
        /// <exception cref="RuleEvaluationException">A merge of a scope's catch-up recorded a failure: a lattice delegate that
        /// threw while the rebuild merged an ancestor's rows into that scope's layer, which the engine records and does not
        /// throw. The scope whose catch-up failed and this scope are faulted with it; the walk stops there.</exception>
        internal void CatchUp() => CatchUpCore(this);

        /// <summary>
        /// Brings the scope up to date as <see cref="CatchUp"/> describes.
        /// </summary>
        /// <param name="called">The scope the call was made on: this scope or a descendant in its thread domain.</param>
        /// <exception cref="RuleEvaluationException">A merge of this scope's catch-up, or of an ancestor's, recorded a
        /// failure; the scope where it happened and <paramref name="called"/> are faulted.</exception>
        void CatchUpCore(Scope called)
        {
            // The writable scopes from here up, caught up root first in a loop rather than by a recursion through the
            // parent, so a chain of any depth needs no more stack than one level.
            var count = 1;
            for (var up = Parent; up is { IsFrozen: false }; up = up.Parent)
                count++;

            if (count == 1)
            {
                CatchUpLevel(called);
                return;
            }

            var chain = ArrayPool<Scope>.Shared.Rent(count);
            try
            {
                var at = this;
                for (var i = count - 1; i >= 0; i--, at = at.Parent!)
                    chain[i] = at;

                for (var i = 0; i < count; i++)
                    chain[i].CatchUpLevel(called);
            }
            finally
            {
                Array.Clear(chain, 0, count);
                ArrayPool<Scope>.Shared.Return(chain);
            }
        }

        /// <summary>
        /// Brings this scope alone up to date, its writable ancestors being up to date: one step of
        /// <see cref="CatchUpCore"/>'s walk.
        /// </summary>
        /// <param name="called">The scope the call was made on.</param>
        /// <exception cref="RuleEvaluationException">A merge of this scope's catch-up recorded a failure; the scope and
        /// <paramref name="called"/> are faulted.</exception>
        void CatchUpLevel(Scope called)
        {
            // The root is behind its own writes only after a rebuild that ran out of epochs before it finished: every
            // other flush there rebuilds to the end and takes what its merges recorded. Its catch-up is then that
            // rebuild, which throws OverflowException, since the epochs never come back: it records nothing and is not
            // checked.
            if (IsRoot)
            {
                Engine.EnsureClean();
                return;
            }

            // The walk has just brought every writable ancestor up to date, root first, so only this level is left.
            var before = Engine.Version;
            Engine.EnsureCleanBelowCurrentAncestors();
            var moved = Engine.Version - before;
            if (moved != 0)
                Interlocked.Add(ref upkeepStamps, moved);

            // The rebuild records a lattice merge that throws and goes on: the layer kept the old value, so the
            // failure is taken here, at the call that ran the merge, rather than left for the scope's next write.
            if (Engine.TryTakeRecordedFailure(out var message))
                throw FaultCatchUp(message, called);
        }

        /// <summary>
        /// Turns a failure this scope's catch-up recorded into a <see cref="RuleEvaluationException"/>, with the runtime's
        /// record of it when there is one, and faults this scope and <paramref name="called"/> with it.
        /// </summary>
        /// <param name="message">The engine's failure message.</param>
        /// <param name="called">The scope the call was made on.</param>
        /// <returns>The exception, to throw.</returns>
        RuleEvaluationException FaultCatchUp(string message, Scope called)
        {
            var exception = FaultWith(new Relations.PanicException(message));
            if (ReferenceEquals(called, this) == false)
                called.Fault(exception);

            return exception;
        }

        /// <summary>
        /// Gets whether a scope under this one has a <see cref="Write"/> batch open, whose plan a write here would change.
        /// </summary>
        internal bool HasBatchBelow => batchesBelow > 0;

        /// <summary>
        /// Counts a <see cref="Write"/> batch opened (<paramref name="delta"/> 1) or ended (-1) on this scope in each of its
        /// writable ancestors, which share its thread domain and refuse writes and runs while their count is positive.
        /// Called on the domain's thread, with its guard held. Cost: one increment per writable ancestor; none at the
        /// root.
        /// </summary>
        /// <param name="delta">1 when the batch opens, -1 when it ends.</param>
        void CountOpenBatch(int delta)
        {
            for (var up = Parent; up is { IsFrozen: false }; up = up.Parent)
                up.batchesBelow += delta;
        }

        /// <summary>
        /// Enters the guard of the scope's thread domain and returns it. The guard is read again once entered: a freeze
        /// of the nearest writable ancestor may have given this scope a new one between the read and the entry (see
        /// <see cref="Freeze"/>), and the entry then moves to the new guard, so that every call holds its scope's
        /// current guard. One compare-and-swap, and one read of the field.
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
        /// Counts a call in progress on the scope: a disposed scope is not released while one runs. One interlocked
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
        /// Enters the scope for a read: a frozen scope takes no guard, since its reads are thread-safe; a writable
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
        /// Enters the scope for one query of an extractor: a frozen scope takes no guard, so any number of extractors
        /// may run on it from any threads; a writable one takes its guard. The caller ends it with
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
        /// it may have a <see cref="Write"/> batch open. The caller ends it with <see cref="Exit"/>(<c>true</c>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope may not be written or run now.</exception>
        internal void EnterWrite()
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

                // An open batch's plan was checked against this scope as it stands: a write or a run here would
                // change what the batch read, so it waits until the batch ends. One read of a field.
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
