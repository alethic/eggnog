namespace Eggnog
{

    /// <summary>
    /// Which builder call made a head item.
    /// </summary>
    internal enum HeadItemKind
    {

        /// <summary>
        /// <see cref="RuleBuilder.Then"/>: a head fact.
        /// </summary>
        Then,

        /// <summary>
        /// <see cref="RuleBuilder.Make{TSort}(string, ConstructorTerm{TSort})"/>: a head variable bound by get-or-add.
        /// </summary>
        Make,

        /// <summary>
        /// <see cref="RuleBuilder.Rewrite{TSort}(Term{TSort}, Term{TSort})"/>: the head half of a rewrite.
        /// </summary>
        Rewrite,

        /// <summary>
        /// <see cref="RuleBuilder.Subsume{TSort}(ConstructorTerm{TSort})"/>, <see cref="RuleBuilder.Subsume(Fact[])"/>,
        /// or the subsumption of a rewrite's left side
        /// (<see cref="RuleBuilder.Rewrite{TSort}(Term{TSort}, Term{TSort}, bool)"/>): egglog's <c>subsume</c> action.
        /// </summary>
        Subsume,

    }

}
