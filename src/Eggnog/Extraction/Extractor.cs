using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// Finds the cheapest terms of classes, or several variants of a class, in one scope as it is now; reusable for many
    /// roots. Reads, queries and
    /// writes that change nothing never invalidate it; a write that changes the scope, or a run in it or in a writable
    /// ancestor, may, and it then throws <see cref="InvalidOperationException"/>; so does a constructor defined after the
    /// extractor once it has rows that extraction may use. Staleness ignores the clock. Ties are broken as egglog's
    /// extractor breaks them, by the order in which its passes meet the rows, so a serial graph extracts
    /// deterministically. Terms of any depth extract: nothing recurses once per level.
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
        readonly int constructorsAtCreation;

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
            constructorsAtCreation = scope.Graph.Extraction.ConstructorCount;
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
                    ThrowIfConstructorAdded();
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
        /// ordered as egglog orders them: by cost, then by the head's name, then by the row's cells. So the first
        /// variant costs what <see cref="Extract{TSort}"/>'s term costs, but where costs tie it may be another node.
        /// </summary>
        /// <remarks>
        /// Each variant's <see cref="ExtractedTerm.Cost"/> is its own, and every variant's
        /// <see cref="ExtractedTerm.Class"/> is the class. Below the variants, shared subterms are one instance across
        /// them all.
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
                    ThrowIfConstructorAdded();
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
                    ThrowIfConstructorAdded();
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
        /// Throws when a constructor defined after the extractor has rows in the scope's view that extraction may use:
        /// the engine extractor reads the constructors declared when it was made, so it would miss those nodes and answer
        /// a dearer term, or none. Defining a constructor alone leaves the extractor usable; its first row makes it stale,
        /// as any other change of what it reads does. Called under the scope's guard. O(constructors defined since).
        /// </summary>
        /// <exception cref="InvalidOperationException">Such a constructor has rows.</exception>
        void ThrowIfConstructorAdded()
        {
            foreach (var constructor in Scope.Graph.Extraction.ConstructorsSince(constructorsAtCreation))
                if (constructor.Extractable && Costs.IsUnextractable(constructor) == false && Scope.Engine.TableSize(constructor.Function) > 0)
                    throw new InvalidOperationException(
                        $"Constructor '{constructor.Name}' was defined after the extractor was created, and has rows in scope '{Scope.Path}'; create a new one.");
        }

        /// <summary>
        /// Wraps an engine term, sharing the wrappers of shared subterms. A node is wrapped once its node children are, in
        /// a post-order walk on a stack of its own, so a term of any depth needs no deeper call stack, as the engine's
        /// <c>Build</c> and <see cref="Relations.ExtractedTerm"/>'s <c>AddTo</c> need none.
        /// </summary>
        ExtractedTerm Wrap(Relations.ExtractedTerm raw, Dictionary<Relations.ExtractedTerm, ExtractedTerm> wrapped)
        {
            if (wrapped.TryGetValue(raw, out var existing))
                return existing;

            // Each entry is a node and the index of its next child to look at; a node is wrapped when none is left.
            var stack = new Stack<(Relations.ExtractedTerm Node, int Next)>();
            stack.Push((raw, 0));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var children = node.Children;
                while (next < children.Length && (children[next].Kind != Relations.ExtractedTermKind.Node || wrapped.ContainsKey(children[next])))
                    next++;

                if (next < children.Length)
                {
                    stack.Push((node, next + 1));
                    stack.Push((children[next], 0));
                    continue;
                }

                if (wrapped.ContainsKey(node))
                    continue;

                var head = Scope.Graph.Extraction.HeadOf(node.Function);
                var arguments = new object[children.Length];
                var cells = new uint[children.Length];
                for (var i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    cells[i] = child.Value.Rep;
                    arguments[i] = child.Kind == Relations.ExtractedTermKind.Node
                        ? wrapped[child]
                        : head.KeyArray[i].DecodeObject(child.Value.Rep, Scope);
                }

                wrapped.Add(node, new ExtractedTerm(head, new ClassId(node.Value.Rep, Scope.Origin), node.Cost, arguments, cells, Scope, node));
            }

            return wrapped[raw];
        }

    }

}
