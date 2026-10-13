using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An atom's constraints after <see cref="Database.ProcessConstraints(TableId, ReadOnlySpan{Constraint})"/>: the
    /// rows matching the fast ones, and the constraints sorted into fast and slow. egglog's
    /// <c>ProcessedConstraints</c> (core-relations/src/free_join/mod.rs:70-104).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fast constraints were answered when the object was made, in O(log n) or O(1): ranges on a sorted column by the
    /// table, equalities on a cacheable column through the database's column index. Their result is
    /// <see cref="Rows"/>, which may include tombstones (whoever scans it through the table gets only live rows).
    /// Slow constraints need a scan and are left to the join. With no fast constraints the subset is the whole table.
    /// </para>
    /// <para>
    /// The subset is only meaningful for the table generation it was computed against. The object owns it:
    /// <see cref="Dispose"/> returns a sparse subset's buffer to the pool. The constraint arrays are immutable and
    /// shared by clones; callers must not modify them. Not thread-safe for <see cref="TakeSubset"/> and
    /// <see cref="Dispose"/>; reads are.
    /// </para>
    /// </remarks>
    internal sealed class ProcessedConstraints : IDisposable
    {

        Subset subset;

        /// <summary>
        /// Initializes a new instance that takes ownership of <paramref name="subset"/> and the arrays.
        /// </summary>
        /// <param name="subset">The rows matching every fast constraint.</param>
        /// <param name="fast">The fast constraints.</param>
        /// <param name="slow">The slow constraints.</param>
        public ProcessedConstraints(Subset subset, Constraint[] fast, Constraint[] slow)
        {
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);

            this.subset = subset;
            Fast = fast;
            Slow = slow;
        }

        /// <summary>
        /// Creates an instance with an empty subset and no constraints. egglog's <c>dummy</c>.
        /// </summary>
        /// <returns>The instance.</returns>
        public static ProcessedConstraints Dummy() => new(default, Array.Empty<Constraint>(), Array.Empty<Constraint>());

        /// <summary>
        /// Gets the rows matching every fast constraint; valid until <see cref="TakeSubset"/> or <see cref="Dispose"/>.
        /// </summary>
        public SubsetRef Rows => subset.AsRef();

        /// <summary>
        /// Gets the constraints the subset already answers. Callers must not modify the array.
        /// </summary>
        public Constraint[] Fast { get; }

        /// <summary>
        /// Gets the constraints that need a scan. Callers must not modify the array.
        /// </summary>
        public Constraint[] Slow { get; }

        /// <summary>
        /// Gets the size of the subset: the planner's size estimate for the atom. egglog's <c>approx_size</c>. O(1).
        /// </summary>
        public int ApproximateSize => subset.Size;

        /// <summary>
        /// Moves the subset out, leaving the empty subset behind; the caller then owns it.
        /// </summary>
        /// <returns>The subset.</returns>
        public Subset TakeSubset() => subset.Take();

        /// <summary>
        /// Creates a copy with its own subset and the same constraint arrays. egglog's <c>Clone</c>.
        /// </summary>
        /// <returns>The copy, which the caller must dispose separately.</returns>
        public ProcessedConstraints Clone() => new(subset.Clone(), Fast, Slow);

        /// <summary>
        /// Returns a sparse subset's buffer to the pool. Idempotent.
        /// </summary>
        public void Dispose() => subset.Dispose();

    }

}
