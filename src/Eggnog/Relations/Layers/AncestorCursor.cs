using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// How far a child context has read one ancestor: the version of every layer of the ancestor when the child last
    /// caught up with it, and the ancestor's change stamp then. A child keeps one per ancestor that is not settled (not
    /// a frozen ancestor it has read in full); the union-find has its own cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RowsOrDefault"/> is <c>(0, 0)</c> for a table the ancestor had no layer of, or that did not exist,
    /// when the cursor was taken: every row of such a layer counts as new. The ancestor's rows appended since are
    /// <c>[RowsOrDefault(t).Minor, layer.Version.Minor)</c> while the generation is unchanged.
    /// </para>
    /// <para>
    /// A catch-up records what it read as pending (<see cref="SetPendingRows"/>, <see cref="SetPendingStamp"/>) and
    /// <see cref="CommitPending"/> publishes it after the rebuild loop, while the ancestor cannot change (it is frozen, or
    /// shares the child's thread domain). Owner's domain thread only.
    /// </para>
    /// </remarks>
    internal sealed class AncestorCursor
    {

        TableVersion[] rows;
        long stamp;
        TableVersion[]? pendingRows;
        long pendingStamp;
        bool hasPendingStamp;

        /// <summary>
        /// Initializes a cursor at <paramref name="rows"/> and <paramref name="stamp"/>.
        /// </summary>
        /// <param name="rows">The ancestor's layer versions by table index; owned by the cursor.</param>
        /// <param name="stamp">The ancestor's change stamp.</param>
        public AncestorCursor(TableVersion[] rows, long stamp)
        {
            ArgumentNullException.ThrowIfNull(rows);
            this.rows = rows;
            this.stamp = stamp;
        }

        /// <summary>
        /// Gets the ancestor's change stamp when the child last caught up with it.
        /// </summary>
        public long Stamp => stamp;

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
        /// Publishes what <see cref="SetPendingRows"/> and <see cref="SetPendingStamp"/> recorded.
        /// </summary>
        /// <returns>Whether the cursor moved.</returns>
        public bool CommitPending()
        {
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
