using System;
using System.Numerics;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one context sees of one function table: a <see cref="LayerView"/> per depth of the chain from the root to
    /// the context, <c>null</c> where that context has no layer of the table or where a deeper level cleared it. A row
    /// is visible when it is live in its layer and that layer's <see cref="LayerView.Hidden"/> does not hold it; at
    /// most one visible row holds a key, and the first layer from the top whose key index holds a key decides it
    /// (<see cref="FindRow(uint, out int)"/>): no row with a hidden row's key is visible below the hidden row's layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable. A context builds and caches its views (<see cref="ContextTables.GetView"/>) and drops them when their
    /// structure changes (a layer created, a hide, a clear); rows appended to a present layer need no new view, since the
    /// layer views read live tables. A context that never touched a table shares its parent's view instance, and the root's
    /// views are <see cref="LayeredSchema.RootView"/>.
    /// </para>
    /// <para>
    /// The fields are plain readonly fields so compiled kernels can read them. <see cref="AllRows"/> and
    /// <see cref="AnyVisible"/> are the layered executor's entry points: the rows a root trie node starts from, and the
    /// existence test of a zero-variable atom, which requires a visible row where egglog takes a nonempty header as
    /// proof (core-relations/src/free_join/plan.rs:1502-1505).
    /// </para>
    /// </remarks>
    internal sealed partial class TableView
    {

        /// <summary>
        /// The table's id.
        /// </summary>
        public readonly TableId Id;

        /// <summary>
        /// The table's spec, shared by every layer.
        /// </summary>
        public readonly TableSpec Spec;

        /// <summary>
        /// The layers by depth: index <c>j</c> is the layer of the context at depth <c>j</c>, or <c>null</c> when absent.
        /// </summary>
        public readonly LayerView?[] Layers;

        /// <summary>
        /// The number of layer slots: the depth of the context that built the view, plus one. A context sharing an
        /// ancestor's view instance has no layer of the table deeper than that ancestor.
        /// </summary>
        public readonly int LayerCount;

        /// <summary>
        /// Bit <c>j</c> is set when <see cref="Layers"/>[<c>j</c>] is present.
        /// </summary>
        public readonly uint PresentMask;

        /// <summary>
        /// The live rows the view hides, counted on first use of <see cref="VisibleCount"/>; −1 until then. The hidden
        /// sets of a view never change while it is in use.
        /// </summary>
        long hiddenLive = -1;

        /// <summary>
        /// Initializes a view.
        /// </summary>
        /// <param name="id">The table's id.</param>
        /// <param name="spec">The table's spec.</param>
        /// <param name="layers">The layers by depth; at most 32 (a layered subset keeps a 32-bit layer mask), each over
        /// a <see cref="SortedWritesTable"/>.</param>
        internal TableView(TableId id, TableSpec spec, LayerView?[] layers) :
            this(id, spec, layers, requireSorted: true)
        {

        }

        /// <summary>
        /// Initializes a view, checking the layers' table kind when <paramref name="requireSorted"/> is set.
        /// </summary>
        TableView(TableId id, TableSpec spec, LayerView?[] layers, bool requireSorted)
        {
            ArgumentNullException.ThrowIfNull(layers);
            if (layers.Length > 32)
                throw new ArgumentException("A view has at most 32 layers.", nameof(layers));

            Id = id;
            Spec = spec;
            Layers = layers;
            LayerCount = layers.Length;
            var mask = 0u;
            for (var j = 0; j < layers.Length; j++)
            {
                if (layers[j] is not { } layer)
                    continue;

                if (requireSorted && layer.Sorted is null)
                    throw new ArgumentException("Every layer of a function table's view is a sorted-writes table.", nameof(layers));

                mask |= 1u << j;
            }

            PresentMask = mask;
        }

        /// <summary>
        /// Creates the one-layer view of a database's own table, hiding nothing: what a root context, or a bare
        /// database, sees of each of its tables (<see cref="ContextView.ForRoot"/>). The table may be of any kind (the
        /// union-find included), since the executor only scans and indexes it; the point lookups of this type
        /// (<see cref="FindRow(uint, out int)"/> and its overloads) need a <see cref="SortedWritesTable"/> and are used
        /// only on a child's views.
        /// </summary>
        /// <param name="id">The table's id.</param>
        /// <param name="info">The database's entry for the table.</param>
        /// <param name="isFrozen">Whether the database is frozen.</param>
        /// <returns>The view.</returns>
        internal static TableView ForTable(TableId id, TableInfo info, bool isFrozen)
        {
            ArgumentNullException.ThrowIfNull(info);
            return new TableView(id, info.Spec, [new LayerView(0, info, HiddenRows.None, isFrozen)], requireSorted: false);
        }

        /// <summary>
        /// Gets every physical row of every present layer that has rows, as one dense subset per layer: the layered
        /// form of a table's <see cref="Table.All"/>. Tombstones and hidden rows are included, as egglog's <c>all</c>
        /// includes tombstones; the executor refines visibility where it reads rows. A view with one layer with rows
        /// gives a single-layer subset, which is every root view's case. O(layers).
        /// </summary>
        /// <param name="pool">The pool a per-layer array comes from when two or more layers have rows, or <c>null</c>.</param>
        /// <returns>The rows, which the caller owns.</returns>
        public LayeredSubset AllRows(SubsetArrayPool? pool)
        {
            // One layer (every root view, and a child's view of a table no level of its chain touched): the table's own
            // All(), exactly the root executor's row set, for any table kind.
            if (Layers.Length == 1)
                return Layers[0] is { } only0 ? LayeredSubset.Single(0, only0.Table.All()) : LayeredSubset.Empty;

            Span<int> counts = stackalloc int[LayeredSubset.MaxLayers];
            var withRows = 0u;
            for (var j = 0; j < Layers.Length; j++)
            {
                if (Layers[j] is not { } layer)
                    continue;

                var count = layer.Sorted!.Rows.Count;
                if (count == 0)
                    continue;

                counts[j] = count;
                withRows |= 1u << j;
            }

            if (withRows == 0)
                return LayeredSubset.Empty;

            if ((withRows & (withRows - 1)) == 0)
            {
                var only = BitOperations.TrailingZeroCount(withRows);
                return LayeredSubset.Single(only, Subset.Dense(0, counts[only]));
            }

            var length = 32 - BitOperations.LeadingZeroCount(withRows);
            var layers = pool is not null && pool.LayerCount >= length ? pool.Rent() : new Subset[length];
            var remaining = withRows;
            while (remaining != 0)
            {
                var j = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                layers[j] = Subset.Dense(0, counts[j]);
            }

            return LayeredSubset.FromLayers(layers, pool);
        }

        /// <summary>
        /// Gets whether some row of <paramref name="subset"/> of layer <paramref name="layer"/> is visible to the view:
        /// the existence test of a zero-variable atom. egglog answers that test by a nonempty header alone
        /// (core-relations/src/free_join/plan.rs:1502-1505), so a deleted fact still fires; this test requires a live
        /// row that no level hides. See <see cref="LayerView.AnyVisible"/>.
        /// </summary>
        /// <param name="layer">The layer; an absent or out-of-range layer has no visible row.</param>
        /// <param name="subset">Row ids of that layer, within its physical rows.</param>
        /// <returns><c>true</c> if a visible row is found.</returns>
        public bool AnyVisible(int layer, SubsetRef subset)
        {
            if ((uint)layer >= (uint)LayerCount || Layers[layer] is not { } view)
                return false;

            return view.AnyVisible(subset);
        }

        /// <summary>
        /// Gets the number of live rows of the present layers, hidden rows included: an upper bound of
        /// <see cref="VisibleCount"/>, as egglog's <c>len</c> is of a table's distinct keys. O(layers).
        /// </summary>
        public long ApproximateCount
        {
            get
            {
                long count = 0;
                foreach (var layer in Layers)
                    if (layer is not null)
                        count += layer.Table.Count;

                return count;
            }
        }

        /// <summary>
        /// Gets the largest timestamp of any row of the present layers (the last run of each sorted layer), or 0 when
        /// the table is unsorted or empty. O(layers).
        /// </summary>
        public uint LatestStamp
        {
            get
            {
                var latest = 0u;
                foreach (var layer in Layers)
                    if (layer?.Sorted is { } sorted && sorted.LastSortValue is var value && value > latest)
                        latest = value;

                return latest;
            }
        }

        /// <summary>
        /// Gets the exact number of rows the view sees: the live rows of the present layers minus the live rows they
        /// hide. The hidden part is counted once per view, O(hidden rows); the rest is O(layers).
        /// </summary>
        public long VisibleCount
        {
            get
            {
                var hidden = Volatile.Read(ref hiddenLive);
                if (hidden < 0)
                {
                    hidden = 0;
                    foreach (var layer in Layers)
                        if (layer is { Hidden.IsNone: false } && layer.Sorted is { } sorted)
                            hidden += layer.Hidden.CountLive(sorted.Rows.Data, sorted.Arity, sorted.Rows.Count);

                    Volatile.Write(ref hiddenLive, hidden);
                }

                return ApproximateCount - hidden;
            }
        }

    }

}
