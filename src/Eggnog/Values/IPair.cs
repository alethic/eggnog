namespace Eggnog
{

    /// <summary>
    /// A <see cref="Pair{T1, T2}"/> seen without its type arguments, for rendering a pair constant that no column type
    /// describes.
    /// </summary>
    internal interface IPair
    {

        /// <summary>
        /// Gets the first element.
        /// </summary>
        object FirstValue { get; }

        /// <summary>
        /// Gets the second element.
        /// </summary>
        object SecondValue { get; }

    }

}
