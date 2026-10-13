using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Gets the context's view of <paramref name="table"/>: the root's single-layer view at the root, otherwise the
        /// child's layered view, built and cached on first use. A view is immutable; rows appended to the context's own
        /// layer later show through it, and it is dropped when its structure changes.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean. A context that is not frozen is entered (its domain); a frozen one is read
        /// from any thread.
        /// </remarks>
        /// <param name="table">A registered function table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, contradicted or not clean, or its domain is
        /// held by another thread.</exception>
        /// <exception cref="NotSupportedException">The table is the union-find, which is read through
        /// <see cref="Find"/> and lookups.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The table is not registered.</exception>
        public TableView GetView(TableId table)
        {
            if (frozen)
            {
                ThrowIfDisposed();
                return ViewOf(table);
            }

            using var scope = Domain.Enter();
            ThrowIfNotReadable();
            return ViewOf(table);
        }

        /// <summary>
        /// Copies the row of <paramref name="table"/> this context sees with key <paramref name="key"/> into
        /// <paramref name="destination"/>: through the context's view, or, for the union-find, its view-level lookup (a
        /// row for every id displaced in the context's view). Keys are taken as given: the rows a context sees are
        /// canonical in it, so a stale key finds nothing.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key.</param>
        /// <param name="destination">Receives the row; at least as many cells as the table has columns.</param>
        /// <returns><c>true</c> if the context sees a row with the key.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, contradicted or not clean, or its domain is
        /// held by another thread.</exception>
        public bool TryGetRow(TableId table, ReadOnlySpan<uint> key, Span<uint> destination)
        {
            if (frozen)
            {
                ThrowIfDisposed();
                return TryGetRowCore(table, key, destination);
            }

            using var scope = Domain.Enter();
            ThrowIfNotReadable();
            return TryGetRowCore(table, key, destination);
        }

        /// <summary>
        /// Gets whether the context is contradictory: a contradiction stopped it, or a row of the store's contradiction
        /// table is visible in it. The one read a stopped context still answers.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is not stopped and is faulted or not clean, or its
        /// domain is held by another thread.</exception>
        public bool IsContradictory
        {
            get
            {
                ThrowIfDisposed();
                if (contradicted)
                    return true;

                if (store.Schema.Contradiction is not { } table)
                    return false;

                return GetView(table).FindRow(ReadOnlySpan<uint>.Empty, out _) >= 0;
            }
        }

        /// <summary>
        /// Visits every live row the child stored itself in <paramref name="table"/>: its own layer's rows, canonical
        /// in it. Nothing when it has no layer of the table. O(own rows).
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean. The sink runs as a callback of the context's thread domain, as a
        /// <see cref="ScanMerges{TVisitor}"/> visitor does: it may read any context, and writes, rebuilds with work to
        /// do and structural calls are refused there; disposing the context during the scan is refused.
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="table">A registered function table.</param>
        /// <param name="sink">Receives each row with its row id in the own layer.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root, which has no layer of its own over a
        /// parent; or it is faulted, contradicted or not clean, or its domain is held by another thread.</exception>
        public void ScanOwn<TSink>(TableId table, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var t = LiftTables(nameof(ScanOwn));
            var current = frozen ? null : Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            BeginScan();
            current?.EnterCallback();
            try
            {
                t.ScanOwn(table, ref sink);
            }
            finally
            {
                current?.ExitCallback();
                Interlocked.Decrement(ref activeScans);
            }
        }

        /// <summary>
        /// Visits every ancestor row of <paramref name="table"/> the child sees that names <paramref name="id"/> in an
        /// id column: the winner probe of the lift, through the ancestors' rebuild indexes, never a scan.
        /// </summary>
        /// <remarks>
        /// Returns at once for a table without rebuild columns (no id can be named), for a context-private table, and
        /// for an id local to the child (no ancestor row names one). <paramref name="id"/> must be canonical in the
        /// child. Requires the context to be clean; the sink runs as a callback, as in <see cref="ScanOwn{TSink}"/>.
        /// Cost: O(depth) index probes plus the uses of the id.
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="table">A registered function table.</param>
        /// <param name="id">An id canonical in the child.</param>
        /// <param name="sink">Receives each row with its row id in its layer; layers come from the root up.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root; or it is faulted, contradicted or not
        /// clean, or its domain is held by another thread.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The table is not registered.</exception>
        public void ScanAncestorsMentioning<TSink>(TableId table, uint id, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var t = LiftTables(nameof(ScanAncestorsMentioning));
            var current = frozen ? null : Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            BeginScan();
            current?.EnterCallback();
            try
            {
                t.ScanAncestorsMentioning(table, id, ref sink);
            }
            finally
            {
                current?.ExitCallback();
                Interlocked.Decrement(ref activeScans);
            }
        }

        /// <summary>
        /// Gets the child's layering for a lift read, or throws on the root and on a disposed context.
        /// </summary>
        ContextTables LiftTables(string operation)
        {
            ThrowIfDisposed();
            return tables ?? throw new InvalidOperationException($"{operation} reads a child's layers over its ancestors; the root has none.");
        }

        /// <summary>
        /// Counts a scan of the context as running, so a dispose meanwhile is refused; throws if the context was disposed.
        /// Pair with a decrement of <see cref="activeScans"/>.
        /// </summary>
        void BeginScan()
        {
            if (Interlocked.Increment(ref activeScans) <= 0)
            {
                Interlocked.Decrement(ref activeScans);
                ThrowDisposed();
            }
        }

        /// <summary>
        /// Gets the view of a table without the readability checks.
        /// </summary>
        TableView ViewOf(TableId table) => tables is { } t ? t.GetView(table) : store.Schema.RootView(table);

        /// <summary>
        /// The body of <see cref="TryGetRow"/>.
        /// </summary>
        bool TryGetRowCore(TableId table, ReadOnlySpan<uint> key, Span<uint> destination)
        {
            if (table == store.UnionFindTable)
                return UnionFindTable.TryGetRow(key, destination, out _);

            return ViewOf(table).TryGetRow(key, destination, out _, out _);
        }

    }

}
