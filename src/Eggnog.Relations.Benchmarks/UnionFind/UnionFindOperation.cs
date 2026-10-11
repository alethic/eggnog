namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// One operation of a union-find workload: egglog's <c>Operation</c> enum in
    /// <c>union-find/benches/union-find.rs</c>, either <c>Union(a, b)</c> or <c>Find(a)</c>.
    /// </summary>
    internal readonly struct UnionFindOperation
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">The first identifier.</param>
        /// <param name="right">The second identifier of a union; zero for a find.</param>
        /// <param name="isUnion">Whether the operation is a union.</param>
        UnionFindOperation(uint left, uint right, bool isUnion)
        {
            Left = left;
            Right = right;
            IsUnion = isUnion;
        }

        /// <summary>
        /// Gets the identifier a find looks up, or the first identifier of a union. The sort key of the "local"
        /// workload.
        /// </summary>
        public uint Left { get; }

        /// <summary>
        /// Gets the second identifier of a union.
        /// </summary>
        public uint Right { get; }

        /// <summary>
        /// Gets whether the operation is a union (otherwise it is a find).
        /// </summary>
        public bool IsUnion { get; }

        /// <summary>
        /// Creates <c>Union(left, right)</c>.
        /// </summary>
        /// <param name="left">The first identifier.</param>
        /// <param name="right">The second identifier.</param>
        /// <returns>The operation.</returns>
        public static UnionFindOperation Union(uint left, uint right) => new(left, right, true);

        /// <summary>
        /// Creates <c>Find(id)</c>.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The operation.</returns>
        public static UnionFindOperation Find(uint id) => new(id, 0, false);

    }

}
