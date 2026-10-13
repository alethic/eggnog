using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IHeaderEvaluator"/> seen as an <see cref="ILayeredHeaderEvaluator"/> whose every table has one layer,
    /// layer 0 (colored-contexts spec §9.4): how <see cref="PlanTemplate.Instantiate{TEvaluator}"/> forwards to
    /// <see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/>, so single-table evaluators (a root database, test
    /// statistics) keep instantiating plans as before.
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
        /// <returns>The rows, in layer 0, which the caller owns.</returns>
        public LayeredSubset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow) =>
            LayeredSubset.Single(0, inner.Evaluate(table, constraints, fast, slow));

    }

}
