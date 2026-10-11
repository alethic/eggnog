namespace Eggnog.Relations
{

    /// <summary>
    /// Visits each live lane of an action batch: the closure egglog passes to <c>MaskIter::for_each</c>
    /// (core-relations/src/action/mask.rs:228-237). Staging inserts and removals are visitors.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass the struct by reference to <see cref="Mask.ForEach{TVisitor}"/>, so the lane
    /// loop is specialized and the call inlined.
    /// </remarks>
    internal interface ILaneVisitor
    {

        /// <summary>
        /// Visits <paramref name="lane"/>. Called once per live lane, in ascending lane order.
        /// </summary>
        /// <param name="lane">A live lane, below <see cref="Mask.MaxLanes"/>.</param>
        void Visit(int lane);

    }

}
