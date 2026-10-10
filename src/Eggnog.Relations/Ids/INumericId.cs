namespace Eggnog.Relations
{

    /// <summary>
    /// A small integer identifier that can index a dense array.
    /// </summary>
    /// <remarks>
    /// Implemented by readonly record structs that wrap an <see cref="int"/>, so that generic collections such as
    /// <see cref="DenseIdMap{TKey, TValue}"/> are specialized per identifier type by the JIT without boxing or
    /// interface dispatch.
    /// </remarks>
    /// <typeparam name="TSelf">The identifier type.</typeparam>
    public interface INumericId<TSelf>
        where TSelf : struct, INumericId<TSelf>
    {

        /// <summary>
        /// Gets the index this identifier occupies in a dense array.
        /// </summary>
        int Index { get; }

        /// <summary>
        /// Creates the identifier at the given index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>The identifier.</returns>
        static abstract TSelf FromIndex(int index);

    }

}
