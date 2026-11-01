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
        // command had not staged yet: its own write. A conflict of the rebuild, two rows of a function without a merge
        // that the unions make meet with different values, keeps one of the rows and fails the command once the rebuild
        // is done, so everything stands. The batch marks where each fact's planning starts (MarkFact); a fact that fails
        // where it runs drops what it planned but its terms (FailFact, UndoFact): what it appended to the batch's lists
        // is cut back to the marks, the values it set or removed and the earlier writes its deletions cancelled are put
        // back from a log, and the congruence check is replayed from the merges of the facts before it; then the writes,
        // deletions and unions its primitives held are planned again, as the fact plans them. One that fails in what its
        // primitives held drops only that write or deletion (PlanHeld), and keeps the rest and its own write, failing at
        // its end; one whose merges make rows conflict keeps all it planned, is flushed and rebuilt on its own, and fails
        // after (EndFact).

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
        /// The first failure of a write or deletion the fact being planned held (<see cref="PlanHeld"/>), which fails the
        /// fact at its end, as egglog's command panics after its flush.
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
        /// rebuild after the command does (<see cref="CheckClassOutputs"/>): a conflict is noted for the fact that made it,
        /// which fails after its rebuild, as egglog's command panics then (<see cref="EndFact"/>). Only when the fact
        /// changed the check and rows meet, or, below the root, a class that loses is held by a container of a kind that a
        /// row of a function whose merge is not a lattice holds (<see cref="ReplaysRootOnlyRows"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope is not the root, and a merge the passes make would
        /// rewrite a row of a function whose merge is not a lattice.</exception>
        void CheckFactClassOutputs()
        {
            if ((classOutputs is not null || rowsRebuildInOrder || ReplaysRootOnlyRows) && TouchedMergeCheck(factStart))
                CheckClassOutputs();
        }

        /// <summary>
        /// Plans one write or deletion a primitive of the fact held, as egglog's flush applies each of a command's staged
        /// writes: one that conflicts, or whose merge fails, is dropped, as the table keeps the value before it, and the
        /// fact goes on, to fail at its end (<see cref="EndFact"/>). Any other failure is a refusal of the fact, which
        /// propagates.
        /// </summary>
        /// <param name="effect">Plans the effect.</param>
        void PlanHeld(Action effect)
        {
            var mark = Mark();
            try
            {
                effect();
            }
            catch (Exception failure) when (failure is FactConflictException or RuleEvaluationException)
            {
                UndoTo(mark, keepTerms: true);
                heldFailure ??= failure;
            }
        }

        /// <summary>
        /// Ends a fact: plans what its primitives held (<see cref="PlanHeldEffects"/>), and fails the fact, keeping what
        /// it planned, when a write or deletion of them failed, as egglog's command panics after its flush. When the
        /// fact's merges make two rows of a function without a lattice meet with different values (<see cref="Meet"/>,
        /// <see cref="CheckClassOutputs"/>), the fact stands, and the batch flushes and rebuilds it at once
        /// (<see cref="FlushConflict"/>), as egglog's rebuild after the command merges the rows: its assert-equal merge
        /// keeps the row the table holds and panics, which fails the command once the rebuild is done, so the union and
        /// every other consequence of the rebuild stand. That rebuild must be the fact's own, as egglog's is the
        /// command's, since which row it keeps depends on the rows' places and the classes' ids as the commands before left
        /// them: when the facts before merged classes the batch has not flushed, the fact is dropped, they are flushed and
        /// rebuilt, and the fact is to be planned again, against the rebuilt scope, as egglog's command runs after the
        /// last one's rebuild; there its own write may be what conflicts instead. Its primitives run once, as egglog's
        /// command evaluates them: one over the tables or classes is planned after such a flush in the first place
        /// (<see cref="Needs(TermShape)"/>), and a pure one over values gives the plan again the result it gave the first
        /// (<see cref="computed"/>). A flush the scope refuses, for a batch open under it, refuses the fact, which
        /// writes nothing, before anything of it is kept.
        /// </summary>
        /// <returns><c>false</c> when the fact is to be planned again.</returns>
        /// <exception cref="FactConflictException">A held write conflicted, or the fact's merges made rows conflict, and
        /// the fact stands.</exception>
        /// <exception cref="RuleEvaluationException">A held write's merge failed; or the flush of the facts before failed,
        /// as egglog's command whose merge panics, and the fact is not written.</exception>
        /// <exception cref="InvalidOperationException">The fact's merges make rows conflict, and a scope under this one
        /// has a <see cref="Scope.Write"/> batch open, which the flush would change; the fact is not written.</exception>
        bool EndFact()
        {
            PlanHeldEffects();
            if (mergeConflicts is not null && scope.HasBatchBelow)
            {
                UndoFact(keepTerms: false);
                throw BatchBelow();
            }

            if (mergeConflicts is not null && mergedBeforeFact)
            {
                UndoFact(keepTerms: false);
                Rebuild();
                ReplayComputed();
                return false;
            }

            ForgetComputed();
            var conflicts = mergeConflicts;
            var failure = heldFailure;
            mergeConflicts = null;
            MarkFact();
            SettleFact();
            if (conflicts is not null)
                FlushConflict(conflicts, failure);

            if (failure is not null)
            {
                settledFailure = failure;
                ExceptionDispatchInfo.Throw(failure);
            }

            return true;
        }

        /// <summary>
        /// Flushes and rebuilds what the batch planned, the fact whose merges make two rows of a function without a
        /// lattice meet with different values last (<see cref="EndFact"/>): the engine's rebuild keeps the row its table
        /// holds, records the failure and rebuilds to the end, and the flush then fails, as egglog's rebuild after the
        /// command does (egglog-bridge/src/lib.rs:594-623, 1186-1192). The fact fails, keeping all it planned, and the
        /// batch goes on afresh, as egglog's later commands do. A flush that does not fail, where the batch's check found
        /// more than the rebuild does, ends the fact as written.
        /// </summary>
        /// <param name="conflicts">The first two rows of each function the batch's check found meeting with different
        /// values.</param>
        /// <param name="failure">The failure of a write the fact's primitives held, which the fact fails with instead, as
        /// egglog's command fails at its flush before its rebuild.</param>
        /// <exception cref="FactConflictException">The rebuild failed: the row it kept holds the stored value, the other
        /// row the new one, and the engine's failure is the inner exception.</exception>
        void FlushConflict(List<(TableHandle Table, uint[] Key, uint Held, uint Incoming)> conflicts, Exception? failure)
        {
            try
            {
                Rebuild();
            }
            catch (RuleEvaluationException panic) when (failure is null)
            {
                var exception = DescribeConflict(conflicts, panic);
                settledFailure = exception;
                throw exception;
            }
            catch (RuleEvaluationException)
            {
            }
        }

        /// <summary>
        /// Describes, after the rebuild, two rows that the fact's merges made meet, of the function whose merge the
        /// engine's failure names, as egglog's panic names the first that fails, or else of the first function noted:
        /// their keys and the value the table holds at the keys' canonical cells, which the rebuild kept, and the other
        /// row's value, as the rebuild left them canonical.
        /// </summary>
        FactConflictException DescribeConflict(List<(TableHandle Table, uint[] Key, uint Held, uint Incoming)> conflicts, RuleEvaluationException panic)
        {
            var message = panic.InnerException is Relations.PanicException inner ? inner.PanicMessage : panic.Message;
            var conflict = conflicts[0];
            foreach (var noted in conflicts)
            {
                if (message == Relations.EGraph.IllegalMergeMessage(noted.Table.Name))
                {
                    conflict = noted;
                    break;
                }
            }

            var table = conflict.Table;
            var columns = table.KeyArray;
            var keys = new uint[conflict.Key.Length];
            for (var i = 0; i < keys.Length; i++)
                keys[i] = columns[i].InvolvesClasses ? engine.Find(conflict.Key[i]) : conflict.Key[i];

            var output = table.Output!;
            var held = output.InvolvesClasses ? engine.Find(conflict.Held) : conflict.Held;
            var incoming = output.InvolvesClasses ? engine.Find(conflict.Incoming) : conflict.Incoming;
            if (engine.TryLookup(table.Function, keys, out var kept) == false)
                kept = held;

            var other = kept == incoming ? held : incoming;
            return new FactConflictException(table, DecodeKeys(table, keys), Decode(output, kept), Decode(output, other), panic);
        }

        /// <summary>
        /// Ends a fact that failed. One whose end failed for what its primitives held, or for the rows its merges make
        /// meet (<see cref="EndFact"/>), keeps everything else it planned, its own write included, as egglog's command
        /// keeps what its flush wrote and its rebuild made. One that failed as egglog's command panics where it runs (a
        /// primitive that threw or has no result, a merge that failed, a conflict of its own write, a function read with
        /// no value) ends as egglog's failed
        /// command ends: what it staged before it failed stands, since its actions run as a rule whose staged writes
        /// outlive the panic (src/lib.rs:1171-1195). The terms its calls made stand, with their ids; so do the writes,
        /// deletions and unions its primitives held, planned again here as the fact plans them; its own write does not
        /// happen, or was refused. Any other failure is a refusal, which egglog's type checker or the library makes
        /// before the command runs, or the ids running out: the fact writes nothing.
        /// </summary>
        /// <param name="failure">What the fact threw.</param>
        void FailFact(Exception failure)
        {
            ForgetComputed();
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
            var unions = factUnions;
            UndoFact(keepTerms: true);
            if (removals is null && writes is null && unions is null)
                return;

            // What it held is planned from a mark after its terms, so that dropping it keeps them.
            MarkFact();
            factRemovals = removals;
            factWrites = writes;
            factUnions = unions;
            try
            {
                PlanHeldEffects();
            }
            catch (Exception)
            {
                // The fact failed already; what its primitives held cannot be planned either, so it goes.
                UndoFact(keepTerms: true);
                return;
            }

            // Its unions make rows meet with different values: the rebuild that keeps one of them follows at once, and the
            // fact fails with its own failure. A scope with a batch open under it refuses the rebuild, and what the fact
            // held goes, before the next fact starts.
            var conflicts = mergeConflicts is not null;
            if (conflicts && scope.HasBatchBelow)
            {
                UndoFact(keepTerms: true);
                return;
            }

            mergeConflicts = null;
            MarkFact();
            SettleFact();
            if (conflicts)
            {
                try
                {
                    Rebuild();
                }
                catch (RuleEvaluationException)
                {
                }
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
            heldFailure = null;
            mergeConflicts = null;
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
