using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Storage
{

    /// <summary>
    /// An e-graph database: a set of tables, and a tree of contexts that each hold a view of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Root"/> context holds what is true unconditionally. A child context, made with
    /// <see cref="Context.CreateChild"/>, is its parent plus whatever is asserted in it: rows, and merges of
    /// classes that hold only under the child's assumptions. A child stores only what differs from its parent,
    /// and reads through to the parent for everything else, so a context is cheap to make and cheap to drop.
    /// </para>
    /// <para>
    /// Identifiers and the clock are shared by every context, so an identifier is unique across the store and a
    /// clock reading orders changes made anywhere in it.
    /// </para>
    /// </remarks>
    public sealed class Store
    {

        readonly object sync = new();
        volatile Table[] tables = Array.Empty<Table>();
        long clock;
        long nextId;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public Store()
        {
            Root = new Context(this, null);
        }

        /// <summary>
        /// Gets the root context, which holds what is true unconditionally.
        /// </summary>
        public Context Root { get; }

        /// <summary>
        /// Gets the tables defined so far.
        /// </summary>
        public IReadOnlyList<Table> Tables => tables;

        /// <summary>
        /// Gets the current clock reading. Every row added or changed afterwards carries a later stamp, so a scan
        /// given this reading as its <c>since</c> sees exactly what changed after it was taken.
        /// </summary>
        public long Clock => Interlocked.Read(ref clock);

        /// <summary>
        /// Defines a table.
        /// </summary>
        /// <param name="schema">The table's schema.</param>
        /// <returns>The table.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <c>null</c>.</exception>
        public Table DefineTable(TableSchema schema)
        {
            if (schema is null)
                throw new ArgumentNullException(nameof(schema));

            lock (sync)
            {
                var table = new Table(this, tables.Length, schema);
                var next = new Table[tables.Length + 1];
                Array.Copy(tables, next, tables.Length);
                next[tables.Length] = table;
                tables = next;
                return table;
            }
        }

        internal long Tick() => Interlocked.Increment(ref clock);

        internal ulong AllocateId()
        {
            var value = Interlocked.Increment(ref nextId) - 1;
            if (value > uint.MaxValue)
                throw new InvalidOperationException("The store has run out of identifiers.");

            return (ulong)value;
        }

    }

}
