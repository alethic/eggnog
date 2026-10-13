using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A coarse cardinality estimate for one column of a table: egglog's <c>ColUniqueness</c>
    /// (core-relations/src/free_join/mod.rs:904-976).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TableSize"/> is the number of rows and <see cref="ColumnSize"/> the number of distinct values in the
    /// column. Their ratio approximates how many rows share one value; estimates are ordered by that ratio (smaller is
    /// more unique, so cheaper to join on), compared without division as egglog does, by cross-multiplying with
    /// saturation. Tree decomposition uses it to break ties between variables with the same neighbourhood size.
    /// </para>
    /// <para>
    /// Equality is egglog's <c>PartialEq</c>, <c>cmp(other) == Equal</c> (mod.rs:957-963): two estimates are equal
    /// when their ratios are, so 100 rows over 50 values equals 10 rows over 5 values, and
    /// <see cref="Equals(ColumnUniqueness)"/>, <c>==</c> and <see cref="CompareTo"/> always agree. Like egglog's, this
    /// equality is not transitive at the edges: an empty estimate (0, 0) and saturated products compare equal to
    /// estimates that differ from each other. So the only hash code consistent with it is a constant; egglog does not
    /// hash the type either, and the planner only orders estimates. Compare the fields to tell two estimates apart.
    /// </para>
    /// </remarks>
    internal readonly struct ColumnUniqueness : IEquatable<ColumnUniqueness>, IComparable<ColumnUniqueness>
    {

        /// <summary>
        /// Initializes a new estimate.
        /// </summary>
        /// <param name="tableSize">The number of rows.</param>
        /// <param name="columnSize">The number of distinct values in the column.</param>
        public ColumnUniqueness(ulong tableSize, ulong columnSize)
        {
            TableSize = tableSize;
            ColumnSize = columnSize;
        }

        /// <summary>
        /// Gets the neutral element of <see cref="Join"/>: one row, one value. egglog's <c>Default</c>.
        /// </summary>
        public static ColumnUniqueness Identity => new(1, 1);

        /// <summary>
        /// Gets the number of rows.
        /// </summary>
        public ulong TableSize { get; }

        /// <summary>
        /// Gets the number of distinct values in the column.
        /// </summary>
        public ulong ColumnSize { get; }

        /// <summary>
        /// Combines the estimates of a variable's occurrences: table sizes multiply (saturating) and the column size is
        /// the larger one. egglog's <c>join</c>.
        /// </summary>
        /// <param name="other">The other estimate.</param>
        /// <returns>The combined estimate.</returns>
        public ColumnUniqueness Join(ColumnUniqueness other) => new(SaturatingMultiply(TableSize, other.TableSize), Math.Max(ColumnSize, other.ColumnSize));

        /// <summary>
        /// Scales the estimate down to a subset of <paramref name="subsetSize"/> rows, assuming the distinct values are
        /// spread evenly: <c>(subsetSize, ColumnSize * subsetSize / TableSize)</c> with a saturating product, or (0, 0)
        /// when either size is zero. egglog's <c>scale</c> (core-relations/src/free_join/mod.rs:931-943), which nothing
        /// in egglog calls.
        /// </summary>
        /// <param name="subsetSize">The number of rows in the subset.</param>
        /// <returns>The scaled estimate.</returns>
        public ColumnUniqueness Scale(ulong subsetSize)
        {
            if (TableSize == 0 || subsetSize == 0)
                return new ColumnUniqueness(0, 0);

            return new ColumnUniqueness(subsetSize, SaturatingMultiply(ColumnSize, subsetSize) / TableSize);
        }

        /// <summary>
        /// Compares by the ratio rows / distinct values: <c>(a.Table * b.Column).CompareTo(b.Table * a.Column)</c>, with
        /// saturating products, exactly as egglog's <c>Ord</c>.
        /// </summary>
        /// <param name="other">The other estimate.</param>
        /// <returns>Negative if this one is more unique.</returns>
        public int CompareTo(ColumnUniqueness other) => SaturatingMultiply(TableSize, other.ColumnSize).CompareTo(SaturatingMultiply(other.TableSize, ColumnSize));

        /// <summary>
        /// Whether the two estimates have the same ratio: <c>CompareTo(other) == 0</c>, egglog's <c>PartialEq</c>.
        /// </summary>
        /// <param name="other">The other estimate.</param>
        /// <returns><c>true</c> if neither is more unique than the other.</returns>
        public bool Equals(ColumnUniqueness other) => CompareTo(other) == 0;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ColumnUniqueness other && Equals(other);

        /// <summary>
        /// Returns a constant: the equality is ratio-based and, at its edges, not transitive (see the remarks), so no
        /// finer hash agrees with it.
        /// </summary>
        /// <returns>Zero.</returns>
        public override int GetHashCode() => 0;

        /// <summary>
        /// Whether the two estimates have the same ratio (see <see cref="Equals(ColumnUniqueness)"/>).
        /// </summary>
        /// <param name="left">The first estimate.</param>
        /// <param name="right">The second estimate.</param>
        /// <returns><c>true</c> if they compare equal.</returns>
        public static bool operator ==(ColumnUniqueness left, ColumnUniqueness right) => left.Equals(right);

        /// <summary>
        /// Whether the two estimates have different ratios.
        /// </summary>
        /// <param name="left">The first estimate.</param>
        /// <param name="right">The second estimate.</param>
        /// <returns><c>true</c> if they do not compare equal.</returns>
        public static bool operator !=(ColumnUniqueness left, ColumnUniqueness right) => left.Equals(right) == false;

        static ulong SaturatingMultiply(ulong a, ulong b)
        {
            var product = a * b;
            return a != 0 && product / a != b ? ulong.MaxValue : product;
        }

        /// <inheritdoc />
        public override string ToString() => $"{TableSize}/{ColumnSize}";

    }

}
