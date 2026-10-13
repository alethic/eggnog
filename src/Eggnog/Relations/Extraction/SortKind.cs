namespace Eggnog.Relations
{

    /// <summary>
    /// What the values of a <see cref="Sort"/> are, which decides how extraction treats a column of it: egglog's
    /// <c>is_eq_sort</c> and <c>is_container_sort</c> split (src/sort/mod.rs:72-79).
    /// </summary>
    internal enum SortKind : byte
    {

        /// <summary>
        /// E-class ids that constructors produce: egglog's <c>EqSort</c>. Extraction picks a cheapest constructor row per
        /// class.
        /// </summary>
        Eq,

        /// <summary>
        /// Base values of one registered type: egglog's <c>BaseSort</c>. A base value is a leaf with a cost of its own.
        /// </summary>
        Base,

        /// <summary>
        /// Container ids whose elements are values of inner sorts: egglog's <c>ContainerSort</c>. A container costs its
        /// own cost plus the costs of its elements.
        /// </summary>
        Container,

    }

}
