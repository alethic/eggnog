using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IHeaderEvaluator"/> seen as an <see cref="ILayeredHeaderEvaluator"/> whose every table has one layer,
    /// layer 0: how <see cref="PlanTemplate.Instantiate{TEvaluator}"/> forwards to
    /// <see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/>, so single-table evaluators (a root database, test
    /// statistics) instantiate plans through the layered path, one layer per table.
    /// </summary>
    /// <typeparam name="TEvaluator">The wrapped evaluator's type.</typeparam>
    internal struct SingleLayerEvaluator<TEvaluator> : ILayeredHeaderEvaluator
        where TEvaluator : IHeaderEvaluator
    {

        TEvaluator inner;

        /// <summary>
        /// Wraps <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">The evaluator.</param>
        public SingleLayerEvaluator(TEvaluator inner)
        {
            this.inner = inner;
        }

        /// <summary>
        /// Gets the wrapped evaluator, with any state its calls changed.
        /// </summary>
        public readonly TEvaluator Inner => inner;

        /// <summary>
        /// Evaluates the header with the wrapped evaluator, as layer 0.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the constraints the subset answers.</param>
        /// <param name="slow">Receives the others.</param>
        /// <param name="scratch">Unused: one layer needs no working buffers.</param>
        /// <returns>The rows, in layer 0, which the caller owns.</returns>
        public LayeredSubset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow, HeaderScratch? scratch) =>
            LayeredSubset.Single(0, inner.Evaluate(table, constraints, fast, slow));

    }

}
