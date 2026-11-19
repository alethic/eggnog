namespace Eggnog
{

    /// <summary>
    /// How a function is defined, beyond its name, columns and merge. A function is never subsumable, as in egglog,
    /// whose language refuses to subsume one.
    /// </summary>
    public sealed class FunctionOptions
    {

        /// <summary>
        /// Gets whether the function is an analysis (<see cref="TableRole.Analysis"/>), derived from terms only, rather
        /// than a fact (<see cref="TableRole.Fact"/>, the default).
        /// </summary>
        public bool Analysis { get; init; }

    }

}
