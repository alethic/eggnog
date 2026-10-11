using System;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Increments <paramref name="counter"/> on the store's shared counters, never redirected to a child context's id
        /// blocks (colored-contexts spec §4.2): for ids that must not be local to any context. Interned container ids
        /// are minted here, so a container interned in a child is the same id everywhere (L3, C13).
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long IncrementSharedCounter(CounterId counter) => RequireView().IncrementSharedCounter(counter);

    }

}
