namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A container of one value, of a container type of its own for each <typeparamref name="TTag"/>: for tests that
    /// register many container types, since a <see cref="ContainerValues"/> holds one environment per container type
    /// and finds it by the type's process-wide slot (<see cref="ContainerTypeSlot{TContainer}"/>).
    /// </summary>
    /// <typeparam name="TTag">Any type; it only tells the container types apart.</typeparam>
    readonly struct TaggedContainer<TTag> : IContainerValue<TaggedContainer<TTag>>
    {

        /// <summary>
        /// Initializes a container of <paramref name="value"/>, an e-class id that rebuilding canonicalizes when
        /// <paramref name="holdsIds"/> is set and a base value otherwise.
        /// </summary>
        public TaggedContainer(uint value, bool holdsIds = false)
        {
            Value = value;
            HoldsIds = holdsIds;
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public uint Value { get; }

        /// <inheritdoc />
        public bool HoldsIds { get; }

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(HoldsIds ? 1UL : 0UL);
            hasher.Write(Value);
            return hasher.Finish();
        }

        /// <inheritdoc />
        public bool TryRebuild(Rebuilder rebuilder, out TaggedContainer<TTag> rebuilt)
        {
            var value = HoldsIds ? rebuilder.RebuildValue(Value) : Value;
            rebuilt = value != Value ? new TaggedContainer<TTag>(value, HoldsIds) : default;
            return value != Value;
        }

        /// <inheritdoc />
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            visitor.Visit(Value);
        }

        /// <inheritdoc />
        public bool Equals(TaggedContainer<TTag> other) => Value == other.Value && HoldsIds == other.HoldsIds;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TaggedContainer<TTag> other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => typeof(TTag).Name + "[" + Value + "]";

    }

}
