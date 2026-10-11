using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Mints a fresh id: a class with nothing known about it (colored-contexts spec §4.2, §5.7). A child mints from
        /// its id blocks, so the id is local to it (<see cref="IsLocalId"/>); the root mints directly from the store's id
        /// counter. Thread-safe.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or contradicted.</exception>
        /// <exception cref="OverflowException">The store has run out of ids: the next one would exceed
        /// <see cref="Relations.UnionFind.MaxId"/>.</exception>
        public uint NewId()
        {
            ThrowIfNotWritable();
            if (idBlocks is { } blocks)
                return blocks.Next();

            var raw = store.Counters.Increment(store.IdCounter);
            if ((ulong)raw > Relations.UnionFind.MaxId)
                throw new OverflowException($"The store has run out of ids: {raw} exceeds the largest id a union-find can hold, {Relations.UnionFind.MaxId}.");

            return (uint)raw;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> was minted in this context: it lies in one of the context's id blocks
        /// (§4.2). Exact for every id the context minted, since every term mint in a child goes through its blocks (I2);
        /// container ids are minted on the shared counter and are never local. Lock-free, O(log blocks).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is local to this context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root, which has no local ids.</exception>
        public bool IsLocalId(uint id)
        {
            ThrowIfDisposed();
            if (idBlocks is not { } blocks)
                throw new InvalidOperationException("The root context has no local ids: every id it mints is visible to all contexts.");

            return blocks.Contains(id);
        }

    }

}
