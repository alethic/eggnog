using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A relation: egglog's <c>Table</c> trait together with <c>WrappedTable</c> (core-relations/src/table_spec.rs:145-362,
    /// 384-859). The base class the catalog, the planner and other cold paths hold tables by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The abstract members are the object-safe part of the trait: schema, version, subsets, refinement, point lookups,
    /// merging, staging, rebuild and cloning; each costs one virtual call, made per batch or per lookup, never per row.
    /// </para>
    /// <para>
    /// The generic scans (<see cref="ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>
    /// and the helpers built on it) are where egglog's <c>WrapperImpl&lt;T&gt;</c> downcasts once and runs a
    /// monomorphized loop. Here the base class switches on <see cref="Kind"/> once per call and calls the sealed class's
    /// non-virtual generic method, so the per-row loop is specialized for the sink and has no virtual call. Code that
    /// knows the concrete class (compiled plans, the table's own merge) calls the sealed class directly.
    /// </para>
    /// <para>
    /// Visibility: every path that yields rows to a caller filters out the rows its reader must not see. A table that
    /// is a layer of a child context's view has rows the view hides (<see cref="HiddenRows"/>) besides its tombstones.
    /// Every scan and refinement that yields rows has a hidden-aware overload taking the layer's hidden rows
    /// (<see cref="ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>,
    /// <see cref="ScanGeneric{TSink}(SubsetRef, HiddenRows, ref TSink)"/>, the <c>ScanProject</c> forms,
    /// <see cref="ForEachColumn{TSink}(SubsetRef, ColumnId, HiddenRows, ref TSink)"/> and <see cref="RefineVisible"/>).
    /// With <see cref="HiddenRows.None"/> they run the plain path; the plain overloads dispatch straight to it, so code
    /// that never sees a hidden set (the root, a bare database) pays nothing for them.
    /// </para>
    /// <para>
    /// Threading: every read may run on many threads at once while no thread merges, clears or rebuilds the table;
    /// <see cref="Merge"/>, <see cref="Clear"/>, <see cref="ApplyRebuild"/> and <see cref="RefreshRowsForValues"/>
    /// need exclusive access (egglog's <c>&amp;mut self</c>). Staging goes through <see cref="MutationBuffer"/>s and may
    /// happen concurrently with reads.
    /// </para>
    /// </remarks>
    internal abstract class Table
    {

        /// <summary>
        /// Initializes a new instance. Only the engine's sealed table classes derive from <see cref="Table"/>, which is
        /// what makes the kind switch exhaustive.
        /// </summary>
        /// <param name="kind">The concrete class.</param>
        /// <param name="arity">The number of columns.</param>
        private protected Table(TableKind kind, int arity)
        {
            Kind = kind;
            Arity = arity;
        }

        /// <summary>
        /// Gets the concrete class, for dispatching hot paths without virtual calls.
        /// </summary>
        public TableKind Kind { get; }

        /// <summary>
        /// Gets the number of columns.
        /// </summary>
        public int Arity { get; }

        /// <summary>
        /// Gets the schema. Immutable. egglog's <c>spec</c>.
        /// </summary>
        public abstract TableSpec Spec { get; }

        /// <summary>
        /// Gets the number of live rows. egglog's <c>len</c>. The <see cref="All"/> subset may be larger: it includes
        /// tombstones.
        /// </summary>
        public abstract int Count { get; }

        /// <summary>
        /// Gets whether the table has no live rows.
        /// </summary>
        public bool IsEmpty => Count == 0;

        /// <summary>
        /// Gets the current version. Row ids and subsets are valid only within one major generation.
        /// </summary>
        public abstract TableVersion Version { get; }

        /// <summary>
        /// Gets whether any row is a tombstone; when <c>false</c>, subsets need no liveness filtering. egglog's
        /// <c>has_stale_rows</c>.
        /// </summary>
        public abstract bool HasStaleRows { get; }

        /// <summary>
        /// Gets every physical row, tombstones included, as a dense subset. egglog's <c>all</c>. O(1).
        /// </summary>
        /// <returns>The subset.</returns>
        public abstract Subset All();

        /// <summary>
        /// Gets the rows appended since the physical row count was <paramref name="offset"/> (a
        /// <see cref="TableVersion.Minor"/> of the current generation). egglog's <c>updates_since</c>. O(1).
        /// </summary>
        /// <param name="offset">An earlier physical row count.</param>
        /// <returns>The dense subset <c>[offset, physical count)</c>.</returns>
        public abstract Subset UpdatesSince(int offset);

        /// <summary>
        /// Keeps the live rows of <paramref name="subset"/> that satisfy <paramref name="constraint"/>. egglog's
        /// <c>refine_one</c>. Tombstones never survive. O(<paramref name="subset"/> size).
        /// </summary>
        /// <param name="subset">The subset, consumed.</param>
        /// <param name="constraint">The constraint.</param>
        /// <returns>The refined subset, which the caller owns.</returns>
        public abstract Subset RefineOne(Subset subset, Constraint constraint);

        /// <summary>
        /// Keeps the live rows of <paramref name="subset"/> that satisfy every constraint. egglog's <c>refine</c>.
        /// </summary>
        /// <param name="subset">The subset, consumed.</param>
        /// <param name="constraints">The constraints.</param>
        /// <returns>The refined subset, which the caller owns.</returns>
        public Subset Refine(Subset subset, ReadOnlySpan<Constraint> constraints)
        {
            foreach (var constraint in constraints)
                subset = RefineOne(subset, constraint);

            return subset;
        }

        /// <summary>
        /// Keeps the live rows of <paramref name="subset"/>. egglog's <c>refine_live</c>, which filters with
        /// <c>col0 &lt; stale</c>.
        /// </summary>
        /// <param name="subset">The subset, consumed.</param>
        /// <returns>The refined subset, which the caller owns.</returns>
        public Subset RefineLive(Subset subset) => RefineOne(subset, Constraint.LtConst(new ColumnId(0), Value.Stale));

        /// <summary>
        /// Keeps the rows of <paramref name="subset"/> a view sees: live, and not in <paramref name="hidden"/>. With
        /// <see cref="HiddenRows.None"/> it is <see cref="RefineLive"/>. O(<paramref name="subset"/> size); the hidden
        /// test walks the sealed sets with a <see cref="HiddenCursor"/>, since
        /// <see cref="Subset.Retain{TFilter}(ref TFilter)"/> visits rows in ascending order.
        /// </summary>
        /// <param name="subset">Row ids of this table, consumed.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <returns>The visible rows, which the caller owns.</returns>
        public abstract Subset RefineVisible(Subset subset, HiddenRows hidden);

        /// <summary>
        /// Turns <paramref name="constraint"/> into a subset quickly when the table can (a range on a sorted column).
        /// The result may include tombstones. egglog's <c>fast_subset</c>.
        /// </summary>
        /// <param name="constraint">The constraint.</param>
        /// <param name="subset">The subset of rows that may satisfy it, which the caller owns.</param>
        /// <returns><c>false</c> if the constraint is slow and must be applied with <see cref="RefineOne"/>.</returns>
        public abstract bool TryFastSubset(Constraint constraint, out Subset subset);

        /// <summary>
        /// Applies the fast constraints to <see cref="All"/> and sorts the constraints into fast and slow ones. egglog's
        /// <c>split_fast_slow</c>. The result may include tombstones.
        /// </summary>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the constraints <see cref="TryFastSubset"/> handled.</param>
        /// <param name="slow">Receives the others.</param>
        /// <returns>The rows matching every fast constraint, which the caller owns.</returns>
        public Subset SplitFastSlow(ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);

            var result = All();
            foreach (var constraint in constraints)
            {
                if (TryFastSubset(constraint, out var sub))
                {
                    result.Intersect(sub.AsRef());
                    sub.Dispose();
                    fast.Add(constraint);
                }
                else
                {
                    slow.Add(constraint);
                }
            }

            return result;
        }

        /// <summary>
        /// Looks up the live row with key <paramref name="key"/> and copies it into <paramref name="destination"/>.
        /// egglog's <c>get_row</c>, without the allocation.
        /// </summary>
        /// <param name="key">The key; as many cells as the table has key columns.</param>
        /// <param name="destination">Receives the row; at least <see cref="Arity"/> cells.</param>
        /// <param name="rowId">The row id, or -1.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public abstract bool TryGetRow(ReadOnlySpan<uint> key, Span<uint> destination, out int rowId);

        /// <summary>
        /// Looks up the live row with key <paramref name="key"/> and returns a copy. egglog's <c>get_row</c>; allocates.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The row, or <c>null</c>.</returns>
        public Row? GetRow(ReadOnlySpan<uint> key)
        {
            var values = new uint[Arity];
            return TryGetRow(key, values, out var rowId) ? new Row(rowId, values) : null;
        }

        /// <summary>
        /// Looks up one column of the live row with key <paramref name="key"/>. egglog's <c>get_row_column</c>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public abstract bool TryGetRowColumn(ReadOnlySpan<uint> key, ColumnId column, out uint value);

        /// <summary>
        /// Applies every staged write, and may compact the table (a new major generation). egglog's <c>merge</c>.
        /// </summary>
        /// <param name="state">The state merge functions run in.</param>
        /// <returns>What changed.</returns>
        public abstract TableChange Merge(ExecutionState state);

        /// <summary>
        /// Creates a buffer for staging writes to this table. egglog's <c>new_buffer</c>; it does not notify any
        /// database (staging through an <see cref="ExecutionState"/> does).
        /// </summary>
        /// <returns>The buffer.</returns>
        public abstract MutationBuffer NewBuffer();

        /// <summary>
        /// Gets the writes staged for this table and not merged yet: the queues its <see cref="MutationBuffer"/>s flush
        /// into. Not in egglog's trait; exposed so that a run that throws can discard what it staged
        /// (<see cref="PendingState.Mark"/>, <see cref="PendingState.DiscardSince"/>).
        /// </summary>
        public abstract PendingState Pending { get; }

        /// <summary>
        /// Removes every row and every staged write. A nonempty table moves to a new major generation. egglog's
        /// <c>clear</c>.
        /// </summary>
        public abstract void Clear();

        /// <summary>
        /// Creates an independent copy of the table's contents. egglog's <c>dyn_clone</c>.
        /// </summary>
        /// <returns>The copy.</returns>
        public abstract Table Clone();

        /// <summary>
        /// Creates a rebuilder that rewrites <paramref name="columns"/> of other tables, if this table provides one.
        /// egglog's <c>rebuilder</c>.
        /// </summary>
        /// <param name="columns">The columns to rewrite.</param>
        /// <returns>The rebuilder, or <c>null</c>.</returns>
        public virtual Rebuilder? GetRebuilder(ReadOnlySpan<ColumnId> columns) => null;

        /// <summary>
        /// Stages the writes that rebuild this table against <paramref name="rebuilderTable"/>'s rebuilder; a later
        /// <see cref="Merge"/> applies them. egglog's <c>apply_rebuild</c>. Tables without rebuildable columns do
        /// nothing.
        /// </summary>
        /// <param name="rebuilderTableId">The id of the providing table.</param>
        /// <param name="rebuilderTable">The providing table (the union-find).</param>
        /// <param name="nextTimestamp">The timestamp rewritten rows get.</param>
        /// <param name="state">The execution state.</param>
        /// <returns>Whether any row may be removed or inserted.</returns>
        public virtual bool ApplyRebuild(TableId rebuilderTableId, Table rebuilderTable, uint nextTimestamp, ExecutionState state) => false;

        /// <summary>
        /// Makes the table read-only for good, because its context is freezing: the one freeze hook of every table
        /// kind. Implementations finish the work a frozen reader must not pay for (the root's union-find compresses
        /// every path; a sorted-writes table compacts where its gate allows and brings its rebuild index up to date)
        /// and seal their pending state, so that staging into the table throws and it never merges again. A second call
        /// does nothing. Requires exclusive access.
        /// </summary>
        /// <exception cref="InvalidOperationException">Writes are staged and not merged.</exception>
        public abstract void Freeze();

        /// <summary>
        /// Gets whether writes are staged into the table and not merged yet: what <see cref="Freeze"/> refuses, checked by
        /// a freezing database for every table before it freezes any.
        /// </summary>
        internal abstract bool HasPendingWrites { get; }

        /// <summary>
        /// Stages a re-insertion, with a fresh timestamp, of every live row whose rebuildable columns mention one of
        /// <paramref name="dirtyIds"/>, so seminaive sees them as new. egglog's <c>refresh_rows_for_values</c>.
        /// </summary>
        /// <param name="dirtyIds">The ids whose meaning changed in place.</param>
        /// <param name="nextTimestamp">The timestamp the rows get.</param>
        /// <returns>Whether anything was staged.</returns>
        public virtual bool RefreshRowsForValues(ReadOnlySpan<uint> dirtyIds, uint nextTimestamp) => false;

        /// <summary>
        /// Visits the rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that are live and
        /// satisfy every constraint, and returns the position to resume from, or -1 when the subset is exhausted.
        /// egglog's <c>scan_generic_bounded</c>. Dispatches once on <see cref="Kind"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type; a struct, so the scan is specialized for it.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="sink">Receives each row with its row id.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            switch (Kind)
            {
                case TableKind.SortedWrites:
                    return Unsafe.As<SortedWritesTable>(this).ScanGenericBounded(subset, start, count, constraints, ref sink);
                case TableKind.Displaced:
                    return Unsafe.As<DisplacedTable>(this).ScanGenericBounded(subset, start, count, constraints, ref sink);
                case TableKind.OverlayDisplaced:
                    return Unsafe.As<OverlayDisplacedTable>(this).ScanGenericBounded(subset, start, count, constraints, ref sink);
                default:
                    throw UnknownKind();
            }
        }

        /// <summary>
        /// Visits the rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that a view sees (live
        /// and not in <paramref name="hidden"/>) and that satisfy every constraint, and returns the position to resume
        /// from, or -1 when the subset is exhausted: the hidden-aware
        /// <see cref="ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>.
        /// Dispatches once on <see cref="Kind"/>; with <see cref="HiddenRows.None"/> each table runs its plain scan, so
        /// the only added cost is one test per call.
        /// </summary>
        /// <remarks>
        /// Positions count entries of the subset, as in the plain scan, so a chunked scan resumes exactly where the plain
        /// one would; hidden rows, like tombstones, are visited and dropped, so a chunk may emit fewer rows.
        /// </remarks>
        /// <typeparam name="TSink">The sink type; a struct, so the scan is specialized for it.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each visible row with its row id.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            switch (Kind)
            {
                case TableKind.SortedWrites:
                    return Unsafe.As<SortedWritesTable>(this).ScanGenericBounded(subset, start, count, constraints, hidden, ref sink);
                case TableKind.Displaced:
                    return Unsafe.As<DisplacedTable>(this).ScanGenericBounded(subset, start, count, constraints, hidden, ref sink);
                case TableKind.OverlayDisplaced:
                    return Unsafe.As<OverlayDisplacedTable>(this).ScanGenericBounded(subset, start, count, constraints, hidden, ref sink);
                default:
                    throw UnknownKind();
            }
        }

        /// <summary>
        /// Visits every live row of <paramref name="subset"/>. egglog's <c>scan_generic</c>. Dispatches once on
        /// <see cref="Kind"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="sink">Receives each row with its row id.</param>
        public void ScanGeneric<TSink>(SubsetRef subset, ref TSink sink)
            where TSink : struct, IRowSink
        {
            switch (Kind)
            {
                case TableKind.SortedWrites:
                    Unsafe.As<SortedWritesTable>(this).ScanGeneric(subset, ref sink);
                    return;
                case TableKind.Displaced:
                    Unsafe.As<DisplacedTable>(this).ScanGeneric(subset, ref sink);
                    return;
                case TableKind.OverlayDisplaced:
                    Unsafe.As<OverlayDisplacedTable>(this).ScanGeneric(subset, ref sink);
                    return;
                default:
                    throw UnknownKind();
            }
        }

        /// <summary>
        /// Visits every row of <paramref name="subset"/> a view sees: live and not in <paramref name="hidden"/>. The
        /// hidden-aware <see cref="ScanGeneric{TSink}(SubsetRef, ref TSink)"/>, which visibility-aware index builds
        /// scan with. Dispatches once on <see cref="Kind"/>.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each visible row with its row id.</param>
        public void ScanGeneric<TSink>(SubsetRef subset, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            switch (Kind)
            {
                case TableKind.SortedWrites:
                    Unsafe.As<SortedWritesTable>(this).ScanGeneric(subset, hidden, ref sink);
                    return;
                case TableKind.Displaced:
                    Unsafe.As<DisplacedTable>(this).ScanGeneric(subset, hidden, ref sink);
                    return;
                case TableKind.OverlayDisplaced:
                    Unsafe.As<OverlayDisplacedTable>(this).ScanGeneric(subset, hidden, ref sink);
                    return;
                default:
                    throw UnknownKind();
            }
        }

        /// <summary>
        /// Appends the live rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> to
        /// <paramref name="output"/>. egglog's <c>WrappedTable::scan_bounded</c>.
        /// </summary>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="output">Receives the rows, tagged with their row ids.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanBounded(SubsetRef subset, int start, int count, TaggedRowBuffer output)
        {
            var sink = new TaggedRowSink(output);
            return ScanGenericBounded(subset, start, count, ReadOnlySpan<Constraint>.Empty, ref sink);
        }

        /// <summary>
        /// Returns every live row of <paramref name="subset"/>. egglog's <c>WrappedTable::scan</c>.
        /// </summary>
        /// <param name="subset">The rows to scan.</param>
        /// <returns>The rows, tagged with their row ids; the caller owns the buffer.</returns>
        public TaggedRowBuffer Scan(SubsetRef subset)
        {
            var output = new TaggedRowBuffer(Arity);
            var next = ScanBounded(subset, 0, int.MaxValue, output);
            if (next >= 0)
                throw new InvalidOperationException("A scan of the whole subset stopped early.");

            return output;
        }

        /// <summary>
        /// Visits the projection onto <paramref name="columns"/> of the live rows at positions
        /// <c>[start, start + count)</c> of <paramref name="subset"/> that satisfy every constraint. egglog's
        /// <c>scan_project</c>: with no columns it emits nothing and returns -1. Specialized for one, two and three
        /// columns (<see cref="ProjectingSink{TSink}"/>).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="columns">The columns to project onto, in output order.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="sink">Receives each projected row with its row id.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanProject<TSink>(SubsetRef subset, ReadOnlySpan<ColumnId> columns, int start, int count, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            if (columns.Length == 0)
                return -1;

            var projection = MemoryMarshal.Cast<ColumnId, int>(columns);
            CheckColumns(projection);
            var projecting = new ProjectingSink<TSink>(sink, projection);
            var next = ScanGenericBounded(subset, start, count, constraints, ref projecting);
            sink = projecting.Inner;
            projecting.Release();
            return next;
        }

        /// <summary>
        /// Appends the projection onto <paramref name="columns"/> of the matching live rows to
        /// <paramref name="output"/>; see
        /// <see cref="ScanProject{TSink}(SubsetRef, ReadOnlySpan{ColumnId}, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>.
        /// </summary>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="columns">The columns to project onto.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="output">Receives the projected rows; its arity is the number of columns.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanProject(SubsetRef subset, ReadOnlySpan<ColumnId> columns, int start, int count, ReadOnlySpan<Constraint> constraints, TaggedRowBuffer output)
        {
            var sink = new TaggedRowSink(output);
            return ScanProject(subset, columns, start, count, constraints, ref sink);
        }

        /// <summary>
        /// Visits the projection onto <paramref name="columns"/> of the rows at positions <c>[start, start + count)</c>
        /// of <paramref name="subset"/> that a view sees (live and not in <paramref name="hidden"/>) and that satisfy
        /// every constraint: the hidden-aware
        /// <see cref="ScanProject{TSink}(SubsetRef, ReadOnlySpan{ColumnId}, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>
        /// form. With no columns it emits nothing and returns -1.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="columns">The columns to project onto, in output order.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each projected visible row with its row id.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanProject<TSink>(SubsetRef subset, ReadOnlySpan<ColumnId> columns, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            if (columns.Length == 0)
                return -1;

            var projection = MemoryMarshal.Cast<ColumnId, int>(columns);
            CheckColumns(projection);
            var projecting = new ProjectingSink<TSink>(sink, projection);
            var next = ScanGenericBounded(subset, start, count, constraints, hidden, ref projecting);
            sink = projecting.Inner;
            projecting.Release();
            return next;
        }

        /// <summary>
        /// Appends the projection onto <paramref name="columns"/> of the matching visible rows to
        /// <paramref name="output"/>; see
        /// <see cref="ScanProject{TSink}(SubsetRef, ReadOnlySpan{ColumnId}, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>.
        /// </summary>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="columns">The columns to project onto.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="output">Receives the projected rows; its arity is the number of columns.</param>
        /// <returns>The next position, or -1.</returns>
        public int ScanProject(SubsetRef subset, ReadOnlySpan<ColumnId> columns, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, TaggedRowBuffer output)
        {
            var sink = new TaggedRowSink(output);
            return ScanProject(subset, columns, start, count, constraints, hidden, ref sink);
        }

        /// <summary>
        /// Visits <paramref name="column"/> of every live row of <paramref name="subset"/>, as a one-cell row. egglog's
        /// <c>for_each_col</c>: no intermediate buffer.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="column">The column.</param>
        /// <param name="sink">Receives each row id with a one-cell span holding the column's value.</param>
        public void ForEachColumn<TSink>(SubsetRef subset, ColumnId column, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ReadOnlySpan<int> projection = [column.Rep];
            CheckColumns(projection);
            var projecting = new ProjectingSink<TSink>(sink, projection);
            ScanGeneric(subset, ref projecting);
            sink = projecting.Inner;
            projecting.Release();
        }

        /// <summary>
        /// Visits <paramref name="column"/> of every row of <paramref name="subset"/> a view sees (live and not in
        /// <paramref name="hidden"/>), as a one-cell row: the hidden-aware
        /// <see cref="ForEachColumn{TSink}(SubsetRef, ColumnId, ref TSink)"/>, which the join's small per-subset
        /// indexes build with (<see cref="SparseColumnIndex"/>).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan.</param>
        /// <param name="column">The column.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each visible row id with a one-cell span holding the column's value.</param>
        public void ForEachColumn<TSink>(SubsetRef subset, ColumnId column, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ReadOnlySpan<int> projection = [column.Rep];
            CheckColumns(projection);
            var projecting = new ProjectingSink<TSink>(sink, projection);
            ScanGeneric(subset, hidden, ref projecting);
            sink = projecting.Inner;
            projecting.Release();
        }

        /// <summary>
        /// Throws unless every column is below <see cref="Arity"/>.
        /// </summary>
        void CheckColumns(ReadOnlySpan<int> columns)
        {
            foreach (var column in columns)
                if ((uint)column >= (uint)Arity)
                    throw new ArgumentOutOfRangeException(nameof(columns), column, $"The table has {Arity} columns.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        Exception UnknownKind() => new InvalidOperationException($"Unknown table kind {Kind}.");

    }

}
