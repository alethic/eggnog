using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        /// <summary>
        /// The writes of primitives over the tables held back while a fact is planned (<see cref="PlanFactUnions"/>).
        /// </summary>
        List<Action>? factWrites;

        /// <summary>
        /// The number of new terms planned before the fact being planned: the later ones are the fact's own, which a
        /// primitive's read does not see, as egglog's lookup does not see the rows its command stages.
        /// </summary>
        int factTerms;

        /// <summary>
        /// Reads a function's value, or a constructor's class, at a key for a primitive of a host write, egglog's
        /// <c>lookup</c> in a top-level action: the value the batch's earlier facts left, else the stored one, never a
        /// row the fact being planned stages; never interning a key.
        /// </summary>
        /// <exception cref="ArgumentException">The table is a relation or not of the batch's graph, or a value is not of
        /// its column's type or a class not usable here.</exception>
        /// <exception cref="InvalidOperationException">The table's keys are classes, and the batch merged classes
        /// before the read.</exception>
        internal bool TryLookupForPrimitive<T>(TableHandle table, ReadOnlySpan<object> keys, out T value)
        {
            value = default!;
            CheckTable(table);
            if (table.IsRelation)
                throw new ArgumentException($"'{table.Name}' is a relation, which has no value to read; use Contains.", nameof(table));

            var output = table.Output!;
            if (output.ClrType != typeof(T))
                throw new ArgumentException($"'{table.Name}' outputs {output.Name}, not {typeof(T).Name}.", nameof(table));

            if (TryReadCell(table, keys, out var cell) == false)
                return false;

            value = (T)DecodeForPrimitive(output, cell);
            return true;
        }

        /// <summary>
        /// Gets whether a relation holds a row, the batch's or a stored one, for a primitive of a host write.
        /// </summary>
        /// <exception cref="ArgumentException">The relation is not of the batch's graph, or a value is not of its column's
        /// type or a class not usable here.</exception>
        /// <exception cref="InvalidOperationException">The relation holds classes, and the batch merged classes before
        /// the read.</exception>
        internal bool ContainsForPrimitive(RelationHandle relation, ReadOnlySpan<object> keys)
        {
            CheckTable(relation);
            CheckReadAfterMerge(relation);
            if (TryReadKeys(relation, keys, out var cells, out var existing) == false)
                return false;

            var row = new uint[cells.Length + 1];
            cells.CopyTo(row, 0);
            row[^1] = graph.Unit.Unit;
            var rowKey = Key(relation, row);
            if (rowKeys.Contains(rowKey) || subsumeKeys.Contains(rowKey))
                return true;

            return existing && TryStored(relation, cells, out _);
        }

        /// <summary>
        /// Gets the number of rows of a table for a primitive of a host write: the stored rows, and those the batch's
        /// earlier facts add or subsume at keys no stored row has, less those it deletes.
        /// </summary>
        /// <exception cref="ArgumentException">The table is not of the batch's graph.</exception>
        /// <exception cref="InvalidOperationException">The table's keys are classes, and the batch merged classes before
        /// the read.</exception>
        internal long TableSizeForPrimitive(TableHandle table)
        {
            CheckTable(table);
            CheckReadAfterMerge(table);
            var count = engine.TableSize(table.Function);
            var added = new HashSet<string>(StringComparer.Ordinal);
            var keyCount = table.KeyArray.Length;
            foreach (var (planned, row) in rows)
                count += Adds(planned, row);

            foreach (var (planned, row) in subsumes)
                count += Adds(planned, row);

            for (var i = 0; i < factTerms; i++)
                if (ReferenceEquals(pending[i].Head, table))
                    count++;

            foreach (var (removed, _) in removes)
                if (ReferenceEquals(removed, table))
                    count--;

            return count;

            // One for a row of the table at a key no stored row and no earlier row of the batch has.
            int Adds(TableHandle planned, uint[] row)
            {
                if (ReferenceEquals(planned, table) == false)
                    return 0;

                var keys = row.AsSpan(0, keyCount).ToArray();
                return added.Add(Key(table, keys)) && (Fresh(keys) || TryStored(table, keys, out _) == false) ? 1 : 0;
            }
        }

        /// <summary>
        /// Gets or makes the class of a constructor term for a primitive of a host write, egglog's lookup of a constructor
        /// in an action, which mints a fresh class on a miss; usable in the batch's later calls.
        /// </summary>
        /// <exception cref="ArgumentException">The constructor, or a value, is not one the batch may write.</exception>
        /// <exception cref="InvalidOperationException">The batch merged classes before the call.</exception>
        internal TSort MakeForPrimitive<TSort>(ConstructorHandle constructor, ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>
        {
            // egglog's next command would see the merge's classes; the batch rebuilds before a fact with a primitive over
            // the tables (RebuildFor), but for a rejected batch.
            if (mergedBeforeFact)
                throw new InvalidOperationException(
                    $"A primitive makes a '{constructor.Name}' term after this batch merged classes; make it in a later call, which sees the merge as egglog's next command does.");

            if (constructor.Output!.ClrType != typeof(TSort))
                throw new ArgumentException($"Constructor '{constructor.Name}' makes {constructor.Output.Name}, not {typeof(TSort).Name}.", nameof(constructor));

            var cell = PlanTermCore(new UntypedApplicationTerm(constructor, constructor.ToArguments(keys)));
            return TSort.FromId(new ClassId(cell, Origin));
        }

        /// <summary>
        /// Checks that a primitive of a host write may write a table: one of the batch's graph, not a scoped view, and
        /// below the root no function whose merge is not a lattice.
        /// </summary>
        /// <exception cref="ArgumentException">It may not.</exception>
        internal void CheckWritableForPrimitive(TableHandle table)
        {
            CheckTable(table);
            if (scope.IsRoot == false && table is FunctionHandle { Merge: { } merge })
                throw new ArgumentException(
                    $"'{table.Name}' has the merge '{merge.Name}', which is not a lattice, so only the root may write it.", nameof(table));
        }

        /// <summary>
        /// Reads the row a primitive of a host write subsumes, when it calls, as egglog's subsume looks it up: what the
        /// batch's earlier facts left, else the stored row; and gets the subsumption to plan at the end of the fact, the
        /// row as read made subsumed, or nothing for a row subsumed already.
        /// </summary>
        /// <returns>Plans the subsumption.</returns>
        /// <exception cref="ArgumentException">The table is not of the batch's graph, is a scoped view or is not
        /// subsumable, or a value is not of its column's type or a class not usable here.</exception>
        /// <exception cref="InvalidOperationException">The table holds no row for the keys, or holds classes and the
        /// batch merged classes before the call.</exception>
        internal Action SubsumeForPrimitive(TableHandle table, ReadOnlySpan<object> keys)
        {
            CheckTable(table);
            CheckSubsumable(table, nameof(table));
            CheckReadAfterMerge(table);
            if (TryReadKeys(table, keys, out var cells, out var existing))
            {
                var key = Key(table, cells);
                if (table.IsRelation)
                {
                    var row = Row(cells, graph.Unit.Unit);
                    var rowKey = Key(table, row);
                    if (subsumeKeys.Contains(rowKey))
                        return static () => { };

                    if (rowKeys.Contains(rowKey))
                        return () => AddSubsume(table, row);

                    if (existing && TryStored(table, cells, out _, out var held))
                        return held ? static () => { } : () => AddSubsume(table, row);
                }
                else if (pendingByKey.TryGetValue(key, out var planned) && OfThisFact(planned) == false)
                {
                    var constructor = (ConstructorHandle)table;
                    return subsumedTerms.Contains(key) ? static () => { } : () => SubsumeTerm(constructor, cells, key, planned.Id);
                }
                else if (existing && TryStored(table, cells, out var output, out var held))
                {
                    var row = Row(cells, output);
                    return held || subsumeKeys.Contains(Key(table, row)) ? static () => { } : () => AddSubsume(table, row);
                }
            }

            factPanicked = true;
            throw new InvalidOperationException($"'{table.Name}' holds no row for the keys, which a subsumption needs, as egglog's subsume does.");
        }

        /// <summary>
        /// Holds back a write of a primitive of a host write until the end of the fact, as egglog stages a command's
        /// writes: a relation's row, a function's value or a subsumption, planned then as the fact or the term it is.
        /// </summary>
        /// <param name="write">Plans the write.</param>
        internal void HoldWrite(Action write) => (factWrites ??= new List<Action>()).Add(write);

        /// <summary>
        /// Holds back a union of two classes of a primitive of a host write until the end of the fact.
        /// </summary>
        /// <exception cref="ArgumentException">A class is not usable here.</exception>
        internal void UnionForPrimitive<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            var x = Canonical(ClassChecks.Require(scope, a.Id, nameof(a), Origin));
            var y = Canonical(ClassChecks.Require(scope, b.Id, nameof(b), Origin));
            CollectUnion(x, y);
        }

        /// <summary>
        /// Encodes the result of a primitive of a host write as the batch encodes a constant: a class, or the classes a
        /// container holds, usable in the batch, made by it included, and canonical.
        /// </summary>
        internal uint EncodeForPrimitive(ColumnType type, object value)
        {
            if (type.Kind == ColumnKind.Sort)
                return Canonical(ClassChecks.Require(scope, type.ClassOf(value), "result", Origin));

            if (type.HoldsClasses)
                return type.EncodeCanonicalObject(value, scope, canonical ??= Canonical, Origin);

            return type.EncodeObject(value);
        }

        /// <summary>
        /// Plans a fact a primitive of a host write held back, at the end of the fact.
        /// </summary>
        internal void PlanHeldFact(Fact fact) => PlanFactCore(fact);

        /// <summary>
        /// Checks that a primitive of a host write may delete a row of a table, as <see cref="CheckDeletable"/> does.
        /// </summary>
        /// <exception cref="ArgumentException">The table is not of the batch's graph, is a scoped view, or is the graph's
        /// contradiction.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete.</exception>
        internal void CheckRemovableForPrimitive(TableHandle table)
        {
            CheckTable(table);
            CheckDeletable(table);
        }

        /// <summary>
        /// Reads the output cell of a function or constructor at a key for a primitive: what the batch's earlier facts
        /// left, else the stored row, but no row the fact being planned stages and none the batch deletes.
        /// </summary>
        bool TryReadCell(TableHandle table, ReadOnlySpan<object> keys, out uint cell)
        {
            cell = 0;
            if (TryReadKeys(table, keys, out var cells, out var existing) == false)
                return false;

            return TryReadCells(table, cells, existing, out cell);
        }

        /// <summary>
        /// Reads the output cell of a function at keys' cells for a function value a host write applies
        /// (<see cref="FnApply"/>), as <see cref="TryReadCell"/> reads it: the class canonical.
        /// </summary>
        /// <exception cref="ArgumentException">The function is not of the batch's graph.</exception>
        /// <exception cref="InvalidOperationException">The table holds classes, and the batch merged classes before the
        /// read.</exception>
        internal bool TryReadCellsForPrimitive(FunctionHandle table, ReadOnlySpan<uint> keys, out uint value)
        {
            CheckTable(table);
            var (cells, existing) = CanonicalKeys(table, keys);
            if (TryReadCells(table, cells, existing, out value) == false)
                return false;

            if (table.Columns[^1].InvolvesClasses)
                value = Canonical(value);

            return true;
        }

        /// <summary>
        /// Reads the output of a function's row at keys for a function value a host write applies, as a value; never
        /// interning a key.
        /// </summary>
        /// <exception cref="ArgumentException">The function is not of the batch's graph, or a value is not of its column's
        /// type or a class not usable here.</exception>
        internal bool TryLookupObjectForPrimitive(FunctionHandle table, ReadOnlySpan<object> keys, out object? value)
        {
            value = null;
            CheckTable(table);
            if (TryReadKeys(table, keys, out var cells, out var existing) == false || TryReadCells(table, cells, existing, out var cell) == false)
                return false;

            value = DecodeForPrimitive(table.Output!, cell);
            return true;
        }

        /// <summary>
        /// Gets or makes the class of a constructor's term at keys' cells for a function value a host write applies, as
        /// <see cref="MakeForPrimitive"/> does: egglog's <c>lookup_or_insert</c>.
        /// </summary>
        /// <exception cref="ArgumentException">The constructor is not of the batch's graph.</exception>
        /// <exception cref="InvalidOperationException">The batch merged classes before the call.</exception>
        internal uint MakeCellsForPrimitive(ConstructorHandle constructor, ReadOnlySpan<uint> keys)
        {
            // As MakeForPrimitive: the batch rebuilds before a fact with a function value's application (RebuildFor), but
            // for a rejected batch.
            if (mergedBeforeFact)
                throw new InvalidOperationException(
                    $"A function value makes a '{constructor.Name}' term after this batch merged classes; make it in a later call, which sees the merge as egglog's next command does.");

            CheckTable(constructor);
            var (cells, existing) = CanonicalKeys(constructor, keys);
            if (existing && TryStored(constructor, cells, out var stored))
                return stored;

            return PlanNewTerm(constructor, cells, Key(constructor, cells));
        }

        /// <summary>
        /// Holds back a union of two class cells of a write primitive a function value applies, as
        /// <see cref="CollectUnion"/> does.
        /// </summary>
        internal void HoldUnion(uint a, uint b) => CollectUnion(a, b);

        /// <summary>
        /// Gets keys' cells canonical, a class the batch made as it is, and whether no key is a class the batch made.
        /// </summary>
        (uint[] Cells, bool Existing) CanonicalKeys(TableHandle table, ReadOnlySpan<uint> keys)
        {
            var columns = table.KeyArray;
            if (keys.Length != columns.Length)
                throw new ArgumentException($"'{table.Name}' takes {columns.Length} keys, not {keys.Length}.", nameof(keys));

            var cells = keys.ToArray();
            var existing = true;
            for (var i = 0; i < cells.Length; i++)
            {
                if (columns[i].Kind != ColumnKind.Sort)
                    continue;

                cells[i] = Canonical(cells[i]);
                existing &= fresh.Contains(cells[i]) == false;
            }

            return (cells, existing);
        }

        /// <summary>
        /// Reads the output cell of a function's or a constructor's row at keys' cells: what the batch's earlier facts
        /// left, else the stored row, but no row the fact being planned stages and none the batch deletes.
        /// </summary>
        bool TryReadCells(TableHandle table, uint[] cells, bool existing, out uint cell)
        {
            cell = 0;
            if (table.Kind == TableKind.Function)
                return TryReadFunction(table, cells, existing, out cell);

            CheckReadAfterMerge(table);
            var key = Key(table, cells);
            if (pendingByKey.TryGetValue(key, out var planned))
            {
                if (OfThisFact(planned))
                    return false;

                cell = planned.Id;
                return true;
            }

            return existing && TryStored(table, cells, out cell);
        }

        /// <summary>
        /// Gets the cells of a read's keys without interning one: <c>false</c> when a value was never interned or a
        /// container of classes was never made, which no row holds.
        /// </summary>
        /// <param name="table">The table read.</param>
        /// <param name="keys">One value per key column.</param>
        /// <param name="cells">The keys' cells, when the result is <c>true</c>.</param>
        /// <param name="existing">Whether no key is a class the batch made.</param>
        bool TryReadKeys(TableHandle table, ReadOnlySpan<object> keys, out uint[] cells, out bool existing)
        {
            var columns = table.KeyArray;
            if (keys.Length != columns.Length)
                throw new ArgumentException($"'{table.Name}' takes {columns.Length} keys, not {keys.Length}.", nameof(keys));

            cells = new uint[columns.Length];
            existing = true;
            for (var i = 0; i < columns.Length; i++)
            {
                var key = keys[i] ?? throw new ArgumentNullException(nameof(keys), $"Key {i} is null.");
                var type = columns[i];
                if (type.ClrType.IsInstanceOfType(key) == false)
                    throw new ArgumentException($"A {ValueStore.DisplayName(key.GetType())} is not a {type.Name}.", nameof(keys));

                if (type.Kind == ColumnKind.Sort)
                {
                    cells[i] = Canonical(ClassChecks.Require(scope, type.ClassOf(key), nameof(keys), Origin));
                    existing &= fresh.Contains(cells[i]) == false;
                }
                else if (type.HoldsClasses)
                {
                    if (type.TryEncodeCanonicalObject(key, scope, canonical ??= Canonical, out cells[i]) == false)
                        return false;
                }
                else if (type.TryEncodeExistingObject(key, scope.ContainerView, out cells[i]) == false)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a function's value as <see cref="ReadFunction"/> does, with no result where it has none.
        /// </summary>
        bool TryReadFunction(TableHandle table, uint[] keys, bool existing, out uint value)
        {
            CheckReadAfterMerge(table);
            var key = Key(table, keys);
            if (unknownValues is not null && unknownValues.Contains(key))
                throw new InvalidOperationException(
                    $"'{table.Name}' is read after this batch wrote its key, and its merge, {((FunctionHandle)table).Merge!.Name}, is an expression only the engine evaluates; read it in a later call.");

            if (currentValues.TryGetValue(key, out value))
                return true;

            return existing && TryStored(table, keys, out value);
        }

        /// <summary>
        /// Refuses a primitive's read of a table that holds classes after the batch merged classes: egglog's next command
        /// would read it after the merge's rebuild, which merges its rows whose keys become equal and makes its classes
        /// canonical, and the batch stages its merges after its writes.
        /// </summary>
        /// <exception cref="InvalidOperationException">The batch merged classes, and the table holds
        /// classes.</exception>
        void CheckReadAfterMerge(TableHandle table)
        {
            if (mergedBeforeFact && (HasSortKey(table) || table.Output is { InvolvesClasses: true }))
                throw new InvalidOperationException(
                    $"'{table.Name}' is read after this batch merged classes, and it holds classes; read it in a later call, which sees the merge as egglog's next command does.");
        }

        /// <summary>
        /// Gets whether a new term is one the fact being planned makes.
        /// </summary>
        bool OfThisFact(PendingTerm term)
        {
            for (var i = factTerms; i < pending.Count; i++)
                if (ReferenceEquals(pending[i], term))
                    return true;

            return false;
        }

        /// <summary>
        /// Decodes a value a primitive of a host write reads: a class with the batch's origin, usable in its later calls.
        /// </summary>
        object DecodeForPrimitive(ColumnType type, uint cell) =>
            type.Kind == ColumnKind.Sort ? type.Wrap(new ClassId(Canonical(cell), Origin)) : type.DecodeObject(cell, scope);

        /// <summary>
        /// Gets whether a key holds a class the batch made.
        /// </summary>
        bool Fresh(uint[] keys)
        {
            foreach (var key in keys)
                if (fresh.Contains(key))
                    return true;

            return false;
        }

    }

}
