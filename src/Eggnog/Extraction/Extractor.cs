using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// Finds the cheapest terms of classes in one scope as it is now; reusable for many roots (§6.10). Reads, queries and
    /// writes that change nothing never invalidate it; a write that changes the scope, or a run in it, may, and it then
    /// throws <see cref="InvalidOperationException"/>. Ties are broken by content (D10), so a serial graph extracts
    /// deterministically. Not thread-safe on a writable scope.
    /// </summary>
    public sealed class Extractor
    {

        readonly Relations.Extractor engine;

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
        /// <exception cref="InvalidOperationException">No finite term exists, or the scope changed since the extractor was
        /// created.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope (D39).</exception>
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
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope (D39).</exception>
        public bool TryExtract<TSort>(TSort root, [NotNullWhen(true)] out ExtractedTerm? term) where TSort : struct, ISort<TSort>
        {
            EngineCallMarker.ThrowIfActive();
            Scope.Guard.Enter(Scope);
            try
            {
                Scope.ThrowIfNotUsable();
                var cell = ClassChecks.Require(Scope, root.Id, nameof(root));
                if (Scope.Engine.TryExtract(engine, SortOf<TSort>(), cell, out var raw) == false || raw is null)
                {
                    term = null;
                    return false;
                }

                term = Wrap(raw, new Dictionary<Relations.ExtractedTerm, ExtractedTerm>(ReferenceEqualityComparer.Instance));
                return true;
            }
            finally
            {
                Scope.Guard.Exit();
            }
        }

        /// <summary>
        /// Gets the cost of the cheapest term of a class, if a finite one exists.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="cost">The cost, when the result is <c>true</c>.</param>
        /// <returns>Whether a finite term exists.</returns>
        /// <exception cref="InvalidOperationException">The scope changed since the extractor was created.</exception>
        /// <exception cref="ArgumentException">The class value is not usable in the scope (D39).</exception>
        public bool TryGetCost<TSort>(TSort root, out long cost) where TSort : struct, ISort<TSort>
        {
            EngineCallMarker.ThrowIfActive();
            Scope.Guard.Enter(Scope);
            try
            {
                Scope.ThrowIfNotUsable();
                var cell = ClassChecks.Require(Scope, root.Id, nameof(root));
                return Scope.Engine.TryGetCost(engine, SortOf<TSort>(), cell, out cost);
            }
            finally
            {
                Scope.Guard.Exit();
            }
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
