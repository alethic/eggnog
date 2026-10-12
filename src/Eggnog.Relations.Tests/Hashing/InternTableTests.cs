using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests <see cref="InternTable{TKey}"/>, including a port of egglog's <c>basic_parallel_vec_push</c> test
    /// (concurrency/src/tests.rs) for its key store.
    /// </summary>
    public class InternTableTests
    {

        [Fact]
        public void IdsAreDenseInFirstSeenOrder()
        {
            var table = new InternTable<string>();
            Assert.Equal(0, table.Count);
            Assert.Equal(0u, table.Intern("a"));
            Assert.Equal(1u, table.Intern("b"));
            Assert.Equal(0u, table.Intern("a"));
            Assert.Equal(2u, table.Intern("c"));
            Assert.Equal(1u, table.Intern(new string('b', 1)));
            Assert.Equal(3, table.Count);

            Assert.Equal("a", table.Get(0));
            Assert.Equal("b", table.Get(1));
            Assert.Equal("c", table.GetRef(2));
        }

        [Fact]
        public void GrowsPastItsInitialCapacity()
        {
            const int count = 100_000;
            var table = new InternTable<long>();
            for (var i = 0; i < count; i++)
                Assert.Equal((uint)i, table.Intern(Key(i)));

            Assert.Equal(count, table.Count);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal((uint)i, table.Intern(Key(i)));
                Assert.Equal(Key(i), table.Get((uint)i));
            }

            static long Key(int i) => (long)i * 1_000_003 - 5_000_000;
        }

        [Fact]
        public void CollidingHashCodesStillInternDistinctKeys()
        {
            var table = new InternTable<InternTestCollidingKey>();
            for (var i = 0; i < 1000; i++)
                Assert.Equal((uint)i, table.Intern(new InternTestCollidingKey(i)));

            for (var i = 0; i < 1000; i++)
                Assert.Equal((uint)i, table.Intern(new InternTestCollidingKey(i)));

            Assert.Equal(1000, table.Count);
        }

        /// <summary>
        /// <see cref="InternTable{TKey}.TryGetId"/> (request E1 of the rule-layer spec) finds exactly the interned keys,
        /// with their ids, and never adds a key.
        /// </summary>
        [Fact]
        public void TryGetIdFindsInternedKeysAndAddsNone()
        {
            var table = new InternTable<string>();
            Assert.False(table.TryGetId("a", out var missing));
            Assert.Equal(0u, missing);
            Assert.Equal(0, table.Count);

            Assert.Equal(0u, table.Intern("a"));
            Assert.Equal(1u, table.Intern("b"));
            Assert.True(table.TryGetId("a", out var a));
            Assert.Equal(0u, a);
            Assert.True(table.TryGetId(new string('b', 1), out var b));
            Assert.Equal(1u, b);
            Assert.False(table.TryGetId("c", out _));
            Assert.Equal(2, table.Count);

            // A key looked up and missed is still assigned the next id when it is interned.
            Assert.Equal(2u, table.Intern("c"));
            Assert.True(table.TryGetId("c", out var c));
            Assert.Equal(2u, c);
            Assert.Throws<ArgumentNullException>(() => table.TryGetId(null!, out _));
        }

        /// <summary>
        /// <see cref="InternTable{TKey}.TryGetId"/> probes past colliding keys, in one shard and in many, and across the
        /// key store's growth.
        /// </summary>
        [Fact]
        public void TryGetIdProbesPastCollisionsAndGrowth()
        {
            var colliding = new InternTable<InternTestCollidingKey>();
            for (var i = 0; i < 1000; i += 2)
                colliding.Intern(new InternTestCollidingKey(i));

            for (var i = 0; i < 1000; i++)
            {
                var found = colliding.TryGetId(new InternTestCollidingKey(i), out var id);
                Assert.Equal(i % 2 == 0, found);
                if (found)
                    Assert.Equal((uint)(i / 2), id);
            }

            Assert.Equal(500, colliding.Count);

            var single = new InternTable<long>(1);
            for (var i = 0; i < 5000; i++)
                single.Intern(i * 3L);

            for (var i = 0; i < 15000; i++)
            {
                var found = single.TryGetId(i, out var id);
                Assert.Equal(i % 3 == 0, found);
                if (found)
                    Assert.Equal((uint)(i / 3), id);
            }

            Assert.Equal(5000, single.Count);
        }

        [Fact]
        public void ASingleShardWorks()
        {
            var table = new InternTable<int>(1);
            for (var i = 0; i < 5000; i++)
                Assert.Equal((uint)i, table.Intern(i * 7));

            for (var i = 0; i < 5000; i++)
                Assert.Equal(i * 7, table.Get((uint)i));
        }

        [Fact]
        public void PrimitiveKeysHashLikeRustFxHasher()
        {
            // Rust: FxHasher::write_u64 / write_usize then finish, i.e. rotl(x * K, 26); write_u32 for 32-bit keys.
            static ulong Rust64(ulong x) => BitOperations.RotateLeft(x * FxHasher.Seed, 26);

            long[] samples = [0, 1, -1, 42, long.MinValue, long.MaxValue, 0x1_0000_0001L, -0x7654_3210_0123_4567L];
            foreach (var x in samples)
            {
                Assert.Equal(Rust64((ulong)x), InternTable<long>.HashKey(x));
                Assert.Equal(Rust64((ulong)x), InternTable<ulong>.HashKey((ulong)x));
                Assert.Equal(Rust64((ulong)x), InternTable<nint>.HashKey((nint)x));
                Assert.Equal(Rust64((ulong)x), InternTable<nuint>.HashKey((nuint)x));
                Assert.Equal(FxHasher.Hash((uint)x), InternTable<int>.HashKey((int)x));
                Assert.Equal(FxHasher.Hash((uint)x), InternTable<uint>.HashKey((uint)x));
            }

            Assert.Equal(Rust64(BitConverter.DoubleToUInt64Bits(1.5)), InternTable<double>.HashKey(1.5));
        }

        [Fact]
        public void EqualDoublesInternToOneId()
        {
            var table = new InternTable<double>();
            var otherNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001UL));
            Assert.True(double.IsNaN(otherNaN));

            var nan = table.Intern(double.NaN);
            Assert.Equal(nan, table.Intern(otherNaN));
            Assert.Equal(nan, table.Intern(-double.NaN));

            var zero = table.Intern(0.0);
            Assert.Equal(zero, table.Intern(-0.0));
            Assert.NotEqual(nan, zero);

            Assert.Equal(2u, table.Intern(1.0));
            Assert.Equal(3u, table.Intern(-1.0));
            Assert.Equal(4u, table.Intern(double.PositiveInfinity));
            Assert.Equal(5, table.Count);
        }

        [Fact]
        public void LongKeysWithFoldingHashCodesInternCorrectly()
        {
            // long.GetHashCode is lo ^ hi, so all these keys share a hash code; their hash keys must still differ.
            const long stride = 0x1_0000_0001L;
            const int count = 20_000;
            var table = new InternTable<long>();
            for (var k = 0; k < count; k++)
                Assert.Equal((uint)k, table.Intern(k * stride));

            for (var k = 0; k < count; k++)
            {
                Assert.Equal((uint)k, table.Intern(k * stride));
                Assert.Equal(k * stride, table.Get((uint)k));
            }

            Assert.Equal(count, table.Count);
            var doubles = Enumerable.Range(1, 4096).Select(k => InternTable<double>.HashKey(BitConverter.Int64BitsToDouble(k * stride))).Distinct().Count();
            Assert.Equal(4096, doubles);
        }

        [Fact]
        public void InvalidArgumentsThrow()
        {
            var table = new InternTable<string>();
            Assert.Throws<ArgumentNullException>(() => table.Intern(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Get(0));
            table.Intern("x");
            Assert.Equal("x", table.Get(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Get(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Get(uint.MaxValue));
        }

        [Fact]
        public void ParallelPushesGetDistinctDenseIds()
        {
            const int threads = 10;
            const int perThread = 10;
            var table = new InternTable<int>();
            var results = new uint[threads][];
            var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
            {
                var got = new uint[perThread];
                for (var j = 0; j < perThread; j++)
                    got[j] = table.Intern(i * perThread + j);

                results[i] = got;
            })).ToList();

            foreach (var worker in workers)
                worker.Start();

            foreach (var worker in workers)
                worker.Join();

            var ids = results.SelectMany(r => r).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, threads * perThread).Select(i => (uint)i), ids);
            Assert.Equal(threads * perThread, table.Count);

            for (var i = 0; i < threads; i++)
                for (var j = 0; j < perThread; j++)
                    Assert.Equal(i * perThread + j, table.Get(results[i][j]));
        }

        [Fact]
        public void ConcurrentInterningAgreesOnIds()
        {
            const int threads = 8;
            const int keys = 20_000;
            var table = new InternTable<string>();
            var seen = new uint[threads][];

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                var random = new Random(t);
                var order = Enumerable.Range(0, keys).OrderBy(_ => random.Next()).ToArray();
                var ids = new uint[keys];
                foreach (var k in order)
                    ids[k] = table.Intern("key" + k);

                seen[t] = ids;
            });

            Assert.Equal(keys, table.Count);
            for (var t = 1; t < threads; t++)
                Assert.Equal(seen[0], seen[t]);

            Assert.Equal(Enumerable.Range(0, keys).Select(i => (uint)i), seen[0].Order());
            for (var k = 0; k < keys; k++)
                Assert.Equal("key" + k, table.Get(seen[0][k]));
        }

        [Fact]
        public async Task ReadersSeeEveryPublishedKeyWhileWritersAppend()
        {
            const int writers = 3;
            const int perWriter = 50_000;
            var table = new InternTable<long>();
            var cancellation = TestContext.Current.CancellationToken;
            var done = 0;
            var failures = 0;

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                while (Volatile.Read(ref done) == 0)
                {
                    var count = table.Count;
                    for (var id = Math.Max(0, count - 64); id < count; id++)
                    {
                        // Writers intern writers * k + w, so every published key is in [0, writers * perWriter).
                        var key = table.Get((uint)id);
                        if (key < 0 || key >= writers * perWriter)
                            Interlocked.Increment(ref failures);
                    }
                }
            }, cancellation)).ToArray();

            var appenders = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (var k = 0; k < perWriter; k++)
                    table.Intern((long)writers * k + w);
            }, cancellation)).ToArray();

            await Task.WhenAll(appenders);
            Volatile.Write(ref done, 1);
            await Task.WhenAll(readers);

            Assert.Equal(0, failures);
            Assert.Equal(writers * perWriter, table.Count);
            var all = Enumerable.Range(0, writers * perWriter).Select(id => table.Get((uint)id)).Order();
            Assert.Equal(Enumerable.Range(0, writers * perWriter).Select(i => (long)i), all);
        }

    }

}
