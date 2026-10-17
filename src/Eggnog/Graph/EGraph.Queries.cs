using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        readonly ConcurrentDictionary<string, Query> queriesByName = new(StringComparer.Ordinal);
        readonly ConcurrentQueue<RuleVariants> deferredFrees = new();

        /// <summary>
        /// Defines a compiled conjunctive query for host reads (<see cref="Scope.Any(Query)"/>,
        /// <see cref="Scope.Count(Query)"/>, <see cref="Scope.ForEachMatch(Query, QueryCallback)"/>), compiled
        /// immediately like a rule body. Any thread, any time. Define a reusable query once; dispose one made for a
        /// single request.
        /// </summary>
        /// <remarks>
        /// Its value constants are interned when it is defined, into the graph's pools, and stay interned after it is
        /// disposed (<see cref="ValueStore.InternedCount"/>). Matching never interns.
        /// </remarks>
        /// <param name="name">The query's name, unique among the graph's live queries.</param>
        /// <param name="define">Declares the variables (the columns of a match) and the body.</param>
        /// <returns>The query.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or a live query has it.</exception>
        /// <exception cref="RuleDefinitionException">The query is not valid (the checks of a rule body); nothing is
        /// registered.</exception>
        /// <exception cref="InvalidOperationException">Called from a primitive, lattice, filter or cost function; or the
        /// query's first scoped read of a table creates its mirror, which is a table definition, while another
        /// thread is using the writable root (nothing is registered; once the root is frozen any thread may).</exception>
        public Query DefineQuery(string name, Action<QueryBuilder> define)
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(define);
            var builder = new QueryBuilder(this, name);
            try
            {
                define(builder);
            }
            finally
            {
                builder.Close();
            }

            lock (DefinitionLock)
            {
                if (queriesByName.ContainsKey(name))
                    throw new ArgumentException($"A query named '{name}' is already defined.", nameof(name));

                var compiled = RuleCompiler.CompileQuery(this, builder.Draft);
                queriesByName[name] = compiled.Query;
                return compiled.Query;
            }
        }

        /// <summary>
        /// Gets the live queries (a snapshot), for the plans of a freeze and for tests.
        /// </summary>
        internal IReadOnlyCollection<Query> Queries => new List<Query>(queriesByName.Values);

        /// <summary>
        /// Forgets a disposed query's name, so that it may be defined again. Lock-free, so a disposal never waits.
        /// </summary>
        /// <param name="query">The disposed query.</param>
        internal void ForgetQuery(Query query) => queriesByName.TryRemove(new KeyValuePair<string, Query>(query.Name, query));

        /// <summary>
        /// Hands variants to free to the graph, from a user delegate the engine invoked, whose thread may be inside an
        /// engine call: they are freed before the next engine call (<see cref="FreeDeferred"/>).
        /// </summary>
        /// <param name="variants">The variants.</param>
        internal void DeferFree(RuleVariants variants) => deferredFrees.Enqueue(variants);

        /// <summary>
        /// Frees the variants handed over by <see cref="DeferFree"/>: before every iteration and every query
        /// (<see cref="ContextEngineScope.PrepareRules"/>, <see cref="ContextEngineScope.PrepareVariant"/>), from a host
        /// call. No call uses a disposed query's variants, so freeing them while other threads run engine calls is safe.
        /// </summary>
        internal void FreeDeferred()
        {
            while (deferredFrees.TryDequeue(out var variants))
                variants.Free(this);
        }

    }

}
