using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one context sees of one function table (colored-contexts spec §6.3, X20): a <see cref="LayerView"/> per
    /// depth of the chain from the root to the context, <c>null</c> where that context has no layer of the table or where
    /// a deeper level cleared it. A row is visible when it is live in its layer and that layer's
    /// <see cref="LayerView.Hidden"/> does not hold it; at most one visible row holds a key (I12), and the first layer
    /// from the top whose key index holds a key decides it (Lemma F, <see cref="FindRow(uint, out int)"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable. A context builds and caches its views (<see cref="ContextTables.GetView"/>) and drops them when their
    /// structure changes (a layer created, a hide, a clear); rows appended to a present layer need no new view, since the
    /// layer views read live tables. A context that never touched a table shares its parent's view instance, and the root's
    /// views are <see cref="LayeredSchema.RootView"/>.
    /// </para>
    /// <para>
    /// The fields are plain readonly fields so compiled kernels can read them (§9.9). The layered subset members
    /// (<c>AllRows</c>, <c>AnyVisible</c>) arrive with the layered executor (M10.1-M10.2).
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
        /// <param name="layers">The layers by depth; at most 32 (X36), each over a <see cref="SortedWritesTable"/>.</param>
        internal TableView(TableId id, TableSpec spec, LayerView?[] layers)
        {
            ArgumentNullException.ThrowIfNull(layers);
            if (layers.Length > 32)
                throw new ArgumentException("A view has at most 32 layers (X36).", nameof(layers));

            Id = id;
            Spec = spec;
            Layers = layers;
            LayerCount = layers.Length;
            var mask = 0u;
            for (var j = 0; j < layers.Length; j++)
            {
                if (layers[j] is not { } layer)
                    continue;

                if (layer.Sorted is null)
                    throw new ArgumentException("Every layer of a function table's view is a sorted-writes table.", nameof(layers));

                mask |= 1u << j;
            }

            PresentMask = mask;
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
        /// Gets the largest timestamp of any row of the present layers (the last run of each sorted layer), or 0 when the
        /// table is unsorted or empty (rule-layer D16). O(layers).
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
