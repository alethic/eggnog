using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of the class of <paramref name="value"/>: egglog's
        /// <c>extract_variants_with_sort</c> (src/extract.rs:651-733). The class's nodes are ordered as egglog sorts
        /// them, by their cost (the node's head plus its children's cheapest terms), then by function name, then by
        /// their rows' values (<see cref="CompareVariants"/>), and each of the first <paramref name="count"/> is
        /// extracted with its children's cheapest terms. So the first variant costs what <c>TryExtractBest</c>'s term
        /// costs, but may be another node of the class when costs tie.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a base or container sort there is one term, and the result holds it (or nothing), whatever
        /// <paramref name="count"/> is, as in egglog. The variants share one memo, so their common subterms are built
        /// once.
        /// </para>
        /// <para>
        /// egglog unwraps every root node's cost (src/extract.rs:691) and so panics on a node with a child that has no
        /// finite term; here such a node, which has no finite term either, is skipped.
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

            var builder = new TermDagBuilder(this, dag);
            var variants = ExtractVariantsCore<TermDagBuilder, TermId>(sort, value, count, ref builder);
            var results = new List<ExtractionResult>(variants.Count);
            foreach (var (cost, term) in variants)
                results.Add(new ExtractionResult(cost, term));

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

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of the class of <paramref name="value"/> as trees of
        /// <see cref="ExtractedTerm"/>s, which carry each subterm's sort, value and cost: the variants
        /// <see cref="ExtractVariants(Sort, Value, int, TermDag)"/> gives, in the same order. A variant's root carries the
        /// class's canonical id and the variant's own cost; below the roots, subterms for the same value are one instance
        /// across all the variants.
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <returns>The variants, cheapest first.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="ArgumentException">The extractor knows a different sort of the same name.</exception>
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, the cost model
        /// gave a negative cost, or an earlier query failed.</exception>
        public IReadOnlyList<ExtractedTerm> ExtractVariantTerms(Sort sort, Value value, int count)
        {
            ArgumentNullException.ThrowIfNull(sort);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            using var scope = Enter(sort);

            var builder = new ExtractedTermBuilder(this);
            var variants = ExtractVariantsCore<ExtractedTermBuilder, ExtractedTerm>(sort, value, count, ref builder);
            var results = new ExtractedTerm[variants.Count];
            for (var i = 0; i < results.Length; i++)
                results[i] = variants[i].Term;

            Exit();
            return results;
        }

        /// <summary>
        /// Extracts the variants of a value with <paramref name="builder"/>, inside a query. A variant's root is built
        /// with the variant's own cost and kept out of the memo, which holds the terms of classes, so a variant is a
        /// separate instance even when it is its class's term; its children are the cheapest terms of their values, built
        /// through one memo shared by all the variants, in the order of the value's sort.
        /// </summary>
        /// <typeparam name="TBuilder">The builder; a struct, so the walk is specialized for it.</typeparam>
        /// <typeparam name="TResult">What a built term is.</typeparam>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <param name="builder">Builds each term.</param>
        /// <returns>Each variant's cost and term, cheapest first.</returns>
        List<(long Cost, TResult Term)> ExtractVariantsCore<TBuilder, TResult>(Sort sort, Value value, int count, ref TBuilder builder)
            where TBuilder : struct, IExtractionBuilder<TResult>
        {
            var results = new List<(long Cost, TResult Term)>();
            var memo = new Dictionary<ulong, TResult>();
            if (sort.Kind != SortKind.Eq)
            {
                if (TryResolve(sort, value, out var resolved, out var at, out var best))
                {
                    var single = OrderFor(resolved);
                    EnsureChoices(single, resolved, at);
                    results.Add((best, Build<TBuilder, TResult>(single, resolved, at, ref builder, memo)));
                }

                return results;
            }

            var index = SortIndexOf(sort);
            var canonical = Canonical(value.Rep);
            var owner = onDemand ? GetOrAddClass(index, canonical) : FindClass(index, canonical);
            if (onDemand)
                Settle();

            if (owner < 0)
                return results;

            var candidates = new List<(long Cost, int Node)>();
            for (var node = classFirstNode[owner]; node >= 0; node = nodeNext[node])
                if (IsCostable(node))
                    candidates.Add((ComputeNodeCost(node), node));

            candidates.Sort(CompareVariants);
            var order = OrderFor(index);
            var children = new List<TResult>();
            for (var i = 0; i < Math.Min(count, candidates.Count); i++)
            {
                var (cost, node) = candidates[i];
                var slot = nodeSlot[node];
                var columnSorts = slotColumnSorts[slot];
                var row = nodeRow[node];
                children.Clear();
                for (var column = 0; column < slots[slot].ChildCount; column++)
                {
                    EnsureChoices(order, columnSorts[column], rowCells[row + column]);
                    children.Add(Build<TBuilder, TResult>(order, columnSorts[column], rowCells[row + column], ref builder, memo));
                }

                results.Add((cost, builder.Node(index, canonical, node, cost, CollectionsMarshal.AsSpan(children))));
            }

            return results;
        }

        /// <summary>
        /// Orders two variants as egglog sorts its <c>(cost, function name, row values)</c> tuples
        /// (src/extract.rs:702): by cost, then by the name of the node's function, by code point as Rust orders strings,
        /// then by the row's values, keys then output, as raw values. Total on distinct nodes: two rows of one function
        /// differ in a key.
        /// </summary>
        /// <returns>The sign of the comparison.</returns>
        int CompareVariants((long Cost, int Node) left, (long Cost, int Node) right)
        {
            if (left.Cost != right.Cost)
                return left.Cost.CompareTo(right.Cost);

            var leftSlot = nodeSlot[left.Node];
            var rightSlot = nodeSlot[right.Node];
            if (leftSlot != rightSlot)
            {
                var names = CodePointComparer.Instance.Compare(slots[leftSlot].Name, slots[rightSlot].Name);
                if (names != 0)
                    return names;
            }

            var leftCells = rowCells.AsSpan(nodeRow[left.Node], slotColumnSorts[leftSlot].Length);
            var rightCells = rowCells.AsSpan(nodeRow[right.Node], slotColumnSorts[rightSlot].Length);
            return leftCells.SequenceCompareTo(rightCells);
        }

    }

}
