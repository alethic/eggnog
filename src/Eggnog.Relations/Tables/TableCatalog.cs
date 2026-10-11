using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
    /// Reads (<see cref="this[TableId]"/>, <see cref="TryGetTable"/>) are safe from many threads while nobody adds or
    /// replaces a table.
    /// </para>
    /// </remarks>
    internal sealed class TableCatalog
    {

        readonly DenseIdMap<TableId, Table> tables = new();

        /// <summary>
        /// Gets the tables notified of staged writes since the database last reset the list.
        /// </summary>
        public NotificationList<TableId> Notifications { get; } = new();

        /// <summary>
        /// Gets the number of table slots: one more than the largest table id.
        /// </summary>
        public int Count => tables.IdCount;

        /// <summary>
        /// Gets the table with id <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public Table this[TableId id] => tables[id];

        /// <summary>
        /// Adds a table and returns its id, the next unused one.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>Its id.</returns>
        public TableId Add(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return tables.Push(table);
        }

        /// <summary>
        /// Puts <paramref name="table"/> at <paramref name="id"/>, replacing any table there.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="table">The table.</param>
        public void Set(TableId id, Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            tables.Insert(id, table);
        }

        /// <summary>
        /// Gets the table with id <paramref name="id"/>, if there is one.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if present.</returns>
        public bool TryGetTable(TableId id, [MaybeNullWhen(false)] out Table table) => tables.TryGetValue(id, out table);

    }

}
