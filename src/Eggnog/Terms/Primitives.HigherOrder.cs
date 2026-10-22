namespace Eggnog
{

    public static partial class Primitives
    {

        /// <summary>
        /// <c>unstable-vec-map</c>: the vector of a function value applied to each element in order, an element it gives
        /// no result for dropped (vec.rs:231-282). Pure itself, each application runs where the call site's context
        /// allows the value's function, as <see cref="UnstableApp{T1, TResult}"/>'s does (decisions row 12).
        /// </summary>
        /// <typeparam name="T">The input vector's element type.</typeparam>
        /// <typeparam name="TResult">The output vector's element type.</typeparam>
        /// <returns>The primitive, of the function value and the vector.</returns>
        public static PrimitiveN<Vec<TResult>> UnstableVecMap<T, TResult>()
            where T : notnull
            where TResult : notnull
            => FnContainerPrimitiveCache<T, TResult>.VecMap;

        /// <summary>
        /// <c>unstable-multiset-map</c>: the multiset of a function value applied to each distinct element, each result
        /// counted as its element, an element it gives no result for dropped (multiset.rs:356-409). Each application runs
        /// where the call site's context allows the value's function, as <see cref="UnstableApp{T1, TResult}"/>'s does.
        /// </summary>
        /// <typeparam name="T">The input multiset's element type.</typeparam>
        /// <typeparam name="TResult">The output multiset's element type.</typeparam>
        /// <returns>The primitive, of the function value and the multiset.</returns>
        public static PrimitiveN<MultiSet<TResult>> UnstableMultiSetMap<T, TResult>()
            where T : notnull
            where TResult : notnull
            => FnContainerPrimitiveCache<T, TResult>.MultiSetMap;

        /// <summary>
        /// <c>unstable-multiset-flat-map</c>: the sum of the multisets of a function value applied to each distinct
        /// element, each counted times its element's count, an element it gives no result for kept as it is
        /// (multiset.rs:538-603). Each application runs where the call site's context allows the value's function, as
        /// <see cref="UnstableApp{T1, TResult}"/>'s does.
        /// </summary>
        /// <typeparam name="T">The multiset's element type.</typeparam>
        /// <returns>The primitive, of the function value and the multiset.</returns>
        public static PrimitiveN<MultiSet<T>> UnstableMultiSetFlatMap<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.FlatMap;

        /// <summary>
        /// <c>unstable-multiset-reduce</c>: a binary function value folded over the elements, each as many times as its
        /// count, in cell order as egglog's multiset keeps them, from the first element; the initial value for an empty
        /// multiset, and no result once an application has none (multiset.rs:721-783). Each application runs where the
        /// call site's context allows the value's function, as <see cref="UnstableApp{T1, T2, TResult}"/>'s does.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive, of the function value, the initial value and the multiset.</returns>
        public static PrimitiveN<T> UnstableMultiSetReduce<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.Reduce;

    }

}
