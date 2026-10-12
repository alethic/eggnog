using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A compiled conjunctive query for host reads (D34, D49): <see cref="Scope.Any(Query)"/>,
    /// <see cref="Scope.Count(Query)"/> and <see cref="Scope.ForEachMatch(Query, QueryCallback)"/>. Made by
    /// <see cref="EGraph.DefineQuery(string, Action{QueryBuilder})"/>. Define a reusable query once; dispose one made for
    /// a single request, which frees its engine rule and call sites.
    /// </summary>
    /// <remarks>
    /// A query runs as a naive engine rule whose only action collects its matches: it writes nothing, so it moves neither
    /// <see cref="Scope.Version"/> nor any extractor, and a failure in it leaves the scope usable (D49). Its class
    /// constants are checked against the scope at each call (D39). Thread-safe.
    /// </remarks>
    public sealed class Query : IDisposable
    {

        readonly EGraph graph;

        /// <summary>
        /// Initializes a new instance; the compiler sets <see cref="Compiled"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The query's name.</param>
        /// <param name="variables">The declared variables, in declaration order.</param>
        internal Query(EGraph graph, string name, Term[] variables)
        {
            this.graph = graph;
            Name = name;
            Variables = Array.AsReadOnly(variables);
        }

        /// <summary>
        /// Gets the query's name, unique among the graph's live queries.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the declared variables, in declaration order: the columns of a match, which binds every one of them.
        /// </summary>
        public IReadOnlyList<Term> Variables { get; }

        /// <summary>
        /// Gets whether <see cref="Dispose"/> has been called.
        /// </summary>
        public bool IsDisposed => Compiled.IsDisposed;

        /// <summary>
        /// Disposes the query (D49): never throws. Later use throws <see cref="ObjectDisposedException"/>; the query's
        /// engine rule and call sites are freed once no call is using it, and its name may be defined again. A second
        /// call does nothing.
        /// </summary>
        public void Dispose()
        {
            if (Compiled.Dispose())
                graph.ForgetQuery(this);
        }

        /// <summary>
        /// Renders the query as written: <c>(query ((present p) (= d (dom p))) (p d))</c>, its body then its variables.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets the graph the query belongs to.
        /// </summary>
        internal EGraph Graph => graph;

        /// <summary>
        /// Gets the compiled query; set once by the compiler.
        /// </summary>
        internal CompiledQuery Compiled { get; set; } = null!;

        /// <summary>
        /// Gets what the query's builder collected, for rendering.
        /// </summary>
        internal RuleDraft Draft { get; set; } = null!;

    }

}
