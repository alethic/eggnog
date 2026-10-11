using System;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The declaration of one table of a differential program (<see cref="DiffProgram"/>): either the union-find
    /// (<see cref="DisplacedTable"/>) or a <see cref="SortedWritesTable"/> with a random layout and merge function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: <see cref="KeyCount"/> key columns, then value columns, then optionally a timestamp column (the last,
    /// the table's sort column) that every write fills with the program's current epoch, so it is nondecreasing in row
    /// order as the engine requires.
    /// </para>
    /// <para>
    /// A table is <see cref="Deterministic"/> when the result of merging a batch of rows that all carry the same
    /// timestamp does not depend on the order of the rows: a set layout (every non-timestamp column is a key), or one
    /// value column under <see cref="DiffMergeKind.Min"/>, <see cref="DiffMergeKind.Max"/> or
    /// <see cref="DiffMergeKind.UnionId"/>. Only such tables are written by rules (whose matches arrive in no fixed
    /// order) and rebuilt through the union-find (whose rewritten rows collide in physical row order); the others are
    /// written only by loads, whose staging order the model replays.
    /// </para>
    /// </remarks>
    sealed class DiffTable
    {

        /// <summary>
        /// Initializes a sorted-writes table declaration.
        /// </summary>
        public DiffTable(int index, int keyCount, int arity, bool hasTimestamp, DiffMergeKind merge, int valueColumn, bool unionUsesTimestamp, int[] rebuildColumns, bool sharded, int[] readDependencies, int[] writeDependencies)
        {
            Index = index;
            KeyCount = keyCount;
            Arity = arity;
            TimestampColumn = hasTimestamp ? arity - 1 : -1;
            Merge = merge;
            ValueColumn = valueColumn;
            UnionUsesTimestamp = unionUsesTimestamp;
            RebuildColumns = rebuildColumns;
            Sharded = sharded;
            ReadDependencies = readDependencies;
            WriteDependencies = writeDependencies;
        }

        /// <summary>
        /// Initializes the union-find declaration.
        /// </summary>
        DiffTable(int index)
        {
            Index = index;
            IsUnionFind = true;
            KeyCount = 1;
            Arity = 3;
            TimestampColumn = 2;
            ValueColumn = 1;
            RebuildColumns = Array.Empty<int>();
            ReadDependencies = Array.Empty<int>();
            WriteDependencies = Array.Empty<int>();
        }

        /// <summary>
        /// Creates the union-find declaration.
        /// </summary>
        public static DiffTable UnionFind(int index) => new(index);

        /// <summary>
        /// Gets the table's position in the program, which is also its engine <see cref="TableId"/>.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// Gets whether this is the union-find (<c>[child, canonical, ts]</c>, key column 0, sorted by column 2).
        /// </summary>
        public bool IsUnionFind { get; }

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount { get; }

        /// <summary>
        /// Gets the number of columns, the timestamp included.
        /// </summary>
        public int Arity { get; }

        /// <summary>
        /// Gets the timestamp (sort) column, or -1.
        /// </summary>
        public int TimestampColumn { get; }

        /// <summary>
        /// Gets whether the table has a timestamp column.
        /// </summary>
        public bool IsSorted => TimestampColumn >= 0;

        /// <summary>
        /// Gets the number of columns other than the timestamp.
        /// </summary>
        public int DataArity => IsSorted ? Arity - 1 : Arity;

        /// <summary>
        /// Gets the merge function (meaningless for the union-find).
        /// </summary>
        public DiffMergeKind Merge { get; }

        /// <summary>
        /// Gets the column the merge function compares, or -1 for merges that compare none.
        /// </summary>
        public int ValueColumn { get; }

        /// <summary>
        /// Gets whether a <see cref="DiffMergeKind.UnionId"/> merge stamps its unions with the incoming row's timestamp
        /// column rather than the merge state's timestamp (both are the epoch of the write).
        /// </summary>
        public bool UnionUsesTimestamp { get; }

        /// <summary>
        /// Gets the columns a rebuild canonicalizes through the union-find.
        /// </summary>
        public int[] RebuildColumns { get; }

        /// <summary>
        /// Gets whether the table is sharded (<see cref="ParallelHeuristics.Default"/>) rather than single-sharded.
        /// </summary>
        public bool Sharded { get; }

        /// <summary>
        /// Gets the tables declared as read dependencies of the merge function.
        /// </summary>
        public int[] ReadDependencies { get; }

        /// <summary>
        /// Gets the tables declared as write dependencies of the merge function.
        /// </summary>
        public int[] WriteDependencies { get; }

        /// <summary>
        /// Gets whether every non-timestamp column is a key.
        /// </summary>
        public bool IsSet => IsUnionFind == false && KeyCount == DataArity;

        /// <summary>
        /// Gets whether merging same-timestamp rows is insensitive to their order (see the remarks), so rules may write
        /// the table and rebuilds may rewrite it.
        /// </summary>
        public bool Deterministic =>
            IsUnionFind == false &&
            (IsSet || (DataArity == KeyCount + 1 && Merge is DiffMergeKind.Min or DiffMergeKind.Max or DiffMergeKind.UnionId));

        /// <summary>
        /// Gets whether a rebuild through the union-find rewrites the table.
        /// </summary>
        public bool Rebuilds => RebuildColumns.Length > 0;

        /// <summary>
        /// Gets the name the program listing uses.
        /// </summary>
        public string Name => IsUnionFind ? "uf" + Index : "t" + Index;

        /// <summary>
        /// Creates the engine table.
        /// </summary>
        /// <param name="unionFind">The engine id of the union-find, for <see cref="DiffMergeKind.UnionId"/>.</param>
        /// <returns>The table.</returns>
        public Table Create(TableId unionFind)
        {
            if (IsUnionFind)
                return new DisplacedTable();

            MergeFunction merge = Merge switch
            {
                DiffMergeKind.TakeNew => ClosureMerge.TakeNew,
                DiffMergeKind.KeepCurrent => KeepCurrentMerge.Instance,
                DiffMergeKind.KeepIncoming => new KeepIncomingMerge(new ColumnId(ValueColumn)),
                DiffMergeKind.Min => new MinMerge(new ColumnId(ValueColumn)),
                DiffMergeKind.Max => new MaxMerge(new ColumnId(ValueColumn)),
                DiffMergeKind.UnionId => new UnionIdMerge(new ColumnId(ValueColumn), unionFind, UnionUsesTimestamp && IsSorted ? new ColumnId(TimestampColumn) : null),
                _ => throw new InvalidOperationException("Unknown merge " + Merge),
            };

            var rebuild = RebuildColumns.Select(c => new ColumnId(c)).ToArray();
            return new SortedWritesTable(KeyCount, Arity, IsSorted ? new ColumnId(TimestampColumn) : null, rebuild, merge, Sharded ? ParallelHeuristics.Default : ParallelHeuristics.Serial);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (IsUnionFind)
                return $"{Name} = union-find [child, canonical, ts]";

            var b = new StringBuilder();
            b.Append(Name).Append(" keys=").Append(KeyCount).Append(" arity=").Append(Arity);
            if (IsSorted)
                b.Append(" ts=c").Append(TimestampColumn);

            b.Append(" merge=").Append(Merge);
            if (ValueColumn >= 0 && IsSet == false)
                b.Append("(c").Append(ValueColumn).Append(')');
            if (Merge == DiffMergeKind.UnionId)
                b.Append(UnionUsesTimestamp && IsSorted ? " union-ts=row" : " union-ts=state");
            if (Rebuilds)
                b.Append(" rebuild=[").Append(string.Join(",", RebuildColumns.Select(c => "c" + c))).Append(']');
            if (Deterministic)
                b.Append(" deterministic");
            if (Sharded)
                b.Append(" sharded");
            if (ReadDependencies.Length > 0)
                b.Append(" reads=[").Append(string.Join(",", ReadDependencies)).Append(']');
            if (WriteDependencies.Length > 0)
                b.Append(" writes=[").Append(string.Join(",", WriteDependencies)).Append(']');

            return b.ToString();
        }

    }

}
