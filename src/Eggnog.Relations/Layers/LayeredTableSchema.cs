using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What every context of a store knows about one function table, independent of any context's rows
    /// (colored-contexts spec §6.1): its id and name, the root's table and <see cref="TableInfo"/>, whether it is
    /// context-private or the store's contradiction table, its stratum, and the shape every layer of it shares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once by <see cref="LayeredSchema.Register"/> and immutable afterwards, apart from
    /// <see cref="IsContradiction"/>, which <see cref="Store.DesignateContradiction"/> sets once. Child databases answer
    /// schema questions from it (X39) instead of from their own table maps, which hold only their own layers.
    /// </para>
    /// <para>
    /// <see cref="RootInfo"/> is the root database's entry held by reference, so it stays valid while the root takes
    /// the table out of its table map to merge it. The shape members read the root table, whose shape never changes.
    /// </para>
    /// </remarks>
    internal sealed class LayeredTableSchema
    {

        volatile bool isContradiction;

        /// <summary>
        /// Initializes the schema of table <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The table's id.</param>
        /// <param name="rootInfo">The root database's entry for it, whose table is a <see cref="SortedWritesTable"/>.</param>
        /// <param name="contextPrivate">Whether descendants never read the table (§6.1, decision 1).</param>
        /// <param name="stratum">The table's merge stratum.</param>
        internal LayeredTableSchema(TableId id, TableInfo rootInfo, bool contextPrivate, int stratum)
        {
            ArgumentNullException.ThrowIfNull(rootInfo);
            Id = id;
            RootInfo = rootInfo;
            Root = (SortedWritesTable)rootInfo.Table;
            IsContextPrivate = contextPrivate;
            Stratum = stratum;
        }

        /// <summary>
        /// Gets the table's id.
        /// </summary>
        public TableId Id { get; }

        /// <summary>
        /// Gets the table's human-readable name, or <c>null</c>.
        /// </summary>
        public string? Name => RootInfo.Name;

        /// <summary>
        /// Gets the root's table: layer 0 of every view of the table.
        /// </summary>
        public SortedWritesTable Root { get; }

        /// <summary>
        /// Gets the root database's entry for the table, held by reference.
        /// </summary>
        public TableInfo RootInfo { get; }

        /// <summary>
        /// Gets whether the table is context-private: not layered, never read by descendants, exempt from the
        /// retraction and compaction gates (decision 1, §6.7, §6.8).
        /// </summary>
        public bool IsContextPrivate { get; }

        /// <summary>
        /// Gets whether the table is the store's designated contradiction table, a nullary relation (§6.9).
        /// </summary>
        public bool IsContradiction => isContradiction;

        /// <summary>
        /// Gets the table's merge stratum (<see cref="DependencyGraph.LevelOf"/>).
        /// </summary>
        public int Stratum { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount => Root.KeyCount;

        /// <summary>
        /// Gets the number of columns.
        /// </summary>
        public int Arity => Root.Arity;

        /// <summary>
        /// Gets the sort (timestamp) column, or −1 when the table is unsorted.
        /// </summary>
        public int SortColumn => Root.SortColumn;

        /// <summary>
        /// Gets the columns that hold e-class ids, which rebuilds canonicalize.
        /// </summary>
        public ReadOnlySpan<ColumnId> RebuildColumns => Root.RebuildColumns;

        /// <summary>
        /// Gets the table's spec, shared by every layer.
        /// </summary>
        public TableSpec Spec => RootInfo.Spec;

        /// <summary>
        /// Gets whether the table's merge is a lattice merge, so children may write it (decision 2).
        /// </summary>
        public bool IsLattice => Root.MergeFunction.IsLattice;

        /// <summary>
        /// Gets whether the table is a constructor, for the action read analysis of §10.2: its merge resolves a conflict
        /// only by a union of ids, so a row's value changes only through unions, which descendants see through their find.
        /// A core <see cref="UnionIdMerge"/>, or a bridge function's <see cref="FunctionTableMerge"/> whose output merge
        /// is <see cref="MergeFnKind.UnionId"/> (egglog's constructors; the bridge wraps every function's merge, so the
        /// core type alone would make no bridge table a constructor).
        /// </summary>
        public bool IsConstructor => ConstructorValueColumn >= 0;

        /// <summary>
        /// Gets the column a constructor's merge unions (<see cref="UnionIdMerge.ValueColumn"/>, or a bridge
        /// function's output column), or -1 when the table is not a constructor (<see cref="IsConstructor"/>).
        /// </summary>
        public int ConstructorValueColumn => Root.MergeFunction switch
        {
            UnionIdMerge union => union.ValueColumn,
            FunctionTableMerge { Output.Kind: MergeFnKind.UnionId } function => function.Math.OutputColumn,
            _ => -1,
        };

        /// <summary>
        /// Marks the table as the store's contradiction table.
        /// </summary>
        internal void MarkContradiction() => isContradiction = true;

    }

}
