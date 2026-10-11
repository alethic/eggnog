namespace Eggnog.Relations
{

    /// <summary>
    /// The shape of a <see cref="Term"/>: egglog's <c>Term</c> variants (src/termdag.rs:13-17), in their order.
    /// </summary>
    public enum TermKind : byte
    {

        /// <summary>
        /// A literal base value: egglog's <c>Term::Lit</c>.
        /// </summary>
        Lit,

        /// <summary>
        /// A named variable: egglog's <c>Term::Var</c>. Extraction uses it for <c>Unextractable</c> placeholders.
        /// </summary>
        Var,

        /// <summary>
        /// An application of a head symbol to child terms: egglog's <c>Term::App</c>.
        /// </summary>
        App,

    }

}
