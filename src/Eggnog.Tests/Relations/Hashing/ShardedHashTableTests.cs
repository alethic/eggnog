using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests <see cref="ShardedHashTable{TEntry}"/>, including the port of egglog's <c>shard_math</c> test
    /// (core-relations/src/table/tests.rs).
    /// </summary>
    public class ShardedHashTableTests
    {

        static readonly HashTestEntryHasher Hasher = default;

        static ShardedHashTable<HashTestEntry> Filled(int shards, int count)
        {
            var table = new ShardedHashTable<HashTestEntry>(shards);
            for (var key = 0; key < count; key++)
            {
                var hash = FxHasher.Hash((uint)key);
                table.ShardFor(hash).InsertUnique(hash, new HashTestEntry(hash, key, key), Hasher);
            }

            return table;
        }

        static int Total(ShardedHashTable<HashTestEntry> table) => Enumerable.Range(0, table.ShardCount).Sum(i => table.GetShard(i).Count);

        [Fact]
        public void ShardMath()
        {
            var table = new ShardedHashTable<HashTestEntry>(14);

            // Should be rounded up to 16.
            Assert.Equal(16, table.ShardCount);

            // A hundred thousand random rows, keyed on their first two columns (egglog's hash_code with n_keys = 2),
            // put more than 100 rows in every shard.
            var random = new Random(9);
            var histogram = new int[table.ShardCount];
            for (var i = 0; i < 100_000; i++)
            {
                var a = (uint)random.NextInt64(1L << 32);
                var b = (uint)random.NextInt64(1L << 32);
                _ = random.NextInt64(1L << 32);
                histogram[table.ShardData.ShardOf(FxHasher.Hash(a, b))]++;
            }

            Assert.All(histogram, count => Assert.True(count > 100, string.Join(", ", histogram)));

            // Picking low numbers should all get shard 0.
            for (var x = 0; x < 100_000; x++)
                Assert.Equal(0, table.ShardData.ShardOf((ulong)x));
        }

        [Fact]
        public void EntriesLiveInTheShardTheirHashSelects()
        {
            var table = Filled(8, 10_000);
            Assert.Equal(8, table.ShardCount);
            Assert.Equal(10_000, Total(table));

            for (var key = 0; key < 10_000; key++)
            {
                var hash = FxHasher.Hash((uint)key);
                var shard = table.ShardData.ShardOf(hash);
                for (var i = 0; i < table.ShardCount; i++)
                {
                    var match = new HashTestEntryMatch(key);
                    Assert.Equal(i == shard, table.GetShard(new ShardId(i)).Find(hash, ref match) >= 0);
                }
            }

            for (var i = 0; i < table.ShardCount; i++)
            {
                Assert.True(table.GetShard(i).Count > 0);
                RawHashTableInvariants.Check(table.GetShard(i), Hasher);
            }
        }

        [Fact]
        public void ASingleShardTakesEverything()
        {
            var table = new ShardedHashTable<HashTestEntry>(new ShardData(1));
            Assert.Equal(1, table.ShardCount);
            for (var key = 0; key < 1000; key++)
                Assert.Same(table.GetShard(0), table.ShardFor(FxHasher.Hash((uint)key)));
        }

        [Fact]
        public void ClearEmptiesEveryShard()
        {
            var table = Filled(4, 1000);
            table.Clear();
            for (var i = 0; i < table.ShardCount; i++)
            {
                Assert.Equal(0, table.GetShard(i).Count);
                RawHashTableInvariants.Check(table.GetShard(i), Hasher);
            }
        }

        [Fact]
        public void CloneCopiesEveryShard()
        {
            var table = Filled(4, 1000);
            var copy = table.Clone();
            Assert.Equal(table.ShardCount, copy.ShardCount);
            Assert.Equal(1000, Total(copy));
            copy.Clear();

            Assert.Equal(1000, Total(table));
            for (var i = 0; i < table.ShardCount; i++)
            {
                Assert.NotSame(table.GetShard(i), copy.GetShard(i));
                Assert.Equal(0, copy.GetShard(i).Count);
            }
        }

    }

}
