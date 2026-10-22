namespace Eggnog
{

    /// <summary>
    /// The pair primitives of <see cref="Primitives"/> for one pair type, made once per pair of element types: egglog's
    /// <c>pair</c>, <c>pair-first</c> and <c>pair-second</c> (src/sort/pair.rs:119-133), all total.
    /// </summary>
    /// <typeparam name="T1">The type of the first element.</typeparam>
    /// <typeparam name="T2">The type of the second element.</typeparam>
    internal static class PairPrimitiveCache<T1, T2>
        where T1 : notnull
        where T2 : notnull
    {

        /// <summary>
        /// <c>pair</c>.
        /// </summary>
        public static readonly Primitive<T1, T2, Pair<T1, T2>> Make = new("pair", static (a, b) => new Pair<T1, T2>(a, b), false);

        /// <summary>
        /// <c>pair-first</c>.
        /// </summary>
        public static readonly Primitive<Pair<T1, T2>, T1> First = new("pair-first", static p => p.First, false);

        /// <summary>
        /// <c>pair-second</c>.
        /// </summary>
        public static readonly Primitive<Pair<T1, T2>, T2> Second = new("pair-second", static p => p.Second, false);

    }

}
