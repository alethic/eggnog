namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// What a query of <see cref="QueryScopeBenchmarks"/> asserts before it saturates.
    /// </summary>
    public enum QueryMode
    {

        /// <summary>
        /// A fact of a relation no rule reads: every rule's run is idle, and the run ends after one iteration that
        /// changes nothing.
        /// </summary>
        Idle,

        /// <summary>
        /// The one fact of <c>p0</c> the root lacks: every rule reading <c>p0</c> matches once, and the run ends after
        /// a second iteration that changes nothing.
        /// </summary>
        Active,

    }

}
