using System;
using System.Collections.Generic;

namespace Eggnog.Storage
{

    /// <summary>
    /// One view of a <see cref="Store"/>: its parent's view, plus the rows and merges asserted in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a context sees.</b> Every row asserted in it or in any ancestor, with every identifier replaced by
    /// its canonical identifier in this context, closed under congruence: two rows of a function whose keys
    /// became equal have their outputs combined. Merges made in a context are visible to it and its descendants
    /// only. That is the whole difference between a context and a copy.
    /// </para>
    /// <para>
    /// <b>How it is stored.</b> A context keeps only what differs from its parent. Its union-find maps its
    /// parent's canonical identifiers to its own. A row of an ancestor is read through unchanged unless this
    /// context changed its meaning — it mentions a class this context merged, or this context holds a row with
    /// the same key — in which case this context holds a rewritten copy and the ancestor's row is hidden from it.
    /// Rows are copied only when a merge touches them, so a context that assumes one fact costs roughly what that
    /// fact touches.
    /// </para>
    /// <para>
    /// <b>Rebuilding.</b> Writes leave work pending: rows that mention a class that has just been merged, and
    /// merges implied by rows that came to share a key. <see cref="Rebuild"/> does that work, and also takes in
    /// whatever ancestors have changed since; reads require it to have been done and throw otherwise. Ancestors
    /// may keep changing after a child is made — the child catches up when it is next rebuilt.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> A context is not thread-safe. Once <see cref="Freeze">frozen</see> it is read-only, and
    /// any number of children of a frozen context may be used concurrently, one thread per child.
    /// </para>
    /// </remarks>
    public sealed partial class Context : IDisposable
    {

        readonly Store store;
        readonly Context? parent;
        readonly Context[] ancestors;
        readonly long[] cursors;
        readonly List<Context> children = new();
        readonly List<LogEntry> log = new();
        readonly List<(Context Reader, int Level)> readers = new();
        readonly Queue<ulong> losers = new();
        readonly Queue<(ulong, ulong)> unions = new();

        RowSet?[] rows;
        uint[] forest = Array.Empty<uint>();
        readonly Dictionary<ulong, ulong>? local;
        HashSet<ulong>? marked;
        long logBase;
        bool frozen;
        bool disposed;

        internal Context(Store store, Context? parent)
        {
            this.store = store;
            this.parent = parent;
            rows = new RowSet?[store.Tables.Count];

            if (parent is null)
            {
                ancestors = Array.Empty<Context>();
                cursors = Array.Empty<long>();
            }
            else
            {
                ancestors = new Context[parent.ancestors.Length + 1];
                Array.Copy(parent.ancestors, ancestors, parent.ancestors.Length);
                ancestors[parent.ancestors.Length] = parent;

                cursors = new long[ancestors.Length];
                for (var i = 0; i < ancestors.Length; i++)
                    cursors[i] = ancestors[i].LogEnd;

                local = new Dictionary<ulong, ulong>();
                marked = new HashSet<ulong>();
            }
        }

        /// <summary>
        /// Gets the store.
        /// </summary>
        public Store Store => store;

        /// <summary>
        /// Gets the parent context, or <c>null</c> for the root.
        /// </summary>
        public Context? Parent => parent;

        /// <summary>
        /// Gets whether the context is frozen, and so read-only.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets whether the context has been disposed.
        /// </summary>
        public bool IsDisposed => disposed;

        /// <summary>
        /// Gets whether reads are allowed: nothing is pending here and nothing an ancestor did is unseen.
        /// </summary>
        public bool IsClean
        {
            get
            {
                if (disposed || losers.Count > 0 || unions.Count > 0)
                    return false;

                for (var i = 0; i < ancestors.Length; i++)
                    if (ancestors[i].losers.Count > 0 || ancestors[i].unions.Count > 0 || cursors[i] != ancestors[i].LogEnd)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Gets a snapshot of the live children.
        /// </summary>
        public IReadOnlyList<Context> Children
        {
            get
            {
                lock (children)
                    return children.ToArray();
            }
        }

        /// <summary>
        /// Makes a child: this context, plus whatever is asserted in the child.
        /// </summary>
        /// <returns>The child.</returns>
        public Context CreateChild()
        {
            ThrowIfDisposed();

            if (frozen == false)
                Rebuild();

            Context child;
            lock (children)
            {
                child = new Context(store, this);
                children.Add(child);
            }

            // Every ancestor's log is read by every descendant, so each keeps track of who has read how far.
            for (var level = 0; level < child.ancestors.Length; level++)
                lock (child.ancestors[level].readers)
                    child.ancestors[level].readers.Add((child, level));

            return child;
        }

        /// <summary>
        /// Makes a new class, with nothing known about it.
        /// </summary>
        /// <returns>The class's identifier.</returns>
        public Id NewId()
        {
            ThrowIfNotWritable();
            return new Id((uint)store.AllocateId());
        }

        /// <summary>
        /// Gets the canonical identifier of a class in this context.
        /// </summary>
        /// <param name="id">Any identifier of the class.</param>
        /// <returns>The canonical identifier.</returns>
        public Id Find(Id id)
        {
            ThrowIfNotReadable();
            return new Id((uint)FindRaw(id.Value));
        }

        /// <summary>
        /// Gets whether two identifiers name the same class in this context.
        /// </summary>
        /// <param name="a">One identifier.</param>
        /// <param name="b">The other.</param>
        /// <returns><c>true</c> where they name one class.</returns>
        public bool AreEqual(Id a, Id b)
        {
            ThrowIfNotReadable();
            return FindRaw(a.Value) == FindRaw(b.Value);
        }

        /// <summary>
        /// Merges two classes in this context and its descendants. Rows that mention either are brought up to date
        /// by the next <see cref="Rebuild"/>.
        /// </summary>
        /// <param name="a">One identifier.</param>
        /// <param name="b">The other.</param>
        /// <returns><c>true</c> where the classes were distinct.</returns>
        public bool Union(Id a, Id b)
        {
            ThrowIfNotWritable();
            Prepare();
            return UnionCore(a.Value, b.Value);
        }

        /// <summary>
        /// Adds a row to a function. Where the key already has an output, the two are combined: two classes are
        /// merged, two values are passed to the table's merge function.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="key">The key columns.</param>
        /// <param name="output">The output.</param>
        public void Insert(Table table, ReadOnlySpan<ulong> key, ulong output)
        {
            CheckTable(table, key, output: true);
            ThrowIfNotWritable();
            Prepare();

            var row = new ulong[table.Arity];
            key.CopyTo(row);
            row[table.KeyCount] = output;
            Canonicalize(table, row);
            AddOrMerge(table, row, materialize: false);
        }

        /// <summary>
        /// Adds a row to a relation.
        /// </summary>
        /// <param name="table">The relation.</param>
        /// <param name="key">The row.</param>
        public void Insert(Table table, ReadOnlySpan<ulong> key)
        {
            CheckTable(table, key, output: false);
            ThrowIfNotWritable();
            Prepare();

            var row = key.ToArray();
            Canonicalize(table, row);
            AddOrMerge(table, row, materialize: false);
        }

        /// <summary>
        /// Gets the class a constructor maps a key to, making a new class for it if there is none.
        /// </summary>
        /// <param name="table">A function whose output is an identifier.</param>
        /// <param name="key">The key columns.</param>
        /// <returns>The class.</returns>
        public Id GetOrAdd(Table table, ReadOnlySpan<ulong> key) => GetOrAdd(table, key, out _);

        /// <summary>
        /// Gets the class a constructor maps a key to, making a new class for it if there is none.
        /// </summary>
        /// <remarks>
        /// Before a <see cref="Rebuild"/> the lookup may miss a row whose key is not yet canonical, and make a new
        /// class; the rebuild then finds the two rows share a key and merges the classes, so the answer is late
        /// rather than wrong.
        /// </remarks>
        /// <param name="table">A function whose output is an identifier.</param>
        /// <param name="key">The key columns.</param>
        /// <param name="created">Whether a new class was made.</param>
        /// <returns>The class.</returns>
        public Id GetOrAdd(Table table, ReadOnlySpan<ulong> key, out bool created)
        {
            CheckTable(table, key, output: true);
            if (table.OutputIsId == false)
                throw new ArgumentException($"Table '{table.Name}' does not map to a class.", nameof(table));

            ThrowIfNotWritable();
            Prepare();

            var row = new ulong[table.Arity];
            key.CopyTo(row);
            Canonicalize(table, row);

            if (LookupView(table, row, out var output))
            {
                created = false;
                return new Id((uint)FindRaw(output));
            }

            var id = store.AllocateId();
            row[table.KeyCount] = id;
            AddOrMerge(table, row, materialize: false);

            created = true;
            return new Id((uint)id);
        }

        /// <summary>
        /// Looks up a function's output for a key.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="key">The key columns.</param>
        /// <param name="output">The output, canonical if it is an identifier.</param>
        /// <returns><c>true</c> where the key has a row.</returns>
        public bool TryLookup(Table table, ReadOnlySpan<ulong> key, out ulong output)
        {
            CheckTable(table, key, output: true);
            ThrowIfNotReadable();

            var probe = key.ToArray();
            Canonicalize(table, probe);

            if (LookupView(table, probe, out output) == false)
                return false;

            if (table.OutputIsId)
                output = FindRaw(output);

            return true;
        }

        /// <summary>
        /// Gets whether a key has a row.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key columns.</param>
        /// <returns><c>true</c> where it has.</returns>
        public bool Contains(Table table, ReadOnlySpan<ulong> key)
        {
            CheckTable(table, key, output: false, keyOnly: true);
            ThrowIfNotReadable();

            var probe = key.ToArray();
            Canonicalize(table, probe);
            return LookupView(table, probe, out _);
        }

        /// <summary>
        /// Visits every row of a table this context sees, or only those added or changed after a clock reading.
        /// </summary>
        /// <remarks>
        /// With <paramref name="since"/> the scan may also return rows whose content is old but which this context
        /// had to rewrite, so it over-approximates what changed; it never misses a change.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="action">Receives each row.</param>
        /// <param name="since">A <see cref="Store.Clock"/> reading; rows stamped after it are visited.</param>
        public void Scan(Table table, RowAction action, long since = 0)
        {
            CheckTable(table);
            if (action is null)
                throw new ArgumentNullException(nameof(action));

            ThrowIfNotReadable();
            ScanLevel(table, since, action, new List<Context>());
        }

        /// <summary>
        /// Visits the rows of a table whose <paramref name="column"/> holds <paramref name="value"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column, counting keys then output.</param>
        /// <param name="value">The value; an identifier need not be canonical.</param>
        /// <param name="action">Receives each row.</param>
        /// <param name="since">A <see cref="Store.Clock"/> reading; rows stamped after it are visited.</param>
        public void ScanColumn(Table table, int column, ulong value, RowAction action, long since = 0)
        {
            CheckTable(table);
            if ((uint)column >= (uint)table.Arity)
                throw new ArgumentOutOfRangeException(nameof(column));
            if (action is null)
                throw new ArgumentNullException(nameof(action));

            ThrowIfNotReadable();

            if (table.IsId[column])
                value = FindRaw(value);

            ScanColumnLevel(table, column, value, since, action, new List<Context>());
        }

        /// <summary>
        /// Does the pending work: takes in what ancestors changed, rewrites rows that mention merged classes, and
        /// merges what congruence requires, until nothing is left.
        /// </summary>
        public void Rebuild()
        {
            ThrowIfDisposed();
            if (frozen)
                return;

            Prepare();
            ProcessPending();
        }

        /// <summary>
        /// Rebuilds the context and makes it read-only, after which its children may be used concurrently.
        /// </summary>
        /// <exception cref="InvalidOperationException">The parent is not frozen.</exception>
        public void Freeze()
        {
            ThrowIfDisposed();
            if (frozen)
                return;

            if (parent is not null && parent.frozen == false)
                throw new InvalidOperationException("A context can be frozen only once its parent is.");

            Rebuild();
            CompressAll();

            foreach (var set in rows)
                set?.BuildAllColumns();

            frozen = true;
        }

        /// <summary>
        /// Drops the context. It must have no live children. The root cannot be dropped.
        /// </summary>
        public void Dispose()
        {
            if (disposed)
                return;

            if (parent is null)
                throw new InvalidOperationException("The root context cannot be disposed.");

            lock (children)
                if (children.Count > 0)
                    throw new InvalidOperationException("A context with live children cannot be disposed.");

            parent.RemoveChild(this);
            foreach (var ancestor in ancestors)
                lock (ancestor.readers)
                    ancestor.readers.RemoveAll(r => r.Reader == this);

            disposed = true;
            rows = Array.Empty<RowSet?>();
        }

        void RemoveChild(Context child)
        {
            lock (children)
                children.Remove(child);
        }

        void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(Context));
        }

        void ThrowIfNotWritable()
        {
            ThrowIfDisposed();
            if (frozen)
                throw new InvalidOperationException("The context is frozen.");
        }

        void ThrowIfNotReadable()
        {
            ThrowIfDisposed();
            if (IsClean == false)
                throw new InvalidOperationException("The context has pending changes, or an ancestor has changed since it was rebuilt; call Rebuild() first.");
        }

        void CheckTable(Table table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));
            if (table.Store != store)
                throw new ArgumentException("The table belongs to another store.", nameof(table));
        }

        void CheckTable(Table table, ReadOnlySpan<ulong> key, bool output, bool keyOnly = false)
        {
            CheckTable(table);

            if (keyOnly == false && table.HasOutput != output)
                throw new ArgumentException(output ? $"Table '{table.Name}' is a relation." : $"Table '{table.Name}' is a function; give it an output.", nameof(table));

            if (key.Length != table.KeyCount)
                throw new ArgumentException($"Table '{table.Name}' has {table.KeyCount} key columns, not {key.Length}.", nameof(key));
        }

        /// <summary>
        /// An entry in the log a context keeps while it has children: a row it added or changed, or a class it
        /// merged away. A child reads it to learn what changed underneath it.
        /// </summary>
        readonly record struct LogEntry(bool IsUnion, int Table, int Slot, ulong Loser);

    }

}
