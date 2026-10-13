using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="EGraphContext.CheckInvariants"/> (colored-contexts spec §12.7): it passes on consistent contexts at every
    /// level, and catches deliberate corruptions of the bridge's parts C1, C2, C5 and C7 with
    /// <see cref="ContextInvariantException"/>.
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
        /// C1: a watermark after the clock or above the floor, or with the wrong number of constants.
        /// </summary>
        [Fact]
        public void C1CatchesCorruptProgress()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            var info = g.EGraph.GetRuleInfo(rule);
            g.Root.SetProgress(rule, new RuleProgress(g.EGraph.NextTimestamp + 100, [1, 2], info.Generation));
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C1:") && v.Contains("after the clock"));
            Assert.Contains(error.Violations, v => v.StartsWith("C1:") && v.Contains("constant"));
        }

        /// <summary>
        /// C2: a frozen context holding host writes (corrupt state: a frozen context cannot stage any) is reported as a C2
        /// violation, not refused as an unclean context.
        /// </summary>
        [Fact]
        public void C2CatchesAFrozenContextWithHostWrites()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();
            var hostWrites = (HostWrites)typeof(EGraphContext).GetField("hostWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g.Root)!;
            hostWrites.AddUnion(a.Rep, b.Rep);
            Assert.False(g.Root.IsClean);

            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C2:") && v.Contains("host writes"));
        }

        /// <summary>
        /// C2: a frozen context holding a failure nobody took.
        /// </summary>
        [Fact]
        public void C2CatchesAFrozenContextWithAFailure()
        {
            var g = new ContextGraph();
            g.Root.Freeze();
            var database = g.Root.Context.Database;
            var state = database.CreateExecutionState(0);
            state.ReportFailure("late");
            database.RecordFailure(state);
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C2:"));
        }

        /// <summary>
        /// C5: a stopped context that sees no contradiction row.
        /// </summary>
        [Fact]
        public void C5CatchesAStopWithoutAContradiction()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            child.Context.MarkContradicted();
            var error = Assert.Throws<ContextInvariantException>(() => child.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C5:"));
        }

        /// <summary>
        /// C7: a storage child created under a bridge context without a bridge context of its own.
        /// </summary>
        [Fact]
        public void C7CatchesAChildOutsideTheBridge()
        {
            var g = new ContextGraph();
            var stray = g.Root.Context.CreateChild();
            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C7:"));
            Assert.Equal(0, error.Violations.Count(v => v.StartsWith("C7:") == false));
            stray.Dispose();
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// The storage parts are reported too: the bridge composes the storage checker (here its gate item, §12.7 1.7).
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

    }

}
