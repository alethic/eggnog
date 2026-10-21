using System;
using System.Globalization;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// Named built-in primitives, with egglog's names: egglog's <c>i64</c> primitives, over <c>long</c>, and the set
    /// primitives, made once per element type. The other value types' primitives are in <see cref="DoublePrimitives"/>,
    /// <see cref="BigIntegerPrimitives"/>, <see cref="BigRationalPrimitives"/>, <see cref="BooleanPrimitives"/> and
    /// <see cref="StringPrimitives"/>.
    /// </summary>
    /// <remarks>
    /// The <c>i64</c> primitives are partial where egglog's are (<see cref="Primitive.IsPartial"/>): checked arithmetic
    /// has no result on overflow, division and remainder none by zero, shifts none by a count outside 0 to 63, and
    /// <see cref="Log2"/> none for a value below 1, as in egglog 3.0.0. No result drops the match in a rule body and
    /// fails the rule in a head. A partial primitive's <c>Function</c> throws where there is no result, what .NET's own
    /// operation throws: <see cref="OverflowException"/> for an overflow, <see cref="DivideByZeroException"/> for a zero
    /// divisor, and <see cref="InvalidOperationException"/> for a shift count or a <see cref="Log2"/> out of range. The
    /// comparisons return a <c>bool</c>, which is egglog's <c>bool-&lt;</c> family as a value and its <c>&lt;</c> family
    /// as a filter (<see cref="RuleBuilder.Where(Term{bool})"/>), and render as each.
    /// </remarks>
    public static class Primitives
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set of a value.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, ValueSet<T>> Singleton<T>() where T : notnull => PrimitiveCache<T>.Singleton;

        /// <summary>
        /// <c>set-of</c> of any number of elements, as egglog's; of none, the empty set, egglog's <c>set-empty</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static VariadicPrimitive<T, ValueSet<T>> SetOf<T>() where T : notnull => PrimitiveCache<T>.SetOf;

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Union<T>() where T : notnull => PrimitiveCache<T>.Union;

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Intersect<T>() where T : notnull => PrimitiveCache<T>.Intersect;

        /// <summary>
        /// <c>set-diff</c>: the elements of the first set not in the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Except<T>() where T : notnull => PrimitiveCache<T>.Except;

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, ValueSet<T>> Insert<T>() where T : notnull => PrimitiveCache<T>.Insert;

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, ValueSet<T>> Remove<T>() where T : notnull => PrimitiveCache<T>.Remove;

        /// <summary>
        /// <c>set-contains</c>: egglog's filter, which renders so; as a <c>bool</c> value, an addition, it renders as
        /// <c>bool-set-contains</c>, a name egglog does not have, since egglog's <c>set-contains</c> is the unit value or
        /// nothing.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, bool> Contains<T>() where T : notnull => PrimitiveCache<T>.Contains;

        /// <summary>
        /// <c>set-not-contains</c>: egglog's filter, which renders so; as a <c>bool</c> value, an addition, it renders as
        /// <c>bool-set-not-contains</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, bool> NotContains<T>() where T : notnull => PrimitiveCache<T>.NotContains;

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, bool> IsSubsetOf<T>() where T : notnull => PrimitiveCache<T>.IsSubsetOf;

        /// <summary>
        /// <c>set-is-empty</c>: whether the set is empty, an addition: egglog writes <c>(= (set-length s) 0)</c>, and its
        /// <c>set-empty</c> is the empty set, <see cref="ValueSet{T}.Empty"/>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, bool> IsEmpty<T>() where T : notnull => PrimitiveCache<T>.IsEmpty;

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, long> Count<T>() where T : notnull => PrimitiveCache<T>.Count;

        /// <summary>
        /// <c>bool-!=</c>: whether two values differ, as a <c>bool</c>; as a filter, egglog's <c>!=</c>. Generic over
        /// every type, as egglog's is over every sort: two classes compare by their canonical ids, as egglog compares
        /// representations (classes are also told apart with <see cref="Term{T}.IsNot(Term{T})"/>).
        /// </summary>
        /// <typeparam name="T">The value type or sort.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, bool> NotEqual<T>() where T : notnull => PrimitiveCache<T>.NotEqual;

        /// <summary>
        /// <c>pair</c>: the pair of two values, egglog's (src/sort/pair.rs:122-129). Its type is registered with
        /// <see cref="ValueStore.RegisterPair{T1, T2}"/>.
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, Pair<T1, T2>> PairOf<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.Make;

        /// <summary>
        /// <c>pair-first</c>: a pair's first element (pair.rs:131).
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Pair<T1, T2>, T1> PairFirst<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.First;

        /// <summary>
        /// <c>pair-second</c>: a pair's second element (pair.rs:132).
        /// </summary>
        /// <typeparam name="T1">The type of the first element.</typeparam>
        /// <typeparam name="T2">The type of the second element.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<Pair<T1, T2>, T2> PairSecond<T1, T2>()
            where T1 : notnull
            where T2 : notnull => PairPrimitiveCache<T1, T2>.Second;

        /// <summary>
        /// <c>ordering-min</c>: of two values, the one whose cell is the smaller, as egglog's compares the values'
        /// representations. A cell is a small integer itself, or an intern index, as egglog's representation is, so the
        /// order is that of interning, not of the values. It runs only in a rule: its <c>Function</c> throws
        /// <see cref="InvalidOperationException"/>, since the cells are the graph's.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, T> OrderingMin<T>() where T : notnull => PrimitiveCache<T>.OrderingMin;

        /// <summary>
        /// <c>ordering-max</c>: of two values, the one whose cell is the larger (<see cref="OrderingMin{T}"/>).
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, T, T> OrderingMax<T>() where T : notnull => PrimitiveCache<T>.OrderingMax;

        /// <summary>
        /// Gets <c>+</c> over <c>i64</c>: partial, like egglog's (<c>checked_add</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails.
        /// <see cref="Primitive{T1, T2, TResult}.Function"/> throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Add { get; } = new("+", static (a, b) => checked(a + b), TryAdd);

        /// <summary>
        /// Gets <c>-</c> over <c>i64</c>: partial, like egglog's (<c>checked_sub</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails.
        /// <see cref="Primitive{T1, T2, TResult}.Function"/> throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Subtract { get; } = new("-", static (a, b) => checked(a - b), TrySubtract);

        /// <summary>
        /// Gets <c>*</c> over <c>i64</c>: partial, like egglog's (<c>checked_mul</c>); an overflow has no result.
        /// </summary>
        public static Primitive<long, long, long> Multiply { get; } =
            BuiltInPrimitive.Partial<long, long, long>("*", static (a, b) => checked(a * b), TryMultiply);

        /// <summary>
        /// Gets <c>/</c> over <c>i64</c>, truncating toward zero: partial, like egglog's (<c>checked_div</c>); a zero
        /// divisor, or <c>long.MinValue / -1</c>, has no result.
        /// </summary>
        public static Primitive<long, long, long> Divide { get; } =
            BuiltInPrimitive.Partial<long, long, long>("/", static (a, b) => CheckDivision(a, b) / b, TryDivide);

        /// <summary>
        /// Gets <c>%</c> over <c>i64</c>, with the dividend's sign: partial, like egglog's (<c>checked_rem</c>); a zero
        /// divisor, or <c>long.MinValue % -1</c>, has no result.
        /// </summary>
        public static Primitive<long, long, long> Remainder { get; } =
            BuiltInPrimitive.Partial<long, long, long>("%", static (a, b) => CheckDivision(a, b) % b, TryRemainder);

        /// <summary>
        /// Gets <c>&amp;</c> over <c>i64</c>: bitwise and.
        /// </summary>
        public static Primitive<long, long, long> BitwiseAnd { get; } = BuiltInPrimitive.Total<long, long, long>("&", static (a, b) => a & b);

        /// <summary>
        /// Gets <c>|</c> over <c>i64</c>: bitwise or.
        /// </summary>
        public static Primitive<long, long, long> BitwiseOr { get; } = BuiltInPrimitive.Total<long, long, long>("|", static (a, b) => a | b);

        /// <summary>
        /// Gets <c>^</c> over <c>i64</c>: bitwise exclusive or (not a power, which <c>^</c> is over <c>f64</c>).
        /// </summary>
        public static Primitive<long, long, long> BitwiseXor { get; } = BuiltInPrimitive.Total<long, long, long>("^", static (a, b) => a ^ b);

        /// <summary>
        /// Gets <c>&lt;&lt;</c> over <c>i64</c>: partial, like egglog's (<c>checked_shl</c>); a count outside 0 to 63 has
        /// no result. Bits shifted out are lost, as in egglog.
        /// </summary>
        public static Primitive<long, long, long> ShiftLeft { get; } = BuiltInPrimitive.Partial<long, long, long>("<<", TryShiftLeft);

        /// <summary>
        /// Gets <c>&gt;&gt;</c> over <c>i64</c>, an arithmetic shift: partial, like egglog's (<c>checked_shr</c>); a count
        /// outside 0 to 63 has no result.
        /// </summary>
        public static Primitive<long, long, long> ShiftRight { get; } = BuiltInPrimitive.Partial<long, long, long>(">>", TryShiftRight);

        /// <summary>
        /// Gets <c>not-i64</c>: bitwise complement.
        /// </summary>
        public static Primitive<long, long> BitwiseNot { get; } = BuiltInPrimitive.Total<long, long>("not-i64", static a => ~a);

        /// <summary>
        /// Gets <c>log2</c> over <c>i64</c>: the floor of the base-2 logarithm. Partial, like egglog 3.0.0's
        /// (<c>checked_ilog2</c>): a value below 1 has no result.
        /// </summary>
        public static Primitive<long, long> Log2 { get; } = BuiltInPrimitive.Partial<long, long>("log2", TryLog2);

        /// <summary>
        /// Gets <c>abs</c> over <c>i64</c>: partial, like egglog's (<c>checked_abs</c>); <c>long.MinValue</c> has no
        /// result.
        /// </summary>
        public static Primitive<long, long> Abs { get; } = BuiltInPrimitive.Partial<long, long>("abs", static a => Math.Abs(a), TryAbs);

        /// <summary>
        /// Gets <c>max</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Max { get; } = new("max", static (a, b) => Math.Max(a, b), false);

        /// <summary>
        /// Gets <c>min</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Min { get; } = new("min", static (a, b) => Math.Min(a, b), false);

        /// <summary>
        /// Gets <c>bool-=</c> over <c>i64</c>: whether the two are equal, as a <c>bool</c>.
        /// </summary>
        public static Primitive<long, long, bool> Equal { get; } = BuiltInPrimitive.Total<long, long, bool>("bool-=", static (a, b) => a == b);

        /// <summary>
        /// Gets <c>bool-&lt;</c> over <c>i64</c>, whose filter is egglog's <c>&lt;</c>.
        /// </summary>
        public static Primitive<long, long, bool> Less { get; } = BuiltInPrimitive.Comparison<long>("<", "bool-<", static (a, b) => a < b);

        /// <summary>
        /// Gets <c>bool-&lt;=</c> over <c>i64</c>, whose filter is egglog's <c>&lt;=</c>.
        /// </summary>
        public static Primitive<long, long, bool> LessOrEqual { get; } = BuiltInPrimitive.Comparison<long>("<=", "bool-<=", static (a, b) => a <= b);

        /// <summary>
        /// Gets <c>bool-&gt;</c> over <c>i64</c>, whose filter is egglog's <c>&gt;</c>.
        /// </summary>
        public static Primitive<long, long, bool> Greater { get; } = BuiltInPrimitive.Comparison<long>(">", "bool->", static (a, b) => a > b);

        /// <summary>
        /// Gets <c>bool-&gt;=</c> over <c>i64</c>, whose filter is egglog's <c>&gt;=</c>.
        /// </summary>
        public static Primitive<long, long, bool> GreaterOrEqual { get; } = BuiltInPrimitive.Comparison<long>(">=", "bool->=", static (a, b) => a >= b);

        /// <summary>
        /// Gets <c>to-f64</c>: the nearest <c>double</c>, as Rust's <c>as f64</c>.
        /// </summary>
        public static Primitive<long, double> ToDouble { get; } = BuiltInPrimitive.Total<long, double>("to-f64", static a => a);

        /// <summary>
        /// Gets <c>to-string</c> over <c>i64</c>: the decimal digits, with a leading <c>-</c> when negative.
        /// </summary>
        public static Primitive<long, string> Format { get; } =
            BuiltInPrimitive.Total<long, string>("to-string", static a => a.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Gets the internal <c>is-true</c> filter a rule body applies to a <see cref="Fact.True"/> condition that is
        /// not a computation.
        /// </summary>
        internal static Primitive<bool, bool> IsTrue { get; } = new("is-true", static b => b, false);

        /// <summary>
        /// <c>checked_add</c>: no result on overflow.
        /// </summary>
        static bool TryAdd(long a, long b, out long result)
        {
            result = unchecked(a + b);
            return ((a ^ result) & (b ^ result)) >= 0;
        }

        /// <summary>
        /// <c>checked_sub</c>: no result on overflow.
        /// </summary>
        static bool TrySubtract(long a, long b, out long result)
        {
            result = unchecked(a - b);
            return ((a ^ b) & (a ^ result)) >= 0;
        }

        /// <summary>
        /// <c>checked_mul</c>: no result when the 128-bit product does not fit.
        /// </summary>
        static bool TryMultiply(long a, long b, out long result)
        {
            var high = Math.BigMul(a, b, out result);
            return high == result >> 63;
        }

        /// <summary>
        /// The dividend of a division whose function throws where <c>checked_div</c> and <c>checked_rem</c> have no
        /// result: <see cref="DivideByZeroException"/> for a zero divisor, <see cref="OverflowException"/> for
        /// <c>long.MinValue</c> by -1, which .NET's remainder does not refuse.
        /// </summary>
        static long CheckDivision(long a, long b)
        {
            if (b == 0)
                throw new DivideByZeroException();

            if (a == long.MinValue && b == -1)
                throw new OverflowException("long.MinValue divided by -1 overflows.");

            return a;
        }

        /// <summary>
        /// <c>checked_div</c>: no result for a zero divisor or <c>long.MinValue / -1</c>.
        /// </summary>
        static bool TryDivide(long a, long b, out long result)
        {
            if (b == 0 || (a == long.MinValue && b == -1))
            {
                result = 0;
                return false;
            }

            result = a / b;
            return true;
        }

        /// <summary>
        /// <c>checked_rem</c>: no result for a zero divisor or <c>long.MinValue % -1</c>.
        /// </summary>
        static bool TryRemainder(long a, long b, out long result)
        {
            if (b == 0 || (a == long.MinValue && b == -1))
            {
                result = 0;
                return false;
            }

            result = a % b;
            return true;
        }

        /// <summary>
        /// egglog's <c>&lt;&lt;</c>: the count converts to <c>u32</c> and <c>checked_shl</c> takes it below 64.
        /// </summary>
        static bool TryShiftLeft(long a, long b, out long result)
        {
            if (b < 0 || b >= 64)
            {
                result = 0;
                return false;
            }

            result = a << (int)b;
            return true;
        }

        /// <summary>
        /// egglog's <c>&gt;&gt;</c>: the count converts to <c>u32</c> and <c>checked_shr</c> takes it below 64.
        /// </summary>
        static bool TryShiftRight(long a, long b, out long result)
        {
            if (b < 0 || b >= 64)
            {
                result = 0;
                return false;
            }

            result = a >> (int)b;
            return true;
        }

        /// <summary>
        /// <c>checked_ilog2</c>: no result below 1.
        /// </summary>
        static bool TryLog2(long a, out long result)
        {
            if (a <= 0)
            {
                result = 0;
                return false;
            }

            result = 63 - BitOperations.LeadingZeroCount((ulong)a);
            return true;
        }

        /// <summary>
        /// <c>checked_abs</c>: no result for <c>long.MinValue</c>.
        /// </summary>
        static bool TryAbs(long a, out long result)
        {
            if (a == long.MinValue)
            {
                result = 0;
                return false;
            }

            result = Math.Abs(a);
            return true;
        }

    }

}
