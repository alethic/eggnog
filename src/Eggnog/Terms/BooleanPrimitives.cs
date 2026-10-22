using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>bool</c> primitives, with egglog's names. <see cref="Or"/> takes any number of arguments, as egglog's
    /// does. egglog's <c>guard</c> is a filter (<see cref="RuleBuilder.Where(Term{bool})"/>), which renders as it.
    /// </summary>
    public static class BooleanPrimitives
    {

        /// <summary>
        /// Gets <c>not</c>.
        /// </summary>
        public static Primitive<bool, bool> Not { get; } = BuiltInPrimitive.Total<bool, bool>("not", static a => !a);

        /// <summary>
        /// Gets <c>and</c>.
        /// </summary>
        public static Primitive<bool, bool, bool> And { get; } = BuiltInPrimitive.Total<bool, bool, bool>("and", static (a, b) => a && b);

        /// <summary>
        /// Gets <c>guard</c>: egglog's fallible primitive of a <c>bool</c>, a partial primitive of <c>Unit</c> with a
        /// result, <c>()</c>, where it is true (bool.rs:33); what a filter over another <c>bool</c> renders as.
        /// </summary>
        public static Primitive<bool, ValueTuple> Guard { get; } = BuiltInPrimitive.Partial<bool, ValueTuple>("guard", static (bool a, out ValueTuple result) =>
        {
            result = default;
            return a;
        });

        /// <summary>
        /// Gets <c>or</c> of any number of arguments; of none, <c>false</c>.
        /// </summary>
        public static VariadicPrimitive<bool, bool> Or { get; } = new("or", static xs =>
        {
            foreach (var x in xs)
                if (x)
                    return true;

            return false;
        });

        /// <summary>
        /// Gets <c>xor</c>.
        /// </summary>
        public static Primitive<bool, bool, bool> Xor { get; } = BuiltInPrimitive.Total<bool, bool, bool>("xor", static (a, b) => a ^ b);

        /// <summary>
        /// Gets <c>=&gt;</c>: implication, <c>!a || b</c>.
        /// </summary>
        public static Primitive<bool, bool, bool> Implies { get; } = BuiltInPrimitive.Total<bool, bool, bool>("=>", static (a, b) => !a || b);

    }

}
