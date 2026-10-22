using System;
using System.Runtime.CompilerServices;

namespace Eggnog
{

    /// <summary>
    /// The <c>unstable-fn</c> primitives of a function sort's value type: one per function, made once and kept while the
    /// function lives, so that a rule's terms of one function share their primitive.
    /// </summary>
    /// <typeparam name="TFn">The function sort's value type.</typeparam>
    internal static class FnPrimitiveCache<TFn>
        where TFn : struct, IFnValue
    {

        static readonly ConditionalWeakTable<object, PrimitiveN<TFn>> ByTarget = new();

        /// <summary>
        /// Gets the <c>unstable-fn</c> primitive of a function: its parameters the function's partially applied
        /// arguments, its result the function value.
        /// </summary>
        /// <param name="target">A constructor or a function, or a primitive.</param>
        /// <param name="inputs">The CLR types of the function sort's inputs.</param>
        /// <param name="output">The CLR type of its output.</param>
        /// <param name="wrap">Makes a value of a content.</param>
        /// <returns>The primitive.</returns>
        /// <exception cref="ArgumentException">The target is a relation, or its signature does not end with the inputs
        /// and the output.</exception>
        public static PrimitiveN<TFn> Of(object target, Type[] inputs, Type output, Func<FnValue, TFn> wrap)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (ByTarget.TryGetValue(target, out var known))
                return known;

            var partial = FnValue.PartialTypes(target, inputs, output);
            var name = target is TableHandle table ? table.Name : ((Primitive)target).Name;
            var made = new PrimitiveN<TFn>("unstable-fn", Relations.LiteralText.String(name), partial,
                arguments => wrap(FnValue.Of(target, arguments.ToArray())));
            return ByTarget.GetValue(target, _ => made);
        }

    }

}
