using System;

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
        /// <c>unstable-multiset-filter</c>: the multiset of the elements a function value into <c>Unit</c> has a result
        /// for, with their counts (multiset.rs:605-662): a function of <c>Unit</c> output's rows, a primitive's fallible
        /// form (<see cref="Fallible"/>). Each application runs where the call site's context allows the value's function,
        /// as <see cref="UnstableApp{T1, TResult}"/>'s does.
        /// </summary>
        /// <typeparam name="T">The multiset's element type.</typeparam>
        /// <returns>The primitive, of the function value and the multiset.</returns>
        public static PrimitiveN<MultiSet<T>> UnstableMultiSetFilter<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.Filter;

        /// <summary>
        /// <c>unstable-multiset-filter-not</c>: the multiset of the elements a function value into <c>Unit</c> has no
        /// result for, with their counts (multiset.rs:605-662), as <see cref="UnstableMultiSetFilter{T}"/> applies it.
        /// </summary>
        /// <typeparam name="T">The multiset's element type.</typeparam>
        /// <returns>The primitive, of the function value and the multiset.</returns>
        public static PrimitiveN<MultiSet<T>> UnstableMultiSetFilterNot<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.FilterNot;

        /// <summary>
        /// <c>unstable-multiset-fill-index</c>: sets the row of a function value's function at the multiset and each of
        /// its distinct elements to the element's count, in cell order, stopping at the first such row there is, as
        /// egglog's does (multiset.rs:411-479); returns <c>()</c>, so it is an action run for its effect alone
        /// (<see cref="Fact.Exists"/>). A full primitive, as egglog's: it reads the rows, so it runs in a naive rule's head
        /// (<see cref="RuleBuilder.Naive"/>) or a host write. The function value's function must be a function, keyed by
        /// the multiset and an element, of an <c>i64</c> output; a primitive's is refused when it runs, as egglog
        /// panics.
        /// </summary>
        /// <typeparam name="T">The multiset's element type.</typeparam>
        /// <returns>The primitive, of the multiset and the function value.</returns>
        public static PrimitiveN<ValueTuple> UnstableMultiSetFillIndex<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.FillIndex;

        /// <summary>
        /// <c>unstable-multiset-clear-index</c>: deletes the rows <see cref="UnstableMultiSetFillIndex{T}"/> sets, those
        /// of a function value's function at the multiset and each of its elements (multiset.rs:481-536); returns
        /// <c>()</c>. A write primitive, as egglog's: it runs in a rule's head or a host write.
        /// </summary>
        /// <typeparam name="T">The multiset's element type.</typeparam>
        /// <returns>The primitive, of the multiset and the function value.</returns>
        public static PrimitiveN<ValueTuple> UnstableMultiSetClearIndex<T>()
            where T : notnull
            => FnMultiSetPrimitiveCache<T>.ClearIndex;

        /// <summary>
        /// egglog's fallible form of a filter primitive: <c>&lt;</c> of <see cref="Less"/> (<c>bool-&lt;</c>),
        /// <c>set-contains</c> of <see cref="Contains{T}"/>, <c>!=</c> of <see cref="NotEqual{T}"/>, and so on, which
        /// <see cref="RuleBuilder.Where(Term{bool})"/> renders. A partial primitive of <c>Unit</c>, named as egglog names
        /// it, with a result where the filter holds and none where it does not: a target for a function value into
        /// <c>Unit</c>, such as egglog's <c>(unstable-fn "&lt;" 1)</c>.
        /// </summary>
        /// <param name="filter">A <c>bool</c> primitive egglog has a fallible form of.</param>
        /// <returns>The fallible form, one per primitive.</returns>
        /// <exception cref="ArgumentException">egglog has no fallible form of the primitive.</exception>
        public static Primitive Fallible(Primitive filter)
        {
            ArgumentNullException.ThrowIfNull(filter);
            if (filter.FilterName is null || filter.ResultType != typeof(bool))
                throw new ArgumentException($"egglog has no fallible form of '{filter.Name}'; a filter over it renders as (guard ...).", nameof(filter));

            return filter.FallibleForm;
        }

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
