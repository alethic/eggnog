namespace Eggnog
{

    /// <summary>
    /// egglog's <c>bool</c> primitives, with egglog's names. egglog's <c>or</c> takes any number of arguments; here
    /// <see cref="Or"/> takes two, and more nest. egglog's <c>guard</c> is a filter
    /// (<see cref="RuleBuilder.Where(Term{bool})"/>).
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
        /// Gets <c>or</c> of two.
        /// </summary>
        public static Primitive<bool, bool, bool> Or { get; } = BuiltInPrimitive.Total<bool, bool, bool>("or", static (a, b) => a || b);

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
