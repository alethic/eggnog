using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    /// Not thread-safe; the database changes it only under exclusive access and reads it while merging. Every read is
    /// O(1) or a span over internal storage.
    /// </para>
    /// </remarks>
    internal sealed class DependencyGraph
    {

        readonly List<List<TableId>> levels;
        readonly DenseIdMap<TableId, int> toLevel;
        readonly DenseIdMap<TableId, TableId[]> writeDeps;

        /// <summary>
        /// Initializes an empty graph.
        /// </summary>
        public DependencyGraph()
        {
            levels = new List<List<TableId>>();
            toLevel = new DenseIdMap<TableId, int>();
            writeDeps = new DenseIdMap<TableId, TableId[]>();
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
        }

        /// <summary>
        /// Gets the number of strata.
        /// </summary>
        public int StratumCount => levels.Count;

        /// <summary>
        /// Registers <paramref name="table"/> with its dependencies. egglog's <c>add_table</c>. Validates everything
        /// before changing anything.
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
        }

        /// <summary>
        /// Gets whether <paramref name="table"/> is registered.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if registered.</returns>
        public bool Contains(TableId table) => toLevel.ContainsKey(table);

        /// <summary>
        /// Gets the stratum of <paramref name="table"/>. O(1).
        /// </summary>
        /// <param name="table">A registered table.</param>
        /// <returns>The stratum, from zero.</returns>
        /// <exception cref="KeyNotFoundException">The table is not registered.</exception>
        public int LevelOf(TableId table) => toLevel[table];

        /// <summary>
        /// Gets the tables of stratum <paramref name="level"/>, in the order they were added. egglog's <c>strata</c>
        /// yields these for each level in turn.
        /// </summary>
        /// <param name="level">The stratum, below <see cref="StratumCount"/>.</param>
        /// <returns>The tables; valid until the next <see cref="AddTable"/>.</returns>
        public ReadOnlySpan<TableId> Stratum(int level) => CollectionsMarshal.AsSpan(levels[level]);

        /// <summary>
        /// Gets the write dependencies of <paramref name="table"/>, without repeats, in declaration order. egglog's
        /// <c>write_deps</c>; empty for an unregistered table.
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
