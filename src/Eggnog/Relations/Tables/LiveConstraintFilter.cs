using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the rows of a row-major buffer that are live and satisfy one constraint: the filter behind
    /// <see cref="SortedWritesTable.RefineOne"/>, egglog's <c>get_if</c> (core-relations/src/table/mod.rs:1027-1034),
    /// which reads through <c>get_row</c> and so drops tombstones before evaluating.
    /// </summary>
    /// <remarks>
    /// The caller has checked that every row the subset names lies within <c>data</c>; the filter indexes with
    /// ordinary array bounds checks regardless.
    /// </remarks>
    internal readonly struct LiveConstraintFilter : IOffsetFilter
    {

        readonly uint[] data;
        readonly int arity;
        readonly Constraint constraint;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="data">The row storage.</param>
        /// <param name="arity">The cells per row.</param>
        /// <param name="constraint">The constraint.</param>
        public LiveConstraintFilter(uint[] data, int arity, Constraint constraint)
        {
            Debug.Assert(arity >= 1);
            this.data = data;
            this.arity = arity;
            this.constraint = constraint;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int row)
        {
            var offset = row * arity;
            return data[offset] != Value.StaleRep && constraint.Evaluate(data, offset);
        }

    }

}
