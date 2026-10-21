using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A multiset of values: egglog's <c>MultiSetContainer</c> (src/sort/multiset.rs:6-28), a <c>BTreeMap&lt;Value,
    /// usize&gt;</c> of positive counts with its <c>do_rebuild</c> flag, which is part of its identity as egglog's derived
    /// <c>Eq</c> and <c>Hash</c> make it. The distinct elements are kept in ascending order of their cells, as egglog's
    /// <c>BTreeMap</c> keeps them, each with its count.
    /// </summary>
    internal readonly struct MultiSetContainer : IContainerValue<MultiSetContainer>
    {

        readonly uint[]? elements;
        readonly int[]? counts;

        /// <summary>
        /// Initializes a new instance over distinct elements in ascending order, each with a positive count, which it
        /// copies.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="elements">The distinct elements' cells, ascending.</param>
        /// <param name="counts">Each element's count, positive.</param>
        /// <exception cref="ArgumentException">The elements are not ascending and distinct, a count is not positive, or
        /// the lengths differ.</exception>
        public MultiSetContainer(bool rebuild, ReadOnlySpan<uint> elements, ReadOnlySpan<int> counts)
        {
            if (elements.Length != counts.Length)
                throw new ArgumentException("A multiset has one count per element.", nameof(counts));

            long total = 0;
            for (var i = 0; i < elements.Length; i++)
            {
                if (i > 0 && elements[i - 1] >= elements[i])
                    throw new ArgumentException("A multiset's elements are ascending and distinct.", nameof(elements));

                if (counts[i] <= 0)
                    throw new ArgumentException("A multiset's counts are positive.", nameof(counts));

                total += counts[i];
            }

            Rebuild = rebuild;
            this.elements = elements.ToArray();
            this.counts = counts.ToArray();
            Length = checked((int)total);
        }

        /// <summary>
        /// Gets whether the elements are rebuilt: egglog's <c>do_rebuild</c>.
        /// </summary>
        public bool Rebuild { get; }

        /// <summary>
        /// Gets the distinct elements' cells, ascending.
        /// </summary>
        public ReadOnlySpan<uint> Elements => elements;

        /// <summary>
        /// Gets each distinct element's count.
        /// </summary>
        public ReadOnlySpan<int> Counts => counts;

        /// <summary>
        /// Gets the number of elements, counted with multiplicity: egglog's <c>len</c>.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Makes a multiset of elements in any order, repeats counted: egglog's <c>FromIterator</c>.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="all">The elements' cells, with repeats.</param>
        /// <returns>The multiset.</returns>
        public static MultiSetContainer Collect(bool rebuild, ReadOnlySpan<uint> all)
        {
            var sorted = all.ToArray();
            Array.Sort(sorted);
            var distinct = new List<uint>(sorted.Length);
            var counts = new List<int>(sorted.Length);
            foreach (var element in sorted)
            {
                if (distinct.Count > 0 && distinct[^1] == element)
                {
                    counts[^1]++;
                    continue;
                }

                distinct.Add(element);
                counts.Add(1);
            }

            return new MultiSetContainer(rebuild, distinct.ToArray(), counts.ToArray());
        }

        /// <summary>
        /// Makes a multiset of elements with counts, in any order: sorted by cell, the counts of equal cells added.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="cells">The elements' cells.</param>
        /// <param name="counts">Each element's count, positive.</param>
        /// <returns>The multiset.</returns>
        public static MultiSetContainer FromCounts(bool rebuild, uint[] cells, int[] counts)
        {
            var order = new int[cells.Length];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, (a, b) => cells[a].CompareTo(cells[b]));
            var distinct = new List<uint>(cells.Length);
            var merged = new List<int>(cells.Length);
            foreach (var at in order)
            {
                if (distinct.Count > 0 && distinct[^1] == cells[at])
                {
                    merged[^1] = checked(merged[^1] + counts[at]);
                    continue;
                }

                distinct.Add(cells[at]);
                merged.Add(counts[at]);
            }

            return new MultiSetContainer(rebuild, distinct.ToArray(), merged.ToArray());
        }

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(Rebuild ? 1UL : 0UL);
            hasher.Write((ulong)Length);
            for (var i = 0; i < Elements.Length; i++)
            {
                hasher.Write(Elements[i]);
                hasher.Write((ulong)Counts[i]);
            }

            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_contents</c> (multiset.rs:12-24): every element, with its repeats, canonicalized and the
        /// multiset collected again, so elements that became equal add their counts.
        /// </remarks>
        public bool TryRebuild(Rebuilder rebuilder, out MultiSetContainer rebuilt)
        {
            if (Rebuild == false || Elements.Length == 0)
            {
                rebuilt = default;
                return false;
            }

            var distinct = Elements.ToArray();
            if (rebuilder.RebuildSlice(distinct) == false)
            {
                rebuilt = default;
                return false;
            }

            var all = new uint[Length];
            var at = 0;
            for (var i = 0; i < distinct.Length; i++)
                for (var k = 0; k < Counts[i]; k++)
                    all[at++] = distinct[i];

            rebuilt = Collect(Rebuild, all);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Visits each distinct element once, and only when the elements are rebuilt, where egglog's <c>iter</c> yields
        /// every element with its repeats: an element that is never rebuilt is never affected by a union (see
        /// <see cref="PairContainer.ForEachValue"/>).
        /// </remarks>
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            if (Rebuild == false)
                return;

            foreach (var element in Elements)
                visitor.Visit(element);
        }

        /// <inheritdoc />
        public bool Equals(MultiSetContainer other) =>
            Rebuild == other.Rebuild && Elements.SequenceEqual(other.Elements) && Counts.SequenceEqual(other.Counts);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MultiSetContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString()
        {
            var parts = new string[Elements.Length];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = Elements[i] + "x" + Counts[i];

            return "multiset{" + string.Join(", ", parts) + "}";
        }

    }

}
