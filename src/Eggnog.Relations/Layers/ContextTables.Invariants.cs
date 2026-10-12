using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// Adds to <paramref name="violations"/> every violation of the tables invariants of the clean context
        /// (colored-contexts spec §12.3, §12.7 item 2): I12–I19 over what the context holds and hides, the tables part of
        /// I10, and every own layer adopted with nothing staged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The checks cost in proportion to the context, not to its ancestors, so DEBUG builds can run them at the end of
        /// every rebuild; ancestors' own invariants are checked by their own checkers, and the context's view of an
        /// ancestor row only ever loses rows to hides (I20). In order:
        /// </para>
        /// <list type="bullet">
        /// <item><description>I18: no hidden set awaits a seal, every set is sealed, and every hidden row lies below its
        /// layer's physical count;</description></item>
        /// <item><description>I34: every own layer is adopted and has no staged write;</description></item>
        /// <item><description>I19: each own layer's timestamp column is nondecreasing and every live own row carries a
        /// timestamp of at least <see cref="Context.CreatedAt"/>;</description></item>
        /// <item><description>I13: every id cell of every live own row is canonical in the context; no visible ancestor row
        /// names one of the context's own displacements (through the ancestors' rebuild indexes); and every visible row an
        /// ancestor appended since the context's creation is canonical in it;</description></item>
        /// <item><description>I12: no ancestor row with the key of a live own row is visible (an ancestor's own rows are
        /// unique in its view, and the context's view of them only shrinks);</description></item>
        /// <item><description>I14: every hidden row is invisible to the parent, or the context retracted rows of the table
        /// or cleared it, or else a visible row carries its information: the row's key made canonical is visible, with an
        /// equal output class for a constructor, and the table's merge adds nothing for any other function
        /// (<see cref="CarrierHolds"/>);</description></item>
        /// <item><description>I15 (Lemma F): no row with a hidden row's key is visible below its layer;</description></item>
        /// <item><description>I16: every hidden set is of its layer's generation; I17: every cursor equals its ancestor's
        /// layer versions and change stamp, and the settled ancestors are frozen;</description></item>
        /// <item><description>I10: no ancestor layer's rebuild index lists an id of the context's blocks.</description></item>
        /// </list>
        /// <para>
        /// Cost: O(own rows × depth + hidden rows × depth + own displacements × depth + rows appended by ancestors since
        /// the context's creation + reserved local ids × tables × depth) probes; the rebuild indexes of ancestors that are
        /// not frozen are refreshed (they share the context's thread domain and are idle).
        /// </para>
        /// </remarks>
        /// <param name="violations">Receives one line per violation.</param>
        internal void CheckInvariants(List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(violations);
            if (unsealed.Count != 0)
                Context.Report(violations, $"I18: {unsealed.Count} hidden set(s) await a seal outside a merge");

            foreach (var layer in Volatile.Read(ref slots))
                if (layer is not null)
                    CheckLayer(violations, layer);

            CheckAncestorRows(violations);
            CheckCursors(violations);
            CheckLocalIdsAbsent(violations);
        }

        /// <summary>
        /// The checks of one own layer: I34, I19, I13 and I12 over its rows, and I18, I16, I14 and I15 over its hides.
        /// </summary>
        void CheckLayer(List<string> violations, TableLayer layer)
        {
            var table = layer.Id;
            var s = layer.Schema;
            var own = layer.Own;
            var find = owner.UnionFind;
            if (layer.IsAdopted == false)
                Context.Report(violations, $"I34: the layer of table {table} is not adopted outside a search");

            if (own.HasPendingWrites)
                Context.Report(violations, $"I34: the layer of table {table} has staged writes outside a merge");

            // Own rows: I19, I13, I12.
            var rows = own.Rows;
            var floor = ClearedFloor(table);
            var previous = 0u;
            for (var r = 0; r < rows.Count; r++)
            {
                var cells = rows.GetRow(r);
                if (s.SortColumn >= 0)
                {
                    var stamp = cells[s.SortColumn];
                    if (stamp < previous)
                        Context.Report(violations, $"I19: own row {r} of table {table} has timestamp {stamp} below the previous row's {previous}");

                    previous = Math.Max(previous, stamp);
                    if (rows.IsStale(r) == false && stamp < owner.CreatedAt)
                        Context.Report(violations, $"I19: own row {r} of table {table} has timestamp {stamp} below the context's creation stamp {owner.CreatedAt}");
                }

                if (rows.IsStale(r))
                    continue;

                foreach (var column in s.RebuildColumns)
                {
                    var id = cells[column.Rep];
                    if (find.Find(id) != id)
                        Context.Report(violations, $"I13: own row {r} of table {table} names {id} in column {column.Rep}, which is {find.Find(id)} here");
                }

                if (s.IsContextPrivate)
                    continue;

                for (var j = floor; j < depth; j++)
                {
                    if (AncestorLayer(j, table) is not { } ancestor)
                        continue;

                    var found = ancestor.FindRow(rows.Data, r * own.Arity);
                    if (found >= 0 && HiddenBetween(table, j, found) == false)
                        Context.Report(violations, $"I12: own row {r} of table {table} and row {found} of the layer at depth {j} both hold key [{string.Join(",", cells[..s.KeyCount].ToArray())}]");
                }
            }

            // Hides: I18, I16, I14, I15.
            List<int>? hidden = null;
            for (var j = 0; j < depth; j++)
            {
                if (layer.HiddenOver(j) is not { } set)
                    continue;

                if (AncestorLayer(j, table) is not { } ancestor)
                {
                    Context.Report(violations, $"I18: table {table} hides rows of the layer at depth {j}, which does not exist");
                    continue;
                }

                if (set.IsSealed == false)
                    Context.Report(violations, $"I18: the hidden set of table {table} over depth {j} is not sealed");

                if (set.Generation != ancestor.Version.Major)
                    Context.Report(violations, $"I16: the hidden set of table {table} over depth {j} is of generation {set.Generation}, the layer of {ancestor.Version.Major}");

                (hidden ??= new List<int>()).Clear();
                set.CopyTo(hidden);
                foreach (var r in hidden)
                {
                    if ((uint)r >= (uint)ancestor.Rows.Count)
                    {
                        Context.Report(violations, $"I18: table {table} hides row {r} of the layer at depth {j}, which has {ancestor.Rows.Count} rows");
                        continue;
                    }

                    CheckHiddenRow(violations, layer, ancestor, j, r);
                }
            }
        }

        /// <summary>
        /// I14 and I15 for row <paramref name="row"/> of the ancestor layer at depth <paramref name="j"/>, which
        /// <paramref name="layer"/> hides.
        /// </summary>
        void CheckHiddenRow(List<string> violations, TableLayer layer, SortedWritesTable ancestor, int j, int row)
        {
            var table = layer.Id;
            var s = layer.Schema;
            var arity = s.Arity;
            var data = ancestor.Rows.Data;
            var offset = row * arity;

            // I15: no row with the hidden row's key is visible below its layer.
            for (var below = ClearedFloor(table); below < j; below++)
            {
                if (AncestorLayer(below, table) is not { } lower)
                    continue;

                var found = lower.FindRow(data, offset);
                if (found >= 0 && HiddenBetween(table, below, found) == false)
                    Context.Report(violations, $"I15: row {row} of table {table} at depth {j} is hidden, yet row {found} with its key is visible at depth {below}");
            }

            // I14: invisible to the parent, retracted or cleared, or carried by a visible row.
            if (data[offset] == Value.StaleRep || layer.HasRetractions || layer.IsCleared || HiddenAtOrBelow(table, j, row, depth - 1) || j < ClearedFloorAtOrBelow(table, depth - 1))
                return;

            var copy = new uint[arity];
            Array.Copy(data, offset, copy, 0, arity);
            var find = owner.UnionFind;
            foreach (var column in s.RebuildColumns)
                copy[column.Rep] = find.Find(copy[column.Rep]);

            var keyHash = TableKeyHash.Hash(copy, 0, s.KeyCount);
            if (TryFindVisible(table, copy, 0, keyHash, depth, out var carrierLayer, out _, out var carrierRow) == false)
            {
                Context.Report(violations, $"I14: row {row} of table {table} at depth {j} is hidden ({layer.ReasonFor(j, row)?.ToString() ?? "no reason"}), and no visible row holds its canonical key");
                return;
            }

            if (CarrierHolds(s, carrierLayer.Rows.GetRow(carrierRow), copy) == false)
                Context.Report(violations, $"I14: row {row} of table {table} at depth {j} is hidden ({layer.ReasonFor(j, row)?.ToString() ?? "no reason"}), and the visible row with its key does not carry its information");
        }

        /// <summary>
        /// Gets whether the visible row <paramref name="carrier"/> carries the information of the hidden row made
        /// canonical, <paramref name="hidden"/> (I14): for a constructor, both outputs name one class; for a relation, the
        /// keys are equal, which the lookup established; for any other function, the table's merge function adds nothing
        /// when the hidden row is merged into the carrier (run on a scratch catalog; a merge that needs more than its two
        /// rows is not checked).
        /// </summary>
        bool CarrierHolds(LayeredTableSchema s, ReadOnlySpan<uint> carrier, uint[] hidden)
        {
            var merge = s.Root.MergeFunction;
            if (merge is UnionIdMerge union)
                return owner.UnionFind.Find(carrier[union.ValueColumn]) == owner.UnionFind.Find(hidden[union.ValueColumn]);

            var values = s.Arity - s.KeyCount - (s.SortColumn >= 0 ? 1 : 0);
            if (values <= 0)
                return true;

            if (s.SortColumn >= 0)
                hidden[s.SortColumn] = carrier[s.SortColumn];

            try
            {
                var output = new uint[s.Arity];
                return merge.Merge(new ExecutionState(new TableCatalog()), carrier, hidden, output) == false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// I13 for ancestor rows: no visible ancestor row names an own displacement of the context, and every visible row
        /// an ancestor appended since the context's creation is canonical in it.
        /// </summary>
        void CheckAncestorRows(List<string> violations)
        {
            var journal = owner.Journal!;
            var blocks = owner.IdBlocks!;
            var find = owner.UnionFind;
            foreach (var table in schema.FunctionTables)
            {
                if (schema[table] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                var floor = ClearedFloor(table);
                for (var j = floor; j < depth; j++)
                {
                    if (AncestorLayer(j, table) is not { } layer)
                        continue;

                    // Own displacements, through the layer's rebuild index.
                    var index = layer.SharedRebuildIndex()!.Data;
                    foreach (var id in journal.Ids)
                    {
                        if (blocks.Contains(id) || owner.IsOwnDisplacement(id) == false || index.TryGetSubset(id, out var rows) == false)
                            continue;

                        foreach (var r in rows)
                            if (layer.Rows.IsStale(r) == false && HiddenBetween(table, j, r) == false)
                                Context.Report(violations, $"I13: row {r} of table {table} at depth {j} is visible and names {id}, which the context displaced");
                    }

                    // Rows appended since the context's creation.
                    if (j < settledAtCreation || creationRows[j] is not { } atCreation)
                        continue;

                    var start = (uint)table.Index < (uint)atCreation.Length && atCreation[table.Index].Major == layer.Version.Major ? atCreation[table.Index].Minor : 0;
                    for (var r = start; r < layer.Rows.Count; r++)
                    {
                        if (layer.Rows.IsStale(r) || HiddenBetween(table, j, r))
                            continue;

                        var cells = layer.Rows.GetRow(r);
                        foreach (var column in s.RebuildColumns)
                        {
                            var id = cells[column.Rep];
                            if (find.Find(id) != id)
                                Context.Report(violations, $"I13: row {r} of table {table} at depth {j}, appended after the context was created, is visible and names {id}, which is {find.Find(id)} here");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// I16 and I17: the settled ancestors are frozen, and every cursor equals its ancestor's layer versions and change
        /// stamp, as after every rebuild.
        /// </summary>
        void CheckCursors(List<string> violations)
        {
            for (var a = 0; a < settled; a++)
                if (chain[a].IsFrozen == false)
                    Context.Report(violations, $"I17: the ancestor at depth {a} is settled but not frozen");

            for (var a = settled; a < depth; a++)
            {
                if (cursors[a] is not { } cursor)
                {
                    Context.Report(violations, $"I17: the unsettled ancestor at depth {a} has no cursor");
                    continue;
                }

                if (cursor.Stamp != chain[a].Database.ChangeStamp)
                    Context.Report(violations, $"I17: the cursor into depth {a} is at stamp {cursor.Stamp}, the ancestor at {chain[a].Database.ChangeStamp}");

                foreach (var table in schema.FunctionTables)
                {
                    if (schema[table] is not { IsContextPrivate: false })
                        continue;

                    var expected = AncestorLayer(a, table)?.Version ?? default;
                    var seen = cursor.RowsOrDefault(table);
                    if (seen.Major != expected.Major)
                        Context.Report(violations, $"I16: the cursor into table {table} at depth {a} is of generation {seen.Major}, the layer of {expected.Major}");
                    else if (seen.Minor != expected.Minor)
                        Context.Report(violations, $"I17: the cursor into table {table} at depth {a} is at row {seen.Minor}, the layer has {expected.Minor}");
                }
            }
        }

        /// <summary>
        /// The tables part of I10: no ancestor layer's rebuild index lists an id of the context's blocks.
        /// </summary>
        void CheckLocalIdsAbsent(List<string> violations)
        {
            var blocks = owner.IdBlocks!;
            if (blocks.BlockCount == 0)
                return;

            foreach (var table in schema.FunctionTables)
            {
                if (schema[table] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                for (var j = 0; j < depth; j++)
                {
                    if (AncestorLayer(j, table) is not { } layer)
                        continue;

                    var index = layer.SharedRebuildIndex()!.Data;
                    for (var b = 0; b < blocks.BlockCount; b++)
                        for (var id = blocks.BlockStart(b); id < blocks.BlockEnd(b); id++)
                            if (index.TryGetSubset((uint)id, out _))
                                Context.Report(violations, $"I10: the layer of table {table} at depth {j} names {id}, local to the context at depth {depth}");
                }
            }
        }

        /// <summary>
        /// Gets whether some level in <c>(layer, top]</c> hides row <paramref name="row"/> of the layer at depth
        /// <paramref name="layer"/>: <see cref="HiddenBetween"/> stopped at <paramref name="top"/>.
        /// </summary>
        bool HiddenAtOrBelow(TableId table, int layer, int row, int top)
        {
            for (var level = layer + 1; level <= top; level++)
                if (LayerAt(level, table) is { } l && l.IsHiddenHere(layer, row))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets the deepest level in <c>(0, top]</c> that cleared <paramref name="table"/>, or 0: <see cref="ClearedFloor"/>
        /// as seen from depth <paramref name="top"/>.
        /// </summary>
        int ClearedFloorAtOrBelow(TableId table, int top)
        {
            for (var level = top; level > 0; level--)
                if (LayerAt(level, table) is { IsCleared: true })
                    return level;

            return 0;
        }

    }

}
