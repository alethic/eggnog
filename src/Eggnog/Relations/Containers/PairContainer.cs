namespace Eggnog.Relations
{

    /// <summary>
    /// A pair of values: egglog's <c>PairContainer</c> (src/sort/pair.rs:3-29), with its two rebuild flags, which are
    /// part of its identity as egglog's derived <c>Eq</c> and <c>Hash</c> make them. An element is rebuilt when its flag
    /// is set, as egglog sets it for an element of an e-class or eq-container sort.
    /// </summary>
    internal readonly struct PairContainer : IContainerValue<PairContainer>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rebuildFirst">Whether the first element is rebuilt.</param>
        /// <param name="rebuildSecond">Whether the second element is rebuilt.</param>
        /// <param name="first">The first element's cell.</param>
        /// <param name="second">The second element's cell.</param>
        public PairContainer(bool rebuildFirst, bool rebuildSecond, uint first, uint second)
        {
            RebuildFirst = rebuildFirst;
            RebuildSecond = rebuildSecond;
            First = first;
            Second = second;
        }

        /// <summary>
        /// Gets whether the first element is rebuilt: egglog's <c>do_rebuild_first</c>.
        /// </summary>
        public bool RebuildFirst { get; }

        /// <summary>
        /// Gets whether the second element is rebuilt: egglog's <c>do_rebuild_second</c>.
        /// </summary>
        public bool RebuildSecond { get; }

        /// <summary>
        /// Gets the first element's cell.
        /// </summary>
        public uint First { get; }

        /// <summary>
        /// Gets the second element's cell.
        /// </summary>
        public uint Second { get; }

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(RebuildFirst ? 1UL : 0UL);
            hasher.Write(RebuildSecond ? 1UL : 0UL);
            hasher.Write(First);
            hasher.Write(Second);
            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_contents</c> (pair.rs:12-24): each flagged element is canonicalized.</remarks>
        public bool TryRebuild(Rebuilder rebuilder, out PairContainer rebuilt)
        {
            var first = RebuildFirst ? rebuilder.RebuildValue(First) : First;
            var second = RebuildSecond ? rebuilder.RebuildValue(Second) : Second;
            if (first == First && second == Second)
            {
                rebuilt = default;
                return false;
            }

            rebuilt = new PairContainer(RebuildFirst, RebuildSecond, first, second);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Visits the flagged elements only, where egglog's <c>iter</c> (pair.rs:26-28) yields both: the environment
        /// indexes a container by its values to find the ones a union affects, and an element that is never rebuilt is
        /// never affected, while a base value whose cell equals some displaced id would make the pair a spurious
        /// candidate.
        /// </remarks>
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            if (RebuildFirst)
                visitor.Visit(First);

            if (RebuildSecond)
                visitor.Visit(Second);
        }

        /// <inheritdoc />
        public bool Equals(PairContainer other) =>
            RebuildFirst == other.RebuildFirst && RebuildSecond == other.RebuildSecond && First == other.First && Second == other.Second;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is PairContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString() => $"pair[{First}, {Second}]";

    }

}
