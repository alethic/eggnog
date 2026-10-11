using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The dependencies of the tables' merge functions on one another, and the strata they induce: egglog's
    /// <c>DependencyGraph</c> (core-relations/src/dependency_graph.rs). <see cref="Database.MergeAll"/> merges the
    /// tables of one stratum together (in parallel when the database is large) and the strata in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table has two kinds of dependencies. Its <em>read</em> dependencies are the tables its merge function reads
    /// (a <c>Function</c> merge looks another table up); they must be fully merged before it merges, so a table's
    /// stratum is one more than the highest stratum of its read dependencies, or zero without any
    /// (dependency_graph.rs:32-53). Its <em>write</em> dependencies are the tables its merge function only stages
    /// writes into (a union-id merge writes the union-find); they impose no order, so two tables that only write each
    /// other share a stratum and may merge in parallel.
    /// </para>
    /// <para>
    /// Strata are contiguous from zero: a table lands one above an existing stratum at most. Within a stratum, tables
    /// keep the order they were added in, which is ascending table id (egglog's <c>IndexSet</c> insertion order).
    /// Read dependencies must already be registered; write dependencies may name any table, including ones added
    /// later. A table cannot read itself.
    /// </para>
    /// <para>
    /// Threading. <see cref="AddTable"/> needs exclusive access among writers. <see cref="LevelOf"/> and
    /// <see cref="Contains"/> read a level snapshot, an <c>int[]</c> by table index (−1 for an unregistered id), and
    /// are safe from any number of threads while a table is added (colored-contexts spec §4.3, X16): child contexts
    /// share the root's graph and merge concurrently while the store adds a table to a frozen root. A level, once
    /// published, never changes (a new table lands at or above existing strata), so the snapshot is extended in place
    /// with a volatile write while it has room and is copied, filled and republished whole when it grows. The other
    /// members (<see cref="StratumCount"/>, <see cref="Stratum"/>, <see cref="WriteDependencies"/>) read the
    /// writer-side structures and need the same exclusive access as <see cref="AddTable"/>.
    /// </para>
    /// <para>
    /// Every read is O(1) or a span over internal storage. <see cref="LevelOf"/> costs two volatile loads.
    /// </para>
    /// </remarks>
    internal sealed class DependencyGraph
    {

        readonly List<List<TableId>> levels;
        readonly DenseIdMap<TableId, int> toLevel;
        readonly DenseIdMap<TableId, TableId[]> writeDeps;

        /// <summary>
        /// The stratum of each table by index, −1 where no table is registered; read lock-free by
        /// <see cref="LevelOf"/>. Slots beyond the registered tables hold −1, so a slot is written once, from −1 to its
        /// level.
        /// </summary>
        int[] levelSnapshot;

        /// <summary>
        /// Initializes an empty graph.
        /// </summary>
        public DependencyGraph()
        {
            levels = new List<List<TableId>>();
            toLevel = new DenseIdMap<TableId, int>();
            writeDeps = new DenseIdMap<TableId, TableId[]>();
            levelSnapshot = Array.Empty<int>();
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        DependencyGraph(DependencyGraph source)
        {
            levels = new List<List<TableId>>(source.levels.Count);
            foreach (var level in source.levels)
                levels.Add(new List<TableId>(level));

            toLevel = source.toLevel.Clone();
            writeDeps = source.writeDeps.Clone();
            levelSnapshot = (int[])Volatile.Read(ref source.levelSnapshot).Clone();
        }

        /// <summary>
        /// Gets the number of strata. Requires exclusive access against <see cref="AddTable"/>.
        /// </summary>
        public int StratumCount => levels.Count;

        /// <summary>
        /// Registers <paramref name="table"/> with its dependencies. egglog's <c>add_table</c>. Validates everything
        /// before changing anything, and publishes the table's level last.
        /// </summary>
        /// <param name="table">The new table.</param>
        /// <param name="readDependencies">The tables its merge reads; all registered already. Repeats are ignored.</param>
        /// <param name="writeDependencies">The tables its merge only writes; any tables. Repeats are ignored.</param>
        /// <exception cref="InvalidOperationException">The table is already registered.</exception>
        /// <exception cref="ArgumentException">A read dependency is not registered, or is the table itself.</exception>
        public void AddTable(TableId table, ReadOnlySpan<TableId> readDependencies, ReadOnlySpan<TableId> writeDependencies)
        {
            if (toLevel.ContainsKey(table))
                throw new InvalidOperationException($"Table {table} is already added to the dependency graph.");

            // egglog: level = 1 + max(level of read deps), or 0 (dependency_graph.rs:44-51); its unwrap panics on an
            // unregistered read dependency, which here is a bad argument.
            var level = 0;
            foreach (var dependency in readDependencies)
            {
                if (dependency == table)
                    throw new ArgumentException($"Table {table} cannot read itself in its merge function.", nameof(readDependencies));

                if (toLevel.TryGetValue(dependency, out var dependencyLevel) == false)
                    throw new ArgumentException($"Read dependency {dependency} of table {table} is not registered.", nameof(readDependencies));

                level = Math.Max(level, dependencyLevel + 1);
            }

            writeDeps.Insert(table, Distinct(writeDependencies));
            toLevel.Insert(table, level);
            if (level == levels.Count)
                levels.Add(new List<TableId>());

            levels[level].Add(table);
            PublishLevel(table.Index, level);
        }

        /// <summary>
        /// Gets whether <paramref name="table"/> is registered. Thread-safe against <see cref="AddTable"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if registered.</returns>
        public bool Contains(TableId table) => TryGetLevel(table, out _);

        /// <summary>
        /// Gets the stratum of <paramref name="table"/>. O(1); thread-safe against <see cref="AddTable"/>.
        /// </summary>
        /// <param name="table">A registered table.</param>
        /// <returns>The stratum, from zero.</returns>
        /// <exception cref="KeyNotFoundException">The table is not registered.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int LevelOf(TableId table)
        {
            if (TryGetLevel(table, out var level) == false)
                ThrowUnregistered(table);

            return level;
        }

        /// <summary>
        /// Gets the tables of stratum <paramref name="level"/>, in the order they were added. egglog's <c>strata</c>
        /// yields these for each level in turn. Requires exclusive access against <see cref="AddTable"/>.
        /// </summary>
        /// <param name="level">The stratum, below <see cref="StratumCount"/>.</param>
        /// <returns>The tables; valid until the next <see cref="AddTable"/>.</returns>
        public ReadOnlySpan<TableId> Stratum(int level) => CollectionsMarshal.AsSpan(levels[level]);

        /// <summary>
        /// Gets the write dependencies of <paramref name="table"/>, without repeats, in declaration order. egglog's
        /// <c>write_deps</c>; empty for an unregistered table. Requires exclusive access against
        /// <see cref="AddTable"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The tables its merge writes.</returns>
        public ReadOnlySpan<TableId> WriteDependencies(TableId table) => writeDeps.TryGetValue(table, out var deps) ? deps : ReadOnlySpan<TableId>.Empty;

        /// <summary>
        /// Creates an independent copy.
        /// </summary>
        /// <returns>The copy.</returns>
        public DependencyGraph Clone() => new(this);

        /// <summary>
        /// Reads the level of <paramref name="table"/> from the snapshot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryGetLevel(TableId table, out int level)
        {
            var snapshot = Volatile.Read(ref levelSnapshot);
            var index = table.Index;
            if ((uint)index < (uint)snapshot.Length)
            {
                level = Volatile.Read(ref snapshot[index]);
                return level >= 0;
            }

            level = -1;
            return false;
        }

        /// <summary>
        /// Publishes the level of the table at <paramref name="index"/>: in place when the snapshot has room (the slot
        /// goes from −1 to the level once), otherwise in a grown copy published whole.
        /// </summary>
        void PublishLevel(int index, int level)
        {
            var snapshot = levelSnapshot;
            if (index < snapshot.Length)
            {
                Volatile.Write(ref snapshot[index], level);
                return;
            }

            var grown = new int[Math.Max(Math.Max(8, snapshot.Length * 2), index + 1)];
            Array.Copy(snapshot, grown, snapshot.Length);
            grown.AsSpan(snapshot.Length).Fill(-1);
            grown[index] = level;
            Volatile.Write(ref levelSnapshot, grown);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnregistered(TableId table)
        {
            throw new KeyNotFoundException($"No entry for {table}.");
        }

        /// <summary>
        /// Copies <paramref name="tables"/> without repeats, keeping first occurrences in order (egglog's
        /// <c>IndexSet::extend</c>).
        /// </summary>
        static TableId[] Distinct(ReadOnlySpan<TableId> tables)
        {
            if (tables.IsEmpty)
                return Array.Empty<TableId>();

            var result = new List<TableId>(tables.Length);
            foreach (var table in tables)
                if (result.Contains(table) == false)
                    result.Add(table);

            return result.ToArray();
        }

    }

}
