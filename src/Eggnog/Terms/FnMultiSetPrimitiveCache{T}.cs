using System;

namespace Eggnog
{

    /// <summary>
    /// The higher-order primitives over a multiset of one element type: egglog's <c>unstable-multiset-flat-map</c>,
    /// <c>-filter</c>, <c>-filter-not</c> and <c>-reduce</c>, made once per element type.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class FnMultiSetPrimitiveCache<T>
        where T : notnull
    {

        public static readonly PrimitiveN<MultiSet<T>> FlatMap =
            new("unstable-multiset-flat-map", [typeof(UnstableFn<T, MultiSet<T>>), typeof(MultiSet<T>)], FnMultiSetFlatMapBody<T>.Instance);

        public static readonly PrimitiveN<MultiSet<T>> Filter =
            new("unstable-multiset-filter", [typeof(UnstableFn<T, ValueTuple>), typeof(MultiSet<T>)], FnMultiSetFilterBody<T>.Filter);

        public static readonly PrimitiveN<MultiSet<T>> FilterNot =
            new("unstable-multiset-filter-not", [typeof(UnstableFn<T, ValueTuple>), typeof(MultiSet<T>)], FnMultiSetFilterBody<T>.FilterNot);

        public static readonly PrimitiveN<T> Reduce =
            new("unstable-multiset-reduce", [typeof(UnstableFn<T, T, T>), typeof(T), typeof(MultiSet<T>)], FnMultiSetReduceBody<T>.Instance);

    }

}
