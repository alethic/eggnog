using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The cached indexes a freezing context builds before it is shared (colored-contexts spec §8.1 step 4.3, X13), so
    /// that its concurrent descendants rarely build one lazily under a frozen catalog's serial refresh (X14).
    /// </summary>
    /// <remarks>
    /// Built from explicit keys, or from plans (<see cref="From"/>, §9.10: every cached plan's index keys, which the
    /// bridge passes at its freeze, §10.8). Keys for tables the freezing database has no layer of are skipped: an
    /// ancestor's layers were warmed by its own freeze. Immutable.
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

        /// <summary>
        /// Creates the warm-up of every cached index <paramref name="plans"/> may read (§9.10,
        /// <see cref="PlanIndexKeys.Collect"/>), each key once.
        /// </summary>
        /// <param name="plans">The plans, typically every cached plan of the rules the frozen context's descendants run.</param>
        /// <param name="schema">The schema that says which columns are cacheable: the freezing context's database.</param>
        /// <returns>The warm-up.</returns>
        public static FreezeWarmup From(IEnumerable<PlanTemplate> plans, IQuerySchema schema)
        {
            ArgumentNullException.ThrowIfNull(plans);
            ArgumentNullException.ThrowIfNull(schema);
            var keys = new List<CachedIndexKey>();
            foreach (var plan in plans)
                PlanIndexKeys.Collect(plan, schema, keys);

            return new FreezeWarmup(keys);
        }

    }

}
