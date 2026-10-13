using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The meet of the live cases' partitions, on which the lift relies to write every equality that holds in all its
    /// cases: the blocks of parent ids equal in every case, from any seed.
    /// </summary>
    public class ClassPartitionTests
    {

        /// <summary>
        /// A graph with <paramref name="count"/> root ids and one case per entry of <paramref name="unions"/>, each
        /// making the unions listed (pairs of id indexes), all brought up to date.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids, List<EGraphContext> Cases) Setup(int count, params (int A, int B)[][] unions)
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            var cases = new List<EGraphContext>();
            foreach (var list in unions)
            {
                var @case = g.Root.CreateChild(ContextOptions.Case);
                foreach (var (a, b) in list)
                    @case.StageUnion(ids[a], ids[b]);

                @case.EnsureClean();
                cases.Add(@case);
            }

            return (g, ids, cases);
        }

        /// <summary>
        /// The blocks of the meet from <paramref name="seed"/>, as sorted text over id indexes.
        /// </summary>
        static string[] Meet(Value[] ids, List<EGraphContext> cases, int seed)
        {
            var blocks = new List<uint[]>();
            ClassPartition.Meet(cases[seed].GetMemberMap(), cases, seed, blocks);
            var index = ids.Select((v, i) => (v.Rep, i)).ToDictionary(p => p.Rep, p => p.i);
            return blocks.Select(b => string.Join(" ", b.Select(m => index[m]))).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }

        [Fact]
        public void TwoCasesMeetOnTheirCommonPairs()
        {
            var (_, ids, cases) = Setup(5, [(0, 1), (1, 2)], [(0, 1), (2, 3)]);
            Assert.Equal(["0 1"], Meet(ids, cases, 0));
            Assert.Equal(["0 1"], Meet(ids, cases, 1));
        }

        [Fact]
        public void ThreeCasesMeetOnWhatAllThreeShare()
        {
            var (_, ids, cases) = Setup(6, [(0, 1), (1, 2), (3, 4)], [(0, 1), (0, 2), (3, 4), (4, 5)], [(1, 2), (2, 0), (5, 4)]);
            Assert.Equal(["0 1 2"], Meet(ids, cases, 0));
            for (var seed = 0; seed < 3; seed++)
                Assert.Equal(Meet(ids, cases, 0), Meet(ids, cases, seed));
        }

        [Fact]
        public void OneCaseYieldsItsGroups()
        {
            var (_, ids, cases) = Setup(6, [(0, 1), (1, 2), (3, 5)]);
            Assert.Equal(["0 1 2", "3 5"], Meet(ids, cases, 0));
        }

        [Fact]
        public void DisjointPartitionsMeetOnNothing()
        {
            var (_, ids, cases) = Setup(4, [(0, 1)], [(2, 3)]);
            Assert.Empty(Meet(ids, cases, 0));
            Assert.Empty(Meet(ids, cases, 1));
        }

        /// <summary>
        /// A group split by another case yields one block per agreeing run, each ascending, in a deterministic order.
        /// </summary>
        [Fact]
        public void AGroupSplitsIntoAgreeingRuns()
        {
            var (_, ids, cases) = Setup(6, [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5)], [(0, 2), (4, 2), (1, 3), (5, 3)]);
            Assert.Equal(["0 2 4", "1 3 5"], Meet(ids, cases, 0));
            var blocks = new List<uint[]>();
            Assert.Equal(2, ClassPartition.Meet(cases[0].GetMemberMap(), cases, 0, blocks));
            foreach (var block in blocks)
                Assert.True(block.SequenceEqual(block.OrderBy(m => m)));
        }

        [Fact]
        public void BadArgumentsAreRefused()
        {
            var (_, _, cases) = Setup(2, [(0, 1)]);
            var map = cases[0].GetMemberMap();
            Assert.Throws<ArgumentOutOfRangeException>(() => ClassPartition.Meet(map, cases, 1, new List<uint[]>()));
            Assert.Throws<ArgumentNullException>(() => ClassPartition.Meet(map, cases, 0, null!));
        }

        /// <summary>
        /// Against a brute-force meet over every pair of ids, from every seed.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void RandomPartitionsMeetAsTheBruteForceDoes(int seed)
        {
            var random = new Random(seed);
            const int Count = 10;
            var unions = Enumerable.Range(0, 1 + random.Next(4))
                .Select(_ => Enumerable.Range(0, random.Next(9)).Select(_ => (random.Next(Count), random.Next(Count))).ToArray())
                .ToArray();
            var (_, ids, cases) = Setup(Count, unions);

            var expected = new HashSet<(int, int)>();
            for (var a = 0; a < Count; a++)
                for (var b = a + 1; b < Count; b++)
                    if (cases.All(c => c.Find(ids[a]) == c.Find(ids[b])))
                        expected.Add((a, b));

            for (var s = 0; s < cases.Count; s++)
            {
                var blocks = new List<uint[]>();
                ClassPartition.Meet(cases[s].GetMemberMap(), cases, s, blocks);
                var index = ids.Select((v, i) => (v.Rep, i)).ToDictionary(p => p.Rep, p => p.i);
                var actual = new HashSet<(int, int)>();
                foreach (var block in blocks)
                    for (var i = 0; i < block.Length; i++)
                        for (var j = i + 1; j < block.Length; j++)
                            actual.Add((Math.Min(index[block[i]], index[block[j]]), Math.Max(index[block[i]], index[block[j]])));

                Assert.Equal(expected.OrderBy(p => p).ToArray(), actual.OrderBy(p => p).ToArray());
            }
        }

    }

}
