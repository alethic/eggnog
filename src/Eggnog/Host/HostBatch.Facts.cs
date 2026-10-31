using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // egglog runs a batch's facts as commands, one after the other, and the commands before and after a failed one
        // stand. A command's actions run as a rule (src/lib.rs:1171-1195): its primitives stage their writes, deletions
        // and unions as they run, its own write is staged after them, and the flush applies the deletions, then the
        // inserts in the order they were staged, each merged into what the table holds, so a write a function without a
        // merge refuses keeps the value before it, and the rebuild then merges the unions. A conflict there, or a panic
        // of a call, fails the command, and what it staged stands but the write that conflicted, or, for a panic, what the
        // command had not staged yet: its own write. The batch marks where each fact's planning starts (MarkFact); a
        // fact that fails where it runs drops what it planned but its terms (FailFact, UndoFact): what it appended to the
        // batch's lists is cut back to the marks, the values it set or removed and the earlier writes its deletions
        // cancelled are put back from a log, and the congruence check is replayed from the merges of the facts before
        // it; then the writes, deletions and unions its primitives held are planned again, as the fact plans them. One
        // that fails in what its primitives held drops only that write, deletion or union (PlanHeld), and keeps the rest
        // and its own write, failing at its end (EndFact).

        /// <summary>
        /// Where the fact being planned started.
        /// </summary>
        FactMark factStart;

        /// <summary>
        /// The ids the fact being planned minted.
        /// </summary>
        readonly List<uint> factFresh = new();

        /// <summary>
        /// Every merge of classes the batch noted (<see cref="Merge"/>), which a failed fact's undo replays.
        /// </summary>
        readonly List<(uint A, uint B)> notedMerges = new();

        /// <summary>
        /// What puts back the values the fact being planned set or removed, and the earlier writes its deletions
        /// cancelled: a value of a map, or an action.
        /// </summary>
        List<(Dictionary<string, uint>? Map, string? Key, bool Had, uint Old, Action? Undo)>? factUndo;

        /// <summary>
        /// The deletions of primitives the fact being planned held and planned at its write point
        /// (<see cref="PlanHeldWrites"/>), which a failure of its own write keeps (<see cref="FailFact"/>).
        /// </summary>
        List<(TableHandle Table, Term[] Arguments)>? factRemovalsApplied;

        /// <summary>
        /// The writes of primitives the fact being planned held and planned at its write point, which a failure of its own
        /// write keeps.
        /// </summary>
        List<Action>? factWritesApplied;

        /// <summary>
        /// The unions the fact being planned held and planned at its end (<see cref="PlanFactUnions"/>), which a failure
        /// of the congruence check its own merges fail keeps.
        /// </summary>
        List<(uint A, uint B)>? factUnionsApplied;

        /// <summary>
        /// The first failure of a write, deletion or union the fact being planned held (<see cref="PlanHeld"/>), which
        /// fails the fact at its end, as egglog's command panics after its flush.
        /// </summary>
        Exception? heldFailure;

        /// <summary>
        /// The failure the end of a fact threw for what it held (<see cref="EndFact"/>), which leaves the fact's other
        /// effects planned: <see cref="FailFact"/> drops nothing for it.
        /// </summary>
        Exception? settledFailure;

        /// <summary>
        /// Whether the fact being planned failed as egglog's command panics where it runs: a function read with no value,
        /// a subsumption of a row that is not there (<see cref="FailFact"/>).
        /// </summary>
        bool factPanicked;

        /// <summary>
        /// Marks the start of a fact: what it plans from here is dropped if it fails (<see cref="UndoFact"/>).
        /// </summary>
        void MarkFact()
        {
            factTerms = pending.Count;
            factRows = rows.Count;
            factSubsumes = subsumes.Count;
            factFresh.Clear();
            factUndo?.Clear();
            factStart = Mark();
            factRemovalsApplied = null;
            factWritesApplied = null;
            factUnionsApplied = null;
            heldFailure = null;
            factPanicked = false;
        }

        /// <summary>
        /// Gets where the batch stands, to go back to (<see cref="UndoTo"/>).
        /// </summary>
        FactMark Mark() => new(
            pending.Count, factTerms, rows.Count, factRows, subsumes.Count, factSubsumes, unions.Count, removes.Count,
            notedMerges.Count, mergeRows?.Count ?? 0, classOutputs?.Count ?? 0, mergeCheckDecided, factUndo?.Count ?? 0);

        /// <summary>
        /// Gets whether the batch changed the congruence check since <paramref name="mark"/>: noted a merge, added a row
        /// to it, or found rows that meet.
        /// </summary>
        bool TouchedMergeCheck(FactMark mark) =>
            notedMerges.Count > mark.Merges || (mergeRows?.Count ?? 0) != mark.MergeRows
            || (classOutputs?.Count ?? 0) != mark.ClassOutputs || mergeCheckDecided != mark.MergeCheckDecided;

        /// <summary>
        /// Compares, at the end of a fact, the rows of a function of a sort that the batch's merges make meet, as egglog's
        /// rebuild after the command does (<see cref="CheckClassOutputs"/>): a conflict fails the fact that made it, as
        /// egglog's command panics. Only when the fact changed the check and rows meet, or, below the root, a class that
        /// loses is held by a container of a kind that a row of a function whose merge is not a lattice holds
        /// (<see cref="ReplaysRootOnlyRows"/>).
        /// </summary>
        /// <exception cref="FactConflictException">Two such rows hold different classes when they meet.</exception>
        /// <exception cref="InvalidOperationException">The scope is not the root, and a merge the passes make would
        /// rewrite a row of a function whose merge is not a lattice.</exception>
        void CheckFactClassOutputs()
        {
            if ((classOutputs is not null || rowsRebuildInOrder || ReplaysRootOnlyRows) && TouchedMergeCheck(factStart))
                CheckClassOutputs();
        }

        /// <summary>
        /// Compares the rows the fact's merges make meet (<see cref="CheckFactClassOutputs"/>) once its held unions are
        /// planned. A conflict refuses the held unions that cause it, as a conflict a union of the batch causes refuses
        /// that union, keeps the others, and fails the fact at its end; one the fact's own merges cause, with none of the
        /// held unions, is the fact's own failure. The held unions are planned again from the first, in halves
        /// (<see cref="PlanHeldUnions"/>), so a union that conflicts alone is the one refused, whatever its place, in
        /// O(log n) checks for each union refused, not one per union.
        /// </summary>
        /// <param name="first">Where the first held union the fact planned started.</param>
        /// <param name="planned">The held unions the fact planned, in order; <c>null</c> for none.</param>
        /// <exception cref="FactConflictException">The fact's own merges make the rows conflict.</exception>
        /// <exception cref="InvalidOperationException">The scope is not the root, and a merge the passes make would
        /// rewrite a row of a function whose merge is not a lattice.</exception>
        void CheckHeldClassOutputs(FactMark first, List<(uint A, uint B)>? planned)
        {
            try
            {
                CheckFactClassOutputs();
            }
            catch (FactConflictException conflict) when (planned is not null)
            {
                heldFailure ??= conflict;
                UndoTo(first, keepTerms: true);
                CheckFactClassOutputs();
                if (planned.Count > 1)
                {
                    var half = planned.Count / 2;
                    PlanHeldUnions(planned, 0, half);
                    PlanHeldUnions(planned, half, planned.Count - half);
                }
            }
        }

        /// <summary>
        /// Plans <paramref name="count"/> held unions of <paramref name="planned"/> from <paramref name="start"/> again,
        /// and keeps them if the rows the fact's merges make meet still agree (<see cref="CheckFactClassOutputs"/>);
        /// otherwise drops them, and plans each half again the same way, down to the one union that conflicts, which is
        /// refused.
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope is not the root, and a merge the passes make would
        /// rewrite a row of a function whose merge is not a lattice.</exception>
        void PlanHeldUnions(List<(uint A, uint B)> planned, int start, int count)
        {
            var mark = Mark();
            for (var i = start; i < start + count; i++)
            {
                var (a, b) = planned[i];
                PlanHeld(() => PlanUnionCore(a, b));
            }

            try
            {
                CheckFactClassOutputs();
                return;
            }
            catch (FactConflictException)
            {
                UndoTo(mark, keepTerms: true);
            }

            if (count > 1)
            {
                var half = count / 2;
                PlanHeldUnions(planned, start, half);
                PlanHeldUnions(planned, start + half, count - half);
            }
        }

        /// <summary>
        /// Plans one write, deletion or union a primitive of the fact held, as egglog's flush applies each of a command's
        /// staged writes: one that conflicts, or whose merge fails, is dropped, as the table keeps the value before it, and
        /// the fact goes on, to fail at its end (<see cref="EndFact"/>). Any other failure is a refusal of the fact, which
        /// propagates.
        /// </summary>
        /// <param name="effect">Plans the effect.</param>
        /// <returns>Whether the effect was planned.</returns>
        bool PlanHeld(Action effect)
        {
            var mark = Mark();
            try
            {
                effect();
                return true;
            }
            catch (Exception failure) when (failure is FactConflictException or RuleEvaluationException)
            {
                UndoTo(mark, keepTerms: true);
                heldFailure ??= failure;
                return false;
            }
        }

        /// <summary>
        /// Ends a fact: plans what its primitives held (<see cref="PlanFactUnions"/>), and fails the fact, keeping what it
        /// planned, when a write, deletion or union of them failed, as egglog's command panics after its flush.
        /// </summary>
        /// <exception cref="FactConflictException">A held write or union conflicted.</exception>
        /// <exception cref="RuleEvaluationException">A held write's merge failed.</exception>
        void EndFact()
        {
            if (PlanFactUnions() is { } failure)
            {
                settledFailure = failure;
                ExceptionDispatchInfo.Throw(failure);
            }
        }

        /// <summary>
        /// Ends a fact that failed. One whose end failed for what its primitives held (<see cref="EndFact"/>) keeps
        /// everything else it planned, its own write included, as egglog's command keeps what its flush wrote. One that
        /// failed as egglog's command panics where it runs (a primitive that threw or has no result, a merge that failed,
        /// a conflict of its own write or of its own merges, a function read with no value) ends as egglog's failed
        /// command ends: what it staged before it failed stands, since its actions run as a rule whose staged writes
        /// outlive the panic (src/lib.rs:1171-1195). The terms its calls made stand, with their ids; so do the writes,
        /// deletions and unions its primitives held, planned again here as the fact plans them; its own write does not
        /// happen, or was refused. Any other failure is a refusal, which egglog's type checker or the library makes
        /// before the command runs, or the ids running out: the fact writes nothing.
        /// </summary>
        /// <param name="failure">What the fact threw.</param>
        void FailFact(Exception failure)
        {
            if (ReferenceEquals(failure, settledFailure))
            {
                settledFailure = null;
                return;
            }

            settledFailure = null;
            if (factPanicked == false && failure is not (RuleEvaluationException or FactConflictException))
            {
                UndoFact(keepTerms: false);
                return;
            }

            var removals = Join(factRemovalsApplied, factRemovals);
            var writes = Join(factWritesApplied, factWrites);
            var unions = Join(factUnionsApplied, factUnions);
            UndoFact(keepTerms: true);
            if (removals is null && writes is null && unions is null)
                return;

            factRemovals = removals;
            factWrites = writes;
            factUnions = unions;
            try
            {
                PlanFactUnions();
            }
            catch (Exception)
            {
                // The fact failed already; what its primitives held cannot be planned either, so it goes.
                UndoFact(keepTerms: true);
            }
        }

        /// <summary>
        /// Joins two lists of what a fact held, either of which may be <c>null</c>.
        /// </summary>
        static List<T>? Join<T>(List<T>? first, List<T>? second) =>
            first is null ? second : second is null ? first : [.. first, .. second];

        /// <summary>
        /// Drops what the fact being planned planned, which failed: the batch is as it was when the fact started, but for
        /// the terms the fact made, of the ids it minted, when <paramref name="keepTerms"/> is set.
        /// </summary>
        /// <param name="keepTerms">Whether the fact's terms stand.</param>
        void UndoFact(bool keepTerms)
        {
            factWrites = null;
            factRemovals = null;
            factUnions = null;
            factReaders = null;
            factRemovalsApplied = null;
            factWritesApplied = null;
            factUnionsApplied = null;
            heldFailure = null;
            factPanicked = false;
            UndoTo(factStart, keepTerms);
            if (keepTerms == false)
            {
                foreach (var id in factFresh)
                    fresh.Remove(id);
            }

            factFresh.Clear();
        }

        /// <summary>
        /// Drops what the batch planned since <paramref name="mark"/>, within the fact being planned, but for the terms
        /// of the ids the fact minted, when <paramref name="keepTerms"/> is set. A term of a class that was there before
        /// is a write, a constructor's output set to a class, which goes.
        /// </summary>
        /// <param name="mark">Where to go back to: the start of the fact, or a point in it.</param>
        /// <param name="keepTerms">Whether the fact's terms stand.</param>
        void UndoTo(FactMark mark, bool keepTerms)
        {
            // A deletion since the mark that cancelled what an earlier fact wrote moved the fact's bounds down, and the
            // lists with them; the log below puts both back.
            var terms = mark.Terms - (mark.FactTerms - factTerms);
            for (var i = pending.Count - 1; i >= terms; i--)
            {
                if (keepTerms && factFresh.Contains(pending[i].Id))
                    continue;

                pendingByKey.Remove(Key(pending[i].Head, pending[i].Keys));
                pending.RemoveAt(i);
            }

            // A function with a merge that is not a lattice stages every write as a row of its own, outside rowKeys.
            var rowCount = mark.Rows - (mark.FactRows - factRows);
            for (var i = rowCount; i < rows.Count; i++)
                if (rows[i].Table is not FunctionHandle { Merge: not null })
                    rowKeys.Remove(Key(rows[i].Table, rows[i].Row));

            rows.RemoveRange(rowCount, rows.Count - rowCount);
            var subsumeCount = mark.Subsumes - (mark.FactSubsumes - factSubsumes);
            for (var i = subsumeCount; i < subsumes.Count; i++)
                subsumeKeys.Remove(Key(subsumes[i].Table, subsumes[i].Row));

            subsumes.RemoveRange(subsumeCount, subsumes.Count - subsumeCount);
            for (var i = mark.Unions; i < unions.Count; i++)
                unionKeys.Remove(unions[i]);

            unions.RemoveRange(mark.Unions, unions.Count - mark.Unions);
            for (var i = mark.Removes; i < removes.Count; i++)
                removeKeys.Remove(Key(removes[i].Table, removes[i].Key));

            removes.RemoveRange(mark.Removes, removes.Count - mark.Removes);
            if (factUndo is { } undo && undo.Count > mark.Undo)
            {
                for (var i = undo.Count - 1; i >= mark.Undo; i--)
                {
                    var (map, key, had, old, action) = undo[i];
                    if (action is not null)
                        action();
                    else if (had)
                        map![key!] = old;
                    else
                        map!.Remove(key!);
                }

                undo.RemoveRange(mark.Undo, undo.Count - mark.Undo);
            }

            // The congruence check as it was at the mark, replayed from the merges noted before it, if it changed since.
            var touched = TouchedMergeCheck(mark);
            notedMerges.RemoveRange(mark.Merges, notedMerges.Count - mark.Merges);
            if (touched)
            {
                ResetMergeCheck();
                foreach (var (a, b) in notedMerges)
                    NoteMerge(a, b);
            }
        }

        /// <summary>
        /// Notes a merge of two classes that the batch plans (<see cref="NoteMerge"/>), which a failed fact's undo
        /// replays for the facts before it.
        /// </summary>
        void Merge(uint a, uint b)
        {
            notedMerges.Add((a, b));
            NoteMerge(a, b);
        }

        /// <summary>
        /// Sets a value of a map, which a failure of the fact puts back.
        /// </summary>
        void SetValue(Dictionary<string, uint> map, string key, uint value)
        {
            var had = map.TryGetValue(key, out var old);
            (factUndo ??= new()).Add((map, key, had, old, null));
            map[key] = value;
        }

        /// <summary>
        /// Removes a value of a map, which a failure of the fact puts back.
        /// </summary>
        void RemoveValue(Dictionary<string, uint> map, string key)
        {
            if (map.TryGetValue(key, out var old))
            {
                (factUndo ??= new()).Add((map, key, true, old, null));
                map.Remove(key);
            }
        }

        /// <summary>
        /// Records what puts back a change the fact made, should it fail.
        /// </summary>
        void OnUndo(Action undo) => (factUndo ??= new()).Add((null, null, false, 0, undo));

    }

}
