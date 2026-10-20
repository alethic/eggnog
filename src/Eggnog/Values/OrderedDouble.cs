using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The order and equality of egglog's <c>f64</c>, Rust's <c>OrderedFloat</c>: IEEE order, except that NaN equals
    /// NaN and is greater than every other value; -0.0 equals 0.0. A column holds one NaN and one zero
    /// (<see cref="DoubleColumnType"/>).
    /// </summary>
    internal sealed class OrderedDouble : IComparer<double>
    {

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static OrderedDouble Instance { get; } = new();

        OrderedDouble()
        {
        }

        /// <summary>
        /// Compares two values in <c>OrderedFloat</c>'s order.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        public static int CompareValues(double x, double y)
        {
            if (double.IsNaN(x))
                return double.IsNaN(y) ? 0 : 1;

            if (double.IsNaN(y))
                return -1;

            return x < y ? -1 : x > y ? 1 : 0;
        }

        /// <inheritdoc />
        public int Compare(double x, double y) => CompareValues(x, y);

    }

}
