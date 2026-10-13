using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos.Scoped;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// Split steps (§6.9.2, §6.9.3): the cases a split rule opens, how long they live, the limits, and nesting. The theory
    /// is the scoped Cosmos theory: <c>dom</c> is a discriminator's domain, and branch rules local to the split scope are
    /// what each case proves.
    /// </summary>
    public sealed class SplitTests
    {

        /// <summary>
        /// A graph with the theory, an empty frozen root, and a writable scope tagged schema beneath it that declares
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
                    r.When(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of(value)), c.Present.Of("late"));
                    r.Then(c.Form.Of("ref", "uuid-lower"));
                });
            }

            // The split comes before the rule that writes late: its cases learn late from the scope in the next pass, and
            // the step lifts what they derive from it then, without splitting kind again (B6).
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

            // The second pass of each run finds the key covered by the run's own live split (B6); the next run does not.
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
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(c.Dom.Of(p).Is(Primitives.Singleton<string>().Of(v)));
            });

            // dom(empty) = {} with no presence: no contradiction, and nothing to split.
            scope.Assume(c.Dom.Of("empty").Is(ValueSet<string>.Empty));
            var report = scope.Run(Schedule.Split(unfiltered, Settle(c)));

            Assert.Equal(2, report.CasesOpened);                    // kind's two values
            Assert.Equal(1, report.SplitsSkipped);                  // empty's no value
            Assert.Empty(scope.Children);
        }

        [Fact]
        public void AGuardThatFailsOpensNoCase()
        {
            var (c, scope) = Discriminated("kind", "a", "b");

            // QuerySplit is guarded by mentioned(kind), which no term here derives (D43).
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
            var results = new List<(RunReport Report, bool Form, ValueSet<string> Dom)>();
            for (var handWritten = 0; handWritten < 2; handWritten++)
            {
                var (c, scope) = Discriminated("kind", "a", "b", "c");
                c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(ValueSet.Of("x")));
                c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(ValueSet.Of("y")));
                c.DeclareBranch(scope, "kind", "c", c.Form.Of("ref", "uuid-lower"), c.Dom.Of("status").Is(ValueSet.Of("x", "z")));
                var rule = handWritten == 0 ? c.SchemaSplit : c.Graph.DefineSplit("by hand", s =>
                {
                    var (k, d) = s.Vars<string, ValueSet<string>>("k", "d");
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
            Assert.Equal(ValueSet.Of("x", "y", "z"), results[0].Dom);
            Assert.Equal(results[0].Dom, results[1].Dom);
        }

        [Fact]
        public void NestedSplitsLiftBeforeTheyDeepen()
        {
            for (var depthLimit = 1; depthLimit <= 2; depthLimit++)
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
                foreach (var v1 in new[] { "a", "b" })
                {
                    foreach (var v2 in new[] { "c", "d" })
                    {
                        // Only a pair of values proves the fact: a split on one path alone proves nothing.
                        scope.DefineRule(c.Horn, $"{v1} and {v2}", r =>
                        {
                            r.When(c.Dom.Of("k1").Is(ValueSet.Of(v1)), c.Dom.Of("k2").Is(ValueSet.Of(v2)));
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
                var d = r.Var<ValueSet<string>>("d");
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

            // The split's engine set owns its cases: a Dispose marks the case disposed at once, and the driver still runs
            // and lifts it; its release waits for the run's end (N96).
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
            var c = new CosmosTheory(new EGraph());
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
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.When(c.Ne.Of(p, v));
            }));
            Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("a read in an assumption", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(c.Present.Of(p), c.Ne.Of(p, v), c.Types.Of(p).Is(c.Dom.Of(p)));
            }));
            Assert.Throws<RuleDefinitionException>(() => c.Graph.DefineSplit("a scoped assumption", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(c.Dom.Of(p)));
                s.Cases("v", d);
                s.Assume(c.Present.In(c.Schema).Of(p));
            }));
            Assert.Equal(2, c.Graph.SplitRules.Count);
        }

        /// <summary>
        /// N105: a domain split writes its domain in each case, so the domain must be writable (not a scoped view) and have a
        /// lattice that narrows it; a guard is only read, and may be a scoped view, as in <c>DefineSplit</c>'s body.
        /// </summary>
        [Fact]
        public void ADomainSplitNeedsAWritableDomainWithALatticeAndReadsItsGuardsAnywhere()
        {
            var c = new CosmosTheory(new EGraph());
            var plain = c.Graph.DefineFunction<string, ValueSet<string>>("plain domain");

            var noLattice = Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("no lattice", plain, [c.Present]));
            Assert.Contains("has no lattice", noLattice.Message, StringComparison.Ordinal);
            var scopedDomain = Assert.Throws<ArgumentException>(() => c.Graph.DefineDomainSplit("scoped domain", c.Dom.In(c.Schema), [c.Present]));
            Assert.Contains("scoped view", scopedDomain.Message, StringComparison.Ordinal);

            var guarded = c.Graph.DefineDomainSplit("guarded in the schema", c.Dom, [c.Present.In(c.Schema)], c.Case);
            Assert.Equal(new[] { c.Schema }, guarded.ReadScopes);
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")));
            var query = schema.CreateChild(c.Query);
            var report = query.Run(Schedule.Split(guarded, Settle(c)));
            Assert.Equal(2, report.CasesOpened);
            Assert.Empty(query.Children);
        }

    }

}
