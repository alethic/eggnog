namespace Eggnog
{

    /// <summary>
    /// The base of the typed constructors of one sort (<see cref="Constructor{TSort}"/> and its arities,
    /// <see cref="ConstructorN{TSort}"/>): what a call that makes or reads a term takes, so that the sort is inferred from
    /// the constructor, as in <see cref="ITableWriter.Make{TSort}(ConstructorHandle{TSort}, System.ReadOnlySpan{object})"/>.
    /// </summary>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public abstract class ConstructorHandle<TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected ConstructorHandle()
        {

        }

    }

}
