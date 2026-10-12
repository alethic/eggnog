using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // The congruence check of merges (D32). A union of the batch, or a constructor write that gives an existing key
        // another class, merges classes; the rebuild then merges every pair of rows whose keys become equal, and for a
        // function without a lattice two such rows with different values are an engine panic, which would fault the
        // scope. Phase 1 therefore simulates the closure the rebuild will compute: an overlay union-find over the cells
        // of the batch (canonical or fresh), and a signature table of the rows whose keys a merge can change, stored and
        // planned. Two rows of a constructor that meet merge their outputs, which cascades; two rows of a function
        // without a lattice that meet with different values are a FactConflictException, before anything is staged.
        //
        // The check starts at the batch's first merge, and only when the graph has a function without a lattice keyed by
        // a sort; otherwise no merge can conflict, and none of this state exists. It then reads every row of the tables
        // whose keys a merge can change (those functions, and the constructors whose outputs can reach their keys): an
        // O(rows of those tables) scan, once per batch that merges.

        List<(TableHandle Table, uint[] Key, uint Output, bool Stored)>? mergeRows;
        HashSet<TableHandle>? congruent;
        Dictionary<string, int>? signatures;
        Dictionary<uint, uint>? overlay;
        Dictionary<uint, List<int>>? uses;
        Queue<(uint A, uint B)>? merges;
        bool mergeCheckDecided;

        /// <summary>
        /// Records that the batch merges two classes (canonical or fresh cells), starting the congruence check at the
        /// first merge, and runs the closure.
        /// </summary>
        /// <exception cref="FactConflictException">The merge would give a function without a lattice two values for one
        /// key.</exception>
        void NoteMerge(uint a, uint b)
        {
            if (StartMergeCheck() == false)
                return;

            merges!.Enqueue((a, b));
            Close();
        }

        /// <summary>
        /// Adds a planned row of <paramref name="table"/> (keys then output) to the congruence check, once it runs.
        /// </summary>
        /// <exception cref="FactConflictException">The row meets a row of a function without a lattice that holds another
        /// value.</exception>
        void TrackRow(TableHandle table, uint[] row)
        {
            if (mergeRows is null || congruent!.Contains(table) == false)
                return;

            TrackRow(table, row[..^1], row[^1]);
        }

        /// <summary>
        /// Adds a planned row of <paramref name="table"/> to the congruence check, once it runs.
        /// </summary>
        /// <exception cref="FactConflictException">The row meets a row of a function without a lattice that holds another
        /// value.</exception>
        void TrackRow(TableHandle table, uint[] key, uint output)
        {
            if (mergeRows is null || congruent!.Contains(table) == false)
                return;

            AddMergeRow(table, key, output, false);
            Close();
        }

        /// <summary>
        /// Starts the congruence check if the graph needs it: indexes the stored rows of the tables whose keys a merge can
        /// change, then the rows the batch planned before its first merge.
        /// </summary>
        /// <returns>Whether the check runs.</returns>
        bool StartMergeCheck()
        {
            if (mergeRows is not null)
                return true;

            if (mergeCheckDecided)
                return false;

            mergeCheckDecided = true;
            var tables = CongruentTables();
            if (tables.Count == 0)
                return false;

            mergeRows = new List<(TableHandle, uint[], uint, bool)>();
            congruent = new HashSet<TableHandle>(tables, ReferenceEqualityComparer.Instance);
            signatures = new Dictionary<string, int>(System.StringComparer.Ordinal);
            overlay = new Dictionary<uint, uint>();
            uses = new Dictionary<uint, List<int>>();
            merges = new Queue<(uint, uint)>();

            var stored = new List<(TableHandle Table, uint[] Row)>();
            foreach (var table in tables)
            {
                var arity = table.Columns.Length;
                engine.ForEach(table.Function, row =>
                {
                    var copy = new uint[arity];
                    for (var i = 0; i < arity; i++)
                        copy[i] = row.Values[i].Rep;

                    stored.Add((table, copy));
                });
            }

            // The root is rebuilt after every flush and every run, so the stored rows are canonical.
            foreach (var (table, row) in stored)
                AddMergeRow(table, row[..^1], row[^1], true);

            foreach (var term in pending)
                if (congruent.Contains(term.Head))
                    AddMergeRow(term.Head, term.Keys, term.Id, false);

            foreach (var (table, row) in rows)
                if (congruent.Contains(table))
                    AddMergeRow(table, row[..^1], row[^1], false);

            return true;
        }

        /// <summary>
        /// Gets the tables whose keys a merge can change in a way a function without a lattice notices: every function
        /// without a lattice with a sort key, and every constructor with a sort key whose output sort reaches one of
        /// their keys (directly, or through other such constructors). Empty when the graph has no such function.
        /// </summary>
        List<TableHandle> CongruentTables()
        {
            var tables = graph.Tables;
            var result = new List<TableHandle>();
            var sorts = new HashSet<ColumnType>(ReferenceEqualityComparer.Instance);
            foreach (var table in tables)
            {
                if (table is FunctionHandle { Lattice: null } && HasSortKey(table))
                {
                    result.Add(table);
                    AddSortKeys(table, sorts);
                }
            }

            if (result.Count == 0)
                return result;

            var added = new HashSet<TableHandle>(ReferenceEqualityComparer.Instance);
            var grown = true;
            while (grown)
            {
                grown = false;
                foreach (var table in tables)
                {
                    if (table.Kind != TableKind.Constructor || sorts.Contains(table.Output!) == false || HasSortKey(table) == false || added.Add(table) == false)
                        continue;

                    result.Add(table);
                    AddSortKeys(table, sorts);
                    grown = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets whether a table has a sort among its key columns.
        /// </summary>
        static bool HasSortKey(TableHandle table)
        {
            foreach (var column in table.KeyArray)
                if (column.Kind == ColumnKind.Sort)
                    return true;

            return false;
        }

        /// <summary>
        /// Adds the sorts of a table's key columns to <paramref name="sorts"/>.
        /// </summary>
        static void AddSortKeys(TableHandle table, HashSet<ColumnType> sorts)
        {
            foreach (var column in table.KeyArray)
                if (column.Kind == ColumnKind.Sort)
                    sorts.Add(column);
        }

        /// <summary>
        /// Adds a row to the check: indexes it under the classes of its sort keys and places it in the signature table.
        /// </summary>
        void AddMergeRow(TableHandle table, uint[] key, uint output, bool stored)
        {
            var index = mergeRows!.Count;
            mergeRows.Add((table, key, output, stored));
            var columns = table.KeyArray;
            for (var i = 0; i < key.Length; i++)
                if (columns[i].Kind == ColumnKind.Sort)
                    UsesOf(Representative(key[i])).Add(index);

            Place(index);
        }

        /// <summary>
        /// Places a row in the signature table under its key in the overlay's classes; a row already there with that key
        /// meets it.
        /// </summary>
        /// <exception cref="FactConflictException">The rows are of a function without a lattice and hold different
        /// values.</exception>
        void Place(int index)
        {
            var (table, key, _, _) = mergeRows![index];
            var columns = table.KeyArray;
            var canonical = new uint[key.Length];
            for (var i = 0; i < key.Length; i++)
                canonical[i] = columns[i].Kind == ColumnKind.Sort ? Representative(key[i]) : key[i];

            var signature = Key(table, canonical);
            if (signatures!.TryGetValue(signature, out var other))
            {
                if (other != index)
                    Meet(other, index);
            }
            else
            {
                signatures.Add(signature, index);
            }
        }

        /// <summary>
        /// Two rows of one table whose keys are equal in the overlay: a constructor's outputs merge; a function without a
        /// lattice must hold one value.
        /// </summary>
        /// <exception cref="FactConflictException">The function's rows hold different values.</exception>
        void Meet(int first, int second)
        {
            var a = mergeRows![first];
            var b = mergeRows[second];
            if (a.Table.Kind == TableKind.Constructor)
            {
                if (Representative(a.Output) != Representative(b.Output))
                    merges!.Enqueue((a.Output, b.Output));

                return;
            }

            if (a.Output == b.Output)
                return;

            var (held, incoming) = a.Stored || b.Stored == false ? (a, b) : (b, a);
            var output = held.Table.Output!;
            throw new FactConflictException(held.Table, DecodeKeys(held.Table, held.Key), Decode(output, held.Output), Decode(output, incoming.Output));
        }

        /// <summary>
        /// Runs the queued merges to closure: the class with fewer indexed rows joins the other, and its rows are placed
        /// again under their new keys, which may queue further merges.
        /// </summary>
        /// <exception cref="FactConflictException">A merge would give a function without a lattice two values for one
        /// key.</exception>
        void Close()
        {
            while (merges!.Count > 0)
            {
                var (a, b) = merges.Dequeue();
                var x = Representative(a);
                var y = Representative(b);
                if (x == y)
                    continue;

                var fromX = UsesOf(x);
                var fromY = UsesOf(y);
                if (fromX.Count > fromY.Count)
                {
                    (x, y) = (y, x);
                    (fromX, fromY) = (fromY, fromX);
                }

                overlay![x] = y;
                uses!.Remove(x);
                fromY.AddRange(fromX);
                foreach (var index in fromX)
                    Place(index);
            }
        }

        /// <summary>
        /// Gets a cell's class in the overlay, compressing the path.
        /// </summary>
        uint Representative(uint cell)
        {
            var root = cell;
            while (overlay!.TryGetValue(root, out var parent))
                root = parent;

            while (cell != root)
            {
                var next = overlay[cell];
                overlay[cell] = root;
                cell = next;
            }

            return root;
        }

        /// <summary>
        /// Gets the rows indexed under a class, made on first use.
        /// </summary>
        List<int> UsesOf(uint representative)
        {
            if (uses!.TryGetValue(representative, out var list) == false)
            {
                list = new List<int>();
                uses.Add(representative, list);
            }

            return list;
        }

    }

}
