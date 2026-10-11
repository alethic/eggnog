using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that <see cref="BaseValues"/> is thread-safe: concurrent boxing agrees on every value and keeps intern
    /// indices dense, and concurrent registration is idempotent and races safely with boxing.
    /// </summary>
    public class BaseValuesConcurrencyTests
    {

        [Fact]
        public void ConcurrentBoxingAgreesAndKeepsIndicesDense()
        {
            const int threads = 8;
            const int keys = 20_000;
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<StringCodec, string>();
            var longs = new uint[threads][];
            var strings = new uint[threads][];

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                var random = new Random(t);
                var order = Enumerable.Range(0, keys).OrderBy(_ => random.Next()).ToArray();
                var l = new uint[keys];
                var s = new uint[keys];
                foreach (var k in order)
                {
                    // Even keys box inline, odd ones are interned.
                    l[k] = bases.Get<Int64Codec, long>(Key(k)).Rep;
                    s[k] = bases.Get<StringCodec, string>("key" + k).Rep;
                }

                longs[t] = l;
                strings[t] = s;
            });

            for (var t = 1; t < threads; t++)
            {
                Assert.Equal(longs[0], longs[t]);
                Assert.Equal(strings[0], strings[t]);
            }

            var interned = longs[0].Where((_, k) => k % 2 == 1).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, keys / 2).Select(i => 0x8000_0000u + (uint)i), interned);
            Assert.Equal(Enumerable.Range(0, keys).Select(i => (uint)i), strings[0].Order());

            for (var k = 0; k < keys; k++)
            {
                if (k % 2 == 0)
                    Assert.Equal((uint)Key(k), longs[0][k]);

                Assert.Equal(Key(k), bases.Unwrap<Int64Codec, long>(new Value(longs[0][k])));
                Assert.Equal("key" + k, bases.Unwrap<StringCodec, string>(new Value(strings[0][k])));
            }

            static long Key(int k) => k % 2 == 0 ? k : -(long)k * 1_000_003;
        }

        [Fact]
        public void ConcurrentRegistrationIsIdempotentAndRacesSafelyWithBoxing()
        {
            const int threads = 8;
            var bases = new BaseValues();
            var registrations = new Func<BaseValueTypeId>[]
            {
                () => bases.RegisterType<Int64Codec, long>(),
                () => bases.RegisterType<UInt64Codec, ulong>(),
                () => bases.RegisterType<IntPtrCodec, nint>(),
                () => bases.RegisterType<UIntPtrCodec, nuint>(),
                () => bases.RegisterType<StringCodec, string>(),
                () => bases.RegisterType<DoubleCodec, double>(),
                () => bases.RegisterType<BooleanCodec, bool>(),
                () => bases.RegisterType<UnitCodec, ValueTuple>(),
                () => bases.RegisterType<ByteCodec, byte>(),
                () => bases.RegisterType<Int32Codec, int>(),
                () => bases.RegisterType<InternedCodec<BigInteger>, BigInteger>(),
                () => bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(),
                () => bases.RegisterType<InternedCodec<Guid>, Guid>(),
                () => bases.RegisterType<InternedCodec<decimal>, decimal>(),
            };
            var ids = new BaseValueTypeId[threads][];
            var failures = 0;
            using var start = new Barrier(threads);

            // Dedicated threads, not Parallel.For: every participant must be running for the barrier to open.
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                var random = new Random(t);
                var order = Enumerable.Range(0, registrations.Length).OrderBy(_ => random.Next()).ToArray();
                var mine = new BaseValueTypeId[registrations.Length];
                start.SignalAndWait();
                foreach (var r in order)
                {
                    mine[r] = registrations[r]();

                    // Box through the types registered so far while other threads keep registering.
                    if (mine[r] != registrations[r]())
                        Interlocked.Increment(ref failures);

                    var text = "t" + random.Next(100);
                    var big = -(long)random.Next(1000) - 1;
                    if (r == 4 && bases.Unwrap<StringCodec, string>(bases.Get<StringCodec, string>(text)) != text)
                        Interlocked.Increment(ref failures);

                    if (r == 0 && bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(big)) != big)
                        Interlocked.Increment(ref failures);
                }

                ids[t] = mine;
            })).ToList();

            foreach (var worker in workers)
                worker.Start();

            foreach (var worker in workers)
                worker.Join();

            Assert.Equal(0, failures);
            for (var t = 1; t < threads; t++)
                Assert.Equal(ids[0], ids[t]);

            Assert.Equal(Enumerable.Range(0, registrations.Length).Select(i => new BaseValueTypeId(i)), ids[0].OrderBy(id => id.Rep));
            Assert.Equal(ids[0][4], bases.GetTypeId<StringCodec, string>());
            Assert.Equal(ids[0][13], bases.GetTypeId(typeof(InternedCodec<decimal>)));
            Assert.Equal("\"t1\"", bases.Format(ids[0][4], bases.Get<StringCodec, string>("t1")));
        }

    }

}
