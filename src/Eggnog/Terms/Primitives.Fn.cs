using System;

namespace Eggnog
{

    public static partial class Primitives
    {

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn ()
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<TResult>> UnstableFn<TResult>(TableHandle target)
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<TResult>>.Of(target, [], typeof(TResult), static content => new global::Eggnog.UnstableFn<TResult>(content));

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn ()
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<TResult>> UnstableFn<TResult>(Primitive target)
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<TResult>>.Of(target, [], typeof(TResult), static content => new global::Eggnog.UnstableFn<TResult>(content));

        /// <summary>
        /// <c>unstable-app</c>: applies a function value of the sort <c>(UnstableFn () TResult)</c> to the inputs, its
        /// parameters after the value. Pure itself, it applies the value's function where the call site's context allows
        /// it, as egglog's <c>FunctionContainer::apply</c> dispatches: a constructor gets or makes its term where the context
        /// writes (a rule's head, a host write, a merge); a function reads its row where the context reads
        /// (a naive rule, a host read or write), none when there is no row; a primitive runs where its access is allowed
        /// (decisions row 12). Anywhere else the application fails: in a rule, make it naive (<c>Naive()</c>).
        /// </summary>
        /// <typeparam name="TResult">The type of the output.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> UnstableApp<TResult>()
            where TResult : notnull
            => FnApplyCache<global::Eggnog.UnstableFn<TResult>, TResult>.Of([]);

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, TResult>> UnstableFn<T1, TResult>(TableHandle target)
            where T1 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, TResult>>.Of(target, [typeof(T1)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, TResult>(content));

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, TResult>> UnstableFn<T1, TResult>(Primitive target)
            where T1 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, TResult>>.Of(target, [typeof(T1)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, TResult>(content));

        /// <summary>
        /// <c>unstable-app</c>: applies a function value of the sort <c>(UnstableFn (T1) TResult)</c> to the inputs, its
        /// parameters after the value. Pure itself, it applies the value's function where the call site's context allows
        /// it, as egglog's <c>FunctionContainer::apply</c> dispatches: a constructor gets or makes its term where the context
        /// writes (a rule's head, a host write, a merge); a function reads its row where the context reads
        /// (a naive rule, a host read or write), none when there is no row; a primitive runs where its access is allowed
        /// (decisions row 12). Anywhere else the application fails: in a rule, make it naive (<c>Naive()</c>).
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="TResult">The type of the output.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> UnstableApp<T1, TResult>()
            where T1 : notnull
            where TResult : notnull
            => FnApplyCache<global::Eggnog.UnstableFn<T1, TResult>, TResult>.Of([typeof(T1)]);

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, TResult>> UnstableFn<T1, T2, TResult>(TableHandle target)
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, TResult>>.Of(target, [typeof(T1), typeof(T2)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, TResult>(content));

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, TResult>> UnstableFn<T1, T2, TResult>(Primitive target)
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, TResult>>.Of(target, [typeof(T1), typeof(T2)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, TResult>(content));

        /// <summary>
        /// <c>unstable-app</c>: applies a function value of the sort <c>(UnstableFn (T1 T2) TResult)</c> to the inputs, its
        /// parameters after the value. Pure itself, it applies the value's function where the call site's context allows
        /// it, as egglog's <c>FunctionContainer::apply</c> dispatches: a constructor gets or makes its term where the context
        /// writes (a rule's head, a host write, a merge); a function reads its row where the context reads
        /// (a naive rule, a host read or write), none when there is no row; a primitive runs where its access is allowed
        /// (decisions row 12). Anywhere else the application fails: in a rule, make it naive (<c>Naive()</c>).
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="TResult">The type of the output.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> UnstableApp<T1, T2, TResult>()
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
            => FnApplyCache<global::Eggnog.UnstableFn<T1, T2, TResult>, TResult>.Of([typeof(T1), typeof(T2)]);

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2 T3)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, T3, TResult>> UnstableFn<T1, T2, T3, TResult>(TableHandle target)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, T3, TResult>>.Of(target, [typeof(T1), typeof(T2), typeof(T3)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, T3, TResult>(content));

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2 T3)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, T3, TResult>> UnstableFn<T1, T2, T3, TResult>(Primitive target)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, T3, TResult>>.Of(target, [typeof(T1), typeof(T2), typeof(T3)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, T3, TResult>(content));

        /// <summary>
        /// <c>unstable-app</c>: applies a function value of the sort <c>(UnstableFn (T1 T2 T3) TResult)</c> to the inputs, its
        /// parameters after the value. Pure itself, it applies the value's function where the call site's context allows
        /// it, as egglog's <c>FunctionContainer::apply</c> dispatches: a constructor gets or makes its term where the context
        /// writes (a rule's head, a host write, a merge); a function reads its row where the context reads
        /// (a naive rule, a host read or write), none when there is no row; a primitive runs where its access is allowed
        /// (decisions row 12). Anywhere else the application fails: in a rule, make it naive (<c>Naive()</c>).
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="TResult">The type of the output.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> UnstableApp<T1, T2, T3, TResult>()
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
            => FnApplyCache<global::Eggnog.UnstableFn<T1, T2, T3, TResult>, TResult>.Of([typeof(T1), typeof(T2), typeof(T3)]);

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2 T3 T4)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="T4">The type of input 4.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>> UnstableFn<T1, T2, T3, T4, TResult>(TableHandle target)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>>.Of(target, [typeof(T1), typeof(T2), typeof(T3), typeof(T4)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>(content));

        /// <summary>
        /// <c>unstable-fn</c>: makes a function value of <paramref name="target"/>, of the sort <c>(UnstableFn (T1 T2 T3 T4)
        /// TResult)</c>, partially applied to the primitive's arguments, the target's parameters before the inputs. Pure:
        /// it makes the value anywhere, as egglog's does; a value that holds classes only where classes are interned
        /// (decisions row 9). Renders as <c>(unstable-fn "name" args)</c>, the target's name first, as egglog resolves it.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="T4">The type of input 4.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <returns>The primitive, one per target.</returns>
        /// <exception cref="ArgumentException">The target's parameters do not end with the inputs, or its result is not the
        /// output.</exception>
        public static PrimitiveN<global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>> UnstableFn<T1, T2, T3, T4, TResult>(Primitive target)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
            => FnPrimitiveCache<global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>>.Of(target, [typeof(T1), typeof(T2), typeof(T3), typeof(T4)], typeof(TResult), static content => new global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>(content));

        /// <summary>
        /// <c>unstable-app</c>: applies a function value of the sort <c>(UnstableFn (T1 T2 T3 T4) TResult)</c> to the inputs, its
        /// parameters after the value. Pure itself, it applies the value's function where the call site's context allows
        /// it, as egglog's <c>FunctionContainer::apply</c> dispatches: a constructor gets or makes its term where the context
        /// writes (a rule's head, a host write, a merge); a function reads its row where the context reads
        /// (a naive rule, a host read or write), none when there is no row; a primitive runs where its access is allowed
        /// (decisions row 12). Anywhere else the application fails: in a rule, make it naive (<c>Naive()</c>).
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="T4">The type of input 4.</typeparam>
        /// <typeparam name="TResult">The type of the output.</typeparam>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> UnstableApp<T1, T2, T3, T4, TResult>()
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
            => FnApplyCache<global::Eggnog.UnstableFn<T1, T2, T3, T4, TResult>, TResult>.Of([typeof(T1), typeof(T2), typeof(T3), typeof(T4)]);

    }

}
