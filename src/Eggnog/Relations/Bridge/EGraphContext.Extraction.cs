namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// Starts an extraction query, or an extractor's creation, on this context: enters the thread domain of a
        /// context that is not frozen and marks it as running a caller's code (the cost model's functions), then checks
        /// that the context may be read: not disposed, faulted or stopped, and, with <paramref name="requireClean"/>,
        /// clean (<see cref="IsClean"/>). A frozen context is entered from any thread and holds nothing. Dispose the
        /// scope, also when the query throws.
        /// </summary>
        /// <remarks>
        /// An extractor is created on a clean context, whose visible rows are canonical, and queried later without
        /// requiring it clean again: its staleness check (<see cref="ExtractionWatch"/>) already refuses every change to
        /// what it reads, and a change elsewhere (an unrelated table written in an ancestor, host writes staged and not
        /// merged) leaves the rows it reads, and their canonical form, as they were. O(ancestors that are not frozen) with
        /// <paramref name="requireClean"/>, O(1) otherwise.
        /// </remarks>
        /// <param name="requireClean">Whether the context must be clean: at an extractor's creation.</param>
        /// <returns>The scope to dispose when the query ends.</returns>
        /// <exception cref="System.ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">The context is faulted or stopped, or not clean when that is
        /// required; or its domain is held by another thread.</exception>
        internal ExtractionQueryScope EnterExtraction(bool requireClean)
        {
            if (context.IsFrozen)
            {
                ThrowIfNotExtractable(requireClean);
                return default;
            }

            var guard = context.Domain;
            guard.Enter();
            try
            {
                ThrowIfNotExtractable(requireClean);
                guard.EnterCallback();
            }
            catch
            {
                guard.Exit();
                throw;
            }

            return new ExtractionQueryScope(guard);
        }

        /// <summary>
        /// Throws unless an extractor may read the context: <see cref="ThrowIfNotReadable"/> when it must be clean, its
        /// lifecycle checks otherwise.
        /// </summary>
        void ThrowIfNotExtractable(bool requireClean)
        {
            if (requireClean)
            {
                ThrowIfNotReadable();
                return;
            }

            context.ThrowIfDisposed();
            context.ThrowIfFaulted();
            context.ThrowIfContradicted();
        }

    }

}
