using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An instantiation that a header proves empty allocates nothing once its rule set builder's scratch has held as many
    /// headers: the idle semi-naive variant, dropped on its focus atom's timestamp bound, at a root and in a child whose
    /// table spans several layers. The instance's arrays are made only once every header is nonempty, and the pooled
    /// buffers of the headers and layers left out are returned.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public sealed class HeaderInstantiationAllocationTests
    {

        /// <summary>
        /// A chain root → middle → leaf, each writing ten rows of <c>f(k) → v</c> at its own epoch, the root and the
        /// middle frozen, and the cached plan of <c>f(k, _, _)</c> planned in the leaf. The root writes 1, the middle 2
        /// and the leaf 3; with <paramref name="mixed"/>, the root and the middle write 4 and the leaf alternates 3 and
        /// 4, so <c>v = 3</c> selects some of the leaf's rows and none of the others.
        /// </summary>
        static (Context Leaf, CachedPlan Cached, uint[] Epochs) Chain(bool mixed = false)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 30).Select(_ => root.NewId()).ToArray();
            var epochs = new uint[3];
            epochs[0] = Write(root, f, ids[..10], _ => mixed ? 4u : 1u);
            root.Freeze();
            var middle = root.CreateChild();
            epochs[1] = Write(middle, f, ids[10..20], _ => mixed ? 4u : 2u);
            middle.Freeze();
            var leaf = middle.CreateChild();
            epochs[2] = Write(leaf, f, ids[20..], i => mixed ? 3u + (uint)(i % 2) : 3u);
            return (leaf, Plan(leaf.Database, f), epochs);
        }

        static uint Write(Context context, TableId f, uint[] keys, Func<int, uint> value) =>
            context.Write(state =>
            {
                for (var i = 0; i < keys.Length; i++)
                    state.StageInsert(f, [keys[i], value(i), state.Timestamp]);

                return state.Timestamp;
            });

        static CachedPlan Plan(Database db, TableId f)
        {
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var k = q.NewVariable("k");
            q.AddAtom(f, [k, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.AssertEq(k, k);
            var rule = rb.Build("all");
            using var built = rsb.Build();
            return built.BuildCachedPlan(rule);
        }

        /// <summary>
        /// The windows of 1,000 calls <see cref="BytesPerThousand"/> measures, at most.
        /// </summary>
        const int Windows = 5;

        /// <summary>
        /// Counts the bytes the current thread allocates over 1,000 calls of <paramref name="add"/> on one builder, after
        /// 100 warm-up calls: the least of up to <see cref="Windows"/> windows, stopping at one that allocates nothing. An
        /// allocation per call shows in every window; a one-off that does not depend on the calls (a pool a collection
        /// trimmed, refilled by the next rental) shows in at most one.
        /// </summary>
        static long BytesPerThousand(RuleSetBuilder builder, Func<RuleSetBuilder, RuleId?> add)
        {
            Assert.Null(add(builder));
            for (var i = 0; i < 100; i++)
                add(builder);

            var least = long.MaxValue;
            for (var window = 0; window < Windows && least > 0; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                    add(builder);

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least;
        }

        [Fact]
        public void AVariantEmptyInEveryLayerAllocatesNothing()
        {
            var (leaf, cached, epochs) = Chain();
            (AtomId, Constraint)[] newer = [(new AtomId(0), Constraint.GtConst(new ColumnId(2), new Value(epochs[2])))];
            var builder = leaf.Database.NewRuleSet();

            Assert.Equal(0, BytesPerThousand(builder, b => b.AddRuleFromCachedPlan(cached, newer)));
            Assert.Equal(0, builder.Count);
        }

        [Fact]
        public void AVariantWhoseLaterHeaderIsEmptyReleasesTheEarlierOnesWithoutAllocating()
        {
            var (leaf, cached, epochs) = Chain();
            (AtomId, Constraint)[] extras =
            [
                (new AtomId(0), Constraint.GeConst(new ColumnId(2), new Value(epochs[2]))),
                (new AtomId(0), Constraint.GtConst(new ColumnId(2), new Value(epochs[2]))),
            ];
            var builder = leaf.Database.NewRuleSet();

            Assert.Equal(0, BytesPerThousand(builder, b => b.AddRuleFromCachedPlan(cached, extras)));

            // The scratch keeps nothing: the next variant's headers are its own.
            var kept = builder.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(new ColumnId(2), new Value(epochs[1])))]);
            Assert.Equal(new RuleId(0), kept);
            using var rs = builder.Build();
            var instance = rs[kept!.Value].Instance!;
            var header = Assert.Single(instance.HeaderSubsets);
            Assert.Equal(0b110u, header.LayerMask);
            Assert.Equal(20, header.Size);
            Assert.Equal([Constraint.GeConst(new ColumnId(2), new Value(epochs[1]))], instance.Headers[0].Constraints);
        }

        /// <summary>
        /// The earlier header, <c>v = 3</c>, is a sparse selection of the leaf's rows, which holds a pooled buffer; the
        /// later one is empty, so the instantiation must return that buffer.
        /// </summary>
        [Fact]
        public void AVariantWhoseLaterHeaderIsEmptyReturnsAnEarlierSparseHeadersBuffer()
        {
            var (leaf, cached, epochs) = Chain(mixed: true);
            (AtomId, Constraint)[] extras =
            [
                (new AtomId(0), Constraint.EqConst(new ColumnId(1), new Value(3))),
                (new AtomId(0), Constraint.GtConst(new ColumnId(2), new Value(epochs[2]))),
            ];
            var builder = leaf.Database.NewRuleSet();

            Assert.Equal(0, BytesPerThousand(builder, b => b.AddRuleFromCachedPlan(cached, extras)));
        }

        /// <summary>
        /// A header that collapses to one layer returns the buffers of the layers it leaves out: the root's rows with
        /// <c>v = 2</c> are a sparse selection, which the key then empties, and only the leaf's row remains.
        /// </summary>
        [Fact]
        public void AHeaderLeftInOneLayerReturnsTheBufferOfALayerItEmptied()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 3).Select(_ => root.NewId()).ToArray();
            Write(root, f, ids, i => i == 1 ? 1u : 2u);
            root.Freeze();
            var leaf = root.CreateChild();
            Write(leaf, f, [ids[1]], _ => 2u);

            var view = leaf.Database.View;
            var scratch = new HeaderScratch();
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            Constraint[] both = [Constraint.EqConst(new ColumnId(1), new Value(2)), Constraint.EqConst(new ColumnId(0), new Value(ids[1]))];
            for (var i = 0; i < 2; i++)
            {
                fast.Clear();
                slow.Clear();
                var rows = view.Evaluate(f, both, fast, slow, scratch);
                Assert.Equal(0b10u, rows.LayerMask);
                Assert.Equal(1, rows.Size);
                rows.Dispose(null);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                fast.Clear();
                slow.Clear();
                view.Evaluate(f, both, fast, slow, scratch).Dispose(null);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void AVariantEmptyAtARootAllocatesNothing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var epoch = Write(root, f, Enumerable.Range(0, 10).Select(_ => root.NewId()).ToArray(), _ => 1u);
            var cached = Plan(root.Database, f);
            (AtomId, Constraint)[] newer = [(new AtomId(0), Constraint.GtConst(new ColumnId(2), new Value(epoch)))];
            var builder = root.Database.NewRuleSet();

            Assert.Equal(0, BytesPerThousand(builder, b => b.AddRuleFromCachedPlan(cached, newer)));
        }

    }

}
