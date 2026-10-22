using System;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A set of ids, kept sorted and distinct: the frontend's <c>SetContainer</c> (src/sort/set.rs:10-24). Rebuilding
    /// canonicalizes the elements and restores the order, so a set can shrink when two elements become equal. A second
    /// container type, for tests across environments.
    /// </summary>
    readonly struct SetContainer : IContainerValue<SetContainer>
    {

        readonly uint[]? values;

        /// <summary>
        /// Initializes a set of the distinct <paramref name="values"/>.
        /// </summary>
        public SetContainer(params uint[] values)
        {
            this.values = values.Distinct().Order().ToArray();
        }

        /// <summary>
        /// Gets the elements, ascending.
        /// </summary>
        public ReadOnlySpan<uint> Values => values;

        /// <inheritdoc />
        public bool HoldsIds => true;

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write((ulong)Values.Length);
            foreach (var value in Values)
                hasher.Write(value);

            return hasher.Finish();
        }

        /// <inheritdoc />
        public bool TryRebuild(Rebuilder rebuilder, out SetContainer rebuilt)
        {
            var copy = Values.ToArray();
            if (rebuilder.RebuildSlice(copy) == false)
            {
                rebuilt = default;
                return false;
            }

            rebuilt = new SetContainer(copy);
            return true;
        }

        /// <inheritdoc />
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            foreach (var value in Values)
                visitor.Visit(value);
        }

        /// <inheritdoc />
        public bool Equals(SetContainer other) => Values.SequenceEqual(other.Values);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SetContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => "set{" + string.Join(", ", Values.ToArray()) + "}";

    }

}
