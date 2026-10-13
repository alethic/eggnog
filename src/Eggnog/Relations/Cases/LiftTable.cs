using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One table of a <see cref="LiftSpec"/>: the function, how it is lifted, and, for a function with a base output,
    /// the case join that combines the live cases' values.
    /// </summary>
    /// <remarks>
    /// The case join takes two values and returns one that each implies in the table's lattice order (for a table
    /// merged by the minimum, the maximum): the strongest fact every case supports, <c>v_K ⊑ J</c> for every live case
    /// K, which is what keeps the lifted value sound. It is called once per pair of live cases per lifted key, on the
    /// lifting thread, and must be pure. Without one, a value is lifted only when every live case holds the same value.
    /// </remarks>
    /// <param name="Function">The function.</param>
    /// <param name="Kind">How it is lifted.</param>
    /// <param name="CaseJoin">The case join for a base output; <c>null</c> requires the cases to agree.</param>
    internal readonly record struct LiftTable(FunctionId Function, LiftTableKind Kind, Func<uint, uint, uint>? CaseJoin = null);

}
