using System;
using System.Buffers;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A vector of values: the test container of egglog's containers/tests.rs (<c>VecContainer(Vec&lt;Value&gt;)</c>),
    /// with the frontend's <c>do_rebuild</c> flag (src/sort/vec.rs:8-25). Rebuilding canonicalizes every element in
    /// place, keeping order and length.
    /// </summary>
    readonly struct VecContainer : IContainerValue<VecContainer>
    {

        /// <summary>
        /// Counts <see cref="TryRebuild"/> calls on the current thread, so tests can tell an incremental rebuild from a
        /// full one.
        /// </summary>
        [ThreadStatic]
        public static int TryRebuildCalls;

        readonly uint[]? values;

        /// <summary>
        /// Initializes a vector that rebuilds its elements.
        /// </summary>
        public VecContainer(params uint[] values)
            : this(true, values)
        {

        }

        /// <summary>
        /// Initializes a vector; one over a base sort does not rebuild.
        /// </summary>
        public VecContainer(bool doRebuild, params uint[] values)
        {
            DoRebuild = doRebuild;
            this.values = (uint[])values.Clone();
        }

        /// <summary>
        /// Gets whether the elements are ids that rebuilding canonicalizes.
        /// </summary>
        public bool DoRebuild { get; }

        /// <summary>
        /// Gets the elements.
        /// </summary>
        public ReadOnlySpan<uint> Values => values;

        /// <summary>
        /// Gets the number of elements.
        /// </summary>
        public int Length => values?.Length ?? 0;

        /// <inheritdoc />
        public bool HoldsIds => true;

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(DoRebuild ? 1UL : 0UL);
            hasher.Write((ulong)Length);
            foreach (var value in Values)
                hasher.Write(value);

            return hasher.Finish();
        }

        /// <inheritdoc />
        public bool TryRebuild(Rebuilder rebuilder, out VecContainer rebuilt)
        {
            TryRebuildCalls++;
            if (DoRebuild == false || Length == 0)
            {
                rebuilt = default;
                return false;
            }

            var scratch = ArrayPool<uint>.Shared.Rent(Length);
            var span = scratch.AsSpan(0, Length);
            Values.CopyTo(span);
            var changed = rebuilder.RebuildSlice(span);
            rebuilt = changed ? new VecContainer(DoRebuild, span.ToArray()) : default;
            ArrayPool<uint>.Shared.Return(scratch);
            return changed;
        }

        /// <inheritdoc />
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            foreach (var value in Values)
                visitor.Visit(value);
        }

        /// <inheritdoc />
        public bool Equals(VecContainer other) => DoRebuild == other.DoRebuild && Values.SequenceEqual(other.Values);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is VecContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => (DoRebuild ? "vec" : "basevec") + "[" + string.Join(", ", Values.ToArray()) + "]";

    }

}
