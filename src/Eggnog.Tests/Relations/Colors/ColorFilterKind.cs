namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The condition of a <see cref="ColorFilter"/>.
    /// </summary>
    enum ColorFilterKind
    {

        /// <summary>
        /// The two ids differ (<see cref="ColorSchema.Ne"/>): an assert-ne on id columns.
        /// </summary>
        Ne,

        /// <summary>
        /// The first id is smaller than the second (<see cref="ColorSchema.Less"/>): a primitive that depends on id
        /// order.
        /// </summary>
        Less,

    }

}
