using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The cached indexes a freezing context builds before it is shared (colored-contexts spec §8.1 step 4.3, X13), so
    /// that its concurrent descendants rarely build one lazily under a frozen catalog's serial refresh (X14).
    /// </summary>
    /// <remarks>
    /// Built from explicit keys here; the bridge builds it from every cached plan's index keys
    /// (<c>FreezeWarmup.From</c> over <c>PlanIndexKeys.Collect</c>, §9.10, with the layered planner of M10.2). Keys for
    /// tables the freezing database has no layer of are skipped. Immutable.
    /// </remarks>
    internal sealed class FreezeWarmup
    {

        readonly CachedIndexKey[] keys;

        /// <summary>
        /// Initializes a warm-up of <paramref name="keys"/>.
        /// </summary>
        /// <param name="keys">The indexes to build.</param>
        public FreezeWarmup(IEnumerable<CachedIndexKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            this.keys = [.. keys];
        }

        /// <summary>
        /// Gets the indexes to build, in order.
        /// </summary>
        public ReadOnlySpan<CachedIndexKey> Keys => keys;

    }

}
