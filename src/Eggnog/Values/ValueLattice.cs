using System;
using System.Collections.Generic;

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
        /// <remarks>
        /// Values are ordered as <see cref="ValueSet{T}"/> orders its elements: strings ordinally, by UTF-16 code unit,
        /// never by the current culture, so the result does not depend on the machine or the thread; every other type
        /// by <see cref="Comparer{T}.Default"/>, which calls <see cref="IComparable{T}.CompareTo"/>. A type's
        /// <c>CompareTo</c> must be a total order consistent with <see cref="IEquatable{T}.Equals(T)"/>: two values
        /// that compare as 0 but are not equal make the merge depend on the order of its arguments, which
        /// <see cref="ValueLattice{T}.Verify"/> reports as a commutativity violation.
        /// </remarks>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<T> Max<T>() where T : IComparable<T> =>
            new("max",
                static (a, b) => ValueSetOrder<T>.Comparer.Compare(a, b) >= 0 ? a : b,
                static (a, b) => ValueSetOrder<T>.Comparer.Compare(a, b) <= 0 ? a : b);

        /// <summary>
        /// The minimum lattice: merge min, case join max (an upper bound).
        /// </summary>
        /// <remarks>
        /// Values are ordered as for <see cref="Max{T}"/>: strings ordinally, as <see cref="ValueSet{T}"/> orders them,
        /// and every other type by its <see cref="IComparable{T}.CompareTo"/>, which must be a total order consistent
        /// with <see cref="IEquatable{T}.Equals(T)"/>; <see cref="ValueLattice{T}.Verify"/> checks this on samples.
        /// </remarks>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The lattice.</returns>
        public static ValueLattice<T> Min<T>() where T : IComparable<T> =>
            new("min",
                static (a, b) => ValueSetOrder<T>.Comparer.Compare(a, b) <= 0 ? a : b,
                static (a, b) => ValueSetOrder<T>.Comparer.Compare(a, b) >= 0 ? a : b);

        /// <summary>
        /// Gets the lattice's name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets whether merging the value of cell <paramref name="incoming"/> into the stored value of cell
        /// <paramref name="stored"/> leaves the stored value unchanged: whether a host write of it holds already. Calls
        /// <c>Merge</c> on the calling thread.
        /// </summary>
        /// <param name="type">The function's output type.</param>
        /// <param name="stored">The stored value's cell.</param>
        /// <param name="incoming">The written value's cell.</param>
        /// <returns>Whether the write changes nothing.</returns>
        internal abstract bool Absorbs(ColumnType type, uint stored, uint incoming);

        /// <summary>
        /// Creates the merge external function of a function with this lattice.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="type">The function's output type.</param>
        /// <returns>The external function.</returns>
        internal abstract Relations.ExternalFunction CreateMerge(EGraph graph, string functionName, ColumnType type);

        /// <summary>
        /// Creates the case join a lift uses for a function with this lattice, over cells; <c>null</c> when the lattice
        /// has none, so that a lift requires every live case to agree.
        /// </summary>
        /// <param name="type">The function's output type.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <returns>The case join, or <c>null</c>.</returns>
        internal abstract Func<uint, uint, uint>? CreateCaseJoin(ColumnType type, string functionName);

    }

}
