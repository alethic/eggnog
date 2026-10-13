using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The cached indexes a freezing context builds before it is shared, after freezing its tables and before switching
    /// its index catalogs to serial refresh (<see cref="TableInfo.FreezeIndexes"/>), so that its concurrent descendants
    /// rarely build one lazily under that serial refresh.
    /// </summary>
    /// <remarks>
    /// Built from explicit keys, or from plans (<see cref="From"/>: every index key a plan may read, on cacheable
    /// columns only; the bridge passes every cached plan at its freeze). Keys for tables the freezing database has no
    /// layer of are skipped: an ancestor's layers were warmed by its own freeze. Immutable.
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
        /// Creates the warm-up of every cached index <paramref name="plans"/> may read
        /// (<see cref="PlanIndexKeys.Collect"/>), each key once.
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
