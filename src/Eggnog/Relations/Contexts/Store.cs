using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An e-graph store with colored contexts: the root <see cref="Context"/> (egglog's database, unchanged), the
    /// store-wide schema, the union-find table, and the two counters every context shares, ids and the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction lays the store out as egglog's bridge lays out its database (egglog-bridge/src/lib.rs:143-152):
    /// the union-find is table 0, the id counter is counter 0 and the clock is counter 1, incremented to 1 so the first
    /// epoch is 1. The bridge on a store uses these and adds no counters or union-find of its own. A bare
    /// <see cref="Database"/> without a store behaves exactly as egglog's.
    /// </para>
    /// <para>
    /// Definitions. Tables are added through <see cref="AddTable"/> at any time, children or not: under
    /// <see cref="DefinitionLock"/>, inside the root's thread domain while the root is not frozen, and published to the
    /// schema copy-on-write so running children never see a torn array. The root database's own
    /// <see cref="Database.AddTable"/> is refused, so every table of the store is known to its schema, which children
    /// read and the root's rebuild walks. Counters are refused once any child has been created, because adding one may
    /// reallocate the counter storage that child threads increment.
    /// </para>
    /// <para>
    /// Time. <see cref="ReadClock"/> returns the current epoch and <see cref="AdvanceClock"/> moves it on; a context
    /// reads it at the start of each run, pass or write batch and advances it at the end. Ids: the root mints directly
    /// from <see cref="IdCounter"/>, children from blocks reserved on it, so ids are unique store-wide.
    /// </para>
    /// <para>
    /// Limits. Neither counter is ever reclaimed. Every path that mints an id (<see cref="Context.NewId"/>, a child's
    /// <see cref="IdBlockAllocator"/>, a rule's mint at the root through <see cref="Database.IncrementCounter"/>, and a
    /// container's id) applies one bound, <see cref="UnionFind.MaxId"/>, through <see cref="CheckId"/> or the same
    /// test, and throws <see cref="OverflowException"/> past it; the clock applies <see cref="CheckEpoch"/>, which throws
    /// <see cref="OverflowException"/> at <see cref="Value.StaleRep"/>. Running out is checked before the operation that
    /// needs the id or the epoch changes anything it cannot discard, so it does not fault a child
    /// (<see cref="Context.Fault"/>). <see cref="IdsReserved"/> and <see cref="EpochsUsed"/> report how far each counter
    /// has gone, against <see cref="IdLimit"/> and <see cref="EpochLimit"/>.
    /// </para>
    /// </remarks>
    internal sealed class Store
    {

        /// <summary>
        /// The name of the union-find table.
        /// </summary>
        internal const string UnionFindName = "$uf";

        readonly object definitionLock = new();
        volatile bool hasHadChildren;

        /// <summary>
        /// Initializes a store with an empty root: the union-find as table 0, the id and clock counters as counters 0
        /// and 1, the clock at 1.
        /// </summary>
        /// <param name="parallelism">The root database's parallelism; <see cref="ParallelHeuristics.Default"/> if
        /// <c>null</c>.</param>
        public Store(ParallelHeuristics? parallelism = null)
        {
            var database = new Database(parallelism);
            UnionFindTable = database.AddTableCore(new DisplacedTable(), name: UnionFindName);
            RootUnionFind = (DisplacedTable)database.GetTable(UnionFindTable);
            IdCounter = database.AddCounter();
            ClockCounter = database.AddCounter();
            Counters = database.Counters;
            Counters.Increment(ClockCounter);
            ContainerLog = database.Containers.Log;
            Schema = new LayeredSchema(this);
            Root = new Context(this, database);
        }

        /// <summary>
        /// Gets the root context, which holds what is true unconditionally.
        /// </summary>
        public Context Root { get; }

        /// <summary>
        /// Gets the store-wide schema that every context reads.
        /// </summary>
        public LayeredSchema Schema { get; }

        /// <summary>
        /// Gets the union-find table's id, 0 (egglog-bridge/src/lib.rs:143-148).
        /// </summary>
        public TableId UnionFindTable { get; }

        /// <summary>
        /// Gets the root's union-find table.
        /// </summary>
        public DisplacedTable RootUnionFind { get; }

        /// <summary>
        /// Gets the id counter, counter 0 (egglog-bridge/src/lib.rs:149).
        /// </summary>
        public CounterId IdCounter { get; }

        /// <summary>
        /// Gets the clock, counter 1, which starts at 1 (egglog-bridge/src/lib.rs:150-152).
        /// </summary>
        public CounterId ClockCounter { get; }

        /// <summary>
        /// Gets the root's container log, which the root's container environments own
        /// (<see cref="ContainerValues.Log"/>): while the root has live descendants, the containers of classes that
        /// appear in its environments and the ids its container rebuild changed in place, from which each child learns
        /// of them through its cursor into the root (<see cref="Relations.ContainerLog"/>). A child's own log belongs to
        /// its container layers (<see cref="ContainerLayers.Log"/>).
        /// </summary>
        public ContainerLog ContainerLog { get; }

        /// <summary>
        /// Gets the number of ids the store can hand out: ids 0 to <see cref="UnionFind.MaxId"/>.
        /// </summary>
        public static long IdLimit => (long)UnionFind.MaxId + 1;

        /// <summary>
        /// Gets the number of epochs the clock can hand out: 1 to <c>uint.MaxValue - 1</c>, since
        /// <see cref="Value.StaleRep"/> is never an epoch (<see cref="ReadClock"/>).
        /// </summary>
        public const long EpochLimit = Value.StaleRep - 1L;

        /// <summary>
        /// Gets the ids handed out or reserved so far: the root's own mints, every block a child reserved, and
        /// container ids. At most <see cref="IdLimit"/>: a reservation refused past the limit still moves the counter,
        /// and is not counted. A snapshot; one volatile read.
        /// </summary>
        public long IdsReserved => Math.Min(Counters.Read(IdCounter), IdLimit);

        /// <summary>
        /// Gets the epochs used so far: the clock's value less its starting value, 1. At most
        /// <see cref="EpochLimit"/>. A snapshot; one volatile read.
        /// </summary>
        public long EpochsUsed => Math.Min(Counters.Read(ClockCounter) - 1, EpochLimit);

        /// <summary>
        /// Gets whether the store has no id left to hand out: the id counter has passed <see cref="UnionFind.MaxId"/>.
        /// </summary>
        public bool IdsExhausted => Counters.Read(IdCounter) > UnionFind.MaxId;

        /// <summary>
        /// Gets whether the store's clock has run out: its value has reached <see cref="Value.StaleRep"/>, so every
        /// clock read throws (<see cref="CheckEpoch"/>).
        /// </summary>
        public bool ClockExhausted => Counters.Read(ClockCounter) >= Value.StaleRep;

        /// <summary>
        /// Gets the counters every context shares.
        /// </summary>
        internal CounterSet Counters { get; }

        /// <summary>
        /// Gets the lock definitions are added under.
        /// </summary>
        internal object DefinitionLock => definitionLock;

        /// <summary>
        /// Gets whether any child context has ever been created. Once set, counters can no longer be added.
        /// </summary>
        internal bool HasHadChildren => hasHadChildren;

#if DEBUG
        /// <summary>
        /// DEBUG only: whether a caller has written rows through a database's raw execution-state API
        /// (<see cref="Database.WithExecutionState{TResult}"/>, <see cref="Database.CreateExecutionState"/>), whose
        /// rows carry the stamps the caller gives them. Only then may a catch-up re-stamp an ancestor row: every engine
        /// and bridge path stamps with the state's timestamp, so a re-stamped copy in a store without raw writes is a
        /// defect, which <c>ContextTables</c> asserts. Set once, never cleared; not synchronized (a stale read can only
        /// skip the assertion).
        /// </summary>
        internal bool HasRawWrites { get; set; }
#endif

        /// <summary>
        /// Gets the current epoch. Thread-safe, one volatile read.
        /// </summary>
        /// <remarks>
        /// Checked: the last epoch is <c>uint.MaxValue - 1</c>. <see cref="uint.MaxValue"/> is
        /// <see cref="Value.StaleRep"/>, which no run could use: a rule reading the clock (egglog's <c>next_ts</c>)
        /// would be refused by the counter conversion, and a stamp in a row's first cell would mark the row deleted.
        /// </remarks>
        /// <returns>The clock's value.</returns>
        /// <exception cref="OverflowException">The clock has reached <see cref="uint.MaxValue"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadClock() => CheckEpoch(Counters.Read(ClockCounter));

        /// <summary>
        /// Advances the clock by one: egglog-bridge's <c>inc_ts</c>. Thread-safe.
        /// </summary>
        public void AdvanceClock() => Counters.Increment(ClockCounter);

        /// <summary>
        /// Adds a function table to the root and registers it with the schema. Allowed at any time; runs under
        /// <see cref="DefinitionLock"/> and, while the root is not frozen, inside the root's thread domain, so it
        /// cannot run concurrently with root work. Touches no other context.
        /// </summary>
        /// <param name="table">The table; a <see cref="SortedWritesTable"/> not part of any database.</param>
        /// <param name="readDependencies">The tables its merge function reads; already added.</param>
        /// <param name="writeDependencies">The tables its merge function only writes.</param>
        /// <param name="name">The table's name, or <c>null</c>.</param>
        /// <param name="contextPrivate">Whether descendants never read the table: it is then not layered, never gated,
        /// and may be retracted freely, even while the context has live descendants.</param>
        /// <returns>The table's id.</returns>
        /// <exception cref="ArgumentException">The table is not a <see cref="SortedWritesTable"/>, is already part of the
        /// database, or a read dependency is not added.</exception>
        /// <exception cref="InvalidOperationException">The root's thread domain is held by another thread.</exception>
        public TableId AddTable(Table table, ReadOnlySpan<TableId> readDependencies = default, ReadOnlySpan<TableId> writeDependencies = default, string? name = null, bool contextPrivate = false)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (table is not SortedWritesTable)
                throw new ArgumentException("Only sorted-writes tables can be added to a store; the union-find is its own.", nameof(table));

            lock (definitionLock)
            {
                // A root that is not frozen cannot freeze while this thread holds its domain, so the flag read once the
                // domain is held is stable. A freeze can complete on another thread between the first read and the enter
                // (it holds the domain until its frozen flag is written), so the flag is read again inside: a root found
                // frozen there takes the frozen path, without its domain.
                var root = Root;
                var frozenRoot = root.IsFrozen;
                var scope = frozenRoot ? default : root.Domain.Enter();
                try
                {
                    if (frozenRoot == false && root.IsFrozen)
                    {
                        scope.Dispose();
                        scope = default;
                        frozenRoot = true;
                    }

                    var id = root.Database.AddTableCore(table, readDependencies, writeDependencies, name);
                    Schema.Register(id, contextPrivate);

                    // A table added to a frozen root is frozen at once, empty, and its frozen root view is published;
                    // frozen children answer for it through their parents.
                    if (frozenRoot)
                    {
                        var info = root.Database.GetTableInfo(id);
                        info.Table.Freeze();
                        info.FreezeIndexes();
                        Schema.PublishFrozenRootView(id);
                    }

                    return id;
                }
                finally
                {
                    scope.Dispose();
                }
            }
        }

        /// <summary>
        /// Adds a counter to the store, starting at zero.
        /// </summary>
        /// <returns>The counter's id.</returns>
        /// <exception cref="InvalidOperationException">A child context has been created.</exception>
        public CounterId AddCounter()
        {
            lock (definitionLock)
                return Root.Database.AddCounter();
        }

        /// <summary>
        /// Designates <paramref name="table"/>, a registered nullary relation, as the store's contradiction table. Once
        /// per store.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <exception cref="InvalidOperationException">A contradiction table is designated already.</exception>
        /// <exception cref="ArgumentException">The table has key columns.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The table is not registered.</exception>
        public void DesignateContradiction(TableId table)
        {
            lock (definitionLock)
                Schema.DesignateContradiction(table);
        }

        /// <summary>
        /// Records that a child context has been created, which seals the counters.
        /// </summary>
        internal void NoteChildCreated() => hasHadChildren = true;

        /// <summary>
        /// Converts a clock value to an epoch, refusing <see cref="Value.StaleRep"/> and beyond: the one bound every
        /// clock read of the store applies, here and in <see cref="Database"/>'s timestamp defaults.
        /// </summary>
        /// <param name="value">The clock's value.</param>
        /// <returns>The epoch.</returns>
        /// <exception cref="OverflowException"><paramref name="value"/> is at least <see cref="uint.MaxValue"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint CheckEpoch(long value)
        {
            if ((ulong)value >= Value.StaleRep)
                ThrowClockExhausted(value);

            return (uint)value;
        }

        /// <summary>
        /// Converts a value of the store's id counter to an id, refusing anything past <see cref="UnionFind.MaxId"/>, the
        /// largest id a union-find can hold: the one bound every id mint of the store applies.
        /// </summary>
        /// <param name="value">The value the counter handed out.</param>
        /// <returns>The id.</returns>
        /// <exception cref="OverflowException"><paramref name="value"/> exceeds <see cref="UnionFind.MaxId"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint CheckId(long value)
        {
            if ((ulong)value > UnionFind.MaxId)
                ThrowIdsExhausted(value);

            return (uint)value;
        }

        /// <summary>
        /// Throws the <see cref="OverflowException"/> of a store that has run out of ids.
        /// </summary>
        /// <param name="value">The first id past the limit that a mint asked for.</param>
        /// <exception cref="OverflowException">Always.</exception>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowIdsExhausted(long value)
        {
            throw new OverflowException($"The store has run out of ids: {value} exceeds the largest id a union-find can hold, {UnionFind.MaxId}.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowClockExhausted(long value)
        {
            throw new OverflowException($"The store's clock has run out: {value} is not an epoch below the stale-row marker {Value.StaleRep}.");
        }

    }

}
