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
        /// A call holds or runs (<see cref="Fact.Exists"/>): an application has a row or a primitive's call a result, or, in a head or a host write, the call runs for its effect.
        /// </summary>
        Exists,

        /// <summary>
        /// A boolean computation is true: a filter.
        /// </summary>
        Filter,

        /// <summary>
        /// <see cref="Fact.In"/>: facts whose table applications read a tagged scope.
        /// </summary>
        Scoped,

    }

}
