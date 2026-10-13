using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Watermark inheritance (D4, colors §10.2): a child's first run of a rule its frozen parent saturated matches only
    /// through the rows new to the child, and naive rules never inherit.
    /// </summary>
    public sealed class InheritanceTests
    {

        /// <summary>
        /// A chain of <paramref name="length"/> edges saturated into paths in a frozen parent, and a child that adds one edge;
        /// returns the step rule's matches in the child's run.
        /// </summary>
        static (long Matches, Scope Child, ScopeTheory T) ChildRun(int length, bool inherit, bool naive)
        {
            var t = new ScopeTheory();
            t.Graph.InheritWatermarks = inherit;
            t.Rules.DefineRule("edge is path", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Edge.Of(x, y));
                r.Then(t.Path.Of(x, y));
            });
            var step = t.Rules.DefineRule("path step", r =>
            {
                var (x, y, z) = r.Vars<long, long, long>("x", "y", "z");
                r.When(t.Path.Of(x, y), t.Edge.Of(y, z));
                r.Then(t.Path.Of(x, z));
                if (naive)
                    r.Naive();
            });
            for (var i = 0L; i < length; i++)
                t.Root.Assume(t.Edge.Of(i, i + 1));

            t.Root.Run(Schedule.Saturate(t.Rules));
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            child.Assume(t.Edge.Of(length, length + 1L));
            var report = child.Run(Schedule.Repeat(1, t.Rules));
            return (report.Rules[step].Matches, child, t);
        }

        [Fact]
        public void AChildsFirstRunMatchesOnlyThroughNewRows()
        {
            var (seeded, child, t) = ChildRun(12, inherit: true, naive: false);
            var (full, _, _) = ChildRun(12, inherit: false, naive: false);

            // Seeded: only the paths that end where the new edge starts. Unseeded: every path joined with every edge.
            Assert.True(seeded < full, $"seeded {seeded}, full {full}");
            Assert.True(seeded > 0, $"seeded {seeded}, full {full}");
            // The paths the new edge extends were all found in that one iteration, and nothing in the parent changed.
            Assert.Equal(12, seeded);
            Assert.True(child.Check(t.Path.Of(0L, 13L), t.Path.Of(12L, 13L)));
            Assert.False(t.Root.Check(t.Path.Of(0L, 13L)));
            Assert.True(child.Run(Schedule.Saturate(t.Rules)).Saturated);
        }

        [Fact]
        public void NaiveRulesNeverInherit()
        {
            var (inherited, _, _) = ChildRun(12, inherit: true, naive: true);
            var (full, _, _) = ChildRun(12, inherit: false, naive: true);

            Assert.Equal(full, inherited);
        }

    }

}
