using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="RetractionGuard"/> and its enforcement points (colored-contexts spec §6.7, decision 1, R1,
    /// I22): every cell of the guard's table, for shared and context-private tables, and the guard execution states and
    /// raw buffers carry.
    /// </summary>
    public class RetractionTests
    {

        /// <summary>
        /// Whether a check passes.
        /// </summary>
        static bool Allowed(Action check)
        {
            try
            {
                check();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Asserts the three checks of <paramref name="guard"/> for a shared and a private table.
        /// </summary>
        static void AssertCell(RetractionGuard guard, TableId shared, TableId @private, bool remove, bool clear, bool subsume)
        {
            Assert.Equal(remove, Allowed(() => guard.CheckRemove(shared)));
            Assert.Equal(clear, Allowed(() => guard.CheckClear(shared)));
            Assert.Equal(subsume, Allowed(() => guard.CheckSubsume(shared)));
            guard.CheckRemove(@private);
            guard.CheckClear(@private);
            guard.CheckSubsume(@private);
        }

        [Fact]
        public void EveryCellOfTheTableHolds()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var root = store.Root;
            var rootGuard = store.Schema.RootGuard;
            Assert.Same(rootGuard, root.Database.RetractionGuard);

            // Root, no live descendants: everything.
            AssertCell(rootGuard, f, p, true, true, true);

            // Root, live descendants: nothing shared; the union-find is never private.
            var writable = root.CreateChild();
            AssertCell(rootGuard, f, p, false, false, false);
            Assert.False(Allowed(() => rootGuard.CheckClear(store.UnionFindTable)));

            // Child, no live descendants, parent writable: subsume only (R1).
            var guard = writable.Tables!.Guard;
            Assert.Same(guard, writable.Database.RetractionGuard);
            AssertCell(guard, f, p, false, false, true);

            // Child, live descendants: nothing shared.
            var grandchild = writable.CreateChild();
            AssertCell(guard, f, p, false, false, false);
            AssertCell(grandchild.Tables!.Guard, f, p, false, false, true);
            grandchild.Dispose();
            writable.Dispose();

            // Child, no live descendants, parent frozen: everything.
            root.Freeze();
            var child = root.CreateChild();
            AssertCell(child.Tables!.Guard, f, p, true, true, true);
            var below = child.CreateChild();
            AssertCell(child.Tables.Guard, f, p, false, false, false);
            below.Dispose();
            AssertCell(child.Tables.Guard, f, p, true, true, true);
        }

        /// <summary>
        /// A bare database's states carry no guard; a store's always carry their database's, which decides when a removal
        /// is staged. The spec's snapshot (no guard at a root without live descendants) let a state created before a child
        /// delete the child's rows after it (review of M9.3); the root guard instead returns after one gate read while the
        /// root has no live descendants.
        /// </summary>
        [Fact]
        public void AStoreStateAlwaysCarriesItsDatabasesGuard()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            Assert.Null(new Database().CreateExecutionState().Guard);
            Assert.Same(store.Schema.RootGuard, root.Database.GuardForNewState());

            // Without a descendant the root's guard allows everything.
            var early = root.Database.CreateExecutionState();
            Assert.Same(store.Schema.RootGuard, early.Guard);
            early.StageRemove(f, [1u]);

            // With a descendant the root's states, their clones and the states made before the child all refuse.
            var child = root.CreateChild();
            var state = root.Database.CreateExecutionState();
            Assert.Same(store.Schema.RootGuard, state.Guard);
            Assert.Same(state.Guard, state.Clone().Guard);
            Assert.Same(child.Tables!.Guard, child.Database.CreateExecutionState().Guard);
            Assert.Throws<InvalidOperationException>(() => early.StageRemove(f, [1u]));
            Assert.Throws<InvalidOperationException>(() => state.Clone().StageRemove(f, [1u]));

            // Once the child is gone the same state may remove again.
            child.Dispose();
            early.StageRemove(f, [1u]);
        }

        /// <summary>
        /// The buffers of <see cref="Database.NewBuffer"/> carry their database's guard (I22 at every entry point), and so do
        /// their fresh handles; the buffers of a table's own rebuild (<see cref="Table.NewBuffer"/>) carry none.
        /// </summary>
        [Fact]
        public void RawBuffersPassTheGuard()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var root = store.Root;
            Insert(root, f, 1, 5, 0);
            var early = root.Database.NewBuffer(f);
            var child = root.CreateChild();

            Assert.Throws<InvalidOperationException>(() => early.StageRemove([1u]));
            Assert.Throws<InvalidOperationException>(() => early.FreshHandle().StageRemove([1u]));
            Assert.Throws<InvalidOperationException>(() => root.Database.NewBuffer(f).StageRemove(new uint[] { 1 }, 0));
            root.Database.NewBuffer(p).StageRemove([1u]);
            store.Schema[f]!.Root.NewBuffer().StageRemove([1u]);

            // Inserts are not retractions; nothing above was flushed, and the child still sees the root's row.
            early.StageInsert([2u, 3u, 0u]);
            Assert.Equal(5u, Row(child, f, 1)?[1]);

            // A child's raw buffer under a writable parent refuses too (R1), before anything is staged.
            Assert.Throws<InvalidOperationException>(() => child.Database.NewBuffer(f).StageRemove([1u]));
            Assert.False(child.IsFaulted);
        }

        [Fact]
        public void EveryEnforcementPointConsultsTheGuard()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, p, a, 1, 0);
            var child = root.CreateChild();

            // The root has a live descendant: staged removals (both forms), a rule's remove and clears are refused;
            // nothing was staged, so nothing is pending afterwards.
            var state = root.Database.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => state.StageRemove(f, [a]));
            Assert.Throws<InvalidOperationException>(() => state.StageRemove(f, new[] { a }, 0));
            var bindings = new Bindings();
            bindings.Insert(new Variable(0), [a]);
            Assert.Throws<InvalidOperationException>(() => state.RunInstrs([new RemoveInstr(f, [QueryEntry.Var(new Variable(0))])], bindings));
            Assert.Throws<InvalidOperationException>(() => root.Database.ClearTable(f));
            Assert.Throws<InvalidOperationException>(() => root.ClearTable(f));
            Assert.Throws<InvalidOperationException>(() => root.Database.ClearTable(store.UnionFindTable));
            state.StageRemove(p, [a]);
            root.ClearTable(p);
            Assert.True(root.Database.Catalog.Notifications.IsEmpty == false);
            root.Rebuild();
            Assert.Equal(1u, Row(root, f, a)![1]);

            // A child under a writable parent: removals and clears of shared tables are refused (R1) and change nothing.
            var refused = child.Database.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => refused.StageRemove(f, [a]));
            Assert.Throws<InvalidOperationException>(() => child.ClearTable(f));
            Assert.Throws<InvalidOperationException>(() => child.Database.ClearTable(f));
            Assert.False(child.IsFaulted);
            Assert.Null(child.Tables!.TryGetLayer(f));

            // Through a write batch the refusal escapes the batch and faults the child (X12).
            Assert.Throws<InvalidOperationException>(() => Remove(child, f, a));
            Assert.True(child.IsFaulted);
            child.Dispose();

            // Under a frozen parent the child may delete and clear.
            root.Freeze();
            var allowed = root.CreateChild();
            Remove(allowed, f, a);
            Assert.Null(Row(allowed, f, a));
            allowed.ClearTable(p);
            allowed.ClearTable(f);
            Assert.Equal(0, allowed.GetView(f).VisibleCount);
            Assert.Equal(1u, Row(root, f, a)![1]);
        }

    }

}
