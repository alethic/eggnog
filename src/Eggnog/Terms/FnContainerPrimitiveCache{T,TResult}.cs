namespace Eggnog
{

    /// <summary>
    /// The higher-order primitives that map a function value of one element type to another over a container: egglog's
    /// <c>unstable-vec-map</c> and <c>unstable-multiset-map</c>, made once per pair of element types.
    /// </summary>
    /// <typeparam name="T">The input element type.</typeparam>
    /// <typeparam name="TResult">The output element type.</typeparam>
    internal static class FnContainerPrimitiveCache<T, TResult>
        where T : notnull
        where TResult : notnull
    {

        public static readonly PrimitiveN<Vec<TResult>> VecMap =
            new("unstable-vec-map", [typeof(UnstableFn<T, TResult>), typeof(Vec<T>)], FnVecMapBody<T, TResult>.Instance);

        public static readonly PrimitiveN<MultiSet<TResult>> MultiSetMap =
            new("unstable-multiset-map", [typeof(UnstableFn<T, TResult>), typeof(MultiSet<T>)], FnMultiSetMapBody<T, TResult>.Instance);

    }

}
