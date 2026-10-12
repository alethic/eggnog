using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Evaluates the fast constraints of a header against a table as a context sees it: one subset per layer of the
    /// table's view (colored-contexts spec §9.4). The layered form of <see cref="IHeaderEvaluator"/>, which
    /// <see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/> instantiates through. A context's
    /// <see cref="ContextView"/> implements it; <see cref="SingleLayerEvaluator{TEvaluator}"/> wraps a single-table
    /// evaluator as layer 0.
    /// </summary>
    /// <remarks>
    /// Passed as a type argument; a struct implementation gets a specialized call. It runs once per header per
    /// instantiation, a cold path.
    /// </remarks>
    internal interface ILayeredHeaderEvaluator
    {

        /// <summary>
        /// Splits <paramref name="constraints"/> into fast and slow ones for <paramref name="table"/> and returns the rows
        /// matching every fast one, per layer of the table's view.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the constraints the subset answers.</param>
        /// <param name="slow">Receives the others.</param>
        /// <returns>The rows, which the caller owns (they may include tombstones and rows the view hides).</returns>
        LayeredSubset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow);

    }

}
