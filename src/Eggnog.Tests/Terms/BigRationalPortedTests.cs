using System.Numerics;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>BigRat</c> tests, ported (<c>tests/rat-pow-eval.egg</c> and <c>tests/web-demo/bignum.egg</c> at
    /// 5e655cae): values in lowest terms, <c>to-i64</c> and <c>pow</c> with their edge cases, and a function of
    /// <c>BigInt</c> keys to a <c>BigRat</c> checked through <c>numer</c>, <c>denom</c> and <c>&gt;&gt;</c>. A top-level
    /// <c>let</c> is, as egglog desugars it, a nullary function the host sets, and a <c>fail</c> of a call is such a
    /// write refused because the call has no result; a <c>check</c> is a host check.
    /// </summary>
    public sealed class BigRationalPortedTests
    {

        /// <summary>
        /// tests/rat-pow-eval.egg.
        /// </summary>
        [Fact]
        public void RatPowEval()
        {
            var lets = new Lets(new EGraph());
            var root = lets.Root;
            var zero = lets.Let("zero", Rat(0, 1));
            var zeroAlt = lets.Let("zero-alt", Rat(0, -1));
            Assert.True(root.Check(Value(zero).Is(zeroAlt)));
            Assert.True(root.Check(Value(zero).Is(BigRationalPrimitives.Multiply.Of(Rat(-1, 1), zeroAlt))));

            var one = lets.Let("one", Rat(1, 1));
            var two = lets.Let("two", Rat(2, 1));

            var four = lets.Let("four", Rat(4, 1));
            var fourth = lets.Let("fourth", Rat(1, 4));
            Assert.True(root.Get(Primitives.NotEqual<BigRational>().Of(four, fourth)));

            var negOne = lets.Let("neg-one", Rat(-1, 1));
            var negOneAlt = lets.Let("neg-one-alt", Rat(1, -1));
            Assert.True(root.Check(Value(negOne).Is(negOneAlt)));

            Assert.True(root.Check(BigRationalPrimitives.ToInt64.Of(two).Is(2L)));
            Assert.True(root.Check(BigRationalPrimitives.ToInt64.Of(negOne).Is(-1L)));
            lets.Fail(BigRationalPrimitives.ToInt64.Of(fourth));
            var pastI64 = BigRationalPrimitives.FromBigIntegers.Of(
                BigIntegerPrimitives.Parse.Of("9223372036854775808"),
                BigIntegerPrimitives.FromInt64.Of(1L));
            lets.Fail(BigRationalPrimitives.ToInt64.Of(pastI64));

            var negTwo = lets.Let("neg-two", BigRationalPrimitives.Multiply.Of(negOne, two));

            // 1 = 0^0 (zero-to-zero edge case)
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(zero, zero))));
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(zero, zeroAlt))));
            // 0 = 0^2 (a positive power)
            Assert.True(root.Check(Value(zero).Is(BigRationalPrimitives.Power.Of(zero, two))));
            // 1/0 error condition
            lets.Fail(BigRationalPrimitives.Power.Of(zero, negOne));
            lets.Fail(BigRationalPrimitives.Power.Of(zero, negTwo));

            // 4 = 2^2
            Assert.True(root.Check(Value(four).Is(BigRationalPrimitives.Power.Of(two, two))));
            // 1/4 == 4^-1
            Assert.True(root.Check(Value(fourth).Is(BigRationalPrimitives.Power.Of(four, negOne))));
            // 1/4 = 2^-2
            Assert.True(root.Check(Value(fourth).Is(BigRationalPrimitives.Power.Of(two, negTwo))));
            // 1 = 1^-2
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(one, negTwo))));
            // 1 = 2^0
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(two, zero))));
            // 1 = (-2)^0
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(negTwo, zero))));

            // rational powers are forbidden!
            lets.Fail(BigRationalPrimitives.Power.Of(zero, fourth));
            lets.Fail(BigRationalPrimitives.Power.Of(two, fourth));

            // big numbers (kept intentionally small for portability)
            var thirtyTwo = lets.Let("thirty-two", Rat(32, 1));
            Assert.True(root.Check(Value(one).Is(BigRationalPrimitives.Power.Of(one, thirtyTwo))));
        }

        /// <summary>
        /// tests/web-demo/bignum.egg.
        /// </summary>
        [Fact]
        public void Bignum()
        {
            var graph = new EGraph();
            var lets = new Lets(graph);
            var x = lets.Let("x", BigIntegerPrimitives.FromInt64.Of(-1234L));
            var y = lets.Let("y", BigIntegerPrimitives.Parse.Of("2"));
            var z = lets.Let("z", BigRationalPrimitives.FromBigIntegers.Of(x, y));
            // A check here never interns, so the string, which no write made, is read through a let.
            Assert.Equal("-617", lets.Let("numer-z", BigIntegerPrimitives.Format.Of(BigRationalPrimitives.Numerator.Of(z))));

            var bignums = graph.DefineFunction<BigInteger, BigInteger, BigRational>("bignums");
            lets.Root.Assume(bignums.Of(x, y).Is(z));
            var check = graph.DefineQuery("check", q =>
            {
                var a = q.Var<BigInteger>("a");
                var b = q.Var<BigInteger>("b");
                var c = q.Var<BigRational>("c");
                q.When(
                    bignums.Of(a, b).Is(c),
                    BigRationalPrimitives.Numerator.Of(c).Is(BigIntegerPrimitives.ShiftRight.Of(a, 1L)),
                    BigRationalPrimitives.Denominator.Of(c).Is(BigIntegerPrimitives.ShiftRight.Of(b, 1L)));
            });

            Assert.True(lets.Root.Any(check));
        }

        /// <summary>
        /// <c>(bigrat (bigint n) (bigint d))</c>.
        /// </summary>
        static Term<BigRational> Rat(long numerator, long denominator) =>
            BigRationalPrimitives.FromBigIntegers.Of(
                BigIntegerPrimitives.FromInt64.Of(numerator),
                BigIntegerPrimitives.FromInt64.Of(denominator));

        /// <summary>
        /// A value as a constant term.
        /// </summary>
        static Term<T> Value<T>(T value) => value;

    }

}
