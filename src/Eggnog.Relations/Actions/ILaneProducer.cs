namespace Eggnog.Relations
{

    /// <summary>
    /// Produces one value per live lane of an action batch, or fails the lane: the closure egglog passes to
    /// <c>MaskIter::fill_vec</c> and <c>MaskIter::assign_vec_and_retain</c> (core-relations/src/action/mask.rs:159-226).
    /// Table lookups and external function calls are producers.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass the struct by reference to <see cref="Mask.Fill{TProducer}"/> or
    /// <see cref="Mask.AssignAndRetain{TProducer}"/>, so the lane loop is specialized and the call inlined.
    /// </remarks>
    internal interface ILaneProducer
    {

        /// <summary>
        /// Produces the value for <paramref name="lane"/>. Called once per live lane, in ascending lane order.
        /// </summary>
        /// <param name="lane">A live lane, below <see cref="Mask.MaxLanes"/>.</param>
        /// <param name="value">The value, when the result is <c>true</c>.</param>
        /// <returns><c>false</c> to fail the lane, which drops it from the mask (egglog's <c>None</c>).</returns>
        bool TryProduce(int lane, out uint value);

    }

}
