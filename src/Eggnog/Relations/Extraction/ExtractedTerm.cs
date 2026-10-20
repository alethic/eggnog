using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One node of an extracted term, as a tree of objects: an e-class's chosen node (a function applied to its
    /// children's terms), a base value, or a container applied to its elements' terms. Each subterm carries its sort, its
    /// value (the canonical e-class id, the base value or the container id) and its cost. Subterms for the same value are
    /// the same instance, so the tree is a DAG of shared objects; the root of a variant, one node of its class with its
    /// own cost, is the exception. <see cref="Extractor.TryExtractTerm"/> and <see cref="Extractor.ExtractVariantTerms"/>
    /// build it; the rule layer's <c>ExtractedTerm</c> is a typed view of it.
    /// </summary>
    /// <remarks>
    /// Immutable and thread-safe.
    /// </remarks>
    internal sealed class ExtractedTerm
    {

        readonly EGraph egraph;
        readonly ExtractedTerm[] children;
        readonly FunctionId function;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="egraph">The e-graph the term was extracted from, which can print its values.</param>
        /// <param name="kind">What the term is.</param>
        /// <param name="sort">The term's sort.</param>
        /// <param name="value">The term's value.</param>
        /// <param name="function">The function of a node; ignored otherwise.</param>
        /// <param name="name">The head of a node, the text of a base value, the sort name of a container.</param>
        /// <param name="cost">The term's cost.</param>
        /// <param name="children">The children; owned by the new instance.</param>
        internal ExtractedTerm(
            EGraph egraph,
            ExtractedTermKind kind,
            Sort sort,
            Value value,
            FunctionId function,
            string name,
            long cost,
            ExtractedTerm[] children)
        {
            this.egraph = egraph;
            this.children = children;
            this.function = function;
            Kind = kind;
            Sort = sort;
            Value = value;
            Name = name;
            Cost = cost;
        }

        /// <summary>
        /// Gets what the term is.
        /// </summary>
        public ExtractedTermKind Kind { get; }

        /// <summary>
        /// Gets the term's sort.
        /// </summary>
        public Sort Sort { get; }

        /// <summary>
        /// Gets the term's value: the canonical e-class id of a node, the base value, or the container id.
        /// </summary>
        public Value Value { get; }

        /// <summary>
        /// Gets the term's cost under the extractor's <see cref="CostModel"/>.
        /// </summary>
        public long Cost { get; }

        /// <summary>
        /// Gets the head of a node (its function's name, or the term constructor's for a view), the literal text of a
        /// base value, or the sort name of a container.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the function whose row a node is.
        /// </summary>
        /// <exception cref="InvalidOperationException">The term is not a node.</exception>
        public FunctionId Function
        {
            get => Kind == ExtractedTermKind.Node ? function : throw new InvalidOperationException("Only a node has a function.");
        }

        /// <summary>
        /// Gets the number of children: a node's child columns or a container's elements.
        /// </summary>
        public int Arity => children.Length;

        /// <summary>
        /// Gets the children, in column or element order.
        /// </summary>
        public ReadOnlySpan<ExtractedTerm> Children => children;

        /// <summary>
        /// Gets the child at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The child's position.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a child position.</exception>
        public ExtractedTerm Child(int index)
        {
            if ((uint)index >= (uint)children.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"The term has {children.Length} children.");

            return children[index];
        }

        /// <summary>
        /// Gets whether a row of <paramref name="function"/> occurs anywhere in the term. O(distinct subterms).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Whether it occurs.</returns>
        public bool Mentions(FunctionId function)
        {
            var seen = new HashSet<ExtractedTerm>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<ExtractedTerm>();
            stack.Push(this);
            seen.Add(this);
            while (stack.Count > 0)
            {
                var term = stack.Pop();
                if (term.Kind == ExtractedTermKind.Node && term.function == function)
                    return true;

                foreach (var child in term.children)
                    if (seen.Add(child))
                        stack.Push(child);
            }

            return false;
        }

        /// <summary>
        /// Adds the term to <paramref name="dag"/>, as
        /// <see cref="Extractor.TryExtractBest(Sort, Value, TermDag, out ExtractionResult)"/> would have, or for a
        /// variant <see cref="Extractor.ExtractVariants(Sort, Value, int, TermDag)"/>. Iterative and memoized per
        /// subterm.
        /// </summary>
        /// <param name="dag">The DAG.</param>
        /// <returns>The term's id in the DAG.</returns>
        public TermId AddTo(TermDag dag)
        {
            ArgumentNullException.ThrowIfNull(dag);

            var memo = new Dictionary<ExtractedTerm, TermId>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(ExtractedTerm Term, bool Expanded)>();
            var arguments = new List<TermId>();
            stack.Push((this, false));
            while (stack.Count > 0)
            {
                var (term, expanded) = stack.Pop();
                if (memo.ContainsKey(term))
                    continue;

                if (expanded == false)
                {
                    stack.Push((term, true));
                    for (var i = term.children.Length - 1; i >= 0; i--)
                        if (memo.ContainsKey(term.children[i]) == false)
                            stack.Push((term.children[i], false));

                    continue;
                }

                arguments.Clear();
                foreach (var child in term.children)
                    arguments.Add(memo[child]);

                var span = CollectionsMarshal.AsSpan(arguments);
                memo[term] = term.Kind switch
                {
                    ExtractedTermKind.Node => dag.App(term.Name, span),
                    ExtractedTermKind.Base => term.Sort.ReconstructBase(egraph.BaseValues, term.Value, dag),
                    _ => ((ContainerSort)term.Sort).Reconstruct(egraph.ContainerValues, term.Value, dag, span),
                };
            }

            return memo[this];
        }

        /// <summary>
        /// Renders the term as an s-expression, as <see cref="TermDag.ToString(TermId)"/> does.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var dag = new TermDag();
            return dag.ToString(AddTo(dag));
        }

    }

}
