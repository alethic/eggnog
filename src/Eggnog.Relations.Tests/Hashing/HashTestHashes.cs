namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Hash functions of varying quality for hash-table tests, from well mixed to fully colliding.
    /// </summary>
    internal static class HashTestHashes
    {

        /// <summary>
        /// The number of schemes <see cref="Hash"/> accepts.
        /// </summary>
        public const int SchemeCount = 5;

        /// <summary>
        /// Hashes a key under a scheme.
        /// </summary>
        /// <param name="scheme">
        /// 0: Fx (well mixed). 1: the key itself (every tag is 0, probe starts differ). 2: Fx in the top 32 bits only
        /// (every probe starts at slot 0, tags differ). 3: a constant (every hash identical). 4: Fx of the key modulo
        /// 37 (groups of identical hashes).
        /// </param>
        /// <param name="key">The key.</param>
        /// <returns>The hash.</returns>
        public static ulong Hash(int scheme, int key) => scheme switch
        {
            0 => FxHasher.Hash((uint)key),
            1 => (uint)key,
            2 => FxHasher.Hash((uint)key) & 0xFFFF_FFFF_0000_0000UL,
            3 => 0xDEAD_BEEF_0000_0007UL,
            _ => FxHasher.Hash((uint)(key % 37)),
        };

    }

}
