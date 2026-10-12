namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a fact of a random program is.
    /// </summary>
    internal enum DiffFactKind
    {

        /// <summary>
        /// A relation row.
        /// </summary>
        Relation,

        /// <summary>
        /// An equality of two terms.
        /// </summary>
        Equal,

        /// <summary>
        /// A disequality of two terms.
        /// </summary>
        NotEqual,

        /// <summary>
        /// A boolean computation that must hold.
        /// </summary>
        Filter,

        /// <summary>
        /// An application that must have a row.
        /// </summary>
        Exists,

    }

}
