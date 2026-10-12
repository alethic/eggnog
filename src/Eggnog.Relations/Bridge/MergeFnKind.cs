namespace Eggnog.Relations
{

    /// <summary>
    /// The cases of egglog-bridge's <c>MergeFn</c> (egglog-bridge/src/lib.rs:1010-1030); see <see cref="MergeFn"/>.
    /// </summary>
    internal enum MergeFnKind : byte
    {

        /// <summary>
        /// Panic if the old and new values differ; keep the old one.
        /// </summary>
        AssertEq,

        /// <summary>
        /// Union the old and new e-class ids and keep the smaller.
        /// </summary>
        UnionId,

        /// <summary>
        /// Apply an external function to the results of the argument merge functions.
        /// </summary>
        Primitive,

        /// <summary>
        /// Look up (or, for a function with a default, insert) the results of the argument merge functions as a key of
        /// another function.
        /// </summary>
        Function,

        /// <summary>
        /// Keep the old value.
        /// </summary>
        Old,

        /// <summary>
        /// Take the new value.
        /// </summary>
        New,

        /// <summary>
        /// Produce a constant.
        /// </summary>
        Const,

    }

}
