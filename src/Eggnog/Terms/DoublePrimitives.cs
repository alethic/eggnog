using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>f64</c> primitives over <c>double</c>, with egglog's names.
    /// </summary>
    /// <remarks>
    /// <see cref="Divide"/>, <see cref="Remainder"/>, <see cref="Log"/> and <see cref="Sqrt"/> are partial where egglog's
    /// are (<see cref="Primitive.IsPartial"/>): a zero divisor, a logarithm of a value not above 0 and a square root of a
    /// negative value have no result. Every primitive here whose result can be NaN is partial there too, a departure:
    /// egglog's <c>f64</c> holds NaN, and no Eggnog column does (<see cref="ValueStore"/>), so a NaN result
    /// (<c>inf - inf</c>, <c>0 * inf</c>, a negative base to a fractional power) has no result. No result drops the match
    /// in a rule body and fails the rule in a head; a partial primitive's <c>Function</c> throws
    /// <see cref="InvalidOperationException"/> there. A result of <c>-0.0</c> is stored as <c>0.0</c>, which egglog
    /// treats as equal to it. The comparisons return a <c>bool</c>; as a filter (<see cref="RuleBuilder.Where(Term{bool})"/>)
    /// they are egglog's <c>&lt;</c> family.
    /// </remarks>
    public static class DoublePrimitives
    {

        /// <summary>
        /// Gets <c>+</c> over <c>f64</c>; a NaN result has no result.
        /// </summary>
        public static Primitive<double, double, double> Add { get; } =
            BuiltInPrimitive.Partial<double, double, double>("+", static (double a, double b, out double r) => NotNaN(a + b, out r));

        /// <summary>
        /// Gets <c>-</c> over <c>f64</c>; a NaN result has no result.
        /// </summary>
        public static Primitive<double, double, double> Subtract { get; } =
            BuiltInPrimitive.Partial<double, double, double>("-", static (double a, double b, out double r) => NotNaN(a - b, out r));

        /// <summary>
        /// Gets <c>*</c> over <c>f64</c>; a NaN result has no result.
        /// </summary>
        public static Primitive<double, double, double> Multiply { get; } =
            BuiltInPrimitive.Partial<double, double, double>("*", static (double a, double b, out double r) => NotNaN(a * b, out r));

        /// <summary>
        /// Gets <c>/</c> over <c>f64</c>: partial, like egglog's; a zero divisor, or a NaN result, has no result.
        /// </summary>
        public static Primitive<double, double, double> Divide { get; } =
            BuiltInPrimitive.Partial<double, double, double>("/", static (double a, double b, out double r) => NonZero(b, a / b, out r));

        /// <summary>
        /// Gets <c>%</c> over <c>f64</c>, with the dividend's sign (C's <c>fmod</c>, as Rust's): partial, like egglog's;
        /// a zero divisor, or a NaN result, has no result.
        /// </summary>
        public static Primitive<double, double, double> Remainder { get; } =
            BuiltInPrimitive.Partial<double, double, double>("%", static (double a, double b, out double r) => NonZero(b, a % b, out r));

        /// <summary>
        /// Gets <c>^</c> over <c>f64</c>: the first to the power of the second (Rust's <c>powf</c>); a NaN result has no
        /// result.
        /// </summary>
        public static Primitive<double, double, double> Power { get; } =
            BuiltInPrimitive.Partial<double, double, double>("^", static (double a, double b, out double r) => NotNaN(Math.Pow(a, b), out r));

        /// <summary>
        /// Gets <c>neg</c>: the negation.
        /// </summary>
        public static Primitive<double, double> Negate { get; } = BuiltInPrimitive.Total<double, double>("neg", static a => -a);

        /// <summary>
        /// Gets <c>abs</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double> Abs { get; } = BuiltInPrimitive.Total<double, double>("abs", Math.Abs);

        /// <summary>
        /// Gets <c>min</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, double> Min { get; } = BuiltInPrimitive.Total<double, double, double>("min", Math.Min);

        /// <summary>
        /// Gets <c>max</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, double> Max { get; } = BuiltInPrimitive.Total<double, double, double>("max", Math.Max);

        /// <summary>
        /// Gets <c>exp</c>: e to the power of the value.
        /// </summary>
        public static Primitive<double, double> Exp { get; } = BuiltInPrimitive.Total<double, double>("exp", Math.Exp);

        /// <summary>
        /// Gets <c>log</c>: the natural logarithm. Partial, like egglog's: a value not above 0 has no result.
        /// </summary>
        public static Primitive<double, double> Log { get; } =
            BuiltInPrimitive.Partial<double, double>("log", static (double a, out double r) => Defined(a > 0, Math.Log(a), out r));

        /// <summary>
        /// Gets <c>sqrt</c>: the square root. Partial, like egglog's: a negative value has no result.
        /// </summary>
        public static Primitive<double, double> Sqrt { get; } =
            BuiltInPrimitive.Partial<double, double>("sqrt", static (double a, out double r) => Defined(a >= 0, Math.Sqrt(a), out r));

        /// <summary>
        /// Gets <c>&lt;</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, bool> Less { get; } = BuiltInPrimitive.Total<double, double, bool>("<", static (a, b) => a < b);

        /// <summary>
        /// Gets <c>&lt;=</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, bool> LessOrEqual { get; } = BuiltInPrimitive.Total<double, double, bool>("<=", static (a, b) => a <= b);

        /// <summary>
        /// Gets <c>&gt;</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, bool> Greater { get; } = BuiltInPrimitive.Total<double, double, bool>(">", static (a, b) => a > b);

        /// <summary>
        /// Gets <c>&gt;=</c> over <c>f64</c>.
        /// </summary>
        public static Primitive<double, double, bool> GreaterOrEqual { get; } = BuiltInPrimitive.Total<double, double, bool>(">=", static (a, b) => a >= b);

        /// <summary>
        /// Gets <c>to-i64</c>: the value truncated toward zero, saturating at <c>long.MinValue</c> and
        /// <c>long.MaxValue</c>, as Rust's <c>as i64</c>.
        /// </summary>
        public static Primitive<double, long> ToInt64 { get; } = BuiltInPrimitive.Total<double, long>("to-i64", Truncate);

        /// <summary>
        /// Gets <c>to-string</c> over <c>f64</c>: Rust's <c>Debug</c> text, as egglog's, which always shows a decimal
        /// point or an exponent (<c>1.0</c>, <c>0.1</c>, <c>1e16</c>, <c>inf</c>).
        /// </summary>
        public static Primitive<double, string> Format { get; } = BuiltInPrimitive.Total<double, string>("to-string", Relations.RustFloatDebug.Format);

        /// <summary>
        /// A result unless it is NaN.
        /// </summary>
        static bool NotNaN(double value, out double result)
        {
            result = value;
            return double.IsNaN(value) == false;
        }

        /// <summary>
        /// A quotient or remainder unless the divisor is zero or the result is NaN.
        /// </summary>
        static bool NonZero(double divisor, double value, out double result) => Defined(divisor != 0, value, out result);

        /// <summary>
        /// A result where it is defined, unless it is NaN.
        /// </summary>
        static bool Defined(bool defined, double value, out double result)
        {
            result = defined ? value : 0;
            return defined && double.IsNaN(value) == false;
        }

        /// <summary>
        /// Rust's <c>as i64</c> of an <c>f64</c>: truncation toward zero, saturating at the ends (NaN, which no column
        /// holds, would be 0). The bounds are tested first because .NET's own conversion of an out-of-range value
        /// depends on the runtime.
        /// </summary>
        static long Truncate(double value)
        {
            if (value >= 9223372036854775808.0)
                return long.MaxValue;

            if (value <= -9223372036854775808.0)
                return long.MinValue;

            return double.IsNaN(value) ? 0 : (long)value;
        }

    }

}
