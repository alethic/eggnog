using System;

namespace Eggnog
{

    /// <summary>
    /// The untyped base of a function's lattice, and the built-in lattices.
    /// </summary>
    public abstract class ValueLattice
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The lattice's name, for rendering and messages.</param>
        /// <param name="valueType">The CLR type of the values it merges.</param>
        private protected ValueLattice(string name, Type valueType)
        {
            Name = name;
            ValueType = valueType;
        }

        /// <summary>
        /// Gets the lattice's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the CLR type of the values it merges.
        /// </summary>
        public Type ValueType { get; }

        /// <summary>
        /// The intersection lattice: merge ∩, case join ∪ (a domain: the values something can hold).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<ValueSet<T>> Intersect<T>() where T : notnull =>
            new("intersect", static (a, b) => a.Intersect(b), static (a, b) => a.Union(b));

        /// <summary>
        /// The union lattice: merge ∪, case join ∩ (a known set that only grows).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<ValueSet<T>> Union<T>() where T : notnull =>
            new("union", static (a, b) => a.Union(b), static (a, b) => a.Intersect(b));

        /// <summary>
        /// The maximum lattice: merge max, case join min (a lower bound).
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<T> Max<T>() where T : IComparable<T> =>
            new("max", static (a, b) => a.CompareTo(b) >= 0 ? a : b, static (a, b) => a.CompareTo(b) <= 0 ? a : b);

        /// <summary>
        /// The minimum lattice: merge min, case join max (an upper bound).
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<T> Min<T>() where T : IComparable<T> =>
            new("min", static (a, b) => a.CompareTo(b) <= 0 ? a : b, static (a, b) => a.CompareTo(b) >= 0 ? a : b);

        /// <summary>
        /// Gets the lattice's name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets whether merging the value of cell <paramref name="incoming"/> into the stored value of cell
        /// <paramref name="stored"/> leaves the stored value unchanged: whether a host write of it holds already (§6.6.1).
        /// Calls <c>Merge</c> on the calling thread.
        /// </summary>
        /// <param name="type">The function's output type.</param>
        /// <param name="stored">The stored value's cell.</param>
        /// <param name="incoming">The written value's cell.</param>
        /// <returns>Whether the write changes nothing.</returns>
        internal abstract bool Absorbs(ColumnType type, uint stored, uint incoming);

        /// <summary>
        /// Creates the merge external function of a function with this lattice (§6.1).
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="type">The function's output type.</param>
        /// <returns>The external function.</returns>
        internal abstract Relations.ExternalFunction CreateMerge(EGraph graph, string functionName, ColumnType type);

    }

}
