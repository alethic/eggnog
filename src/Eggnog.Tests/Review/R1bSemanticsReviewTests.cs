using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Defects and coverage gaps the R1b semantics review found. The first two tests fail on the reviewed sandbox; the
    /// last two pass and pin behaviour no existing test would catch regressing.
    /// </summary>
    public sealed class R1bSemanticsReviewTests
    {

        /// <summary>
        /// Schedule.Until refuses filters at construction, but accepts conditions that no host check of this scope can
        /// read: a computation, or another graph's table. Each throws ArgumentException from inside Scope.Run only when the
        /// Until node first executes, after the schedule's earlier steps have iterated: their work is applied (Version
        /// moved) and the run's report is lost. Expected: refused before any work, as Run refuses another graph's ruleset.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnUncheckableUntilConditionIsRefusedBeforeAnyWork(bool foreignTable)
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var version = t.Root.Version;
            var condition = foreignTable ? other.Num.Of(2L) : t.Num.Of(Primitives.Add.Of(1L, 1L));

            var error = Record.Exception(() => t.Root.Run(Schedule.Sequence(t.Rules, Schedule.Until(t.Rules, condition))));

            Assert.IsType<ArgumentException>(error);
            Assert.Equal(version, t.Root.Version);
            Assert.False(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// QueryMatch.Get promises a class canonical in the queried scope, and ForEachMatch lets its callback call the
        /// scope. A union made in the callback leaves the later matches' classes non-canonical.
        /// </summary>
        [Fact]
        public void AMatchDeliveredAfterACallbackUnionIsCanonical()
        {
            var t = new NodeTheory();
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            Var<Node> n = null!;
            var lits = t.Graph.DefineQuery("lits", b =>
            {
                n = b.Var<Node>("n");
                var x = b.Var<long>("x");
                b.When(n.Is(t.Lit.Of(x)));
            });

            var delivered = new List<(Node Class, Node Canonical)>();
            t.Root.ForEachMatch(lits, m =>
            {
                var current = m.Get(n);
                delivered.Add((current, t.Root.Find(current)));
                if (delivered.Count == 1)
                    t.Root.Union(one, two);

                return true;
            });

            Assert.Equal(2, delivered.Count);
            Assert.All(delivered.Skip(1), d => Assert.Equal(d.Canonical, d.Class));
        }

        /// <summary>
        /// ForEachMatch delivers matches in content order (§6.6.4): strings ordinal, numbers numeric. The existing tests
        /// use small non-negative numbers, whose cells are already in numeric order, so a regression to raw cell order
        /// passes the whole suite; this test fails on it.
        /// </summary>
        [Fact]
        public void ForEachMatchDeliversStringsAndNumbersInContentOrder()
        {
            var g = new EGraph();
            var e = g.DefineRelation<string, long>("e");
            var rows = new (string, long)[] { ("b", 5), ("a", -1), ("B", long.MaxValue), ("a", 3_000_000_000), ("", -7), ("a", 0) };
            g.Root.Assume(rows.Select(r => e.Of(r.Item1, r.Item2)).ToArray());
            Var<string> s = null!;
            Var<long> v = null!;
            var q = g.DefineQuery("q", b =>
            {
                (s, v) = b.Vars<string, long>("s", "v");
                b.When(e.Of(s, v));
            });

            var seen = new List<(string, long)>();
            g.Root.ForEachMatch(q, m =>
            {
                seen.Add((m.Get(s), m.Get(v)));
                return true;
            });

            var expected = rows.OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2).ToList();
            Assert.Equal(expected, seen);
        }

        /// <summary>
        /// A body let checked against a constant (§6.3: <c>p(args).Is(c)</c>, a check). The differential never generates
        /// one, so lowering such a let as a fresh binding (dropping the check) passes 5000 differential seeds; this test
        /// fails on it.
        /// </summary>
        [Fact]
        public void ALetCheckedAgainstAConstantFilters()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Var<long> x = null!;
            var q = t.Graph.DefineQuery("q", b =>
            {
                x = b.Var<long>("x");
                b.When(t.Num.Of(x), Primitives.Add.Of(x, 1L).Is(3L));
            });
            t.Rules.DefineRule("tag two", r =>
            {
                var y = r.Var<long>("y");
                r.When(t.Num.Of(y), Primitives.Add.Of(y, 1L).Is(3L));
                r.Then(t.Tag.Of(t.Lit.Of(y)));
            });

            var seen = new List<long>();
            t.Root.ForEachMatch(q, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });
            t.Root.Run(t.Rules);

            Assert.Equal(new[] { 2L }, seen);
            Assert.Equal(1, t.Root.CountRows(t.Tag));
            Assert.True(t.Root.Check(t.Tag.Of(t.Lit.Of(2L))));
        }

    }

}
