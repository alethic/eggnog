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

    }

}
