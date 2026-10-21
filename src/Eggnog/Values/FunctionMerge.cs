using System;

namespace Eggnog
{

    /// <summary>
    /// Makes egglog's merges that are not lattices (<see cref="FunctionMerge{T}"/>): <c>:merge new</c>, <c>:merge old</c>,
    /// <c>:merge (f old new)</c> with a C# function, and <c>:merge</c> of any expression over <c>old</c> and
    /// <c>new</c>.
    /// </summary>
    public static class FunctionMerge
    {

        /// <summary>
        /// egglog's <c>:merge new</c>: the new value replaces the old.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The merge.</returns>
        public static FunctionMerge<T> New<T>() => new(FunctionMergeDefinition<T>.NewMerge);

        /// <summary>
        /// egglog's <c>:merge old</c>: the old value stays.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The merge.</returns>
        public static FunctionMerge<T> Old<T>() => new(FunctionMergeDefinition<T>.OldMerge);

        /// <summary>
        /// egglog's <c>:merge (f old new)</c>: any function of the old and the new value, such as <c>(+ old new)</c>; it
        /// need be neither associative, commutative nor idempotent. The writes fold in the order they come; in a parallel
        /// run, writes from different threads come in no fixed order, as in egglog.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="name">The merge's name, for messages.</param>
        /// <param name="merge">The function of the old and the new value: pure, deterministic and thread-safe.</param>
        /// <returns>The merge.</returns>
        public static FunctionMerge<T> Of<T>(string name, Func<T, T, T> merge)
        {
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(merge);
            return new(new FunctionMergeDefinition<T>(name, merge));
        }

        /// <summary>
        /// egglog's <c>:merge</c> of any expression over <c>old</c> and <c>new</c>: <paramref name="merge"/> builds it once
        /// from the two values, out of primitives (<c>Primitives.Max.Of(old, new)</c>), constants, and applications of
        /// functions and constructors (<c>join.Of(old, new)</c>), which the merge looks up or makes as egglog's does. The
        /// merge is checked and lowered when its function is defined, and named by its rendering, such as
        /// <c>(Join old new)</c>. A merge over classes may make terms; a function lookup with no row, or a primitive that
        /// throws or has no result, fails the merge, as egglog's merge panics, which fails a rule's run, or a host write's
        /// flush, and faults the scope. As every merge that is not a lattice, only the root writes its function.
        /// </summary>
        /// <typeparam name="T">The value type, or the sort.</typeparam>
        /// <param name="merge">Builds the merge from the old and the new value.</param>
        /// <returns>The merge.</returns>
        /// <exception cref="ArgumentException">The expression is <c>null</c>.</exception>
        public static FunctionMerge<T> Expression<T>(Func<Term<T>, Term<T>, Term<T>> merge)
        {
            ArgumentNullException.ThrowIfNull(merge);
            return new(new FunctionMergeExpression<T>(merge));
        }

    }

}
