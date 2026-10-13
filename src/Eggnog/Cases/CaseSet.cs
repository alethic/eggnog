using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Host-driven cases under one parent, made by <see cref="Scope.OpenCases"/>: add cases, make each assume its part and
    /// run, then <see cref="Lift"/> into the parent what every live case proves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coverage is the caller's obligation: the cases must cover the parent (every model of the parent satisfies some
    /// case's assumptions), or a lift is unsound. A case stops on contradiction; a lift leaves such a vacuous case out,
    /// disposes it, and when every case is vacuous declares the parent contradictory.
    /// </para>
    /// <para>
    /// The set owns its cases: disposing a case directly marks it disposed, and its release waits for the set, whose
    /// <see cref="Lift"/> then refuses to lift over cases that no longer cover the parent. The cases are children of the
    /// parent and share its thread domain; an open set defers the parent's release. <see cref="Add"/> and
    /// <see cref="Lift"/> enter the parent's thread domain, as its own calls do; <see cref="Dispose"/> may be called from
    /// any thread and never throws. The list of cases is guarded by a private lock, held only to read or change it.
    /// </para>
    /// </remarks>
    public sealed class CaseSet : IDisposable
    {

        readonly Relations.CaseSet engine;
        readonly object sync = new();
        readonly List<Scope> cases = new();
        int disposed;

        /// <summary>
        /// Initializes a set over the engine's cases of <paramref name="parent"/>.
        /// </summary>
        /// <param name="parent">The parent scope; writable.</param>
        /// <param name="engine">The engine's set over the parent's context.</param>
        internal CaseSet(Scope parent, Relations.CaseSet engine)
        {
            Parent = parent;
            this.engine = engine;
        }

        /// <summary>
        /// Gets the parent: the scope the cases cover and a lift writes into.
        /// </summary>
        public Scope Parent { get; }

        /// <summary>
        /// Gets the live cases, in creation order (a snapshot). A case a lift found contradictory is disposed and leaves the
        /// list; so does a case disposed directly.
        /// </summary>
        public IReadOnlyList<Scope> Cases
        {
            get
            {
                var live = new List<Scope>();
                foreach (var @case in Snapshot())
                    if (@case.IsDisposed == false)
                        live.Add(@case);

                return live.AsReadOnly();
            }
        }

        /// <summary>
        /// Adds a case: a new child of the parent that stops on contradiction (<see cref="Scope.IsCase"/>), starting as a
        /// copy of the parent. Make it assume its part (<see cref="Scope.Assume"/>) and run it.
        /// </summary>
        /// <param name="tag">The case's tag; <c>null</c> for none.</param>
        /// <returns>The case.</returns>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or it is the root's.</exception>
        /// <exception cref="ObjectDisposedException">The set or the parent is disposed.</exception>
        /// <exception cref="ScopeContradictedException">The parent is a case whose contradiction stopped it, or lies under one.</exception>
        /// <exception cref="InvalidOperationException">The parent is frozen, faulted, at depth 31 (the deepest a scope
        /// tree goes), has a <c>Write</c> batch open, or is in use on another thread; or the call comes from a run
        /// observer, or from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A failure the parent recorded surfaced when the case was created, or a
        /// lattice merge threw in the catch-up the call makes first, in the parent or a writable ancestor of it; the parent
        /// is faulted, and so is that ancestor.</exception>
        public Scope Add(ScopeTag? tag = null)
        {
            var graph = Parent.Graph;
            if (tag is not null)
            {
                graph.CheckTag(tag, nameof(tag));
                if (ReferenceEquals(tag, graph.RootTag))
                    throw new ArgumentException("The root tag is the root's; tag a case with a tag of its own.", nameof(tag));
            }

            // A structural call, as CreateChild: allowed while a batch below the parent is open, since adding a case changes
            // nothing the batch read.
            EngineCallMarker.ThrowIfActive();
            Parent.Enter();
            try
            {
                if (Parent.Guard.InObserver)
                    throw new InvalidOperationException("Not allowed inside a run observer");

                ThrowIfDisposed();
                if (Parent.IsFrozen)
                    throw new InvalidOperationException($"Scope '{Parent.Path}' is frozen; open cases in a child of it");

                if (Parent.Depth() >= Scope.MaxDepth)
                    throw new InvalidOperationException($"Scope '{Parent.Path}' is at depth {Scope.MaxDepth}, the deepest a scope tree goes.");

                // Brought up to date before the check: a parent case its own parent's contradiction reached since its last
                // call stops at this catch-up, and the engine would refuse to add a case under it.
                Parent.CatchUpAndThrowIfStopped();
                Parent.Runtime.TakeFailure();
                Relations.EGraphContext context;
                try
                {
                    context = engine.AddCase(new Relations.ContextOptions
                    {
                        IsCase = true,
                        StopOnContradiction = true,
                        InheritWatermarks = graph.InheritWatermarks,
                    });
                }
                catch (Relations.PanicException panic)
                {
                    throw Parent.FaultWith(panic);
                }

                var @case = Parent.AdoptCase(new ContextEngineScope(graph, context), tag);

                // The disposed flag and the list change together under the lock (Dispose sets one and drains the other in
                // it): a set disposed on another thread since the check above never keeps a case that outlives it.
                bool orphaned;
                lock (sync)
                {
                    orphaned = disposed != 0;
                    if (orphaned == false)
                        cases.Add(@case);
                }

                if (orphaned)
                {
                    @case.ReleaseCase();
                    ThrowIfDisposed();
                }

                return @case;
            }
            finally
            {
                Parent.Exit(true);
            }
        }

        /// <summary>
        /// Lifts into the parent what every live case proves: the equalities between the parent's classes, and the rows of
        /// every table that lifts (<see cref="TableHandle.Lift"/>; relations, constructors, and functions with their
        /// lattice's case join or, without one, the value every live case agrees on). Then the cases found contradictory are
        /// disposed and leave <see cref="Cases"/>; when every case is vacuous, the parent's
        /// <see cref="EGraph.Contradiction"/> is written. May be called repeatedly: a lift over cases and a parent that have
        /// not changed since the last one costs one check (the set remembers what its last full lift read).
        /// </summary>
        /// <remarks>
        /// The lift catches every case up with the parent (see the remarks on <see cref="Scope"/>) before it reads the
        /// case. A lattice merge that throws in that catch-up is recorded in the case, which keeps its old value; the
        /// lift reads that value, which is sound (it carries less information), and the found-vacuous check after it
        /// takes the record and faults the case, not the parent.
        /// </remarks>
        /// <param name="options">The limits; <c>null</c> for <see cref="LiftOptions.Default"/>.</param>
        /// <param name="cancellationToken">Checked before every round of the lift.</param>
        /// <returns>What the lift did.</returns>
        /// <exception cref="ObjectDisposedException">The set or the parent is disposed.</exception>
        /// <exception cref="ScopeContradictedException">The parent is a case whose contradiction stopped it, or lies under one.</exception>
        /// <exception cref="InvalidOperationException">The set has no case and none was found vacuous ("A lift needs at least
        /// one case"; with none, the parent would be declared contradictory); a case was disposed directly, so the rest
        /// no longer cover the parent; the parent is frozen, faulted, has a <c>Write</c> batch open in it or below it, or is
        /// in use on another thread; or the call comes from a run observer, or from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled between rounds.</exception>
        /// <exception cref="RuleEvaluationException">A merge of the lifted writes failed (the parent is faulted); a case join
        /// threw (nothing of that round is written; the parent is not faulted); or a lattice merge threw in the catch-up the
        /// call makes first, in the parent or a writable ancestor of it: the parent is faulted, and so is the
        /// ancestor.</exception>
        public LiftReport Lift(LiftOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= LiftOptions.Default;
            Parent.EnterWrite();
            try
            {
                ThrowIfDisposed();
                Parent.ThrowIfStopped();
                if (engine.Cases.Count == 0 && engine.VacuousCount == 0)
                    throw new InvalidOperationException("A lift needs at least one case; with none, the scope would be declared contradictory. Assume Contradiction explicitly if that is meant.");

                foreach (var @case in Snapshot())
                {
                    // A case disposed directly, not found vacuous: the remaining cases do not cover the parent.
                    if (@case.IsDisposed && @case.Engine.IsDisposed == false && IsVacuous(@case) == false)
                        throw new InvalidOperationException($"Case '{@case.Path}' of the set was disposed directly; without it the remaining cases do not cover '{Parent.Path}', so the set can only be disposed.");
                }

                var before = Parent.Version;
                Parent.Runtime.TakeFailure();
                Relations.LiftReport lifted;
                try
                {
                    lifted = engine.Lift(LiftSpecBuilder.Build(Parent.Graph), options.Engine, cancellationToken);
                }
                catch (Relations.PanicException panic)
                {
                    throw Parent.FaultWith(panic);
                }

                DisposeVacuousCases();
                return new LiftReport(lifted, Parent.Version != before);
            }
            finally
            {
                Parent.Exit(true);
            }
        }

        /// <summary>
        /// Disposes every case: never throws. Each case is released once nothing defers it (a child of it, a call in
        /// progress); the parent's release, which the open set deferred, can then proceed. A second call does nothing. A case
        /// that an <see cref="Add"/> in progress on another thread makes meanwhile is released by that call, which then
        /// throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            // The flag is set and the list drained under one lock, so an Add in progress either lists its case before the
            // drain or sees the flag and releases the case itself.
            Scope[] owned;
            lock (sync)
            {
                if (disposed != 0)
                    return;

                Volatile.Write(ref disposed, 1);
                owned = cases.ToArray();
                cases.Clear();
            }

            for (var i = owned.Length - 1; i >= 0; i--)
                owned[i].ReleaseCase();
        }

        /// <summary>
        /// Disposes the cases a lift found contradictory: their descendants first, then the engine set disposes their
        /// contexts, then their scopes are released and leave the list. A vacuous case whose descendant a call in progress
        /// keeps alive (a callback on this thread) is disposed as a scope but stays with the engine set until a later lift or
        /// the set's disposal. O(cases).
        /// </summary>
        void DisposeVacuousCases()
        {
            var any = false;
            var blocked = false;
            var snapshot = Snapshot();
            foreach (var @case in snapshot)
            {
                if (@case.Engine.IsDisposed || IsVacuous(@case) == false)
                    continue;

                @case.DisposeVacuous();
                any = true;
                blocked |= @case.HasChildren;
            }

            if (any == false)
                return;

            if (blocked == false)
                engine.DisposeVacuous();

            foreach (var @case in snapshot)
            {
                if (@case.Engine.IsDisposed == false)
                    continue;

                lock (sync)
                    cases.Remove(@case);

                @case.ReleaseCase();
            }
        }

        /// <summary>
        /// Gets a snapshot of the set's cases, disposed ones included, under the set's lock.
        /// </summary>
        Scope[] Snapshot()
        {
            lock (sync)
                return cases.ToArray();
        }

        /// <summary>
        /// Gets whether a case is vacuous: stopped by its contradiction, or contradictory once brought up to date (left out
        /// of its <see cref="Scope.Version"/>). A lattice merge that threw in the case's catch-up, the one the engine's lift
        /// made before reading the case (which records it and goes on) or this one, faults the case here and is not
        /// rethrown: the case is caught up by then, the failure is the case's, not the lift's, and a lift goes on over a
        /// faulted case as over any other.
        /// </summary>
        static bool IsVacuous(Scope @case)
        {
            if (@case.IsStopped)
                return true;

            try
            {
                @case.CatchUp();
            }
            catch (RuleEvaluationException)
            {
                // The case is faulted with it; its next call reports it.
            }

            return @case.IsStopped || @case.Engine.IsContradictory;
        }

        /// <summary>
        /// Throws when the set is disposed.
        /// </summary>
        void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(CaseSet), $"The cases of '{Parent.Path}' are disposed.");
        }

    }

}
