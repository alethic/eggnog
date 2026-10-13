using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// The layering of the child context whose database the state runs over, or <c>null</c> at a root, in a bare
        /// database and over a bare catalog. Set by the view constructor and copied by clones.
        /// </summary>
        ContextTables? layers;

        /// <summary>
        /// The retraction guard of the state's database, taken when the state was created and consulted when a removal
        /// is staged; <c>null</c> in a bare database. Copied by clones.
        /// </summary>
        RetractionGuard? guard;

        /// <summary>
        /// The views a rule run's search reads: the rule set's pinned snapshot, given to the run's root state and
        /// copied by its clones (action batches); <c>null</c> outside a rule run.
        /// </summary>
        ViewSnapshot? views;

        /// <summary>
        /// Gets whether the state runs in a child context: committed tables are read through the context's views and
        /// write-phase lookups, never as single tables.
        /// </summary>
        public bool InChildContext => layers is not null;

        /// <summary>
        /// Gets the child context's layering, or <c>null</c>.
        /// </summary>
        internal ContextTables? Layers => layers;

        /// <summary>
        /// Gets the retraction guard the state's removals go through, or <c>null</c>.
        /// </summary>
        internal RetractionGuard? Guard => guard;

        /// <summary>
        /// Gets the views of the rule run the state belongs to, or <c>null</c> outside a rule run.
        /// </summary>
        internal ViewSnapshot? Views => views;

        /// <summary>
        /// Gives a rule run's root state the rule set's pinned views: its search-phase lookups in a child read them
        /// first, so they see the tables as the run's join does. One store at a root, whose lookups never read views.
        /// </summary>
        /// <param name="snapshot">The rule set's views.</param>
        internal void AttachViews(ViewSnapshot snapshot) => views = snapshot;

        /// <summary>
        /// Increments <paramref name="counter"/> on the store's shared counters, never redirected to a child context's
        /// id blocks: for ids that must not be local to any context. Interned container ids are minted here, so a
        /// container interned in a child is the same id everywhere.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long IncrementSharedCounter(CounterId counter) => RequireView().IncrementSharedCounter(counter);

        /// <summary>
        /// Gets the number of rows of <paramref name="table"/> the state's context sees: the committed table's live row
        /// count at a root, the view's exact visible count in a child (the bridge's <c>TableAction.row_count</c>).
        /// </summary>
        /// <remarks>
        /// The union-find has no view in a child; its count there is the number of ids not canonical in the child's
        /// view (<see cref="Context.DisplacedInViewCount"/>), one row per displaced id as at the root, so a child that
        /// asserts nothing counts what its parent counts (context = copy).
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <returns>The row count.</returns>
        public long VisibleRowCount(TableId table)
        {
            if (layers is not { } l)
                return ReadRootTable(table).Count;

            return table == l.Schema.UnionFind ? l.Owner.DisplacedInViewCount : ViewFor(l, table).VisibleCount;
        }

        /// <summary>
        /// Looks up column <paramref name="column"/> of the row of <paramref name="table"/> with key
        /// <paramref name="key"/> that the state's context sees, without inserting: the committed table's row at a root
        /// (one hash probe, as <c>GetTable(table).TryGetRowColumn</c>), the write-phase visible row in a child (one
        /// probe per layer and a sealed-set test on a hit; for the union-find, the child's view-level lookup). The
        /// bridge's <c>TableAction.lookup</c> reads through it, so merge functions and primitives look rows up in their
        /// own context.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key: one cell per key column, taken as given.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="value">The column's value, or zero.</param>
        /// <returns><c>true</c> if the context sees a row with the key.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryLookupColumn(TableId table, ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            if (layers is { } l)
                return TryGetVisibleColumn(l, table, key, column, out value);

            return ReadRootTable(table).TryGetRowColumn(key, column, out value);
        }

        /// <summary>
        /// Gets the view a child reads <paramref name="table"/> through in the search phase: the rule set's pinned view
        /// when the state belongs to a rule run that reads the table, otherwise the context's cached view.
        /// </summary>
        TableView ViewFor(ContextTables layers, TableId table) => views?.Get(table) ?? layers.GetView(table);

        /// <summary>
        /// <c>Lookup</c> and the lookup half of <c>LookupOrInsertDefault</c> and <c>LookupWithFallback</c> in a child:
        /// through the context's view of the table, or, for the union-find, through the child's union-find table (its
        /// <see cref="TableKind.OverlayDisplaced"/> arm).
        /// </summary>
        void LayeredLookupRow(ContextTables layers, TableId table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable output)
        {
            if (table == layers.Schema.UnionFind)
                VectorizedLookup.LookupRow(layers.Owner.UnionFindTable, ref mask, bindings, args, column, output);
            else
                VectorizedLookup.LookupRow(ViewFor(layers, table), ref mask, bindings, args, column, output);
        }

        /// <summary>
        /// <c>LookupWithDefault</c> in a child; see <see cref="LayeredLookupRow"/>.
        /// </summary>
        void LayeredLookupWithDefault(ContextTables layers, TableId table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, QueryEntry @default, Variable output)
        {
            if (table == layers.Schema.UnionFind)
                VectorizedLookup.LookupWithDefault(layers.Owner.UnionFindTable, ref mask, bindings, args, column, @default, output);
            else
                VectorizedLookup.LookupWithDefault(ViewFor(layers, table), ref mask, bindings, args, column, @default, output);
        }

        /// <summary>
        /// <see cref="PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/> in a child: the
        /// committed row is the one the context sees, found by the write-phase lookup, and the row's shape comes from
        /// the store's schema.
        /// </summary>
        void PredictRowLayered(ContextTables layers, TableId table, ReadOnlySpan<uint> key, ReadOnlySpan<MergeVal> values, Span<uint> row)
        {
            var spec = layers.Schema.GetSpec(table);
            if (row.Length < spec.Arity)
                throw new ArgumentException($"The destination needs room for {spec.Arity} cells.", nameof(row));

            if (TryGetVisibleRow(layers, table, key, row))
                return;

            var p = Predicted;
            var probe = p.GetProbeBuffer(key.Length);
            key.CopyTo(probe);
            var offset = PredictCore(p, spec.KeyCount, spec.Arity, table, probe, 0, key.Length, values);
            p.Cells.AsSpan(offset, spec.Arity).CopyTo(row);
        }

        /// <summary>
        /// <see cref="PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/> in a child.
        /// </summary>
        uint PredictColumnLayered(ContextTables layers, TableId table, ReadOnlySpan<uint> key, ReadOnlySpan<MergeVal> values, ColumnId column)
        {
            var spec = layers.Schema.GetSpec(table);
            if (TryGetVisibleColumn(layers, table, key, column, out var visible))
                return visible;

            var p = Predicted;
            var probe = p.GetProbeBuffer(key.Length);
            key.CopyTo(probe);
            return PredictedColumn(p, spec.KeyCount, spec.Arity, table, probe, 0, key.Length, values, column);
        }

        /// <summary>
        /// The array form of <see cref="PredictColumn(TableId, uint[], int, int, MergeVal[], ColumnId)"/> in a child.
        /// </summary>
        uint PredictColumnLayered(ContextTables layers, TableId table, uint[] key, int keyOffset, int keyLength, ReadOnlySpan<MergeVal> values, ColumnId column)
        {
            var spec = layers.Schema.GetSpec(table);
            if (TryGetVisibleColumn(layers, table, new ReadOnlySpan<uint>(key, keyOffset, keyLength), column, out var visible))
                return visible;

            return PredictedColumn(Predicted, spec.KeyCount, spec.Arity, table, key, keyOffset, keyLength, values, column);
        }

        /// <summary>
        /// The write-phase row lookup of
        /// <see cref="PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/> in a child:
        /// <see cref="ContextTables.TryGetVisibleRow"/>, or the child's union-find table's view-level lookup for the
        /// union-find.
        /// </summary>
        static bool TryGetVisibleRow(ContextTables layers, TableId table, ReadOnlySpan<uint> key, Span<uint> row)
        {
            if (table == layers.Schema.UnionFind)
                return layers.Owner.UnionFindTable.TryGetRow(key, row, out _);

            return layers.TryGetVisibleRow(table, key, row);
        }

        /// <summary>
        /// The write-phase column lookup of
        /// <see cref="PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/> in a child.
        /// </summary>
        static bool TryGetVisibleColumn(ContextTables layers, TableId table, ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            if (table == layers.Schema.UnionFind)
                return layers.Owner.UnionFindTable.TryGetRowColumn(key, column, out value);

            return layers.TryGetVisibleColumn(table, key, column, out value);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static Table ThrowLayeredRead(TableId table)
        {
            throw new InvalidOperationException($"Table {table} is layered in a child context and is read through the context's views, not as one table.");
        }

    }

}
