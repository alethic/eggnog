namespace Eggnog.Relations
{

    /// <summary>
    /// What a rule records about one of its variables: egglog-bridge's <c>VarInfo</c> (egglog-bridge/src/rule.rs:51-55),
    /// named apart from the planner's <see cref="VarInfo"/>. When the rule is planned, each variable becomes a core
    /// variable with the same name.
    /// </summary>
    /// <param name="Type">The variable's column type, checked against the columns it is used in.</param>
    /// <param name="Name">The name given at creation, or <c>null</c>.</param>
    internal readonly record struct RuleVarInfo(ColumnTy Type, string? Name);

}
