using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// What extraction needs to know about a function of an <see cref="EGraph"/> beyond its column types: the sorts of its
    /// columns and the parts of egglog's frontend <c>FunctionDecl</c> that extraction reads (src/extract.rs:219-243,
    /// 759-818): whether it is a constructor, its cost, and the <c>unextractable</c>, <c>internal_hidden</c> and
    /// <c>term_constructor</c> annotations. Declared with <see cref="ExtractionSchema.Declare"/>, which copies what it
    /// needs, so the instance may be reused.
    /// </summary>
    internal sealed class FunctionSignature
    {

        /// <summary>
        /// Gets the sorts of the key columns, in order; one per key of the function.
        /// </summary>
        public required IReadOnlyList<Sort> Inputs { get; init; }

        /// <summary>
        /// Gets the sort of the output column.
        /// </summary>
        public required Sort Output { get; init; }

        /// <summary>
        /// Gets whether the function is a constructor (egglog's <c>FunctionSubtype::Constructor</c>): its rows are the
        /// e-nodes of the classes of its output sort, which must then be an <see cref="EqSort"/>. Rows of other functions
        /// are never extracted from, except views (<see cref="TermConstructor"/>).
        /// </summary>
        public bool IsConstructor { get; init; }

        /// <summary>
        /// Gets the cost of one row, without its children: egglog's <c>:cost</c>. <c>null</c> means 1, egglog's
        /// <c>DefaultCost::unit()</c>. Must not be negative.
        /// </summary>
        public long? Cost { get; init; }

        /// <summary>
        /// Gets whether extraction must not use the function's rows: egglog's <c>:unextractable</c>. Ignored by
        /// <see cref="ExtractionOptions.AllowUnextractable"/>.
        /// </summary>
        public bool Unextractable { get; init; }

        /// <summary>
        /// Gets whether the function is an internal one that extraction hides: egglog's <c>internal_hidden</c>. Ignored
        /// by <see cref="ExtractionOptions.AllowUnextractable"/>.
        /// </summary>
        public bool Hidden { get; init; }

        /// <summary>
        /// Gets the hidden term constructor this function is a view of, if any: egglog's <c>term_constructor</c>, used
        /// by its proof encoding. A view's rows are <c>[children..., class, view]</c>: its last key is the e-class (whose
        /// sort, an <see cref="EqSort"/>, is the one it produces), the keys before it are the children, and it extracts
        /// under the term constructor's name and, unless the cost model says otherwise, its cost.
        /// </summary>
        public FunctionId? TermConstructor { get; init; }

    }

}
