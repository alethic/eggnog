using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A function's rows extracted as terms: egglog's <c>function_to_dag</c> result, <c>(inputs, output, termdag)</c>
    /// (src/extract.rs:858-915). <see cref="Extractor.FunctionToDag"/> builds it.
    /// </summary>
    internal sealed class FunctionDag
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="dag">The DAG the terms live in.</param>
        /// <param name="inputs">One term per row: the function applied to the extracted keys.</param>
        /// <param name="outputs">One term per row, the extracted output, or <c>null</c> when outputs were not asked
        /// for.</param>
        internal FunctionDag(TermDag dag, IReadOnlyList<TermId> inputs, IReadOnlyList<TermId>? outputs)
        {
            Dag = dag;
            Inputs = inputs;
            Outputs = outputs;
        }

        /// <summary>
        /// Gets the DAG the terms live in.
        /// </summary>
        public TermDag Dag { get; }

        /// <summary>
        /// Gets one term per row, in table order: the function applied to the cheapest terms of the row's keys, with
        /// the variable <c>Unextractable</c> for a key that has none.
        /// </summary>
        public IReadOnlyList<TermId> Inputs { get; }

        /// <summary>
        /// Gets one term per row, the cheapest term of its output (or <c>Unextractable</c>), when outputs were asked for;
        /// otherwise <c>null</c>.
        /// </summary>
        public IReadOnlyList<TermId>? Outputs { get; }

    }

}
