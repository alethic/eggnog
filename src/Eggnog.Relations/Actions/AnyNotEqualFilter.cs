using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the lanes in which two operand lists differ at some position: the closure of egglog's
    /// <c>Instr::AssertAnyNe</c> (core-relations/src/action/mod.rs:863-872), <c>vals[..d].zip(vals[d..]).any(l != r)</c>.
    /// </summary>
    internal readonly struct AnyNotEqualFilter : ILaneFilter
    {

        readonly ArgGather ops;
        readonly int divider;
        readonly int pairs;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ops">Gathers both lists, one after the other.</param>
        /// <param name="divider">The length of the first list.</param>
        /// <param name="pairs">The number of pairs compared.</param>
        public AnyNotEqualFilter(ArgGather ops, int divider, int pairs)
        {
            this.ops = ops;
            this.divider = divider;
            this.pairs = pairs;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int lane)
        {
            ops.Load(lane);
            var cells = ops.Cells;
            for (var i = 0; i < pairs; i++)
                if (cells[i] != cells[divider + i])
                    return true;

            return false;
        }

    }

}
