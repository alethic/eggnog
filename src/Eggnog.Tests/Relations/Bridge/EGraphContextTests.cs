using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="EGraphContext"/>: isolation, panics per context, the change stamp, locals, local ids, canonicalizing
    /// host writes, the lifecycle members, the parallelism, the report level, reads and saturation.
    /// </summary>
    public class EGraphContextTests
    {

        [Fact]
        public void TheRootIsTheEGraphsRootContext()
        {
            var g = new ContextGraph();
            var root = g.Root;
            Assert.Same(root, g.EGraph.Root);
            Assert.Same(g.EGraph, root.Owner);
            Assert.True(root.IsRoot);
            Assert.Null(root.Parent);
            Assert.Equal(0, root.Depth);
            Assert.Same(root, root.Context.Host);
            Assert.Same(g.EGraph.Store.Root, root.Context);
            Assert.Equal(0u, root.CreatedAt);
            Assert.False(root.IsFaulted);
            Assert.True(root.IsClean);
            Assert.Throws<InvalidOperationException>(() => root.Dispose());
        }

        [Fact]
        public void AChildIsIsolatedFromItsParentAndItsSiblings()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [a, b, g.Unit])]);
            g.Root.Freeze();

            using var left = g.Root.CreateChild();
            using var right = g.Root.CreateChild();
            Assert.Same(g.Root, left.Parent);
            Assert.Equal(1, left.Depth);
            g.Assert(left, g.Edge, b, a);
            left.StageUnion(a, b);
            Assert.False(left.IsClean);
            left.FlushUpdates();
            left.RunRules(rule);

            Assert.Equal([$"{a.Rep} {a.Rep}"], ContextGraph.Keys(left, g.Path));
            Assert.Equal(a, left.Find(b));
            Assert.Empty(ContextGraph.Keys(right, g.Path));
            Assert.Equal(b, right.Find(b));
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(right, g.Edge));
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Equal(b, g.Root.Find(b));
            Assert.Equal(1, left.TableSize(g.Edge));
            Assert.Equal(1, right.TableSize(g.Edge));
            Assert.True(left.OwnRowCount > 0);
            Assert.Equal(0, right.OwnRowCount);
            left.CheckInvariants();
            right.CheckInvariants();
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// A panic in a child's run is the child's: the root's next run reports nothing, and a sibling is not affected.
        /// The panic function reports through the calling state, not a channel of the e-graph.
        /// </summary>
        [Fact]
        public void PanicsArePerContext()
        {
            var g = new ContextGraph();
            var rb = g.EGraph.NewRule("panics", true);
            rb.QueryTable(g.Mark, [rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Panic("boom");
            var rule = rb.Build();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            using var sibling = g.Root.CreateChild();
            g.Assert(child, g.Mark, child.FreshId());
            child.FlushUpdates();
            var error = Assert.Throws<PanicException>(() => child.RunRules(rule));
            Assert.Equal("boom", error.PanicMessage);
            Assert.False(child.IsFaulted);
            Assert.False(g.EGraph.PanicChannel.HasValue);
            Assert.Null(g.Root.Context.Database.Failure);
            Assert.Null(child.Context.Database.Failure);

            // A run that records a failure commits no progress, so the match is found again.
            Assert.Throws<PanicException>(() => child.RunRules(rule));
            Assert.Equal(0, sibling.RunRules(rule).RuleSetReport.NumMatches("panics"));
        }

        /// <summary>
        /// The change stamp moves only for effective changes: re-asserting a visible row, a redundant union and a run
        /// that derives nothing new leave it.
        /// </summary>
        [Fact]
        public void VersionIgnoresNoOps()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [a, b, g.Unit])]);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var v0 = child.Version;
            g.Assert(child, g.Edge, a, b);
            child.FlushUpdates();
            Assert.Equal(v0, child.Version);

            child.RunRules(rule);
            var v1 = child.Version;
            Assert.True(v1 > v0);
            child.RunRules(rule);
            Assert.Equal(v1, child.Version);

            child.StageUnion(a, b);
            child.FlushUpdates();
            var v2 = child.Version;
            Assert.True(v2 > v1);
            child.StageUnion(b, a);
            child.FlushUpdates();
            Assert.Equal(v2, child.Version);
        }

        [Fact]
        public void LocalsAreBuiltOncePerContext()
        {
            var g = new ContextGraph();
            var built = 0;
            var slot = g.EGraph.NewLocalSlot(context =>
            {
                Interlocked.Increment(ref built);
                return new LocalDepth(context.Depth);
            });
            var other = g.EGraph.NewLocalSlot(_ => "other");
            Assert.NotEqual(slot.Index, other.Index);

            var rootValue = g.Root.GetLocal(slot);
            Assert.Same(rootValue, g.Root.GetLocal(slot));
            Assert.Equal(0, rootValue.Depth);
            Assert.Equal("other", g.Root.GetLocal(other));
            g.Root.Freeze();

            var child = g.Root.CreateChild();
            var childValue = child.GetLocal(slot);
            Assert.NotSame(rootValue, childValue);
            Assert.Equal(1, childValue.Depth);
            Assert.Equal(2, built);

            // Any number of threads asking a frozen context agree on one value.
            var values = new LocalDepth[16];
            Parallel.For(0, values.Length, i => values[i] = g.Root.GetLocal(slot));
            Assert.All(values, v => Assert.Same(rootValue, v));

            child.Dispose();
            Assert.Throws<ObjectDisposedException>(() => child.GetLocal(slot));
            Assert.Throws<ArgumentException>(() => g.Root.GetLocal(new ContextGraph().EGraph.NewLocalSlot(_ => "foreign")));
            Assert.Throws<InvalidOperationException>(() => g.Root.GetLocal(g.EGraph.NewLocalSlot<string>(_ => null!)));
        }

        /// <summary>
        /// Every term minted in a child is local to it, since every term mint draws on the child's own id blocks: fresh
        /// ids, staged terms, and the ids its rules mint; container ids are minted on the shared counter and are never
        /// local.
        /// </summary>
        [Fact]
        public void IsLocalIdCoversTermMintsAndNotContainerMints()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>(holdsIds: false);
            var rb = g.EGraph.NewRule("mint", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            var minted = rb.Lookup(g.Add, [x, x], () => "");
            rb.Set(g.Path, [minted, x, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var fresh = child.FreshId();
            var term = child.StageTerm(g.Num, [g.Int(7)]);
            child.FlushUpdates();
            g.Assert(child, g.Mark, fresh);
            child.FlushUpdates();
            child.RunRules(rule);
            var add = child.LookupId(g.Add, [fresh, fresh]);
            var container = child.GetContainerValue(new VecContainer(false, 1, 2));

            Assert.True(child.Context.IsLocalId(fresh.Rep));
            Assert.True(child.Context.IsLocalId(term.Rep));
            Assert.NotNull(add);
            Assert.True(child.Context.IsLocalId(add!.Value.Rep));
            Assert.False(child.Context.IsLocalId(container.Rep));
            Assert.Equal(container, g.EGraph.GetContainerValue(new VecContainer(false, 1, 2)));
        }

        /// <summary>
        /// The context API canonicalizes the id cells of host writes at every level, the root included, so that every
        /// id cell it stages is canonical in its context or freshly minted; the egglog-compatible root API writes them
        /// as given.
        /// </summary>
        [Fact]
        public void TheContextApiCanonicalizesHostIdsAtTheRoot()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            Union(g.EGraph, a, b);
            Assert.Equal(a, g.Root.Find(b));

            g.Assert(g.Root, g.Mark, b);
            g.Root.FlushUpdates();
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(g.Root, g.Mark));

            // The egglog API stores the stale id, as egglog's add_values does; a rebuild would rewrite it later.
            g.EGraph.AddValues([(g.Path, [b, c, g.Unit])]);
            Assert.Equal([$"{b.Rep} {c.Rep}"], ContextGraph.Keys(g.Root, g.Path));

            // Reads canonicalize keys: the stale key finds the canonical row through the context API.
            Assert.True(g.Root.ContainsKey(g.Mark, [b]));
            Assert.Null(g.EGraph.LookupId(g.Mark, [b]));
        }

        /// <summary>
        /// The lifecycle members follow the storage contexts': <c>CreateChild</c> with options, <c>Dispose</c> (refused
        /// while children live, bottom-up and idempotent), <c>IsFaulted</c>, <c>Parent</c> and <c>OwnRowCount</c>.
        /// </summary>
        [Fact]
        public void TheLifecycleFollowsStorage()
        {
            var g = new ContextGraph();
            var child = g.Root.CreateChild(ContextOptions.Case);
            Assert.True(child.Options.IsCase);
            Assert.Same(g.Root, child.Parent);
            Assert.Same(child, child.Context.Host);
            var grandchild = child.CreateChild();
            Assert.Equal(2, grandchild.Depth);
            Assert.Same(child, grandchild.Parent);

            // A context with live children cannot be disposed; disposal is bottom-up and idempotent.
            Assert.Throws<InvalidOperationException>(() => child.Dispose());
            grandchild.Dispose();
            grandchild.Dispose();
            Assert.True(grandchild.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => grandchild.RunRules());
            Assert.Throws<ObjectDisposedException>(() => grandchild.CreateChild());
            child.Dispose();
            Assert.Equal(0, g.Root.Context.ChildCount);
            Assert.Null(child.Context.Host);

            // OwnRowCount is the context's own rows.
            var counted = g.Root.CreateChild();
            Assert.Equal(0, counted.OwnRowCount);
            g.Assert(counted, g.Mark, counted.FreshId());
            g.Assert(counted, g.Mark, counted.FreshId());
            counted.FlushUpdates();
            Assert.Equal(2, counted.OwnRowCount);
            Assert.Equal(0, g.Root.OwnRowCount);
            counted.Dispose();
        }

        /// <summary>
        /// The store-backed e-graph's constructor takes the parallelism; a child of a writable context inherits it, a
        /// child of a frozen one is serial, and options override both.
        /// </summary>
        [Fact]
        public void ParallelismReachesTheStoreAndItsChildren()
        {
            var parallel = new ParallelHeuristics(4);
            var g = new ContextGraph(parallel);
            Assert.Same(parallel, g.EGraph.Database.Parallelism);
            Assert.Same(parallel, g.EGraph.Store.Root.Database.Parallelism);

            using (var writable = g.Root.CreateChild())
                Assert.Same(parallel, writable.Context.Database.Parallelism);

            var own = new ParallelHeuristics(2);
            using (var overridden = g.Root.CreateChild(new ContextOptions { Parallelism = own }))
                Assert.Same(own, overridden.Context.Database.Parallelism);

            g.Root.Freeze();
            using var serial = g.Root.CreateChild();
            Assert.Same(ParallelHeuristics.Serial, serial.Context.Database.Parallelism);
        }

        /// <summary>
        /// A child's run reports at the e-graph's report level.
        /// </summary>
        [Fact]
        public void RunsReportAtTheGraphsLevel()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.AddValues([(g.Edge, [g.Root.FreshId(), g.Root.FreshId(), g.Unit])]);
            g.Root.Freeze();
            using var child = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            g.EGraph.ReportLevel = ReportLevel.WithPlan;
            var report = child.RunRules(rule);
            var reports = report.RuleSetReport.RuleReports["edge-path"];
            Assert.All(reports, r => Assert.False(string.IsNullOrEmpty(r.Plan)));
        }

        [Fact]
        public void SaturateRunsToAFixpointOrTheLimit()
        {
            var g = new ContextGraph();
            var start = g.EdgeToPath();
            var step = g.Transitive();
            var ids = Enumerable.Range(0, 6).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i + 1 < ids.Length; i++)
                g.Assert(g.Root, g.Edge, ids[i], ids[i + 1]);

            g.Root.FlushUpdates();
            var limited = g.Root.Saturate([start, step], 2);
            Assert.Equal(new SaturateReport(2, false, true), limited);
            Assert.False(limited.Saturated);

            var report = g.Root.Saturate([start, step], 100);
            Assert.True(report.Saturated);
            Assert.True(report.Iterations < 10);
            Assert.Equal(15, g.Root.TableSize(g.Path));
            Assert.Equal(new SaturateReport(0, false, true), g.Root.Saturate([start, step], 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => g.Root.Saturate([start], -1));
        }

        /// <summary>
        /// Reads require a clean context: staged host writes, or an ancestor's, make it unclean until
        /// <see cref="EGraphContext.EnsureClean"/>; a frozen context is read from any thread.
        /// </summary>
        [Fact]
        public void ReadsRequireACleanContext()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            using var child = g.Root.CreateChild();
            g.Assert(g.Root, g.Mark, a);
            Assert.False(g.Root.IsClean);
            Assert.False(child.IsClean);
            Assert.Throws<InvalidOperationException>(() => child.Find(a));
            Assert.Throws<InvalidOperationException>(() => g.Root.TableSize(g.Mark));

            child.EnsureClean();
            Assert.True(g.Root.IsClean);
            Assert.True(child.IsClean);
            Assert.True(child.ContainsKey(g.Mark, [a]));
            Assert.Equal(g.Unit, child.LookupId(g.Mark, [a]));
            Assert.True(child.TryLookup(g.Mark, [a], out var unit));
            Assert.Equal(g.Unit, unit);
            Assert.False(child.ContainsKey(g.Mark, [child.FreshId()]));
            Assert.Throws<ArgumentException>(() => child.TryLookup(g.Mark, [], out _));

            child.Dispose();
            g.Root.Freeze();
            var counts = new int[8];
            Parallel.For(0, counts.Length, i => counts[i] = g.Root.TableSize(g.Mark));
            Assert.All(counts, n => Assert.Equal(1, n));
        }

        /// <summary>
        /// A stopped context's writes and runs are no-ops, and its reads but
        /// <see cref="EGraphContext.IsContradictory"/> are refused: a contradicted case has no model.
        /// </summary>
        [Fact]
        public void AStoppedContextIgnoresWritesAndRuns()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            g.Root.Freeze();
            using var @case = g.Root.CreateChild(ContextOptions.Case);
            var a = @case.FreshId();
            @case.StageRow(g.Bottom, [g.Unit]);
            Assert.True(@case.FlushUpdates());
            Assert.True(@case.IsStopped);
            Assert.True(@case.IsContradictory);
            g.Assert(@case, g.Mark, a);
            Assert.False(@case.FlushUpdates());
            Assert.True(@case.RunRules(g.EdgeToPath()).Stopped);
            Assert.Throws<InvalidOperationException>(() => @case.TableSize(g.Mark));
            Assert.Throws<InvalidOperationException>(() => @case.CreateChild());
            Assert.Throws<InvalidOperationException>(() => @case.FreshId());
            @case.CheckInvariants();
        }

    }

}
