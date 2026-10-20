using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// Finds the cheapest terms of classes, or several variants of a class, in one scope as it is now; reusable for many
    /// roots. Reads, queries and
    /// writes that change nothing never invalidate it; a write that changes the scope, or a run in it or in a writable
    /// ancestor, may, and it then throws <see cref="InvalidOperationException"/>; staleness ignores the clock. Ties are
    /// broken by content, so a serial graph extracts deterministically.
    /// </summary>
    /// <remarks>
    /// On a writable scope, a call takes the scope's thread domain: from another thread while the scope is in use it
    /// throws <see cref="InvalidOperationException"/>. On a frozen scope any number of extractors run on any threads at
    /// once, and one extractor is thread-safe too: its calls are serialized on a private lock, because its caches are
    /// shared. Use one extractor per thread for parallel extraction. A cost function must not wait for another thread's
    /// call of the same extractor.
    /// </remarks>
    public sealed class Extractor
    {

        readonly Relations.Extractor engine;
        readonly object sync = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The scope extracted from.</param>
        /// <param name="costs">The cost model.</param>
        /// <param name="engine">The engine's on-demand extractor over the scope.</param>
        internal Extractor(Scope scope, CostModel costs, Relations.Extractor engine)
        {
            Scope = scope;
            Costs = costs;
            this.engine = engine;
        }

        /// <summary>
        /// Gets the scope extracted from.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Gets the cost model.
        /// </summary>
        public CostModel Costs { get; }

        /// <summary>
        /// Extracts the cheapest term of a class.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <returns>The term.</returns>
        /// <exception cref="InvalidOperationException">No finite term exists, the scope changed since the extractor was
        /// created, or a cost function returned a negative cost.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it since the
        /// extractor was made, or lies under one, the contradiction of the case's parent included.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up a query of a writable scope makes
        /// first, in the scope or a writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public ExtractedTerm Extract<TSort>(TSort root) where TSort : struct, ISort<TSort>
        {
            if (TryExtract(root, out var term))
                return term;

            throw new InvalidOperationException($"Class {root.Id} has no finite term in scope '{Scope.Path}' that extraction may use.");
        }

        /// <summary>
        /// Extracts the cheapest term of a class, if a finite one exists.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="term">The term, when the result is <c>true</c>.</param>
        /// <returns>Whether a finite term exists.</returns>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created, or a cost
        /// function returned a negative cost.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it since the
        /// extractor was made, or lies under one, the contradiction of the case's parent included.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up a query of a writable scope makes
        /// first, in the scope or a writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public bool TryExtract<TSort>(TSort root, [NotNullWhen(true)] out ExtractedTerm? term) where TSort : struct, ISort<TSort>
        {
            var held = Scope.EnterExtraction();
            var previous = ExtractionCatalog.CurrentScope;
            ExtractionCatalog.CurrentScope = Scope;
            try
            {
                ThrowIfStopped(held);
                var cell = ClassChecks.Require(Scope, root.Id, nameof(root));
                lock (sync)
                {
                    if (Scope.Engine.TryExtract(engine, SortOf<TSort>(), cell, out var raw) == false || raw is null)
                    {
                        term = null;
                        return false;
                    }

                    term = Wrap(raw, new Dictionary<Relations.ExtractedTerm, ExtractedTerm>(ReferenceEqualityComparer.Instance));
                    return true;
                }
            }
            finally
            {
                ExtractionCatalog.CurrentScope = previous;
                Scope.Exit(held);
            }
        }

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of a class, cheapest first: egglog's <c>(extract x n)</c>.
        /// A variant is one node of the class with the cheapest terms of its arguments. Only the nodes extraction may
        /// use are variants: not subsumed, with an extractable head and a finite term for each argument. They are
        /// ordered by cost and then by content, except that the node <see cref="Extract{TSort}"/> chooses comes first,
        /// so the first variant is the term it gives.
        /// </summary>
        /// <remarks>
        /// Each variant's <see cref="ExtractedTerm.Cost"/> is its own, and every variant's
        /// <see cref="ExtractedTerm.Class"/> is the class. Below the variants, shared subterms are one instance across
        /// them all. Variants of equal cost follow content order (constructor declaration order, then arguments), where
        /// egglog orders them by head name and then row values; the node <see cref="Extract{TSort}"/> chooses leads
        /// even where zero costs let it choose a node later in that order.
        /// </remarks>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <returns>The variants, cheapest first: <paramref name="count"/> of them, or all the class has if fewer, and
        /// none when the class has no finite term or <paramref name="count"/> is 0.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created, a cost function
        /// returned a negative cost, or an earlier call of this extractor failed midway; or the scope is faulted; or it
        /// is writable and in use on another thread; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it since the
        /// extractor was made, or lies under one, the contradiction of the case's parent included.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up a query of a writable scope makes
        /// first, in the scope or a writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public IReadOnlyList<ExtractedTerm> ExtractVariants<TSort>(TSort root, int count) where TSort : struct, ISort<TSort>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var held = Scope.EnterExtraction();
            var previous = ExtractionCatalog.CurrentScope;
            ExtractionCatalog.CurrentScope = Scope;
            try
            {
                ThrowIfStopped(held);
                var cell = ClassChecks.Require(Scope, root.Id, nameof(root));
                lock (sync)
                {
                    var raw = Scope.Engine.ExtractVariants(engine, SortOf<TSort>(), cell, count);
                    var wrapped = new Dictionary<Relations.ExtractedTerm, ExtractedTerm>(ReferenceEqualityComparer.Instance);
                    var variants = new ExtractedTerm[raw.Count];
                    for (var i = 0; i < variants.Length; i++)
                        variants[i] = Wrap(raw[i], wrapped);

                    return variants;
                }
            }
            finally
            {
                ExtractionCatalog.CurrentScope = previous;
                Scope.Exit(held);
            }
        }

        /// <summary>
        /// Gets the cost of the cheapest term of a class, if a finite one exists.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="cost">The cost, when the result is <c>true</c>.</param>
        /// <returns>Whether a finite term exists.</returns>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created, or a cost
        /// function returned a negative cost.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it since the
        /// extractor was made, or lies under one, the contradiction of the case's parent included.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up a query of a writable scope makes
        /// first, in the scope or a writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public bool TryGetCost<TSort>(TSort root, out long cost) where TSort : struct, ISort<TSort>
        {
            var held = Scope.EnterExtraction();
            var previous = ExtractionCatalog.CurrentScope;
            ExtractionCatalog.CurrentScope = Scope;
            try
            {
                ThrowIfStopped(held);
                var cell = ClassChecks.Require(Scope, root.Id, nameof(root));
                lock (sync)
                    return Scope.Engine.TryGetCost(engine, SortOf<TSort>(), cell, out cost);
            }
            finally
            {
                ExtractionCatalog.CurrentScope = previous;
                Scope.Exit(held);
            }
        }

        /// <summary>
        /// Refuses a query of a scope with no model: a stopped case, or a scope under one. A writable scope is brought
        /// up to date first, so that a contradiction reaching its case through the case's parent since the case's last
        /// call stops the case here, as on the structural entry points, rather than being missed on a stale stop flag;
        /// the catch-up is left out of <see cref="Scope.Version"/>. A frozen scope is never under a case and is only
        /// checked. Called inside <see cref="Scope.EnterExtraction"/>, with the guard held when <paramref name="held"/>
        /// is true. Cost: the entry catch-up of a writable call, then one volatile read per writable scope up to the
        /// first frozen one.
        /// </summary>
        /// <param name="held">Whether the extraction entry took the scope's guard (the scope is writable).</param>
        /// <exception cref="ScopeContradictedException">The scope is a stopped case, or lies under one.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up a query of a writable scope makes
        /// first, in the scope or a writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        void ThrowIfStopped(bool held)
        {
            if (held)
                Scope.CatchUpAndThrowIfStopped();
            else
                Scope.ThrowIfStopped();
        }

        /// <summary>
        /// Gets the extraction sort of a sort of the scope's graph.
        /// </summary>
        Relations.Sort SortOf<TSort>() where TSort : struct, ISort<TSort> => Scope.Graph.Values.TypeOf<TSort>().ExtractionSort;

        /// <summary>
        /// Wraps an engine term, sharing the wrappers of shared subterms.
        /// </summary>
        ExtractedTerm Wrap(Relations.ExtractedTerm raw, Dictionary<Relations.ExtractedTerm, ExtractedTerm> wrapped)
        {
            if (wrapped.TryGetValue(raw, out var existing))
                return existing;

            var head = Scope.Graph.Extraction.HeadOf(raw.Function);
            var children = raw.Children;
            var arguments = new object[children.Length];
            var cells = new uint[children.Length];
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                cells[i] = child.Value.Rep;
                arguments[i] = child.Kind == Relations.ExtractedTermKind.Node
                    ? Wrap(child, wrapped)
                    : head.KeyArray[i].DecodeObject(child.Value.Rep, Scope);
            }

            var term = new ExtractedTerm(head, new ClassId(raw.Value.Rep, Scope.Origin), raw.Cost, arguments, cells, Scope, raw);
            wrapped.Add(raw, term);
            return term;
        }

    }

}
