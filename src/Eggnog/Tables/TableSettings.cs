namespace Eggnog
{

    /// <summary>
    /// The resolved definition settings of a table, from the options of its kind: whether it is subsumable, and, for a
    /// constructor, whether extraction skips it.
    /// </summary>
    internal readonly record struct TableSettings(bool Subsumable, bool Unextractable)
    {

        /// <summary>
        /// The settings of a relation: subsumable unless opted out.
        /// </summary>
        public static TableSettings Of(RelationOptions? options) => new(options?.Subsumable ?? true, false);

        /// <summary>
        /// The settings of a function: never subsumable.
        /// </summary>
        public static TableSettings Of(FunctionOptions? options) => new(false, false);

        /// <summary>
        /// The settings of a constructor: subsumable and extractable unless the options say otherwise.
        /// </summary>
        public static TableSettings Of(ConstructorOptions? options) =>
            new(options?.Subsumable ?? true, options?.Unextractable ?? false);

    }

}
