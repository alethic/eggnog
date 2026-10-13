namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="ReferenceContextOp"/> asserts.
    /// </summary>
    enum ReferenceContextOpKind
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

        /// <summary>
        /// A fact a rule run derived (<see cref="ReferenceRuleContext"/>): a row, or a union when the op has no table.
        /// It holds like an <see cref="Insert"/> or a <see cref="Union"/>, and descendants inherit it.
        /// </summary>
        Derived,

    }

}
