using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A child context's union-find exposed as a table: the child's <see cref="UnionFindOverlay"/> and
    /// <see cref="DisplacedJournal"/> over its parent's union-find, the counterpart of the root's
    /// <see cref="DisplacedTable"/> at <c>TableId 0</c> of the child's database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are the journal's: row <c>r</c> is <c>[id, find_C(id), ts]</c>, one per id that stopped being canonical in
    /// the child. Column 1 is computed on every read with <see cref="Finder"/>, the parent's find extended with the
    /// overlay, so it is always current and never cacheable; rows are in nondecreasing timestamp order. Scans, subsets
    /// and versions cover the journal only, which is the child's own layer: <see cref="TableVersion.Minor"/> is the
    /// journal's length and the generation never changes, so a layer's rebuild finds the ids displaced since its last one
    /// as a suffix (the rebuilder's hint column). Point lookups are view-level: <see cref="TryGetRow"/> answers for an id
    /// displaced in an ancestor too, and column 1 is answered for any id (the bridge's <c>lookup_uf</c>).
    /// </para>
    /// <para>
    /// To union <c>a</c> and <c>b</c> in the child, stage <c>[a, b, ts]</c>; <see cref="Merge"/> maps both through the
    /// parent's find, merges their overlay classes and journals the loser. The context must be current with its parent
    /// when it merges, which its writes and rebuilds guarantee. Rows are never deleted or cleared, and the table cannot
    /// be cloned (a child's union-find is not a standalone copy).
    /// </para>
    /// <para>
    /// Threading: reads run concurrently while no merge runs; <see cref="Merge"/> is exclusive (the owning context's
    /// domain). Once frozen, the table is sealed and read-only.
    /// </para>
    /// </remarks>
    internal sealed class OverlayDisplacedTable : Table
    {

        readonly LayeredUnionFind parentFinder;
        readonly UnionFindOverlay overlay;
        readonly DisplacedJournal journal;
        readonly Table parentUnionFind;
        readonly IdBlockAllocator? localIds;
        readonly LayeredUnionFind finder;
        readonly PendingState pending;
        long ownUnions;
        long parentIdLosers;
        volatile bool frozen;

        /// <summary>
        /// Initializes a child's union-find table.
        /// </summary>
        /// <param name="parentFinder">The parent's find, which the child extends with <paramref name="overlay"/>.</param>
        /// <param name="overlay">The child's overlay.</param>
        /// <param name="journal">The child's displaced journal.</param>
        /// <param name="parentUnionFind">The parent's union-find table (a <see cref="DisplacedTable"/> or another
        /// <see cref="OverlayDisplacedTable"/>), which answers lookups of ids displaced in an ancestor.</param>
        /// <param name="localIds">The child's id blocks, to count own losers that are parent ids beaten by a local id
        /// (<see cref="ParentIdLosers"/>); <c>null</c> to count none.</param>
        /// <exception cref="ArgumentException"><paramref name="parentUnionFind"/> is not a union-find table.</exception>
        public OverlayDisplacedTable(LayeredUnionFind parentFinder, UnionFindOverlay overlay, DisplacedJournal journal, Table parentUnionFind, IdBlockAllocator? localIds = null)
            : base(TableKind.OverlayDisplaced, 3)
        {
            ArgumentNullException.ThrowIfNull(parentFinder);
            ArgumentNullException.ThrowIfNull(overlay);
            ArgumentNullException.ThrowIfNull(journal);
            ArgumentNullException.ThrowIfNull(parentUnionFind);
            if (parentUnionFind.Kind is not (TableKind.Displaced or TableKind.OverlayDisplaced))
                throw new ArgumentException("The parent's union-find must be a union-find table.", nameof(parentUnionFind));

            this.parentFinder = parentFinder;
            this.overlay = overlay;
            this.journal = journal;
            this.parentUnionFind = parentUnionFind;
            this.localIds = localIds;
            finder = parentFinder.Extend(overlay);
            pending = new PendingState(new ShardData(1), lazyQueues: true);
        }

        /// <summary>
        /// Gets the child's find: the parent's find followed by the overlay.
        /// </summary>
        public LayeredUnionFind Finder => finder;

        /// <summary>
        /// Gets the parent's find, which staged unions are mapped through.
        /// </summary>
        public LayeredUnionFind ParentFinder => parentFinder;

        /// <summary>
        /// Gets the child's overlay.
        /// </summary>
        public UnionFindOverlay Overlay => overlay;

        /// <summary>
        /// Gets the child's displaced journal: the table's rows.
        /// </summary>
        public DisplacedJournal Journal => journal;

        /// <summary>
        /// Gets the parent's union-find table.
        /// </summary>
        public Table ParentUnionFind => parentUnionFind;

        /// <summary>
        /// Gets whether unions are staged and not merged yet. Exact for this single-shard table.
        /// </summary>
        public bool HasPendingUnions => pending.RowQueue(0).IsEmpty == false;

        /// <inheritdoc />
        internal override bool HasPendingWrites => HasPendingUnions;

        /// <summary>
        /// Gets whether the table is frozen.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets the number of effective unions merged into the table (<see cref="UnionFindStatistics.OwnUnions"/>).
        /// </summary>
        public long OwnUnions => Interlocked.Read(ref ownUnions);

        /// <summary>
        /// Gets the number of own unions whose loser was an id of an ancestor and whose winner an id local to the child
        /// (<see cref="UnionFindStatistics.ParentIdLosers"/>): what smaller-id-wins costs when a parent that is not
        /// frozen lifts terms after cases created them.
        /// </summary>
        public long ParentIdLosers => Interlocked.Read(ref parentIdLosers);

        /// <inheritdoc />
        public override TableSpec Spec => DisplacedTable.DisplacedSpec;

        /// <inheritdoc />
        public override int Count => journal.Count;

        /// <inheritdoc />
        public override TableVersion Version => new(0, journal.Count);

        /// <inheritdoc />
        public override bool HasStaleRows => false;

        /// <inheritdoc />
        public override Subset All() => Subset.Dense(0, journal.Count);

        /// <inheritdoc />
        public override Subset UpdatesSince(int offset)
        {
            if ((uint)offset > (uint)journal.Count)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The table has {journal.Count} rows.");

            return Subset.Dense(offset, journal.Count);
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in the child without writing: column 1 for any id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FindNaive(uint id) => finder.Find(id);

        /// <summary>
        /// Gets one cell of journal row <paramref name="row"/>: the displaced id, its canonical id in the child, or the
        /// timestamp. O(1), or O(find) for column 1.
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
                    return journal.GetId(row);
                case 1:
                    return finder.Find(journal.GetId(row));
                case 2:
                    return journal.GetTimestamp(row);
                default:
                    throw new ArgumentOutOfRangeException(nameof(column), column, "The union-find table has three columns.");
            }
        }

        /// <summary>
        /// Visits the journal rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that satisfy
        /// every constraint, each expanded to its three cells;
        /// <see cref="DisplacedTable.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>
        /// over the journal. Hides
        /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>,
        /// which dispatches here.
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

            if (bounds.End > journal.Count || bounds.Start < 0)
                throw new ArgumentOutOfRangeException(nameof(subset), bounds.End, $"The subset extends past the table's {journal.Count} rows.");

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
        /// Visits the journal rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that are not in
        /// <paramref name="hidden"/> and satisfy every constraint: the plain scan through a
        /// <see cref="HiddenSkippingSink{TSink}"/>, as <see cref="DisplacedTable"/> does. Hides the hidden-aware
        /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>,
        /// which dispatches here; with <see cref="HiddenRows.None"/> it is the plain scan.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="hidden">The rows hidden from the reader; sealed.</param>
        /// <param name="sink">Receives each expanded visible row with its row id; must not keep the span.</param>
        /// <returns>The next position, or -1.</returns>
        public new int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            if (hidden.IsNone)
                return ScanGenericBounded(subset, start, count, constraints, ref sink);

            var skipping = new HiddenSkippingSink<TSink>(sink, hidden);
            var next = ScanGenericBounded(subset, start, count, constraints, ref skipping);
            sink = skipping.Inner;
            return next;
        }

        /// <summary>
        /// Visits every journal row of <paramref name="subset"/>. Hides
        /// <see cref="Table.ScanGeneric{TSink}(SubsetRef, ref TSink)"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="sink">Receives each expanded row with its row id.</param>
        public new void ScanGeneric<TSink>(SubsetRef subset, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ScanGenericBounded(subset, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref sink);
        }

        /// <summary>
        /// Visits every journal row of <paramref name="subset"/> not in <paramref name="hidden"/>. Hides
        /// <see cref="Table.ScanGeneric{TSink}(SubsetRef, HiddenRows, ref TSink)"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="hidden">The rows hidden from the reader; sealed.</param>
        /// <param name="sink">Receives each expanded visible row with its row id.</param>
        public new void ScanGeneric<TSink>(SubsetRef subset, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ScanGenericBounded(subset, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, hidden, ref sink);
        }

        /// <inheritdoc />
        /// <remarks>The union-find's <c>refine_one</c> on the expanded journal rows.</remarks>
        public override Subset RefineOne(Subset subset, Constraint constraint)
        {
            if ((uint)constraint.Column >= 3u || (constraint.Kind == ConstraintKind.Eq && (uint)constraint.OtherColumn >= 3u))
                throw new ArgumentOutOfRangeException(nameof(constraint), constraint, "The union-find table has three columns.");

            var filter = new OverlayDisplacedConstraintFilter(this, constraint);
            subset.Retain(ref filter);
            return subset;
        }

        /// <inheritdoc />
        /// <remarks>
        /// As <see cref="DisplacedTable.RefineVisible"/>, over the journal: <see cref="Table.RefineLive"/> (every journal
        /// row is live), then a <see cref="HiddenOnlyFilter"/> when something is hidden. O(subset size).
        /// </remarks>
        public override Subset RefineVisible(Subset subset, HiddenRows hidden)
        {
            var live = RefineLive(subset);
            if (hidden.IsNone)
                return live;

            var filter = new HiddenOnlyFilter(hidden);
            live.Retain(ref filter);
            return live;
        }

        /// <summary>
        /// Gets whether journal row <paramref name="row"/> satisfies <paramref name="constraint"/>, with column 1
        /// computed.
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
        /// As <see cref="DisplacedTable.TryFastSubset"/>, over the journal: an <c>EqConst</c> on column 0 is a lookup;
        /// range constraints on the timestamp column are binary searches; column 1 is never fast.
        /// </remarks>
        public override bool TryFastSubset(Constraint constraint, out Subset subset)
        {
            subset = default;
            if (constraint.Kind == ConstraintKind.Eq)
                return false;

            if (constraint.Kind == ConstraintKind.EqConst && constraint.Column == 0)
            {
                subset = journal.TryGetRow(constraint.Constant, out var row) ? Subset.Dense(row, row + 1) : Subset.Empty;
                return true;
            }

            if (constraint.Column != 2)
                return false;

            var found = journal.TimestampBounds(constraint.Constant, out var start, out var end);
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
                    subset = Subset.Dense(end, journal.Count);
                    return true;
                case ConstraintKind.LeConst:
                    subset = Subset.Dense(0, end);
                    return true;
                default:
                    subset = Subset.Dense(start, journal.Count);
                    return true;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// View-level: an id the child journaled gives its journal row; an id displaced in an ancestor's view gives the
        /// ancestor's row with column 1 replaced by the child's canonical id, and <paramref name="rowId"/> -1, since
        /// the row is not one of this table's.
        /// </remarks>
        public override bool TryGetRow(ReadOnlySpan<uint> key, Span<uint> destination, out int rowId)
        {
            CheckKey(key);
            if (destination.Length < 3)
                throw new ArgumentException("The destination needs room for 3 cells.", nameof(destination));

            var id = key[0];
            if (journal.TryGetRow(id, out rowId))
            {
                destination[0] = id;
                destination[1] = finder.Find(id);
                destination[2] = journal.GetTimestamp(rowId);
                return true;
            }

            rowId = -1;
            if (parentUnionFind.TryGetRow(key, destination, out _) == false)
                return false;

            destination[1] = finder.Find(id);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>Column 1 is the child's canonical id of any key, displaced or not; columns 0 and 2 are view-level.</remarks>
        public override bool TryGetRowColumn(ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            CheckKey(key);
            return TryGetColumn(key[0], column.Rep, out value);
        }

        /// <summary>
        /// Gets column <paramref name="column"/> for key <paramref name="key"/>: column 1 is the child's canonical id of
        /// any id; columns 0 and 2 exist for ids displaced in the child's view (its journal, then its ancestors). The form
        /// <see cref="OverlayDisplacedLookup"/> probes with.
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
                value = finder.Find(key);
                return true;
            }

            if ((uint)column > 2u)
                throw new ArgumentOutOfRangeException(nameof(column), column, "The union-find table has three columns.");

            if (journal.TryGetRow(key, out var row))
            {
                value = column == 0 ? key : journal.GetTimestamp(row);
                return true;
            }

            return TryGetAncestorColumn(key, column, out value);
        }

        /// <inheritdoc />
        public override MutationBuffer NewBuffer() => new(pending, 3, 1, false);

        /// <inheritdoc />
        public override PendingState Pending => pending;

        /// <inheritdoc />
        /// <remarks>
        /// For each staged <c>[l, r, ts]</c>, in queue and staging order, maps both ids through the parent's find,
        /// merges their overlay classes, and journals the loser at <c>ts</c>. A union between distinct classes whose
        /// timestamp precedes the journal's last row throws before changing anything (the order check of
        /// <see cref="DisplacedUnionFind.Union"/>); a redundant union may carry an old timestamp. A row counts as both
        /// added and removed. Precondition: the owning context is current with its parent.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The table is frozen, or a union's timestamp goes backwards.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An id exceeds <see cref="UnionFind.MaxId"/>.</exception>
        public override TableChange Merge(ExecutionState state)
        {
            if (frozen)
                ThrowFrozen();

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
                    changed |= UnionOne(cells[offset], cells[offset + 1], cells[offset + 2]);
                }

                buffer.Dispose();
            }

            return new TableChange(changed, changed);
        }

        /// <inheritdoc />
        /// <remarks>A child's union-find is never cleared.</remarks>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public override void Clear()
        {
            throw new InvalidOperationException("A child context's union-find cannot be cleared: its displacements are never retracted.");
        }

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always: a child's union-find reads through its ancestors'.</exception>
        public override Table Clone()
        {
            throw new NotSupportedException("A child context's union-find cannot be cloned.");
        }

        /// <inheritdoc />
        /// <remarks>An <see cref="OverlayCanonicalizer"/> over <see cref="Finder"/>, with hint column 0 (the journal).</remarks>
        public override Rebuilder? GetRebuilder(ReadOnlySpan<ColumnId> columns) => new OverlayCanonicalizer(finder, columns);

        /// <inheritdoc />
        /// <remarks>
        /// Freezes the overlay and seals the pending state; afterwards <see cref="Merge"/> throws and staging a union
        /// throws. A second call does nothing.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Unions are staged and not merged.</exception>
        public override void Freeze()
        {
            if (frozen)
                return;

            if (HasPendingUnions)
                throw new InvalidOperationException("The union-find has staged unions that are not merged; merge them before freezing.");

            overlay.Freeze();
            pending.Seal();
            frozen = true;
        }

        /// <summary>
        /// Applies one staged union.
        /// </summary>
        /// <returns>Whether the two ids were in different classes.</returns>
        bool UnionOne(uint left, uint right, uint timestamp)
        {
            if (left > UnionFind.MaxId || right > UnionFind.MaxId)
                ThrowIdOutOfRange(Math.Max(left, right));

            var a = parentFinder.Find(left);
            var b = parentFinder.Find(right);
            if (journal.Count > 0 && timestamp < journal.LastTimestamp && overlay.Map(a) != overlay.Map(b))
                ThrowDecreasingTimestamp(timestamp, journal.LastTimestamp);

            if (overlay.Union(a, b, out var winner, out var loser) == false)
                return false;

            journal.Append(loser, timestamp);
            Interlocked.Increment(ref ownUnions);
            if (localIds is { } ids && ids.Contains(winner) && ids.Contains(loser) == false)
                Interlocked.Increment(ref parentIdLosers);

            return true;
        }

        /// <summary>
        /// Reads column 0 or 2 of an id displaced in an ancestor's view, from the parent's union-find table.
        /// </summary>
        bool TryGetAncestorColumn(uint key, int column, out uint value)
        {
            Span<uint> probe = stackalloc uint[1];
            probe[0] = key;
            return parentUnionFind.TryGetRowColumn(probe, new ColumnId(column), out value);
        }

        /// <summary>
        /// Expands journal row <paramref name="row"/> into <paramref name="cells"/> and emits it if it satisfies the
        /// constraints.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EmitIfMatches<TSink>(int row, Span<uint> cells, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var id = journal.GetId(row);
            cells[0] = id;
            cells[1] = finder.Find(id);
            cells[2] = journal.GetTimestamp(row);
            if (constraints.IsEmpty == false && Constraint.EvaluateAll(constraints, cells) == false)
                return;

            sink.AddRow(row, cells);
        }

        static void CheckKey(ReadOnlySpan<uint> key)
        {
            if (key.Length != 1)
                throw new ArgumentException("A union-find lookup takes a key of exactly one cell.", nameof(key));
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowFrozen()
        {
            throw new InvalidOperationException("The union-find of a frozen context cannot be written.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDecreasingTimestamp(uint timestamp, uint last)
        {
            throw new InvalidOperationException(
                $"Union at timestamp {timestamp} follows a union at timestamp {last}: timestamps must not decrease.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowIdOutOfRange(uint id)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Union-find identifiers cannot exceed {UnionFind.MaxId}.");
        }

    }

}
