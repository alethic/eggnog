namespace Eggnog
{

    /// <summary>
    /// A compiled rule-layer rule: its program and its engine variants (D36).
    /// </summary>
    internal sealed class CompiledRule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rule">The public rule.</param>
        /// <param name="program">The program.</param>
        /// <param name="variants">The engine variants.</param>
        /// <param name="serial">The graph-unique serial number, for descriptions.</param>
        public CompiledRule(Rule rule, RuleProgram program, RuleVariants variants, int serial)
        {
            Rule = rule;
            Program = program;
            Variants = variants;
            Serial = serial;
        }

        /// <summary>
        /// Gets the public rule.
        /// </summary>
        public Rule Rule { get; }

        /// <summary>
        /// Gets the program.
        /// </summary>
        public RuleProgram Program { get; }

        /// <summary>
        /// Gets the engine variants.
        /// </summary>
        public RuleVariants Variants { get; }

        /// <summary>
        /// Gets the graph-unique serial number, for descriptions.
        /// </summary>
        public int Serial { get; }

        /// <inheritdoc />
        public override string ToString() => Rule.Name + "#" + Serial;

    }

}
