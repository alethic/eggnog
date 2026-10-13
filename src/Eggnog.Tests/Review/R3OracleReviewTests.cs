using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review of rule-layer R3's tests by mutation: each test here kills a plausible bug that the milestone's own tests and
    /// differentials let through (named in its summary).
    /// </summary>
    public sealed class R3OracleReviewTests
    {

        /// <summary>
        /// Kills: the lift spec cache never rebuilt (<c>LiftSpecBuilder.Build</c> returning the cached spec whatever the
        /// table count). A table defined after a first lift must lift too (D6: definitions are never sealed), with its
        /// lattice's case join.
        /// </summary>
        [Fact]
        public void TablesDefinedAfterALiftAreLiftedToo()
        {
            var graph = new EGraph();
            var first = graph.DefineRelation<string>("first");
            var root = graph.Root;
            using (var cases = root.OpenCases())
            {
                cases.Add().Assume(first.Of("a"));
                cases.Add().Assume(first.Of("a"));
                Assert.Equal(1, cases.Lift().Rows);
            }

            var later = graph.DefineRelation<string>("later");
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            using (var cases = root.OpenCases())
            {
                cases.Add().Assume(later.Of("b"), dom.Of("k").Is(ValueSet.Of("x")));
                cases.Add().Assume(later.Of("b"), dom.Of("k").Is(ValueSet.Of("y")));
                var lifted = cases.Lift();
                Assert.Equal(1, lifted.Rows);
                Assert.Equal(1, lifted.Values);
            }

            Assert.True(root.Check(later.Of("b")));
            Assert.Equal(ValueSet.Of("x", "y"), root.Get(dom.Of("k")));
        }

        /// <summary>
        /// Kills: a one-element set skipped like an empty one (<c>cases &lt;= 1</c> in <c>RuleSplitSource</c>). A set of
        /// one element opens one case (§6.9.2: one case per element; only an empty set is skipped), and what that case
        /// assumes is lifted.
        /// </summary>
        [Fact]
        public void AOneElementSetOpensOneCaseWhoseAssumptionsLift()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("one").Is(ValueSet.Of("x")), t.Dom.Of("two").Is(ValueSet.Of("y", "z")));

            var report = root.Run(Schedule.Split(t.Split, t.Rules));

            Assert.Equal(3, report.CasesOpened);
            Assert.Equal(0, report.SplitsSkipped);
            Assert.True(root.Check(t.Chosen.Of("one", "x")));             // the only case of one proves it
            Assert.False(root.Check(t.Chosen.Of("two", "y")));
            Assert.False(root.Check(t.Chosen.Of("two", "z")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// Kills: a split step that does not bring current the mirrors its matcher reads (both <c>MirrorSync.Ensure</c>
        /// calls of R3 removed, D48). The matcher reads <c>present@schema</c> from below a writable scope tagged schema
        /// that gains the fact after the child last synced.
        /// </summary>
        [Fact]
        public void ASplitMatcherReadsTheScopesLatestFactsThroughItsMirror()
        {
            var t = new ChooseSplitTheory(scoped: true);
            Assert.Equal(new[] { t.Schema }, t.Split.ReadScopes);
            t.Graph.Root.Freeze();
            var outer = t.Graph.Root.CreateChild(t.Schema);
            var inner = outer.CreateChild();
            inner.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));

            var before = inner.Run(Schedule.Split(t.Split, t.Rules));
            Assert.Equal(0, before.CasesOpened);

            outer.Assume(t.Present.Of("kind"));
            var after = inner.Run(Schedule.Split(t.Split, t.Rules), new RunOptions { RecordSteps = true });

            Assert.Equal(2, after.CasesOpened);
            Assert.Contains(after.Steps, s => s.Kind == RunStepKind.MirrorSync);
            Assert.Empty(inner.Children);
        }

        /// <summary>
        /// Kills: lift steps always marked changed (<c>ObserveLifts</c> ignoring the scope's version). A split step that
        /// finds nothing new still lifts its persistent cases (a memo answer, N92); those lifts change nothing.
        /// </summary>
        [Fact]
        public void ALiftThatChangesNothingIsAnUnchangedStep()
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")), c.Discriminator.Of("kind"));
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            var versions = new List<(RunStep Step, long Version)>();

            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var report = scope.Run(Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle)), new RunOptions
            {
                RecordSteps = true,
                Observer = step => versions.Add((step, scope.Version)),
            });

            var lifts = report.Steps.Where(s => s.Kind == RunStepKind.Lift).ToArray();
            Assert.Contains(lifts, l => l.Changed);
            Assert.Contains(lifts, l => l.Changed == false);

            // A lift that follows a case's iteration is marked changed exactly when the split scope's version moved since
            // that iteration (the cases change only themselves).
            var compared = 0;
            for (var i = 1; i < versions.Count; i++)
            {
                if (versions[i].Step.Kind != RunStepKind.Lift || versions[i - 1].Step.Kind != RunStepKind.Iteration || versions[i - 1].Step.Scope.IsCase == false)
                    continue;

                Assert.Equal(versions[i].Version != versions[i - 1].Version, versions[i].Step.Changed);
                compared++;
            }

            Assert.True(compared >= 2);
        }

        /// <summary>
        /// Kills: <c>Run</c> on a stopped case not returning at once (the D45 early return removed). A schedule of split
        /// steps alone then completes as if the case had a model; the run must report the contradiction, with no work.
        /// </summary>
        [Fact]
        public void RunOnAStoppedCaseReportsTheContradictionWhateverTheSchedule()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));
            using var cases = root.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.Engine.IsStopped);

            foreach (var schedule in new[] { Schedule.Split(t.Split, t.Rules), Schedule.Saturate(Schedule.Split(t.Split, t.Rules)) })
            {
                var report = @case.Run(schedule, new RunOptions { RecordSteps = true });
                Assert.Equal(RunStopReason.Contradiction, report.StopReason);
                Assert.Equal(0, report.CasesOpened);
                Assert.Empty(report.Steps);
            }

            Assert.Empty(@case.Children);
        }

        /// <summary>
        /// Kills: requests left in the engine's scan order (<c>SortByContent</c> removed from <c>RuleSplitSource</c>).
        /// Cases are made in content order of their matches (decision 4), whatever order the facts were written in.
        /// </summary>
        [Fact]
        public void CasesAreMadeInContentOrderOfTheirKeys()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            string[] keys = ["z", "m", "a", "q"];
            foreach (var key in keys)
                root.Assume(t.Dom.Of(key).Is(ValueSet.Of("y", "x")));

            // What each case assumed, by the serial its path ends in: the order the cases were made in.
            var assumed = new SortedDictionary<int, string>();
            root.Run(Schedule.Split(t.Split, t.Rules), new RunOptions
            {
                Observer = step =>
                {
                    // At the lift, the cases are the root's children, alive until the run ends.
                    if (step.Kind != RunStepKind.Lift)
                        return;

                    foreach (var @case in root.Children)
                    {
                        var path = @case.Path;
                        var serial = int.Parse(path[(path.LastIndexOf('#') + 1)..], CultureInfo.InvariantCulture);
                        foreach (var key in keys)
                            foreach (var value in new[] { "x", "y" })
                                if (@case.Check(t.Chosen.Of(key, value)))
                                    assumed[serial] = key + value;
                    }
                },
            });

            Assert.Equal(new[] { "ax", "ay", "mx", "my", "qx", "qy", "zx", "zy" }, assumed.Values.ToArray());
        }

        /// <summary>
        /// Kills: a case whose engine set disposed it reporting <see cref="Scope.IsDisposed"/> false until the run ends
        /// (the <c>ownedByCases &amp;&amp; Engine.IsDisposed</c> term removed). A vacuous case a split step's lift drops is
        /// disposed from then on, as an observer sees.
        /// </summary>
        [Fact]
        public void AVacuousCaseOfASplitIsDisposedOnceTheLiftDropsIt()
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")), c.Discriminator.Of("kind"));
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Graph.Contradiction.Of());
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var vacuous = new List<Scope>();
            var disposedSeen = false;

            var report = scope.Run(Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle)), new RunOptions
            {
                Observer = step =>
                {
                    if (step.Scope.IsCase && step.Scope.IsDisposed == false && step.Scope.IsContradictory && vacuous.Contains(step.Scope) == false)
                        vacuous.Add(step.Scope);

                    if (step.Kind == RunStepKind.Lift || step.Kind == RunStepKind.SplitStep)
                        foreach (var @case in vacuous)
                            if (@case.Engine.IsDisposed)
                            {
                                Assert.True(@case.IsDisposed);
                                disposedSeen = true;
                            }
                },
            });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Single(vacuous);
            Assert.True(disposedSeen, "the vacuous case's context was disposed during the run");
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));         // what the only live case proves
            Assert.Equal(ValueSet.Of("a"), scope.Get(c.Dom.Of("kind")));
        }

        /// <summary>
        /// Kills: the set's vacuity check not bringing a case up to date (<c>CatchUp</c> removed from
        /// <c>CaseSet.IsVacuous</c>). A case disposed directly that is vacuous once caught up with its parent does not
        /// stop the lift (§6.9.4); the lift then finds every case vacuous and contradicts the parent (L4).
        /// </summary>
        [Fact]
        public void ACaseDisposedDirectlyThatIsVacuousDoesNotStopTheLift()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var kept = cases.Add();
            var dropped = cases.Add();
            kept.Assume(t.Fact.Of("a"));
            dropped.Assume(t.Fact.Of("a"));
            dropped.Dispose();
            Assert.Throws<InvalidOperationException>(() => cases.Lift());

            parent.Assume(t.Graph.Contradiction.Of());
            var lifted = cases.Lift();

            Assert.True(lifted.ParentContradicted);
            Assert.True(parent.IsContradictory);
            Assert.Empty(cases.Cases);
        }

        /// <summary>
        /// Kills: a run's end releasing only the cases directly under it (the recursion of
        /// <c>Scope.ReleaseDisposedCases</c> removed). A nested split's cases are released too: they report disposed, leave
        /// their parent case's children, and a class read from one is refused afterwards (D39).
        /// </summary>
        [Fact]
        public void ANestedSplitsCasesAreReleasedWhenTheRunEnds()
        {
            var c = new CosmosTheory(new EGraph());
            var outer = c.Graph.DefineRelation<string>("outer discriminator");
            var inner = c.Graph.DefineRelation<string>("inner discriminator");
            var outerSplit = c.Graph.DefineDomainSplit("outer", c.Dom, [outer], c.Case);
            var innerSplit = c.Graph.DefineDomainSplit("inner", c.Dom, [inner], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Dom.Of("k1").Is(ValueSet.Of("a", "b")), outer.Of("k1"), c.Dom.Of("k2").Is(ValueSet.Of("c", "d")), inner.Of("k2"));
            var q = scope.Make(c.StrEq.Of("k2", "c"));
            var nested = new List<Scope>();
            Pred? fromNested = null;

            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var report = scope.Run(Schedule.Saturate(settle, Schedule.Split(outerSplit, Schedule.Saturate(settle, Schedule.Split(innerSplit, settle)))),
                new RunOptions
                {
                    Observer = step =>
                    {
                        if (step.Scope.IsCase && step.Scope.Parent is { IsCase: true } && nested.Contains(step.Scope) == false)
                        {
                            nested.Add(step.Scope);
                            fromNested ??= step.Scope.Find(q);
                        }
                    },
                });

            Assert.Equal(2 + 4, report.CasesOpened);
            Assert.Equal(4, nested.Count);
            Assert.Empty(scope.Children);
            Assert.False(scope.HasChildren);                                // every case released, not only marked disposed
            Assert.All(nested, n => Assert.True(n.IsDisposed));
            Assert.All(nested, n => Assert.Empty(n.Parent!.Children));
            Assert.NotNull(fromNested);
            var refused = Assert.Throws<ArgumentException>(() => scope.Find(fromNested.Value));
            Assert.Contains("which is disposed", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Kills: a split step that skips the run's checks before it (<c>BeforeStep</c> removed from
        /// <c>SplitSchedule</c>). Limits are checked before every split step as before every iteration (D42): a row limit
        /// the scope already exceeds stops the run before the split opens a case.
        /// </summary>
        [Fact]
        public void LimitsAreCheckedBeforeASplitStep()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));

            var report = root.Run(Schedule.Split(t.Split, t.Rules), new RunOptions { RowLimit = 0 });

            Assert.Equal(RunStopReason.RowLimit, report.StopReason);
            Assert.Equal(0, report.CasesOpened);
            Assert.False(root.Check(t.Chosen.Of("kind", "a")));

            using var cancelled = new System.Threading.CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => root.Run(Schedule.Split(t.Split, t.Rules), null, cancelled.Token));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// Kills: a split's cases made without the split rule's case tag (<c>RuleSplitSource.Assume</c> adopting the case
        /// untagged). The default path segment of an untagged case is <c>case</c>, so the paths alone do not tell.
        /// </summary>
        [Fact]
        public void ASplitsCasesCarryItsCaseTag()
        {
            var t = new ChooseSplitTheory();
            var tag = t.Graph.DefineScopeTag("chosen case");
            var tagged = t.Graph.DefineSplit("choose tagged", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(t.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(t.Chosen.Of(p, v));
            }, tag);
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));
            var cases = new List<Scope>();

            root.Run(Schedule.Split(tagged, t.Rules), new RunOptions
            {
                Observer = step =>
                {
                    if (step.Kind == RunStepKind.Lift)
                        cases.AddRange(root.Children);
                },
            });

            Assert.NotEmpty(cases);
            Assert.All(cases, k => Assert.Same(tag, k.Tag));
            Assert.All(cases, k => Assert.Contains("/chosen case#", k.Path, StringComparison.Ordinal));
        }

        /// <summary>
        /// D45 through a parent: a case its parent's contradiction reaches stops when <c>Make</c> brings it up to date, and
        /// the call throws <see cref="ScopeContradictedException"/>. (The second <c>ThrowIfStopped</c> of <c>Make</c>, after
        /// the commit, survived every test: a <c>Make</c> batch writes only constructor rows and unions, and stops a case
        /// only through a catch-up the entry check already sees.)
        /// </summary>
        [Fact]
        public void AMakeOnACaseItsParentContradictedThrows()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            Assert.False(@case.Engine.IsStopped);

            // The parent's contradiction reaches the case when the Make brings it up to date.
            parent.Assume(t.Graph.Contradiction.Of());
            var refused = Assert.Throws<ScopeContradictedException>(() => @case.Make(t.A.Of()));

            Assert.Equal(@case.Path, refused.ScopePath);
            Assert.True(@case.Engine.IsStopped);
            Assert.True(@case.IsContradictory);
        }

        /// <summary>
        /// Kills: <see cref="CaseSet.Add"/> and <see cref="CaseSet.Lift"/> on a set whose parent is a case that stopped
        /// after the set was opened (their <c>Parent.ThrowIfStopped()</c> removed). Both throw
        /// <see cref="ScopeContradictedException"/> (D45).
        /// </summary>
        [Fact]
        public void ACaseSetUnderAStoppedCaseRefusesAddAndLift()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var outer = parent.OpenCases();
            var @case = outer.Add();
            using var inner = @case.OpenCases();
            inner.Add().Assume(t.Fact.Of("a"));

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.Engine.IsStopped);

            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);
        }

        /// <summary>
        /// Kills: <c>Run</c> accepting a split rule of another graph (its check removed). It is refused before any work
        /// with <see cref="ArgumentException"/>, like a ruleset of another graph.
        /// </summary>
        [Fact]
        public void RunRefusesASplitRuleOfAnotherGraphBeforeAnyWork()
        {
            var t = new ChooseSplitTheory();
            var other = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));
            var seen = t.Graph.DefineRelation<string>("seen");
            t.Rules.DefineRule("see", r => { var p = r.Var<string>("p"); r.When(t.Dom.Of(p).Is(r.Var<ValueSet<string>>("d"))); r.Then(seen.Of(p)); });

            Assert.Throws<ArgumentException>(() => root.Run(Schedule.Sequence(t.Rules, Schedule.Split(other.Split, t.Rules))));

            Assert.False(root.Check(seen.Of("kind")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// Kills: <c>Run</c> not resolving, before any work, the tags a split rule's matcher reads (§6.5.3; the check
        /// removed). The split reads <c>present@schema</c>, which nothing above the root carries: refused with
        /// <see cref="InvalidOperationException"/>, and the schedule's first step never runs.
        /// </summary>
        [Fact]
        public void RunRefusesASplitRuleWhoseTagDoesNotResolveBeforeAnyWork()
        {
            var t = new ChooseSplitTheory(scoped: true);
            var seen = t.Graph.DefineRelation<string>("seen");
            t.Rules.DefineRule("see", r => { var p = r.Var<string>("p"); r.When(t.Dom.Of(p).Is(r.Var<ValueSet<string>>("d"))); r.Then(seen.Of(p)); });
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("a", "b")));

            Assert.Throws<InvalidOperationException>(() => root.Run(Schedule.Sequence(t.Rules, Schedule.Split(t.Split, t.Rules))));

            Assert.False(root.Check(seen.Of("kind")));
            Assert.False(root.IsFaulted);
        }

        /// <summary>
        /// Kills: the run's report dropping the rows its lifts skipped (<c>RunReport.LiftRowsSkipped</c> never summed). Each
        /// case unions <c>a</c> with a different class and tags it, so its row stands for two parent-level candidates; with
        /// <see cref="LiftOptions.CandidateLimit"/> 1 the lift skips it, and the run reports it.
        /// </summary>
        [Fact]
        public void TheRowsASplitsLiftsSkipAreReported()
        {
            for (var limit = 1; limit <= 64; limit += 63)
            {
                var t = new NodeTheory();
                var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
                var chosen = t.Graph.DefineRelation<string, string>("chosen");
                var split = t.Graph.DefineSplit("choose", s =>
                {
                    var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                    s.When(d.Is(dom.Of(p)));
                    var v = s.Cases("v", d);
                    s.Assume(chosen.Of(p, v));
                });
                var root = t.Root;
                var a = root.Make(t.A.Of());
                root.Make(t.B.Of());
                root.Make(t.Lit.Of(1L));
                root.Assume(dom.Of("k").Is(ValueSet.Of("x", "y")));
                t.Rules.DefineRule("x", r => { r.When(chosen.Of("k", "x")); r.Then(t.B.Of().Is(a), t.Tag.Of(a)); });
                t.Rules.DefineRule("y", r => { r.When(chosen.Of("k", "y")); r.Then(t.Lit.Of(1L).Is(a), t.Tag.Of(a)); });

                var report = root.Run(Schedule.Split(split, Schedule.Saturate(t.Rules)),
                    new RunOptions { Lift = new LiftOptions { CandidateLimit = limit } });

                Assert.Equal(2, report.CasesOpened);
                if (limit == 1)
                {
                    Assert.True(report.LiftRowsSkipped > 0, $"{report.LiftRowsSkipped} rows skipped");
                    Assert.False(root.Check(t.Tag.Of(a)));                      // sound, with fewer facts
                }
                else
                {
                    Assert.Equal(0, report.LiftRowsSkipped);
                    Assert.True(root.Check(t.Tag.Of(a)));                       // tagged in both cases
                }
            }
        }

        /// <summary>
        /// An <c>Until</c> node in a case its own rules stopped runs nothing and reads nothing (D45, N102): the step goes on
        /// with the other case, and the lift keeps only the live case's facts (L4).
        /// </summary>
        [Fact]
        public void AnUntilInACaseThatStoppedRunsNothing()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(ValueSet.Of("x", "y")));
            t.Rules.DefineRule("y contradicts", r => { r.When(t.Chosen.Of("kind", "y")); r.Then(t.Graph.Contradiction.Of()); });
            var inCase = Schedule.Sequence(Schedule.Saturate(t.Rules), Schedule.Until(Schedule.Saturate(t.Rules), t.Present.Of("never")));

            var report = root.Run(Schedule.Split(t.Split, inCase));

            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(root.Check(t.Chosen.Of("kind", "x")));               // the only live case
            Assert.False(root.IsContradictory);
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// Two classes a child unions are one key there, split once. (Skipping <c>CanonicalizeColumns</c> in
        /// <c>RuleSplitSource</c> survived this test too: a split step brings its scope up to date before the matcher runs,
        /// so the matcher reads canonical rows, and the canonicalization is a guard against a future engine change.)
        /// </summary>
        [Fact]
        public void ClassesAChildUnionsAreSplitAsOneKey()
        {
            var t = new NodeTheory();
            var domain = t.Graph.DefineFunction<Node, ValueSet<string>>("node dom", ValueLattice.Intersect<string>());
            var chosen = t.Graph.DefineRelation<Node, string>("node chosen");
            var split = t.Graph.DefineSplit("choose node", s =>
            {
                var (n, d) = s.Vars<Node, ValueSet<string>>("n", "d");
                s.When(d.Is(domain.Of(n)));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(n, v));
            });
            var root = t.Root;
            var a = root.Make(t.A.Of());
            var b = root.Make(t.B.Of());
            root.Assume(domain.Of(a).Is(ValueSet.Of("x", "y")), domain.Of(b).Is(ValueSet.Of("x", "y")));
            root.Freeze();
            using var child = root.CreateChild();
            Assert.True(child.Union(a, b));

            var report = child.Run(Schedule.Split(split, t.Rules));

            Assert.Equal(2, report.CasesOpened);
            Assert.False(child.Check(chosen.Of(a, "x")));
            Assert.Empty(child.Children);
        }

    }

}
