namespace Eggnog
{

    /// <summary>
    /// What a fact is.
    /// </summary>
    internal enum FactKind
    {

        /// <summary>
        /// A relation holds a tuple.
        /// </summary>
        Relation,

        /// <summary>
        /// Two terms are equal.
        /// </summary>
        Equality,

        /// <summary>
        /// Two terms differ: a filter.
        /// </summary>
        NotEqual,

        /// <summary>
        /// An application has a row.
        /// </summary>
        Exists,

        /// <summary>
        /// A boolean computation is true: a filter.
        /// </summary>
        Filter,

        /// <summary>
        /// <see cref="Fact.In"/>: facts whose table applications read a tagged scope (R2).
        /// </summary>
        Scoped,

    }

}
