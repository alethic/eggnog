namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the values a container holds, from <see cref="IContainerValue{TSelf}.ForEachValue{TVisitor}"/>: the
    /// consumer side of egglog's <c>ContainerValue::iter</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by reference, so the container's loop is specialized for the visitor and
    /// the per-element call is direct, where egglog returns an <c>impl Iterator</c> that the compiler inlines.
    /// </remarks>
    internal interface IContainerElementVisitor
    {

        /// <summary>
        /// Receives one value of the container.
        /// </summary>
        /// <param name="value">The value.</param>
        void Visit(uint value);

    }

}
