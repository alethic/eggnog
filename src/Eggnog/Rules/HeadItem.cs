namespace Eggnog
{

    /// <summary>
    /// One head item of a draft, in builder-call order.
    /// </summary>
    /// <param name="Kind">Which builder call made it.</param>
    /// <param name="Fact">The head fact of <see cref="HeadItemKind.Then"/>.</param>
    /// <param name="Variable">The variable of <see cref="HeadItemKind.Make"/>, or the hidden class variable of
    /// <see cref="HeadItemKind.Rewrite"/>.</param>
    /// <param name="Left">The term of <see cref="HeadItemKind.Make"/>, or the left side of a rewrite.</param>
    /// <param name="Right">The right side of a rewrite.</param>
    internal readonly record struct HeadItem(HeadItemKind Kind, Fact? Fact, Term? Variable, Term? Left, Term? Right);

}
