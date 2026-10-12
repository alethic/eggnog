namespace Eggnog.Relations
{

    /// <summary>
    /// A literal in a <see cref="TermDag"/>: a base value, with its type and its printed form. egglog's <c>Literal</c>
    /// (egglog-ast), generalized from egglog's five literal kinds to any registered base value type.
    /// </summary>
    /// <remarks>
    /// Equality is structural over all three fields, so two literals are hash-consed together exactly when they are the
    /// same value of the same type printed the same way.
    /// </remarks>
    /// <param name="Type">The base value type.</param>
    /// <param name="Value">The boxed value.</param>
    /// <param name="Text">How the literal prints; never <c>null</c> in a DAG.</param>
    internal readonly record struct TermLiteral(BaseValueTypeId Type, Value Value, string Text)
    {

        /// <inheritdoc />
        public override string ToString() => Text ?? string.Empty;

    }

}
