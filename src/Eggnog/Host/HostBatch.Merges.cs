using System;
using System.Collections.Generic;

namespace Eggnog
{

    internal sealed partial class HostBatch
    {

        // The congruence check of merges. A union of the batch, or a constructor write that gives an existing key
        // another class, merges classes; the rebuild then merges every pair of rows whose keys become equal, and for a
        // function without a lattice two such rows with different values are an engine panic, which would fault the
        // scope. Phase 1 therefore simulates the closure the rebuild will compute: an overlay union-find over the cells
        // of the batch (canonical or fresh), and a signature table of the rows whose keys a merge can change, stored and
        // planned. Two rows of a constructor that meet merge their outputs, which cascades; two rows of a function
        // without a lattice that meet with different values are a FactConflictException, before anything is staged.
        //
        // The check starts at the batch's first merge, and only when the graph has a function without a lattice keyed by
        // a sort (or, below the root, a function with a merge that only the root writes, keyed by a sort or holding
        // one); otherwise no merge can conflict, and none of this state exists. It then reads every row of the tables
        // whose keys a merge can change (those functions, and the constructors whose outputs can reach their keys or
        // the classes they hold): an O(rows of those tables) scan, once per batch that merges. Two rows of a function
        // of a sort that meet are checked at the end of the planning instead (CheckClassOutputs), since whether their
        // classes are one when the rebuild compares them depends on the rebuild's passes.

        List<(TableHandle Table, uint[] Key, uint Output, bool Stored)>? mergeRows;
        HashSet<TableHandle>? congruent;
        Dictionary<string, int>? signatures;
        Dictionary<uint, uint>? overlay;
        Dictionary<uint, List<int>>? uses;
        Queue<(uint A, uint B)>? merges;
        Dictionary<uint, TableHandle>? rootOnlyKeys;
        Dictionary<uint, uint>? smallest;
        List<(int First, int Second)>? classOutputs;
        Dictionary<uint, uint>? direct;
        Dictionary<string, uint>? contents;
        Dictionary<uint, uint>? childMerges;
        List<(uint A, uint B)>? factUnions;
        Action<uint, uint>? collectUnion;
        Func<uint, uint>? canonical;
        bool mergeCheckDecided;
        bool mergedClasses;

        /// <summary>
        /// Records that the batch merges two classes (canonical or fresh cells), starting the congruence check at the
        /// first merge, and runs the closure.
        /// </summary>
        /// <exception cref="FactConflictException">The merge would give a function without a lattice two values for one
        /// key.</exception>
        /// <exception cref="InvalidOperationException">The scope is not the root, and the merge would rewrite a row of a
        /// function whose merge is not a lattice.</exception>
        void NoteMerge(uint a, uint b)
        {
            mergedClasses = true;
            CheckChildMerge(a, b);
            if (StartMergeCheck() == false)
                return;

            var x = Find(direct!, a);
            var y = Find(direct!, b);
            if (x != y)
                direct![x] = y;

            merges!.Enqueue((a, b));
            Close();
        }

        /// <summary>
        /// Below the root, refuses a merge that would displace a class a container of classes holds: a child never
        /// rebuilds containers, so the engine's eq-container guard would fault it (decisions row 9). The engine keeps the
        /// smaller cell of two classes it merges, so the larger is the one displaced. A merge the rebuild derives, such as
        /// two rows of a constructor meeting, is not foreseen here and still faults the child.
        /// </summary>
        /// <exception cref="InvalidOperationException">The merge displaces a class a container holds.</exception>
        void CheckChildMerge(uint a, uint b)
        {
            if (scope.IsRoot || graph.Engine.ContainerValues.HasEqContainers == false)
                return;

            childMerges ??= new Dictionary<uint, uint>();
            var x = Find(childMerges, a);
            var y = Find(childMerges, b);
            if (x == y)
                return;

            var (kept, displaced) = x < y ? (x, y) : (y, x);
            if (graph.Engine.ContainerValues.AnyEqContainerMentions(displaced))
                throw new InvalidOperationException(
                    $"This merge in '{scope.Path}' displaces class #{displaced}, which a container of classes holds; a child never rebuilds containers, so only the root may merge it (decisions row 9).");

            childMerges[displaced] = kept;
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
            direct = new Dictionary<uint, uint>();
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

            // The scope is rebuilt after every flush and every run, and caught up when the write enters, so the stored
            // rows are canonical.
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
                if (table is FunctionHandle { Lattice: null } function && (function.Merge is null || scope.IsRoot == false)
                    && (HasSortKey(table) || (function.Merge is not null && table.Output!.InvolvesClasses)))
                {
                    result.Add(table);
                    AddSortKeys(table, sorts);

                    // Classes a function holds become equal through the constructors that reach their sort, which the
                    // check of meeting rows (CheckClassOutputs) and the refusal below the root follow.
                    ContainerClassCells.AddSorts(table.Output!, sorts);
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
        /// Gets whether a table has a key column that names classes: a sort, or a container of classes, whose cells a
        /// merge changes.
        /// </summary>
        static bool HasSortKey(TableHandle table)
        {
            foreach (var column in table.KeyArray)
                if (column.InvolvesClasses)
                    return true;

            return false;
        }

        /// <summary>
        /// Adds the sorts of a table's key columns, and the sorts their containers hold, to <paramref name="sorts"/>.
        /// </summary>
        static void AddSortKeys(TableHandle table, HashSet<ColumnType> sorts)
        {
            foreach (var column in table.KeyArray)
                ContainerClassCells.AddSorts(column, sorts);
        }

        /// <summary>
        /// Gets a cell of <paramref name="column"/> as the rebuild would compare it under <paramref name="find"/>: a
        /// class's canonical cell; a container of classes as one stand-in cell per canonical content, since the rebuild
        /// makes containers of one content one container; any other cell as it is.
        /// </summary>
        uint CanonicalCell(ColumnType column, uint cell, Func<uint, uint> find)
        {
            if (column.Kind == ColumnKind.Sort)
                return find(cell);

            if (column.HoldsClasses == false)
                return cell;

            contents ??= new Dictionary<string, uint>(System.StringComparer.Ordinal);
            var content = ContainerClassCells.CanonicalContent(column, cell, find);
            if (contents.TryGetValue(content, out var standIn) == false)
            {
                // Stand-ins count down from the top of the id space, which a column of containers holds only so.
                standIn = uint.MaxValue - (uint)contents.Count;
                contents.Add(content, standIn);
            }

            return standIn;
        }

        /// <summary>
        /// Adds a row to the check: indexes it under the classes of its sort keys and places it in the signature table.
        /// </summary>
        void AddMergeRow(TableHandle table, uint[] key, uint output, bool stored)
        {
            var index = mergeRows!.Count;
            mergeRows.Add((table, key, output, stored));
            var columns = table.KeyArray;
            var classes = new List<uint>();
            for (var i = 0; i < key.Length; i++)
            {
                if (columns[i].InvolvesClasses == false)
                    continue;

                // A key that is a container is placed again when a class it holds merges.
                classes.Clear();
                ContainerClassCells.AppendClasses(columns[i], key[i], classes);
                foreach (var cell in classes)
                {
                    UsesOf(Representative(cell)).Add(index);
                    if (table is FunctionHandle { Merge: not null })
                        (rootOnlyKeys ??= new Dictionary<uint, TableHandle>()).TryAdd(cell, table);
                }
            }

            // Below the root, a merge of a class a row of such a function outputs would rewrite the row too.
            if (table is FunctionHandle { Merge: not null } && table.Output!.InvolvesClasses)
            {
                classes.Clear();
                ContainerClassCells.AppendClasses(table.Output, output, classes);
                foreach (var cell in classes)
                    (rootOnlyKeys ??= new Dictionary<uint, TableHandle>()).TryAdd(cell, table);
            }

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
                canonical[i] = CanonicalCell(columns[i], key[i], Representative);

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

            // Two classes agree when the rebuild has made them equal by the pass in which the rows meet, which the
            // check at the end of the planning works out (CheckClassOutputs); so do two containers of classes, which
            // the rebuild makes one container once their classes are equal.
            if (a.Table.Output!.InvolvesClasses)
            {
                (classOutputs ??= new List<(int, int)>()).Add((first, second));
                return;
            }

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
        /// <exception cref="InvalidOperationException">The scope is not the root, and a merge would rewrite a row of a
        /// function whose merge is not a lattice.</exception>
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

                // Set only below the root, from the stored rows indexed before the first merge.
                if (rootOnlyKeys is not null)
                {
                    var low = Smallest(x);
                    var high = Smallest(y);
                    if (low > high)
                        (low, high) = (high, low);

                    if (rootOnlyKeys.TryGetValue(high, out var keyed))
                        throw new InvalidOperationException(
                            $"'{keyed.Name}' has the merge '{((FunctionHandle)keyed).Merge!.Name}', which is not a lattice, so only the root may write it, and this merge of classes in '{scope.Path}' would rewrite one of its rows.");

                    smallest ??= new Dictionary<uint, uint>();
                    smallest.Remove(x);
                    smallest[y] = low;
                }

                overlay![x] = y;
                uses!.Remove(x);
                fromY.AddRange(fromX);
                foreach (var index in fromX)
                    Place(index);
            }
        }

        /// <summary>
        /// Gets the smallest cell of a representative's class: the cell the engine keeps when the class merges.
        /// </summary>
        uint Smallest(uint representative) =>
            smallest is not null && smallest.TryGetValue(representative, out var cell) ? cell : representative;

        /// <summary>
        /// Checks, at the end of the planning, the rows of a function of a sort that the batch's merges make meet, as the
        /// rebuild will: once the closure says some do (<see cref="Meet"/>), this replays the rebuild's passes over the
        /// rows of the check. Each pass compares every row under the union-find the earlier passes left, starting from
        /// the batch's own merges (its unions and its constructor writes), and then merges what met: a constructor's
        /// meeting rows union their classes for the next pass, and two rows of a function of a sort that meet must hold
        /// one class already, since its assert-equal merge panics on two, as egglog's does, before a later pass could
        /// merge them. The batch refuses that, rather than let the panic fault the scope. O(passes × rows of the check),
        /// and nothing unless such rows meet.
        /// </summary>
        /// <exception cref="FactConflictException">Two such rows hold different classes when they meet.</exception>
        void CheckClassOutputs()
        {
            if (classOutputs is null)
                return;

            var finder = new Dictionary<uint, uint>(direct!);
            var containers = RowContainers();
            while (true)
            {
                // Each pass rebuilds the containers first, against the union-find as the pass starts, and merges those
                // that became equal before the tables compare their rows; a container of containers so merges a pass
                // after the containers it holds, as the engine's and egglog's do.
                var mergedContainers = MergeContainers(containers, finder);
                var placed = new Dictionary<string, int>(System.StringComparer.Ordinal);
                var found = new List<(uint, uint)>();
                for (var i = 0; i < mergeRows!.Count; i++)
                {
                    var row = mergeRows[i];
                    var columns = row.Table.KeyArray;
                    var canonical = new uint[row.Key.Length];
                    for (var c = 0; c < canonical.Length; c++)
                        canonical[c] = columns[c].InvolvesClasses ? Find(finder, row.Key[c]) : row.Key[c];

                    var signature = Key(row.Table, canonical);
                    if (placed.TryGetValue(signature, out var j) == false)
                    {
                        placed.Add(signature, i);
                        continue;
                    }

                    // Values never change, so the closure checked them as they met; a function with a merge only the
                    // root writes never meets a row here, since its key's merge is refused below the root.
                    if (row.Table.Output!.InvolvesClasses == false || row.Table is FunctionHandle { Merge: not null })
                        continue;

                    var other = mergeRows[j];
                    var x = Find(finder, other.Output);
                    var y = Find(finder, row.Output);
                    if (x == y)
                        continue;

                    if (row.Table.Kind == TableKind.Constructor)
                    {
                        found.Add((x, y));
                        continue;
                    }

                    var (held, incoming) = other.Stored || row.Stored == false ? (other, row) : (row, other);
                    var output = held.Table.Output!;
                    throw new FactConflictException(held.Table, DecodeKeys(held.Table, held.Key), Decode(output, held.Output), Decode(output, incoming.Output));
                }

                if (found.Count == 0 && mergedContainers == false)
                    return;

                foreach (var (a, b) in found)
                {
                    var x = Find(finder, a);
                    var y = Find(finder, b);
                    if (x != y)
                        finder[x] = y;
                }
            }
        }

        /// <summary>
        /// Gets the containers of classes the rows of the check hold, those inside them too, each with its type and its
        /// elements.
        /// </summary>
        List<(ColumnType Type, uint Cell, List<(ColumnType Type, uint Cell)> Elements)> RowContainers()
        {
            var result = new List<(ColumnType, uint, List<(ColumnType, uint)>)>();
            var seen = new HashSet<uint>();
            var pending = new Stack<(ColumnType Type, uint Cell)>();
            foreach (var row in mergeRows!)
            {
                var columns = row.Table.KeyArray;
                for (var c = 0; c < columns.Length; c++)
                    if (columns[c].HoldsClasses)
                        pending.Push((columns[c], row.Key[c]));

                if (row.Table.Output!.HoldsClasses)
                    pending.Push((row.Table.Output, row.Output));
            }

            while (pending.Count > 0)
            {
                var (type, cell) = pending.Pop();
                if (seen.Add(cell) == false)
                    continue;

                var elements = new List<(ColumnType Type, uint Cell)>();
                ((IContainerColumnType)type).AppendElements(cell, elements);
                result.Add((type, cell, elements));
                foreach (var element in elements)
                    if (element.Type.HoldsClasses)
                        pending.Push(element);
            }

            return result;
        }

        /// <summary>
        /// Rebuilds the containers against <paramref name="finder"/> as it stands, then merges in it those of one type
        /// whose elements are now equal, as a pass of the engine's rebuild merges the containers before it rebuilds the
        /// tables.
        /// </summary>
        /// <returns>Whether two containers merged.</returns>
        static bool MergeContainers(
            List<(ColumnType Type, uint Cell, List<(ColumnType Type, uint Cell)> Elements)> containers,
            Dictionary<uint, uint> finder)
        {
            if (containers.Count == 0)
                return false;

            // Every content against the union-find as the pass starts, before any of the pass's merges.
            var contents = new List<(string Content, uint Cell)>(containers.Count);
            foreach (var (type, cell, elements) in containers)
            {
                var builder = new System.Text.StringBuilder(type.Name);
                foreach (var (elementType, elementCell) in elements)
                {
                    var current = elementType.InvolvesClasses ? Find(finder, elementCell) : elementCell;
                    builder.Append(' ').Append(current.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                contents.Add((builder.ToString(), cell));
            }

            var first = new Dictionary<string, uint>(System.StringComparer.Ordinal);
            var merged = false;
            foreach (var (content, cell) in contents)
            {
                if (first.TryGetValue(content, out var other) == false)
                {
                    first.Add(content, cell);
                    continue;
                }

                var x = Find(finder, other);
                var y = Find(finder, cell);
                if (x == y)
                    continue;

                finder[Math.Max(x, y)] = Math.Min(x, y);
                merged = true;
            }

            return merged;
        }

        /// <summary>
        /// Gets a cell's class in a union-find of parent links, compressing the path.
        /// </summary>
        static uint Find(Dictionary<uint, uint> parents, uint cell)
        {
            var root = cell;
            while (parents.TryGetValue(root, out var parent))
                root = parent;

            while (cell != root)
            {
                var next = parents[cell];
                parents[cell] = root;
                cell = next;
            }

            return root;
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
