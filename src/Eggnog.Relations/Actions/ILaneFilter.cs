namespace Eggnog.Relations
{

    /// <summary>
    /// Decides, lane by lane, whether a lane of an action batch stays live: the closure egglog passes to
    /// <c>MaskIter::retain</c> (core-relations/src/action/mask.rs:131-148). Assertions are filters.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass the struct by reference to <see cref="Mask.Retain{TFilter}"/>, so the JIT
    /// specializes the lane loop and inlines the test, the way Rust monomorphizes the closure.
    /// </remarks>
    internal interface ILaneFilter
    {

        /// <summary>
        /// Gets whether <paramref name="lane"/> stays live. Called once per live lane, in ascending lane order.
        /// </summary>
        /// <param name="lane">A live lane, below <see cref="Mask.MaxLanes"/>.</param>
        /// <returns><c>false</c> to drop the lane.</returns>
        bool Keep(int lane);

    }

}
