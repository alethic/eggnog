using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class EGraph
    {

        /// <summary>
        /// The number of context-local slots made so far (<see cref="NewLocalSlot{T}"/>).
        /// </summary>
        int localSlotCount;

        /// <summary>
        /// Gets the number of context-local slots made so far.
        /// </summary>
        internal int LocalSlotCount => Volatile.Read(ref localSlotCount);

        /// <summary>
        /// Makes a slot of per-context state: every context of the e-graph holds one value of type
        /// <typeparamref name="T"/> for it, built by <paramref name="factory"/> the first time the context is asked
        /// (<see cref="EGraphContext.GetLocal{T}"/>). Thread-safe.
        /// </summary>
        /// <typeparam name="T">The type of the per-context value.</typeparam>
        /// <param name="factory">Builds a context's value; may run on any thread that first asks, and more than once when
        /// threads race (one value is kept).</param>
        /// <returns>The slot.</returns>
        public ContextLocalSlot<T> NewLocalSlot<T>(Func<EGraphContext, T> factory)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(factory);
            return new ContextLocalSlot<T>(this, Interlocked.Increment(ref localSlotCount) - 1, factory);
        }

    }

}
