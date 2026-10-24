using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// How far a child context has read one ancestor: the version of every layer of the ancestor when the child last
    /// caught up with it, the ancestor's change stamp then, and its positions in the two streams of the ancestor's
    /// container log (<see cref="ContainerLog"/>). A child keeps one per ancestor that is not settled (not a frozen
    /// ancestor it has read in full); the union-find has its own cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RowsOrDefault"/> is <c>(0, 0)</c> for a table the ancestor had no layer of, or that did not exist,
    /// when the cursor was taken: every row of such a layer counts as new. The ancestor's rows appended since are
    /// <c>[RowsOrDefault(t).Minor, layer.Version.Minor)</c> while the generation is unchanged.
    /// </para>
    /// <para>
    /// A catch-up records what it read as pending (<see cref="SetPendingRows"/>, <see cref="SetPendingStamp"/>,
    /// <see cref="SetPendingDirty"/>) and <see cref="CommitPending"/> publishes it after the rebuild loop, while the
    /// ancestor cannot change (it is frozen, or shares the child's thread domain). The Added position moves where the
    /// eq-container guard reads the Added stream (<see cref="Added"/>), which is consumed once read, before the rebuild
    /// loop. Owner's domain thread only.
    /// </para>
    /// </remarks>
    internal sealed class AncestorCursor
    {

        TableVersion[] rows;
        long stamp;
        int dirty;
        TableVersion[]? pendingRows;
        long pendingStamp;
        bool hasPendingStamp;
        int pendingDirty = -1;

        /// <summary>
        /// Initializes a cursor at <paramref name="rows"/> and <paramref name="stamp"/>, and at
        /// <paramref name="added"/> and <paramref name="dirty"/> in the ancestor's container log.
        /// </summary>
        /// <param name="rows">The ancestor's layer versions by table index; owned by the cursor.</param>
        /// <param name="stamp">The ancestor's change stamp.</param>
        /// <param name="added">The ancestor's container log's Added count, after a drain.</param>
        /// <param name="dirty">Its Dirty count.</param>
        public AncestorCursor(TableVersion[] rows, long stamp, int added, int dirty)
        {
            ArgumentNullException.ThrowIfNull(rows);
            this.rows = rows;
            this.stamp = stamp;
            Added = added;
            this.dirty = dirty;
        }

        /// <summary>
        /// Gets the ancestor's change stamp when the child last caught up with it.
        /// </summary>
        public long Stamp => stamp;

        /// <summary>
        /// Gets or sets the number of Added entries of the ancestor's container log the child has consumed: the
        /// eq-container guard moves it past what it read (<see cref="ContextTables.ConsumeAdded"/>).
        /// </summary>
        public int Added { get; set; }

        /// <summary>
        /// Gets the number of Dirty ids of the ancestor's container log the child has refreshed its rows for.
        /// </summary>
        public int Dirty => dirty;

        /// <summary>
        /// Gets the number of tables the cursor records.
        /// </summary>
        public int TableCount => rows.Length;

        /// <summary>
        /// Gets the version of the ancestor's layer of <paramref name="table"/> when the child last caught up, or
        /// <c>(0, 0)</c> when the cursor has not seen it.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The version.</returns>
        public TableVersion RowsOrDefault(TableId table) => (uint)table.Index < (uint)rows.Length ? rows[table.Index] : default;

        /// <summary>
        /// Records the version of the ancestor's layer of <paramref name="table"/> that a catch-up read up to.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="version">The version read.</param>
        public void SetPendingRows(TableId table, TableVersion version)
        {
            var pending = pendingRows ??= (TableVersion[])rows.Clone();
            if (table.Index >= pending.Length)
            {
                Array.Resize(ref pending, table.Index + 1);
                pendingRows = pending;
            }

            pending[table.Index] = version;
        }

        /// <summary>
        /// Records the ancestor's change stamp that a catch-up read up to.
        /// </summary>
        /// <param name="value">The stamp.</param>
        public void SetPendingStamp(long value)
        {
            pendingStamp = value;
            hasPendingStamp = true;
        }

        /// <summary>
        /// Records the Dirty count of the ancestor's container log that a catch-up read up to.
        /// </summary>
        /// <param name="value">The count.</param>
        public void SetPendingDirty(int value) => pendingDirty = value;

        /// <summary>
        /// Publishes what <see cref="SetPendingRows"/>, <see cref="SetPendingStamp"/> and <see cref="SetPendingDirty"/>
        /// recorded.
        /// </summary>
        /// <returns>Whether the rows or the stamp moved: a Dirty position changes no view.</returns>
        public bool CommitPending()
        {
            if (pendingDirty >= 0)
            {
                dirty = pendingDirty;
                pendingDirty = -1;
            }

            var moved = false;
            if (pendingRows is { } pending)
            {
                moved = pending.Length != rows.Length || pending.AsSpan().SequenceEqual(rows) == false;
                rows = pending;
                pendingRows = null;
            }

            if (hasPendingStamp)
            {
                moved |= pendingStamp != stamp;
                stamp = pendingStamp;
                hasPendingStamp = false;
            }

            return moved;
        }

    }

}
