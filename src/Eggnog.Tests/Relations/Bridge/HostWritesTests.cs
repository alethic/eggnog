using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="HostWrites"/>: rows and unions kept bridge-side, replayed stamped with the flush's epoch, id cells
    /// canonicalized only for rows staged through the context API; staging rolled back on a refused call, clears that
    /// drop one function's staged rows, and arenas released after a large batch.
    /// </summary>
    public class HostWritesTests
    {

        [Fact]
        public void ReplayStampsCanonicalizesAndKeepsOrder()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            BridgeTestUtil.Union(g.EGraph, a, b);

            var writes = new HostWrites();
            Assert.True(writes.IsEmpty);
            writes.AddRow(g.Mark, [b.Rep, g.Unit.Rep], canonicalizeIds: true);
            writes.AddRow(g.Path, [b.Rep, b.Rep, g.Unit.Rep], canonicalizeIds: false);
            writes.AddUnion(a.Rep, b.Rep);
            Assert.Equal(2, writes.RowCount);
            Assert.Equal(1, writes.UnionCount);
            Assert.False(writes.IsEmpty);

            var timestamp = g.EGraph.NextTimestamp;
            var database = g.EGraph.Database;
            var state = database.CreateExecutionState(timestamp);
            writes.ReplayInto(state, timestamp, g.EGraph, g.Root.Context.UnionFind);
            state.Flush();
            database.MergeAll(timestamp);

            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal([$"{b.Rep} {b.Rep}"], ContextGraph.Keys(g.Root, g.Path));
            var mark = BridgeTestUtil.PhysicalRow(g.EGraph, g.Mark, a.Rep);
            Assert.Equal(timestamp, mark[g.EGraph.GetFunctionInfo(g.Mark).Math.TimestampColumn]);

            // Replay does not clear; Clear keeps nothing; Release drops the storage.
            Assert.False(writes.IsEmpty);
            writes.Clear();
            Assert.True(writes.IsEmpty);
            writes.AddUnion(a.Rep, b.Rep);
            writes.Release();
            Assert.True(writes.IsEmpty);
        }

        /// <summary>
        /// Rows wider than the stack buffer and many rows grow the arenas; every row replays intact.
        /// </summary>
        [Fact]
        public void WideAndManyRowsReplayIntact()
        {
            var g = new ContextGraph();
            var keys = Enumerable.Repeat(ColumnTy.Id, 40).ToArray();
            var wide = g.Relation("wide", keys);
            var ids = Enumerable.Range(0, 40).Select(_ => g.Root.FreshId()).ToArray();
            for (var r = 0; r < 100; r++)
            {
                var row = ids.Select((v, i) => i == r % 40 ? g.Root.FreshId() : v).Append(g.Unit).ToArray();
                g.Root.StageRow(wide, row);
            }

            g.Root.FlushUpdates();
            Assert.Equal(100, g.Root.TableSize(wide));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// <see cref="RuleProgress.SameConstants"/> compares executed constants by value.
        /// </summary>
        [Fact]
        public void ProgressComparesConstantsByValue()
        {
            Assert.True(RuleProgress.SameConstants(null, null));
            Assert.False(RuleProgress.SameConstants(null, []));
            Assert.True(RuleProgress.SameConstants([1, 2], [1, 2]));
            Assert.False(RuleProgress.SameConstants([1, 2], [2, 1]));
            Assert.False(RuleProgress.SameConstants([1], [1, 2]));
            Assert.Equal(0, default(RuleProgress).Generation);
            Assert.Equal("(none)", default(RuleProgress).ToString());
        }

        /// <summary>
        /// Adds a function over one id key whose output is an i64 merged by <paramref name="merge"/>.
        /// </summary>
        static FunctionId ValueFunction(ContextGraph g, MergeFn merge, string name) => g.EGraph.AddTable(new FunctionConfig
        {
            Schema = [ColumnTy.Id, g.I64],
            Default = DefaultVal.Fail,
            Merge = merge,
            Name = name,
        });

        /// <summary>
        /// The context API's <see cref="EGraphContext.AddValues"/> stages each row as it validates it and drops the
        /// call's rows on a malformed one (<see cref="HostWrites.TruncateRows"/>): rows staged by earlier calls stay
        /// staged and land at the next flush; none of the refused call's rows do.
        /// </summary>
        [Fact]
        public void ARefusedAddValuesKeepsTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.StageUnion(b, c);

            Assert.Throws<ArgumentException>(() => g.Root.AddValues([(g.Edge, [b, c, g.Unit]), (g.Mark, [a, g.Unit]), (g.Edge, [a])]));
            Assert.False(g.Root.IsClean);
            g.Root.FlushUpdates();

            Assert.Equal([$"{g.Root.Find(a).Rep} {g.Root.Find(b).Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal(g.Root.Find(b), g.Root.Find(c));
        }

        /// <summary>
        /// The same in a child, where the refusal is that of a function that is not a lattice: the child's rows staged
        /// before the refused call land, the call's own rows do not, and the child stays usable.
        /// </summary>
        [Fact]
        public void AChildsRefusedAddValuesKeepsTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var last = ValueFunction(g, MergeFn.New, "last");
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Mark, a);
            Assert.Throws<InvalidOperationException>(() => child.AddValues([(g.Mark, [b, g.Unit]), (last, [a, g.Int(2)])]));
            Assert.False(child.IsFaulted);
            child.FlushUpdates();

            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
            Assert.Null(child.LookupId(last, [a]));
            child.CheckInvariants();
        }

        /// <summary>
        /// <see cref="HostWrites.RemoveRows"/> compacts one function's rows out of an arena of rows of several widths:
        /// the other functions' rows and the staged unions survive the clear, in order and intact.
        /// </summary>
        [Fact]
        public void AClearKeepsTheOtherFunctionsRowsAndTheUnions()
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, 8).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Assert(g.Root, g.Mark, ids[2]);
            g.Root.StageRow(g.Low, [ids[3], g.Int(7)]);
            g.Assert(g.Root, g.Edge, ids[4], ids[5]);
            g.Root.StageUnion(ids[6], ids[7]);
            g.Assert(g.Root, g.Mark, ids[5]);
            g.Root.StageRow(g.Low, [ids[3], g.Int(4)]);

            g.Root.ClearTable(g.Edge);
            g.Assert(g.Root, g.Edge, ids[1], ids[2]);
            g.Root.FlushUpdates();

            Assert.Equal([$"{ids[1].Rep} {ids[2].Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Equal(new[] { $"{ids[2].Rep}", $"{ids[5].Rep}" }.OrderBy(s => s, StringComparer.Ordinal), ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal(g.Int(4), g.Root.LookupId(g.Low, [ids[3]]));
            Assert.Equal(g.Root.Find(ids[6]), g.Root.Find(ids[7]));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// A context that staged a batch larger than the retention limits releases its arenas at the flush
        /// (<see cref="HostWrites.Clear"/>) and stages the next batches, rows and unions, from empty storage.
        /// </summary>
        [Fact]
        public void StagingWorksAgainAfterALargeBatchIsReleased()
        {
            var g = new ContextGraph();
            var count = 3 * HostWrites.RetainedRows;
            var ids = Enumerable.Range(0, count + 4).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i < count; i++)
                g.Assert(g.Root, g.Mark, ids[i]);

            for (var i = 0; i + 1 < count; i += 2)
                g.Root.StageUnion(ids[i], ids[i + 1]);

            g.Root.FlushUpdates();
            Assert.Equal(count / 2, ContextGraph.Keys(g.Root, g.Mark).Length);

            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            for (var round = 0; round < 3; round++)
            {
                var x = child.FreshId();
                g.Assert(child, g.Edge, ids[count], x);
                child.StageUnion(ids[count + 1], x);
                child.FlushUpdates();
                Assert.Equal(child.Find(ids[count + 1]), child.Find(x));
            }

            for (var i = 0; i < count; i++)
                g.Assert(child, g.Edge, ids[i], ids[count + 2]);

            child.FlushUpdates();
            g.Assert(child, g.Edge, ids[count + 3], ids[count + 3]);
            child.FlushUpdates();
            // The three round edges collapse into one (their targets were unioned), the bulk edges into one per class.
            Assert.Equal(count / 2 + 2, ContextGraph.Keys(child, g.Edge).Length);
            child.CheckInvariants();
        }

    }

}
