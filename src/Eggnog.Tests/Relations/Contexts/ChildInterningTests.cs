using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child context interns into its store's one base-value registry and one container environment, which every
    /// context shares by reference: a value has one cell in every context, whichever interned it, and what a child
    /// interned outlives it. A base-value container interned from a child takes its id from the store's shared counter,
    /// not from the child's id blocks, so it is never local to the child.
    /// </summary>
    public sealed class ChildInterningTests
    {

        /// <summary>
        /// A string a child interns has the same cell at the root and in a sibling, which find it without interning it
        /// again, and it is counted once.
        /// </summary>
        [Fact]
        public void AValueAChildInternsHasOneCellInEveryContext()
        {
            var g = new ContextGraph();
            g.EGraph.BaseValues.RegisterType<StringCodec, string>();
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            using var sibling = g.Root.CreateChild();
            Assert.Same(g.EGraph.BaseValues, child.Context.Database.BaseValues);
            Assert.Same(g.EGraph.BaseValues, sibling.Context.Database.BaseValues);
            var before = g.EGraph.BaseValues.InternedCount;

            var cell = child.Context.Database.BaseValues.Get<StringCodec, string>("interned in a child");

            Assert.Equal(before + 1, g.EGraph.BaseValues.InternedCount);
            Assert.True(g.Root.Context.Database.BaseValues.TryGetExisting<StringCodec, string>("interned in a child", out var atRoot));
            Assert.Equal(cell, atRoot);
            Assert.True(sibling.Context.Database.BaseValues.TryGetExisting<StringCodec, string>("interned in a child", out var inSibling));
            Assert.Equal(cell, inSibling);
            Assert.Equal(cell, sibling.Context.Database.BaseValues.Get<StringCodec, string>("interned in a child"));
            Assert.Equal(before + 1, g.EGraph.BaseValues.InternedCount);
        }

        /// <summary>
        /// After the child that interned a value is disposed, the value still decodes from its cell, is still counted
        /// once, and a later child that interns it gets the same cell without interning it again.
        /// </summary>
        [Fact]
        public void AValueAChildInternedOutlivesTheChild()
        {
            var g = new ContextGraph();
            g.EGraph.BaseValues.RegisterType<StringCodec, string>();
            g.Root.Freeze();
            var before = g.EGraph.BaseValues.InternedCount;
            Value cell;
            using (var child = g.Root.CreateChild())
                cell = child.Context.Database.BaseValues.Get<StringCodec, string>("outlives its child");

            Assert.Equal("outlives its child", g.EGraph.BaseValues.Unwrap<StringCodec, string>(cell));
            Assert.Equal(before + 1, g.EGraph.BaseValues.InternedCount);
            using var later = g.Root.CreateChild();
            Assert.Equal(cell, later.Context.Database.BaseValues.Get<StringCodec, string>("outlives its child"));
            Assert.Equal(before + 1, g.EGraph.BaseValues.InternedCount);
        }

        /// <summary>
        /// A container over base values interned from a child takes the next id of the store's shared counter, not an
        /// id block: the child reserves no block for it, and the id is not local to it. The container stays in the
        /// shared environment after the child is disposed, so a later child gets the same id for an equal container.
        /// </summary>
        [Fact]
        public void ABaseContainerAChildInternsTakesASharedIdAndStays()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<SetContainer>(holdsIds: false);
            g.Root.Freeze();
            var store = g.EGraph.Store;
            Assert.Same(g.EGraph.ContainerValues, g.Root.Context.Database.Containers);
            Value set;
            using (var child = g.Root.CreateChild())
            {
                Assert.Same(g.Root.Context.Database.Containers, child.Context.Database.Containers);
                var next = store.Counters.Read(store.IdCounter);

                set = child.GetContainerValue(new SetContainer(5, 6));

                Assert.Equal(next, set.Rep);
                Assert.Equal(next + 1, store.Counters.Read(store.IdCounter));
                Assert.Equal(0, child.Context.IdBlocks!.BlockCount);
                Assert.False(child.Context.IsLocalId(set.Rep));
            }

            var counter = store.Counters.Read(store.IdCounter);
            using var later = g.Root.CreateChild();
            Assert.Equal(set, later.GetContainerValue(new SetContainer(6, 5)));
            Assert.Equal(counter, store.Counters.Read(store.IdCounter));
        }

    }

}
