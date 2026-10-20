using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // egglog's top-level delete. A delete is a batch of deletes only (Scope.Delete), planned against what the scope
        // holds: a key's terms are evaluated as egglog's top-level action evaluates them (a missing constructor term is
        // made, a function read must find a value), a row that does not exist plans nothing, and the table's merge
        // deletes before it inserts. A scope deletes only when the engine's retraction guard lets it (no live
        // descendant, no writable parent), which the batch checks before it plans anything. A deleted row of a table
        // that the scope reads under its own tag is deleted from that mirror too, so the mirror stays equal to the
        // table, as a scoped read of the scope's own tag must be.

        readonly List<(TableHandle Table, uint[] Key)> removes = new();
        readonly HashSet<string> removeKeys = new(StringComparer.Ordinal);

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
            var shape = term.Shape;
            if (shape.Kind != TermShapeKind.Application)
                throw new ArgumentException($"Delete takes a function or constructor application; {term} is not one", nameof(term));

            PlanDelete(shape.Table!, shape.Arguments, nameof(term));
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
            if (fact is not RelationFact relation)
            {
                throw fact is ScopedFact scoped
                    ? new ArgumentException($"host writes go to the scope written; Fact.In('{scoped.Scope.Name}') is read-only", nameof(fact))
                    : new ArgumentException($"Delete(rows) takes relation rows, r.Of(...); {fact} is not one (delete an application with Delete(term))", nameof(fact));
            }

            PlanDelete(relation.Table, relation.Arguments, nameof(fact));
        }

        /// <summary>
        /// Plans the deletion of the row of <paramref name="arguments"/> from <paramref name="table"/>, and from the
        /// mirror of the table under the scope's own tag.
        /// </summary>
        void PlanDelete(TableHandle table, Term[] arguments, string parameter)
        {
            CheckTable(table);
            CheckDeletable(table);
            var keys = PlanKeys(table, arguments, out var existing);
            if (existing == false || engine.TryLookup(table.Function, keys, out _) == false)
                return;

            AddRemove(table, keys);
            if (scope.Tag is { } tag && graph.Mirrors.TryGet(table, tag, out var mirror) && engine.TryLookup(mirror.Handle.Function, keys, out _))
                AddRemove(mirror.Handle, keys);
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
