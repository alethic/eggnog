namespace Eggnog.Relations
{

    /// <summary>
    /// The process-wide slot number of the container type <typeparamref name="TContainer"/>
    /// (<see cref="ContainerTypeSlots"/>). The JIT treats the initialized static as a constant, so finding a type's
    /// environment costs one array load.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal static class ContainerTypeSlot<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// The slot number.
        /// </summary>
        public static readonly int Index = ContainerTypeSlots.Allocate();

    }

}
