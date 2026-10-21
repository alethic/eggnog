namespace Eggnog
{

    /// <summary>
    /// One head item of a draft, in builder-call order.
    /// </summary>
    /// <param name="Kind">Which builder call made it.</param>
    /// <param name="Fact">The head fact of <see cref="HeadItemKind.Then"/>, or the relation row of
    /// <see cref="HeadItemKind.Subsume"/>.</param>
    /// <param name="Variable">The variable of <see cref="HeadItemKind.Make"/>, or the hidden class variable of <see
    /// cref="HeadItemKind.Rewrite"/> and of the <see cref="HeadItemKind.Subsume"/> of a rewrite's left side.</param>
    /// <param name="Left">The term of <see cref="HeadItemKind.Make"/>, the left side of a rewrite, or the constructor
    /// term of <see cref="HeadItemKind.Subsume"/>.</param>
    /// <param name="Right">The right side of a rewrite.</param>
    /// <param name="Callback">The callback of <see cref="HeadItemKind.Callback"/>.</param>
    internal readonly record struct HeadItem(HeadItemKind Kind, Fact? Fact, Term? Variable, Term? Left, Term? Right,
        RuleCallbackDefinition? Callback = null);

}
