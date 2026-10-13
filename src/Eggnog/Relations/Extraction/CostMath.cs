using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Cost arithmetic: non-negative <see cref="long"/> costs added with saturation, egglog's <c>Cost::combine</c> for
    /// integers (<c>saturating_add</c>, src/extract.rs:65-77). A saturated cost is still the cost of a finite term, as
    /// in egglog, where <c>u64::MAX</c> is an ordinary cost.
    /// </summary>
    internal static class CostMath
    {

        /// <summary>
        /// Adds two non-negative costs, saturating at <see cref="long.MaxValue"/>.
        /// </summary>
        /// <param name="left">A cost, at least 0.</param>
        /// <param name="right">A cost, at least 0.</param>
        /// <returns>The sum, or <see cref="long.MaxValue"/> if it overflows.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Add(long left, long right)
        {
            Debug.Assert(left >= 0 && right >= 0, "costs are non-negative");

            // Both operands are non-negative, so the sum overflows exactly when it wraps negative.
            var sum = unchecked(left + right);
            return sum < 0 ? long.MaxValue : sum;
        }

        /// <summary>
        /// Checks a cost a cost model computed: costs must not be negative. Allocation-free on success: the message is
        /// built only when the check fails, so per-row callers pass its parts, not the message.
        /// </summary>
        /// <param name="cost">The cost.</param>
        /// <param name="what">What kind of thing was costed, for the message: "a row of", "a value of", ...</param>
        /// <param name="name">The name of the function or sort the costed thing belongs to, for the message.</param>
        /// <returns>The cost.</returns>
        /// <exception cref="InvalidOperationException">The cost is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Check(long cost, string what, string name)
        {
            if (cost < 0)
                ThrowNegative(cost, what, name);

            return cost;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNegative(long cost, string what, string name)
        {
            throw new InvalidOperationException($"The cost model gave {what} {name} the negative cost {cost}; costs must not be negative.");
        }

    }

}
