using System;

namespace Eggnog
{

    /// <summary>
    /// Checks and normalizes a draft into a program and a compiled rule (§6.3), then lowers the rule's default variant.
    /// Every error is a <see cref="RuleDefinitionException"/> naming the rule. Called under the graph's definition lock.
    /// </summary>
    internal static class RuleCompiler
    {

        /// <summary>
        /// Compiles a rule.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">What the builder collected.</param>
        /// <param name="ruleset">The ruleset that schedules the rule.</param>
        /// <param name="owner">The scope the rule is local to; <c>null</c> for a global rule.</param>
        /// <returns>The compiled rule, its default variant lowered with the written class constants (step 10).</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule, or the engine rejected it; nothing is
        /// registered then (a mirror its first scoped read created stays, harmlessly).</exception>
        public static CompiledRule Compile(EGraph graph, RuleDraft draft, Ruleset ruleset, Scope? owner)
        {
            var licence = draft.Erasing ?? ruleset.Licence;
            var program = new ProgramBuilder(graph, draft, owner, licence: licence).Build();
            var serial = graph.NextSerial();
            var effective = draft.OwnFactsReason is null ? licence : null;
            var rule = new Rule(draft.Name, ruleset, owner, draft.Naive, draft, effective, Array.AsReadOnly(program.ReadScopes));
            var description = (owner is null ? "" : owner.Path + ":") + ruleset.Name + "/" + draft.Name + "#" + serial;
            var variants = new RuleVariants(program, rule, description, draft.Naive);
            var compiled = new CompiledRule(rule, program, variants, serial);
            rule.Compiled = compiled;
            try
            {
                variants.Get(graph, draft.Naive == false);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the rule: " + exception.Message, exception);
            }

            return compiled;
        }

        /// <summary>
        /// Compiles a query (§6.6.4): the body as a rule's (§6.3 steps 1 to 7) and a head of one collector of the declared
        /// variables, then lowers its one variant, naive, with the written class constants. Called under the graph's
        /// definition lock.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">What the query's builder collected.</param>
        /// <returns>The compiled query, with its public <see cref="Query"/>.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid query, or the engine rejected it; nothing is
        /// registered then.</exception>
        public static CompiledQuery CompileQuery(EGraph graph, RuleDraft draft)
        {
            var program = new ProgramBuilder(graph, draft, null, query: true).BuildQuery();
            var serial = graph.NextSerial();
            var description = "$query/" + draft.Name + "#" + serial;
            var variants = new RuleVariants(program, null, description, true);
            var columns = program.Head[0].Inputs;
            var types = new ColumnType[columns.Length];
            var variables = new Term[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                types[i] = columns[i].Type;
                variables[i] = draft.Variables[i].Variable;
            }

            var query = new Query(graph, draft.Name, variables) { Draft = draft };
            var compiled = new CompiledQuery(graph, query, program, variants, serial, types);
            query.Compiled = compiled;
            try
            {
                variants.Get(graph, false);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the query: " + exception.Message, exception);
            }

            return compiled;
        }

    }

}
