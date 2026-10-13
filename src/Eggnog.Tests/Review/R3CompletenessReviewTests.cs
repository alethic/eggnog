using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of R3 against the rule-layer specification (completeness review): behaviour the spec states for R3 that no
    /// R3 test pinned. P01 fails at the reviewed commit (5e7c855): a split the depth limit keeps from being taken is not
    /// counted in <see cref="RunReport.SplitsSkipped"/>, which §3.6 and the member's own documentation say it is.
    /// </summary>
    public sealed class R3CompletenessReviewTests
    {

        /// <summary>
        /// A graph with the Cosmos theory, an empty frozen root, and a writable scope tagged schema beneath it that declares
        /// <paramref name="path"/> a present discriminator with <paramref name="values"/>.
        /// </summary>
        static (CosmosTheory C, Scope Scope) Discriminated(string path, params string[] values)
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of(path), c.Dom.Of(path).Is(ValueSet.Of(values)), c.Discriminator.Of(path));
            return (c, scope);
        }

        /// <summary>
        /// Horn and lattice rules to saturation.
        /// </summary>
        static Schedule Settle(CosmosTheory c) => Schedule.Saturate(c.Horn, c.Lattice);

        /// <summary>
        /// A split over the domain of every path whose fact the outer scope's view holds: its body reads that view through a
        /// scoped atom (a mirror, §6.8).
        /// </summary>
        static SplitRule ScopedSplit(ScopeTheory t, Function<string, ValueSet<string>> dom) =>
            t.Graph.DefineSplit("scoped", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(t.Fact.In(t.Outer).Of(p), d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Key(p);
                s.Assume(dom.Of(p).Is(Primitives.Singleton<string>().Of(v)));
            });

        /// <summary>
        /// §3.6 as N107 restates it: <c>RunReport.SplitsSkipped</c> counts the requests a split step found and did not take
        /// (over CaseLimit, covered by a live split, or an empty element set). A split below SplitDepthLimit is never looked
        /// for (colors §11.3: no phase B at the limit, nested splits disabled in phase A), so it is not counted. With a
        /// depth limit of 1 the inner split, which would open two cases in each outer case, is never taken, and nothing
        /// counts it.
        /// </summary>
        [Fact]
        public void P01_ASplitTheDepthLimitStopsIsNotLookedFor()
        {
            var c = new CosmosTheory(new EGraph());
            var outer = c.Graph.DefineRelation<string>("outer discriminator");
            var inner = c.Graph.DefineRelation<string>("inner discriminator");
            var outerSplit = c.Graph.DefineDomainSplit("outer", c.Dom, [outer], c.Case);
            var innerSplit = c.Graph.DefineDomainSplit("inner", c.Dom, [inner], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(
                c.Dom.Of("k1").Is(ValueSet.Of("a", "b")), outer.Of("k1"),
                c.Dom.Of("k2").Is(ValueSet.Of("c", "d")), inner.Of("k2"));
            var schedule = Schedule.Split(outerSplit, Schedule.Split(innerSplit, Settle(c)));

            // Below the limit: the inner split is taken in both outer cases, and nothing is skipped.
            var deep = scope.Run(schedule, new RunOptions { SplitDepthLimit = 2 });
            Assert.Equal(2 + 4, deep.CasesOpened);
            Assert.Equal(0, deep.SplitsSkipped);

            // At the limit: the outer cases are not split further.
            var shallow = scope.Run(schedule, new RunOptions { SplitDepthLimit = 1 });
            Assert.Equal(2, shallow.CasesOpened);
            Assert.Equal(0, shallow.SplitsSkipped);
        }

        /// <summary>
        /// D48, §6.9.2: before a split matcher reads a mirror, the tagged scope it resolves to, when writable, is brought
        /// current; a host write into that ancestor reaches the next run's matcher.
        /// </summary>
        [Fact]
        public void P02_ASplitMatcherReadsAWritableTaggedAncestorThroughItsMirror()
        {
            var t = new ScopeTheory();
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var split = ScopedSplit(t, dom);
            Assert.Equal(new[] { t.Outer }, split.ReadScopes);
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild();
            inner.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), dom.Of("m").Is(ValueSet.Of("c", "d", "e")));

            Assert.Equal(0, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);

            outer.Assume(t.Fact.Of("k"));
            Assert.Equal(2, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);

            outer.Assume(t.Fact.Of("m"));
            Assert.Equal(2 + 3, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);
            Assert.Empty(inner.Children);
        }

        /// <summary>
        /// §6.5.3: a split rule of another graph is refused before any work, so the earlier steps write nothing.
        /// </summary>
        [Fact]
        public void P03_ARunRefusesASplitRuleOfAnotherGraphBeforeAnyWork()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var other = new CosmosTheory(new EGraph());
            var version = scope.Version;

            var refused = Assert.Throws<ArgumentException>(() => scope.Run(Schedule.Sequence(Settle(c), Schedule.Split(other.SchemaSplit, c.Horn))));

            Assert.Contains("belongs to another graph", refused.Message, StringComparison.Ordinal);
            Assert.Equal(version, scope.Version);
            Assert.False(scope.IsFaulted);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// §6.5.3: every tag a reachable split rule reads must resolve from the scope, before any work.
        /// </summary>
        [Fact]
        public void P04_ARunRefusesASplitRuleWhoseTagDoesNotResolveBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var split = ScopedSplit(t, dom);
            t.DefinePaths();
            var untagged = t.Root.CreateChild();
            untagged.Assume(t.Edge.Of(1L, 2L), dom.Of("k").Is(ValueSet.Of("a", "b")));
            var version = untagged.Version;

            var refused = Assert.Throws<InvalidOperationException>(() => untagged.Run(Schedule.Sequence(t.Rules, Schedule.Split(split, t.Rules))));

            Assert.Contains("outer", refused.Message, StringComparison.Ordinal);
            Assert.Equal(version, untagged.Version);
            Assert.False(untagged.Check(t.Path.Of(1L, 2L)));
            Assert.False(untagged.IsFaulted);
        }

        /// <summary>
        /// N94, D18: a split matcher whose filter throws fails the run; the scope the run was called on is faulted with
        /// that cause, and the run's cases are gone.
        /// </summary>
        [Fact]
        public void P05_AFailingSplitMatcherFaultsTheScopeTheRunWasCalledOn()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var failing = c.Graph.DefineSplit("failing", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(c.Discriminator.Of(p), d.Is(c.Dom.Of(p)));
                s.Where(p, _ => throw new InvalidOperationException("in a matcher"));
                var v = s.Cases("v", d);
                s.Key(p);
                s.Assume(c.Dom.Of(p).Is(Primitives.Singleton<string>().Of(v)));
            });

            var failure = Assert.Throws<RuleEvaluationException>(() => scope.Run(Schedule.Split(failing, c.Horn)));

            Assert.True(scope.IsFaulted);
            Assert.Same(failure, scope.Failure);
            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
        }

        /// <summary>
        /// §3.1 <c>Children</c>, §6.9.2: while the run lasts, a split's cases are children of the split scope, tagged with the
        /// split rule's case tag, and cases; they are disposed and leave the list when it ends.
        /// </summary>
        [Fact]
        public void P06_ASplitsCasesAreTaggedChildrenOfTheSplitScopeWhileTheRunLasts()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var seen = new List<Scope>();
            var listed = true;

            scope.Run(Schedule.Split(c.SchemaSplit, c.Horn), new RunOptions
            {
                Observer = step =>
                {
                    if (step.Scope.IsCase == false)
                        return;

                    if (seen.Contains(step.Scope) == false)
                        seen.Add(step.Scope);

                    listed &= scope.Children.Contains(step.Scope);
                },
            });

            Assert.Equal(2, seen.Count);
            Assert.True(listed);
            Assert.All(seen, k =>
            {
                Assert.Same(c.Case, k.Tag);
                Assert.Same(scope, k.Parent);
                Assert.True(k.IsDisposed);
                Assert.StartsWith(scope.Path + "/case#", k.Path, StringComparison.Ordinal);
            });
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// N99: the definition errors of a split rule are RuleDefinitionExceptions naming it, and nothing is registered.
        /// </summary>
        [Fact]
        public void P07_SplitDefinitionErrorsNameTheSplitRule()
        {
            var c = new CosmosTheory(new EGraph());
            var child = c.Graph.Root.CreateChild();
            var childClass = child.Make(c.PTrue.Of());
            Var<ValueSet<string>>? stray = null;

            RuleDefinitionException Refused(string name, Action<SplitBuilder> define)
            {
                var refused = Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit(name, define));
                Assert.Equal(name, refused.RuleName);
                return refused;
            }

            Refused("stray source", s => stray = s.Var<ValueSet<string>>("d"));
            Refused("cases twice", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Cases("w", d);
            });
            Refused("a foreign set", s =>
            {
                var p = s.Var<string>("p");
                s.When(c.Present.Of(p));
                s.Cases("v", stray!);
            });
            Refused("the element in the key", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Key(p, v);
            });
            Refused("a foreign key", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Key(stray!);
            });
            var filter = Refused("a filter assumed", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(p.IsNot(v));
            });
            Assert.Contains("assumptions are writes", filter.Message, StringComparison.Ordinal);
            Refused("a class of a child", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Assume(c.WhereClause.Of(childClass));
            });

            Assert.Equal(new[] { c.SchemaSplit, c.QuerySplit }, c.Graph.SplitRules);
        }

        /// <summary>
        /// D45 in a split: a case whose contradiction stopped it answers IsContradictory and refuses reads with
        /// ScopeContradictedException; the lift leaves it out, so the scope learns only what the live case proves.
        /// </summary>
        [Fact]
        public void P08_AStoppedSplitCaseAnswersOnlyWhatNeedsNoModel()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            scope.Assume(c.Present.Of("x"), c.Dom.Of("x").Is(ValueSet.Of("p")));
            c.DeclareBranch(scope, "kind", "b", c.Dom.Of("x").Is(ValueSet.Of("q")));
            Scope? stopped = null;
            string? refusedPath = null;

            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))), new RunOptions
            {
                Observer = step =>
                {
                    if (stopped is null && step.Scope.IsCase && step.Scope.IsContradictory)
                    {
                        stopped = step.Scope;
                        refusedPath = Assert.Throws<ScopeContradictedException>(() => stopped.Check(c.Present.Of("x"))).ScopePath;
                    }
                },
            });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.NotNull(stopped);
            Assert.Equal(stopped.Path, refusedPath);
            Assert.False(scope.IsContradictory);
            Assert.Equal(ValueSet.Of("a"), scope.Get(c.Dom.Of("kind")));
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// §6.9.3, §6.9.4: a run's split disposes only its own cases; a host-driven set open on the same scope keeps its
        /// case, which sees what the split lifted, and can still lift.
        /// </summary>
        [Fact]
        public void P09_ARunsSplitLeavesAnOpenHostCaseSetAlone()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            using var cases = scope.OpenCases();
            var host = cases.Add(c.Case);

            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))));

            Assert.Equal(2, report.CasesOpened);
            Assert.Same(host, Assert.Single(scope.Children));
            Assert.False(host.IsDisposed);
            Assert.True(host.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Equal(1, cases.Lift().LiveCases);
        }

        /// <summary>
        /// D45, N101: a host-driven set whose parent is a stopped case refuses Add and Lift with ScopeContradictedException.
        /// </summary>
        [Fact]
        public void P10_ACaseSetUnderAStoppedCaseRefusesAddAndLift()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var outer = parent.OpenCases();
            var @case = outer.Add();
            using var inner = @case.OpenCases();
            inner.Add();

            @case.Assume(t.Graph.Contradiction.Of());

            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => inner.Lift());
            Assert.Throws<ScopeContradictedException>(() => inner.Add());
        }

        /// <summary>
        /// §6.9.2: a split keyed on a class: the matcher's class cells are canonical in the split scope, and each case's
        /// assumption over the class is written into the case.
        /// </summary>
        [Fact]
        public void P11_ASplitKeyedOnAClassAssumesOverThatClass()
        {
            var t = new NodeTheory();
            var labels = t.Graph.DefineFunction<Node, ValueSet<string>>("labels", ValueLattice.Intersect<string>());
            var split = t.Graph.DefineSplit("by label", s =>
            {
                var (n, d) = s.Vars<Node, ValueSet<string>>("n", "d");
                s.When(t.Tag.Of(n), d.Is(labels.Of(n)));
                var v = s.Cases("v", d);
                s.Key(n);
                s.Assume(labels.Of(n).Is(Primitives.Singleton<string>().Of(v)));
            });
            foreach (var label in new[] { "x", "y" })
            {
                t.Rules.DefineRule($"labelled {label}", r =>
                {
                    var n = r.Var<Node>("n");
                    r.When(labels.Of(n).Is(ValueSet.Of(label)));
                    r.Then(t.Num.Of(1L));
                });
            }

            var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            scope.Assume(t.Tag.Of(a), t.Tag.Of(b), labels.Of(a).Is(ValueSet.Of("x", "y")));
            scope.Union(a, b);

            var report = scope.Run(Schedule.Split(split, t.Rules));

            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.Check(t.Num.Of(1L)));
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// D17, §6.12: queries under one frozen container run their split steps concurrently on parallel threads (each query
        /// scope is its own thread domain; split rules, their sources, lift specs and case joins are shared), and give the
        /// results of serial runs.
        /// </summary>
        [Fact]
        public void P12_SplitQueriesUnderOneFrozenContainerOnParallelThreadsGiveTheSerialResults()
        {
            static string Ask(CosmosTheory c, Scope schema, ConstructorTerm<Pred> term)
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Empty(query.Children);
                var best = query.Extract(q);
                var forms = string.Join(",", query.Rows(c.Form).Select(r => r.ToString()));
                return $"{best} cost {best.Cost} cases {report.CasesOpened} contradictory {query.IsContradictory} forms {forms}";
            }

            var serial = new CosmosTheory(new EGraph());
            serial.Graph.Root.Freeze();
            var serialSchema = SweepLinks.Compile(serial);
            var expected = SweepLinks.Queries(serial).Select(term => Ask(serial, serialSchema, term)).ToArray();
            Assert.Contains(expected, e => e.Contains("cases 2", StringComparison.Ordinal));

            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = SweepLinks.Compile(c);
            var terms = SweepLinks.Queries(c);
            var results = new string[32];

            var threads = DedicatedThreads.For(8, results.Length, i => results[i] = Ask(c, schema, terms[i % terms.Length]));

            Assert.Equal(8, threads);
            for (var i = 0; i < results.Length; i++)
                Assert.Equal(expected[i % expected.Length], results[i]);

            Assert.Empty(schema.Children);
            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// §6.9.3 (colors N58, N91): two split nodes over one scope each run their in-case schedule only in the cases they
        /// opened, and each lifts what its own cases prove.
        /// </summary>
        [Fact]
        public void P13_EachSplitNodeRunsItsInCaseScheduleOnlyInItsOwnCases()
        {
            var c = new CosmosTheory(new EGraph());
            var first = c.Graph.DefineRelation<string>("first discriminator");
            var second = c.Graph.DefineRelation<string>("second discriminator");
            var tagA = c.Graph.DefineScopeTag("first cases");
            var tagB = c.Graph.DefineScopeTag("second cases");
            var splitA = c.Graph.DefineDomainSplit("first", c.Dom, [first], tagA);
            var splitB = c.Graph.DefineDomainSplit("second", c.Dom, [second], tagB);
            var inA = c.Graph.DefineRuleset("in first");
            var inB = c.Graph.DefineRuleset("in second");
            inA.DefineRule("marks first", r =>
            {
                var p = r.Var<string>("p");
                r.When(first.Of(p));
                r.Then(c.Ne.Of(p, "first"));
            });
            inB.DefineRule("marks second", r =>
            {
                var p = r.Var<string>("p");
                r.When(second.Of(p));
                r.Then(c.Ne.Of(p, "second"));
            });
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(
                c.Dom.Of("k1").Is(ValueSet.Of("x", "y")), first.Of("k1"),
                c.Dom.Of("k2").Is(ValueSet.Of("u", "v")), second.Of("k2"));
            var steps = new List<RunStep>();

            var report = scope.Run(Schedule.Saturate(Schedule.Split(splitA, inA), Schedule.Split(splitB, inB)), new RunOptions { Observer = steps.Add });

            Assert.Equal(4, report.CasesOpened);
            var caseIterations = steps.Where(s => s.Kind == RunStepKind.Iteration && s.Scope.IsCase).ToArray();
            Assert.Contains(caseIterations, s => s.Scope.Tag == tagA);
            Assert.Contains(caseIterations, s => s.Scope.Tag == tagB);
            Assert.All(caseIterations, s => Assert.All(s.Rules, r => Assert.Same(s.Scope.Tag == tagA ? inA : inB, r.Rule.Ruleset)));
            Assert.True(scope.Check(c.Ne.Of("k1", "first"), c.Ne.Of("k2", "second")));
            Assert.False(scope.Check(c.Ne.Of("k1", "second")));
            Assert.False(scope.Check(c.Ne.Of("k2", "first")));
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// N100: RunOptions.Lift refuses null.
        /// </summary>
        [Fact]
        public void P14_RunOptionsRefuseANullLift()
        {
            Assert.Throws<ArgumentNullException>(() => new RunOptions { Lift = null! });
            Assert.Same(LiftOptions.Default, RunOptions.Default.Lift);
        }

    }

}
