using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Gets the context's view of <paramref name="table"/> (§6.3, §6.5): the root's single-layer view at the root,
        /// otherwise the child's layered view, built and cached on first use. A view is immutable; rows appended to the
        /// context's own layer later show through it, and it is dropped when its structure changes.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean (§7.5). A context that is not frozen is entered (its domain); a frozen one is
        /// read from any thread.
        /// </remarks>
        /// <param name="table">A registered function table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, contradicted or not clean, or its domain is
        /// held by another thread.</exception>
        /// <exception cref="NotSupportedException">The table is the union-find, which is read through
        /// <see cref="Find"/> and lookups (X23).</exception>
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
        /// <paramref name="destination"/> (§6.5): through the context's view, or, for the union-find, its view-level lookup
        /// (a row for every id displaced in the context's view). Keys are taken as given: the rows a context sees are
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
        /// Gets whether the context is contradictory (§6.9): a contradiction stopped it, or a row of the store's
        /// contradiction table is visible in it. The one read a stopped context still answers.
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
