using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Member maps: the classes a child merged over its parent's ids, with local ids left out and members canonical in
    /// the parent, cached by the overlay's version, and checked by the invariant checker's member-map check (whose
    /// violations start with "member maps:").
    /// </summary>
    public class MemberMapTests
    {

        /// <summary>
        /// Mints <paramref name="count"/> ids at the root, ascending.
        /// </summary>
        static Value[] Ids(ContextGraph g, int count) => Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();

        [Fact]
        public void AChildThatMergedNothingHasNoGroups()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 3);
            using var child = g.Root.CreateChild();
            var local = child.FreshId();
            var map = child.GetMemberMap();
            Assert.Equal(0, map.GroupCount);
            Assert.True(map.GroupRoots.IsEmpty);
            var id = ids[1].Rep;
            Assert.Equal([id], map.Members(in id).ToArray());
            var l = local.Rep;
            Assert.True(map.Members(in l).IsEmpty);
            Assert.True(map.IsLocal(l));
            child.CheckInvariants();
        }

        [Fact]
        public void GroupsHoldTheirParentMembersAscendingWithTheRoot()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 6);
            using var child = g.Root.CreateChild();
            child.StageUnion(ids[4], ids[0]);
            child.StageUnion(ids[2], ids[0]);
            child.StageUnion(ids[5], ids[3]);
            child.EnsureClean();

            var map = child.GetMemberMap();
            Assert.Equal(2, map.GroupCount);
            Assert.Equal([ids[0].Rep, ids[3].Rep], map.GroupRoots.ToArray());
            var a = ids[0].Rep;
            Assert.Equal([ids[0].Rep, ids[2].Rep, ids[4].Rep], map.Members(in a).ToArray());
            var b = ids[3].Rep;
            Assert.Equal([ids[3].Rep, ids[5].Rep], map.Members(in b).ToArray());
            Assert.Equal(5, map.MemberCount);
            child.CheckInvariants();
        }

        /// <summary>
        /// A local id never appears in a member list; a class whose canonical is local lists its parent members only,
        /// and a local class with no parent member stands for nothing.
        /// </summary>
        [Fact]
        public void LocalIdsAreLeftOut()
        {
            var g = new ContextGraph();
            var parentIds = Ids(g, 2);
            using var child = g.Root.CreateChild();

            // The child's block is reserved by its first mint; ids the root mints afterwards are larger than its
            // locals.
            var local = child.FreshId();
            var other = child.FreshId();
            var late = g.Root.FreshId();
            Assert.True(local.Rep < late.Rep);
            child.StageUnion(local, late);
            child.StageUnion(other, parentIds[0]);
            child.EnsureClean();

            var map = child.GetMemberMap();
            Assert.Equal(2, map.GroupCount);
            var root = child.Find(late).Rep;
            Assert.Equal(local.Rep, root);
            Assert.True(map.IsLocal(root));
            Assert.Equal([late.Rep], map.Members(in root).ToArray());
            var p = parentIds[0].Rep;
            Assert.Equal([p], map.Members(in p).ToArray());

            var lonely = child.FreshId().Rep;
            Assert.True(map.Members(in lonely).IsEmpty);
            child.CheckInvariants();
        }

        [Fact]
        public void TheMapIsCachedWhileTheOverlayVersionHolds()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 4);
            using var child = g.Root.CreateChild();
            child.StageUnion(ids[0], ids[1]);
            child.EnsureClean();
            var first = child.GetMemberMap();
            Assert.Same(first, child.GetMemberMap());

            // A redundant union changes nothing; a new one moves the version.
            child.StageUnion(ids[1], ids[0]);
            child.EnsureClean();
            Assert.Same(first, child.GetMemberMap());
            child.StageUnion(ids[2], ids[3]);
            child.EnsureClean();
            var second = child.GetMemberMap();
            Assert.NotSame(first, second);
            Assert.Equal(2, second.GroupCount);
            Assert.NotSame(second, MemberMap.Build(child));
            Assert.Equal(second.GroupRoots.ToArray(), MemberMap.Build(child).GroupRoots.ToArray());
            child.CheckInvariants();
        }

        /// <summary>
        /// After the parent merges ids of a child's class, the child re-keys and its map lists parent-canonical members
        /// only.
        /// </summary>
        [Fact]
        public void MembersFollowTheParentsUnions()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 4);
            using var child = g.Root.CreateChild();
            child.StageUnion(ids[1], ids[3]);
            child.EnsureClean();
            var a = ids[1].Rep;
            Assert.Equal([ids[1].Rep, ids[3].Rep], child.GetMemberMap().Members(in a).ToArray());

            g.Root.StageUnion(ids[0], ids[3]);
            g.Root.FlushUpdates();
            child.EnsureClean();
            var map = child.GetMemberMap();
            var root = child.Find(ids[1]).Rep;
            Assert.Equal(ids[0].Rep, root);
            Assert.Equal([ids[0].Rep, ids[1].Rep], map.Members(in root).ToArray());
            child.CheckInvariants();
        }

        [Fact]
        public void TheRootHasNoMemberMap()
        {
            var g = new ContextGraph();
            Assert.Throws<InvalidOperationException>(() => g.Root.GetMemberMap());
        }

        [Fact]
        public void ANotCleanChildIsRefused()
        {
            var g = new ContextGraph();
            var ids = Ids(g, 2);
            using var child = g.Root.CreateChild();
            child.StageUnion(ids[0], ids[1]);
            Assert.Throws<InvalidOperationException>(() => child.GetMemberMap());
        }

        /// <summary>
        /// Random unions, local mints and parent unions keep the map exact, as the invariant checker's member-map check
        /// requires, and the map agrees with the context's find.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RandomUnionsKeepTheMapExact(int seed)
        {
            var random = new Random(seed);
            var g = new ContextGraph();
            var ids = Ids(g, 12);
            using var child = g.Root.CreateChild();
            var locals = Enumerable.Range(0, 4).Select(_ => child.FreshId()).ToArray();
            var all = ids.Concat(locals).ToArray();
            for (var step = 0; step < 20; step++)
            {
                if (random.Next(4) == 0)
                {
                    g.Root.StageUnion(ids[random.Next(ids.Length)], ids[random.Next(ids.Length)]);
                    g.Root.FlushUpdates();
                }
                else
                {
                    child.StageUnion(all[random.Next(all.Length)], all[random.Next(all.Length)]);
                }

                child.EnsureClean();
                child.CheckInvariants();
                var map = child.GetMemberMap();
                foreach (var id in ids)
                {
                    var canonical = child.Find(id).Rep;
                    var members = map.Members(in canonical).ToArray();
                    if (g.Root.Find(id).Rep == id.Rep)
                        Assert.Contains(id.Rep, members);
                    else
                        Assert.DoesNotContain(id.Rep, members);

                    foreach (var m in members)
                        Assert.Equal(canonical, child.Find(new Value(m)).Rep);
                }
            }
        }

        /// <summary>
        /// A graph whose contradiction function is designated, with <paramref name="count"/> root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        /// <summary>
        /// The invariant checker's member-map check reports a cached member map whose version is current but whose
        /// groups differ from the overlay's. The corrupt map is another case's map at the same overlay version.
        /// </summary>
        [Fact]
        public void TheMemberMapCheckReportsACachedMapThatDiffersFromTheOverlay()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            var two = set.AddCase();
            two.StageUnion(ids[2], ids[3]);
            one.EnsureClean();
            two.EnsureClean();
            var mapOne = one.GetMemberMap();
            var mapTwo = two.GetMemberMap();
            Assert.Equal(mapOne.Version, mapTwo.Version);
            one.CheckInvariants();

            var field = typeof(EGraphContext).GetField("memberMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(one, mapTwo);
            var thrown = Assert.Throws<ContextInvariantException>(() => one.CheckInvariants());
            Assert.Contains("member maps:", thrown.Message);
        }

    }

}
