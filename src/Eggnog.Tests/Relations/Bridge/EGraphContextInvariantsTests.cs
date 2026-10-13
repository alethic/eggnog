using System;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="EGraphContext.CheckInvariants"/>: it passes on consistent contexts at every level, and catches
    /// deliberate corruptions of the bridge's parts with <see cref="ContextInvariantException"/>, each reported under
    /// the name of the part it breaks: rule progress, frozen state, the stop flag and the tree (the link between bridge
    /// and storage contexts).
    /// </summary>
    public class EGraphContextInvariantsTests
    {

        [Fact]
        public void ConsistentContextsPass()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.RunRules(rule);
            g.Root.CheckInvariants();
            using var writable = g.Root.CreateChild();
            writable.RunRules(rule);
            writable.CheckInvariants();
            writable.Dispose();
            g.Root.Freeze();
            g.Root.CheckInvariants();
            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, b, a);
            child.RunRules(rule);
            child.CheckInvariants();
            child.Freeze();
            child.CheckInvariants();
        }

        [Fact]
        public void AnUncleanContextIsRefused()
        {
            var g = new ContextGraph();
            g.Assert(g.Root, g.Mark, g.Root.FreshId());
            Assert.Throws<InvalidOperationException>(() => g.Root.CheckInvariants());
        }

        /// <summary>
        /// Rule progress: a watermark after the clock or above the floor, or with the wrong number of constants.
        /// </summary>
        [Fact]
        public void CorruptRuleProgressIsReported()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            var info = g.EGraph.GetRuleInfo(rule);
            g.Root.SetProgress(rule, new RuleProgress(g.EGraph.NextTimestamp + 100, [1, 2], info.Generation));
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("rule progress:") && v.Contains("after the clock"));
            Assert.Contains(error.Violations, v => v.StartsWith("rule progress:") && v.Contains("constant"));
        }

        /// <summary>
        /// Frozen state: a frozen context holding host writes (corrupt state: a frozen context cannot stage any) is
        /// reported as a frozen-state violation, not refused as an unclean context.
        /// </summary>
        [Fact]
        public void AFrozenContextWithHostWritesIsReported()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();
            var hostWrites = (HostWrites)typeof(EGraphContext).GetField("hostWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g.Root)!;
            hostWrites.AddUnion(a.Rep, b.Rep);
            Assert.False(g.Root.IsClean);

            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("frozen state:") && v.Contains("host writes"));
        }

        /// <summary>
        /// Frozen state: a frozen context holding a failure nobody took.
        /// </summary>
        [Fact]
        public void AFrozenContextWithAFailureIsReported()
        {
            var g = new ContextGraph();
            g.Root.Freeze();
            var database = g.Root.Context.Database;
            var state = database.CreateExecutionState(0);
            state.ReportFailure("late");
            database.RecordFailure(state);
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("frozen state:"));
        }

        /// <summary>
        /// The stop flag: a stopped context that sees no contradiction row.
        /// </summary>
        [Fact]
        public void AStopWithoutAContradictionIsReported()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            child.Context.MarkContradicted();
            var error = Assert.Throws<ContextInvariantException>(() => child.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("stop flag:"));
        }

        /// <summary>
        /// The tree: a storage child created under a bridge context without a bridge context of its own.
        /// </summary>
        [Fact]
        public void AChildOutsideTheBridgeIsReported()
        {
            var g = new ContextGraph();
            var stray = g.Root.Context.CreateChild();
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("tree:"));
            Assert.Equal(0, error.Violations.Count(v => v.StartsWith("tree:") == false));
            stray.Dispose();
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// The storage parts are reported too: the bridge composes the storage checker (here its descendant-gate item).
        /// </summary>
        [Fact]
        public void StorageViolationsAreReported()
        {
            var g = new ContextGraph();
            using var child = g.Root.CreateChild();
            g.Root.Context.Gate.Enter();
            try
            {
                var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
                Assert.NotEmpty(error.Violations);
                Assert.Equal(0, error.Violations.Count(v => v.StartsWith("C")));
            }
            finally
            {
                g.Root.Context.Gate.Exit();
            }

            g.Root.CheckInvariants();
        }

        /// <summary>
        /// Rule progress: a committed run above the context's semi-naive floor (but not after the clock) is reported
        /// twice: as the recorded progress and as the watermark a run would use.
        /// </summary>
        [Fact]
        public void RuleProgressAboveTheFloorIsReported()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            var info = g.EGraph.GetRuleInfo(rule);
            var floor = g.Root.Context.SemiNaiveFloor;
            Assert.True(floor + 1 <= g.EGraph.NextTimestamp);
            g.Root.SetProgress(rule, new RuleProgress(floor + 1, null, info.Generation));

            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("rule progress:") && v.Contains("above the context's semi-naive floor"));
            Assert.Contains(error.Violations, v => v.StartsWith("rule progress:") && v.Contains("would run from"));
            Assert.DoesNotContain(error.Violations, v => v.Contains("after the clock"));
        }

        /// <summary>
        /// Rule progress: a child's own progress older than its creation is reported.
        /// </summary>
        [Fact]
        public void AChildsRuleProgressBeforeItsCreationIsReported()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            child.RunRules(rule);
            child.CheckInvariants();
            var info = g.EGraph.GetRuleInfo(rule);
            Assert.True(child.CreatedAt > 0);
            child.SetProgress(rule, new RuleProgress(child.CreatedAt - 1, null, info.Generation));

            var error = Assert.Throws<ContextInvariantException>(() => child.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("rule progress:") && v.Contains("before the context was created"));
        }

    }

}
