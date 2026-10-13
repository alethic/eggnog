namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The kind of a table of a <see cref="ScopedProgram"/>.
    /// </summary>
    internal enum ScopedTableKind
    {

        /// <summary>
        /// A relation over values.
        /// </summary>
        Relation,

        /// <summary>
        /// A function from values to an <c>i64</c>, merged by max or min.
        /// </summary>
        Function,

        /// <summary>
        /// A constructor <c>C(i64) → Node</c> (programs with constructors only).
        /// </summary>
        Constructor,

        /// <summary>
        /// A relation over <c>Node</c> (programs with constructors only).
        /// </summary>
        NodeRelation,

    }

}
