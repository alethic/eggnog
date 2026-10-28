using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// Split steps: the cases a split rule opens, how long they live, what they lift, the limits and what the run
    /// reports of them, nesting, and the checks of split rules at definition and before a run. Most tests use
    /// <see cref="PredicateTheory"/>: <c>dom</c> is a discriminator's domain, and branch rules local to the split scope
    /// are what each case proves; others use <see cref="ChooseSplitTheory"/> or a theory of their own.
    /// </summary>
    public sealed class SplitTests
    {

        /// <summary>
        /// A graph with the theory, an empty frozen root, and a writable scope tagged schema beneath it that declares
        /// <paramref name="path"/> a present discriminator with <paramref name="values"/>.
        /// </summary>
        static (PredicateTheory C, Scope Scope) Discriminated(string path, params string[] values)
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of(path), c.Dom.Of(path).Is(Set.Of(values)), c.Discriminator.Of(path));
            return (c, scope);
        }

        /// <summary>
        /// Horn and lattice rules to saturation.
        /// </summary>
        static Schedule Settle(PredicateTheory c) => Schedule.Saturate(c.Horn, c.Lattice);

        [Fact]
        public void ASplitLiftsWhatEveryCaseProvesAndDisposesItsCases()
        {
            var (c, scope) = Discriminated("kind", "a", "b", "c");
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"), c.Present.Of("x"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "c", c.Form.Of("ref", "uuid-lower"), c.Present.Of("x"));

            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))));

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.True(report.Changed);
            Assert.Equal(3, report.CasesOpened);
            Assert.True(report.Lifts >= 1);
            Assert.Equal(0, report.LiftRowsSkipped);
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.False(scope.Check(c.Present.Of("x")));                  // the b case does not prove it
            Assert.Empty(scope.Children);
            Assert.Contains(", 3 cases,", report.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void PersistentCasesTakeInTheScopesLaterFactsWithinOneRun()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var late = c.Graph.DefineRuleset("late");
            late.DefineRule("present kind makes late", r => { r.When(c.Present.Of("kind")); r.Then(c.Present.Of("late")); });
            foreach (var value in new[] { "a", "b" })
            {
                scope.DefineRule(c.Horn, $"kind = {value} and late", r =>
                {
                    r.When(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(value)), c.Present.Of("late"));
                    r.Then(c.Form.Of("ref", "uuid-lower"));
                });
            }

            // The split comes before the rule that writes late: its cases learn late from the scope in the next pass,
            // and the step lifts what they derive from it then, without splitting kind again: the run's live split
            // covers it.
            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c)), late));

            Assert.Equal(2, report.CasesOpened);
            Assert.True(report.SplitsSkipped >= 1);
            Assert.True(scope.Check(c.Present.Of("late")));
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Empty(scope.Children);
        }

        [Fact]
        public void TheRunsRecordsDieWithItSoTheNextRunSplitsAgain()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var split = Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c)));

            var first = scope.Run(split);
            Assert.Equal(2, first.CasesOpened);
            Assert.Empty(scope.Children);

            // The second pass of each run finds the key covered by the run's own live split; the next run does not.
            Assert.Equal(1, first.SplitsSkipped);
            var second = scope.Run(split);
            Assert.Equal(2, second.CasesOpened);
            Assert.Equal(1, second.SplitsSkipped);
            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
        }

        [Fact]
        public void TheCaseLimitSkipsALargeSplit()
        {
            var (c, scope) = Discriminated("kind", "a", "b", "c");
            var split = Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c)));

            var limited = scope.Run(split, new RunOptions { CaseLimit = 2 });
            Assert.Equal(0, limited.CasesOpened);
            Assert.Equal(2, limited.SplitsSkipped);                 // once per pass of the saturation
            Assert.True(limited.Saturated);

            Assert.Equal(3, scope.Run(split, new RunOptions { CaseLimit = 3 }).CasesOpened);
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.Run(split, new RunOptions { CaseLimit = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.Run(split, new RunOptions { SplitDepthLimit = 0 }));
        }

        [Fact]
        public void AnEmptyElementSetIsSkippedAndCounted()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var unfiltered = c.Graph.DefineSplit("any domain", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(c.Dom.Of(p).Is(Primitives.Singleton<string>().Of(v)));
            });

            // dom(empty) = {} with no presence: no contradiction, and nothing to split.
            scope.Assume(c.Dom.Of("empty").Is(Set<string>.Empty));
            var report = scope.Run(Schedule.Split(unfiltered, Settle(c)));

            Assert.Equal(2, report.CasesOpened);                    // kind's two values
            Assert.Equal(1, report.SplitsSkipped);                  // empty's no value
            Assert.Empty(scope.Children);
        }

        [Fact]
        public void AGuardThatFailsOpensNoCase()
        {
            var (c, scope) = Discriminated("kind", "a", "b");

            // QuerySplit is guarded by mentioned(kind), which no term here derives.
            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.QuerySplit, Settle(c))));

            Assert.Equal(0, report.CasesOpened);
            Assert.Equal(0, report.SplitsSkipped);
            Assert.True(report.Saturated);
        }

        [Fact]
        public void ASplitStepOnAContradictoryScopeDoesNothing()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            scope.Assume(c.Graph.Contradiction.Of());

            var report = scope.Run(Schedule.Split(c.SchemaSplit, Settle(c)), new RunOptions { RecordSteps = true });

            Assert.Equal(0, report.CasesOpened);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Empty(report.Steps);
        }

        [Fact]
        public void TheDomainSplitEqualsTheHandWrittenRule()
        {
            var results = new List<(RunReport Report, bool Form, Set<string> Dom)>();
            for (var handWritten = 0; handWritten < 2; handWritten++)
            {
                var (c, scope) = Discriminated("kind", "a", "b", "c");
                c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(Set.Of("x")));
                c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(Set.Of("y")));
                c.DeclareBranch(scope, "kind", "c", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(Set.Of("x", "z")));
                var rule = handWritten == 0 ? c.SchemaSplit : c.Graph.DefineSplit("by hand", s =>
                {
                    var (k, d) = s.Vars<string, Set<string>>("k", "d");
                    s.When(c.Discriminator.Of(k), c.Present.Of(k), d.Is(c.Dom.Of(k)));
                    s.Where(Primitives.Greater.Of(Primitives.Count<string>().Of(d), 1L));
                    var v = s.Cases("v", d);
                    s.Key(k);
                    s.Assume(c.Dom.Of(k).Is(Primitives.Singleton<string>().Of(v)));
                }, c.Case);

                var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(rule, Settle(c))));
                results.Add((report, scope.Check(c.Form.Of("ref", "uuid-lower")), scope.Get(c.Dom.Of("status"))));
                if (handWritten == 1)
                {
                    // Both render the same body, cases, key and assumption.
                    Assert.Equal(c.SchemaSplit.ToString(), rule.ToString());
                    Assert.Equal(
                        "(split ((discriminator k) (present k) (= d (dom k)) (> (set-length d) 1)) (cases v d) (key k) ((set (dom k) (set-of v))) :case-tag case)",
                        rule.ToString());
                    Assert.Equal(c.SchemaSplit.ReadScopes, rule.ReadScopes);
                }
            }

            Assert.Equal(results[0].Report.CasesOpened, results[1].Report.CasesOpened);
            Assert.Equal(3, results[0].Report.CasesOpened);
            Assert.True(results[0].Form);
            Assert.True(results[1].Form);
            Assert.Equal(Set.Of("x", "y", "z"), results[0].Dom);
            Assert.Equal(results[0].Dom, results[1].Dom);
        }

        [Fact]
        public void NestedSplitsLiftBeforeTheyDeepen()
        {
            for (var depthLimit = 1; depthLimit <= 2; depthLimit++)
            {
                var c = new PredicateTheory(new EGraph());
                var outer = c.Graph.DefineRelation<string>("outer discriminator");
                var inner = c.Graph.DefineRelation<string>("inner discriminator");
                var outerSplit = c.Graph.DefineDomainSplit("outer", c.Dom, [outer], c.Case);
                var innerSplit = c.Graph.DefineDomainSplit("inner", c.Dom, [inner], c.Case);
                c.Graph.Root.Freeze();
                var scope = c.Graph.Root.CreateChild(c.Schema);
                scope.Assume(
                    c.Dom.Of("k1").Is(Set.Of("a", "b")), outer.Of("k1"),
                    c.Dom.Of("k2").Is(Set.Of("c", "d")), inner.Of("k2"));
                foreach (var v1 in new[] { "a", "b" })
                {
                    foreach (var v2 in new[] { "c", "d" })
                    {
                        // Only a pair of values proves the fact: a split on one path alone proves nothing.
                        scope.DefineRule(c.Horn, $"{v1} and {v2}", r =>
                        {
                            r.When(c.Dom.Of("k1").Is(Set.Of(v1)), c.Dom.Of("k2").Is(Set.Of(v2)));
                            r.Then(c.Form.Of("both", "f"));
                        });
                    }
                }

                var steps = new List<RunStep>();
                var schedule = Schedule.Saturate(Settle(c), Schedule.Split(outerSplit, Schedule.Saturate(Settle(c), Schedule.Split(innerSplit, Settle(c)))));
                var report = scope.Run(schedule, new RunOptions { SplitDepthLimit = depthLimit, Observer = steps.Add });

                Assert.Empty(scope.Children);
                if (depthLimit == 1)
                {
                    // No phase B: the outer cases are never split further, and nothing is proven.
                    Assert.Equal(2, report.CasesOpened);
                    Assert.False(scope.Check(c.Form.Of("both", "f")));
                    continue;
                }

                // Phase A runs each outer case without nested splits and lifts; phase B splits each again on k2.
                Assert.Equal(2 + 4, report.CasesOpened);
                Assert.True(scope.Check(c.Form.Of("both", "f")));
                var splitSteps = steps.Where(s => s.Kind == RunStepKind.SplitStep).ToArray();
                Assert.Contains(splitSteps, s => s.Scope == scope && s.CasesOpened == 2);
                Assert.Equal(2, splitSteps.Count(s => s.Scope.IsCase && s.CasesOpened == 2));
                var firstNested = steps.FindIndex(s => s.Kind == RunStepKind.SplitStep && s.Scope.IsCase && s.CasesOpened > 0);
                var firstLift = steps.FindIndex(s => s.Kind == RunStepKind.Lift && s.Scope == scope);
                Assert.True(firstLift >= 0 && firstLift < firstNested, "the outer cases are lifted before they are split further");
            }
        }

        [Fact]
        public void TheCasesAreDisposedWhenARunStopsEarly()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))), new RunOptions { IterationLimit = 4 });

            Assert.Equal(RunStopReason.IterationLimit, report.StopReason);
            Assert.Equal(4, report.Iterations);
            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
            Assert.False(scope.IsFaulted);
        }

        [Fact]
        public void TheCasesAreDisposedWhenARunThrowsAndTheScopeIsFaulted()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            scope.DefineRule(c.Horn, "a case that fails", r =>
            {
                var d = r.Var<Set<string>>("d");
                r.When(d.Is(c.Dom.Of("kind")));
                r.Where(d, d => d.Count == 1 ? throw new InvalidOperationException("in a case") : true);
                r.Then(c.Present.Of("seen"));
            });

            var failure = Assert.Throws<RuleEvaluationException>(() => scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c)))));

            Assert.Equal("a case that fails", failure.RuleName);
            Assert.True(scope.IsFaulted);
            Assert.Same(failure, scope.Failure);
            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
        }

        [Fact]
        public void ACaseDisposedFromAnObserverIsReleasedWhenTheRunEnds()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            Scope? disposed = null;

            // The split's engine set owns its cases: a Dispose marks the case disposed at once, and the driver still
            // runs and lifts it; its release waits for the run's end.
            var report = scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))), new RunOptions
            {
                Observer = step =>
                {
                    if (disposed is null && step.Scope.IsCase)
                    {
                        disposed = step.Scope;
                        disposed.Dispose();
                        Assert.True(disposed.IsDisposed);
                        Assert.False(disposed.Engine.IsDisposed);
                    }
                },
            });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.NotNull(disposed);
            Assert.True(disposed.Engine.IsDisposed);
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Empty(scope.Children);
        }

        [Fact]
        public void OpenCasesOnAFrozenScopeThrows()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            scope.Freeze();

            var refused = Assert.Throws<InvalidOperationException>(scope.OpenCases);
            Assert.Contains("open cases in a child of it", refused.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => c.Graph.Root.OpenCases());
        }

        [Fact]
        public void WatermarkInheritanceOffReachesTheCases()
        {
            foreach (var inherit in new[] { true, false })
            {
                var (c, scope) = Discriminated("kind", "a", "b");
                c.Graph.InheritWatermarks = inherit;
                var cases = new List<Scope>();
                scope.Run(Schedule.Saturate(Settle(c), Schedule.Split(c.SchemaSplit, Settle(c))),
                    new RunOptions { Observer = s => { if (s.Scope.IsCase) cases.Add(s.Scope); } });

                Assert.NotEmpty(cases);
                Assert.All(cases, k => Assert.Equal(inherit, k.Engine.Context.Options.InheritWatermarks));

                using var host = scope.OpenCases();
                Assert.Equal(inherit, host.Add().Engine.Context.Options.InheritWatermarks);
            }
        }

        [Fact]
        public void SplitRulesAreListedAndCheckedAtDefinition()
        {
            var c = new PredicateTheory(new EGraph());
            Assert.Equal(new[] { c.SchemaSplit, c.QuerySplit }, c.Graph.SplitRules);
            Assert.Same(c.Case, c.SchemaSplit.CaseTag);
            Assert.Empty(c.SchemaSplit.ReadScopes);
            Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("discriminator", c.Dom, [c.Present]));
            Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("no guard", c.Dom, Array.Empty<Relation<string>>()));
            Assert.Throws<ArgumentException>(() => c.Graph.DefineSplit("root cases", s => { }, c.Graph.RootTag));
            Assert.Throws<ArgumentException>(() => Schedule.Split(Array.Empty<SplitRule>(), c.Horn));
            Assert.Throws<ArgumentException>(() => Schedule.Split([c.SchemaSplit, c.SchemaSplit], c.Horn));

            var noCases = Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("no cases", s => s.When(c.Present.Of(s.Var<string>("p")))));
            Assert.Equal("no cases", noCases.RuleName);
            Assert.EndsWith("a split rule needs Cases: the set whose elements make the cases", noCases.Message, StringComparison.Ordinal);
            Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("element in the body", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.When(c.Ne.Of(p, v));
            }));
            Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("a read in an assumption", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(c.Present.Of(p), c.Ne.Of(p, v), c.Types.Of(p).Is(c.Dom.Of(p)));
            }));
            Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("a scoped assumption", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Assume(c.Present.In(c.Schema).Of(p));
            }));
            Assert.Equal(2, c.Graph.SplitRules.Count);
        }

        /// <summary>
        /// A domain split writes its domain in each case, so the domain must be writable (not a scoped view) and have a
        /// lattice that narrows it; a guard is only read, and may be a scoped view, as in <c>DefineSplit</c>'s body.
        /// </summary>
        [Fact]
        public void ADomainSplitNeedsAWritableDomainWithALatticeAndReadsItsGuardsAnywhere()
        {
            var c = new PredicateTheory(new EGraph());
            var plain = c.Graph.DefineFunction<string, Set<string>>("plain domain");

            var noLattice = Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("no lattice", plain, [c.Present]));
            Assert.Contains("has no lattice", noLattice.Message, StringComparison.Ordinal);
            var scopedDomain = Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("scoped domain", c.Dom.In(c.Schema), [c.Present]));
            Assert.Contains("scoped view", scopedDomain.Message, StringComparison.Ordinal);

            var guarded = c.Graph.DefineDomainSplit("guarded in the schema", c.Dom, [c.Present.In(c.Schema)], c.Case);
            Assert.Equal(new[] { c.Schema }, guarded.ReadScopes);
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("a", "b")));
            var query = schema.CreateChild(c.Query);
            var report = query.Run(Schedule.Split(guarded, Settle(c)));
            Assert.Equal(2, report.CasesOpened);
            Assert.Empty(query.Children);
        }

        /// <summary>
        /// A graph with a domain function and two guard relations, each with a domain split, under an empty frozen
        /// root, and a writable scope that declares <c>k1</c> (outer, {a, b}) and <c>k2</c> (inner, {c, d}).
        /// </summary>
        static (EGraph Graph, Scope Scope, SplitRule Outer, SplitRule Inner, Ruleset Rules) TwoSplits()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var outer = graph.DefineRelation<string>("outer");
            var inner = graph.DefineRelation<string>("inner");
            var outerSplit = graph.DefineDomainSplit("outer", dom, [outer]);
            var innerSplit = graph.DefineDomainSplit("inner", dom, [inner]);
            var rules = graph.DefineRuleset("rules");
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k1").Is(Set.Of("a", "b")), outer.Of("k1"), dom.Of("k2").Is(Set.Of("c", "d")), inner.Of("k2"));
            return (graph, scope, outerSplit, innerSplit, rules);
        }

        /// <summary>
        /// <see cref="RunReport.SplitsSkipped"/> counts the requests a split step found and did not take. A nested
        /// split the depth limit stops is never searched for (at the limit there is no phase B, and phase A runs the
        /// cases without nested splits), so it adds nothing to the count; a key a live split of the same run already
        /// covers is counted, at each execution of the step that finds it covered.
        /// </summary>
        [Fact]
        public void SplitsSkippedCountsCoveredKeysButNeverADepthLimitedSplit()
        {
            // Depth limit 1: the outer split opens its two cases, and the inner split, which would open two more in
            // each, never runs.
            var (_, scope, outer, inner, rules) = TwoSplits();
            var limited = scope.Run(Schedule.Split(outer, Schedule.Split(inner, rules)), new RunOptions { SplitDepthLimit = 1 });
            Assert.Equal(2, limited.CasesOpened);
            Assert.Equal(0, limited.SplitsSkipped);

            // Depth limit 2: the inner split opens two cases in each outer case.
            (_, scope, outer, inner, rules) = TwoSplits();
            var deep = scope.Run(Schedule.Split(outer, Schedule.Split(inner, rules)), new RunOptions { SplitDepthLimit = 2 });
            Assert.Equal(2 + 4, deep.CasesOpened);
            Assert.Equal(0, deep.SplitsSkipped);

            // A second execution of the step in one run finds k1 covered by its own live split: counted as skipped.
            (_, scope, outer, _, rules) = TwoSplits();
            var twice = scope.Run(Schedule.Sequence(Schedule.Split(outer, rules), Schedule.Split(outer, rules)));
            Assert.Equal(2, twice.CasesOpened);
            Assert.Equal(1, twice.SplitsSkipped);
        }

        /// <summary>
        /// A split's assumptions are written by the run, into its cases. An assumption that gives a function without a
        /// lattice a second value is a write conflict during a run: <see cref="Scope.Run"/> throws
        /// <see cref="RuleEvaluationException"/>, the scope is faulted, and the run's cases are gone.
        /// </summary>
        [Fact]
        public void AnAssumptionConflictInASplitIsAnEvaluationFailureOfTheRun()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineFunction<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p).Is(v));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(Set.Of("a", "b")), pick.Of("k").Is("a"));

            // The b case's assumption pick(k) = b conflicts with the inherited pick(k) = a.
            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, rules)));

            Assert.NotNull(failure);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// The cases of a split whose assumption conflicts are disposed when the run ends, as they are whether the run
        /// completes, stops at a limit or throws, and the scope's split records are gone.
        /// </summary>
        [Fact]
        public void AnAssumptionConflictLeavesNoCaseBehind()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineFunction<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p).Is(v));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(Set.Of("a", "b")), pick.Of("k").Is("a"));

            Assert.ThrowsAny<Exception>(() => scope.Run(Schedule.Split(split, rules)));

            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
            Assert.Equal(0, scope.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// <c>DefineDomainSplit</c> accepts a guard read through a scoped view, as <c>DefineSplit</c> accepts scoped
        /// reads in its body: the split written out with its guard read in the schema reads that tag, and the domain
        /// split guarded in the schema is defined without an error.
        /// </summary>
        [Fact]
        public void ADomainSplitRefusesAScopedGuardThatDefineSplitAccepts()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var present = graph.DefineRelation<string>("present");
            var schema = graph.DefineScopeTag("schema");

            // The same split, written out, with the guard read in the schema.
            var written = graph.DefineSplit("written out", s =>
            {
                var (k, d) = s.Vars<string, Set<string>>("k", "d");
                s.When(present.In(schema).Of(k), d.Is(dom.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<string>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Key(k);
                s.Assume(dom.Of(k).Is(Primitives.Singleton<string>().Of(v)));
            });
            Assert.Equal(new[] { schema }, written.ReadScopes);

            var refused = Record.Exception(() => graph.DefineDomainSplit("guarded in the schema", dom, [present.In(schema)]));
            Assert.Null(refused);
        }

        /// <summary>
        /// A split over the domain of every path whose fact the outer scope's view holds: its body reads that view
        /// through a scoped atom, which a mirror table serves.
        /// </summary>
        static SplitRule ScopedSplit(ScopeTheory t, Function<string, Set<string>> dom) =>
            t.Graph.DefineSplit("scoped", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(t.Fact.In(t.Outer).Of(p), d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Key(p);
                s.Assume(dom.Of(p).Is(Primitives.Singleton<string>().Of(v)));
            });

        /// <summary>
        /// <see cref="RunReport.SplitsSkipped"/> counts the requests a split step found and did not take: over
        /// <c>CaseLimit</c>, covered by a live split, or an empty element set. A split below <c>SplitDepthLimit</c> is
        /// never looked for (at the limit there is no phase B, and phase A runs the cases without nested splits), so it
        /// is not counted: with a depth limit of 1 the inner split, which would open two cases in each outer case, is
        /// never taken, and nothing counts it.
        /// </summary>
        [Fact]
        public void ASplitTheDepthLimitStopsIsNotLookedFor()
        {
            var c = new PredicateTheory(new EGraph());
            var outer = c.Graph.DefineRelation<string>("outer discriminator");
            var inner = c.Graph.DefineRelation<string>("inner discriminator");
            var outerSplit = c.Graph.DefineDomainSplit("outer", c.Dom, [outer], c.Case);
            var innerSplit = c.Graph.DefineDomainSplit("inner", c.Dom, [inner], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(
                c.Dom.Of("k1").Is(Set.Of("a", "b")), outer.Of("k1"),
                c.Dom.Of("k2").Is(Set.Of("c", "d")), inner.Of("k2"));
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
        /// Before a split matcher reads a mirror, the tagged scope it resolves to, when writable, is brought current: a
        /// host write into that ancestor reaches the next run's matcher.
        /// </summary>
        [Fact]
        public void ASplitMatcherReadsAWritableTaggedAncestorThroughItsMirror()
        {
            var t = new ScopeTheory();
            var dom = t.Graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var split = ScopedSplit(t, dom);
            Assert.Equal(new[] { t.Outer }, split.ReadScopes);
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild();
            inner.Assume(dom.Of("k").Is(Set.Of("a", "b")), dom.Of("m").Is(Set.Of("c", "d", "e")));

            Assert.Equal(0, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);

            outer.Assume(t.Fact.Of("k"));
            Assert.Equal(2, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);

            outer.Assume(t.Fact.Of("m"));
            Assert.Equal(2 + 3, inner.Run(Schedule.Split(split, t.Rules)).CasesOpened);
            Assert.Empty(inner.Children);
        }

        /// <summary>
        /// <c>Run</c> refuses a split rule of another graph before any work, so the earlier steps write nothing and the
        /// scope is not faulted.
        /// </summary>
        [Fact]
        public void ARunRefusesASplitRuleOfAnotherGraphBeforeAnyWork()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var other = new PredicateTheory(new EGraph());
            var version = scope.Version;

            var refused = Assert.Throws<ArgumentException>(() => scope.Run(Schedule.Sequence(Settle(c), Schedule.Split(other.SchemaSplit, c.Horn))));

            Assert.Contains("belongs to another graph", refused.Message, StringComparison.Ordinal);
            Assert.Equal(version, scope.Version);
            Assert.False(scope.IsFaulted);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// Every tag a reachable split rule reads must resolve from the scope: <c>Run</c> checks it before any work, so
        /// the earlier steps write nothing and the scope is not faulted.
        /// </summary>
        [Fact]
        public void ARunRefusesASplitRuleWhoseTagDoesNotResolveBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var dom = t.Graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var split = ScopedSplit(t, dom);
            t.DefinePaths();
            var untagged = t.Root.CreateChild();
            untagged.Assume(t.Edge.Of(1L, 2L), dom.Of("k").Is(Set.Of("a", "b")));
            var version = untagged.Version;

            var refused = Assert.Throws<InvalidOperationException>(() => untagged.Run(Schedule.Sequence(t.Rules, Schedule.Split(split, t.Rules))));

            Assert.Contains("outer", refused.Message, StringComparison.Ordinal);
            Assert.Equal(version, untagged.Version);
            Assert.False(untagged.Check(t.Path.Of(1L, 2L)));
            Assert.False(untagged.IsFaulted);
        }

        /// <summary>
        /// A split matcher whose filter throws fails the run; the scope the run was called on is faulted with that
        /// cause, and the run's cases and split records are gone.
        /// </summary>
        [Fact]
        public void AFailingSplitMatcherFaultsTheScopeTheRunWasCalledOn()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            var failing = c.Graph.DefineSplit("failing", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
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
        /// While the run lasts, a split's cases are children of the split scope (<see cref="Scope.Children"/>), tagged
        /// with the split rule's case tag, and cases; they are disposed and leave the list when it ends.
        /// </summary>
        [Fact]
        public void ASplitsCasesAreTaggedChildrenOfTheSplitScopeWhileTheRunLasts()
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
        /// The definition errors of a split rule are <see cref="RuleDefinitionException"/>s naming it, and nothing is
        /// registered: no <c>Cases</c>, <c>Cases</c> twice, a set or a key declared by another builder, the case
        /// variable in the key, a filter among the assumptions, and a class of a child scope.
        /// </summary>
        [Fact]
        public void SplitDefinitionErrorsNameTheSplitRule()
        {
            var c = new PredicateTheory(new EGraph());
            var child = c.Graph.Root.CreateChild();
            var childClass = child.Make(c.PTrue.Of());
            Var<Set<string>>? stray = null;

            RuleDefinitionException Refused(string name, Action<SplitBuilder> define)
            {
                var refused = Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit(name, define));
                Assert.Equal(name, refused.RuleName);
                return refused;
            }

            Refused("stray source", s => stray = s.Var<Set<string>>("d"));
            Refused("cases twice", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
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
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Key(p, v);
            });
            Refused("a foreign key", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Key(stray!);
            });
            var filter = Refused("a filter assumed", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(p.IsNot(v));
            });
            Assert.Contains("assumptions are writes", filter.Message, StringComparison.Ordinal);
            Refused("a class of a child", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Assume(c.WhereClause.Of(childClass));
            });

            Assert.Equal(new[] { c.SchemaSplit, c.QuerySplit }, c.Graph.SplitRules);
        }

        /// <summary>
        /// A split case whose contradiction stopped it answers <c>IsContradictory</c> and refuses reads with
        /// <see cref="ScopeContradictedException"/>; the lift leaves it out, so the scope learns only what the live
        /// case proves.
        /// </summary>
        [Fact]
        public void AStoppedSplitCaseAnswersOnlyWhatNeedsNoModel()
        {
            var (c, scope) = Discriminated("kind", "a", "b");
            scope.Assume(c.Present.Of("x"), c.Dom.Of("x").Is(Set.Of("p")));
            c.DeclareBranch(scope, "kind", "b", c.Dom.Of("x").Is(Set.Of("q")));
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
            Assert.Equal(Set.Of("a"), scope.Get(c.Dom.Of("kind")));
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// A run's split disposes only its own cases: a host-driven set open on the same scope keeps its case, which
        /// sees what the split lifted, and can still lift.
        /// </summary>
        [Fact]
        public void ARunsSplitLeavesAnOpenHostCaseSetAlone()
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
        /// A split keyed on a class: the matcher's class cells are canonical in the split scope, and each case's
        /// assumption over the class is written into the case.
        /// </summary>
        [Fact]
        public void ASplitKeyedOnAClassAssumesOverThatClass()
        {
            var t = new NodeTheory();
            var labels = t.Graph.DefineFunction<Node, Set<string>>("labels", ValueLattice.Intersect<string>());
            var split = t.Graph.DefineSplit("by label", s =>
            {
                var (n, d) = s.Vars<Node, Set<string>>("n", "d");
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
                    r.When(labels.Of(n).Is(Set.Of(label)));
                    r.Then(t.Num.Of(1L));
                });
            }

            var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            scope.Assume(t.Tag.Of(a), t.Tag.Of(b), labels.Of(a).Is(Set.Of("x", "y")));
            scope.Union(a, b);

            var report = scope.Run(Schedule.Split(split, t.Rules));

            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.Check(t.Num.Of(1L)));
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// Two split nodes over one scope each run their in-case schedule only in the cases they opened, and each lifts
        /// what its own cases prove.
        /// </summary>
        [Fact]
        public void EachSplitNodeRunsItsInCaseScheduleOnlyInItsOwnCases()
        {
            var c = new PredicateTheory(new EGraph());
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
                c.Dom.Of("k1").Is(Set.Of("x", "y")), first.Of("k1"),
                c.Dom.Of("k2").Is(Set.Of("u", "v")), second.Of("k2"));
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
        /// A set of one element opens one case, whose assumptions are lifted: a split makes one case per element, and
        /// skips a request only when its set is empty.
        /// </summary>
        [Fact]
        public void AOneElementSetOpensOneCaseWhoseAssumptionsLift()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("one").Is(Set.Of("x")), t.Dom.Of("two").Is(Set.Of("y", "z")));

            var report = root.Run(Schedule.Split(t.Split, t.Rules));

            Assert.Equal(3, report.CasesOpened);
            Assert.Equal(0, report.SplitsSkipped);
            Assert.True(root.Check(t.Chosen.Of("one", "x")));             // the only case of one proves it
            Assert.False(root.Check(t.Chosen.Of("two", "y")));
            Assert.False(root.Check(t.Chosen.Of("two", "z")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// A split step brings current the mirrors its matcher reads: the matcher reads <c>present @schema</c> from
        /// below a writable scope tagged schema that gains the fact after the child last synced, and the run records
        /// the sync as a step.
        /// </summary>
        [Fact]
        public void ASplitMatcherReadsTheScopesLatestFactsThroughItsMirror()
        {
            var t = new ChooseSplitTheory(scoped: true);
            Assert.Equal(new[] { t.Schema }, t.Split.ReadScopes);
            t.Graph.Root.Freeze();
            var outer = t.Graph.Root.CreateChild(t.Schema);
            var inner = outer.CreateChild();
            inner.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));

            var before = inner.Run(Schedule.Split(t.Split, t.Rules));
            Assert.Equal(0, before.CasesOpened);

            outer.Assume(t.Present.Of("kind"));
            var after = inner.Run(Schedule.Split(t.Split, t.Rules), new RunOptions { RecordSteps = true });

            Assert.Equal(2, after.CasesOpened);
            Assert.Contains(after.Steps, s => s.Kind == RunStepKind.MirrorSync);
            Assert.Empty(inner.Children);
        }

        /// <summary>
        /// A lift step is marked changed only when the split scope's <see cref="Scope.Version"/> moved since the case
        /// iteration before it. A split step that finds nothing new still lifts its persistent cases, which the
        /// engine's lift memo answers at once, and those lifts change nothing.
        /// </summary>
        [Fact]
        public void ALiftThatChangesNothingIsAnUnchangedStep()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("a", "b")), c.Discriminator.Of("kind"));
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

            // A lift that follows a case's iteration is marked changed exactly when the split scope's version moved
            // since that iteration (the cases change only themselves).
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
        /// Cases are made in content order of their matches, whatever order the facts were written in: the split source
        /// sorts the matcher's rows by content rather than leaving them in the engine's scan order.
        /// </summary>
        [Fact]
        public void CasesAreMadeInContentOrderOfTheirKeys()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            string[] keys = ["z", "m", "a", "q"];
            foreach (var key in keys)
                root.Assume(t.Dom.Of(key).Is(Set.Of("y", "x")));

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
        /// A vacuous case that a split step's lift drops reports <see cref="Scope.IsDisposed"/> from then on, as an
        /// observer sees, although the run that made it goes on.
        /// </summary>
        [Fact]
        public void AVacuousCaseOfASplitIsDisposedOnceTheLiftDropsIt()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("a", "b")), c.Discriminator.Of("kind"));
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
            Assert.Equal(Set.Of("a"), scope.Get(c.Dom.Of("kind")));
        }

        /// <summary>
        /// The end of a run releases the cases of nested splits too: they report disposed, leave their parent case's
        /// children, and a class read from one is refused afterwards, its origin being disposed.
        /// </summary>
        [Fact]
        public void ANestedSplitsCasesAreReleasedWhenTheRunEnds()
        {
            var c = new PredicateTheory(new EGraph());
            var outer = c.Graph.DefineRelation<string>("outer discriminator");
            var inner = c.Graph.DefineRelation<string>("inner discriminator");
            var outerSplit = c.Graph.DefineDomainSplit("outer", c.Dom, [outer], c.Case);
            var innerSplit = c.Graph.DefineDomainSplit("inner", c.Dom, [inner], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Dom.Of("k1").Is(Set.Of("a", "b")), outer.Of("k1"), c.Dom.Of("k2").Is(Set.Of("c", "d")), inner.Of("k2"));
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
            Assert.False(scope.HasChildren);                            // every case released, not only marked disposed
            Assert.All(nested, n => Assert.True(n.IsDisposed));
            Assert.All(nested, n => Assert.Empty(n.Parent!.Children));
            Assert.NotNull(fromNested);
            var refused = Assert.Throws<ArgumentException>(() => scope.Find(fromNested.Value));
            Assert.Contains("which is disposed", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Limits and cancellation are checked before every split step as before every iteration: a row limit the scope
        /// already exceeds stops the run before the split opens a case, and a cancelled token throws.
        /// </summary>
        [Fact]
        public void LimitsAreCheckedBeforeASplitStep()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));

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
        /// A split's cases carry the split rule's case tag. The default path segment of an untagged case is
        /// <c>case</c>, so the split here has a tag of its own, and both the tag and the path are checked.
        /// </summary>
        [Fact]
        public void ASplitsCasesCarryItsCaseTag()
        {
            var t = new ChooseSplitTheory();
            var tag = t.Graph.DefineScopeTag("chosen case");
            var tagged = t.Graph.DefineSplit("choose tagged", s =>
            {
                var (p, d) = s.Vars<string, Set<string>>("p", "d");
                s.When(d.Is(t.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(t.Chosen.Of(p, v));
            }, tag);
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));
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
        /// <c>Run</c> refuses a split rule of another graph before any work with <see cref="ArgumentException"/>, like
        /// a ruleset of another graph: the schedule's first step never runs.
        /// </summary>
        [Fact]
        public void RunRefusesASplitRuleOfAnotherGraphBeforeAnyWork()
        {
            var t = new ChooseSplitTheory();
            var other = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));
            var seen = t.Graph.DefineRelation<string>("seen");
            t.Rules.DefineRule("see", r => { var p = r.Var<string>("p"); r.When(t.Dom.Of(p).Is(r.Var<Set<string>>("d"))); r.Then(seen.Of(p)); });

            Assert.Throws<ArgumentException>(() => root.Run(Schedule.Sequence(t.Rules, Schedule.Split(other.Split, t.Rules))));

            Assert.False(root.Check(seen.Of("kind")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// <c>Run</c> resolves, before any work, the tags a split rule's matcher reads. The split reads
        /// <c>present @schema</c>, which nothing up to the root carries: refused with
        /// <see cref="InvalidOperationException"/>, and the schedule's first step never runs.
        /// </summary>
        [Fact]
        public void RunRefusesASplitRuleWhoseTagDoesNotResolveBeforeAnyWork()
        {
            var t = new ChooseSplitTheory(scoped: true);
            var seen = t.Graph.DefineRelation<string>("seen");
            t.Rules.DefineRule("see", r => { var p = r.Var<string>("p"); r.When(t.Dom.Of(p).Is(r.Var<Set<string>>("d"))); r.Then(seen.Of(p)); });
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("a", "b")));

            Assert.Throws<InvalidOperationException>(() => root.Run(Schedule.Sequence(t.Rules, Schedule.Split(t.Split, t.Rules))));

            Assert.False(root.Check(seen.Of("kind")));
            Assert.False(root.IsFaulted);
        }

        /// <summary>
        /// <see cref="RunReport.LiftRowsSkipped"/> sums the rows the run's lifts skipped. Each case unions <c>a</c>
        /// with a different class and tags it, so its row stands for two parent-level candidates; with
        /// <see cref="LiftOptions.CandidateLimit"/> 1 the lift skips it, and the run reports it.
        /// </summary>
        [Fact]
        public void TheRowsASplitsLiftsSkipAreReported()
        {
            for (var limit = 1; limit <= 64; limit += 63)
            {
                var t = new NodeTheory();
                var dom = t.Graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
                var chosen = t.Graph.DefineRelation<string, string>("chosen");
                var split = t.Graph.DefineSplit("choose", s =>
                {
                    var (p, d) = s.Vars<string, Set<string>>("p", "d");
                    s.When(d.Is(dom.Of(p)));
                    var v = s.Cases("v", d);
                    s.Assume(chosen.Of(p, v));
                });
                var root = t.Root;
                var a = root.Make(t.A.Of());
                root.Make(t.B.Of());
                root.Make(t.Lit.Of(1L));
                root.Assume(dom.Of("k").Is(Set.Of("x", "y")));
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
        /// An <c>Until</c> node in a case its own rules stopped runs nothing and reads nothing: the step goes on with
        /// the other case, and the lift keeps only the live case's facts.
        /// </summary>
        [Fact]
        public void AnUntilInACaseThatStoppedRunsNothing()
        {
            var t = new ChooseSplitTheory();
            var root = t.Graph.Root;
            root.Assume(t.Dom.Of("kind").Is(Set.Of("x", "y")));
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
        /// Two classes a child unions are one key there, split once. A split step brings its scope up to date before
        /// the matcher runs, so the matcher reads canonical rows; the split source canonicalizes the matches' class
        /// columns again, and deduplicates and sorts the matches.
        /// </summary>
        [Fact]
        public void ClassesAChildUnionsAreSplitAsOneKey()
        {
            var t = new NodeTheory();
            var domain = t.Graph.DefineFunction<Node, Set<string>>("node dom", ValueLattice.Intersect<string>());
            var chosen = t.Graph.DefineRelation<Node, string>("node chosen");
            var split = t.Graph.DefineSplit("choose node", s =>
            {
                var (n, d) = s.Vars<Node, Set<string>>("n", "d");
                s.When(d.Is(domain.Of(n)));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(n, v));
            });
            var root = t.Root;
            var a = root.Make(t.A.Of());
            var b = root.Make(t.B.Of());
            root.Assume(domain.Of(a).Is(Set.Of("x", "y")), domain.Of(b).Is(Set.Of("x", "y")));
            root.Freeze();
            using var child = root.CreateChild();
            Assert.True(child.Union(a, b));

            var report = child.Run(Schedule.Split(split, t.Rules));

            Assert.Equal(2, report.CasesOpened);
            Assert.False(child.Check(chosen.Of(a, "x")));
            Assert.Empty(child.Children);
        }

        /// <summary>
        /// A write that conflicts on a function without a lattice during a run is a
        /// <see cref="RuleEvaluationException"/> and faults the scope. A split's assumption is written during the run,
        /// so its conflict is one too, and the run's cases and split records are gone.
        /// </summary>
        [Fact]
        public void AnAssumptionConflictDuringARunIsAnEvaluationFailure()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, string>("choice");
            var options = graph.DefineFunction<string, Set<string>>("options", ValueLattice.Intersect<string>());
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("by option", s =>
            {
                var (k, d) = s.Vars<string, Set<string>>("k", "d");
                s.When(d.Is(options.Of(k)));
                var v = s.Cases("v", d);
                s.Key(k);
                s.Assume(choice.Of(k).Is(v));
            });

            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(options.Of("x").Is(Set.Of("a", "b")), choice.Of("x").Is("a"));

            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, Schedule.Saturate(rules))));

            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.SplitRecordCount);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// A split run in a scope with an open host-driven set leaves the host's cases alone (the run releases only the
        /// cases its splits made); the host cases catch up with the split's lift, and the host's set still lifts what
        /// both of them hold.
        /// </summary>
        [Fact]
        public void ARunsSplitLeavesAnOpenHostSetAlone()
        {
            var graph = new EGraph();
            var c = new PredicateTheory(graph);
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("a", "b")), c.Discriminator.Of("kind"));
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));

            using var hosted = scope.OpenCases();
            var x = hosted.Add();
            var y = hosted.Add();
            x.Assume(c.Present.Of("x"), c.Present.Of("both"));
            y.Assume(c.Present.Of("y"), c.Present.Of("both"));

            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var report = scope.Run(Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle)));
            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Equal(2, scope.Children.Count);
            Assert.False(x.IsDisposed);
            Assert.False(y.IsDisposed);

            // The host cases caught up with the split's lift, and still lift what both of them hold.
            Assert.True(x.Check(c.Form.Of("ref", "uuid-lower")));
            var lifted = hosted.Lift();
            Assert.Equal(2, lifted.LiveCases);
            Assert.True(scope.Check(c.Present.Of("both")));
            Assert.False(scope.Check(c.Present.Of("x")));
        }

        /// <summary>
        /// <c>DefineDomainSplit</c> writes <c>domain(k) = {v}</c> in each case, which only a lattice merge can narrow;
        /// over a domain function without a lattice that assumption conflicts in every case of every split. Either the
        /// definition refuses such a function (<see cref="ArgumentException"/> or
        /// <see cref="RuleDefinitionException"/>), or the run's conflict is a write conflict during a run: a
        /// <see cref="RuleEvaluationException"/> that faults the scope.
        /// </summary>
        [Fact]
        public void ADomainSplitOverAFunctionWithoutALatticeIsRefusedOrFaults()
        {
            var graph = new EGraph();
            var key = graph.DefineRelation<string>("key");
            var domain = graph.DefineFunction<string, Set<string>>("domain");
            var rules = graph.DefineRuleset("rules");

            Exception? definition = Record.Exception(() => graph.DefineDomainSplit("by domain", domain, [key]));
            if (definition is not null)
            {
                Assert.True(definition is ArgumentException or RuleDefinitionException, definition.ToString());
                return;
            }

            var split = graph.SplitRules.Single();
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(key.Of("k"), domain.Of("k").Is(Set.Of("a", "b")));

            var failure = Record.Exception(() => scope.Run(Schedule.Split(split, Schedule.Saturate(rules))));

            Assert.Empty(scope.Children);
            Assert.IsType<RuleEvaluationException>(failure);
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// <see cref="RunReport.Changed"/> says an iteration, split step or lift of the run changed the top-level
        /// scope. A split whose cases prove nothing new opens and drops cases and leaves the scope as it was: its
        /// <see cref="Scope.Version"/> does not move, and the run reports no change.
        /// </summary>
        [Fact]
        public void ASplitThatLiftsNothingDoesNotChangeTheScope()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("a", "b")), c.Discriminator.Of("kind"));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            scope.Run(settle);
            var version = scope.Version;

            var report = scope.Run(Schedule.Split(c.SchemaSplit, settle));

            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(version, scope.Version);
            Assert.False(report.Changed);
        }

        /// <summary>
        /// As <see cref="ASplitTheDepthLimitStopsIsNotLookedFor"/>, with keys present in the schema: a nested split the
        /// depth limit cuts off is never looked for, so <see cref="RunReport.SplitsSkipped"/> does not count it.
        /// </summary>
        [Fact]
        public void ASplitCutOffByTheDepthLimitIsNotLookedFor()
        {
            var c = new PredicateTheory(new EGraph());
            var outerKey = c.Graph.DefineRelation<string>("outer key");
            var innerKey = c.Graph.DefineRelation<string>("inner key");
            var outer = c.Graph.DefineDomainSplit("outer", c.Dom, [outerKey], c.Case);
            var inner = c.Graph.DefineDomainSplit("inner", c.Dom, [innerKey], c.Case);
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(
                outerKey.Of("k0"), c.Present.Of("k0"), c.Dom.Of("k0").Is(Set.Of("a", "b")),
                innerKey.Of("k1"), c.Present.Of("k1"), c.Dom.Of("k1").Is(Set.Of("x", "y")));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var nested = Schedule.Split(outer, Schedule.Saturate(settle, Schedule.Split(inner, settle)));

            var deep = scope.Run(nested, new RunOptions { SplitDepthLimit = 2 });
            Assert.Equal(2 + 2 * 2, deep.CasesOpened);

            var shallow = scope.Run(nested, new RunOptions { SplitDepthLimit = 1 });
            Assert.Equal(2, shallow.CasesOpened);
            Assert.Equal(0, shallow.SplitsSkipped);
        }

        /// <summary>
        /// A writable root with a domain split on <c>k</c> over {a, b} and one ruleset: in each case <c>fact(x)</c> holds,
        /// and in the case <c>a</c> also <c>fact(a only)</c>; the <c>Max</c> function <c>counter(c)</c>, 0 at the root,
        /// counts up to 2 in the case <c>a</c>, and in the case <c>b</c> up to <paramref name="limitInB"/>, or without end
        /// when it is <c>null</c>.
        /// </summary>
        static (EGraph Graph, SplitRule Split, Ruleset Rules, Function<string, Set<string>> Dom, Function<string, long> Counter, Relation<string> Fact) CountingCases(long? limitInB)
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var discriminator = graph.DefineRelation<string>("discriminator");
            var counter = graph.DefineFunction<string, long>("counter", ValueLattice.Max<long>());
            var fact = graph.DefineRelation<string>("fact");
            var split = graph.DefineDomainSplit("k", dom, [discriminator]);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("count in a", r =>
            {
                var n = r.Var<long>("n");
                r.When(dom.Of("k").Is(Set.Of("a")), n.Is(counter.Of("c")));
                r.Where(n, n => n < 2);
                r.Then(counter.Of("c").Is(Primitives.Add.Of(n, 1L)));
            });
            rules.DefineRule("count in b", r =>
            {
                var n = r.Var<long>("n");
                r.When(dom.Of("k").Is(Set.Of("b")), n.Is(counter.Of("c")));
                r.Where(n, n => limitInB is null || n < limitInB);
                r.Then(counter.Of("c").Is(Primitives.Add.Of(n, 1L)));
            });
            rules.DefineRule("facts in a", r => { r.When(dom.Of("k").Is(Set.Of("a"))); r.Then(fact.Of("x"), fact.Of("a only")); });
            rules.DefineRule("fact in b", r => { r.When(dom.Of("k").Is(Set.Of("b"))); r.Then(fact.Of("x")); });
            graph.Root.Assume(discriminator.Of("k"), dom.Of("k").Is(Set.Of("a", "b")), counter.Of("c").Is(0L));
            return (graph, split, rules, dom, counter, fact);
        }

        /// <summary>
        /// <c>Repeat(n, rules)</c> as a split's in-case schedule bounds each case: the case <c>b</c>, which counts without
        /// end, is cut off after <c>n</c> passes and stays live, so the lift keeps only what both cases prove (not
        /// <c>fact(a only)</c>), the sibling's <c>fact(x)</c> reaches the root, the counter's case join (the lesser) gives
        /// the root 2, and the run completes well inside its iteration limit.
        /// </summary>
        [Fact]
        public void RepeatAsTheInCaseScheduleBoundsEachCaseAndTheSiblingsFactsLift()
        {
            var (graph, split, rules, _, counter, fact) = CountingCases(null);
            var root = graph.Root;

            var report = root.Run(Schedule.Saturate(rules, Schedule.Split(split, Schedule.Repeat(5, rules))), new RunOptions { IterationLimit = 200 });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Iterations < 200);
            Assert.Equal(2, report.CasesOpened);
            Assert.True(root.Check(fact.Of("x")));
            Assert.False(root.Check(fact.Of("a only")));
            Assert.Equal(2L, root.Get(counter.Of("c")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// The same run with <c>Saturate(rules)</c> as the in-case schedule: the case <c>b</c>, which counts far past
        /// the limit, uses up the run-wide <see cref="RunOptions.IterationLimit"/> before the split step lifts, so the
        /// run stops on the limit and the root gets nothing from either case, not even the facts the case <c>a</c>
        /// saturated on. The count in <c>b</c> is finite so that the test fails, rather than hangs, if case iterations
        /// stop counting towards the limit.
        /// </summary>
        [Fact]
        public void SaturateAsTheInCaseScheduleLetsOneCaseUseUpTheIterationLimitBeforeTheLift()
        {
            var (graph, split, rules, _, counter, fact) = CountingCases(5_000);
            var root = graph.Root;

            var report = root.Run(Schedule.Saturate(rules, Schedule.Split(split, Schedule.Saturate(rules))), new RunOptions { IterationLimit = 200 });

            Assert.Equal(RunStopReason.IterationLimit, report.StopReason);
            Assert.Equal(200, report.Iterations);
            Assert.False(root.Check(fact.Of("x")));
            Assert.Equal(0L, root.Get(counter.Of("c")));
            Assert.Empty(root.Children);
        }

        /// <summary>
        /// The bound of <c>Repeat(n, rules)</c> applies to each execution of the in-case schedule, not to the case over
        /// the run: the runaway case runs <c>n</c> iterations in each of the four executions of this run (before the
        /// lift and again when the step deepens, at each of the split step's two executions), and its counter rises by
        /// <c>n</c> in each.
        /// </summary>
        /// <param name="n">The passes of the in-case schedule.</param>
        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        public void TheBoundOfRepeatAppliesToEachExecutionOfTheInCaseSchedule(int n)
        {
            var (graph, split, rules, dom, counter, _) = CountingCases(null);
            var inB = new List<(int Index, long Counter)>();
            void Observe(RunStep step)
            {
                if (step.Kind == RunStepKind.Iteration && step.Scope.IsCase && step.Scope.Get(dom.Of("k")).Equals(Set.Of("b")))
                    inB.Add((step.Index, step.Scope.Get(counter.Of("c"))));
            }

            var report = graph.Root.Run(
                Schedule.Saturate(rules, Schedule.Split(split, Schedule.Repeat(n, rules))),
                new RunOptions { IterationLimit = 200, RecordSteps = true, Observer = Observe });

            // An execution is a run of consecutive steps in the case b: the steps of the case a, the lifts and the split
            // steps come between them.
            var executions = new List<int> { 1 };
            for (var i = 1; i < inB.Count; i++)
            {
                if (inB[i].Index == inB[i - 1].Index + 1)
                    executions[^1]++;
                else
                    executions.Add(1);
            }

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Equal(2, report.Steps.Count(s => s.Kind == RunStepKind.SplitStep && s.Scope.IsRoot));
            Assert.Equal(new[] { n, n, n, n }, executions);
            Assert.Equal(4L * n, inB[^1].Counter);
        }

        /// <summary>
        /// A single split step with <c>Repeat(3, rules)</c> as the in-case schedule: the case <c>a</c> derives
        /// <c>fact(y)</c> at once, the case <c>b</c> only once its counter reaches 4, which it does in the execution of
        /// the in-case schedule when the step deepens. The step's first lift leaves <c>fact(y)</c> out, and the lift
        /// after deepening brings it to the root.
        /// </summary>
        [Fact]
        public void TheLiftAfterTheStepDeepensTakesWhatACutOffCaseDerivedWhileDeepening()
        {
            var (graph, split, rules, dom, counter, fact) = CountingCases(null);
            rules.DefineRule("y in a", r => { r.When(dom.Of("k").Is(Set.Of("a"))); r.Then(fact.Of("y")); });
            rules.DefineRule("y in b from 4", r =>
            {
                var n = r.Var<long>("n");
                r.When(dom.Of("k").Is(Set.Of("b")), n.Is(counter.Of("c")));
                r.Where(n, n => n >= 4);
                r.Then(fact.Of("y"));
            });
            var root = graph.Root;

            var report = root.Run(Schedule.Split(split, Schedule.Repeat(3, rules)));

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(2, report.Lifts);
            Assert.True(root.Check(fact.Of("x")));
            Assert.True(root.Check(fact.Of("y")));
            Assert.False(root.Check(fact.Of("a only")));
        }

        /// <summary>
        /// On a theory whose cases terminate, <c>Repeat</c> of a large <c>n</c> as the in-case schedule lifts what
        /// <c>Saturate</c> lifts, in as many iterations and lifts: the bound changes nothing when no case reaches it.
        /// </summary>
        [Fact]
        public void RepeatOfALargeBoundLiftsWhatSaturateLiftsWhenTheCasesTerminate()
        {
            var lifted = new List<string[]>();
            var counts = new List<(int Iterations, int Lifts)>();
            foreach (var bounded in new[] { false, true })
            {
                var (graph, split, rules, dom, counter, fact) = CountingCases(3);
                var inCase = bounded ? Schedule.Repeat(1000, rules) : Schedule.Saturate(rules);
                var report = graph.Root.Run(Schedule.Saturate(rules, Schedule.Split(split, inCase)));

                Assert.Equal(RunStopReason.Completed, report.StopReason);
                Assert.Equal(2, report.CasesOpened);
                counts.Add((report.Iterations, report.Lifts));
                lifted.Add(new TableHandle[] { dom, counter, fact }
                    .SelectMany(table => graph.Root.Rows(table))
                    .Select(row => row.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            }

            Assert.Equal(lifted[0], lifted[1]);
            Assert.Equal(counts[0], counts[1]);
            Assert.Contains("(counter \"c\") = 2", lifted[1]);
            Assert.Contains("(fact \"x\")", lifted[1]);
            Assert.DoesNotContain("(fact \"a only\")", lifted[1]);
        }

        /// <summary>
        /// A split's cases each assume a constant vector of classes from the root, which the root's view holds only when
        /// <paramref name="rootHolds"/>; a case-local rule unions the two classes in case 1, whose container pass
        /// rebuilds the vector. Each case is checked against egglog's binary running it as a push with its assumptions
        /// written as sets, and the lift against what checks in every push (probe <c>split-case-vector.egg</c>, which
        /// exits 0). The split runs at a writable root, in a child of a frozen root, or in a child of a writable root
        /// (<paramref name="where"/>).
        /// </summary>
        [Theory]
        [InlineData("writable root", false)]
        [InlineData("writable root", true)]
        [InlineData("frozen root", false)]
        [InlineData("frozen root", true)]
        [InlineData("child", false)]
        [InlineData("child", true)]
        public void ACaseAssumesAConstantVectorOfClassesAsEgglogsPushDoes(string where, bool rootHolds) =>
            AssumeAConstantContainerInEachCase<Vec<Node>>(where, rootHolds, graph =>
            {
                graph.Values.RegisterVec<Node>();
                return ((one, two) => Vec.Of(one, two), one => Vec.Of(one, one));
            });

        /// <summary>
        /// <see cref="ACaseAssumesAConstantVectorOfClassesAsEgglogsPushDoes"/> for a set of classes, which case 1's
        /// rebuild collapses to one element (probe <c>split-case-set.egg</c>, which exits 0).
        /// </summary>
        [Theory]
        [InlineData("writable root", false)]
        [InlineData("writable root", true)]
        [InlineData("frozen root", false)]
        [InlineData("frozen root", true)]
        [InlineData("child", false)]
        [InlineData("child", true)]
        public void ACaseAssumesAConstantSetOfClassesAsEgglogsPushDoes(string where, bool rootHolds) =>
            AssumeAConstantContainerInEachCase<Set<Node>>(where, rootHolds, graph =>
            {
                graph.Values.RegisterSet<Node>();
                return ((one, two) => Set.Of(one, two), one => Set.Of(one));
            });

        /// <summary>
        /// <see cref="ACaseAssumesAConstantVectorOfClassesAsEgglogsPushDoes"/> for a function value partially applied to
        /// a class, <c>(unstable-fn "Add" (Num 2))</c>, which case 1's rebuild makes <c>(unstable-fn "Add" (Num 1))</c>
        /// (probe <c>split-case-fn.egg</c>, which exits 0).
        /// </summary>
        [Theory]
        [InlineData("writable root", false)]
        [InlineData("writable root", true)]
        [InlineData("frozen root", false)]
        [InlineData("frozen root", true)]
        [InlineData("child", false)]
        [InlineData("child", true)]
        public void ACaseAssumesAConstantFunctionValueOfAClassAsEgglogsPushDoes(string where, bool rootHolds) =>
            AssumeAConstantContainerInEachCase<UnstableFn<Node, Node>>(where, rootHolds, graph =>
            {
                graph.Values.RegisterFn<Node, Node>();
                var add = graph.DefineConstructor<Node, Node, Node>("Add");
                return ((one, two) => UnstableFn.Of<Node, Node>(add, two), one => UnstableFn.Of<Node, Node>(add, one));
            });

        /// <summary>
        /// The program of the probes <c>split-case-vector.egg</c>, <c>split-case-set.egg</c> and <c>split-case-fn.egg</c>
        /// for one container type <typeparamref name="T"/>. The root makes <c>(Num 1)</c> and <c>(Num 2)</c> and holds
        /// <c>held("one")</c>, the container of the first class alone (<c>Same</c>), and, when
        /// <paramref name="rootHolds"/>, <c>held("both")</c>, the container of both (<c>Original</c>). Each case of a
        /// split over <c>choice("k") = {1, 2}</c> assumes <c>choice("k") = {v}</c>, <c>g("k") = Original</c> and
        /// <c>h("k") = Same</c> as constants, and an in-case rule unions the two classes in case 1. In case 1, then,
        /// <c>g("k")</c> is rebuilt to <c>Same</c> and merges with the root's <c>held("one")</c>; in case 2 it is a
        /// container the case interned in its own layers, or the root's <c>held("both")</c>. The lift keeps what checks in
        /// every push, <c>h("k") = held("one")</c> and, when the root holds it, <c>g("k") = held("both")</c>; without it
        /// the lift drops <c>g("k")</c>, whose value in case 2 is local to the case, where egglog's every push checks
        /// <c>g("k") = Original</c> (decisions row 9). Nothing a case interned reaches the root's environment or the split
        /// scope's layers.
        /// </summary>
        static void AssumeAConstantContainerInEachCase<T>(string where, bool rootHolds,
            Func<EGraph, (Func<Node, Node, T> Original, Func<Node, T> Same)> define) where T : notnull
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var (original, same) = define(graph);
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var g = graph.DefineFunction<string, T>("g");
            var h = graph.DefineFunction<string, T>("h");
            var held = graph.DefineFunction<string, T>("held");
            var inCase = graph.DefineRuleset("in-case");
            inCase.DefineRule("union in case 1", r =>
            {
                r.When(choice.Of("k").Is(Set.Of(1L)));
                r.Then(num.Of(1L).Is(num.Of(2L)));
            });

            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Assume(held.Of("one").Is(same(one)));
            if (rootHolds)
                root.Assume(held.Of("both").Is(original(one, two)));

            var split = graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), g.Of(k).Is(original(one, two)), h.Of(k).Is(same(one)));
            });

            if (where == "frozen root")
                root.Freeze();

            using var child = where == "writable root" ? null : root.CreateChild();
            var scope = child ?? root;
            scope.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));
            var type = graph.Values.TypeOf<T>();

            // Each case's state at its last step: what egglog's push checks after (run-schedule (saturate in-case)).
            var states = new Dictionary<long, bool[]>();
            var report = scope.Run(Schedule.Split(split, Schedule.Saturate(inCase)), new RunOptions
            {
                Observer = step =>
                {
                    var inside = step.Scope;
                    if (inside.IsCase == false)
                        return;

                    var v = Assert.Single(inside.Get(choice.Of("k")));
                    states[v] = new[]
                    {
                        inside.Check(num.Of(1L).Is(num.Of(2L))),
                        inside.Check(g.Of("k").Is(same(one))),
                        inside.Check(g.Of("k").Is(held.Of("one"))),
                        inside.Check(g.Of("k").Is(original(one, two))),
                        inside.Check(h.Of("k").Is(held.Of("one"))),
                        rootHolds && inside.Check(g.Of("k").Is(held.Of("both"))),
                        inside.Engine.Context.Context.ContainerLayers!.IsEmpty,
                    };
                },
            });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Equal(2, report.CasesOpened);
            Assert.False(scope.IsFaulted);

            // Case 1: (check (= (Num 1) (Num 2))), (check (= (g "k") Same)), (check (= (g "k") (held "one"))),
            // (check (= (g "k") Original)), (check (= (h "k") (held "one"))), and (check (= (g "k") (held "both"))) when
            // the root holds it; its union made the case claim and rebuild containers in its layers.
            Assert.Equal(new[] { true, true, true, true, true, rootHolds, false }, states[1]);

            // Case 2: the union, (= (g "k") Same) and (= (g "k") (held "one")) fail; (g "k") is Original, interned in the
            // case's own layers unless the root holds it.
            Assert.Equal(new[] { false, false, false, true, true, rootHolds, rootHolds }, states[2]);

            // The lift: what checks in every push, but for a row naming a container a case interned.
            Assert.Empty(scope.Children);
            Assert.True(scope.Check(h.Of("k").Is(held.Of("one"))));
            Assert.True(scope.Check(h.Of("k").Is(same(one))));
            Assert.False(scope.Check(num.Of(1L).Is(num.Of(2L))));
            if (rootHolds)
            {
                Assert.True(scope.Check(g.Of("k").Is(held.Of("both"))));
                Assert.True(scope.Check(g.Of("k").Is(original(one, two))));
                Assert.False(scope.Check(held.Of("one").Is(held.Of("both"))));
            }
            else
            {
                Assert.False(scope.TryGet(g.Of("k"), out _));
            }

            // Nothing case-local leaked: the split scope's view and the root's hold Original only when the root made it,
            // the split scope's layers are empty and, below the root, the root has no row of the cases'.
            Assert.Equal(rootHolds, type.TryEncodeExistingObject(original(one, two), root.ContainerView, out _));
            Assert.Equal(rootHolds, type.TryEncodeExistingObject(original(one, two), scope.ContainerView, out _));
            if (child is not null)
            {
                Assert.True(child.Engine.Context.Context.ContainerLayers!.IsEmpty);
                Assert.False(root.TryGet(h.Of("k"), out _));
                Assert.False(root.TryGet(g.Of("k"), out _));
            }

            scope.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// A split's constant map whose keys are vectors of the root's classes <c>(Num 1)</c> and <c>(Num 2)</c> is accepted
        /// at the definition, which reads the classes as they were written; once the root has unioned the two, before the
        /// definition or after it, each case's host batch refuses the map, whose two keys are one now, as the root refuses
        /// the same host write (architecture 2.2): the run fails, naming the case, and faults the scope it was called on,
        /// as every case whose assumptions cannot be written does. egglog has no constant map to compare with, so the
        /// expectation is the root's own refusal of that write.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACasesConstantMapWhoseKeysAreOneNowIsRefusedWhenTheCaseRuns(bool unionFirst)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterMap<Vec<Node>, long>();
            var num = graph.DefineConstructor<long, Node>("Num");
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var g = graph.DefineFunction<string, Map<Vec<Node>, long>>("g");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            var map = Map.Of((Vec.Of(one), 1L), (Vec.Of(two), 2L));
            if (unionFirst)
                root.Union(one, two);

            var split = graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), g.Of(k).Is(map));
            });

            if (unionFirst == false)
                root.Union(one, two);

            root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));
            var refused = Assert.Throws<ArgumentException>(() => root.Assume(g.Of("host").Is(map)));
            Assert.Contains("hold classes that are one now", refused.Message, StringComparison.Ordinal);

            var failure = Assert.Throws<RuleEvaluationException>(() => root.Run(Schedule.Split(split, Schedule.Saturate(graph.DefineRuleset("in-case")))));
            Assert.Contains("case 0 of 'root' assumes", failure.Message, StringComparison.Ordinal);
            Assert.Contains("hold classes that are one now", failure.InnerException?.Message, StringComparison.Ordinal);
            Assert.True(root.IsFaulted);
            Assert.Same(failure, root.Failure);
        }

    }

}
