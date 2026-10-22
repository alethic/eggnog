using System;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The <c>unstable-app</c> primitive of a function sort's value type: its parameters the value then the inputs, its
    /// result the output (<see cref="FnApply"/>).
    /// </summary>
    /// <typeparam name="TFn">The function sort's value type.</typeparam>
    /// <typeparam name="TResult">The output's type.</typeparam>
    internal static class FnApplyCache<TFn, TResult>
        where TFn : struct, IFnValue
    {

        static PrimitiveN<TResult>? apply;

        /// <summary>
        /// Gets the primitive.
        /// </summary>
        /// <param name="inputs">The CLR types of the inputs.</param>
        /// <returns>The primitive.</returns>
        public static PrimitiveN<TResult> Of(Type[] inputs)
        {
            if (Volatile.Read(ref apply) is { } made)
                return made;

            Interlocked.CompareExchange(ref apply, new PrimitiveN<TResult>("unstable-app", [typeof(TFn), .. inputs], FnApplyBody.Instance), null);
            return apply;
        }

    }

}
