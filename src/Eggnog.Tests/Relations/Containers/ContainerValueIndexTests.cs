using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ContainerValueIndex"/>, the element index, which unlike egglog's stays exact through
    /// removals and renames, against a dictionary of occurrence counts.
    /// </summary>
    public class ContainerValueIndexTests
    {

        static uint[] Holders(ContainerValueIndex index, uint element)
        {
            var output = new OrderedValueSet();
            index.CollectContaining(element, output);
            return output.ToArray().Order().ToArray();
        }

        [Fact]
        public void AddRemoveAndRenameKeepExactCounts()
        {
            var index = new ContainerValueIndex(new ShardData(4));
            index.Add(7, 100);
            Assert.Equal(new uint[] { 100 }, Holders(index, 7));
            index.Add(7, 100);
            Assert.Equal(2, index.CountOf(7, 100));
            index.Add(7, 200);
            Assert.Equal(new uint[] { 100, 200 }, Holders(index, 7));

            index.Remove(7, 100);
            Assert.Equal(1, index.CountOf(7, 100));
            index.Rename(7, 100, 300);
            Assert.Equal(new uint[] { 200, 300 }, Holders(index, 7));
            Assert.Equal(0, index.CountOf(7, 100));

            index.Remove(7, 200);
            index.Remove(7, 300);
            Assert.Empty(Holders(index, 7));
            Assert.Equal(0, index.ValueCount);

            // The common case: one value in one container is renamed in place.
            index.Add(8, 1);
            index.Rename(8, 1, 2);
            index.Rename(8, 2, 2);
            Assert.Equal(new uint[] { 2 }, Holders(index, 8));
            Assert.Equal(1, index.ValueCount);
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var index = new ContainerValueIndex(new ShardData(2));
            index.Add(1, 10);
            index.Add(1, 11);
            index.Add(2, 10);
            var copy = index.Clone();
            index.Remove(1, 11);
            index.Add(1, 12);
            index.Remove(2, 10);

            Assert.Equal(new uint[] { 10, 11 }, Holders(copy, 1));
            Assert.Equal(new uint[] { 10 }, Holders(copy, 2));
            Assert.Equal(new uint[] { 10, 12 }, Holders(index, 1));
            Assert.Empty(Holders(index, 2));
        }

        /// <summary>
        /// Random sequences of additions, removals of recorded occurrences and renames agree with a dictionary of
        /// counts.
        /// </summary>
        [Theory]
        [InlineData(1, 0)]
        [InlineData(1, 1)]
        [InlineData(8, 2)]
        [InlineData(8, 3)]
        public void RandomOperationsAgreeWithCounts(int shards, int seed)
        {
            var random = new Random(seed);
            var index = new ContainerValueIndex(new ShardData(shards));
            var model = new Dictionary<(uint Element, uint Id), int>();
            for (var step = 0; step < 5000; step++)
            {
                var roll = random.Next(10);
                if (roll < 5 || model.Count == 0)
                {
                    var key = ((uint)random.Next(40), (uint)random.Next(12));
                    index.Add(key.Item1, key.Item2);
                    model[key] = model.GetValueOrDefault(key) + 1;
                }
                else
                {
                    var key = model.Keys.ElementAt(random.Next(model.Count));
                    if (--model[key] == 0)
                        model.Remove(key);

                    if (roll < 8)
                    {
                        index.Remove(key.Element, key.Id);
                    }
                    else
                    {
                        var renamed = (key.Element, (uint)random.Next(12));
                        index.Rename(key.Element, key.Id, renamed.Item2);
                        model[renamed] = model.GetValueOrDefault(renamed) + 1;
                    }
                }
            }

            Assert.Equal(model.OrderBy(x => x.Key).ToArray(), index.Snapshot().OrderBy(x => x.Key).ToArray());
            foreach (var element in model.Keys.Select(k => k.Element).Distinct())
                Assert.Equal(model.Keys.Where(k => k.Element == element).Select(k => k.Id).Order().ToArray(), Holders(index, element));
        }

        [Fact]
        public void ConcurrentAdditionsAreAllRecorded()
        {
            var index = new ContainerValueIndex(new ShardData(8));
            var threads = ConcurrencyTestThread.StartMany(8, t =>
            {
                for (var i = 0u; i < 3000; i++)
                    index.Add(i % 97, (uint)t);
            });
            ConcurrencyTestThread.JoinAll(threads);

            var snapshot = index.Snapshot();
            Assert.Equal(97 * 8, snapshot.Count);
            Assert.Equal(3000 * 8, snapshot.Values.Sum());
        }

    }

}
