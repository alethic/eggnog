namespace Eggnog
{

    /// <summary>
    /// What kind of table a handle names.
    /// </summary>
    public enum TableKind
    {

        /// <summary>
        /// A set of value or class tuples (D27).
        /// </summary>
        Relation,

        /// <summary>
        /// A map from keys to a value, merged by a lattice or required to agree.
        /// </summary>
        Function,

        /// <summary>
        /// A term constructor: a map from keys to the class of the term.
        /// </summary>
        Constructor,

    }

}
