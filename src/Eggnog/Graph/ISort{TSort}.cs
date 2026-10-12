namespace Eggnog
{

    /// <summary>
    /// An e-class sort: a struct wrapping a class, e.g. <c>readonly record struct Pred(ClassId Id) : ISort&lt;Pred&gt;</c>.
    /// Register it with <see cref="EGraph.DefineSort{TSort}(string)"/>. The library calls <see cref="FromId"/> only with
    /// a <see cref="ClassId"/> it made.
    /// </summary>
    /// <typeparam name="TSort">The sort itself.</typeparam>
    public interface ISort<TSort> where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Gets the class this value wraps.
        /// </summary>
        ClassId Id { get; }

        /// <summary>
        /// Wraps a class the library made.
        /// </summary>
        /// <param name="id">The class.</param>
        /// <returns>The sort value.</returns>
        static abstract TSort FromId(ClassId id);

    }

}
