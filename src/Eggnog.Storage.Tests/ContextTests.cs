using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Storage.Tests
{

    /// <summary>
    /// The behaviours a reader would look for first, each on its own.
    /// </summary>
    public class ContextTests
    {

        static (Store Store, Table Pair, Table Low) Create()
        {
            var store = new Store();
            var pair = store.DefineTable(TableSchema.Constructor("pair", ColumnKind.Id, ColumnKind.Id));
            var low = store.DefineTable(TableSchema.Lattice("low", Math.Min, ColumnKind.Id));
            return (store, pair, low);
        }

        static ulong V(Id id) => id.ToValue();

        [Fact]
        public void MergingArgumentsMergesWhatIsBuiltFromThem()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var ab = root.GetOrAdd(pair, new[] { V(a), V(b) });
            var cb = root.GetOrAdd(pair, new[] { V(c), V(b) });

            root.Union(a, c);
            root.Rebuild();

            Assert.True(root.AreEqual(ab, cb));
        }

        [Fact]
        public void AMergeInAChildIsSeenByNeitherItsParentNorItsSibling()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var ab = root.GetOrAdd(pair, new[] { V(a), V(b) });
            var cb = root.GetOrAdd(pair, new[] { V(c), V(b) });

            var assumed = root.CreateChild();
            var sibling = root.CreateChild();

            assumed.Union(a, c);
            assumed.Rebuild();
            sibling.Rebuild();
            root.Rebuild();

            Assert.True(assumed.AreEqual(ab, cb));
            Assert.False(root.AreEqual(ab, cb));
            Assert.False(sibling.AreEqual(ab, cb));
        }

        [Fact]
        public void AChildSeesWhatItsParentLearnsAfterItWasMade()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var child = root.CreateChild();

            // The child merges a and c, then the parent builds over them.
            child.Union(a, c);
            child.Rebuild();

            var ab = root.GetOrAdd(pair, new[] { V(a), V(b) });
            var cb = root.GetOrAdd(pair, new[] { V(c), V(b) });

            Assert.Throws<InvalidOperationException>(() => child.AreEqual(ab, cb));

            child.Rebuild();
            Assert.True(child.AreEqual(ab, cb));
            Assert.False(root.AreEqual(ab, cb));
        }

        [Fact]
        public void AParentMergeReachesTheChildsCopies()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var ab = root.GetOrAdd(pair, new[] { V(a), V(b) });
            var db = root.GetOrAdd(pair, new[] { V(d), V(b) });

            var child = root.CreateChild();
            child.Union(a, c);

            // The parent then merges c with d: in the child a, c and d are now one class.
            root.Union(c, d);
            child.Rebuild();

            Assert.True(child.AreEqual(a, d));
            Assert.True(child.AreEqual(ab, db));
            Assert.False(root.AreEqual(a, d));
        }

        [Fact]
        public void ALatticeValueInAChildCombinesWithTheParentsLaterOne()
        {
            var (store, _, low) = Create();
            var root = store.Root;

            var a = root.NewId();
            root.Insert(low, new[] { V(a) }, 9);

            var child = root.CreateChild();
            child.Insert(low, new[] { V(a) }, 5);

            root.Insert(low, new[] { V(a) }, 3);
            child.Rebuild();
            root.Rebuild();

            Assert.True(child.TryLookup(low, new[] { V(a) }, out var inChild));
            Assert.True(root.TryLookup(low, new[] { V(a) }, out var inRoot));
            Assert.Equal(3UL, inChild);
            Assert.Equal(3UL, inRoot);

            child.Insert(low, new[] { V(a) }, 1);
            child.Rebuild();
            Assert.True(child.TryLookup(low, new[] { V(a) }, out inChild));
            Assert.Equal(1UL, inChild);
            Assert.True(root.TryLookup(low, new[] { V(a) }, out inRoot));
            Assert.Equal(3UL, inRoot);
        }

        [Fact]
        public void AGrandchildSeesBothItsAncestorsAssumptions()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var ab = root.GetOrAdd(pair, new[] { V(a), V(b) });
            var cd = root.GetOrAdd(pair, new[] { V(c), V(d) });

            var child = root.CreateChild();
            child.Union(a, c);
            var grandchild = child.CreateChild();
            grandchild.Union(b, d);
            grandchild.Rebuild();
            child.Rebuild();

            Assert.True(grandchild.AreEqual(ab, cd));
            Assert.False(child.AreEqual(ab, cd));
        }

        [Fact]
        public void AFrozenContextRefusesWrites()
        {
            var (store, _, _) = Create();
            store.Root.NewId();
            store.Root.Freeze();

            Assert.Throws<InvalidOperationException>(() => store.Root.NewId());
            Assert.NotNull(store.Root.CreateChild());
        }

        [Fact]
        public void AContextIsDroppedOnlyOnceItHasNoChildren()
        {
            var (store, _, _) = Create();
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();

            Assert.Throws<InvalidOperationException>(() => child.Dispose());
            Assert.Throws<InvalidOperationException>(() => store.Root.Dispose());

            grandchild.Dispose();
            child.Dispose();

            Assert.Empty(store.Root.Children);
            Assert.Throws<ObjectDisposedException>(() => child.NewId());
        }

        /// <summary>
        /// The Cosmos case the store was built for: under the assumption that a discriminator holds one value, a
        /// comparison the service cannot run is equal to one it can, and everything built from the first is equal
        /// to the same thing built from the second — in that context, and nowhere else.
        /// </summary>
        [Fact]
        public void AnEqualityThatHoldsUnderOneAssumptionStaysUnderIt()
        {
            var store = new Store();
            var uuidEq = store.DefineTable(TableSchema.Constructor("UuidEq", ColumnKind.Value, ColumnKind.Value));
            var strEq = store.DefineTable(TableSchema.Constructor("StrEq", ColumnKind.Value, ColumnKind.Value));
            var and = store.DefineTable(TableSchema.Constructor("And", ColumnKind.Id, ColumnKind.Id));
            const ulong kind = 1, order = 2, invoice = 3, note = 4, @ref = 5, literal = 6;

            var schema = store.Root;
            var lowered = schema.GetOrAdd(strEq, new[] { @ref, literal });
            schema.Freeze();

            var query = schema.CreateChild();
            var guard = query.GetOrAdd(strEq, new[] { kind, order });
            var asked = query.GetOrAdd(uuidEq, new[] { @ref, literal });
            var predicate = query.GetOrAdd(and, new[] { V(guard), V(asked) });
            var rewritten = query.GetOrAdd(and, new[] { V(guard), V(lowered) });

            var orders = query.CreateChild();
            var invoices = query.CreateChild();
            var notes = query.CreateChild();

            orders.Union(asked, lowered);
            invoices.Union(asked, lowered);

            foreach (var context in new[] { query, orders, invoices, notes })
                context.Rebuild();

            Assert.True(orders.AreEqual(predicate, rewritten));
            Assert.True(invoices.AreEqual(predicate, rewritten));
            Assert.False(notes.AreEqual(predicate, rewritten));
            Assert.False(query.AreEqual(predicate, rewritten));

            _ = (invoice, note);
        }

        /// <summary>
        /// A merge at the bottom of two long chains built in the parent makes the tops equal in the child, while
        /// the parent keeps them apart.
        /// </summary>
        [Fact]
        public void CongruenceCascadesThroughAChildWithoutTouchingTheParent()
        {
            var store = new Store();
            var wrap = store.DefineTable(TableSchema.Constructor("wrap", ColumnKind.Id));
            var root = store.Root;

            const int length = 2000;
            var left = root.NewId();
            var right = root.NewId();
            var top = (Left: left, Right: right);

            for (var i = 0; i < length; i++)
                top = (root.GetOrAdd(wrap, new[] { V(top.Left) }), root.GetOrAdd(wrap, new[] { V(top.Right) }));

            root.Freeze();

            var child = root.CreateChild();
            child.Union(left, right);
            child.Rebuild();

            Assert.True(child.AreEqual(top.Left, top.Right));
            Assert.False(root.AreEqual(top.Left, top.Right));

            var count = 0;
            child.Scan(wrap, _ => count++);
            Assert.Equal(length, count);
        }

        [Fact]
        public void ALogIsDiscardedOnceEveryReaderHasTakenItIn()
        {
            var (store, pair, _) = Create();
            var root = store.Root;
            var child = root.CreateChild();
            var grandchild = child.CreateChild();

            var a = root.NewId();
            root.GetOrAdd(pair, new[] { V(a), V(a) });
            root.Rebuild();
            Assert.True(root.LogLength > 0);

            // The child has caught up; the grandchild, which reads the root's log too, has not.
            child.Rebuild();
            root.Rebuild();
            Assert.True(root.LogLength > 0);

            grandchild.Rebuild();
            root.Rebuild();
            Assert.Equal(0, root.LogLength);
        }

        [Fact]
        public void AnIdleChildHoldsTheLogBackAndDroppingItReleasesIt()
        {
            var (store, pair, _) = Create();
            var root = store.Root;
            var child = root.CreateChild();

            var a = root.NewId();
            root.GetOrAdd(pair, new[] { V(a), V(a) });
            root.Rebuild();
            Assert.True(root.LogLength > 0);

            child.Dispose();
            root.Rebuild();
            Assert.Equal(0, root.LogLength);
        }

        [Fact]
        public void RewrittenRowsAreCompactedAway()
        {
            var store = new Store();
            var wrap = store.DefineTable(TableSchema.Constructor("wrap", ColumnKind.Id));
            var root = store.Root;

            // Two chains, then their bottoms merged: every row of one chain is rewritten onto the other.
            const int length = 200;
            var left = root.NewId();
            var right = root.NewId();
            var (l, r) = (left, right);
            for (var i = 0; i < length; i++)
                (l, r) = (root.GetOrAdd(wrap, new[] { V(l) }), root.GetOrAdd(wrap, new[] { V(r) }));

            root.Rebuild();
            Assert.Equal(2 * length, root.SlotsHeld(wrap));

            root.Union(left, right);
            root.Rebuild();

            Assert.True(root.AreEqual(l, r));
            Assert.Equal(length, root.SlotsHeld(wrap));

            var count = 0;
            root.Scan(wrap, _ => count++);
            Assert.Equal(length, count);
        }

        [Fact]
        public void AScanSinceAClockReadingSeesOnlyLaterRows()
        {
            var (store, pair, _) = Create();
            var root = store.Root;

            var a = root.NewId();
            var b = root.NewId();
            root.GetOrAdd(pair, new[] { V(a), V(a) });
            root.Rebuild();

            var clock = store.Clock;
            root.GetOrAdd(pair, new[] { V(a), V(b) });
            root.Rebuild();

            var seen = new List<ulong[]>();
            root.Scan(pair, r => seen.Add(r.ToArray()), clock);

            var row = Assert.Single(seen);
            Assert.Equal(V(b), row[1]);
        }

    }

}
