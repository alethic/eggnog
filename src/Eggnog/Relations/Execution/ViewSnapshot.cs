using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The views of the tables a rule set reads, pinned when the rule set was built: the executor's trie roots, probers
    /// and kernels, and the search phase's action lookups, read only these, so a run sees one fixed picture of every
    /// table even while its tasks create layers and whatever the context's view cache does meanwhile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="TableView"/> per table index, <c>null</c> for a table the rule set does not read. Built by
    /// <see cref="ContextView.Snapshot(ReadOnlySpan{TableId})"/> from the context's cached view instances, the same ones
    /// header evaluation read, so headers and trie roots agree. At a root each entry is the root's single-layer view
    /// of the table, and every rule set shares one snapshot over the root's view cache: a root's views never change, and
    /// the cache's entries are only ever filled, so the entries a rule set reads stay what they were when it was built.
    /// </para>
    /// <para>
    /// Immutable in every entry a rule set reads; any number of threads may read it.
    /// </para>
    /// </remarks>
    internal sealed class ViewSnapshot
    {

        readonly TableView?[] views;

        /// <summary>
        /// Initializes a snapshot over <paramref name="views"/>, indexed by table.
        /// </summary>
        /// <param name="views">The views by table index; owned by the snapshot from now on, or, for a root's shared
        /// snapshot, the root's view cache, whose entries are only ever filled.</param>
        public ViewSnapshot(TableView?[] views)
        {
            ArgumentNullException.ThrowIfNull(views);
            this.views = views;
        }

        /// <summary>
        /// Gets the number of table slots, one more than the largest table index the snapshot can hold.
        /// </summary>
        public int Count => views.Length;

        /// <summary>
        /// Gets whether the snapshot reads <paramref name="array"/>: how a root's view reuses its shared snapshot while its
        /// view cache keeps the same array.
        /// </summary>
        /// <param name="array">An array of views.</param>
        /// <returns><c>true</c> if it is the snapshot's own.</returns>
        internal bool Wraps(TableView?[] array) => ReferenceEquals(views, array);

        /// <summary>
        /// Gets the pinned view of <paramref name="table"/>, or <c>null</c> when the rule set does not read it. O(1).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The view, or <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TableView? Get(TableId table) => (uint)table.Index < (uint)views.Length ? views[table.Index] : null;

        /// <summary>
        /// Gets the pinned view of <paramref name="table"/>, which the rule set must read.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="InvalidOperationException">The snapshot holds no view of the table: the rule set was not built
        /// with an atom or lookup over it.</exception>
        public TableView Require(TableId table) => Get(table) ?? ThrowMissing(table);

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static TableView ThrowMissing(TableId table)
        {
            throw new InvalidOperationException($"The rule set's view snapshot holds no view of table {table}; it reads only the tables its rules' atoms and lookups name.");
        }

    }

}
