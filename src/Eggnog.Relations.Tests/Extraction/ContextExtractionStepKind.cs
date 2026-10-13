namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="ContextExtractionStep"/> does.
    /// </summary>
    enum ContextExtractionStepKind
    {

        /// <summary>
        /// Makes a term.
        /// </summary>
        Make,

        /// <summary>
        /// Unions two terms.
        /// </summary>
        Union,

        /// <summary>
        /// Saturates the rules.
        /// </summary>
        Saturate,

        /// <summary>
        /// Creates the next level's context under the step's level, freezing that level first if asked.
        /// </summary>
        Child,

    }

}
