namespace Eggnog
{

    /// <summary>
    /// The higher-order primitives over a multiset of one element type: egglog's <c>unstable-multiset-flat-map</c> and
    /// <c>unstable-multiset-reduce</c>, made once per element type.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class FnMultiSetPrimitiveCache<T>
        where T : notnull
    {

        public static readonly PrimitiveN<MultiSet<T>> FlatMap =
            new("unstable-multiset-flat-map", [typeof(UnstableFn<T, MultiSet<T>>), typeof(MultiSet<T>)], FnMultiSetFlatMapBody<T>.Instance);

        public static readonly PrimitiveN<T> Reduce =
            new("unstable-multiset-reduce", [typeof(UnstableFn<T, T, T>), typeof(T), typeof(MultiSet<T>)], FnMultiSetReduceBody<T>.Instance);

    }

}
