using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Cost arithmetic: <see cref="ulong"/> costs added with saturation, egglog's <c>Cost::combine</c> for its <c>u64</c>
    /// costs (<c>saturating_add</c>, src/extract.rs:65-77). A saturated cost is still the cost of a finite term, as in
    /// egglog, where <c>u64::MAX</c> is an ordinary cost.
    /// </summary>
    internal static class CostMath
    {

        /// <summary>
        /// Adds two costs, saturating at <see cref="ulong.MaxValue"/>.
        /// </summary>
        /// <param name="left">A cost.</param>
        /// <param name="right">A cost.</param>
        /// <returns>The sum, or <see cref="ulong.MaxValue"/> if it overflows.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Add(ulong left, ulong right)
        {
            // The sum overflows exactly when it wraps below an operand.
            var sum = unchecked(left + right);
            return sum < left ? ulong.MaxValue : sum;
        }

    }

}
