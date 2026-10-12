namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a term of a random program is.
    /// </summary>
    internal enum DiffTermKind
    {

        /// <summary>
        /// A variable of the rule or query.
        /// </summary>
        Var,

        /// <summary>
        /// A constant: a value, or for a sort the class of a host-made term.
        /// </summary>
        Const,

        /// <summary>
        /// An application of a function or constructor.
        /// </summary>
        App,

        /// <summary>
        /// A computation: a named primitive or a lambda.
        /// </summary>
        Prim,

    }

}
