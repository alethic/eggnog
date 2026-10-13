using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Host writes and the bridge's action handles: terms and rows through the context API, raw states, clears, the
    /// lattice rule in a child, <see cref="TableAction"/> reading the row the running context sees, and the flush of a
    /// parent's staged writes when it creates a child.
    /// </summary>
    /// <remarks>
    /// egglog's <c>clear_table</c> (egglog-bridge/src/lib.rs:409-424) drops "any rows staged for this table by an
    /// in-flight <c>MutationBuffer</c> ... along with the table contents", and <see cref="EGraph.ClearTable"/> removes
    /// every row of the function and every write staged for it. A context stages host rows bridge-side until its next
    /// flush, so a clear issued after <see cref="EGraphContext.StageRow"/> drops those rows too; otherwise the
    /// operations would take effect out of order, the clear first and the earlier write after it. Rows staged raw into
    /// database buffers are dropped the same way.
    /// </remarks>
    public class ContextWritesTests
    {

        /// <summary>
        /// <see cref="EGraphContext.AddTerm"/> in a child mints a local id for a new key, and returns the visible
        /// term's class for a key the parent holds (the constructor's union merge reconciles the fresh id).
        /// </summary>
        [Fact]
        public void AddTermInAChildReusesTheParentsTerms()
        {
            var g = new ContextGraph();
            var one = g.Root.AddTerm(g.Num, [g.Int(1)]);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            Assert.Equal(one, child.AddTerm(g.Num, [g.Int(1)]));
            var two = child.AddTerm(g.Num, [g.Int(2)]);
            Assert.True(child.Context.IsLocalId(two.Rep));
            Assert.Equal(2, child.TableSize(g.Num));
            Assert.Equal(1, g.Root.TableSize(g.Num));
            child.AddValues([(g.Add, [one, two, child.FreshId()])]);
            Assert.Equal(1, child.TableSize(g.Add));
            child.CheckInvariants();
        }

        /// <summary>
        /// A function whose merge is not a lattice merge can be written only at the root; the child refuses the row
        /// when it is staged, before anything changes, and stays usable.
        /// </summary>
        [Fact]
        public void AChildRefusesNonLatticeFunctionsWhenStaging()
        {
            var g = new ContextGraph();
            var last = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.New,
                Name = "last",
            });
            var a = g.Root.FreshId();
            g.Root.StageRow(last, [a, g.Int(1)]);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.StageRow(last, [a, g.Int(2)]));
            Assert.Throws<InvalidOperationException>(() => child.AddValues([(g.Mark, [a, g.Unit]), (last, [a, g.Int(2)])]));
            Assert.True(child.IsClean);
            Assert.False(child.IsFaulted);
            Assert.Equal(g.Int(1), child.LookupId(last, [a]));
            Assert.Throws<ArgumentException>(() => child.StageRow(last, [a]));
            Assert.Throws<ArgumentException>(() => child.StageTerm(g.Num, []));
        }

        /// <summary>
        /// A clear in a child needs a frozen parent and no live descendants, since a context may not retract rows a
        /// writable parent or a live descendant shares; then the parent's rows of the function disappear from the
        /// child's view only.
        /// </summary>
        [Fact]
        public void ClearTableFollowsTheRetractionRules()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            g.Assert(g.Root, g.Mark, a);
            g.Root.FlushUpdates();

            using (var underWritable = g.Root.CreateChild())
                Assert.Throws<InvalidOperationException>(() => underWritable.ClearTable(g.Mark));

            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            var grandchild = child.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.ClearTable(g.Mark));
            grandchild.Dispose();
            child.ClearTable(g.Mark);
            Assert.Equal(0, child.TableSize(g.Mark));
            Assert.Equal(1, g.Root.TableSize(g.Mark));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A raw execution state of a child stages into the child's layers; the next flush merges it, canonicalized and
        /// stamped at the merge.
        /// </summary>
        [Fact]
        public void WithExecutionStateStagesIntoTheChild()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            child.StageUnion(a, b);
            child.FlushUpdates();
            var mark = new TableAction(g.EGraph, g.Mark);
            child.WithExecutionState(state =>
            {
                mark.Insert(state, [b.Rep, g.Unit.Rep]);
                return 0;
            });
            Assert.False(child.IsClean);
            child.FlushUpdates();
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));
            child.CheckInvariants();
        }

        /// <summary>
        /// The bridge's <see cref="TableAction"/> reads the row the running context sees: a primitive that looks up
        /// <c>low</c> in a child's rule gets the child's own, lowered value, and a row count counts the child's view.
        /// </summary>
        [Fact]
        public void TableActionsReadTheRunningContext()
        {
            var g = new ContextGraph();
            var low = new TableAction(g.EGraph, g.Low);
            var marks = new TableAction(g.EGraph, g.Mark);
            var seen = new List<(long Value, int Rows)>();
            var read = g.EGraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var found = low.TryLookup(state, args, out result);
                lock (seen)
                    seen.Add((found ? state.BaseValues.Unwrap<Int64Codec, long>(new Value(result)) : -1, marks.RowCount(state)));

                return found;
            }, readsTables: true));

            var rb = g.EGraph.NewRule("read-low", false);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            rb.QueryPrim(read, [x, rb.NewVar(g.I64)], g.I64);
            var rule = rb.Build();

            var a = g.Root.FreshId();
            g.Root.AddValues([(g.Mark, [a, g.Unit]), (g.Low, [a, g.Int(5)])]);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            child.AddValues([(g.Low, [a, g.Int(2)]), (g.Mark, [child.FreshId(), g.Unit])]);
            child.RunRules(rule);
            Assert.Contains((2L, 2), seen);
            Assert.DoesNotContain(seen, s => s.Value == 5);
            Assert.False(g.EGraph.GetRuleInfo(rule).AllowInheritance);

            seen.Clear();
            using var sibling = g.Root.CreateChild();
            sibling.RunRules(rule);
            Assert.Equal([(5L, 1)], seen);
        }

        /// <summary>
        /// The egglog-compatible root API keeps working while the root has live children, which catch up with what it
        /// writes.
        /// </summary>
        [Fact]
        public void TheRootApiWorksWithLiveChildren()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            using var child = g.Root.CreateChild();
            var a = g.EGraph.FreshId();
            var b = g.EGraph.FreshId();
            g.EGraph.AddValues([(g.Edge, [a, b, g.Unit])]);
            g.EGraph.RunRules(rule);
            Assert.Equal(1, g.EGraph.TableSize(g.Path));
            Assert.Equal(g.Unit, g.EGraph.LookupId(g.Path, [a, b]));
            Union(g.EGraph, a, b);
            Assert.Equal(a, g.EGraph.GetCanonInUf(b));

            child.EnsureClean();
            Assert.Equal([$"{a.Rep} {a.Rep}"], ContextGraph.Keys(child, g.Path));
            Assert.Equal(a, child.Find(b));
            child.CheckInvariants();
            Assert.Throws<InvalidOperationException>(() => g.EGraph.ClearTable(g.Path));
        }

        /// <summary>
        /// A row staged raw at the root and not merged, then the e-graph's clear of its function, then a flush.
        /// egglog's <c>clear_table</c> drops what in-flight buffers staged for the table (lib.rs:409-424), so the
        /// function is empty.
        /// </summary>
        [Fact]
        public void TheRootsClearDropsARowStagedRawBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var edge = new TableAction(g.EGraph, g.Edge);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            g.EGraph.ClearTable(g.Edge);
            g.EGraph.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// The same in a child of a frozen root, where a clear is allowed: a row the child staged raw, then the child's
        /// clear, then a flush. The child's view of the function is empty.
        /// </summary>
        [Fact]
        public void AChildsClearDropsARowItStagedRawBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, c);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var edge = new TableAction(g.EGraph, g.Edge);
            child.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            child.ClearTable(g.Edge);
            child.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(child, g.Edge));
        }

        /// <summary>
        /// <c>CreateChild</c> of a writable context flushes the host writes staged on it first, so the child starts as
        /// a copy of everything the parent was given, and both are readable at once.
        /// </summary>
        [Fact]
        public void CreateChildFlushesTheParentsStagedWrites()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.StageUnion(a, b);
            Assert.False(g.Root.IsClean);

            using var child = g.Root.CreateChild();
            Assert.True(g.Root.IsClean);
            Assert.True(child.IsClean);
            Assert.Equal(1, child.TableSize(g.Edge));
            Assert.Equal(child.Find(a), child.Find(b));
            Assert.Equal(1, g.Root.TableSize(g.Edge));
            child.CheckInvariants();
        }

        /// <summary>
        /// At the root: a row staged through the context API, then the egglog API's clear of its function, then a
        /// flush. The clear came last, so the function must be empty, as egglog's <c>clear_table</c> and the
        /// documentation of <see cref="EGraph.ClearTable"/> say.
        /// </summary>
        [Fact]
        public void TheRootsClearDropsRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();

            g.Assert(g.Root, g.Edge, a, b);
            g.EGraph.ClearTable(g.Edge);
            g.Root.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// The same through the context API at the root: <see cref="EGraphContext.ClearTable"/> after
        /// <see cref="EGraphContext.StageRow"/> leaves the function empty once flushed.
        /// </summary>
        [Fact]
        public void TheContextApiClearDropsRowsStagedBeforeItAtTheRoot()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();

            g.Assert(g.Root, g.Edge, a, b);
            g.Root.ClearTable(g.Edge);
            g.Root.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// In a child of a frozen root, where a clear is allowed: a row the child staged, then the child's clear of the
        /// function, then a flush. The child's view of the function is empty.
        /// </summary>
        [Fact]
        public void AChildsClearDropsRowsItStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, c);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, a, b);
            child.ClearTable(g.Edge);
            child.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(child, g.Edge));
        }

    }

}
