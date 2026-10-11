using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Moves <paramref name="atom"/>'s node out of the bindings into <paramref name="prober"/> and gives it an index
        /// over <paramref name="columns"/>. egglog's <c>get_index</c> (core-relations/src/free_join/execute.rs:819-884):
        /// <list type="bullet">
        /// <item>at most eight rows and one column: a <see cref="SparseColumnIndex"/>;</item>
        /// <item>a dense range of more than half the table, every column cacheable: the database's cached index,
        /// clamped to the range unless it is the whole table;</item>
        /// <item>otherwise several columns: a <see cref="TupleIndex"/> built for this stage;</item>
        /// <item>otherwise one column: the <see cref="ColumnIndex"/> cached on the node.</item>
        /// </list>
        /// Only a cached index can list rows the table no longer shows, so only its probers refine for visibility.
        /// </summary>
        /// <param name="prober">Receives the node and the index.</param>
        /// <param name="atom">The atom's index.</param>
        /// <param name="columns">The columns to index, at least one.</param>
        void GetIndex(ref Prober prober, int atom, ReadOnlySpan<ColumnId> columns)
        {
            var node = nodes[atom]!;
            nodes[atom] = null;
            var table = tables[atom];
            prober = default;
            prober.Node = node;
            var rows = node.Rows;
            if (rows.Size <= SparseColumnIndex.Capacity && columns.Length == 1)
            {
                var sparse = RentSparse();
                sparse.Build(table, rows, columns[0]);
                prober.Kind = ProberKind.Sparse;
                prober.Sparse = sparse;
                return;
            }

            var info = infos[atom];
            var cacheable = true;
            foreach (var column in columns)
                cacheable &= info.Spec.IsUncacheable(column) == false;

            // Every table's whole row set is the dense range [0, physical rows).
            var whole = table.All();
            var wholeSize = whole.Size;
            whole.Dispose();
            if (cacheable && rows.TryGetDense(out var range) && wholeSize / 2 < rows.Size)
            {
                // A large share of the table: probe the database's index, clamped to the range unless it is the table.
                prober.HasOuter = (range.Start == 0 && range.End == wholeSize) == false;
                prober.Outer = range;
                prober.MayHoldInvisible = table.HasStaleRows;
                if (columns.Length != 1)
                {
                    prober.Kind = ProberKind.CachedTuple;
                    prober.Tuple = info.GetIndex(columns).Data;
                }
                else
                {
                    prober.Kind = ProberKind.CachedColumn;
                    prober.Column = info.GetColumnIndex(columns[0]).Data;
                }

                return;
            }

            if (columns.Length != 1)
            {
                // egglog has no caching strategy for multi-column dynamic indexes either: built per stage invocation.
                prober.Kind = ProberKind.DynamicTuple;
                prober.Tuple = table.GroupByKey(rows, columns);
                return;
            }

            prober.Kind = ProberKind.DynamicColumn;
            prober.Column = node.GetCachedIndex(columns[0], table);
        }

        /// <summary>
        /// Moves <paramref name="prober"/>'s node back into the bindings and releases what the prober owns. egglog's
        /// <c>move_back</c>.
        /// </summary>
        /// <param name="prober">The prober; cleared.</param>
        /// <param name="atom">The atom's index.</param>
        void MoveBack(ref Prober prober, int atom)
        {
            nodes[atom] = prober.Node;
            switch (prober.Kind)
            {
                case ProberKind.Sparse:
                    ReturnSparse(prober.Sparse!);
                    break;
                case ProberKind.DynamicTuple:
                    prober.Tuple!.Dispose();
                    break;
            }

            prober = default;
        }

        /// <summary>
        /// Copies a candidate subset and keeps the rows that are visible and satisfy <paramref name="constraints"/>.
        /// egglog's <c>refine_subset</c> (core-relations/src/free_join/execute.rs:1091-1107): visibility is refined only
        /// when the subset came from an index that may list invisible rows, and the table's constraint refinement
        /// itself drops invisible rows.
        /// </summary>
        /// <param name="rows">The candidate rows.</param>
        /// <param name="constraints">The slow constraints of the scan or probe.</param>
        /// <param name="table">The atom's table.</param>
        /// <param name="mayHoldInvisible">Whether <paramref name="rows"/> may list rows the table no longer shows.</param>
        /// <returns>The refined rows, owned by the caller.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Subset RefineVisible(SubsetRef rows, Constraint[] constraints, Table table, bool mayHoldInvisible)
        {
            var subset = rows.ToOwned();
            if (mayHoldInvisible)
                subset = table.RefineLive(subset);

            if (constraints.Length != 0)
                subset = table.Refine(subset, constraints);

            return subset;
        }

        /// <summary>
        /// Refines the atom of <paramref name="scan"/> to its rows holding <paramref name="key"/>, appending the
        /// refinement to <paramref name="updates"/>: a small candidate is refined and copied into the frame; a large one
        /// goes through the prober node's child cache, so it is refined once per rule execution
        /// (core-relations/src/free_join/execute.rs:1124-1143).
        /// </summary>
        /// <param name="updates">The frame buffer.</param>
        /// <param name="prober">The scan's prober.</param>
        /// <param name="scan">The scan.</param>
        /// <param name="key">The value bound.</param>
        /// <param name="rows">The atom's candidate rows holding the value.</param>
        /// <param name="table">The atom's table.</param>
        /// <returns><c>false</c> if no row survives; the caller rolls the frame back.</returns>
        bool RefineScan(FrameUpdates updates, in Prober prober, SingleScanSpec scan, uint key, SubsetRef rows, Table table)
        {
            if (rows.Size <= SmallRefinement)
            {
                var subset = RefineVisible(rows, scan.Constraints, table, prober.MayHoldInvisible);
                if (subset.IsEmpty)
                {
                    subset.Dispose();
                    return false;
                }

                updates.RefineAtomSubset(scan.Atom, subset, pool);
                return true;
            }

            var column = scan.Column.Index;
            if (prober.Node.TryGetChild(column, key, out var child) == false)
            {
                var subset = RefineVisible(rows, scan.Constraints, table, prober.MayHoldInvisible);
                child = prober.Node.AddChild(column, key, pool.Rent(subset), table.Arity, pool);
            }

            if (child.IsEmpty)
                return false;

            updates.RefineAtom(scan.Atom, child);
            return true;
        }

        /// <summary>
        /// Refines a probed atom to <paramref name="rows"/>, appending the refinement to <paramref name="updates"/>. The
        /// probes of Free Join stages and materialization scans are not cached on trie nodes, as in egglog.
        /// </summary>
        /// <param name="updates">The frame buffer.</param>
        /// <param name="prober">The probe's prober.</param>
        /// <param name="probe">The probe.</param>
        /// <param name="rows">The atom's rows matching the key.</param>
        /// <returns><c>false</c> if no row survives; the caller rolls the frame back.</returns>
        bool RefineProbe(FrameUpdates updates, in Prober prober, Probe probe, SubsetRef rows)
        {
            var atom = probe.Scan.Atom;
            var subset = RefineVisible(rows, probe.Scan.Constraints, tables[atom.Index], prober.MayHoldInvisible);
            if (subset.IsEmpty)
            {
                subset.Dispose();
                return false;
            }

            updates.RefineAtomSubset(atom, subset, pool);
            return true;
        }

        /// <summary>
        /// Probes <paramref name="prober"/> with the key that <paramref name="probe"/> builds from the tuple at
        /// <paramref name="source"/>[<paramref name="offset"/>].
        /// </summary>
        /// <param name="prober">The prober.</param>
        /// <param name="probe">The probe, whose key positions index the tuple.</param>
        /// <param name="source">The array holding the tuple.</param>
        /// <param name="offset">The index of the tuple's first cell.</param>
        /// <param name="rows">The matching rows.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ProbeTuple(in Prober prober, Probe probe, uint[] source, int offset, out SubsetRef rows)
        {
            var key = probe.Key;
            if (key.Length == 1)
                return prober.TryGetSubset(source[offset + key[0].Index], out rows);

            var scratchKey = keyScratch;
            for (var k = 0; k < key.Length; k++)
                scratchKey[k] = source[offset + key[k].Index];

            return prober.TryGetSubset(scratchKey, out rows);
        }

        /// <summary>
        /// Takes a sparse column index from the task's stack.
        /// </summary>
        SparseColumnIndex RentSparse() => sparseFreeCount > 0 ? sparseFree[--sparseFreeCount] : new SparseColumnIndex();

        /// <summary>
        /// Returns a sparse column index to the task's stack.
        /// </summary>
        void ReturnSparse(SparseColumnIndex index)
        {
            if (sparseFreeCount == sparseFree.Length)
                Array.Resize(ref sparseFree, sparseFree.Length * 2);

            sparseFree[sparseFreeCount++] = index;
        }

    }

}
