using System;

namespace Eggnog
{

    /// <summary>
    /// Runs a query in a scope: checks its class constants against the scope, brings the mirrors it reads current, runs
    /// its naive engine rule, and returns the distinct full bindings, canonical, and in content order when asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a writable scope the query is an iteration of that scope that writes nothing: the clock advances, but the
    /// scope's version does not move and no extractor goes stale. A query reading a tag whose scope is writable and
    /// changed since its last sync first syncs that scope's mirrors; the mirror tables are internal, so that moves
    /// neither its <see cref="Scope.Version"/> nor any extractor. In a frozen scope it runs in a transient child
    /// created for the call and disposed after it, so a frozen scope stays read-only and can be queried from many
    /// threads; the transient child defers the scope's release like any child. A query writes nothing, so a failure
    /// in it throws <see cref="RuleEvaluationException"/> without faulting the scope.
    /// </para>
    /// <para>
    /// Allocation: the matches go into one pooled <see cref="TupleBuffer"/>, canonicalized in place and sorted through a
    /// pooled order, so a call allocates nothing per match beyond what the engine run allocates. Only
    /// <c>ForEachMatch</c> needs content order; <c>Any</c> and <c>Count</c> skip the sort.
    /// </para>
    /// </remarks>
    internal static class QueryRunner
    {

        /// <summary>
        /// Runs <paramref name="query"/> in <paramref name="scope"/>.
        /// </summary>
        /// <param name="scope">The scope queried.</param>
        /// <param name="query">The query.</param>
        /// <param name="stopAtFirst">Whether the engine stops after the first match (<c>Any</c>).</param>
        /// <param name="sort">Whether the matches are sorted in content order (<c>ForEachMatch</c>).</param>
        /// <returns>The matches, which the caller disposes.</returns>
        /// <exception cref="ObjectDisposedException">The query or the scope is disposed.</exception>
        /// <exception cref="ArgumentException">A class constant of the query cannot be used in the scope.</exception>
        /// <exception cref="InvalidOperationException">A tag the query reads does not resolve from the scope, or the scope
        /// is not usable.</exception>
        /// <exception cref="RuleEvaluationException">A primitive or filter of the query failed.</exception>
        public static TupleBuffer Run(Scope scope, CompiledQuery query, bool stopAtFirst, bool sort)
        {
            query.Acquire();
            try
            {
                var program = query.Program;
                for (var i = 0; i < program.WrittenIdConstants.Length; i++)
                {
                    var constant = new ClassId(program.WrittenIdConstants[i], program.IdConstantOrigins[i]);
                    if (ClassChecks.Check(scope.Origin, constant, null) is { } error)
                        throw new ArgumentException($"Query '{query.Query.Name}': {error}", nameof(query));
                }

                // Every tag resolves before any work: a tag read later must not fail after an earlier one synced.
                foreach (var tag in program.ReadScopes)
                    ScopeResolver.Resolve(scope, tag, $"Query '{query.Query.Name}'");

                var held = scope.EnterRead();
                try
                {
                    if (program.ReadScopes.Length > 0)
                        MirrorSync.Ensure(scope, program.ReadScopes, $"Query '{query.Query.Name}'", null);

                    var matches = scope.IsFrozen ? CollectInTransient(scope, query, stopAtFirst) : Collect(scope.Engine, query, stopAtFirst);
                    try
                    {
                        // Canonical in the scope, then deduplicated: at a clean scope the collected cells are canonical
                        // already, so this changes nothing there.
                        if (scope.Engine.CanonicalizeColumns(matches.Cells, matches.Width, query.SortColumns))
                            matches.Deduplicate();

                        if (sort)
                            matches.SortByContent(query.Columns, scope.ContainerView);

                        return matches;
                    }
                    catch
                    {
                        matches.Dispose();
                        throw;
                    }
                }
                finally
                {
                    scope.Exit(held);
                }
            }
            finally
            {
                query.Release();
            }
        }

        /// <summary>
        /// Runs the query in a transient child of a frozen scope, disposed after the call.
        /// </summary>
        static TupleBuffer CollectInTransient(Scope scope, CompiledQuery query, bool stopAtFirst)
        {
            var transient = scope.CreateTransient();
            try
            {
                return Collect(transient, query, stopAtFirst);
            }
            finally
            {
                scope.ReleaseTransient(transient);
            }
        }

        /// <summary>
        /// Runs the query's naive engine rule in <paramref name="target"/>, its collector filling a new buffer through the
        /// target's runtime.
        /// </summary>
        static TupleBuffer Collect(ContextEngineScope target, CompiledQuery query, bool stopAtFirst)
        {
            var runtime = target.Runtime;
            var matches = new TupleBuffer(query.Width);
            runtime.TakeFailure();
            runtime.QueryMatches = matches;
            runtime.StopAtFirstQueryMatch = stopAtFirst;
            try
            {
                var variant = target.PrepareVariant(query.Variants, query.Program.WrittenIdConstants, false);
                target.RunQuery(variant);
                return matches;
            }
            catch (Relations.PanicException panic)
            {
                matches.Dispose();
                var failure = runtime.TakeFailure();
                throw new RuleEvaluationException(failure?.RuleName, failure?.Bindings, failure?.What ?? panic.PanicMessage,
                    failure?.Exception ?? panic);
            }
            catch
            {
                matches.Dispose();
                throw;
            }
            finally
            {
                runtime.QueryMatches = null;
                runtime.StopAtFirstQueryMatch = false;
            }
        }

    }

}
