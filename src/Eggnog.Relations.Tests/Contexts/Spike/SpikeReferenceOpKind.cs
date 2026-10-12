namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="SpikeReferenceOp"/> asserts.
    /// </summary>
    enum SpikeReferenceOpKind
    {

        /// <summary>
        /// Two classes are equal.
        /// </summary>
        Union,

        /// <summary>
        /// A row holds.
        /// </summary>
        Insert,

        /// <summary>
        /// The row with a key is retracted (applied to the view closed at that point).
        /// </summary>
        Delete,

        /// <summary>
        /// Every row of a table is retracted.
        /// </summary>
        Clear,

    }

}
