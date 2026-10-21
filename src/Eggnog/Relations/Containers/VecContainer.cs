using System;
using System.Buffers;

namespace Eggnog.Relations
{

    /// <summary>
    /// A vector of values: egglog's <c>VecContainer</c> (src/sort/vec.rs:8-25), with its <c>do_rebuild</c> flag, which is
    /// part of its identity as egglog's derived <c>Eq</c> and <c>Hash</c> make it. When the flag is set, rebuilding
    /// canonicalizes every element, keeping order and length, as egglog sets it for a vector of an e-class or
    /// eq-container sort.
    /// </summary>
    internal readonly struct VecContainer : IContainerValue<VecContainer>
    {

        readonly uint[]? values;

        /// <summary>
        /// Initializes a new instance over a copy of <paramref name="values"/>.
        /// </summary>
        /// <param name="rebuild">Whether the elements are rebuilt.</param>
        /// <param name="values">The elements' cells.</param>
        public VecContainer(bool rebuild, ReadOnlySpan<uint> values)
        {
            Rebuild = rebuild;
            this.values = values.ToArray();
        }

        /// <summary>
        /// Gets whether the elements are rebuilt: egglog's <c>do_rebuild</c>.
        /// </summary>
        public bool Rebuild { get; }

        /// <summary>
        /// Gets the elements' cells.
        /// </summary>
        public ReadOnlySpan<uint> Values => values;

        /// <summary>
        /// Gets the number of elements.
        /// </summary>
        public int Length => values?.Length ?? 0;

        /// <inheritdoc />
        public bool HoldsIds => Rebuild;

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(Rebuild ? 1UL : 0UL);
            hasher.Write((ulong)Length);
            foreach (var value in Values)
                hasher.Write(value);

            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_contents</c> (vec.rs:14-24).</remarks>
        public bool TryRebuild(Rebuilder rebuilder, out VecContainer rebuilt)
        {
            if (Rebuild == false || Length == 0)
            {
                rebuilt = default;
                return false;
            }

            var scratch = ArrayPool<uint>.Shared.Rent(Length);
            var span = scratch.AsSpan(0, Length);
            Values.CopyTo(span);
            var changed = rebuilder.RebuildSlice(span);
            rebuilt = changed ? new VecContainer(Rebuild, span) : default;
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

            foreach (var value in Values)
                visitor.Visit(value);
        }

        /// <inheritdoc />
        public bool Equals(VecContainer other) => Rebuild == other.Rebuild && Values.SequenceEqual(other.Values);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is VecContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => "vec[" + string.Join(", ", Values.ToArray()) + "]";

    }

}
