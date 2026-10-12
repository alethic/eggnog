using System;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Isolation of a frozen root's union-find (colored-contexts spec I11, I23, §5.4): children of a frozen root find,
    /// union and rebuild over ids of their own, which lie above every id the root reserved. The root's parent array must
    /// stay the same object and the same contents: a find that reserved or compressed on the way (egglog's writing
    /// <c>find</c> in place of <c>FindNaive</c>) would grow or rewrite it from many threads at once.
    /// <c>FreezeTests.FrozenFindsDoNotWrite</c> only finds root ids on a fully compressed root, where such a find writes
    /// nothing observable.
    /// </summary>
    public class FrozenRootIsolationReviewTests
    {

        [Fact]
        public void ChildrenWorkingOnLocalIdsNeverWriteTheFrozenRootsUnionFind()
        {
            const int Ids = 200;
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, Ids).Select(_ => root.NewId()).ToArray();
            for (var i = 0; i + 1 < Ids; i += 3)
                root.Union(ids[i], ids[i + 1]);

            for (var i = 0; i < Ids; i += 2)
                Insert(root, f, ids[i], (uint)i, 0);

            root.Rebuild();
            root.Freeze();

            var before = store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray();
            var failures = 0;
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                try
                {
                    var random = new Random(7 + t);
                    for (var round = 0; round < 20; round++)
                    {
                        using var child = root.CreateChild();
                        var local = Enumerable.Range(0, 6).Select(_ => child.NewId()).ToArray();
                        child.Union(local[0], local[1]);
                        child.Union(local[2], ids[random.Next(Ids)]);
                        child.Union(ids[random.Next(Ids)], ids[random.Next(Ids)]);
                        Insert(child, f, local[3], 1, 0);
                        Insert(child, f, local[4], 2, 0);
                        child.Rebuild();
                        foreach (var id in local)
                            child.Find(id);

                        child.CheckInvariants();
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failures);
                }
            })).ToArray();

            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                thread.Join();

            Assert.Equal(0, failures);
            Assert.Equal(before, store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray());
        }

    }

}
