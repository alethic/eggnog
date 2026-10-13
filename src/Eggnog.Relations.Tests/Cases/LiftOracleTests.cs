using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Lift behaviours (colored-contexts spec §11.2) that the other <c>Cases/</c> tests leave unchecked: the memo's cover of
    /// writable ancestors, the inclusive candidate limit, duplicate cases, and the refusal of a lift from inside a callback
    /// of the parent's thread domain.
    /// </summary>
    public class LiftOracleTests
    {

        /// <summary>
        /// A graph whose contradiction function is designated, with <paramref name="count"/> root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        /// <summary>
        /// The relations of the context graph, lifted as relations.
        /// </summary>
        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation));

        /// <summary>
        /// The memo covers the parent's ancestors that are not frozen (<see cref="LiftSignature"/>, §11.2). The parent is a
        /// child of a writable root, and its cases split b = t against b = f. The first lift settles with nothing to lift.
        /// The root then gains <c>edge(t, c)</c> and <c>edge(f, c)</c>: neither the parent nor a case writes anything, but
        /// <c>edge(b, c)</c> now holds in both cases through the winners, so the next lift must not be skipped by the memo.
        /// </summary>
        [Fact]
        public void TheMemoSeesAWritableAncestorChange()
        {
            var (g, ids) = Setup(4);
            var (t, f, c, b) = (ids[0], ids[1], ids[2], ids[3]);
            using var scope = g.Root.CreateChild();
            using (var set = new CaseSet(scope))
            {
                set.AddCase().StageUnion(b, t);
                set.AddCase().StageUnion(b, f);
                var first = set.Lift(Spec(g), LiftOptions.Default);
                Assert.False(first.Changed);
                Assert.False(scope.ContainsKey(g.Edge, [b, c]));

                g.Assert(g.Root, g.Edge, t, c);
                g.Assert(g.Root, g.Edge, f, c);
                g.Root.FlushUpdates();

                var second = set.Lift(Spec(g), LiftOptions.Default);
                Assert.True(second.Changed);
                Assert.True(scope.ContainsKey(g.Edge, [b, c]));
                Assert.False(g.Root.ContainsKey(g.Edge, [b, c]));
                scope.CheckInvariants();
            }
        }

        /// <summary>
        /// The candidate limit is inclusive (§11.2: a source is skipped when its product is greater than the limit). The seed
        /// merges a and x and holds <c>edge(a, d)</c>, which stands for two parent keys; the other case merges a with a third
        /// id and holds both keys. With a limit of two the row is expanded and both facts are lifted, though no equality is.
        /// </summary>
        [Fact]
        public void ARowAtTheCandidateLimitIsExpanded()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            g.Assert(one, g.Edge, ids[0], ids[3]);
            var two = set.AddCase();
            two.StageUnion(ids[0], ids[2]);
            g.Assert(two, g.Edge, ids[0], ids[3]);
            g.Assert(two, g.Edge, ids[1], ids[3]);

            var report = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 2 });
            Assert.Equal(0, report.Skipped);
            Assert.Equal(0, report.Unions);
            Assert.Equal(2, report.Rows);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[3]]));
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[1], ids[3]]));
            Assert.False(g.Root.ContainsKey(g.Edge, [ids[2], ids[3]]));
            Assert.NotEqual(g.Root.Find(ids[0]), g.Root.Find(ids[1]));

            // One below the product, the row is skipped and its keys counted.
            var (h, hid) = Setup(4);
            using var narrow = new CaseSet(h.Root);
            var three = narrow.AddCase();
            three.StageUnion(hid[0], hid[1]);
            h.Assert(three, h.Edge, hid[0], hid[3]);
            var four = narrow.AddCase();
            four.StageUnion(hid[0], hid[2]);
            h.Assert(four, h.Edge, hid[0], hid[3]);
            h.Assert(four, h.Edge, hid[1], hid[3]);
            var skipped = narrow.Lift(Spec(h), new LiftOptions { CandidateLimit = 1 });
            Assert.Equal(2, skipped.Skipped);
            Assert.False(h.Root.ContainsKey(h.Edge, [hid[0], hid[3]]));
        }

        /// <summary>
        /// Late parent ids (L7, §4.2): the root mints p after the cases reserved their id blocks, so in each case the local
        /// term that is unioned with p is the class's canonical id. A lifted constructor row, and a lifted function with an
        /// id output, must name the common parent id p, never the seed's local id, which the parent cannot read.
        /// </summary>
        [Fact]
        public void LiftedOutputsNameTheLateParentIdNotTheSeedsLocal()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            var terms = new Value[2];
            var sums = new Value[2];
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                terms[i] = @case.AddTerm(g.Num, [g.Int(7)]);
                sums[i] = @case.AddTerm(g.Add, [ids[0], ids[1]]);
            }

            var p = g.Root.FreshId();
            var q = g.Root.FreshId();
            for (var i = 0; i < 2; i++)
            {
                var @case = set.Cases[i];
                Assert.True(@case.IsLocalId(terms[i]));
                Assert.True(terms[i].Rep < p.Rep);
                @case.StageUnion(terms[i], p);
                @case.StageUnion(sums[i], q);
                @case.EnsureClean();
                Assert.Equal(terms[i], @case.Find(p));
                Assert.Equal(sums[i], @case.Find(q));
            }

            var spec = new LiftSpec(new LiftTable(g.Num, LiftTableKind.Constructor), new LiftTable(g.Add, LiftTableKind.Function));
            var report = set.Lift(spec, LiftOptions.Default);
            Assert.Equal(2, report.Rows);
            Assert.Equal(0, report.Terms);
            Assert.Equal(g.Root.Find(p), g.Root.LookupId(g.Num, [g.Int(7)]));
            Assert.Equal(g.Root.Find(q), g.Root.LookupId(g.Add, [ids[0], ids[1]]));
            Assert.Equal(1, g.Root.TableSize(g.Num));
            Assert.Equal(1, g.Root.TableSize(g.Add));
            g.Root.CheckInvariants();
            foreach (var @case in set.Cases)
                @case.CheckInvariants();
        }

        /// <summary>
        /// A lift into a child parent leaves out a table whose merge is not a lattice merge (decision 2: only the root may
        /// write it). The frozen root holds <c>val(t) = 5</c> and <c>val(f) = 5</c>, so <c>val(b) = 5</c> holds in both
        /// cases of the split b = t against b = f; the lift writes what it may (<c>mark(b)</c>, which both cases assert) and
        /// neither throws nor writes <c>val</c> into the child parent.
        /// </summary>
        [Fact]
        public void ALiftIntoAChildLeavesOutTablesOnlyTheRootMayWrite()
        {
            var (g, ids) = Setup(4);
            var (t, f, _, b) = (ids[0], ids[1], ids[2], ids[3]);
            var val = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Old,
                Name = "val",
            });
            g.Root.StageRow(val, [t, g.Int(5)]);
            g.Root.StageRow(val, [f, g.Int(5)]);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var scope = g.Root.CreateChild();
            using (var set = new CaseSet(scope))
            {
                var one = set.AddCase();
                one.StageUnion(b, t);
                g.Assert(one, g.Mark, b);
                var two = set.AddCase();
                two.StageUnion(b, f);
                g.Assert(two, g.Mark, b);
                foreach (var @case in set.Cases)
                {
                    @case.EnsureClean();
                    Assert.Equal(g.Int(5), @case.LookupId(val, [b]));
                }

                var spec = new LiftSpec(new LiftTable(val, LiftTableKind.Function), new LiftTable(g.Mark, LiftTableKind.Relation));
                var report = set.Lift(spec, LiftOptions.Default);
                Assert.Equal(1, report.Rows);
                Assert.Equal(0, report.Values);
                Assert.True(scope.ContainsKey(g.Mark, [b]));
                Assert.Null(scope.LookupId(val, [b]));
                Assert.False(scope.IsFaulted);
                scope.CheckInvariants();
            }
        }

        /// <summary>
        /// A case listed twice is refused (§11.2's preconditions).
        /// </summary>
        [Fact]
        public void ACaseListedTwiceIsRefused()
        {
            var (g, ids) = Setup(2);
            using var @case = g.Root.CreateChild(ContextOptions.Case);
            @case.StageUnion(ids[0], ids[1]);
            LiftSignature? memo = null;
            Assert.Throws<ArgumentException>(() => Lifter.Lift(g.Root, [@case, @case], Spec(g), LiftOptions.Default, ref memo));
            Assert.Null(memo);
            Assert.NotEqual(g.Root.Find(ids[0]), g.Root.Find(ids[1]));
        }

        /// <summary>
        /// A lift started inside a callback of the parent's thread domain (here a <c>ScanMerges</c> visitor of a case) is
        /// refused before it reads or writes anything, even when it would write nothing; outside the visitor the same lift
        /// runs.
        /// </summary>
        [Fact]
        public void ALiftInsideAMergeVisitorIsRefused()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            set.AddCase();
            foreach (var @case in set.Cases)
                @case.EnsureClean();

            Exception? thrown = null;
            var visits = 0;
            var visitor = new MergeCollector((_, _) =>
            {
                if (visits++ == 0)
                    thrown = Record.Exception(() => set.Lift(Spec(g), LiftOptions.Default));
            });
            one.ScanMerges(ref visitor);
            Assert.True(visits > 0);
            Assert.IsType<InvalidOperationException>(thrown);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.False(report.Changed);
            Assert.Equal(0, report.Rounds);
            Assert.Equal(2, report.LiveCases);
        }

    }

}
