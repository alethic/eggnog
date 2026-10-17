using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// The rules of an iteration below a scope with local rules: the ruleset's global rules, then its local rules from
    /// the root down. The scope keeps the list it built for each ruleset, the children without local rules of their own
    /// share it and its lowered variants, also when their schedule alternates rulesets, and a rule defined since, in the
    /// ruleset or on the path, makes it build the list again. A disposed scope keeps serving its list to its live
    /// descendants, and its local rules' variants are freed only when it is released. A run's statistics sum every
    /// report of a variant and list every rule that ran.
    /// </summary>
    public sealed class IterationRulesTests
    {

        /// <summary>
        /// A frozen root and a container with the local rule <c>fact(x) → seen(x)</c>.
        /// </summary>
        static (ScopeTheory T, Scope Container, Rule Local) Container()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();
            var container = t.Root.CreateChild(t.Outer);
            var local = container.DefineRule(t.Rules, "fact is seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            return (t, container, local);
        }

        /// <summary>
        /// Defines on <paramref name="scope"/> the local rule <c>seen(x) → best(x) = value</c> of <paramref name="ruleset"/>.
        /// </summary>
        static Rule DefineBest(ScopeTheory t, Scope scope, Ruleset ruleset, string name, long value) =>
            scope.DefineRule(ruleset, name, r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(value));
            });

        [Fact]
        public void ChildrenOfAFrozenContainerShareOneList()
        {
            var (t, container, local) = Container();
            container.Freeze();
            using var a = container.CreateChild();
            using var b = container.CreateChild();

            var inA = ScheduleExecutor.RulesFor([t.Rules], a);
            var inB = ScheduleExecutor.RulesFor([t.Rules], b);

            Assert.Same(inA, inB);
            Assert.Same(inA, container.CachedIterationRules(t.Rules));
            Assert.Equal([local], inA.Select(r => r.Rule));
        }

        [Fact]
        public void ChildrenOfAFrozenContainerShareTheLoweredVariants()
        {
            var (t, container, _) = Container();
            container.Freeze();
            using var a = container.CreateChild();
            using var b = container.CreateChild();

            var (inA, idsA) = ((IterationRules)ScheduleExecutor.RulesFor([t.Rules], a)).Lower(a, true);
            var (inB, idsB) = ((IterationRules)ScheduleExecutor.RulesFor([t.Rules], b)).Lower(b, true);

            Assert.Same(inA, inB);
            Assert.Same(idsA, idsB);
        }

        [Fact]
        public void EachSemiNaiveSettingGetsItsOwnVariants()
        {
            var (t, container, _) = Container();
            container.Freeze();
            using var query = container.CreateChild();
            var rules = (IterationRules)ScheduleExecutor.RulesFor([t.Rules], query);

            var (naive, _) = rules.Lower(query, false);
            var (semiNaive, _) = rules.Lower(query, true);

            Assert.All(naive, v => Assert.False(v.SemiNaive));
            Assert.Contains(semiNaive, v => v.SemiNaive);
        }

        /// <summary>
        /// A schedule that alternates rulesets, as <c>Saturate(Horn, Lattice, Rewrite)</c> does, asks for one list per
        /// ruleset at every switch: the container keeps one for each, a ruleset without local rules on the path gets
        /// the global snapshot, and the next query below the container builds none of them again.
        /// </summary>
        [Fact]
        public void AnAlternatingScheduleKeepsOneListPerRuleset()
        {
            var (t, container, local) = Container();
            var other = t.Graph.DefineRuleset("other");
            var plain = t.Graph.DefineRuleset("plain");
            var global = plain.DefineRule("fact is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });
            var otherLocal = DefineBest(t, container, other, "seen is best", 3);
            container.Freeze();

            using (var first = container.CreateChild())
            {
                first.Assume(t.Fact.Of("a"));
                first.Run(Schedule.Saturate(t.Rules, other, plain));
                Assert.True(first.Check(t.Best.Of("a").Is(3L)));
            }

            var rules = container.CachedIterationRules(t.Rules);
            var others = container.CachedIterationRules(other);
            var plains = container.CachedIterationRules(plain);
            Assert.Equal([local], rules!.Select(r => r.Rule));
            Assert.Equal([otherLocal], others!.Select(r => r.Rule));
            Assert.Equal([global], plains!.Select(r => r.Rule));

            using var second = container.CreateChild();
            second.Assume(t.Fact.Of("b"));
            second.Run(Schedule.Saturate(t.Rules, other, plain));
            Assert.True(second.Check(t.Best.Of("b").Is(3L)));
            Assert.Same(rules, ScheduleExecutor.RulesFor([t.Rules], second));
            Assert.Same(others, ScheduleExecutor.RulesFor([other], second));
            Assert.Same(plains, ScheduleExecutor.RulesFor([plain], second));
        }

        /// <summary>
        /// Rebuilding the list of one ruleset, after a rule is defined in it, keeps the list of the other.
        /// </summary>
        [Fact]
        public void RebuildingOneRulesetsListKeepsTheOthers()
        {
            var (t, container, _) = Container();
            var other = t.Graph.DefineRuleset("other");
            DefineBest(t, container, other, "seen is best", 3);
            container.Freeze();
            using var query = container.CreateChild();
            var rules = ScheduleExecutor.RulesFor([t.Rules], query);
            var others = ScheduleExecutor.RulesFor([other], query);

            t.Rules.DefineRule("fact is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });

            var rebuilt = ScheduleExecutor.RulesFor([t.Rules], query);
            Assert.NotSame(rules, rebuilt);
            Assert.Equal(2, rebuilt.Count);
            Assert.Same(rebuilt, container.CachedIterationRules(t.Rules));
            Assert.Same(others, ScheduleExecutor.RulesFor([other], query));
        }

        /// <summary>
        /// A run checks the tags of every rule its schedule reaches through the lists its iterations use, one ruleset
        /// at a time, not through a list merged over all of them: a run over two rulesets that stops before its first
        /// iteration leaves the container a list for each.
        /// </summary>
        [Fact]
        public void ARunChecksTagsThroughTheKeptLists()
        {
            var (t, container, local) = Container();
            var other = t.Graph.DefineRuleset("other");
            var otherLocal = DefineBest(t, container, other, "seen is best", 3);
            container.Freeze();
            using var query = container.CreateChild();
            var none = new RunOptions { IterationLimit = 0 };

            var report = query.Run(Schedule.Saturate(t.Rules, other), none);

            Assert.Equal(0, report.Iterations);
            var rules = container.CachedIterationRules(t.Rules);
            var others = container.CachedIterationRules(other);
            Assert.Equal([local], rules!.Select(r => r.Rule));
            Assert.Equal([otherLocal], others!.Select(r => r.Rule));
        }

        /// <summary>
        /// Under the class rule a disposed scope stays usable from its live descendants until they are released: a
        /// disposed container keeps serving its list and its lowered variants to a live child, and the variants of its
        /// local rule are freed, and its lists dropped, only when the last child's release releases it.
        /// </summary>
        [Fact]
        public void ADisposedAnchorServesItsLiveDescendantsUntilItIsReleased()
        {
            var (t, container, _) = Container();
            container.Freeze();
            var child = container.CreateChild();
            child.Assume(t.Fact.Of("a"));
            child.Run(Schedule.Saturate(t.Rules));
            var rules = (IterationRules)ScheduleExecutor.RulesFor([t.Rules], child);
            var (lowered, _) = rules.Lower(child, true);
            var variants = container.LocalCompiled[0].Variants;
            Assert.NotNull(variants.Default);

            container.Dispose();
            Assert.False(container.IsReleased);
            child.Assume(t.Fact.Of("b"));
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Seen.Of("b")));
            Assert.Same(rules, ScheduleExecutor.RulesFor([t.Rules], child));
            Assert.Same(lowered, rules.Lower(child, true).Lowered);
            Assert.Same(lowered[0], variants.Default);

            child.Dispose();
            Assert.True(container.IsReleased);
            Assert.Null(variants.Default);
            Assert.Empty(variants.All);
            Assert.Null(container.CachedIterationRules(t.Rules));
        }

        [Fact]
        public void TheLocalRulesComeFromTheRootDown()
        {
            var (t, outer, upper) = Container();
            using var anchor = outer.CreateChild();
            var lower = anchor.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });
            using var query = anchor.CreateChild();

            Assert.Equal([upper, lower], ScheduleExecutor.RulesFor([t.Rules], query).Select(r => r.Rule));
        }

        /// <summary>
        /// The step rule's two semi-naive variants share a description, and both run when <c>path</c> and <c>edge</c>
        /// have new rows: its matches are the sum of their reports, not the last one's.
        /// </summary>
        [Fact]
        public void AVariantsMatchesAreSummedOverItsReports()
        {
            var t = new ScopeTheory();
            var step = t.DefinePaths();
            t.Root.Assume(t.Edge.Of(7L, 8L));
            t.Root.Run(Schedule.Saturate(t.Rules));
            t.Root.Assume(t.Path.Of(1L, 2L));
            t.Root.Assume(t.Edge.Of(2L, 3L));

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(t.Path.Of(1L, 3L)));
            Assert.Equal(1, report.Rules[step].Matches);
        }

        [Fact]
        public void AnIdleQueryReportsEveryRuleThatRan()
        {
            var (t, container, local) = Container();
            var global = t.Rules.DefineRule("best of seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });
            container.Freeze();

            for (var i = 0; i < 2; i++)
            {
                using var query = container.CreateChild();
                query.Assume(t.Edge.Of(1L, 2L));
                var report = query.Run(Schedule.Saturate(t.Rules));

                Assert.Equal(1, report.Iterations);
                Assert.Equal(2, report.Rules.Count);
                Assert.Equal(0, report.Rules[local].Matches);
                Assert.Equal(0, report.Rules[global].Matches);
                Assert.Equal(0, report.Rules[local].IterationsWithMatches);
            }
        }

        [Fact]
        public void AGlobalRuleDefinedAfterTheFreezeJoinsTheNextQuery()
        {
            var (t, container, _) = Container();
            container.Freeze();
            using (var first = container.CreateChild())
            {
                first.Assume(t.Fact.Of("a"));
                first.Run(Schedule.Saturate(t.Rules));
                Assert.True(first.Check(t.Seen.Of("a")));
                Assert.False(first.Check(t.Best.Of("a").Is(1L)));
            }

            t.Rules.DefineRule("best of seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });

            using var second = container.CreateChild();
            second.Assume(t.Fact.Of("a"));
            second.Run(Schedule.Saturate(t.Rules));
            Assert.True(second.Check(t.Best.Of("a").Is(1L)));
        }

        [Fact]
        public void ALocalRuleDefinedOnTheAnchorJoinsTheNextIteration()
        {
            var (t, container, _) = Container();
            using var child = container.CreateChild();
            child.Assume(t.Fact.Of("a"));
            child.Run(Schedule.Saturate(t.Rules));
            var before = container.CachedIterationRules(t.Rules);

            container.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Best.Of("a").Is(2L)));
            Assert.NotSame(before, container.CachedIterationRules(t.Rules));
        }

        /// <summary>
        /// The anchor is the nearest scope with local rules, so its list also holds the local rules of the scopes above
        /// it: one defined there after the anchor built its list joins the next query below the anchor. <c>best</c> is a
        /// max lattice, so the outer rule's value wins only if it ran.
        /// </summary>
        [Fact]
        public void ALocalRuleDefinedAboveTheAnchorJoinsTheNextQuery()
        {
            var (t, outer, _) = Container();
            using var anchor = outer.CreateChild();
            anchor.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });

            using (var first = anchor.CreateChild())
            {
                first.Assume(t.Fact.Of("a"));
                first.Run(Schedule.Saturate(t.Rules));
                Assert.True(first.Check(t.Best.Of("a").Is(2L)));
            }

            var before = anchor.CachedIterationRules(t.Rules);
            Assert.NotNull(before);
            outer.DefineRule(t.Rules, "seen is better", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(5L));
            });

            using var second = anchor.CreateChild();
            second.Assume(t.Fact.Of("b"));
            second.Run(Schedule.Saturate(t.Rules));
            Assert.True(second.Check(t.Best.Of("b").Is(5L)));
            Assert.NotSame(before, anchor.CachedIterationRules(t.Rules));
            Assert.Equal(3, anchor.CachedIterationRules(t.Rules)!.Count);
        }

        [Fact]
        public void ARuleOfAnotherRulesetIsNotInTheList()
        {
            var (t, container, local) = Container();
            var other = t.Graph.DefineRuleset("other");
            container.DefineRule(other, "other local", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Best.Of(x).Is(3L));
            });
            container.Freeze();
            using var query = container.CreateChild();

            Assert.Equal([local], ScheduleExecutor.RulesFor([t.Rules], query).Select(r => r.Rule));
            Assert.Equal(2, ScheduleExecutor.RulesFor([t.Rules, other], query).Count);
        }

    }

}
