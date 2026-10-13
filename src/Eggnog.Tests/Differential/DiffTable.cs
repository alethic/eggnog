namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A table of a random program: a relation over values, a function with a value output, or a constructor of the
    /// sort <see cref="Support.Node"/>.
    /// </summary>
    internal sealed class DiffTable
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The position in declaration order.</param>
        /// <param name="name">The name.</param>
        /// <param name="kind">Relation, function or constructor.</param>
        /// <param name="keys">The key types.</param>
        /// <param name="output">The output type; <c>null</c> for a relation.</param>
        /// <param name="lattice">A function's merge.</param>
        public DiffTable(int index, string name, TableKind kind, DiffType[] keys, DiffType? output, DiffLattice lattice)
        {
            Index = index;
            Name = name;
            Kind = kind;
            Keys = keys;
            Output = output;
            Lattice = lattice;
        }

        /// <summary>Gets the position in declaration order.</summary>
        public int Index { get; }

        /// <summary>Gets the name.</summary>
        public string Name { get; }

        /// <summary>Gets whether the table is a relation, a function or a constructor.</summary>
        public TableKind Kind { get; }

        /// <summary>Gets the key types.</summary>
        public DiffType[] Keys { get; }

        /// <summary>Gets the output type; <c>null</c> for a relation.</summary>
        public DiffType? Output { get; }

        /// <summary>Gets a function's merge.</summary>
        public DiffLattice Lattice { get; }

        /// <summary>Gets whether the table is the function without a lattice that only host writes write.</summary>
        public bool HostOnly => Kind == TableKind.Function && Lattice == DiffLattice.None;

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
