namespace Eggnog
{

    /// <summary>
    /// Where a program register comes from.
    /// </summary>
    internal enum RegisterKind
    {

        /// <summary>
        /// A variable the builder declared (<c>Var</c>, <c>Vars</c>, or a rewrite's hidden class variable).
        /// </summary>
        Declared,

        /// <summary>
        /// A body register the compiler made: an application's output or a computation's result.
        /// </summary>
        Fresh,

        /// <summary>
        /// A head register: a get-or-add's class or a head computation's result, defined by the action that makes it.
        /// </summary>
        Head,

    }

}
