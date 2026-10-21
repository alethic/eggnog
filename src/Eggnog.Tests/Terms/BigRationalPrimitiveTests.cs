using System;
using System.Globalization;
using System.Numerics;
using System.Text;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>BigRat</c> primitives in <see cref="BigRationalPrimitives"/>: their egglog names, which are partial,
    /// and their values, which follow num-rational as egglog uses it (<c>src/sort/bigrat.rs</c>); and the
    /// <c>BigRat</c> column type, which renders and serializes as egglog's.
    /// </summary>
    public sealed class BigRationalPrimitiveTests
    {

        public static TheoryData<Primitive, string, bool> Names => new()
        {
            { BigRationalPrimitives.Add, "+", true },
            { BigRationalPrimitives.Subtract, "-", true },
            { BigRationalPrimitives.Multiply, "*", true },
            { BigRationalPrimitives.Divide, "/", true },
            { BigRationalPrimitives.Min, "min", false },
            { BigRationalPrimitives.Max, "max", false },
            { BigRationalPrimitives.Negate, "neg", false },
            { BigRationalPrimitives.Abs, "abs", false },
            { BigRationalPrimitives.Floor, "floor", false },
            { BigRationalPrimitives.Ceiling, "ceil", false },
            { BigRationalPrimitives.Round, "round", false },
            { BigRationalPrimitives.FromBigIntegers, "bigrat", true },
            { BigRationalPrimitives.Numerator, "numer", false },
            { BigRationalPrimitives.Denominator, "denom", false },
            { BigRationalPrimitives.ToDouble, "to-f64", false },
            { BigRationalPrimitives.ToInt64, "to-i64", true },
            { BigRationalPrimitives.Power, "pow", true },
            { BigRationalPrimitives.Log, "log", true },
            { BigRationalPrimitives.Sqrt, "sqrt", true },
            { BigRationalPrimitives.Cbrt, "cbrt", true },
            { BigRationalPrimitives.Less, "bool-<", false },
            { BigRationalPrimitives.LessOrEqual, "bool-<=", false },
            { BigRationalPrimitives.Greater, "bool->", false },
            { BigRationalPrimitives.GreaterOrEqual, "bool->=", false },
        };

        [Theory]
        [MemberData(nameof(Names))]
        public void EachPrimitiveHasEgglogsNameAndPartiality(Primitive primitive, string name, bool partial)
        {
            Assert.Equal(name, primitive.Name);
            Assert.Equal(partial, primitive.IsPartial);
        }

        [Fact]
        public void DivisionAndConstructionHaveNoResultForZero()
        {
            Assert.False(BigRationalPrimitives.Divide.TryInvoke(1, default, out _));
            Assert.Throws<DivideByZeroException>(() => BigRationalPrimitives.Divide.Function(1, default));
            Assert.False(BigRationalPrimitives.FromBigIntegers.TryInvoke(1, 0, out _));
            Assert.True(BigRationalPrimitives.FromBigIntegers.TryInvoke(-2, -6, out var third));
            Assert.Equal(new BigRational(1, 3), third);
            Assert.True(BigRationalPrimitives.Add.TryInvoke(third, third, out var sum));
            Assert.Equal(new BigRational(2, 3), sum);
        }

        [Theory]
        [InlineData(7, 2, 3, 4, 4)]
        [InlineData(-7, 2, -4, -3, -4)]
        [InlineData(5, 2, 2, 3, 3)]
        [InlineData(-5, 2, -3, -2, -3)]
        [InlineData(4, 3, 1, 2, 1)]
        [InlineData(-4, 3, -2, -1, -1)]
        [InlineData(5, 3, 1, 2, 2)]
        [InlineData(-5, 3, -2, -1, -2)]
        [InlineData(6, 3, 2, 2, 2)]
        [InlineData(0, 1, 0, 0, 0)]
        public void FloorCeilingAndRoundFollowNumRational(long numerator, long denominator, long floor, long ceiling, long round)
        {
            var value = new BigRational(numerator, denominator);

            Assert.Equal(new BigRational(floor), BigRationalPrimitives.Floor.Function(value));
            Assert.Equal(new BigRational(ceiling), BigRationalPrimitives.Ceiling.Function(value));
            Assert.Equal(new BigRational(round), BigRationalPrimitives.Round.Function(value));
        }

        [Fact]
        public void TheUnaryAndBinaryArithmeticIsExact()
        {
            var a = new BigRational(-3, 4);
            var b = new BigRational(5, 6);

            Assert.Equal(new BigRational(3, 4), BigRationalPrimitives.Abs.Function(a));
            Assert.Equal(new BigRational(3, 4), BigRationalPrimitives.Negate.Function(a));
            Assert.Equal(a, BigRationalPrimitives.Min.Function(a, b));
            Assert.Equal(b, BigRationalPrimitives.Max.Function(a, b));
            Assert.Equal(new BigRational(-19, 12), BigRationalPrimitives.Subtract.Function(a, b));
            Assert.Equal(new BigRational(-5, 8), BigRationalPrimitives.Multiply.Function(a, b));
            Assert.Equal(new BigRational(-9, 10), BigRationalPrimitives.Divide.Function(a, b));
            Assert.Equal(new BigInteger(-3), BigRationalPrimitives.Numerator.Function(a));
            Assert.Equal(new BigInteger(4), BigRationalPrimitives.Denominator.Function(a));
        }

        /// <summary>
        /// <c>to-f64</c> is the nearest <c>f64</c>, ties to even, as num-rational's <c>to_f64</c>: what .NET's parser,
        /// which rounds correctly, reads from the value's decimal expansion.
        /// </summary>
        [Theory]
        [InlineData("1", "10")]
        [InlineData("1", "3")]
        [InlineData("-2", "3")]
        [InlineData("9007199254740993", "1")]
        [InlineData("9007199254740995", "1")]
        [InlineData("123456789012345678901234567890123", "7")]
        [InlineData("1", "100000000000000000")]
        [InlineData("-31415926535897932384626433832795", "10000000000000000")]
        [InlineData("1", "3000000000000000000000000000000000000000")]
        public void ToDoubleIsTheNearestDouble(string numerator, string denominator)
        {
            var value = new BigRational(
                BigInteger.Parse(numerator, CultureInfo.InvariantCulture),
                BigInteger.Parse(denominator, CultureInfo.InvariantCulture));

            Assert.Equal(Nearest(value), BigRationalPrimitives.ToDouble.Function(value));
        }

        [Fact]
        public void ToDoubleRoundsHugeTinyAndSubnormalValues()
        {
            var ten = new BigInteger(10);

            Assert.Equal(1e308, BigRationalPrimitives.ToDouble.Function(new BigRational(BigInteger.Pow(ten, 309), 10)));
            Assert.Equal(double.PositiveInfinity, BigRationalPrimitives.ToDouble.Function(new BigRational(BigInteger.Pow(ten, 400), 3)));
            Assert.Equal(double.NegativeInfinity, BigRationalPrimitives.ToDouble.Function(new BigRational(-BigInteger.Pow(ten, 309))));
            Assert.Equal(1e-320, BigRationalPrimitives.ToDouble.Function(new BigRational(1, BigInteger.Pow(ten, 320))));
            Assert.Equal(-7e-310, BigRationalPrimitives.ToDouble.Function(new BigRational(-7, BigInteger.Pow(ten, 310))));
            Assert.Equal(double.Epsilon, BigRationalPrimitives.ToDouble.Function(new BigRational(1, BigInteger.One << 1074)));
            Assert.Equal(double.Epsilon, BigRationalPrimitives.ToDouble.Function(new BigRational(3, BigInteger.One << 1076)));
            Assert.Equal(0.0, BigRationalPrimitives.ToDouble.Function(new BigRational(1, BigInteger.One << 1075)));
            Assert.Equal(double.Epsilon * 2, BigRationalPrimitives.ToDouble.Function(new BigRational(3, BigInteger.One << 1075)));
            Assert.Equal(0.0, BigRationalPrimitives.ToDouble.Function(new BigRational(1, BigInteger.Pow(ten, 400))));
            Assert.Equal(double.MaxValue, BigRationalPrimitives.ToDouble.Function(new BigRational(new BigInteger(double.MaxValue))));
        }

        /// <summary>
        /// Random rationals across the range, from subnormal to near the largest <c>f64</c>, with numerators and
        /// denominators of up to 40 digits, convert to the nearest <c>f64</c>.
        /// </summary>
        [Fact]
        public void ToDoubleIsTheNearestDoubleAcrossTheRange()
        {
            var random = new Random(20261020);
            foreach (var exponent in new[] { -330, -320, -310, -300, -20, 0, 20, 290, 300, 307 })
            {
                for (var i = 0; i < 100; i++)
                {
                    var numerator = RandomInteger(random, random.Next(1, 40)) * (random.Next(2) == 0 ? 1 : -1);
                    var denominator = RandomInteger(random, random.Next(1, 40));
                    var scale = BigInteger.Pow(10, Math.Abs(exponent));
                    var value = exponent >= 0
                        ? new BigRational(numerator * scale, denominator)
                        : new BigRational(numerator, denominator * scale);

                    Assert.Equal(Nearest(value), BigRationalPrimitives.ToDouble.Function(value));
                }
            }
        }

        [Fact]
        public void ToInt64HasAResultOnlyForAnIntegerWithinI64()
        {
            Assert.True(BigRationalPrimitives.ToInt64.TryInvoke(new BigRational(-6, 3), out var two));
            Assert.Equal(-2L, two);
            Assert.True(BigRationalPrimitives.ToInt64.TryInvoke(new BigRational(long.MinValue), out var least));
            Assert.Equal(long.MinValue, least);
            Assert.False(BigRationalPrimitives.ToInt64.TryInvoke(new BigRational(1, 4), out _));
            Assert.False(BigRationalPrimitives.ToInt64.TryInvoke(new BigRational(new BigInteger(long.MaxValue) + 1), out _));
            Assert.Throws<InvalidOperationException>(() => BigRationalPrimitives.ToInt64.Function(new BigRational(1, 4)));
        }

        [Fact]
        public void PowerTakesIntegerPowersOnly()
        {
            var two = new BigRational(2);
            var negativeHalf = new BigRational(-1, 2);

            Assert.Equal(new BigRational(1, 8), BigRationalPrimitives.Power.Function(two, -3));
            Assert.Equal(new BigRational(-8), BigRationalPrimitives.Power.Function(negativeHalf, -3));
            Assert.Equal(new BigRational(1, 4), BigRationalPrimitives.Power.Function(negativeHalf, 2));
            Assert.Equal(new BigRational(-2, 3), BigRationalPrimitives.Power.Function(new BigRational(-3, 2), -1));
            Assert.Equal(new BigRational(1), BigRationalPrimitives.Power.Function(new BigRational(-5, 7), 0));
            Assert.False(BigRationalPrimitives.Power.TryInvoke(two, new BigRational(1, 2), out _));
            Assert.False(BigRationalPrimitives.Power.TryInvoke(default, new BigRational(1, 2), out _));
            Assert.False(BigRationalPrimitives.Power.TryInvoke(two, new BigRational(new BigInteger(long.MaxValue) + 1), out _));
        }

        /// <summary>
        /// A power of 1 or -1 of any size within <c>i64</c> has its result; anything else whose result no
        /// <see cref="BigInteger"/> holds throws <see cref="OverflowException"/>, where egglog runs out of memory.
        /// </summary>
        [Fact]
        public void PowerOfOneTakesAnyPowerAndALargerResultThrows()
        {
            Assert.Equal(new BigRational(1), BigRationalPrimitives.Power.Function(1, long.MaxValue));
            Assert.Equal(new BigRational(-1), BigRationalPrimitives.Power.Function(-1, long.MaxValue));
            Assert.Equal(new BigRational(-1), BigRationalPrimitives.Power.Function(-1, long.MinValue + 1));
            Assert.Equal(new BigRational(1), BigRationalPrimitives.Power.Function(-1, long.MinValue + 2));
            Assert.False(BigRationalPrimitives.Power.TryInvoke(-1, long.MinValue, out _));
            Assert.Throws<OverflowException>(() => BigRationalPrimitives.Power.Function(2, long.MaxValue));
            Assert.Throws<OverflowException>(() => BigRationalPrimitives.Power.Function(new BigRational(1, 2), int.MaxValue));
            Assert.Throws<OverflowException>(() => BigRationalPrimitives.Power.Function(3, 2_000_000_000));
            Assert.Throws<OverflowException>(() => BigRationalPrimitives.Power.Function(new BigRational(1, 3), -1_400_000_000));
            var expected = new BigRational(BigInteger.Pow(3, 1000), BigInteger.Pow(7, 1000));
            Assert.Equal(expected, BigRationalPrimitives.Power.Function(new BigRational(3, 7), 1000));
            Assert.Equal(new BigRational(BigInteger.Pow(-5, 777)), BigRationalPrimitives.Power.Function(new BigRational(-1, 5), -777));
        }

        [Fact]
        public void SqrtHasAResultForPositivePerfectSquaresOnly()
        {
            Assert.Equal(new BigRational(2, 3), BigRationalPrimitives.Sqrt.Function(new BigRational(4, 9)));
            var big = BigInteger.Pow(12345678901234567, 3);
            Assert.Equal(new BigRational(big, 5), BigRationalPrimitives.Sqrt.Function(new BigRational(big * big, 25)));
            Assert.Equal(new BigRational(1), BigRationalPrimitives.Sqrt.Function(1));
            Assert.False(BigRationalPrimitives.Sqrt.TryInvoke(default, out _));
            Assert.False(BigRationalPrimitives.Sqrt.TryInvoke(new BigRational(-4), out _));
            Assert.False(BigRationalPrimitives.Sqrt.TryInvoke(new BigRational(2), out _));
            Assert.False(BigRationalPrimitives.Sqrt.TryInvoke(new BigRational(4, 3), out _));
            Assert.False(BigRationalPrimitives.Sqrt.TryInvoke(new BigRational(big * big + 1), out _));
        }

        [Fact]
        public void LogAndCbrtHaveAResultAtOneAndAreUnfinishedElsewhere()
        {
            Assert.Equal(default(BigRational), BigRationalPrimitives.Log.Function(1));
            Assert.Equal(new BigRational(1), BigRationalPrimitives.Cbrt.Function(1));
            Assert.Throws<NotSupportedException>(() => BigRationalPrimitives.Log.Function(2));
            Assert.Throws<NotSupportedException>(() => BigRationalPrimitives.Cbrt.TryInvoke(8, out _));
        }

        [Fact]
        public void TheComparisonsReturnBooleansAndFilterAsEgglogs()
        {
            var third = new BigRational(1, 3);
            var half = new BigRational(1, 2);

            Assert.True(BigRationalPrimitives.Less.Function(third, half));
            Assert.False(BigRationalPrimitives.Greater.Function(third, half));
            Assert.True(BigRationalPrimitives.LessOrEqual.Function(half, half));
            Assert.True(BigRationalPrimitives.GreaterOrEqual.Function(half, third));
            Assert.Equal("<", BigRationalPrimitives.Less.FilterName);
            Assert.Equal(">=", BigRationalPrimitives.GreaterOrEqual.FilterName);
        }

        /// <summary>
        /// A <c>BigRat</c> computes in a rule's head, filters in a body, extracts as egglog reconstructs one,
        /// <c>(bigrat (from-string "n") (from-string "d"))</c>, and serializes as egglog prints one, num-rational's
        /// <c>Debug</c>.
        /// </summary>
        [Fact]
        public void ABigRatComputesInRulesExtractsAndSerializesAsEgglogs()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<BigRational, Node>("Num");
            var lit = graph.DefineRelation<long>("lit");
            var half = graph.DefineFunction<long, BigRational>("half");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("half", r =>
            {
                var (x, q) = r.Vars<long, BigRational>("x", "q");
                var rational = BigRationalPrimitives.FromBigIntegers.Of(
                    BigIntegerPrimitives.FromInt64.Of(x),
                    BigIntegerPrimitives.FromInt64.Of(1L));
                r.When(lit.Of(x), q.Is(rational));
                r.Where(BigRationalPrimitives.Less.Of(new BigRational(0), q));
                r.Then(half.Of(x).Is(BigRationalPrimitives.Divide.Of(q, new BigRational(2))));
            });
            graph.Root.Assume(lit.Of(3L), lit.Of(-1L));
            var three = graph.Root.Make(num.Of(new BigRational(3)));
            var fraction = graph.Root.Make(num.Of(new BigRational(-1, 4)));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.Equal(new BigRational(3, 2), graph.Root.Get(half.Of(3L)));
            Assert.False(graph.Root.TryGet(half.Of(-1L), out _));
            Assert.Equal("(Num (bigrat (from-string \"3\") (from-string \"1\")))", graph.Root.Extract(three).ToString());
            Assert.Equal("(Num (bigrat (from-string \"-1\") (from-string \"4\")))", graph.Root.Extract(fraction).ToString());
            var serialized = graph.Root.Serialize();
            Assert.Contains(serialized.Nodes, n => n.Op == "Ratio { numer: 3, denom: 2 }");
            Assert.Contains(serialized.Nodes, n => n.Op == "Ratio { numer: -1, denom: 4 }");
            Assert.Equal("BigRat", graph.Values.TypeOf<BigRational>().Name);
            Assert.Throws<ArgumentException>(() => graph.Values.Register<BigRational>("Rational"));
        }

        /// <summary>
        /// The nearest <c>f64</c> to a rational, as .NET's correctly rounding parser reads its decimal expansion: exact
        /// where the expansion ends, which a tie's does, and otherwise 1,200 places, past where any of these values
        /// could lie between a double and a tie.
        /// </summary>
        static double Nearest(BigRational value)
        {
            var integer = BigInteger.DivRem(BigInteger.Abs(value.Numerator), value.Denominator, out var remainder);
            var text = new StringBuilder(value.Sign < 0 ? "-" : "").Append(integer.ToString(CultureInfo.InvariantCulture)).Append('.');
            for (var place = 0; place < 1200 && remainder.IsZero == false; place++)
            {
                var digit = BigInteger.DivRem(remainder * 10, value.Denominator, out remainder);
                text.Append((char)('0' + (int)digit));
            }

            return double.Parse(text.Append('0').ToString(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A positive integer of <paramref name="digits"/> random decimal digits.
        /// </summary>
        static BigInteger RandomInteger(Random random, int digits)
        {
            var value = new BigInteger(random.Next(1, 10));
            for (var d = 1; d < digits; d++)
                value = value * 10 + random.Next(0, 10);

            return value;
        }

    }

}
