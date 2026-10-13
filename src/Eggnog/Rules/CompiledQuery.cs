using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A compiled query: its program, whose head is one collector of the declared variables, its one engine variant
    /// (naive, lowered once: the engine runs its class constants in their canonical forms in the running context), the
    /// column types of its matches, and the count of calls using it, which defers the release of a disposed query.
    /// </summary>
    /// <remarks>
    /// Thread-safe: <see cref="Acquire"/>, <see cref="Release"/> and <see cref="Dispose"/> take a private lock, and the
    /// variants are freed outside it, once, by whichever call leaves the query disposed and unused. Freeing never waits:
    /// while an engine call that may run user delegates is in progress on any thread (<see cref="EGraph.ActiveEngineCalls"/>),
    /// or when the call comes from a user delegate the engine invoked (<see cref="EngineCallMarker"/>, whose thread may be
    /// inside an engine call), the variants are handed to the graph, which frees them before its next iteration or
    /// query.
    /// </remarks>
    internal sealed class CompiledQuery
    {

        readonly object sync = new();
        readonly EGraph graph;
        readonly Dictionary<Term, int> columns;
        int users;
        bool disposed;
        bool freed;

        /// <summary>
        /// Initializes a new instance; nothing is lowered yet.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="query">The public query.</param>
        /// <param name="program">The program.</param>
        /// <param name="variants">The engine variants: the naive one only.</param>
        /// <param name="serial">The graph-unique serial number of the query's description.</param>
        /// <param name="types">The column types of a match: the declared variables' types, in declaration order.</param>
        public CompiledQuery(EGraph graph, Query query, RuleProgram program, RuleVariants variants, int serial, ColumnType[] types)
        {
            this.graph = graph;
            Query = query;
            Program = program;
            Variants = variants;
            Serial = serial;
            Columns = types;
            columns = new Dictionary<Term, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < query.Variables.Count; i++)
                columns[query.Variables[i]] = i;

            var sorts = new List<int>();
            for (var i = 0; i < types.Length; i++)
                if (types[i].Kind == ColumnKind.Sort)
                    sorts.Add(i);

            SortColumns = sorts.ToArray();
        }

        /// <summary>
        /// Gets the public query.
        /// </summary>
        public Query Query { get; }

        /// <summary>
        /// Gets the program.
        /// </summary>
        public RuleProgram Program { get; }

        /// <summary>
        /// Gets the engine variants: the naive one only.
        /// </summary>
        public RuleVariants Variants { get; }

        /// <summary>
        /// Gets the graph-unique serial number of the query's description.
        /// </summary>
        public int Serial { get; }

        /// <summary>
        /// Gets the column types of a match: the declared variables' types, in declaration order.
        /// </summary>
        public ColumnType[] Columns { get; }

        /// <summary>
        /// Gets the columns of a match whose cells are classes, ascending: the ones a query canonicalizes.
        /// </summary>
        public int[] SortColumns { get; }

        /// <summary>
        /// Gets the number of columns of a match.
        /// </summary>
        public int Width => Columns.Length;

        /// <summary>
        /// Gets the number of calls in progress.
        /// </summary>
        public int Users
        {
            get
            {
                lock (sync)
                    return users;
            }
        }

        /// <summary>
        /// Gets whether the query is disposed.
        /// </summary>
        public bool IsDisposed
        {
            get
            {
                lock (sync)
                    return disposed;
            }
        }

        /// <summary>
        /// Gets the column of a declared variable.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>Its column in a match.</returns>
        /// <exception cref="ArgumentException">The variable is not one of the query's.</exception>
        public int ColumnOf(Term variable)
        {
            if (columns.TryGetValue(variable, out var column))
                return column;

            throw new ArgumentException($"Variable '{variable}' is not a variable of query '{Query.Name}'.", nameof(variable));
        }

        /// <summary>
        /// Starts a call that uses the query.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The query is disposed.</exception>
        public void Acquire()
        {
            lock (sync)
            {
                if (disposed)
                    throw new ObjectDisposedException(Query.Name, $"Query '{Query.Name}' is disposed.");

                users++;
            }
        }

        /// <summary>
        /// Ends a call that used the query; frees the variants when the query is disposed and this was the last call.
        /// </summary>
        public void Release()
        {
            lock (sync)
            {
                users--;
                if (disposed == false || users > 0 || freed)
                    return;

                freed = true;
            }

            Free();
        }

        /// <summary>
        /// Marks the query disposed and frees its variants once no call uses it. Never throws.
        /// </summary>
        /// <returns><c>true</c> the first time; <c>false</c> when the query was disposed already.</returns>
        public bool Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return false;

                disposed = true;
                if (users > 0)
                    return true;

                freed = true;
            }

            Free();
            return true;
        }

        /// <summary>
        /// Frees the variants now, or hands them to the graph when the call comes from a user delegate the engine invoked,
        /// or while an engine call that may run user delegates is in progress on any thread: the graph frees them before it
        /// next prepares an iteration or a query. Never waits.
        /// </summary>
        void Free()
        {
            if (EngineCallMarker.IsActive || graph.ActiveEngineCalls > 0)
            {
                graph.DeferFree(Variants);
                return;
            }

            Variants.Free(graph);
        }

        /// <inheritdoc />
        public override string ToString() => Query.Name + "#" + Serial;

    }

}
