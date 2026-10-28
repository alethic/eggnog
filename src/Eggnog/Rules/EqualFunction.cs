using System;

namespace Eggnog
{

    /// <summary>
    /// The equal filter, one per graph: <c>[a, b] → unit</c> when the cells are equal, nothing otherwise. Its operands are
    /// constants a body equates (<see cref="ProgramStepKind.Equal"/>): class constants whose comparison a definition
    /// leaves to run time, which the engine substitutes with their canonical forms in the running context, so equal cells
    /// mean one class there, or two different value constants, which never hold. No user code runs; immutable and
    /// thread-safe.
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
