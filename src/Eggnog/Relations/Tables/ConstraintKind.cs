namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of a <see cref="Constraint"/>: the variants of egglog's <c>Constraint</c> enum
    /// (core-relations/src/table_spec.rs:92-101).
    /// </summary>
    internal enum ConstraintKind : byte
    {

        /// <summary>
        /// Two columns of the row hold the same value.
        /// </summary>
        Eq,

        /// <summary>
        /// A column equals a constant.
        /// </summary>
        EqConst,

        /// <summary>
        /// A column is less than a constant (unsigned comparison of the raw cells).
        /// </summary>
        LtConst,

        /// <summary>
        /// A column is greater than a constant.
        /// </summary>
        GtConst,

        /// <summary>
        /// A column is at most a constant.
        /// </summary>
        LeConst,

        /// <summary>
        /// A column is at least a constant.
        /// </summary>
        GeConst,

    }

}
