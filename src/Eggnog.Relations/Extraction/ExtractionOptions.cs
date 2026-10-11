namespace Eggnog.Relations
{

    /// <summary>
    /// Which functions extraction may use: egglog's <c>ExtractionOptions</c> (src/extract.rs:142-158), without the cost
    /// model and root sorts, which <see cref="Extractor"/> takes directly.
    /// </summary>
    /// <remarks>
    /// Immutable.
    /// </remarks>
    public sealed class ExtractionOptions
    {

        /// <summary>
        /// Gets the options for user extraction: unextractable and hidden functions are skipped, views are used (they
        /// carry the better names). egglog's <c>compute_costs_from_rootsorts</c> (src/extract.rs:167-183).
        /// </summary>
        public static ExtractionOptions Default { get; } = new();

        /// <summary>
        /// Gets the options for proof extraction: unextractable and hidden functions are used, views are skipped so that
        /// terms come from the original term tables under their own names. egglog's
        /// <c>compute_costs_from_rootsorts_allow_unextractable</c> (src/extract.rs:190-205).
        /// </summary>
        public static ExtractionOptions AllowUnextractable { get; } = new()
        {
            RespectUnextractable = false,
            SkipViewTables = true,
            RespectHidden = false,
        };

        /// <summary>
        /// Gets whether functions marked <see cref="FunctionSignature.Unextractable"/> are skipped.
        /// </summary>
        public bool RespectUnextractable { get; init; } = true;

        /// <summary>
        /// Gets whether views (functions with a <see cref="FunctionSignature.TermConstructor"/>) are skipped.
        /// </summary>
        public bool SkipViewTables { get; init; }

        /// <summary>
        /// Gets whether functions marked <see cref="FunctionSignature.Hidden"/> are skipped.
        /// </summary>
        public bool RespectHidden { get; init; } = true;

    }

}
