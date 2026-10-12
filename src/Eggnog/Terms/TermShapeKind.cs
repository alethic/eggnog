namespace Eggnog
{

    /// <summary>
    /// What a term is.
    /// </summary>
    internal enum TermShapeKind
    {

        /// <summary>
        /// A variable of a rule builder.
        /// </summary>
        Variable,

        /// <summary>
        /// A constant: a value, or a class of a sort.
        /// </summary>
        Constant,

        /// <summary>
        /// An application of a function or a constructor.
        /// </summary>
        Application,

        /// <summary>
        /// An application of a primitive.
        /// </summary>
        Computation,

    }

}
