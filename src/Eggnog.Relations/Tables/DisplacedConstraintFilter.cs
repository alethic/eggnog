using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the rows of a <see cref="DisplacedTable"/> that satisfy one constraint, with the canonical column computed
    /// on the fly: the filter behind <see cref="DisplacedTable.RefineOne"/>. The union-find table has no tombstones.
    /// </summary>
    internal readonly struct DisplacedConstraintFilter : IOffsetFilter
    {

        readonly DisplacedTable table;
        readonly Constraint constraint;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The union-find table.</param>
        /// <param name="constraint">The constraint, on columns 0 to 2.</param>
        public DisplacedConstraintFilter(DisplacedTable table, Constraint constraint)
        {
            this.table = table;
            this.constraint = constraint;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int row) => table.Evaluate(constraint, row);

    }

}
