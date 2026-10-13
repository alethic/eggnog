using System;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Verification probes of the third R3 fix round (N113): a lattice merge that throws in a catch-up. These probe the
    /// structural entry points, the record the exception carries when the merge ran in an ancestor, where the merge runs
    /// in a host lift, and catch-ups the engine makes inside a split step of a run.
    /// </summary>
    public sealed class R3FixVerify3Tests
    {

        /// <summary>
        /// A graph whose lattice function <c>f</c> refuses, once armed, the merges <c>refuses</c> selects, and records
        /// whether a refused merge ran inside the engine's lifter.
        /// </summary>
        sealed class World
        {

            int armed;

            public World(Func<long, long, bool>? refuses = null)
            {
                Graph = new EGraph();
                Graph.DefineSort<Node>("Node");
                A = Graph.DefineConstructor<Node>("A");
                Tag = Graph.DefineRelation<Node>("tag");
                F = Graph.DefineFunction<string, long>("f", new ValueLattice<long>("throwing max", (x, y) =>
                {
                    if (Volatile.Read(ref armed) != 0 && (refuses?.Invoke(x, y) ?? true))
                    {
                        Interlocked.Increment(ref Refused);
                        if (Environment.StackTrace.Contains("Lifter"))
                            Interlocked.Increment(ref RefusedInLifter);

                        throw new InvalidOperationException("merge refused");
                    }

                    return Math.Max(x, y);
                }, Math.Min));
            }

            public EGraph Graph { get; }

            public Constructor<Node> A { get; }

            public Relation<Node> Tag { get; }

            public Function<string, long> F { get; }

            public int Refused;

            public int RefusedInLifter;

            public void Arm() => Volatile.Write(ref armed, 1);

        }

        /// <summary>
        /// The merge throws in the catch-up of a writable ancestor of the scope read: the exception carries the lattice's
        /// record (its name in the message, the delegate's exception inside), as when the merge runs in the scope read.
        /// </summary>
        [Fact]
        public void AnAncestorsCatchUpFailureCarriesTheLatticesRecord()
        {
            var w = new World();
            w.Graph.Root.Freeze();
            var top = w.Graph.Root.CreateChild();
            var middle = top.CreateChild();
            var bottom = middle.CreateChild();
            middle.Assume(w.F.Of("x").Is(1L));
            top.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => bottom.CountRows(w.Tag));

            Assert.Contains("throwing max", failure.Message);
            Assert.Equal("merge refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Same(failure, middle.Failure);
            Assert.Same(failure, bottom.Failure);
        }

        /// <summary>
        /// N113 lists the structural entry points: CreateChild, OpenCases, CaseSet.Add, DefineRule and Freeze each report
        /// the catch-up failure as <see cref="RuleEvaluationException"/>, fault the scope, and leave nothing behind (no
        /// child, no case, no rule).
        /// </summary>
        [Fact]
        public void StructuralCallsReportACatchUpFailureAndLeaveNothingBehind()
        {
            for (var call = 0; call < 5; call++)
            {
                var w = new World();
                var rules = w.Graph.DefineRuleset("rules");
                w.Graph.Root.Freeze();
                var parent = w.Graph.Root.CreateChild();
                var child = parent.CreateChild();
                var cases = call == 2 ? child.OpenCases() : null;
                child.Assume(w.F.Of("x").Is(1L));
                parent.Assume(w.F.Of("x").Is(2L));
                w.Arm();

                Action act = call switch
                {
                    0 => () => child.CreateChild(),
                    1 => () => child.OpenCases(),
                    2 => () => cases!.Add(),
                    3 => () => child.DefineRule(rules, "late", r => { r.When(w.Tag.Of(r.Var<Node>("n"))); r.Then(w.Tag.Of(r.Var<Node>("n"))); }),
                    _ => () => child.Freeze(),
                };

                var failure = Assert.Throws<RuleEvaluationException>(act);
                Assert.Contains("throwing max", failure.Message);
                Assert.Same(failure, child.Failure);
                Assert.False(parent.IsFaulted);
                Assert.Empty(child.Children);
                Assert.Empty(child.LocalRules);
                Assert.False(child.IsFrozen);
                if (cases is not null)
                    Assert.Empty(cases.Cases);
            }
        }

        /// <summary>
        /// §6.9.4 says a lattice merge that throws "while the engine's lift writes the parent, or catches a case up inside
        /// it, faults the parent, as before", and that the found-vacuous check is where a case's catch-up failure faults the
        /// case. Pins where the refused merge of <see cref="Scopes.CatchUpFaultTests.ALiftGoesOnWhenACasesCatchUpMergeThrows"/>
        /// actually runs: inside the engine's lift (its lifter catches every case up before reading it), and the parent is
        /// not faulted; the record is taken later by the found-vacuous check, which faults the case.
        /// </summary>
        [Fact]
        public void ACasesCatchUpMergeInsideTheEnginesLiftFaultsTheCaseNotTheParent()
        {
            var w = new World((old, incoming) => old < incoming);
            w.Graph.Root.Freeze();
            var parent = w.Graph.Root.CreateChild();
            using var cases = parent.OpenCases();
            var first = cases.Add();
            var second = cases.Add();
            first.Assume(w.F.Of("x").Is(1L));
            second.Assume(w.F.Of("x").Is(1L));
            parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            cases.Lift();

            Assert.True(w.Refused > 0);
            Assert.Equal(w.Refused, w.RefusedInLifter);
            Assert.False(parent.IsFaulted);
            Assert.True(first.IsFaulted);
            Assert.True(second.IsFaulted);
        }

        /// <summary>
        /// D18, D31 and §6.13: a lattice merge that throws during a run is a <see cref="RuleEvaluationException"/> and
        /// faults the scope; N113 extends that to every catch-up a call makes. Inside a split step, the engine's lift catches
        /// each case up with the parent it just wrote: case b holds f(x) = 3, the lift writes the cases' join f(x) = 1 into
        /// the parent, and case b's merge of it throws. When nothing takes that record, the cases are disposed when the run ends,
        /// and Run returns as if the lattice had never thrown. With a depth limit above 1, phase B runs the in-case schedule
        /// again, and its iteration takes the record (the run fails, as it should); with SplitDepthLimit = 1 nothing runs in
        /// the cases after the lift, so nothing takes it.
        /// </summary>
        [Theory]
        [InlineData(4)]
        [InlineData(1)]
        public void ACasesCatchUpMergeFailureInsideASplitStepIsReported(int depthLimit)
        {
            var w = new World((old, incoming) => old != incoming);
            var dom = w.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var weight = w.Graph.DefineFunction<string, long>("weight");
            var pick = w.Graph.DefineRelation<string, string>("pick");
            var rules = w.Graph.DefineRuleset("rules");
            rules.DefineRule("weigh the pick", r =>
            {
                var (k, v) = r.Vars<string, string>("k", "v");
                var n = r.Var<long>("n");
                r.When(pick.Of(k, v), weight.Of(v).Is(n));
                r.Then(w.F.Of("x").Is(n));
            });
            var split = w.Graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p, v));
            });
            w.Graph.Root.Freeze();
            var scope = w.Graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), weight.Of("a").Is(1L), weight.Of("b").Is(3L));
            w.Arm();

            var thrown = Record.Exception(() => scope.Run(Schedule.Split(split, Schedule.Saturate(rules)), new RunOptions { SplitDepthLimit = depthLimit }));

            Assert.True(w.Refused > 0, "the probe needs the refused merge to run");
            Assert.True(thrown is RuleEvaluationException, $"expected RuleEvaluationException after {w.Refused} refused merge(s) ({w.RefusedInLifter} inside the lifter), got {thrown?.ToString() ?? "no exception"}; scope faulted: {scope.IsFaulted}");
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// N113 says every entry that catches up reports a catch-up failure. Union, Scope.Extract and CaseSet.Lift catch up
        /// at their entry (EnterWrite, CreateExtractor, the parent's EnterWrite) and throw
        /// <see cref="RuleEvaluationException"/>, faulting the scope called, although none of their XML docs lists it
        /// (Union has no RuleEvaluationException entry, Extract none, and Lift's covers only the lifted writes and the
        /// case join).
        /// </summary>
        [Fact]
        public void UnionExtractAndLiftReportTheirEntrysCatchUpFailure()
        {
            for (var call = 0; call < 3; call++)
            {
                var w = new World();
                w.Graph.Root.Freeze();
                var parent = w.Graph.Root.CreateChild();
                var child = parent.CreateChild();
                var a = child.Make(w.A.Of());
                using var cases = child.OpenCases();
                var @case = cases.Add();
                child.Assume(w.F.Of("x").Is(1L));
                parent.Assume(w.F.Of("x").Is(2L));
                w.Arm();

                Action act = call switch
                {
                    0 => () => child.Union(a, a),
                    1 => () => child.Extract(a),
                    _ => () => cases.Lift(),
                };

                var failure = Assert.Throws<RuleEvaluationException>(act);
                Assert.Contains("throwing max", failure.Message);
                Assert.Same(failure, child.Failure);
                Assert.False(@case.IsFaulted);
            }
        }

        /// <summary>
        /// D45 says IsContradictory always answers; N113 lists it among the reads whose catch-up failure throws. Pins the
        /// latter: it throws <see cref="RuleEvaluationException"/> and faults the scope.
        /// </summary>
        [Fact]
        public void IsContradictoryReportsItsCatchUpFailure()
        {
            var w = new World();
            w.Graph.Root.Freeze();
            var parent = w.Graph.Root.CreateChild();
            var child = parent.CreateChild();
            child.Assume(w.F.Of("x").Is(1L));
            parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            Assert.Throws<RuleEvaluationException>(() => child.IsContradictory);
            Assert.True(child.IsFaulted);
        }

    }

}
