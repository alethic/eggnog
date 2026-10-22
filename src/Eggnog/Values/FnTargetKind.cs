namespace Eggnog
{

    /// <summary>
    /// What a function value applies: egglog's <c>ResolvedFunctionId</c> (src/sort/fn.rs:520-548).
    /// </summary>
    internal enum FnTargetKind
    {

        /// <summary>
        /// A constructor, egglog's <c>Constructor</c>: applied, it gets or makes the term, only where writes are.
        /// </summary>
        Constructor,

        /// <summary>
        /// A function, egglog's <c>Function</c>: applied, it reads the value at its key, only where reads are.
        /// </summary>
        Function,

        /// <summary>
        /// A primitive, egglog's <c>Primitive</c>: applied, it runs where its access is allowed (decisions row 12).
        /// </summary>
        Primitive,

    }

}
