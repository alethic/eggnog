namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The column types of a random program: the CLR types <c>long</c>, <c>string</c>, <c>Set&lt;int&gt;</c>, the
    /// sort <see cref="Support.Node"/>, and <c>bool</c> for filter results.
    /// </summary>
    internal enum DiffType
    {

        /// <summary>
        /// <c>long</c>, the engine's <c>i64</c>.
        /// </summary>
        I64,

        /// <summary>
        /// <c>string</c>.
        /// </summary>
        Str,

        /// <summary>
        /// <c>Set&lt;int&gt;</c>, merged by <c>ValueLattice.Intersect&lt;int&gt;()</c>.
        /// </summary>
        IntSet,

        /// <summary>
        /// The sort <see cref="Support.Node"/>: cells are classes.
        /// </summary>
        Node,

        /// <summary>
        /// <c>bool</c>: a filter's result, never a column.
        /// </summary>
        Bool,

    }

}
