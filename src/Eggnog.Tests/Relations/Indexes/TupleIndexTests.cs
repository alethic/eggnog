using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Unit tests for <see cref="TupleIndex"/> (egglog's core-relations/src/hash_index/mod.rs:535-736): keys of every
    /// arity, the probe overloads, iteration, cloning and grouping a subset by key.
    /// </summary>
    public class TupleIndexTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static int[] Get(TupleIndex index, params uint[] key) => index.TryGetSubset(key, out var rows) ? IndexTestUtil.ToArray(rows) : [];

        [Fact]
        public void HashKeyIsTheLengthPrefixedFxHash()
        {
            // egglog's hash_key hashes &[Value]: the slice length, then each cell (rustc-hash 2.x).
            static ulong Reference(params uint[] key)
            {
                var hasher = default(FxHasher);
                hasher.Write((ulong)key.Length);
                foreach (var cell in key)
                    hasher.Write(cell);

                return hasher.Finish();
            }

            Assert.Equal(Reference(7), TupleIndex.HashKey(7));
            Assert.Equal(Reference(7, 9), TupleIndex.HashKey(7, 9));
            Assert.Equal(Reference(7, 9, 11), TupleIndex.HashKey(7, 9, 11));
            Assert.Equal(Reference(1, 2, 3, 4, 5), TupleIndex.HashKey(R(1, 2, 3, 4, 5)));
            Assert.Equal(TupleIndex.HashKey(4, 5), TupleIndex.HashKey(R(4, 5)));
            Assert.NotEqual(TupleIndex.HashKey(R(4)), TupleIndex.HashKey(R(4, 0)));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(3, 4)]
        [InlineData(4, 1)]
        [InlineData(5, 4)]
        public void KeysOfEveryArityMatchAModel(int arity, int threads)
        {
            var random = new Random(arity * 31 + threads);
            var index = new TupleIndex(arity, new ParallelHeuristics(threads));
            var model = new Dictionary<string, List<int>>();
            for (var row = 0; row < 2000; row++)
            {
                var key = Enumerable.Range(0, arity).Select(_ => (uint)random.Next(5)).ToArray();
                index.AddRow(key, row);
                var text = IndexTestUtil.KeyOf(key);
                if (model.TryGetValue(text, out var list) == false)
                    model[text] = list = new List<int>();

                list.Add(row);
            }

            Assert.Equal(model.Count, index.Count);
            var entries = IndexTestUtil.Entries(index);
            IndexTestUtil.AssertExactly(model.ToDictionary(x => x.Key, x => x.Value.ToArray()), entries, $"arity {arity}");

            foreach (var (text, rows) in model)
            {
                var key = text.Split(',').Select(uint.Parse).ToArray();
                Assert.Equal(rows, Get(index, key));

                var padded = new uint[arity + 3];
                key.CopyTo(padded, 2);
                Assert.True(index.TryGetSubset(padded, 2, out var viaArray));
                Assert.Equal(rows, IndexTestUtil.ToArray(viaArray));

                SubsetRef viaValues;
                var found = arity switch
                {
                    1 => index.TryGetSubset(key[0], out viaValues),
                    2 => index.TryGetSubset(key[0], key[1], out viaValues),
                    3 => index.TryGetSubset(key[0], key[1], key[2], out viaValues),
                    _ => index.TryGetSubset(key, out viaValues),
                };
                Assert.True(found);
                Assert.Equal(rows, IndexTestUtil.ToArray(viaValues));
            }

            Assert.Empty(Get(index, Enumerable.Repeat(9u, arity).ToArray()));
        }

        [Fact]
        public void TheCursorVisitsWhatForEachVisits()
        {
            var index = new TupleIndex(2, new ParallelHeuristics(4));
            for (var row = 0; row < 300; row++)
                index.AddRow(R((uint)(row % 11), (uint)(row % 3)), row);

            var collector = new TupleIndexCollector();
            index.ForEach(ref collector);
            var cursor = default(TupleIndexCursor);
            var visited = new List<string>();
            while (cursor.MoveNext(index))
            {
                var key = IndexTestUtil.KeyOf(cursor.Key);
                Assert.Equal(key, IndexTestUtil.KeyOf(cursor.KeyData.AsSpan(cursor.KeyOffset, 2)));
                Assert.Equal(collector.Entries[key], IndexTestUtil.ToArray(cursor.Rows));
                visited.Add(key);
            }

            Assert.Equal(collector.Order, visited);
            Assert.Equal(33, visited.Count);
        }

        [Fact]
        public void ProbesCheckTheArity()
        {
            var index = new TupleIndex(2, ParallelHeuristics.Serial);
            index.AddRow(R(1, 2), 0);
            Assert.Throws<ArgumentException>(() => index.TryGetSubset(1u, out _));
            Assert.Throws<ArgumentException>(() => index.TryGetSubset(R(1, 2, 3), out _));
            Assert.Throws<ArgumentException>(() => index.AddRow(R(1), 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => index.TryGetSubset(R(1), 0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TupleIndex(0, ParallelHeuristics.Serial));
        }

        [Fact]
        public void ClearCloneAndDispose()
        {
            var index = new TupleIndex(3, new ParallelHeuristics(2));
            for (var row = 0; row < 50; row++)
                index.AddRow(R((uint)(row % 4), 1, (uint)(row % 2)), row);

            var copy = index.Clone();
            index.AddRow(R(0, 1, 0), 60);
            Assert.Equal(Enumerable.Range(0, 50).Where(r => r % 4 == 0 && r % 2 == 0).Append(60), Get(index, 0, 1, 0));
            Assert.Equal(Enumerable.Range(0, 50).Where(r => r % 4 == 0 && r % 2 == 0), Get(copy, 0, 1, 0));

            index.Clear();
            Assert.Equal(0, index.Count);
            Assert.Empty(Get(index, 0, 1, 0));
            index.AddRow(R(5, 5, 5), 3);
            Assert.Equal([3], Get(index, 5, 5, 5));

            copy.Dispose();
            Assert.Equal(0, copy.Count);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void GroupByKeyGroupsTheLiveRowsOfASubset(int arity)
        {
            var random = new Random(arity);
            var rows = Enumerable.Range(0, 800).Select(i => new[] { (uint)i }.Concat(Enumerable.Range(0, 4).Select(_ => (uint)random.Next(4))).ToArray()).ToList();
            var table = TableTestUtil.FillTable(rows, 1, null, ClosureMerge.TakeNew);
            var state = TableTestUtil.EmptyState();
            TableTestUtil.StageRemove(table, Enumerable.Range(0, 800).Where(i => i % 5 == 2).Select(i => R((uint)i)).ToArray());
            table.Merge(state);

            var columns = Enumerable.Range(1, arity).Reverse().ToArray();
            var all = table.All();
            var index = table.GroupByKey(all.AsRef(), IndexTestUtil.Ids(columns));
            Assert.Equal(1, index.ShardCount);
            IndexTestUtil.AssertExactly(IndexTestUtil.GroupByTuple(table, columns), IndexTestUtil.Entries(index), $"arity {arity}");

            var picked = Enumerable.Range(0, table.Rows.Count).Where(r => r % 4 != 0).ToArray();
            var sparse = SubsetRef.Sparse(picked, 0, picked.Length);
            var partial = TupleIndex.BuildForSubset(table, sparse, IndexTestUtil.Ids(columns));
            var expected = TableTestUtil.DumpSubset(table, sparse)
                .GroupBy(x => IndexTestUtil.KeyOf(columns.Select(c => x.Row[c]).ToArray()))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(partial), $"sparse arity {arity}");
        }

    }

}
