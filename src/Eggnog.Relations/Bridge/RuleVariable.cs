namespace Eggnog.Relations
{

    /// <summary>
    /// A variable of a rule built with an <see cref="EGraphRuleBuilder"/>, with an optional name for diagnostics:
    /// egglog-bridge's <c>Variable</c> (egglog-bridge/src/rule.rs:37-41), named apart from the core query's
    /// <see cref="Variable"/>. Its type is recorded by the builder that created it. Equality compares the id and the name,
    /// as egglog's derived equality does.
    /// </summary>
    /// <param name="Id">The variable's id within its rule.</param>
    /// <param name="Name">The name given at creation, or <c>null</c>.</param>
    public readonly record struct RuleVariable(VariableId Id, string? Name)
    {

        /// <summary>
        /// Wraps <paramref name="variable"/> as an entry: egglog's <c>From&lt;Variable&gt; for QueryEntry</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public static implicit operator RuleEntry(RuleVariable variable) => RuleEntry.Var(variable);

        /// <inheritdoc />
        public override string ToString() => Name ?? Id.ToString();

    }

}
