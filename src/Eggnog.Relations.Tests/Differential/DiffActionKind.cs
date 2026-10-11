namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The instructions a differential rule's action may hold besides the recorder call that every action starts with
    /// (<see cref="DiffAction"/>).
    /// </summary>
    enum DiffActionKind
    {

        /// <summary>
        /// Inserts a row (<see cref="RuleBuilder.Insert"/>); into the union-find it is a union.
        /// </summary>
        Insert,

        /// <summary>
        /// Removes a key (<see cref="RuleBuilder.Remove"/>).
        /// </summary>
        Remove,

        /// <summary>
        /// Looks a key up and binds one column of its row to a new variable; lanes whose key is absent halt
        /// (<see cref="RuleBuilder.Lookup"/>).
        /// </summary>
        Lookup,

        /// <summary>
        /// Halts lanes where two operands differ (<see cref="RuleBuilder.AssertEq"/>).
        /// </summary>
        AssertEq,

        /// <summary>
        /// Halts lanes where two operands are equal (<see cref="RuleBuilder.AssertNe"/>).
        /// </summary>
        AssertNe,

    }

}
