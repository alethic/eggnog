using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The definitions of <see cref="BridgeDiffPrimitive"/>, shared by the e-graphs (registered as external functions)
    /// and the oracle (called directly), so that both evaluate the same functions.
    /// </summary>
    /// <remarks>
    /// Integers are non-negative and below <see cref="Cap"/>, so their cells are their values (<see cref="Int64Codec"/>
    /// stores them inline) and the primitives work on raw cells without boxing.
    /// </remarks>
    static class BridgeDiffPrimitives
    {

        /// <summary>
        /// The value <see cref="BridgeDiffPrimitive.Add"/> saturates at, well below 2^31.
        /// </summary>
        public const uint Cap = 1_000_000_000;

        /// <summary>
        /// The number of primitives.
        /// </summary>
        public const int Count = 5;

        /// <summary>
        /// Evaluates <paramref name="primitive"/> on two cells.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="unit">The cell of the unit value, which the filters produce.</param>
        /// <param name="result">The result, when there is one.</param>
        /// <returns><c>false</c> when the primitive produces nothing (a filter that fails).</returns>
        public static bool TryEvaluate(BridgeDiffPrimitive primitive, uint a, uint b, uint unit, out uint result)
        {
            switch (primitive)
            {
                case BridgeDiffPrimitive.NotEqual:
                    result = unit;
                    return a != b;
                case BridgeDiffPrimitive.Less:
                    result = unit;
                    return a < b;
                case BridgeDiffPrimitive.Add:
                    result = (uint)Math.Min((ulong)a + b, Cap);
                    return true;
                case BridgeDiffPrimitive.Min:
                    result = Math.Min(a, b);
                    return true;
                case BridgeDiffPrimitive.Max:
                    result = Math.Max(a, b);
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(primitive));
            }
        }

        /// <summary>
        /// Registers every primitive with <paramref name="egraph"/> as an external function.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="unit">The cell of the unit value.</param>
        /// <returns>The external function of each primitive, by <see cref="BridgeDiffPrimitive"/>.</returns>
        public static ExternalFunctionId[] Register(EGraph egraph, uint unit)
        {
            var ids = new ExternalFunctionId[Count];
            for (var i = 0; i < Count; i++)
            {
                var primitive = (BridgeDiffPrimitive)i;
                ids[i] = egraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
                {
                    if (args.Length != 2)
                        throw new InvalidOperationException($"{Name(primitive)} takes two arguments, not {args.Length}.");

                    return TryEvaluate(primitive, args[0], args[1], unit, out result);
                }));
            }

            return ids;
        }

        /// <summary>
        /// Gets the sort of what <paramref name="primitive"/> produces: the unit sort for the filters, integers otherwise.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <returns>The sort.</returns>
        public static int ResultSort(BridgeDiffPrimitive primitive) => primitive is BridgeDiffPrimitive.NotEqual or BridgeDiffPrimitive.Less ? BridgeDiffSort.Unit : BridgeDiffSort.I64;

        /// <summary>
        /// Gets the egglog name of <paramref name="primitive"/>.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <returns>The name.</returns>
        public static string Name(BridgeDiffPrimitive primitive) => primitive switch
        {
            BridgeDiffPrimitive.NotEqual => "!=",
            BridgeDiffPrimitive.Less => "<",
            BridgeDiffPrimitive.Add => "+",
            BridgeDiffPrimitive.Min => "min",
            _ => "max",
        };

    }

}
