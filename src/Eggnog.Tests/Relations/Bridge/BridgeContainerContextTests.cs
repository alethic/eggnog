using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Containers on the bridge's paths. Eq-sort containers (containers of e-classes) are not supported in child
    /// contexts: registration passes whether a container type holds ids; a child cannot intern an eq-sort container and
    /// is refused at its rebuild when an eq-sort container holds one of its own losers; containers over base values
    /// work in every context. Named apart from the storage <c>ContainerContextTests</c>.
    /// </summary>
    public class BridgeContainerContextTests
    {

        [Fact]
        public void RegistrationPassesHoldsIds()
        {
            var g = new ContextGraph();
            var values = g.EGraph.RegisterContainerType<VecContainer>(holdsIds: false);
            Assert.Equal(values, g.EGraph.RegisterContainerType<VecContainer>());
            g.EGraph.GetContainerValue(new VecContainer(false, 1, 2));
            Assert.False(g.EGraph.ContainerValues.HasEqContainers);

            var h = new ContextGraph();
            h.EGraph.RegisterContainerType<VecContainer>();
            h.EGraph.GetContainerValue(new VecContainer(h.Root.FreshId().Rep));
            Assert.True(h.EGraph.ContainerValues.HasEqContainers);
        }

        /// <summary>
        /// On the bridge's path, interning an eq-sort container in a child throws before anything changes, and the
        /// child stays usable; a container over base values interns in a child with the id the root gives it.
        /// </summary>
        [Fact]
        public void AChildInternsBaseContainersButNotEqContainers()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            g.EGraph.RegisterContainerType<SetContainer>(holdsIds: false);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.GetContainerValue(new VecContainer(child.FreshId().Rep)));
            Assert.False(child.IsFaulted);

            var set = child.GetContainerValue(new SetContainer(3, 4));
            using var sibling = g.Root.CreateChild();
            Assert.Equal(set, sibling.GetContainerValue(new SetContainer(4, 3)));
            Assert.False(child.Context.IsLocalId(set.Rep));
            g.Assert(child, g.Mark, set);
            child.FlushUpdates();
            Assert.Equal([$"{set.Rep}"], ContextGraph.Keys(child, g.Mark));
            child.CheckInvariants();
        }

        /// <summary>
        /// On the bridge's path, a child whose own union displaces an element of an eq-sort container is refused at the
        /// flush's rebuild with <see cref="NotSupportedException"/> and faulted: the child cannot rebuild the
        /// container, and the elements of eq-sort containers must stay canonical in every clean child.
        /// </summary>
        [Fact]
        public void AChildDisplacingAnEqContainerElementIsRefused()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            var v = g.Root.FreshId();
            var w = g.Root.FreshId();
            g.EGraph.GetContainerValue(new VecContainer(w.Rep));
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            child.StageUnion(v, w);
            Assert.Throws<NotSupportedException>(() => child.FlushUpdates());
            Assert.True(child.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => child.RunRules());

            // Displacing an id no container holds is fine.
            using var other = g.Root.CreateChild();
            var x = other.FreshId();
            other.StageUnion(v, x);
            other.FlushUpdates();
            Assert.Equal(v, other.Find(x));
            other.CheckInvariants();
        }

    }

}
