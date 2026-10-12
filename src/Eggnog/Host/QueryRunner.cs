using System;

namespace Eggnog
{

    /// <summary>
    /// Runs a query in a scope (§6.6.4, D34, D49): checks its class constants against the scope, runs its naive engine
    /// rule (R1: without a merge and without a clock tick, §6.6.5), and returns the distinct full bindings, canonical, and
    /// in content order when asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query writes nothing, so a failure in it throws <see cref="RuleEvaluationException"/> without faulting the scope.
    /// From R2 a frozen scope's queries run in a transient child; R1 runs every query at the root.
    /// </para>
    /// <para>
    /// Allocation: the matches go into one pooled <see cref="TupleBuffer"/>, canonicalized in place and sorted through a
    /// pooled order, so a call allocates nothing per match beyond what the engine run allocates (conventions). Only
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
        /// <param name="sort">Whether to order the matches by content (<c>ForEachMatch</c>).</param>
        /// <returns>The distinct full bindings: one tuple per match, a cell per declared variable, sort cells canonical in
        /// the scope, in content order when <paramref name="sort"/>. The caller disposes it, which returns its arrays to
        /// the pool.</returns>
        /// <exception cref="ObjectDisposedException">The query or the scope is disposed.</exception>
        /// <exception cref="ArgumentException">A class constant of the query cannot be used in the scope (D39).</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted or in use on another thread.</exception>
        /// <exception cref="RuleEvaluationException">A primitive or filter of the query failed; the scope is not
        /// faulted.</exception>
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

                scope.Guard.Enter(scope);
                try
                {
                    scope.ThrowIfNotUsable();
                    var engine = scope.Engine;
                    engine.EnsureClean();
                    var matches = Collect(scope, query, stopAtFirst);
                    try
                    {
                        // Canonical in the scope, then deduplicated: at a clean scope the collected cells are canonical
                        // already, so this changes nothing there.
                        if (engine.CanonicalizeColumns(matches.Cells, matches.Width, query.SortColumns))
                            matches.Deduplicate();

                        if (sort)
                            matches.SortByContent(query.Columns);

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
                    scope.Guard.Exit();
                }
            }
            finally
            {
                query.Release();
            }
        }

        /// <summary>
        /// Runs the query's engine rule with the scope's runtime collecting into a fresh buffer.
        /// </summary>
        static TupleBuffer Collect(Scope scope, CompiledQuery query, bool stopAtFirst)
        {
            var runtime = scope.Runtime;
            var matches = new TupleBuffer(query.Width);
            runtime.TakeFailure();
            runtime.QueryMatches = matches;
            runtime.StopAtFirstQueryMatch = stopAtFirst;
            try
            {
                var variant = scope.Engine.PrepareVariant(query.Variants, query.Program.WrittenIdConstants, false);
                scope.Engine.RunQuery(variant);
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
