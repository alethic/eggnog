using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // egglog runs a batch's facts as commands, one after the other: a command that fails writes nothing, and the
        // commands before and after it stand. The batch marks where each fact's planning starts (MarkFact) and, when the
        // fact fails, drops what the fact planned (UndoFact): what it appended to the batch's lists is cut back to the
        // marks, the values it set or removed and the earlier writes its deletions cancelled are put back from a log, and
        // the congruence check is replayed from the merges of the facts before it.

        /// <summary>
        /// The number of unions planned before the fact being planned.
        /// </summary>
        int factUnionCount;

        /// <summary>
        /// The number of deletions planned before the fact being planned.
        /// </summary>
        int factRemoveCount;

        /// <summary>
        /// The number of merges noted before the fact being planned.
        /// </summary>
        int factMergeCount;

        /// <summary>
        /// The number of rows of the congruence check before the fact being planned.
        /// </summary>
        int factMergeRows;

        /// <summary>
        /// The number of pairs of meeting rows the check's replay compares (<see cref="CheckClassOutputs"/>) before the
        /// fact being planned.
        /// </summary>
        int factClassOutputs;

        /// <summary>
        /// Whether the congruence check was decided before the fact being planned.
        /// </summary>
        bool factMergeCheckDecided;

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
        /// Marks the start of a fact: what it plans from here is dropped if it fails (<see cref="UndoFact"/>).
        /// </summary>
        void MarkFact()
        {
            factTerms = pending.Count;
            factRows = rows.Count;
            factSubsumes = subsumes.Count;
            factUnionCount = unions.Count;
            factRemoveCount = removes.Count;
            factMergeCount = notedMerges.Count;
            factMergeRows = mergeRows?.Count ?? 0;
            factClassOutputs = classOutputs?.Count ?? 0;
            factMergeCheckDecided = mergeCheckDecided;
            factFresh.Clear();
            factUndo?.Clear();
        }

        /// <summary>
        /// Gets whether the fact being planned changed the congruence check: noted a merge, added a row to it, or found
        /// rows that meet.
        /// </summary>
        bool FactTouchedMergeCheck =>
            notedMerges.Count > factMergeCount || (mergeRows?.Count ?? 0) != factMergeRows
            || (classOutputs?.Count ?? 0) != factClassOutputs || mergeCheckDecided != factMergeCheckDecided;

        /// <summary>
        /// Compares, at the end of a fact, the rows of a function of a sort that the batch's merges make meet, as egglog's
        /// rebuild after the command does (<see cref="CheckClassOutputs"/>): a conflict fails the fact that made it, as
        /// egglog's command panics. Only when the fact changed the check and rows meet.
        /// </summary>
        /// <exception cref="FactConflictException">Two such rows hold different classes when they meet.</exception>
        void CheckFactClassOutputs()
        {
            if ((classOutputs is not null || rowsRebuildInOrder) && FactTouchedMergeCheck)
                CheckClassOutputs();
        }

        /// <summary>
        /// Drops what the fact being planned planned, which failed: the batch is as it was when the fact started, as
        /// egglog's failed command writes nothing.
        /// </summary>
        void UndoFact()
        {
            factWrites = null;
            factRemovals = null;
            factUnions = null;
            factReaders = null;

            for (var i = factTerms; i < pending.Count; i++)
                pendingByKey.Remove(Key(pending[i].Head, pending[i].Keys));

            pending.RemoveRange(factTerms, pending.Count - factTerms);
            foreach (var id in factFresh)
                fresh.Remove(id);

            // A function with a merge that is not a lattice stages every write as a row of its own, outside rowKeys.
            for (var i = factRows; i < rows.Count; i++)
                if (rows[i].Table is not FunctionHandle { Merge: not null })
                    rowKeys.Remove(Key(rows[i].Table, rows[i].Row));

            rows.RemoveRange(factRows, rows.Count - factRows);
            for (var i = factSubsumes; i < subsumes.Count; i++)
                subsumeKeys.Remove(Key(subsumes[i].Table, subsumes[i].Row));

            subsumes.RemoveRange(factSubsumes, subsumes.Count - factSubsumes);
            for (var i = factUnionCount; i < unions.Count; i++)
                unionKeys.Remove(unions[i]);

            unions.RemoveRange(factUnionCount, unions.Count - factUnionCount);
            for (var i = factRemoveCount; i < removes.Count; i++)
                removeKeys.Remove(Key(removes[i].Table, removes[i].Key));

            removes.RemoveRange(factRemoveCount, removes.Count - factRemoveCount);
            if (factUndo is { } undo)
            {
                for (var i = undo.Count - 1; i >= 0; i--)
                {
                    var (map, key, had, old, action) = undo[i];
                    if (action is not null)
                        action();
                    else if (had)
                        map![key!] = old;
                    else
                        map!.Remove(key!);
                }

                undo.Clear();
            }

            factFresh.Clear();

            // The congruence check of the facts before, replayed from their merges, if the fact changed it.
            var touched = FactTouchedMergeCheck;
            notedMerges.RemoveRange(factMergeCount, notedMerges.Count - factMergeCount);
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
