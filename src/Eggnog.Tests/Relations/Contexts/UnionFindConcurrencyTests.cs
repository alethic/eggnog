using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Concurrency of the layered union-find under a frozen root: many children of a frozen root, each on whichever
    /// thread runs it, union, rebuild, freeze and find concurrently, and the root's union-find is bit-identical
    /// afterwards.
    /// </summary>
    public class UnionFindConcurrencyTests
    {

        /// <summary>
        /// A small union-find over ids with min-canonical classes, for one child's expected answers.
        /// </summary>
        static uint Find(Dictionary<uint, uint> parents, uint x)
        {
            while (parents.TryGetValue(x, out var p) && p != x)
                x = p;

            return x;
        }

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/> in the small union-find, the smaller
        /// root winning.
        /// </summary>
        static void Union(Dictionary<uint, uint> parents, uint a, uint b)
        {
            var ra = Find(parents, a);
            var rb = Find(parents, b);
            if (ra != rb)
                parents[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        /// <summary>
        /// A thousand children of a frozen root with 200,000 ids, on eight threads, union root and local ids, rebuild
        /// and find, a quarter of them freezing under a grandchild; every find matches the expected classes, and the
        /// root's union-find is unchanged.
        /// </summary>
        [Fact]
        public void StressFrozenRoot()
        {
            const int Ids = 200_000;
            const int Children = 1000;
            var store = new Store();
            var root = store.Root;
            for (var i = 0; i < Ids; i++)
                root.NewId();

            var setup = new Random(17);
            root.Write((setup, store.UnionFindTable), static (state, args) =>
            {
                Span<uint> row = stackalloc uint[3];
                for (var i = 0; i < 100_000; i++)
                {
                    row[0] = (uint)args.setup.Next(Ids);
                    row[1] = (uint)args.setup.Next(Ids);
                    row[2] = state.Timestamp;
                    state.StageInsert(args.UnionFindTable, row);
                }

                return 0;
            });
            root.Rebuild();
            root.Freeze();
            root.CheckInvariants();

            var displaced = store.RootUnionFind.UnionFind;
            var parents = displaced.UnionFind.Parents.ToArray();
            var rows = displaced.Children.ToArray();
            var stamps = displaced.Timestamps.ToArray();
            Assert.True(rows.Length > 50_000);

            Parallel.For(0, Children, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var random = new Random(i);
                var child = root.CreateChild();
                var expected = new Dictionary<uint, uint>();
                var locals = Enumerable.Range(0, 3).Select(_ => child.NewId()).ToArray();
                uint Pick() => random.Next(4) == 0 ? locals[random.Next(locals.Length)] : (uint)random.Next(Ids);

                for (var k = 0; k < 20; k++)
                {
                    var a = Pick();
                    var b = Pick();
                    child.Union(a, b);
                    Union(expected, displaced.FindNaive(a), displaced.FindNaive(b));
                }

                child.Rebuild();
                for (var k = 0; k < 100; k++)
                {
                    var x = k < 3 ? locals[k] : (uint)random.Next(Ids);
                    Assert.Equal(Find(expected, displaced.FindNaive(x)), child.Find(x));
                }

                // A quarter freeze and give a grandchild the flattened chain.
                if (i % 4 == 0)
                {
                    child.Freeze();
                    var grandchild = child.CreateChild();
                    var a = (uint)random.Next(Ids);
                    var b = locals[0];
                    grandchild.Union(a, b);
                    grandchild.Rebuild();
                    var own = new Dictionary<uint, uint>(expected);
                    Union(own, displaced.FindNaive(a), displaced.FindNaive(b));
                    for (var k = 0; k < 50; k++)
                    {
                        var x = (uint)random.Next(Ids);
                        Assert.Equal(Find(own, displaced.FindNaive(x)), grandchild.Find(x));
                        Assert.Equal(Find(expected, displaced.FindNaive(x)), child.Find(x));
                    }

                    grandchild.Dispose();
                }

                child.Dispose();
            });

            // The children wrote nothing of the root's: its union-find is bit-identical.
            Assert.Equal(parents, displaced.UnionFind.Parents.ToArray());
            Assert.Equal(rows, displaced.Children.ToArray());
            Assert.Equal(stamps, displaced.Timestamps.ToArray());
            Assert.Equal(0, root.Gate.LiveDescendants);
            Assert.Equal(0, root.ChildCount);
            Assert.True(root.IsFrozen);
            root.CheckInvariants();
        }

    }

}
