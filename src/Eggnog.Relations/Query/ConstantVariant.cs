using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A cached plan's template and action program with its Id constants replaced by one canonical vector K
    /// (colored-contexts spec §9.8): what a context in which some constant has lost a union runs. Cached per K by
    /// <see cref="CachedPlan.GetVariantFor"/>, so the rewrite happens once per distinct K, not per run.
    /// </summary>
    /// <param name="Key">K: the executed constants, query constants first.</param>
    /// <param name="Template">The template with the canonical query constants.</param>
    /// <param name="Actions">The action program with the canonical action constants.</param>
    internal sealed record ConstantVariant(uint[] Key, PlanTemplate Template, ActionInfo Actions)
    {

        /// <summary>
        /// Gets whether the variant was made for <paramref name="key"/>.
        /// </summary>
        /// <param name="key">A K.</param>
        /// <returns><c>true</c> if the vectors are equal.</returns>
        public bool Matches(ReadOnlySpan<uint> key) => key.SequenceEqual(Key);

    }

}
