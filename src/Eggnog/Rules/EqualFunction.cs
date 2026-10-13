using System;

namespace Eggnog
{

    /// <summary>
    /// The equal filter, one per graph: <c>[a, b] → unit</c> when the cells are equal, nothing otherwise. Its operands are
    /// class constants whose comparison a definition left to run time (§6.3 step 3), which the engine substitutes with
    /// their canonical forms in the running context (X19), so equal cells mean one class there. No user code runs;
    /// immutable and thread-safe.
    /// </summary>
    internal sealed class EqualFunction : Relations.ExternalFunction
    {

        readonly uint unit;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unit">The unit value's cell.</param>
        public EqualFunction(uint unit)
        {
            this.unit = unit;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            if (args[0] == args[1])
            {
                result = unit;
                return true;
            }

            result = 0;
            return false;
        }

    }

}
