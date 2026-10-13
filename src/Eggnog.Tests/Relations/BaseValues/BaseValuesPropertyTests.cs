using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Model-based tests of <see cref="BaseValues"/>: random operation sequences checked step by step against
    /// <see cref="BaseValueModel{T}"/>, a reference written from egglog's encoding rules, and the canonical-boxing
    /// property (Value equality is semantic equality within a type) over everything seen.
    /// </summary>
    public class BaseValuesPropertyTests
    {

        const uint Tag = 0x8000_0000u;

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RandomOperationsMatchTheReferenceModel(int seed)
        {
            var rng = new Random(seed);
            var bases = new BaseValues();

            // Register in a random order: ids follow registration order, and re-registering changes nothing.
            var registrations = new Func<BaseValueTypeId>[]
            {
                () => bases.RegisterType<Int64Codec, long>(),
                () => bases.RegisterType<UInt64Codec, ulong>(),
                () => bases.RegisterType<Int32Codec, int>(),
                () => bases.RegisterType<StringCodec, string>(),
                () => bases.RegisterType<DoubleCodec, double>(),
                () => bases.RegisterType<InternedCodec<BigInteger>, BigInteger>(),
                () => bases.RegisterType<BaseValueTestTinyLongCodec, long>(),
                () => bases.RegisterType<BooleanCodec, bool>(),
            };
            var order = Enumerable.Range(0, registrations.Length).OrderBy(_ => rng.Next()).ToArray();
            for (var i = 0; i < order.Length; i++)
                Assert.Equal(new BaseValueTypeId(i), registrations[order[i]]());

            for (var i = 0; i < order.Length; i++)
                Assert.Equal(new BaseValueTypeId(i), registrations[order[i]]());

            // The models restate egglog's rules from the Rust source: medium integers are inline iff
            // `x & (VAL_MASK - 1) == x` (unboxed.rs:79), small ones are `x as u32`, the rest interned.
            var longs = new BaseValueModel<long>(x => (x & 0x7FFF_FFFFL) == x ? (uint)x : null, tagged: true);
            var ulongs = new BaseValueModel<ulong>(x => (x & 0x7FFF_FFFFUL) == x ? (uint)x : null, tagged: true);
            var ints = new BaseValueModel<int>(x => unchecked((uint)x), tagged: true);
            var strings = new BaseValueModel<string>(_ => null, tagged: false);
            var doubles = new BaseValueModel<double>(_ => null, tagged: false);
            var bigs = new BaseValueModel<BigInteger>(_ => null, tagged: false);
            var tinies = new BaseValueModel<long>(x => x >= 0 && x < 16 ? (uint)x : null, tagged: true);
            var bools = new BaseValueModel<bool>(x => x ? 1u : 0u, tagged: true);

            var stringPool = Enumerable.Range(0, 300).Select(i => "s" + i).Concat(["", "\0", "é", "é"]).ToArray();
            var bigPool = Enumerable.Range(0, 200).Select(i => BigInteger.Pow(3, i) - 1).ToArray();

            for (var step = 0; step < 20_000; step++)
            {
                switch (rng.Next(8))
                {
                    case 0:
                        Step<Int64Codec, long>(bases, longs, NextLong(rng));
                        break;
                    case 1:
                        Step<UInt64Codec, ulong>(bases, ulongs, unchecked((ulong)NextLong(rng)));
                        break;
                    case 2:
                        Step<Int32Codec, int>(bases, ints, unchecked((int)NextLong(rng)));
                        break;
                    case 3:
                        Step<StringCodec, string>(bases, strings, new string(stringPool[rng.Next(stringPool.Length)].AsSpan()));
                        break;
                    case 4:
                        Step<DoubleCodec, double>(bases, doubles, NextDouble(rng));
                        break;
                    case 5:
                        Step<InternedCodec<BigInteger>, BigInteger>(bases, bigs, bigPool[rng.Next(bigPool.Length)] * (rng.Next(2) * 2 - 1));
                        break;
                    case 6:
                        Step<BaseValueTestTinyLongCodec, long>(bases, tinies, NextLong(rng));
                        break;
                    default:
                        Step<BooleanCodec, bool>(bases, bools, rng.Next(2) == 1);
                        break;
                }
            }

            // Every interned value is still where the model put it, and the intern tables hold nothing else.
            CheckInterned<Int64Codec, long>(bases, longs, Tag);
            CheckInterned<UInt64Codec, ulong>(bases, ulongs, Tag);
            CheckInterned<StringCodec, string>(bases, strings, 0);
            CheckInterned<DoubleCodec, double>(bases, doubles, 0);
            CheckInterned<InternedCodec<BigInteger>, BigInteger>(bases, bigs, 0);
            CheckInterned<BaseValueTestTinyLongCodec, long>(bases, tinies, Tag);
            Assert.Equal(0, bases.GetTable<Int32Codec, int>().Table.Count);
            Assert.Equal(0, bases.GetTable<BooleanCodec, bool>().Table.Count);
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        public void BoxingIsCanonical(int seed)
        {
            var rng = new Random(seed);
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<UInt64Codec, ulong>();
            bases.RegisterType<IntPtrCodec, nint>();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<DoubleCodec, double>();

            var longs = Enumerable.Range(0, 5000).Select(_ => NextLong(rng)).ToArray();
            CheckCanonical<Int64Codec, long>(bases, longs);
            CheckCanonical<UInt64Codec, ulong>(bases, longs.Select(x => unchecked((ulong)x)).ToArray());
            CheckCanonical<IntPtrCodec, nint>(bases, longs.Select(x => (nint)x).ToArray());
            CheckCanonical<Int32Codec, int>(bases, longs.Select(x => unchecked((int)x)).ToArray());
            CheckCanonical<SByteCodec, sbyte>(bases, longs.Select(x => unchecked((sbyte)x)).ToArray());
            CheckCanonical<UInt16Codec, ushort>(bases, longs.Select(x => unchecked((ushort)x)).ToArray());
            CheckCanonical<StringCodec, string>(bases, longs.Select(x => (x % 997).ToString("x")).ToArray());
            CheckCanonical<DoubleCodec, double>(bases, Enumerable.Range(0, 5000).Select(_ => NextDouble(rng)).ToArray());
            CheckCanonical<BooleanCodec, bool>(bases, longs.Select(x => (x & 1) == 1).ToArray());
        }

        /// <summary>
        /// Boxes one value through the registry and the model and checks they agree, and that it unwraps to the model's
        /// representative.
        /// </summary>
        static void Step<TCodec, T>(BaseValues bases, BaseValueModel<T> model, T value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var expected = model.Box(value);
            var actual = bases.Get<TCodec, T>(value);
            Assert.Equal(expected, actual.Rep);
            Assert.Equal(model.Representative(value), bases.Unwrap<TCodec, T>(actual));
        }

        /// <summary>
        /// Checks that the type's intern table holds exactly the model's interned values, in order.
        /// </summary>
        static void CheckInterned<TCodec, T>(BaseValues bases, BaseValueModel<T> model, uint tag)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var table = bases.GetTable<TCodec, T>();
            Assert.Equal(model.InternedCount, table.Table.Count);
            for (var i = 0; i < model.InternedCount; i++)
            {
                Assert.Equal(model.Interned[i], table.Table.Get((uint)i));
                Assert.Equal(model.Interned[i], bases.Unwrap<TCodec, T>(new Value(tag + (uint)i)));
            }
        }

        /// <summary>
        /// Checks that boxing is a function of the value's equality class and is injective on classes: two values box
        /// equally exactly when they are equal, and each value unwraps to an equal one.
        /// </summary>
        static void CheckCanonical<TCodec, T>(BaseValues bases, T[] values)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var byValue = new Dictionary<T, uint>();
            var byRep = new Dictionary<uint, T>();
            foreach (var value in values)
            {
                var rep = bases.Get<TCodec, T>(value).Rep;
                Assert.True(value.Equals(bases.Unwrap<TCodec, T>(new Value(rep))));
                if (byValue.TryGetValue(value, out var seen))
                    Assert.Equal(seen, rep);
                else
                    byValue.Add(value, rep);

                if (byRep.TryGetValue(rep, out var other))
                    Assert.True(other.Equals(value));
                else
                    byRep.Add(rep, value);
            }

            Assert.Equal(byValue.Count, byRep.Count);
        }

        /// <summary>
        /// A 64-bit integer biased toward the boundaries of egglog's encodings and toward repeats.
        /// </summary>
        static long NextLong(Random rng)
        {
            return rng.Next(6) switch
            {
                0 => rng.Next(100),
                1 => 0x8000_0000L + rng.Next(-3, 3),
                2 => rng.Next(-3, 3),
                3 => new[] { long.MinValue, long.MaxValue, 0x1_0000_0000L, 0xFFFF_FFFFL, -0x8000_0000L, 15, 16 }[rng.Next(7)],
                4 => (rng.Next(500) - 250) * 0x1_0000_0001L,
                _ => rng.NextInt64(long.MinValue, long.MaxValue),
            };
        }

        /// <summary>
        /// A double drawn mostly from a small pool, including every NaN and zero variant.
        /// </summary>
        static double NextDouble(Random rng)
        {
            return rng.Next(4) switch
            {
                0 => new[] { 0.0, -0.0, double.NaN, -double.NaN, BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0001L), double.PositiveInfinity, double.NegativeInfinity, double.Epsilon }[rng.Next(8)],
                1 => rng.Next(-50, 50) / 8.0,
                2 => rng.Next(-1000, 1000) * 1e300,
                _ => rng.NextDouble() * rng.Next(-5, 5),
            };
        }

    }

}
