using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // egglog's delete: a top-level delete (Scope.Delete), and a primitive's remove in a host write. A key's terms are
        // evaluated as egglog's top-level action evaluates them (a missing constructor term is made, a function read must
        // find a value), and a row that does not exist plans nothing. A scope deletes only when the engine's retraction
        // guard lets it (no live descendant, no writable parent), which the batch checks before it plans anything. A
        // deleted row of a table that the scope reads under its own tag is deleted from that mirror too, so the mirror
        // stays equal to the table, as a scoped read of the scope's own tag must be.
        //
        // egglog's flush applies a command's removals before its inserts, and its lookups see no staged removal. The
        // batch flushes once, its removals before its inserts too, so it plans a deletion at the write point of its
        // fact, once the fact's terms are evaluated: the fact's reads see the row, the deletion cancels what the
        // batch's earlier facts wrote at the key, the fact's own writes there are kept, and later facts see the key
        // without its stored row, as egglog's next command does.

        readonly List<(TableHandle Table, uint[] Key)> removes = new();
        readonly HashSet<string> removeKeys = new(StringComparer.Ordinal);
        List<(TableHandle Table, Term[] Arguments)>? factRemovals;

        /// <summary>
        /// The number of rows planned before the fact being planned, which its deletions cancel; the later ones are the
        /// fact's own.
        /// </summary>
        int factRows;

        /// <summary>
        /// The number of subsumed rows planned before the fact being planned.
        /// </summary>
        int factSubsumes;

        /// <summary>
        /// Plans the deletion of the row of a ground function or constructor application (egglog's <c>delete</c>): the
        /// row of its keys, if there is one. A constructor's class stays, with its other members.
        /// </summary>
        /// <param name="term">The application: <c>f.Of(k)</c> of a function or a constructor.</param>
        /// <exception cref="ArgumentException">The term is not an application of a function or a constructor of the
        /// scope written, or a term in its keys is not ground or holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete, or a function read in the keys has no
        /// value.</exception>
        /// <exception cref="RuleEvaluationException">A primitive in the keys threw, or has no result.</exception>
        public void PlanDelete(Term term)
        {
            ArgumentNullException.ThrowIfNull(term);
            PlanFactUnions();
            var shape = term.Shape;
            if (shape.Kind != TermShapeKind.Application)
                throw new ArgumentException($"Delete takes a function or constructor application; {term} is not one", nameof(term));

            try
            {
                RebuildFor(shape.Table!, shape.Arguments, deletes: true);
                if (stopped)
                    return;

                PlanDelete(shape.Table!, shape.Arguments, nameof(term));
                PlanFactUnions();
            }
            catch
            {
                UndoFact();
                throw;
            }
        }

        /// <summary>
        /// Plans the deletion of a ground relation row (egglog's <c>delete</c>), if it exists.
        /// </summary>
        /// <param name="fact">The row: <c>r.Of(a, b)</c>.</param>
        /// <exception cref="ArgumentException">The fact is not a relation row of the scope written, or a term in it is not
        /// ground or holds a class value this scope cannot use.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete, or a function read in the row has no
        /// value.</exception>
        /// <exception cref="RuleEvaluationException">A primitive in the row threw, or has no result.</exception>
        public void PlanDelete(Fact fact)
        {
            ArgumentNullException.ThrowIfNull(fact);
            PlanFactUnions();
            if (fact is not RelationFact relation)
            {
                throw fact is ScopedFact scoped
                    ? new ArgumentException($"host writes go to the scope written; Fact.In('{scoped.Scope.Name}') is read-only", nameof(fact))
                    : new ArgumentException($"Delete(rows) takes relation rows, r.Of(...); {fact} is not one (delete an application with Delete(term))", nameof(fact));
            }

            try
            {
                RebuildFor(relation.Table, relation.Arguments, deletes: true);
                if (stopped)
                    return;

                PlanDelete(relation.Table, relation.Arguments, nameof(fact));
                PlanFactUnions();
            }
            catch
            {
                UndoFact();
                throw;
            }
        }

        /// <summary>
        /// Plans the deletion of the row of <paramref name="arguments"/> from <paramref name="table"/>, and from the
        /// mirror of the table under the scope's own tag.
        /// </summary>
        void PlanDelete(TableHandle table, Term[] arguments, string parameter)
        {
            CheckTable(table);
            CheckDeletable(table);
            Literals.Action(arguments, null);
            var keys = PlanKeys(table, arguments, out var existing);
            ApplyFactRemovals();
            RemoveRow(table, keys, existing);
        }

        /// <summary>
        /// Holds back a primitive's deletion of a row until the write point of its fact (<see cref="ApplyFactRemovals"/>).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="arguments">The row's keys, as constant terms.</param>
        internal void HoldRemove(TableHandle table, Term[] arguments) => (factRemovals ??= new()).Add((table, arguments));

        /// <summary>
        /// Plans the deletions the fact being planned holds back, at its write point: after its terms are evaluated,
        /// before its own writes.
        /// </summary>
        /// <exception cref="InvalidOperationException">A deletion is of a table whose keys are classes after the batch
        /// merged classes, or of a table an expression of the batch's merges reads.</exception>
        void ApplyFactRemovals()
        {
            if (factRemovals is not { Count: > 0 } held)
                return;

            factRemovals = null;
            foreach (var (table, arguments) in held)
            {
                var keys = PlanKeys(table, arguments, out var existing);
                RemoveRow(table, keys, existing);
            }
        }

        /// <summary>
        /// Plans the deletion of a row at the write point of a fact: what the batch's earlier facts wrote at the key is
        /// cancelled, and the stored row, if there is one, removed, from the scope's mirror of the table too.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="keys">The key's cells.</param>
        /// <param name="existing">Whether no key is a class the batch made, which no stored row holds.</param>
        /// <exception cref="InvalidOperationException">The table's keys are classes and the batch merged classes before; or
        /// an expression of the batch's merges reads the table.</exception>
        void RemoveRow(TableHandle table, uint[] keys, bool existing)
        {
            // egglog's next command would delete after the merge's rebuild had moved the rows whose keys it merged; the
            // batch rebuilds before such a deletion (RebuildFor), but for a rejected batch.
            if (mergedBeforeFact && HasSortKey(table))
                throw new InvalidOperationException(
                    $"'{table.Name}' loses a row after this batch merged classes, and its keys are classes; delete it in a later call, which sees the merge as egglog's next command does.");

            CheckNotReadByExpression(table);
            CancelWrites(table, keys, Key(table, keys));
            if (existing == false || TryStored(table, keys, out _) == false)
                return;

            AddRemove(table, keys);
            if (scope.Tag is { } tag && graph.Mirrors.TryGet(table, tag, out var mirror) && engine.TryLookup(mirror.Handle.Function, keys, out _))
                AddRemove(mirror.Handle, keys);
        }

        /// <summary>
        /// Cancels what the batch's earlier facts wrote at a key, as the deletion in a later egglog command removes it: a
        /// new term's row (its class stays, with whatever else holds it), a row, a value, a subsumption. A constructor's
        /// row that gave a stored key another class keeps the merge it made, as egglog's rebuild made it before the
        /// deletion.
        /// </summary>
        void CancelWrites(TableHandle table, uint[] keys, string key)
        {
            if (pendingByKey.TryGetValue(key, out var term))
            {
                var index = pending.IndexOf(term);
                if (index < factTerms)
                {
                    pending.RemoveAt(index);
                    pendingByKey.Remove(key);
                    var wasSubsumed = subsumedTerms.Remove(key);
                    factTerms--;
                    OnUndo(() =>
                    {
                        pending.Insert(index, term);
                        pendingByKey.Add(key, term);
                        if (wasSubsumed)
                            subsumedTerms.Add(key);

                        factTerms++;
                    });
                }
            }

            for (var i = factRows - 1; i >= 0; i--)
            {
                var (written, row) = rows[i];
                if (ReferenceEquals(written, table) == false || row.AsSpan(0, keys.Length).SequenceEqual(keys) == false)
                    continue;

                var index = i;
                rows.RemoveAt(index);
                var rowKey = Key(table, row);
                var hadKey = rowKeys.Remove(rowKey);
                factRows--;
                OnUndo(() =>
                {
                    rows.Insert(index, (written, row));
                    if (hadKey)
                        rowKeys.Add(rowKey);

                    factRows++;
                });

                if (table.Kind == TableKind.Constructor && TryStored(table, keys, out var stored) && stored != row[^1])
                    PlanUnionCore(stored, row[^1]);
            }

            for (var i = factSubsumes - 1; i >= 0; i--)
            {
                var (subsumed, row) = subsumes[i];
                if (ReferenceEquals(subsumed, table) == false || row.AsSpan(0, keys.Length).SequenceEqual(keys) == false)
                    continue;

                var index = i;
                subsumes.RemoveAt(index);
                var rowKey = Key(table, row);
                subsumeKeys.Remove(rowKey);
                factSubsumes--;
                OnUndo(() =>
                {
                    subsumes.Insert(index, (subsumed, row));
                    subsumeKeys.Add(rowKey);
                    factSubsumes++;
                });
            }

            RemoveValue(functionValues, key);
            RemoveValue(currentValues, key);
            if (unknownValues is not null && unknownValues.Remove(key))
                OnUndo(() => unknownValues.Add(key));
        }

        /// <summary>
        /// Reads a stored row's output at planned keys, none where the batch deletes the row.
        /// </summary>
        bool TryStored(TableHandle table, uint[] keys, out uint output)
        {
            if (removes.Count > 0 && removeKeys.Contains(Key(table, keys)))
            {
                output = 0;
                return false;
            }

            return engine.TryLookup(table.Function, keys, out output);
        }

        /// <summary>
        /// Reads a stored row's output and whether it is subsumed, as <see cref="TryStored(TableHandle, uint[], out uint)"/>
        /// does.
        /// </summary>
        bool TryStored(TableHandle table, uint[] keys, out uint output, out bool subsumed)
        {
            if (removes.Count > 0 && removeKeys.Contains(Key(table, keys)))
            {
                output = 0;
                subsumed = false;
                return false;
            }

            return engine.TryLookup(table.Function, keys, out output, out subsumed);
        }

        /// <summary>
        /// Checks that the scope may delete rows of <paramref name="table"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The table is the graph's contradiction.</exception>
        /// <exception cref="InvalidOperationException">The scope has live descendants or a writable parent, or the table
        /// is a function with a merge that is not a lattice and the scope is not the root.</exception>
        void CheckDeletable(TableHandle table)
        {
            if (ReferenceEquals(table, graph.Contradiction))
                throw new ArgumentException($"'{table.Name}' is the graph's contradiction, which a deletion does not undo.");

            if (engine.RetractionRefusal is { } why)
                throw new InvalidOperationException($"Scope '{scope.Path}' may not delete rows of '{table.Name}': {why}.");

            if (table is FunctionHandle { Merge: { } merge } && scope.IsRoot == false)
                throw new InvalidOperationException(
                    $"'{table.Name}' has the merge '{merge.Name}', which is not a lattice, so only the root may write it, and a deletion below the root hides the row in a layer of the scope.");
        }

        /// <summary>
        /// Records a removal once.
        /// </summary>
        void AddRemove(TableHandle table, uint[] key)
        {
            if (removeKeys.Add(Key(table, key)))
                removes.Add((table, key));
        }

    }

}
