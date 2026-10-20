using System;

namespace Eggnog
{

    /// <summary>
    /// Makes egglog's merges that are not lattices (<see cref="FunctionMerge{T}"/>): <c>:merge new</c>, <c>:merge old</c>
    /// and <c>:merge (f old new)</c>.
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

    }

}
