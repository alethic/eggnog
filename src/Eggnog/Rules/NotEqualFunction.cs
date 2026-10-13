using System;

namespace Eggnog
{

    /// <summary>
    /// The not-equal filter, one per graph: <c>[a, b] → unit</c> when the cells differ, nothing otherwise. Comparing cells
    /// is sound because the engine passes canonical classes and equal values of one column type have equal cells. No user
    /// code runs; immutable and thread-safe.
    /// </summary>
    internal sealed class NotEqualFunction : Relations.ExternalFunction
    {

        readonly uint unit;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unit">The unit value's cell.</param>
        public NotEqualFunction(uint unit)
        {
            this.unit = unit;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            if (args[0] != args[1])
            {
                result = unit;
                return true;
            }

            result = 0;
            return false;
        }

    }

}
