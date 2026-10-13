using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Evaluates the fast constraints of a header against the current state of a table: egglog's
    /// <c>Database::process_constraints</c> (core-relations/src/free_join/mod.rs:718-761) as used by cached-plan
    /// instantiation (core-relations/src/query.rs:133-154).
    /// </summary>
    /// <remarks>
    /// Passed as the type argument of <see cref="PlanTemplate.Instantiate{TEvaluator}"/>; a struct implementation gets
    /// a specialized call. It runs once per header per instantiation, a cold path.
    /// </remarks>
    internal interface IHeaderEvaluator
    {

        /// <summary>
        /// Splits <paramref name="constraints"/> into fast and slow ones for <paramref name="table"/> and returns the
        /// rows matching every fast one.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the constraints the subset answers.</param>
        /// <param name="slow">Receives the others.</param>
        /// <returns>The subset, which the caller owns (it may include tombstones).</returns>
        Subset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow);

    }

}
