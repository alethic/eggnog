using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Wraps a lookup so that a missing key yields a default instead of failing the lane: the closure of egglog's
    /// <c>lookup_with_default_vectorized</c> (core-relations/src/table_spec.rs:509-544), <c>get_row_column(..).unwrap_or(default)</c>.
    /// Never fails.
    /// </summary>
    /// <typeparam name="TLookup">The wrapped lookup; a struct, so the call is inlined.</typeparam>
    internal struct DefaultingLookup<TLookup> : ILaneProducer
        where TLookup : struct, ILaneProducer
    {

        TLookup lookup;
        readonly ArgSource fallback;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="lookup">The lookup.</param>
        /// <param name="fallback">The default, read in the same lane.</param>
        public DefaultingLookup(TLookup lookup, ArgSource fallback)
        {
            this.lookup = lookup;
            this.fallback = fallback;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value)
        {
            if (lookup.TryProduce(lane, out value) == false)
                value = fallback.Get(lane);

            return true;
        }

    }

}
