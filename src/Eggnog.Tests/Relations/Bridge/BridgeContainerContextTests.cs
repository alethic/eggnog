using System;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Containers on the bridge's paths. Registration passes whether a container type holds ids, and a child registers
    /// as the root does; a child interns an eq-sort container (a container of e-classes) in its own container layers,
    /// and rebuilds there those it interned and the ones its own unions displace an element of; containers over base
    /// values work in every context. Named apart from the storage <c>ContainerContextTests</c>.
    /// </summary>
    public class BridgeContainerContextTests
    {

        [Fact]
        public void RegistrationPassesHoldsIds()
        {
            var g = new ContextGraph();
            var values = g.EGraph.RegisterContainerType<VecContainer>(holdsIds: false);
            Assert.Equal(values, g.EGraph.RegisterContainerType<VecContainer>(holdsIds: false));
            g.EGraph.EnsureContainerType<VecContainer>();
            g.EGraph.GetContainerValue(new VecContainer(false, 1, 2));
            Assert.False(g.EGraph.ContainerValues.HasEqContainers);

            // Registered again as holding ids, the one environment holds them, as egglog keeps a container type's eq-sort
            // and base containers in one.
            Assert.Equal(values, g.EGraph.RegisterContainerType<VecContainer>());
            Assert.True(g.EGraph.ContainerValues.HasEqContainers);
            Assert.True(g.EGraph.ContainerValues.GetEnvironment<VecContainer>().HoldsIds);

            var h = new ContextGraph();
            h.EGraph.RegisterContainerType<VecContainer>();
            h.EGraph.GetContainerValue(new VecContainer(h.Root.FreshId().Rep));
            Assert.True(h.EGraph.ContainerValues.HasEqContainers);
        }

        /// <summary>
        /// On the bridge's path, a child interns an eq-sort container its view does not hold in its own container
        /// layer, under an id local to it, which a row of the child may name and which the frozen root and a sibling
        /// never see, as egglog's push interns in its copy; a container over base values interns in a child with the id
        /// the root gives it.
        /// </summary>
        [Fact]
        public void AChildInternsEqContainersInItsLayersAndBaseContainersGlobally()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            g.EGraph.RegisterContainerType<SetContainer>(holdsIds: false);
            g.Root.Freeze();
            var env = g.EGraph.ContainerValues.GetEnvironment<VecContainer>();

            using var child = g.Root.CreateChild();
            var x = child.FreshId();
            var vec = child.GetContainerValue(new VecContainer(x.Rep));
            Assert.False(child.IsFaulted);
            Assert.True(child.Context.IsLocalId(vec.Rep));
            Assert.Equal(vec, child.GetContainerValue(new VecContainer(x.Rep)));
            Assert.Equal(0, env.Count);
            g.Assert(child, g.Mark, vec);

            var set = child.GetContainerValue(new SetContainer(3, 4));
            using var sibling = g.Root.CreateChild();
            Assert.Equal(set, sibling.GetContainerValue(new SetContainer(4, 3)));
            Assert.False(child.Context.IsLocalId(set.Rep));
            Assert.False(ContainerView.Of(sibling.Context.Database).TryGetContainer(env, vec.Rep, out _));
            g.Assert(child, g.Mark, set);
            child.FlushUpdates();
            Assert.Equal(new[] { $"{vec.Rep}", $"{set.Rep}" }.Order(), ContextGraph.Keys(child, g.Mark).Order());
            child.CheckInvariants();
        }

        /// <summary>
        /// Two children of a frozen root, each on its own thread domain, intern through the bridge a container of a
        /// type nobody registered, at once: each registers it as it interns, as egglog declares a container sort inside
        /// a <c>(push)</c>, and registration, which takes the container environments' writer lock, gives the type one
        /// environment that holds ids. Each child holds its container in its own layer, under an id local to it, and
        /// the root's environment holds none.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenRootRegisterAContainerTypeAtOnce()
        {
            for (var round = 0; round < 20; round++)
            {
                var g = new ContextGraph();
                g.Root.Freeze();
                var children = new[] { g.Root.CreateChild(), g.Root.CreateChild() };
                var ids = new Value[children.Length];
                var errors = new Exception?[children.Length];
                var go = new ManualResetEventSlim();
                var threads = new Thread[children.Length];
                for (var i = 0; i < threads.Length; i++)
                {
                    var index = i;
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            var child = children[index];
                            var x = child.FreshId();
                            go.Wait();
                            ids[index] = child.GetContainerValue(new VecContainer(x.Rep));
                        }
                        catch (Exception e)
                        {
                            errors[index] = e;
                        }
                    });
                    threads[i].Start();
                }

                go.Set();
                foreach (var thread in threads)
                    thread.Join();

                Assert.All(errors, Assert.Null);
                var values = g.EGraph.ContainerValues;
                Assert.Equal(1, values.TypeCount);
                var env = values.GetEnvironment<VecContainer>();
                Assert.True(env.HoldsIds);
                Assert.Equal(0, env.Count);
                for (var i = 0; i < children.Length; i++)
                {
                    Assert.True(children[i].Context.IsLocalId(ids[i].Rep));
                    Assert.True(ContainerView.Of(children[i].Context.Database).TryGetContainer(env, ids[i].Rep, out _));
                    children[i].CheckInvariants();
                    children[i].Dispose();
                }

                values.CheckInvariants();
            }
        }

        /// <summary>
        /// On the bridge's path, a child whose own union displaces an element of an eq-sort container rebuilds the
        /// container at the flush's rebuild, in its own container layers under the container's id: the child sees the
        /// rebuilt container, canonical, and the frozen root and a sibling the original.
        /// </summary>
        [Fact]
        public void AChildDisplacingAnEqContainerElementRebuildsItsCopy()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            var v = g.Root.FreshId();
            var w = g.Root.FreshId();
            var vec = g.EGraph.GetContainerValue(new VecContainer(w.Rep));
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            child.StageUnion(v, w);
            child.FlushUpdates();
            Assert.False(child.IsFaulted);
            Assert.Equal(v, child.Find(w));
            var env = g.EGraph.ContainerValues.GetEnvironment<VecContainer>();
            var view = ContainerView.Of(child.Context.Database);
            Assert.True(view.TryGetContainer(env, vec.Rep, out var rebuilt));
            Assert.Equal(new VecContainer(v.Rep), rebuilt);
            Assert.True(view.TryGetId(env, new VecContainer(v.Rep), out var id));
            Assert.Equal(vec.Rep, id);
            Assert.True(env.TryGetContainer(vec.Rep, out var original));
            Assert.Equal(new VecContainer(w.Rep), original);
            child.CheckInvariants();

            // A sibling that displaces an id no container holds claims nothing and sees the root's container.
            using var other = g.Root.CreateChild();
            var x = other.FreshId();
            other.StageUnion(v, x);
            other.FlushUpdates();
            Assert.Equal(v, other.Find(x));
            Assert.True(other.Context.ContainerLayers!.IsEmpty);
            Assert.True(ContainerView.Of(other.Context.Database).TryGetContainer(env, vec.Rep, out var seen));
            Assert.Equal(new VecContainer(w.Rep), seen);
            other.CheckInvariants();
        }

        /// <summary>
        /// egglog's comment on the order of its rebuild loop (egglog-bridge/src/lib.rs:635-665), in a child of a frozen
        /// root: an assert-eq function maps each of <c>x2</c> and <c>x3</c> to the vector of itself. The child's union
        /// of the two makes the vectors equal in its container pass, whose unions it merges before its tables' pass, so
        /// the two rows collapse onto one vector and agree: no merge conflict, as at the root
        /// (<c>EGraphContainerTests.ContainersRebuildBeforeTablesSoNoMergeFunctionsAgree</c>). The rows are the root's,
        /// which the child hides and copies, or the child's own, which its tables' pass rewrites. The root keeps both.
        /// The order shows in the rows the child's merge canonicalizes: none, since the tables' pass already sees the
        /// vector merged away and stages the row as <c>(x2, v2)</c>; were the container unions merged only with the
        /// tables' pass, it would stage <c>(x2, v3)</c>, which the merge would rewrite.
        /// </summary>
        /// <param name="ownRows">Whether the child writes the rows itself.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildMergesContainersBeforeItsTablesSoAssertEqRowsAgree(bool ownRows)
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            var toVec = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "to-vec",
            });
            var x2 = g.Root.FreshId();
            var x3 = g.Root.FreshId();
            var v2 = g.EGraph.GetContainerValue(new VecContainer(x2.Rep));
            var v3 = g.EGraph.GetContainerValue(new VecContainer(x3.Rep));
            var writer = ownRows ? null : g.Root;
            writer?.StageRow(toVec, [x2, v2]);
            writer?.StageRow(toVec, [x3, v3]);
            writer?.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            if (ownRows)
            {
                child.StageRow(toVec, [x2, v2]);
                child.StageRow(toVec, [x3, v3]);
                child.FlushUpdates();
            }

            var statistics = child.Context.Tables!.Statistics;
            var canonicalized = statistics.RowsCanonicalized;
            child.StageUnion(x3, x2);
            child.FlushUpdates();
            Assert.Equal(canonicalized, statistics.RowsCanonicalized);
            Assert.False(child.TakeFailure(out var message), message);
            Assert.False(child.IsFaulted);
            Assert.Equal(v2, child.Find(v3));
            Assert.Equal(v2, child.LookupId(toVec, [x2]));
            Assert.Equal(1, child.TableSize(toVec));
            child.CheckInvariants();
            Assert.Equal(ownRows ? 0 : 2, g.Root.TableSize(toVec));
        }

    }

}
