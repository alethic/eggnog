namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A head item of a random rule, lowered in order: a head fact, a <c>Make</c>, or a rewrite. Immutable.
    /// </summary>
    internal sealed class DiffHead
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        DiffHead(DiffHeadKind kind, DiffFact? fact, string? name, DiffTerm? left, DiffTerm? right)
        {
            Kind = kind;
            Fact = fact;
            Name = name;
            Left = left;
            Right = right;
        }

        /// <summary>Gets what the item is.</summary>
        public DiffHeadKind Kind { get; }

        /// <summary>Gets a head fact.</summary>
        public DiffFact? Fact { get; }

        /// <summary>Gets a <c>Make</c> variable's name.</summary>
        public string? Name { get; }

        /// <summary>Gets a <c>Make</c>'s constructor term, or a rewrite's left side.</summary>
        public DiffTerm? Left { get; }

        /// <summary>Gets a rewrite's right side.</summary>
        public DiffTerm? Right { get; }

        /// <summary>
        /// A head fact.
        /// </summary>
        /// <param name="fact">The fact: a row, a function or constructor write, or a union.</param>
        /// <returns>The item.</returns>
        public static DiffHead Then(DiffFact fact) => new(DiffHeadKind.Then, fact, null, null, null);

        /// <summary>
        /// A head variable bound by get-or-add.
        /// </summary>
        /// <param name="name">The variable's name.</param>
        /// <param name="term">The constructor term.</param>
        /// <returns>The item.</returns>
        public static DiffHead Make(string name, DiffTerm term) => new(DiffHeadKind.Make, null, name, term, null);

        /// <summary>
        /// A rewrite.
        /// </summary>
        /// <param name="left">The pattern: a constructor term.</param>
        /// <param name="right">The replacement: a sort term.</param>
        /// <returns>The item.</returns>
        public static DiffHead Rewrite(DiffTerm left, DiffTerm right) => new(DiffHeadKind.Rewrite, null, null, left, right);

    }

}
