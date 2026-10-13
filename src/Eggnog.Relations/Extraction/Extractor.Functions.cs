using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// The variable that stands for a key or output with no finite term (src/extract.rs:894, 903).
        /// </summary>
        internal const string UnextractableName = "Unextractable";

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>, with an extractor
        /// made for that sort alone. egglog's <c>EGraph::extract_value_with_cost_model</c> (src/extract.rs:831-845).
        /// </summary>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value.</param>
        /// <param name="dag">The DAG to extract into.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The term and its cost.</returns>
        /// <exception cref="InvalidOperationException">The value has no finite term, or the cost model gave a negative
        /// cost.</exception>
        public static ExtractionResult ExtractValue(ExtractionSchema schema, Sort sort, Value value, TermDag dag, CostModel? costs = null)
        {
            ArgumentNullException.ThrowIfNull(sort);
            var extractor = ComputeCostsFromRootSorts(schema, [sort], costs);
            if (extractor.TryExtractBest(value, dag, out var result))
                return result;

            throw new InvalidOperationException($"Value {value} of sort {sort.Name} has no finite term the extractor may use.");
        }

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>, and renders it.
        /// egglog's <c>EGraph::extract_value_to_string</c> (src/extract.rs:847-856).
        /// </summary>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value.</param>
        /// <param name="cost">The term's cost.</param>
        /// <returns>The term as an s-expression.</returns>
        /// <exception cref="InvalidOperationException">The value has no finite term, or the cost model gave a negative
        /// cost.</exception>
        public static string ExtractValueToString(ExtractionSchema schema, Sort sort, Value value, out long cost)
        {
            var dag = new TermDag();
            var result = ExtractValue(schema, sort, value, dag);
            cost = result.Cost;
            return dag.ToString(result.Term);
        }

        /// <summary>
        /// Extracts the first <paramref name="count"/> rows of <paramref name="function"/>, in table order and subsumed
        /// rows included, as terms: each row becomes the function applied to the cheapest terms of its keys and,
        /// optionally, the cheapest term of its output. egglog's <c>EGraph::function_to_dag</c>
        /// (src/extract.rs:858-915), which the frontend's <c>print-function</c> uses.
        /// </summary>
        /// <remarks>
        /// One extractor, rooted at the function's key sorts (and output sort), serves every row; a key or output with no
        /// finite term becomes the variable <c>Unextractable</c>. The rows are read first and extracted afterwards, so the
        /// extraction never runs inside the table scan.
        /// </remarks>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="function">A function declared in the schema; any function, not only a constructor.</param>
        /// <param name="count">The number of rows to extract; at least 0.</param>
        /// <param name="includeOutput">Whether to extract each row's output too.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The terms and the DAG they live in.</returns>
        /// <exception cref="ArgumentException">The function is not declared in the schema.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The cost model gave a negative cost.</exception>
        public static FunctionDag FunctionToDag(
            ExtractionSchema schema,
            FunctionId function,
            int count,
            bool includeOutput,
            CostModel? costs = null)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            var declared = schema.GetFunction(function);
            var inputSorts = declared.Inputs.ToArray();
            var roots = new List<Sort>(inputSorts);
            if (includeOutput)
                roots.Add(declared.Output);

            var extractor = ComputeCostsFromRootSorts(schema, roots, costs);

            var rows = new List<Value[]>();
            schema.EGraph.ForEachWhile(function, row =>
            {
                if (rows.Count >= count)
                    return false;

                rows.Add(row.Values.ToArray());
                return true;
            });

            var dag = new TermDag();
            var inputs = new List<TermId>(rows.Count);
            var outputs = includeOutput ? new List<TermId>(rows.Count) : null;
            var memo = new Dictionary<ulong, TermId>();
            var children = new TermId[inputSorts.Length];
            using (extractor.Enter())
            {
                foreach (var row in rows)
                {
                    for (var i = 0; i < inputSorts.Length; i++)
                        children[i] = ExtractOrPlaceholder(extractor, inputSorts[i], row[i], dag, memo);

                    inputs.Add(dag.App(declared.Name, children));
                    outputs?.Add(ExtractOrPlaceholder(extractor, declared.Output, row[^1], dag, memo));
                }

                extractor.Exit();
            }

            return new FunctionDag(dag, inputs, outputs);
        }

        /// <summary>
        /// Extracts a value's cheapest term into <paramref name="dag"/>, or the variable <c>Unextractable</c> if it has
        /// none (src/extract.rs:892-904).
        /// </summary>
        static TermId ExtractOrPlaceholder(Extractor extractor, Sort sort, Value value, TermDag dag, Dictionary<ulong, TermId> memo)
        {
            return extractor.TryExtractBestCore(sort, value, dag, memo, out var result) ? result.Term : dag.Var(UnextractableName);
        }

    }

}
