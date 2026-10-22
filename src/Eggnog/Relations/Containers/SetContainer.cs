using System;
using System.Buffers;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A set of values: egglog's <c>SetContainer</c> (src/sort/set.rs:4-24), a <c>BTreeSet&lt;Value&gt;</c> with its
    /// <c>do_rebuild</c> flag, which is part of its identity as egglog's derived <c>Eq</c> and <c>Hash</c> make it. The
    /// elements are kept distinct and ascending by cell, as the <c>BTreeSet</c> keeps them; when the flag is set,
    /// rebuilding canonicalizes them and collects them again, so elements that became equal become one.
    /// </summary>
    internal readonly struct SetContainer : IContainerValue<SetContainer>
    {

        readonly uint[]? elements;

        /// <summary>
        /// Initializes a new instance over elements that are ascending and distinct, which it copies.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="elements">The elements' cells, ascending and distinct.</param>
        /// <exception cref="ArgumentException">The elements are not ascending and distinct.</exception>
        public SetContainer(bool rebuild, ReadOnlySpan<uint> elements)
        {
            for (var i = 1; i < elements.Length; i++)
                if (elements[i - 1] >= elements[i])
                    throw new ArgumentException("A set's elements are ascending and distinct.", nameof(elements));

            Rebuild = rebuild;
            this.elements = elements.ToArray();
        }

        /// <summary>
        /// Gets whether the elements are rebuilt: egglog's <c>do_rebuild</c>.
        /// </summary>
        public bool Rebuild { get; }

        /// <summary>
        /// Gets the elements' cells, ascending.
        /// </summary>
        public ReadOnlySpan<uint> Elements => elements;

        /// <summary>
        /// Gets the number of elements: egglog's <c>len</c>.
        /// </summary>
        public int Count => elements?.Length ?? 0;

        /// <inheritdoc />
        public bool HoldsIds => Rebuild;

        /// <summary>
        /// Makes a set of elements in any order, repeats once: egglog's <c>FromIterator</c> of a <c>BTreeSet</c>.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="cells">The elements' cells.</param>
        /// <returns>The set.</returns>
        public static SetContainer Collect(bool rebuild, ReadOnlySpan<uint> cells)
        {
            var sorted = cells.ToArray();
            Array.Sort(sorted);
            var distinct = new List<uint>(sorted.Length);
            foreach (var cell in sorted)
                if (distinct.Count == 0 || distinct[^1] != cell)
                    distinct.Add(cell);

            return new SetContainer(rebuild, distinct.ToArray());
        }

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(Rebuild ? 1UL : 0UL);
            hasher.Write((ulong)Count);
            foreach (var element in Elements)
                hasher.Write(element);

            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_contents</c> (set.rs:10-20): the elements rebuilt and collected again.</remarks>
        public bool TryRebuild(Rebuilder rebuilder, out SetContainer rebuilt)
        {
            if (Rebuild == false || Count == 0)
            {
                rebuilt = default;
                return false;
            }

            var scratch = ArrayPool<uint>.Shared.Rent(Count);
            var span = scratch.AsSpan(0, Count);
            Elements.CopyTo(span);
            var changed = rebuilder.RebuildSlice(span);
            rebuilt = changed ? Collect(Rebuild, span) : default;
            ArrayPool<uint>.Shared.Return(scratch);
            return changed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Visits the elements only when they are rebuilt, where egglog's <c>iter</c> yields them always: an element that
        /// is never rebuilt is never affected by a union (see <see cref="PairContainer.ForEachValue"/>).
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
        public bool Equals(SetContainer other) => Rebuild == other.Rebuild && Elements.SequenceEqual(other.Elements);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SetContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => "set{" + string.Join(", ", Elements.ToArray()) + "}";

    }

}
