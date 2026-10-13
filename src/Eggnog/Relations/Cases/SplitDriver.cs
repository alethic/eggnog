using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Drives case splits in a scope: finds the splits its sources ask for, opens the cases of the ones worth taking,
    /// runs the in-case schedule in them, and lifts what holds in every live case back into the scope, lifting before
    /// deepening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Step"/>:
    /// </para>
    /// <list type="number">
    /// <item>finds the requests of every source in the scope;</item>
    /// <item>skips a request covered by a live record in the scope or any ancestor, so a split an ancestor already
    /// opened is not opened again: a record of the same source object (by reference, so a record another driver opened
    /// from a source it shares with this one covers the request, and a record of an unrelated source at the same index
    /// in another driver's list does not) over the same cells; or by another request of the same step;</item>
    /// <item>skips a request over the limits (<see cref="SplitOptions.CaseLimit"/>, at least one case);</item>
    /// <item>skips a request the demand rejects, before any case exists;</item>
    /// <item>opens the cases: a <see cref="CaseSet"/> recorded on the scope, then one case per index, each assumed by its
    /// source;</item>
    /// <item><b>phase A</b>: for every live record this driver opened in the scope, in creation order (those its earlier
    /// steps opened and those this step opened; never another driver's, whose cases belong to that driver's in-case
    /// schedule and lift spec), runs the in-case schedule in each case with nested splits disabled, then lifts (lift
    /// before deepening, TheSy);</item>
    /// <item><b>phase B</b>: while <c>depth + 1 &lt; DepthLimit</c>, for every such record, runs the schedule again in each
    /// live case with nested splits enabled (which recurses into <see cref="Step"/> at <c>depth + 1</c>), and lifts again
    /// when that changed something.</item>
    /// </list>
    /// <para>
    /// Records live until <see cref="DisposeRecords"/>: later steps in the scope or its descendants skip their keys,
    /// and the cases take in the scope's new facts through their catch-up. A later step runs and lifts them again, so
    /// what the cases derive from the scope's new facts is lifted, and a case those facts make vacuous is dropped; the
    /// lift memo makes a record whose cases and scope did not change cost one signature check. A step reports a change
    /// only when it opened cases or a lift changed the scope, never for in-case work, so a schedule that repeats it
    /// until nothing changes ends. The driver never holds the scope's thread domain while it calls a source, the demand
    /// or the schedule, which may create children (nested splits). Exceptions from a source or the schedule propagate;
    /// the records opened so far stay for <see cref="DisposeRecords"/>. After every lift the cases found vacuous are
    /// disposed; a lift whose cases are all vacuous contradicts the scope, and the step ends there.
    /// </para>
    /// <para>
    /// One driver may serve any number of scopes, one step at a time per thread; its per-step lists are local. Several
    /// drivers may serve one scope (the rule layer builds one per split node): each runs and lifts only its own
    /// records, and they share coverage only through shared source objects.
    /// </para>
    /// </remarks>
    internal sealed class SplitDriver
    {

        readonly ISplitSource[] sources;
        readonly ICaseSchedule inCase;
        readonly ISplitDemand? demand;
        readonly LiftSpec spec;
        readonly SplitOptions options;

        /// <summary>
        /// Initializes a driver.
        /// </summary>
        /// <param name="sources">The split sources; a request's <see cref="SplitKey.Source"/> indexes this list.</param>
        /// <param name="inCase">The in-case schedule.</param>
        /// <param name="demand">Decides which splits are worth taking; <c>null</c> takes every one.</param>
        /// <param name="spec">The tables to lift.</param>
        /// <param name="options">The limits and case options.</param>
        /// <exception cref="ArgumentException">A source is null.</exception>
        public SplitDriver(IReadOnlyList<ISplitSource> sources, ICaseSchedule inCase, ISplitDemand? demand, LiftSpec spec, SplitOptions options)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(inCase);
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(options);
            this.sources = new ISplitSource[sources.Count];
            for (var i = 0; i < this.sources.Length; i++)
                this.sources[i] = sources[i] ?? throw new ArgumentException("A split source is null.", nameof(sources));

            CaseSet.CheckCaseOptions(options.CaseOptions, nameof(options));
            this.inCase = inCase;
            this.demand = demand;
            this.spec = spec;
            this.options = options;
        }

        /// <summary>
        /// Gets the driver's options.
        /// </summary>
        public SplitOptions Options => options;

        /// <summary>
        /// Runs one split step in <paramref name="scope"/> at split depth <paramref name="depth"/>.
        /// </summary>
        /// <param name="scope">The scope; writable.</param>
        /// <param name="depth">The scope's split depth: 0 at the top.</param>
        /// <param name="report">Accumulates what the step did.</param>
        /// <returns>Whether the step changed the scope: it opened cases, or a lift changed the scope.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen (a lift could not write into it) or faulted; or
        /// a request names a source the driver does not have.</exception>
        public bool Step(EGraphContext scope, int depth, SplitReport report)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(report);
            ArgumentOutOfRangeException.ThrowIfNegative(depth);
            if (scope.IsDisposed)
                throw new ObjectDisposedException(nameof(EGraphContext), "The scope of a split step is disposed.");

            if (scope.IsFrozen)
                throw new InvalidOperationException("A split step needs a writable scope to lift into; split in a child of a frozen context.");

            if (depth >= options.DepthLimit || scope.IsStopped)
                return false;

            scope.EnsureClean();
            if (scope.IsStopped || scope.IsContradictory)
                return false;

            // 1. Every source's requests, with the scope's domain free.
            var requests = new List<SplitRequest>();
            foreach (var source in sources)
                source.FindRequests(scope, requests, report);

            // 2-5. Filter, then open the cases of each request taken.
            var opened = 0;
            var taken = new HashSet<SplitKey>();
            foreach (var request in requests)
            {
                var key = request.Key;
                if ((uint)key.Source >= (uint)sources.Length)
                    throw new InvalidOperationException($"A split request names source {key.Source}; the driver has {sources.Length}.");

                if (taken.Contains(key) || IsCovered(scope, sources[key.Source], key.Values) || request.CaseCount < 1 || request.CaseCount > options.CaseLimit)
                {
                    report.SplitsSkipped++;
                    continue;
                }

                if (demand is not null && demand.Wants(scope, request) == false)
                {
                    report.SplitsSkipped++;
                    continue;
                }

                taken.Add(key);
                Open(scope, request, depth, report);
                opened++;
            }

            // Every live record this driver opened in the scope, in creation order: those earlier steps opened (their cases
            // have caught up with the scope's new facts) and those just opened. Another driver's records are its own: their
            // cases run that driver's in-case schedule and lift with its spec. Opening cases is a change of the scope's
            // state.
            var records = new List<SplitRecord>();
            scope.CopyLiveSplitRecords(records, this);
            var changed = opened > 0;
            if (records.Count == 0)
                return changed;

            // 6. Phase A: run each case without nested splits, then lift (lift before deepening).
            foreach (var record in records)
            {
                if (record.Cases.Cases.Count == 0)
                    continue;

                record.Cases.RunEach(inCase, depth, allowSplits: false);
                if (Lift(record, report, ref changed))
                    return changed;
            }

            // 7. Phase B: deepen into the live cases, and lift again when that changed something.
            if (depth + 1 >= options.DepthLimit)
                return changed;

            foreach (var record in records)
            {
                var deeper = false;
                foreach (var @case in new List<EGraphContext>(record.Cases.Cases))
                {
                    if (@case.IsDisposed || @case.IsStopped)
                        continue;

                    deeper |= inCase.Run(@case, depth + 1, allowSplits: true);
                }

                if (deeper && Lift(record, report, ref changed))
                    return changed;
            }

            return changed;
        }

        /// <summary>
        /// Disposes every split record of <paramref name="scope"/>: depth-first, in reverse creation order, each
        /// record's cases disposed by their <see cref="CaseSet"/> (each case's own records first). Nothing when the
        /// scope has none.
        /// </summary>
        /// <param name="scope">The scope.</param>
        public static void DisposeRecords(EGraphContext scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var records = scope.TakeSplitRecords();
            for (var i = records.Length - 1; i >= 0; i--)
                records[i].Cases.Dispose();
        }

        /// <summary>
        /// Gets whether a live record of <paramref name="scope"/> or of an ancestor, opened by any driver, splits
        /// <paramref name="source"/> over <paramref name="values"/>. O(depth x records x cells).
        /// </summary>
        static bool IsCovered(EGraphContext scope, ISplitSource source, ReadOnlySpan<uint> values)
        {
            for (var s = scope; s is not null; s = s.Parent)
                if (s.CoversSplit(source, values))
                    return true;

            return false;
        }

        /// <summary>
        /// Opens the cases of <paramref name="request"/> in <paramref name="scope"/>: records the split first, so a failure
        /// while opening leaves it for <see cref="DisposeRecords"/>, then creates and assumes each case.
        /// </summary>
        void Open(EGraphContext scope, in SplitRequest request, int depth, SplitReport report)
        {
            var source = sources[request.Key.Source];
            var record = new SplitRecord(request, source, new CaseSet(scope), depth, this);
            scope.AddSplitRecord(record);
            for (var i = 0; i < request.CaseCount; i++)
            {
                var @case = record.Cases.AddCase(options.CaseOptions);
                report.CasesOpened++;
                source.Assume(@case, request, i);
            }
        }

        /// <summary>
        /// Lifts one record's live cases into its scope and disposes the cases found vacuous; notes in
        /// <paramref name="changed"/> whether the lift changed the scope, and returns whether the scope is now
        /// contradicted, which ends the step.
        /// </summary>
        bool Lift(SplitRecord record, SplitReport report, ref bool changed)
        {
            var set = record.Cases;
            var lifted = set.Lift(spec, options.Lift);
            report.Lifts++;
            report.LiftRowsSkipped += lifted.Skipped;
            changed |= lifted.Changed;
            if (lifted.ParentContradicted)
                return true;

            set.DisposeVacuous();
            return false;
        }

    }

}
