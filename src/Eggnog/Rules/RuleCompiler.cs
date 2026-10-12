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
        /// <param name="owner">The scope the rule is local to; <c>null</c> for a global rule (always in R1).</param>
        /// <returns>The compiled rule, its default variant lowered with the written class constants (step 10).</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule, or the engine rejected it; nothing is
        /// registered then.</exception>
        public static CompiledRule Compile(EGraph graph, RuleDraft draft, Ruleset ruleset, Scope? owner)
        {
            var program = new ProgramBuilder(graph, draft, owner).Build();
            var serial = graph.NextSerial();
            var rule = new Rule(draft.Name, ruleset, owner, draft.Naive, draft);
            var description = ruleset.Name + "/" + draft.Name + "#" + serial;
            var variants = new RuleVariants(program, rule, description, draft.Naive);
            var compiled = new CompiledRule(rule, program, variants, serial);
            rule.Compiled = compiled;
            try
            {
                variants.Get(graph, draft.Naive == false, program.WrittenIdConstants);
            }
            catch (Exception exception) when (exception is not RuleDefinitionException)
            {
                variants.Free(graph);
                throw new RuleDefinitionException(draft.Name, "the engine rejected the rule: " + exception.Message, exception);
            }

            return compiled;
        }

    }

}
