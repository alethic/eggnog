using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A function value of 4 inputs: egglog's <c>(UnstableFn (T1 T2 T3 T4) TResult)</c> sort (src/sort/fn.rs), whose
    /// column type <see cref="ValueStore.RegisterFn{T1, T2, T3, T4, TResult}"/> registers. It applies a constructor, a function or a primitive,
    /// partially applied to <see cref="Arguments"/>, its first parameters, to the inputs:
    /// <see cref="Primitives.UnstableApp{T1, T2, T3, T4, TResult}"/>, egglog's <c>unstable-app</c>; <see cref="Primitives.UnstableFn{T1, T2, T3, T4, TResult}(TableHandle)"/>
    /// makes one in a term, egglog's <c>unstable-fn</c>, and <see cref="UnstableFn.Of{T1, T2, T3, T4, TResult}(TableHandle, ReadOnlySpan{object})"/>
    /// on the host. Equal by function and arguments, as egglog's are. Immutable.
    /// </summary>
    /// <typeparam name="T1">The type of input 1.</typeparam>
    /// <typeparam name="T2">The type of input 2.</typeparam>
    /// <typeparam name="T3">The type of input 3.</typeparam>
    /// <typeparam name="T4">The type of input 4.</typeparam>
    /// <typeparam name="TResult">The type of the output.</typeparam>
    public readonly struct UnstableFn<T1, T2, T3, T4, TResult> : IEquatable<UnstableFn<T1, T2, T3, T4, TResult>>, IComparable<UnstableFn<T1, T2, T3, T4, TResult>>, IFnValue
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull
        where T4 : notnull
        where TResult : notnull
    {

        readonly FnValue? content;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="content">The content.</param>
        internal UnstableFn(FnValue content)
        {
            this.content = content;
        }

        /// <summary>
        /// Gets the function applied: a <see cref="TableHandle"/> or a <see cref="Primitive"/>; <c>null</c> for a default
        /// value.
        /// </summary>
        public object? Target => content?.Target;

        /// <summary>
        /// Gets the name of the function applied, egglog's: the table's or the primitive's; empty for a default value.
        /// </summary>
        public string Name => content?.Name ?? string.Empty;

        /// <summary>
        /// Gets the partially applied arguments, the function's first parameters.
        /// </summary>
        public IReadOnlyList<object> Arguments => content?.Arguments ?? Array.Empty<object>();

        /// <inheritdoc />
        FnValue? IFnValue.Content => content;

        /// <inheritdoc />
        public bool Equals(UnstableFn<T1, T2, T3, T4, TResult> other) => content is null ? other.content is null : content.Equals(other.content);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is UnstableFn<T1, T2, T3, T4, TResult> other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => content?.GetHashCode() ?? 0;

        /// <summary>
        /// Compares in content order: by name, by code point, then argument by argument as <see cref="Set{T}"/> orders
        /// its elements; a default value first.
        /// </summary>
        /// <param name="other">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An argument is not comparable.</exception>
        public int CompareTo(UnstableFn<T1, T2, T3, T4, TResult> other) =>
            content is null ? (other.content is null ? 0 : -1) : other.content is null ? 1 : content.CompareTo(other.content);

        /// <summary>
        /// Renders the value as egglog writes its term: <c>(unstable-fn "name" args)</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => content?.ToString() ?? "(unstable-fn)";

        /// <summary>
        /// Gets whether two function values are equal.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they are equal.</returns>
        public static bool operator ==(UnstableFn<T1, T2, T3, T4, TResult> left, UnstableFn<T1, T2, T3, T4, TResult> right) => left.Equals(right);

        /// <summary>
        /// Gets whether two function values differ.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they differ.</returns>
        public static bool operator !=(UnstableFn<T1, T2, T3, T4, TResult> left, UnstableFn<T1, T2, T3, T4, TResult> right) => left.Equals(right) == false;

    }

}
