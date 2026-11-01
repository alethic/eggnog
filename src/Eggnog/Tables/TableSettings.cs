namespace Eggnog
{

    /// <summary>
    /// The resolved definition settings of a table, from the options of its kind: its role, whether lifts copy it,
    /// whether it is subsumable, and, for a constructor, whether extraction skips it.
    /// </summary>
    internal readonly record struct TableSettings(TableRole Role, bool Lift, bool Subsumable, bool Unextractable)
    {

        /// <summary>
        /// The settings of a relation: a fact unless an analysis, lifted by the role's default, subsumable unless opted
        /// out.
        /// </summary>
        public static TableSettings Of(RelationOptions? options)
        {
            var role = options is { Analysis: true } ? TableRole.Analysis : TableRole.Fact;
            return new(role, options?.Lift ?? role == TableRole.Fact, options?.Subsumable ?? true, false);
        }

        /// <summary>
        /// The settings of a function: a fact unless an analysis, lifted by the role's default, never subsumable.
        /// </summary>
        public static TableSettings Of(FunctionOptions? options)
        {
            var role = options is { Analysis: true } ? TableRole.Analysis : TableRole.Fact;
            return new(role, options?.Lift ?? role == TableRole.Fact, false, false);
        }

        /// <summary>
        /// The settings of a constructor: a term, term-lifted, subsumable and extractable unless the options say
        /// otherwise.
        /// </summary>
        public static TableSettings Of(ConstructorOptions? options) =>
            new(TableRole.Term, options?.Lift ?? true, options?.Subsumable ?? true, options?.Unextractable ?? false);

    }

}
