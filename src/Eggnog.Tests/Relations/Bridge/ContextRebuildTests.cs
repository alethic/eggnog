using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The rebuilds a context's runs and flushes trigger: a run's first rebuild pass reuses the run's timestamp, as
    /// egglog's rebuild after a run does, a flush's rebuild runs at the next epoch, and a child never rebuilds
    /// containers.
    /// </summary>
    public class ContextRebuildTests
    {

        /// <summary>
        /// The physical row a context sees for a key, timestamp included.
        /// </summary>
        static uint[] Row(EGraphContext context, FunctionId function, params Value[] key)
        {
            var info = context.Owner.GetFunctionInfo(function);
            var keys = new uint[key.Length];
            for (var i = 0; i < key.Length; i++)
                keys[i] = key[i].Rep;

            var row = new uint[info.Math.TableColumns];
            Assert.True(context.Context.TryGetRow(info.Table, keys, row), $"no row of {info.Name} for [{string.Join(", ", keys)}]");
            return row;
        }

        static uint Stamp(EGraphContext context, FunctionId function, params Value[] key) => Row(context, function, key)[context.Owner.GetFunctionInfo(function).Math.TimestampColumn];

        [Fact]
        public void TheRunsFirstPassReusesItsTimestamp()
        {
            var g = new ContextGraph();
            var rule = g.UnionEdges();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, a, b);
            child.FlushUpdates();

            var t = g.EGraph.NextTimestamp;
            var report = child.RunRules(rule);
            Assert.True(report.Changed);
            Assert.True(report.RebuildChanged);
            Assert.Equal(2, report.RebuildPasses);

            // The rewrite of the child's own row is stamped with the run's T, as egglog stamps it, and each pass then
            // advanced the clock.
            Assert.Equal(t, Stamp(child, g.Edge, a, a));
            Assert.Equal(t + 2, g.EGraph.NextTimestamp);
            child.CheckInvariants();
        }

        [Fact]
        public void AFlushRebuildsAtTheNextEpoch()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, a, b);
            child.FlushUpdates();
            var t = g.EGraph.NextTimestamp;
            Assert.Equal(t - 1, Stamp(child, g.Edge, a, b));

            // The union merges at t, the clock advances, and the rebuild's first pass rewrites edge(a, b) at t + 1.
            child.StageUnion(a, b);
            Assert.True(child.FlushUpdates());
            Assert.Equal(t + 1, Stamp(child, g.Edge, a, a));
            Assert.Equal(t + 3, g.EGraph.NextTimestamp);
            child.CheckInvariants();

            // The same at the root through the egglog API: merge at s, rebuild at s + 1.
            var g2 = new ContextGraph();
            var c = g2.EGraph.FreshId();
            var d = g2.EGraph.FreshId();
            g2.EGraph.AddValues([(g2.Edge, [c, d, g2.Unit])]);
            var s = g2.EGraph.NextTimestamp;
            BridgeTestUtil.Union(g2.EGraph, c, d);
            Assert.Equal(s + 1, Stamp(g2.Root, g2.Edge, c, c));
            Assert.Equal(s + 3, g2.EGraph.NextTimestamp);
        }

        /// <summary>
        /// A child never rebuilds containers: its rebuild reconciles only rows, and its database refuses the container
        /// rebuild; the root rebuilds them.
        /// </summary>
        [Fact]
        public void ContainersAreNeverRebuiltInAChild()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<VecContainer>();
            var w = g.Root.FreshId();
            var container = g.EGraph.GetContainerValue(new VecContainer(w.Rep));
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.EGraph.AddValues([(g.Mark, [container, g.Unit])]);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, a, b);
            VecContainer.TryRebuildCalls = 0;
            child.StageUnion(a, b);
            child.FlushUpdates();
            Assert.Equal(0, VecContainer.TryRebuildCalls);
            Assert.Equal(a, child.Find(b));
            Assert.Throws<InvalidOperationException>(() => child.Context.Database.RebuildContainers(g.EGraph.UnionFindTable));
            child.CheckInvariants();

            // The root does rebuild its containers when an element loses a union.
            var g2 = new ContextGraph();
            g2.EGraph.RegisterContainerType<VecContainer>();
            var x = g2.Root.FreshId();
            var y = g2.Root.FreshId();
            g2.EGraph.GetContainerValue(new VecContainer(y.Rep));
            VecContainer.TryRebuildCalls = 0;
            BridgeTestUtil.Union(g2.EGraph, x, y);
            Assert.True(VecContainer.TryRebuildCalls > 0);
        }

        /// <summary>
        /// <see cref="EGraphContext.EnsureClean"/> brings a writable parent up to date first, flushes, and rebuilds; at
        /// the root it rebuilds only when the union-find grew since the last rebuild, so writes staged at the root stay
        /// pending into the next run and merge there, as in egglog.
        /// </summary>
        [Fact]
        public void EnsureCleanFlushesUpTheChainAndKeepsRootStagingPending()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            using var child = g.Root.CreateChild();
            g.Assert(g.Root, g.Edge, a, b);
            g.Assert(child, g.Mark, a);
            child.EnsureClean();
            Assert.True(child.IsClean);
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(child, g.Edge));
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));

            // Raw root staging stays pending through EnsureClean, and merges in the next run.
            var edge = new TableAction(g.EGraph, g.Edge);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [b.Rep, a.Rep, g.Unit.Rep]);
                return 0;
            });
            var clock = g.EGraph.NextTimestamp;
            g.Root.EnsureClean();
            Assert.Equal(clock, g.EGraph.NextTimestamp);
            Assert.Null(g.Root.LookupId(g.Edge, [b, a]));
            g.Root.RunRules(g.EdgeToPath());
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [b, a]));
        }

    }

}
