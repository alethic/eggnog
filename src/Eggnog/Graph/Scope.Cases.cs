using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// Gets whether the scope is a case: made by a split step (<see cref="Schedule.Split(SplitRule, Schedule)"/>)
        /// or by a <see cref="CaseSet"/>. A case stops on contradiction: once its contradiction holds it has no model,
        /// and every read and write that needs one throws <see cref="ScopeContradictedException"/>.
        /// </summary>
        public bool IsCase { get; }

        /// <summary>
        /// Opens host-driven cases under this scope: add cases with <see cref="CaseSet.Add"/>, make each assume its
        /// part and run, then <see cref="CaseSet.Lift"/> what every live case proves into this scope.
        /// </summary>
        /// <remarks>
        /// Coverage is the caller's obligation: the cases added must cover this scope (every model of it satisfies some
        /// case's assumptions), or the lift is unsound. The cases are children of this scope, share its thread domain, and
        /// defer its release until the set is disposed.
        /// </remarks>
        /// <returns>The set of cases, which the caller disposes.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (open the cases in a child of it), faulted, has a
        /// <see cref="Write"/> batch open, or is in use on another thread; or the call comes from a run observer (a
        /// structural call) or from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public CaseSet OpenCases()
        {
            EngineCallMarker.ThrowIfActive();
            Enter();
            try
            {
                if (Guard.InObserver)
                    throw new InvalidOperationException("Not allowed inside a run observer");

                // Brought up to date first: a case its parent's contradiction reached since its last call is stopped.
                CatchUpAndThrowIfStopped();
                if (IsFrozen)
                    throw new InvalidOperationException($"Scope '{Path}' is frozen; open cases in a child of it");

                return new CaseSet(this, new Relations.CaseSet(Engine.Context));
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Wraps a case context an engine set (a split's <c>CaseSet</c>, or a host-driven <see cref="CaseSet"/>) just
        /// created under this scope's context: a case scope listed in <see cref="Children"/>, with this
        /// scope's guard (a case's parent is writable), an origin under this scope's, and the context's runtime owned
        /// by it. The engine set owns the context: <see cref="Dispose"/> of the case marks it disposed, and the release
        /// waits for the set (<see cref="ReleaseCase"/>). Called on the domain's thread.
        /// </summary>
        /// <param name="engine">The case's engine side.</param>
        /// <param name="tag">The case's tag; <c>null</c> for none.</param>
        /// <returns>The case scope.</returns>
        /// <exception cref="ObjectDisposedException">This scope is disposed.</exception>
        internal Scope AdoptCase(ContextEngineScope engine, ScopeTag? tag)
        {
            lock (childrenLock)
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(Path, $"Scope '{Path}' is disposed.");

                var path = Path + "/" + (tag?.Name ?? "case") + "#" + Graph.NextScopeSerial().ToString(CultureInfo.InvariantCulture);
                var child = new Scope(this, tag, engine, Guard, path, isCase: true);
                children.Add(child);
                return child;
            }
        }

        /// <summary>
        /// Ends the engine set's ownership of this case and releases it as <see cref="Dispose"/> would: the case is
        /// marked disposed, its class values die, and once nothing defers it its local rules are freed, its context
        /// disposed (a no-op when the engine set disposed it already) and it leaves its parent's children. Never throws;
        /// idempotent.
        /// </summary>
        internal void ReleaseCase()
        {
            ownedByCases = false;
            if (IsRoot)
                return;

            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Origin.Disposed = true;

            TryRelease();
        }

        /// <summary>
        /// Releases, deepest first, every case under this scope whose context its engine set disposed: a split's
        /// cases once the run that made them disposed its records, and the vacuous cases a step dropped meanwhile. Other
        /// children, and cases whose context lives (a host-driven set's), are left alone. Called on the domain's thread
        /// when a run ends. O(cases under this scope).
        /// </summary>
        internal void ReleaseDisposedCases()
        {
            foreach (var child in Snapshot())
            {
                if (child.IsCase == false)
                    continue;

                child.ReleaseDisposedCases();
                if (child.ownedByCases && child.Engine.IsDisposed)
                    child.ReleaseCase();
            }
        }

        /// <summary>
        /// Marks this vacuous case disposed before its engine set disposes its context, and disposes its descendants,
        /// deepest first: a contradicted case has no model, nor has any scope under it, and the engine disposes a context
        /// only once it has no live children. A descendant whose release a call in progress defers keeps the case's context
        /// alive; the set then disposes it later. Never throws.
        /// </summary>
        internal void DisposeVacuous()
        {
            // A descendant's own case set, if any, loses its cases with them: their parent has no model.
            foreach (var child in Snapshot())
            {
                child.DisposeVacuous();
                child.ReleaseCase();
            }

            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Origin.Disposed = true;
        }

        /// <summary>
        /// Takes the first failure recorded, and not taken since, in a case of this scope that <paramref name="runCases"/>
        /// holds, in creation order, a case its engine set disposed as vacuous included while its release waits: a lattice
        /// merge that threw while the engine caught the case up inside a split step (the lift's catch-up of each case, or
        /// the vacuity check after it), which the engine records and does not throw. That case is faulted with
        /// it; the caller throws it and the run faults its scope. Called on the domain's thread when a split step
        /// ends. Cost: one volatile read per case of this scope, under the children's lock; no allocation unless a failure
        /// was recorded.
        /// </summary>
        /// <param name="runCases">The cases of the run.</param>
        /// <returns>The exception, to throw; <c>null</c> when no case of the run recorded a failure.</returns>
        internal RuleEvaluationException? TakeCaseCatchUpFailure(IReadOnlySet<Scope> runCases)
        {
            Scope? failed = null;
            var message = string.Empty;
            lock (childrenLock)
            {
                foreach (var child in children)
                {
                    if (child.IsCase == false || runCases.Contains(child) == false)
                        continue;

                    if (child.Engine.TryTakeRecordedFailure(out message))
                    {
                        failed = child;
                        break;
                    }
                }
            }

            return failed?.FaultWith(new Relations.PanicException(message));
        }

        /// <summary>
        /// Gets whether this scope still has a child whose release has not happened (a live child, or a disposed one whose
        /// release waits).
        /// </summary>
        internal bool HasChildren
        {
            get
            {
                lock (childrenLock)
                    return children.Count > 0;
            }
        }

        /// <summary>
        /// Gets a snapshot of the children, released or not.
        /// </summary>
        List<Scope> Snapshot()
        {
            lock (childrenLock)
                return new List<Scope>(children);
        }

    }

}
