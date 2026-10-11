using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The union-find exposed as a table, so that rules, merges and rebuild see equalities through ordinary table
    /// operations: egglog's <c>DisplacedTable</c> (core-relations/src/uf/mod.rs:30-523), built on
    /// <see cref="DisplacedUnionFind"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Logical row <c>r</c> is <c>[child, canonical(child), timestamp]</c>, one per effective union, where
    /// <c>child</c> is the id the union displaced. Column 0 is the key; column 1 is computed on every read with
    /// <see cref="DisplacedUnionFind.FindNaive"/>, so it is always current and never cacheable; rows are in
    /// nondecreasing timestamp order. Ids that were never displaced have no row, but
    /// <see cref="TryGetRowColumn"/> of column 1 answers for any id (the bridge's <c>lookup_uf</c>).
    /// </para>
    /// <para>
    /// To union <c>a</c> and <c>b</c>, stage the row <c>[a, b, ts]</c>; the merge decides which root is displaced (the
    /// larger) and ignores redundant unions. Rows are never deleted: staging a removal throws.
    /// </para>
    /// <para>
    /// Versions: <see cref="TableVersion.Minor"/> is the row count, so a table's rebuild finds the ids displaced since
    /// its last one as a suffix. egglog's <c>clear</c> keeps the version and the child-to-row lookup
    /// (core-relations/src/uf/mod.rs:286-289; map §8.1 row 12); here clearing a nonempty table empties the lookup and
    /// starts a new generation, so trackers and indexes see the change.
    /// </para>
    /// <para>
    /// Threading: reads run concurrently while no merge runs; <see cref="Merge"/> and <see cref="Clear"/> are
    /// exclusive.
    /// </para>
    /// </remarks>
    internal sealed class DisplacedTable : Table
    {

        static readonly TableSpec DisplacedSpec = new(1, 2, false, [new ColumnId(1)]);

        readonly DisplacedUnionFind uf;
        readonly PendingState pending;
        int generation;

        /// <summary>
        /// Initializes an empty union-find table. egglog's <c>DisplacedTable::default</c>.
        /// </summary>
        public DisplacedTable()
            : this(new DisplacedUnionFind(), 0)
        {

        }

        DisplacedTable(DisplacedUnionFind uf, int generation)
            : base(TableKind.Displaced, 3)
        {
            this.uf = uf;
            this.generation = generation;
            pending = new PendingState(new ShardData(1));
        }

        /// <summary>
        /// Gets the union-find the table exposes. Read-only use only while rules run; unions go through staged rows and
        /// <see cref="Merge"/>.
        /// </summary>
        public DisplacedUnionFind UnionFind => uf;

        /// <inheritdoc />
        public override TableSpec Spec => DisplacedSpec;

        /// <inheritdoc />
        public override int Count => uf.Count;

        /// <inheritdoc />
        public override TableVersion Version => new(generation, uf.Count);

        /// <inheritdoc />
        public override bool HasStaleRows => false;

        /// <inheritdoc />
        public override Subset All() => Subset.Dense(0, uf.Count);

        /// <inheritdoc />
        public override Subset UpdatesSince(int offset)
        {
            if ((uint)offset > (uint)uf.Count)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The table has {uf.Count} rows.");

            return Subset.Dense(offset, uf.Count);
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> without writing: column 1 for any id. For compiled plans.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FindNaive(uint id) => uf.FindNaive(id);

        /// <summary>
        /// Gets one cell of row <paramref name="row"/>: the displaced id, its canonical id, or the timestamp.
        /// O(1), or O(find) for column 1.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <param name="column">The column, 0 to 2.</param>
        /// <returns>The cell.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetCell(int row, int column)
        {
            switch (column)
            {
                case 0:
                    return uf.GetChild(row);
                case 1:
                    return uf.GetCanonical(row);
                case 2:
                    return uf.GetTimestamp(row);
                default:
                    throw new ArgumentOutOfRangeException(nameof(column), column, "The union-find table has three columns.");
            }
        }

        /// <summary>
        /// Visits the rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that satisfy every
        /// constraint, each expanded to its three cells: egglog's <c>scan_generic_bounded</c>
        /// (core-relations/src/uf/mod.rs:316-344). Hides <see cref="Table.ScanGenericBounded{TSink}"/>, which dispatches
        /// here.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="sink">Receives each expanded row with its row id; must not keep the span.</param>
        /// <returns>The next position, or -1.</returns>
        public new int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (subset.TryGetBounds(out var bounds) == false)
                return -1;

            if (bounds.End > uf.Count || bounds.Start < 0)
                throw new ArgumentOutOfRangeException(nameof(subset), bounds.End, $"The subset extends past the table's {uf.Count} rows.");

            // Positions [start, stop) of the subset, clipped, with SubsetRef.IterBounded's resume rules.
            var size = subset.Size;
            var stop = (int)Math.Min((long)start + count, size);
            var rows = subset.Rows;
            if (rows is null)
            {
                if (start >= size)
                    return -1;
            }
            else if (start > stop)
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, $"Position {start} is past the end of the chunk {stop}.");
            }

            Span<uint> cells = stackalloc uint[3];
            if (rows is null)
            {
                for (int row = subset.Lo + start, last = subset.Lo + stop; row < last; row++)
                    EmitIfMatches(row, cells, constraints, ref sink);
            }
            else
            {
                foreach (var row in new ReadOnlySpan<int>(rows, subset.Lo + start, stop - start))
                    EmitIfMatches(row, cells, constraints, ref sink);
            }

            return stop < size ? stop : -1;
        }

        /// <summary>
        /// Visits every row of <paramref name="subset"/>. Hides <see cref="Table.ScanGeneric{TSink}"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="sink">Receives each expanded row with its row id.</param>
        public new void ScanGeneric<TSink>(SubsetRef subset, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ScanGenericBounded(subset, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref sink);
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>refine_one</c> (core-relations/src/uf/mod.rs:346-349), on the expanded rows.</remarks>
        public override Subset RefineOne(Subset subset, Constraint constraint)
        {
            if ((uint)constraint.Column >= 3u || (constraint.Kind == ConstraintKind.Eq && (uint)constraint.OtherColumn >= 3u))
                throw new ArgumentOutOfRangeException(nameof(constraint), constraint, "The union-find table has three columns.");

            var filter = new DisplacedConstraintFilter(this, constraint);
            subset.Retain(ref filter);
            return subset;
        }

        /// <summary>
        /// Gets whether row <paramref name="row"/> satisfies <paramref name="constraint"/>, with column 1 computed.
        /// egglog's <c>eval</c> (core-relations/src/uf/mod.rs:485-488).
        /// </summary>
        /// <param name="constraint">The constraint.</param>
        /// <param name="row">The row.</param>
        /// <returns><c>true</c> if satisfied.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Evaluate(Constraint constraint, int row)
        {
            var cell = GetCell(row, constraint.Column);
            return constraint.Kind == ConstraintKind.Eq ? cell == GetCell(row, constraint.OtherColumn) : constraint.Compare(cell);
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>fast_subset</c> (core-relations/src/uf/mod.rs:351-419): an <c>EqConst</c> on column 0 is a lookup;
        /// range constraints on the timestamp column are binary searches; column 1 is never fast. As in egglog, an
        /// <c>EqConst</c> on the timestamp with no matching row is left to the slow path.
        /// </remarks>
        public override bool TryFastSubset(Constraint constraint, out Subset subset)
        {
            subset = default;
            if (constraint.Kind == ConstraintKind.Eq)
                return false;

            if (constraint.Kind == ConstraintKind.EqConst && constraint.Column == 0)
            {
                subset = uf.TryGetRow(constraint.Constant, out var row) ? Subset.Dense(row, row + 1) : Subset.Empty;
                return true;
            }

            if (constraint.Column != 2)
                return false;

            var found = uf.TimestampBounds(constraint.Constant, out var start, out var end);
            switch (constraint.Kind)
            {
                case ConstraintKind.EqConst:
                    if (found == false)
                        return false;

                    subset = Subset.Dense(start, end);
                    return true;
                case ConstraintKind.LtConst:
                    subset = Subset.Dense(0, start);
                    return true;
                case ConstraintKind.GtConst:
                    subset = Subset.Dense(end, uf.Count);
                    return true;
                case ConstraintKind.LeConst:
                    subset = Subset.Dense(0, end);
                    return true;
                default:
                    subset = Subset.Dense(start, uf.Count);
                    return true;
            }
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>get_row</c> (core-relations/src/uf/mod.rs:421-427): only displaced ids have rows.</remarks>
        public override bool TryGetRow(ReadOnlySpan<uint> key, Span<uint> destination, out int rowId)
        {
            CheckKey(key);
            if (destination.Length < 3)
                throw new ArgumentException("The destination needs room for 3 cells.", nameof(destination));

            if (uf.TryGetRow(key[0], out rowId) == false)
                return false;

            uf.Expand(rowId, out destination[0], out destination[1], out destination[2]);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>get_row_column</c> (core-relations/src/uf/mod.rs:429-437): column 1 is the canonical id of any
        /// key, displaced or not.
        /// </remarks>
        public override bool TryGetRowColumn(ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            CheckKey(key);
            return TryGetColumn(key[0], column.Rep, out value);
        }

        /// <summary>
        /// Gets column <paramref name="column"/> for key <paramref name="key"/>; column 1 is answered for any id. The
        /// form for compiled plans.
        /// </summary>
        /// <param name="key">The id.</param>
        /// <param name="column">The column, 0 to 2.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the value exists.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint key, int column, out uint value)
        {
            if (column == 1)
            {
                value = uf.FindNaive(key);
                return true;
            }

            if (uf.TryGetRow(key, out var row) == false)
            {
                if ((uint)column > 2u)
                    throw new ArgumentOutOfRangeException(nameof(column), column, "The union-find table has three columns.");

                value = 0;
                return false;
            }

            value = GetCell(row, column);
            return true;
        }

        /// <inheritdoc />
        public override MutationBuffer NewBuffer() => new(pending, 3, 1, false);

        /// <inheritdoc />
        public override PendingState Pending => pending;

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>merge</c> (core-relations/src/uf/mod.rs:446-459): applies each staged <c>[a, b, ts]</c> in queue
        /// and staging order with <see cref="DisplacedUnionFind.Union"/>. A row counts as both added and removed, since
        /// unions change rows in place.
        /// </remarks>
        public override TableChange Merge(ExecutionState state)
        {
            pending.TakeTotalRows();
            pending.TakeTotalRemovals();
            var queue = pending.RowQueue(0);
            var changed = false;
            while (queue.TryDequeue(out var buffer))
            {
                var cells = buffer.Data;
                var count = buffer.Count;
                for (var row = 0; row < count; row++)
                {
                    var offset = row * 3;
                    changed |= uf.Union(cells[offset], cells[offset + 1], cells[offset + 2], out _, out _);
                }

                buffer.Dispose();
            }

            return new TableChange(changed, changed);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Drops staged unions, resets the union-find and its rows, and, if there were rows, starts a new generation.
        /// egglog keeps the lookup and the version (core-relations/src/uf/mod.rs:286-289; map §8.1 row 12) and leaves
        /// staged unions queued.
        /// </remarks>
        public override void Clear()
        {
            pending.Clear();
            var hadRows = uf.Count > 0;
            uf.Clear();
            if (hadRows)
                generation++;
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>Clone for DisplacedTable</c> (core-relations/src/uf/mod.rs:210-220): the union-find and rows are
        /// copied; staged unions are not.
        /// </remarks>
        public override Table Clone() => new DisplacedTable(uf.Clone(), generation);

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuilder</c> (core-relations/src/uf/mod.rs:279-284).</remarks>
        public override Rebuilder? GetRebuilder(ReadOnlySpan<ColumnId> columns) => new Canonicalizer(uf, columns);

        /// <summary>
        /// Expands row <paramref name="row"/> into <paramref name="cells"/> and emits it if it satisfies the
        /// constraints.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EmitIfMatches<TSink>(int row, Span<uint> cells, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            uf.Expand(row, out cells[0], out cells[1], out cells[2]);
            if (constraints.IsEmpty == false && Constraint.EvaluateAll(constraints, cells) == false)
                return;

            sink.AddRow(row, cells);
        }

        static void CheckKey(ReadOnlySpan<uint> key)
        {
            if (key.Length != 1)
                throw new ArgumentException("A union-find lookup takes a key of exactly one cell.", nameof(key));
        }

    }

}
