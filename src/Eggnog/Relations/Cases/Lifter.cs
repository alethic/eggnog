using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Lifts into a parent what holds in every live case of a split: the equalities between parent ids (the meet of the
    /// cases' partitions, <see cref="ClassPartition"/>), the facts of the spec's tables over parent ids, and lattice
    /// values joined over the cases; vacuous cases are left out, and a split whose cases are all vacuous contradicts
    /// the parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rounds. Each round brings the parent and every case that is not stopped up to date, drops the contradictory cases,
    /// picks the live case with the fewest groups as the seed, stages the meet's unions and the facts of every table
    /// (<c>Lifter.Facts.cs</c>), and flushes them into the parent at one epoch. The cases then see the parent's new
    /// facts, which can make further facts expressible over parent ids (a term lifted in one round names the class the
    /// next round's facts mention), so rounds repeat until a round writes nothing, the parent does not change, or
    /// <see cref="LiftOptions.RoundLimit"/> is reached.
    /// </para>
    /// <para>
    /// Soundness: a written equality or fact holds in every live case, a vacuous case has no model, and the coverage of
    /// the cases is the caller's obligation. Completeness: every equality between parent ids, and every fact of a
    /// listed table over parent ids, that holds in every live case is written once the rounds settle, through the
    /// seed's own rows and the winner probe of its group roots. The probe is needed because only losers are marked: an
    /// ancestor row that mentions a merged class's winner is never copied into the case, so the lift looks such rows up
    /// by each winner.
    /// </para>
    /// <para>
    /// Memo. The caller keeps the <see cref="LiftSignature"/> of the last lift that settled: the versions it read and the
    /// spec it lifted. A lift whose first round finds it unchanged returns at once with <see cref="LiftReport.Changed"/>
    /// false. A lift has settled when it stopped on its own (not at <see cref="LiftOptions.RoundLimit"/>) and its last round
    /// skipped no source row over <see cref="LiftOptions.CandidateLimit"/>, so its answer does not depend on the limits; a
    /// lift that did not settle clears the memo, and the next one runs in full.
    /// </para>
    /// <para>
    /// Threading and cost. The lift runs on the thread that holds the parent's domain, which every case shares (the parent
    /// is not frozen); it holds the domain for the whole lift and never calls caller code but the case joins. A round costs
    /// the cases' rebuilds plus, per table, O(seed's own rows + uses of its group roots) source rows, each expanded into at
    /// most <see cref="LiftOptions.CandidateLimit"/> parent keys that are looked up once per live case and in the parent.
    /// One <see cref="Lifter"/> keeps its scratch across lifts; it is not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed partial class Lifter
    {

        readonly List<EGraphContext> live = new();
        readonly List<MemberMap> maps = new();
        readonly List<uint[]> blocks = new();
        readonly List<LiftedTable> tables = new();
        readonly LiftCandidates sources = new();
        readonly LiftCandidates candidates = new();
        readonly LiftWrites writes = new();
        LayeredUnionFind[] finders = Array.Empty<LayeredUnionFind>();
        uint[] outputs = Array.Empty<uint>();

        /// <summary>
        /// Lifts what holds in every live case of <paramref name="cases"/> into <paramref name="parent"/> with a new
        /// <see cref="Lifter"/>.
        /// </summary>
        /// <param name="parent">The cases' parent; not frozen, stopped or disposed.</param>
        /// <param name="cases">The cases: children of <paramref name="parent"/>, at least one.</param>
        /// <param name="spec">The tables to lift.</param>
        /// <param name="options">The limits.</param>
        /// <param name="memo">The signature of the last settled lift over these cases, or <c>null</c>; replaced by this
        /// lift's when it settles, and cleared when it does not.</param>
        /// <param name="cancellationToken">Checked before every round.</param>
        /// <returns>What the lift did.</returns>
        /// <exception cref="ArgumentException">A case is not a child of <paramref name="parent"/> or is disposed; a listed
        /// function is not declared or does not fit its kind.</exception>
        /// <exception cref="InvalidOperationException">There are no cases; the parent is frozen, stopped or faulted; or a
        /// context's domain is held elsewhere.</exception>
        /// <exception cref="PanicException">Flushing the lifted writes recorded a failure.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled between rounds.</exception>
        public static LiftReport Lift(EGraphContext parent, IReadOnlyList<EGraphContext> cases, LiftSpec spec, LiftOptions options, ref LiftSignature? memo, CancellationToken cancellationToken = default)
        {
            return new Lifter().Run(parent, cases, spec, options, ref memo, cancellationToken);
        }

        /// <summary>
        /// Lifts what holds in every live case of <paramref name="cases"/> into <paramref name="parent"/>, reusing this
        /// lifter's scratch (<see cref="Lift"/>).
        /// </summary>
        /// <param name="parent">The cases' parent; not frozen, stopped or disposed.</param>
        /// <param name="cases">The cases: children of <paramref name="parent"/>, at least one.</param>
        /// <param name="spec">The tables to lift.</param>
        /// <param name="options">The limits.</param>
        /// <param name="memo">The memo (<see cref="Lift"/>).</param>
        /// <param name="cancellationToken">Checked before every round.</param>
        /// <returns>What the lift did.</returns>
        public LiftReport Run(EGraphContext parent, IReadOnlyList<EGraphContext> cases, LiftSpec spec, LiftOptions options, ref LiftSignature? memo, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(cases);
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(options);
            Validate(parent, cases);
            Resolve(parent, spec);

            var context = parent.Context;
            var domain = context.Domain;
            using var scope = domain.Enter();
            Context.ThrowIfInCallback(domain, "Lift");
            var report = new LiftReport();
            long skippedBefore = 0;
            try
            {
                for (var first = true; ; first = false)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    parent.EnsureClean();
                    foreach (var @case in cases)
                        if (@case.IsStopped == false)
                            @case.EnsureClean();

                    if (first && memo is not null && memo.Matches(parent, cases, spec))
                    {
                        // Nothing the settled lift read has changed: it found every case vacuous (and contradicted the
                        // parent) exactly when no live case is left now.
                        CountLive(cases, report);
                        report.ParentContradicted = live.Count == 0;
                        report.Memoized = true;
                        return report;
                    }

                    // A lift that runs replaces the memo, and one that throws part-way leaves none.
                    memo = null;
                    skippedBefore = report.Skipped;
                    CountLive(cases, report);
                    if (live.Count == 0)
                    {
                        // No live case, so no model of the parent under the cases' coverage.
                        report.ParentContradicted = true;
                        report.Changed |= ContradictParent(parent, cases, false, 0);
                        break;
                    }

                    var seed = ChooseSeed();
                    writes.Clear();
                    LiftEqualities(seed, report);
                    LiftFacts(parent, seed, options, report);
                    if (writes.IsEmpty)
                        break;

                    var before = parent.Version;
                    try
                    {
                        writes.ApplyTo(parent);
                    }
                    catch
                    {
                        // Staging failed part-way (a lifted term that ran out of ids): the round merges nothing. The
                        // parent was brought up to date as the round began, so every host write staged in it is the
                        // round's.
                        parent.DropHostWrites();
                        throw;
                    }

                    writes.Clear();
                    parent.FlushUpdates();
                    report.Rounds++;
                    if (parent.Version == before)
                        break;

                    report.Changed = true;
                    if (report.Rounds == options.RoundLimit)
                    {
                        report.HitRoundLimit = true;
                        break;
                    }
                }

                if (parent.IsStopped == false)
                {
                    parent.EnsureClean();
                    foreach (var @case in cases)
                        if (@case.IsStopped == false)
                            @case.EnsureClean();
                }

                // A lift cut short by its round limit, or whose last round skipped rows over the candidate limit, has not
                // settled: a later lift with other limits may write more, so it must not be skipped.
                memo = report.HitRoundLimit || report.Skipped != skippedBefore ? null : LiftSignature.Of(parent, cases, spec);
                return report;
            }
            finally
            {
                writes.Clear();
                live.Clear();
                maps.Clear();
                blocks.Clear();
                Array.Clear(finders);
            }
        }

        /// <summary>
        /// Writes the parent's contradiction when every case is vacuous, through the e-graph's contradiction function
        /// if it has one and the parent does not see it yet, and flushes. The row's output is the function's constant
        /// default; else, for an id output, the term's id in the parent (a fresh one), never a case's output, which can
        /// be local to it and so absent from the parent; else the base value a vacuous case merged into its own layer
        /// of the function, read from <paramref name="cases"/> or, when none of them has it (a case set that already
        /// disposed its vacuous cases), <paramref name="keptOutput"/>.
        /// </summary>
        /// <param name="parent">The parent; clean.</param>
        /// <param name="cases">The cases, all vacuous; may be empty.</param>
        /// <param name="hasKeptOutput">Whether <paramref name="keptOutput"/> holds a base output a vacuous case held
        /// before it was disposed (<see cref="TryReadContradictionOutput"/>).</param>
        /// <param name="keptOutput">That output.</param>
        /// <returns>Whether the parent changed.</returns>
        /// <exception cref="InvalidOperationException">The contradiction function's output cannot be produced.</exception>
        internal static bool ContradictParent(EGraphContext parent, IReadOnlyList<EGraphContext> cases, bool hasKeptOutput, uint keptOutput)
        {
            var graph = parent.Owner;
            if (graph.Contradiction is not { } contradiction || parent.IsStopped || parent.IsContradictory)
                return false;

            var info = graph.GetFunctionInfo(contradiction);
            var before = parent.Version;
            if (info.Default.Kind == DefaultValKind.Const)
            {
                parent.StageRow(contradiction, [info.Default.Constant]);
            }
            else if (info.OutputType.IsId)
            {
                parent.StageTerm(contradiction, ReadOnlySpan<Value>.Empty);
            }
            else if (TryReadVacuousOutput(cases, info, out var output))
            {
                parent.StageRow(contradiction, [new Value(output)]);
            }
            else if (hasKeptOutput)
            {
                parent.StageRow(contradiction, [new Value(keptOutput)]);
            }
            else
            {
                throw new InvalidOperationException($"Every case is vacuous, but no value of {info.Name}'s output is known to write the parent's contradiction.");
            }

            parent.FlushUpdates();
            return parent.Version != before;
        }

        /// <summary>
        /// Reads the base output of a contradiction row some vacuous case merged into its own layer (base values are shared
        /// by every context, so the cell is valid in the parent).
        /// </summary>
        static bool TryReadVacuousOutput(IReadOnlyList<EGraphContext> cases, FunctionInfo info, out uint output)
        {
            foreach (var @case in cases)
                if (@case.TryReadOwnCell(info.Table, info.Math.OutputColumn, out output))
                    return true;

            output = 0;
            return false;
        }

        /// <summary>
        /// Reads the base output of the contradiction row a vacuous case merged into its own layer, so a case set can keep
        /// it before disposing the case (<see cref="ContradictParent"/>). Only when the e-graph has a contradiction
        /// function whose output is a base value with no constant default, the one case where
        /// <see cref="ContradictParent"/> needs it. O(the case's own rows of that function), at most one.
        /// </summary>
        /// <param name="case">A vacuous case, not disposed.</param>
        /// <param name="output">The output, when found.</param>
        /// <returns>Whether an output was found.</returns>
        internal static bool TryReadContradictionOutput(EGraphContext @case, out uint output)
        {
            output = 0;
            var graph = @case.Owner;
            if (graph.Contradiction is not { } contradiction)
                return false;

            var info = graph.GetFunctionInfo(contradiction);
            if (info.Default.Kind == DefaultValKind.Const || info.OutputType.IsId)
                return false;

            return @case.TryReadOwnCell(info.Table, info.Math.OutputColumn, out output);
        }

        /// <summary>
        /// Checks the lift's preconditions on the parent and the cases.
        /// </summary>
        static void Validate(EGraphContext parent, IReadOnlyList<EGraphContext> cases)
        {
            if (parent.IsDisposed)
                throw new ObjectDisposedException(nameof(EGraphContext), "The cases' parent is disposed.");

            if (parent.IsFrozen)
                throw new InvalidOperationException("A frozen context cannot be lifted into; open the cases under a child of it.");

            if (parent.IsFaulted)
                throw new InvalidOperationException("The cases' parent is faulted; only Dispose is allowed.");

            if (parent.IsStopped)
                throw new InvalidOperationException("The cases' parent is stopped by a contradiction; there is nothing to lift into.");

            if (cases.Count == 0)
                throw new InvalidOperationException("A lift needs at least one case; with none, the parent would be declared contradictory.");

            for (var i = 0; i < cases.Count; i++)
            {
                var @case = cases[i] ?? throw new ArgumentException("A case is null.", nameof(cases));
                if (ReferenceEquals(@case.Parent, parent) == false)
                    throw new ArgumentException("Every case must be a child of the parent lifted into.", nameof(cases));

                if (@case.IsDisposed)
                    throw new ArgumentException("A case is disposed.", nameof(cases));

                for (var j = 0; j < i; j++)
                    if (ReferenceEquals(cases[j], @case))
                        throw new ArgumentException("A case is listed twice.", nameof(cases));
            }
        }

        /// <summary>
        /// Collects the live cases and counts them into <paramref name="report"/>.
        /// </summary>
        void CountLive(IReadOnlyList<EGraphContext> cases, LiftReport report)
        {
            live.Clear();
            foreach (var @case in cases)
                if (@case.IsContradictory == false)
                    live.Add(@case);

            report.LiveCases = live.Count;
            report.VacuousCases = cases.Count - live.Count;
        }

        /// <summary>
        /// Takes every live case's member map and its find, and returns the index of the case with the fewest groups.
        /// </summary>
        int ChooseSeed()
        {
            maps.Clear();
            if (finders.Length < live.Count)
                finders = new LayeredUnionFind[live.Count];

            var seed = 0;
            for (var i = 0; i < live.Count; i++)
            {
                var map = live[i].GetMemberMap();
                maps.Add(map);
                finders[i] = live[i].LiftFinder();
                if (map.GroupCount < maps[seed].GroupCount)
                    seed = i;
            }

            return seed;
        }

    }

}
