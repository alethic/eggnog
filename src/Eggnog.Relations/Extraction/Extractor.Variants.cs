using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of the class of <paramref name="value"/>: egglog's
        /// <c>extract_variants_with_sort</c> (src/extract.rs:647-733). The class's nodes are ordered by their cost (the
        /// node's head plus its children's cheapest terms) and then by content (rule-layer decision D10), except that the
        /// class's choice comes first, and each of the first <paramref name="count"/> is extracted with its children's
        /// cheapest terms. So the first variant is always the term <c>TryExtractBest</c> gives. The choice is the least of
        /// the cheapest nodes in content order whenever head costs are positive and nothing saturates; with zero or
        /// saturated costs Knuth's algorithm may choose another node of the same cost, because a node is queued only once
        /// its children are final.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a base or container sort there is one term, and the result holds it (or nothing), whatever
        /// <paramref name="count"/> is, as in egglog. The variants share one memo, so their common subterms are built
        /// once.
        /// </para>
        /// <para>
        /// Defect fixed (src/extract.rs:691): egglog unwraps every root node's cost and so panics on a node with a child
        /// that has no finite term. Such a node has no finite term either, so it is skipped. egglog orders equal-cost
        /// variants by function name and then raw row values; here they follow D10's order, the extractor's own.
        /// </para>
        /// </remarks>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <param name="dag">The DAG to extract into.</param>
        /// <returns>The variants, cheapest first.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="ArgumentException">The extractor knows a different sort of the same name.</exception>
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, the cost model
        /// gave a negative cost, or an earlier query failed.</exception>
        public IReadOnlyList<ExtractionResult> ExtractVariants(Sort sort, Value value, int count, TermDag dag)
        {
            ArgumentNullException.ThrowIfNull(sort);
            ArgumentNullException.ThrowIfNull(dag);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            using var scope = Enter(sort);

            var results = new List<ExtractionResult>();
            if (sort.Kind != SortKind.Eq)
            {
                if (TryExtractBestCore(sort, value, dag, null, out var best))
                    results.Add(best);

                Exit();
                return results;
            }

            var index = SortIndexOf(sort);
            var canonical = Canonical(value.Rep);
            var owner = onDemand ? GetOrAddClass(index, canonical) : FindClass(index, canonical);
            if (onDemand)
                Settle();

            if (owner >= 0)
            {
                var candidates = new List<(long Cost, int Node)>();
                for (var node = classFirstNode[owner]; node >= 0; node = nodeNext[node])
                    if (IsCostable(node))
                        candidates.Add((ComputeNodeCost(node), node));

                // The choice costs the least of all, so putting it first keeps the order by cost.
                var chosen = classBest[owner];
                candidates.Sort((left, right) =>
                {
                    if (left.Node == right.Node)
                        return 0;

                    if (left.Node == chosen)
                        return -1;

                    if (right.Node == chosen)
                        return 1;

                    return left.Cost != right.Cost ? left.Cost.CompareTo(right.Cost) : CompareNodes(left.Node, right.Node);
                });

                var memo = new Dictionary<ulong, TermId>();
                var builder = new TermDagBuilder(this, dag);
                var children = new List<TermId>();
                for (var i = 0; i < Math.Min(count, candidates.Count); i++)
                {
                    var (cost, node) = candidates[i];
                    var slot = nodeSlot[node];
                    var columnSorts = slotColumnSorts[slot];
                    var row = nodeRow[node];
                    children.Clear();
                    for (var column = 0; column < slots[slot].ChildCount; column++)
                        children.Add(Build<TermDagBuilder, TermId>(columnSorts[column], rowCells[row + column], ref builder, memo));

                    results.Add(new ExtractionResult(cost, dag.App(slots[slot].TermName, CollectionsMarshal.AsSpan(children))));
                }
            }

            Exit();
            return results;
        }

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of <paramref name="value"/>, a value of the extractor's only
        /// root sort. egglog's <c>extract_variants</c> (src/extract.rs:735-756).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <param name="dag">The DAG to extract into.</param>
        /// <returns>The variants, cheapest first.</returns>
        /// <exception cref="InvalidOperationException">The extractor does not have exactly one root sort, the e-graph
        /// changed since it was created, the cost model gave a negative cost, or an earlier query failed.</exception>
        public IReadOnlyList<ExtractionResult> ExtractVariants(Value value, int count, TermDag dag)
        {
            return ExtractVariants(SingleRootSort(), value, count, dag);
        }

    }

}
