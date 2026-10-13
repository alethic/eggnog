using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The cases of one split of a parent context: children created as cases, run, lifted into the parent, and disposed
    /// together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lifecycle: <see cref="AddCase"/> creates a case (a child that stops on a contradiction); the caller makes it
    /// assume its part; <see cref="RunEach"/> runs a schedule in every case, disposing the cases found vacuous first;
    /// <see cref="Lift"/> writes into the parent what holds in every live case; <see cref="DisposeVacuous"/> drops the
    /// cases that are contradictory; <see cref="Dispose"/> disposes every case in reverse creation order, each case's
    /// own split records first (depth-first).
    /// </para>
    /// <para>
    /// Coverage is the caller's obligation: the cases must cover the parent, or the lift is unsound. Lifts are memoized
    /// (<see cref="LiftSignature"/>): a lift of the same spec over cases and a parent that have not changed since the last
    /// settled one returns at once.
    /// </para>
    /// <para>
    /// The set owns its cases: only the set disposes them. A case disposed outside the set was not found vacuous, so
    /// dropping it would lift what holds in the remaining cases only, which do not cover the parent; every operation but
    /// <see cref="Dispose"/> refuses such a set (<see cref="InvalidOperationException"/>).
    /// </para>
    /// <para>
    /// Threading: the parent is not frozen, so its cases share its thread domain; a case set is used from the thread that
    /// holds it, and is not thread-safe itself.
    /// </para>
    /// </remarks>
    internal sealed class CaseSet : IDisposable
    {

        readonly EGraphContext parent;
        readonly List<EGraphContext> cases = new();
        Lifter? lifter;
        LiftSignature? memo;
        int vacuous;
        bool hasKeptOutput;
        uint keptOutput;
        bool disposed;

        /// <summary>
        /// Initializes an empty set of cases of <paramref name="parent"/>.
        /// </summary>
        /// <param name="parent">The parent; not frozen (a lift writes into it) and not disposed.</param>
        /// <exception cref="ObjectDisposedException">The parent is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is frozen.</exception>
        public CaseSet(EGraphContext parent)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (parent.IsDisposed)
                throw new ObjectDisposedException(nameof(EGraphContext), "The parent of a case set is disposed.");

            if (parent.IsFrozen)
                throw new InvalidOperationException("A frozen context cannot be lifted into; open the cases under a child of it.");

            this.parent = parent;
        }

        /// <summary>
        /// Gets the parent.
        /// </summary>
        public EGraphContext Parent => parent;

        /// <summary>
        /// Gets the cases, in creation order, without those the set disposed as vacuous.
        /// </summary>
        public IReadOnlyList<EGraphContext> Cases => cases;

        /// <summary>
        /// Gets the number of cases this set disposed as vacuous (<see cref="RunEach"/>, <see cref="DisposeVacuous"/>).
        /// </summary>
        public int VacuousCount => vacuous;

        /// <summary>
        /// Gets whether the set is disposed.
        /// </summary>
        public bool IsDisposed => disposed;

        /// <summary>
        /// Creates a case: a child of the parent.
        /// </summary>
        /// <param name="options">The case's options; they must make a case that stops on a contradiction.
        /// <see cref="ContextOptions.Case"/> if <c>null</c>.</param>
        /// <returns>The case.</returns>
        /// <exception cref="ArgumentException">The options do not make a case that stops on a contradiction.</exception>
        /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent cannot create a child now (see
        /// <see cref="EGraphContext.CreateChild"/>).</exception>
        public EGraphContext AddCase(ContextOptions? options = null)
        {
            ThrowIfDisposed();
            options ??= ContextOptions.Case;
            CheckCaseOptions(options, nameof(options));
            var @case = parent.CreateChild(options);
            cases.Add(@case);
            return @case;
        }

        /// <summary>
        /// Runs <paramref name="schedule"/> in every case: each case is brought up to date first, and a contradictory
        /// case is disposed at once and counted as vacuous instead of run. A contradictory case holds vacuously, so
        /// dropping it does not change what holds in every case.
        /// </summary>
        /// <param name="schedule">The in-case work.</param>
        /// <param name="depth">The depth passed to the schedule.</param>
        /// <param name="allowSplits">Whether the schedule may take nested splits.</param>
        /// <returns>Whether a schedule reported a change.</returns>
        /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
        /// <exception cref="InvalidOperationException">A case was disposed outside the set.</exception>
        public bool RunEach(ICaseSchedule schedule, int depth, bool allowSplits)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            ThrowIfDisposed();
            ThrowIfCaseDisposedOutside();
            var changed = false;
            var snapshot = cases.ToArray();
            foreach (var @case in snapshot)
            {
                // A schedule may dispose a case it was not given; the set refuses that at its next operation.
                if (@case.IsDisposed)
                    continue;

                if (@case.IsStopped == false)
                    @case.EnsureClean();

                if (@case.IsContradictory)
                {
                    DisposeVacuousCase(@case);
                    vacuous++;
                    continue;
                }

                changed |= schedule.Run(@case, depth, allowSplits);
            }

            return changed;
        }

        /// <summary>
        /// Lifts into the parent what holds in every live case (<see cref="Lifter"/>), checking
        /// <paramref name="cancellationToken"/> between rounds. When every case of the set was disposed as vacuous
        /// before the lift, the parent is contradictory, since its cases cover it and none is consistent, and its
        /// contradiction is written, with the base output a vacuous case held when the set disposed it if the
        /// contradiction function needs one.
        /// </summary>
        /// <param name="spec">The tables to lift.</param>
        /// <param name="options">The limits.</param>
        /// <param name="cancellationToken">Checked before every round.</param>
        /// <returns>What the lift did.</returns>
        /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
        /// <exception cref="InvalidOperationException">The set has no case and none was found vacuous; a case was disposed
        /// outside the set; or the lift is refused (<see cref="Lifter.Lift"/>).</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled between rounds.</exception>
        /// <exception cref="PanicException">Flushing the lifted writes recorded a failure.</exception>
        public LiftReport Lift(LiftSpec spec, LiftOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(options);
            ThrowIfDisposed();
            ThrowIfCaseDisposedOutside();
            if (cases.Count == 0)
            {
                if (vacuous == 0)
                    throw new InvalidOperationException("A lift needs at least one case; with none, the parent would be declared contradictory.");

                cancellationToken.ThrowIfCancellationRequested();
                parent.EnsureClean();
                var report = new LiftReport { VacuousCases = vacuous, ParentContradicted = true };
                report.Changed = Lifter.ContradictParent(parent, cases, hasKeptOutput, keptOutput);
                return report;
            }

            lifter ??= new Lifter();
            return lifter.Run(parent, cases, spec, options, ref memo, cancellationToken);
        }

        /// <summary>
        /// Disposes the cases that are contradictory, each case's split records first, and returns their number. Each
        /// case that is not stopped is brought up to date first. This serves lifts the host drives itself, which never
        /// call <see cref="RunEach"/>.
        /// </summary>
        /// <returns>The number of cases disposed.</returns>
        /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
        /// <exception cref="InvalidOperationException">A case was disposed outside the set.</exception>
        public int DisposeVacuous()
        {
            ThrowIfDisposed();
            ThrowIfCaseDisposedOutside();
            var disposedNow = 0;
            foreach (var @case in cases.ToArray())
            {
                if (@case.IsStopped == false)
                    @case.EnsureClean();

                if (@case.IsContradictory)
                {
                    DisposeVacuousCase(@case);
                    disposedNow++;
                }
            }

            vacuous += disposedNow;
            return disposedNow;
        }

        /// <summary>
        /// Disposes every case, in reverse creation order, each case's split records first. A second call does nothing.
        /// </summary>
        public void Dispose()
        {
            if (disposed)
                return;

            for (var i = cases.Count - 1; i >= 0; i--)
            {
                var @case = cases[i];
                SplitDriver.DisposeRecords(@case);
                @case.Dispose();
                cases.RemoveAt(i);
            }

            disposed = true;
            memo = null;
        }

        /// <summary>
        /// Throws <see cref="ArgumentException"/> unless <paramref name="options"/> make a case that stops on a
        /// contradiction, so that a vacuous case stops and can be dropped.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="parameter">The parameter's name for the exception.</param>
        internal static void CheckCaseOptions(ContextOptions options, string parameter)
        {
            if (options.IsCase == false || options.StopOnContradiction == false)
                throw new ArgumentException("Case options must set IsCase and StopOnContradiction, so a vacuous case stops and is dropped.", parameter);
        }

        /// <summary>
        /// Disposes one vacuous case as <see cref="DisposeCase"/> does, first keeping the base output of its
        /// contradiction row while the set has none (<see cref="Lifter.TryReadContradictionOutput"/>): once every case
        /// is disposed, <see cref="Lift"/> writes the parent's contradiction with it, as a lift over the undisposed
        /// cases would. O(1) beyond the disposal for a contradiction function with a constant default or an id output.
        /// </summary>
        void DisposeVacuousCase(EGraphContext @case)
        {
            if (hasKeptOutput == false)
                hasKeptOutput = Lifter.TryReadContradictionOutput(@case, out keptOutput);

            DisposeCase(@case);
        }

        /// <summary>
        /// Disposes one case, its split records first, and removes it from the set.
        /// </summary>
        void DisposeCase(EGraphContext @case)
        {
            SplitDriver.DisposeRecords(@case);
            @case.Dispose();
            cases.Remove(@case);
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when a case of the set was disposed by someone else: the set
        /// removes the cases it disposes itself, so any disposed case left in it was disposed outside. O(cases).
        /// </summary>
        void ThrowIfCaseDisposedOutside()
        {
            foreach (var @case in cases)
                if (@case.IsDisposed)
                    throw new InvalidOperationException("A case of the set was disposed outside it. Only vacuous cases may be dropped; without it the remaining cases do not cover the parent, so the set can only be disposed.");
        }

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> when the set is disposed.
        /// </summary>
        void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(CaseSet));
        }

    }

}
