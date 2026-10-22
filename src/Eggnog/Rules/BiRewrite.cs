using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>birewrite</c>, desugared as egglog does (<c>src/ast/desugar.rs</c>, <c>desugar_birewrite</c>): two
    /// rewrites, <c>{name}=&gt;</c> from left to right and <c>{name}&lt;=</c> from right to left, under the same
    /// conditions. Serves <see cref="Ruleset.DefineBiRewrite{TSort}"/> and <see cref="Scope.DefineBiRewrite{TSort}"/>.
    /// </summary>
    internal static class BiRewrite
    {

        /// <summary>
        /// Collects the two rules' drafts, calling <paramref name="define"/> once for each.
        /// </summary>
        /// <typeparam name="TSort">The sort of both sides.</typeparam>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The name the rules' names start with.</param>
        /// <param name="define">Declares the variables and conditions and returns the two sides.</param>
        /// <returns>The drafts.</returns>
        public static (RuleDraft Forward, RuleDraft Backward) Draft<TSort>(EGraph graph, string name,
            Func<RuleBuilder, (Term<TSort> Left, Term<TSort> Right)> define)
            where TSort : struct, ISort<TSort> =>
            (One(graph, name + "=>", define, false), One(graph, name + "<=", define, true));

        /// <summary>
        /// Compiles both drafts so that a refused pair registers nothing: the right-to-left rule is checked first, short
        /// of its mirrors, then the left-to-right rule compiles, creating the mirrors of its scoped reads, then the
        /// right-to-left one. Its scoped reads are the conditions', whose mirrors the first rule created: the right side
        /// is the first rule's head, which reads no scoped view. Should the engine reject the second, the first's engine
        /// rules are freed. Called under the graph's definition lock.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="forward">The left-to-right draft.</param>
        /// <param name="backward">The right-to-left draft.</param>
        /// <param name="ruleset">The ruleset.</param>
        /// <param name="owner">The scope of local rules; <c>null</c> for global ones.</param>
        /// <returns>The compiled rules.</returns>
        public static (CompiledRule Forward, CompiledRule Backward) Compile(EGraph graph, RuleDraft forward, RuleDraft backward, Ruleset ruleset,
            Scope? owner)
        {
            RuleCompiler.Check(graph, backward, ruleset, owner);
            var first = RuleCompiler.Compile(graph, forward, ruleset, owner);
            try
            {
                return (first, RuleCompiler.Compile(graph, backward, ruleset, owner));
            }
            catch
            {
                first.Variants.Free(graph);
                throw;
            }
        }

        /// <summary>
        /// Collects one direction's draft. <paramref name="define"/> adds variables and conditions only: egglog's
        /// <c>birewrite</c> has no head but the rewrite.
        /// </summary>
        /// <exception cref="RuleDefinitionException"><paramref name="define"/> added a head item.</exception>
        static RuleDraft One<TSort>(EGraph graph, string name, Func<RuleBuilder, (Term<TSort> Left, Term<TSort> Right)> define, bool backward)
            where TSort : struct, ISort<TSort>
        {
            var builder = new RuleBuilder(graph, name);
            try
            {
                var (left, right) = define(builder);
                if (builder.Draft.Head.Count > 0)
                    throw new RuleDefinitionException(name, "a birewrite's head is its rewrite: define adds variables and conditions (When, Where) only");

                if (backward)
                    builder.RewriteFirst(right, left);
                else
                    builder.RewriteFirst(left, right);
            }
            finally
            {
                builder.Close();
            }

            return builder.Draft;
        }

    }

}
