using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the journal rows of an <see cref="OverlayDisplacedTable"/> that satisfy one constraint, with the canonical
    /// column computed on the fly: the filter behind <see cref="OverlayDisplacedTable.RefineOne"/>, mirroring
    /// <see cref="DisplacedConstraintFilter"/>. The union-find table has no tombstones.
    /// </summary>
    internal readonly struct OverlayDisplacedConstraintFilter : IOffsetFilter
    {

        readonly OverlayDisplacedTable table;
        readonly Constraint constraint;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The child's union-find table.</param>
        /// <param name="constraint">The constraint, on columns 0 to 2.</param>
        public OverlayDisplacedConstraintFilter(OverlayDisplacedTable table, Constraint constraint)
        {
            this.table = table;
            this.constraint = constraint;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int row) => table.Evaluate(constraint, row);

    }

}
