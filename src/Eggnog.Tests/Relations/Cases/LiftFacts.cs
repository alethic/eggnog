using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What holds in a set of contexts over a parent's original ids, for <see cref="LifterPropertyTests"/>: equal
    /// pairs, relation rows, lattice joins and constructor terms, by id index. Sorted sets, so comparisons are
    /// order-free.
    /// </summary>
    sealed class LiftFacts
    {

        /// <summary>
        /// Gets the pairs of id indexes <c>(a, b)</c>, <c>a &lt; b</c>, equal in every context.
        /// </summary>
        public SortedSet<(int, int)> Equalities { get; } = new();

        /// <summary>
        /// Gets the <c>edge</c> rows every context holds.
        /// </summary>
        public SortedSet<(int, int)> Edges { get; } = new();

        /// <summary>
        /// Gets the <c>path</c> rows every context holds.
        /// </summary>
        public SortedSet<(int, int)> Paths { get; } = new();

        /// <summary>
        /// Gets the <c>mark</c> rows every context holds.
        /// </summary>
        public SortedSet<int> Marks { get; } = new();

        /// <summary>
        /// Gets, per id index, the largest <c>low</c> value over the contexts when every context holds one.
        /// </summary>
        public List<long?> Lows { get; } = new();

        /// <summary>
        /// Gets the <c>num(i)</c> terms every context holds.
        /// </summary>
        public SortedSet<int> Nums { get; } = new();

        /// <summary>
        /// Gets the pairs <c>(i, x)</c> where <c>num(i)</c> equals id <c>x</c> in every context.
        /// </summary>
        public SortedSet<(int, int)> NumEqualities { get; } = new();

    }

}
