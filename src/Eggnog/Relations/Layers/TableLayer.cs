using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A child context's layer of one function table: the rows the context stored itself (<see cref="Own"/>, an
    /// ordinary single-shard <see cref="SortedWritesTable"/> in the context's database) and the rows of each ancestor
    /// layer it hides (<see cref="HiddenOver"/>), plus whether it cleared the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created lazily by <see cref="ContextTables.GetOrCreateLayer"/>, on the context's first write, hide or clear of the
    /// table; a context that only reads a table has no layer of it and sees its parent's view. The own table stages and
    /// merges like any table of the database, through <see cref="SortedWritesTable.MergeLayered"/>, which canonicalizes and
    /// stamps incoming rows, merges with the visible ancestor row of the key and hides it.
    /// </para>
    /// <para>
    /// Hides (<see cref="Hide"/>) run on the context's domain thread only, inside its merge or right before the merge
    /// of the same pass; the merge epilogue seals them (<see cref="SealHidden"/>). Hidden sets only grow.
    /// </para>
    /// </remarks>
    internal sealed class TableLayer
    {

        /// <summary>
        /// The context's layering, which owns this layer.
        /// </summary>
        public readonly ContextTables Owner;

        /// <summary>
        /// The table's store-wide schema.
        /// </summary>
        public readonly LayeredTableSchema Schema;

        /// <summary>
        /// The table's id.
        /// </summary>
        public readonly TableId Id;

        /// <summary>
        /// The depth of the owning context: the layer's index in the context's views.
        /// </summary>
        public readonly int Depth;

        /// <summary>
        /// The hidden rows of each ancestor layer, by ancestor depth; created on the first hide.
        /// </summary>
        readonly HiddenSet?[] hidden;

#if DEBUG
        /// <summary>
        /// Why each row was hidden, by <c>(layer &lt;&lt; 32) | row</c>, for the invariant checker. DEBUG builds only.
        /// </summary>
        readonly Dictionary<long, HideReason> reasons = new();
#endif

        volatile bool isAdopted;
        bool isCleared;
        bool hasRetractions;
        int version;

        /// <summary>
        /// Initializes the layer of <paramref name="schema"/>'s table in the context <paramref name="owner"/> lays out,
        /// creating its own table.
        /// </summary>
        /// <param name="owner">The context's layering.</param>
        /// <param name="schema">The table's schema.</param>
        internal TableLayer(ContextTables owner, LayeredTableSchema schema)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(schema);
            Owner = owner;
            Schema = schema;
            Id = schema.Id;
            Depth = owner.Depth;
            hidden = new HiddenSet?[Depth];
            Own = schema.Root.CreateLayer(this);
            Info = new TableInfo(schema.Name, Own, ParallelHeuristics.Serial);
        }

        /// <summary>
        /// Gets the context's own rows of the table.
        /// </summary>
        public SortedWritesTable Own { get; }

        /// <summary>
        /// Gets the own table's entry in the context's database: its cached indexes.
        /// </summary>
        public TableInfo Info { get; }

        /// <summary>
        /// Gets whether the context's database holds the layer in its table map (<see cref="Database.AdoptLayer"/>). A
        /// layer created during a search waits for the next merge round to be adopted.
        /// </summary>
        public bool IsAdopted => isAdopted;

        /// <summary>
        /// Gets whether the context cleared the table: every ancestor layer is absent from its views.
        /// </summary>
        public bool IsCleared => isCleared;

        /// <summary>
        /// Gets whether the context retracted rows of the table: it staged a delete (its own row tombstoned, or a row
        /// it saw through an ancestor hidden with <see cref="HideReason.Retracted"/>) or a clear. A row it hid earlier
        /// may then have lost its carrier, which the invariant checker allows.
        /// </summary>
        public bool HasRetractions => hasRetractions;

        /// <summary>
        /// Gets a counter moved by every hide and clear: the layer's structure version.
        /// </summary>
        public int Version => Volatile.Read(ref version);

        /// <summary>
        /// Gets the rows of the ancestor layer at depth <paramref name="layer"/> that this context hides, or <c>null</c>.
        /// </summary>
        /// <param name="layer">An ancestor depth, below <see cref="Depth"/>.</param>
        /// <returns>The set, or <c>null</c> when nothing of that layer is hidden here.</returns>
        public HiddenSet? HiddenOver(int layer) => (uint)layer < (uint)hidden.Length ? hidden[layer] : null;

        /// <summary>
        /// Gets whether this context hides row <paramref name="row"/> of the ancestor layer at depth
        /// <paramref name="layer"/>, sealed or not: the write path's probe.
        /// </summary>
        /// <param name="layer">An ancestor depth.</param>
        /// <param name="row">A row id of that layer.</param>
        /// <returns><c>true</c> if hidden here.</returns>
        public bool IsHiddenHere(int layer, int row) => HiddenOver(layer) is { } set && set.Contains(row);

        /// <summary>
        /// Gets whether this context hides any row of any ancestor layer of the table (a set exists only once a row was
        /// hidden, and sets never shrink). O(depth). Domain thread only.
        /// </summary>
        internal bool HasHidden
        {
            get
            {
                foreach (var set in hidden)
                    if (set is not null)
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Hides row <paramref name="row"/> of the ancestor layer at depth <paramref name="layer"/>: the row carries
        /// information this context holds elsewhere (<paramref name="reason"/>). Moves the layer's version, the
        /// context's change stamp and the table's dirty flag. Domain thread only, inside the context's merge or right
        /// before the merge of the same pass.
        /// </summary>
        /// <param name="layer">An ancestor depth.</param>
        /// <param name="row">A row id of that layer.</param>
        /// <param name="reason">Why the row is hidden.</param>
        /// <returns><c>false</c> if the row was hidden at this level already.</returns>
        public bool Hide(int layer, int row, HideReason reason)
        {
            Debug.Assert((uint)layer < (uint)hidden.Length, "only ancestor layers are hidden");
            var ancestor = Owner.AncestorLayer(layer, Id) ?? throw new InvalidOperationException($"The ancestor at depth {layer} has no layer of table {Id} to hide a row of.");
            var set = hidden[layer];
            if (set is null)
                hidden[layer] = set = new HiddenSet(ancestor.Version.Major);

            Debug.Assert(set.Generation == ancestor.Version.Major, "a hidden set outlived its layer's generation");
            var wasSealed = set.IsSealed;
            if (set.Add(row, ancestor.Rows.Count) == false)
                return false;

            if (wasSealed)
                Owner.NoteUnsealed(set, ancestor);

#if DEBUG
            reasons[((long)layer << 32) | (uint)row] = reason;
#endif
            Interlocked.Increment(ref version);
            Owner.NoteHidden(Id);
            return true;
        }

        /// <summary>
        /// Gets why row <paramref name="row"/> of the ancestor layer at depth <paramref name="layer"/> was hidden, in DEBUG
        /// builds; <c>null</c> when it is not hidden here or in a release build.
        /// </summary>
        /// <param name="layer">An ancestor depth.</param>
        /// <param name="row">A row id of that layer.</param>
        /// <returns>The reason, or <c>null</c>.</returns>
        internal HideReason? ReasonFor(int layer, int row)
        {
#if DEBUG
            return reasons.TryGetValue(((long)layer << 32) | (uint)row, out var reason) ? reason : null;
#else
            return null;
#endif
        }

        /// <summary>
        /// Records that the context retracted rows of the table.
        /// </summary>
        internal void SetRetracted() => hasRetractions = true;

        /// <summary>
        /// Records that the context cleared the table: its views drop every ancestor layer.
        /// </summary>
        internal void SetCleared()
        {
            isCleared = true;
            Interlocked.Increment(ref version);
        }

        /// <summary>
        /// Records that the context's database holds the layer in its table map.
        /// </summary>
        internal void SetAdopted() => isAdopted = true;

        /// <summary>
        /// Seals every hidden set of the layer.
        /// </summary>
        internal void SealHidden()
        {
            for (var j = 0; j < hidden.Length; j++)
                if (hidden[j] is { } set && Owner.AncestorLayer(j, Id) is { } ancestor)
                    set.Seal(ancestor.Rows.Count);
        }

    }

}
