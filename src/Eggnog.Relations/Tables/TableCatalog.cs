using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The tables an <see cref="ExecutionState"/> can stage writes into, by <see cref="TableId"/>: the part of egglog's
    /// <c>DbView</c> (<c>table_info</c> plus <c>notification_list</c>, core-relations/src/free_join/mod.rs) that the
    /// table layer needs. The database milestone builds on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every staged write notifies <see cref="Notifications"/>, as egglog's <c>MutationBuffers::stage_insert</c> does
    /// (core-relations/src/action/mod.rs:401-409), so a merge loop can find the tables that have pending writes.
    /// </para>
    /// <para>
    /// Staging resolves tables through <see cref="GetForStaging"/>: the catalog's own table, or else what the
    /// <see cref="Resolver"/> supplies. A child context's catalog has a resolver that supplies the context's union-find
    /// and lazily creates the context's layer of a table on its first write, which it then publishes here
    /// (<see cref="Publish"/>; colored-contexts spec §6.2, X37); a root or bare catalog has none.
    /// </para>
    /// <para>
    /// Reads (<see cref="this[TableId]"/>, <see cref="TryGetTable"/>, <see cref="GetForStaging"/>) are safe from many
    /// threads while nobody adds or replaces a table, and read the table map with a volatile load, so a map
    /// <see cref="Publish"/> replaces whole is seen whole, also by a rule task staging while another task creates a
    /// layer. <see cref="Add"/> and <see cref="Set"/> change the map in place and need exclusive access.
    /// </para>
    /// </remarks>
    internal sealed class TableCatalog
    {

        /// <summary>
        /// The tables by id. Changed in place by <see cref="Add"/> and <see cref="Set"/> under exclusive access; read
        /// with a volatile load.
        /// </summary>
        DenseIdMap<TableId, Table> tables = new();

        /// <summary>
        /// Gets the tables notified of staged writes since the database last reset the list.
        /// </summary>
        public NotificationList<TableId> Notifications { get; } = new();

        /// <summary>
        /// Gets the function that supplies a table the catalog does not hold when a write is staged into it, or
        /// <c>null</c> (a root or bare catalog). Set by a child context's database (§6.2).
        /// </summary>
        public Func<TableId, Table>? Resolver { get; init; }

        /// <summary>
        /// Gets the number of table slots: one more than the largest table id.
        /// </summary>
        public int Count => Volatile.Read(ref tables).IdCount;

        /// <summary>
        /// Gets the table with id <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public Table this[TableId id] => Volatile.Read(ref tables)[id];

        /// <summary>
        /// Adds a table and returns its id, the next unused one. Requires exclusive access.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>Its id.</returns>
        public TableId Add(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return tables.Push(table);
        }

        /// <summary>
        /// Puts <paramref name="table"/> at <paramref name="id"/>, replacing any table there. Requires exclusive access.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="table">The table.</param>
        public void Set(TableId id, Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            tables.Insert(id, table);
        }

        /// <summary>
        /// Adds <paramref name="table"/> at <paramref name="id"/> copy-on-write (colored-contexts spec §6.2, X37): clones
        /// the map, inserts, and publishes the clone with a volatile write, so readers on other threads see the old map or
        /// the new one, never a torn one. The caller serializes publications (a child context's layer lock). O(tables).
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="table">The table.</param>
        internal void Publish(TableId id, Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            var next = Volatile.Read(ref tables).Clone();
            next.Insert(id, table);
            Volatile.Write(ref tables, next);
        }

        /// <summary>
        /// Gets the table with id <paramref name="id"/>, if there is one.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if present.</returns>
        public bool TryGetTable(TableId id, [MaybeNullWhen(false)] out Table table) => Volatile.Read(ref tables).TryGetValue(id, out table);

        /// <summary>
        /// Gets the table writes to <paramref name="id"/> are staged into: the catalog's own table, or else the one the
        /// <see cref="Resolver"/> supplies. One volatile load and one lookup when the catalog holds the table, which is
        /// what a root pays per (state, table) buffer creation.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="KeyNotFoundException">No such table and no resolver.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Table GetForStaging(TableId id)
        {
            if (Volatile.Read(ref tables).TryGetValue(id, out var table))
                return table;

            return Resolve(id);
        }

        /// <summary>
        /// The slow path of <see cref="GetForStaging"/>: asks the resolver, or throws.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        Table Resolve(TableId id)
        {
            if (Resolver is { } resolver)
                return resolver(id);

            throw new KeyNotFoundException($"No entry for {id}.");
        }

    }

}
