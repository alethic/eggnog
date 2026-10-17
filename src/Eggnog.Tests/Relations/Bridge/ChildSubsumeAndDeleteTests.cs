using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Subsumption and deletion by rules in a child, through <see cref="EGraphRuleBuilder"/>: what the child's later
    /// rules match after it subsumes or deletes, what its ancestors and descendants see, and how a refused delete
    /// surfaces through a rule run. A subsume is a sticky value change, allowed wherever a write is: in a child under a
    /// writable parent, and at a root with live descendants, which see the subsumed row once they catch up. A delete
    /// from a shared table is a retraction, allowed in a child only under a frozen parent
    /// (<see cref="RetractionGuard"/>).
    /// </summary>
    public class ChildSubsumeAndDeleteTests
    {

        /// <summary>
        /// The name of the rule <see cref="Live"/> builds.
        /// </summary>
        const string LiveRule = "live";

        /// <summary>
        /// Builds the e-graph: <c>f(i64) -&gt; id</c> (a constructor that can subsume), <c>seen(i64)</c> (a relation
        /// with a unit output), and <c>f(1)</c> and <c>f(2)</c> added at the root.
        /// </summary>
        static (EGraph EGraph, FunctionId F, FunctionId Seen, ColumnTy I64, ColumnTy UnitType, Value Unit) Setup()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var unitType = ColumnTy.Base(egraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            var unit = egraph.BaseValues.Get<UnitCodec, ValueTuple>(default);
            var f = Constructor(egraph, "f", true, i64);
            var seen = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, unitType],
                Default = DefaultVal.Const(unit),
                Merge = MergeFn.AssertEq,
                Name = "seen",
            });
            egraph.Root.AddTerm(f, [Int(egraph, 1)]);
            egraph.Root.AddTerm(f, [Int(egraph, 2)]);
            return (egraph, f, seen, i64, unitType, unit);
        }

        /// <summary>
        /// <c>f(k) -&gt; _</c>, not subsumed <c>=&gt; seen(k)</c>: a naive rule, so each run matches every live row of
        /// <c>f</c> again and its match count is the number of rows that are not subsumed.
        /// </summary>
        static EGraphRuleId Live(EGraph egraph, FunctionId f, FunctionId seen, ColumnTy i64, ColumnTy unitType, Value unit)
        {
            var rb = egraph.NewRule(LiveRule, false);
            var k = rb.NewVar(i64);
            rb.QueryTable(f, [k, rb.NewVar(ColumnTy.Id)], false);
            rb.Set(seen, [k, RuleEntry.Const(unit, unitType)]);
            return rb.Build();
        }

        /// <summary>
        /// <c>=&gt; (subsume (f 1))</c>.
        /// </summary>
        static EGraphRuleId SubsumeOne(EGraph egraph, FunctionId f) => Subsume(egraph, f, 1);

        /// <summary>
        /// <c>=&gt; (subsume (f key))</c>.
        /// </summary>
        static EGraphRuleId Subsume(EGraph egraph, FunctionId f, long key)
        {
            var rb = egraph.NewRule($"subsume f({key})", true);
            rb.Subsume(f, [egraph.BaseValueConstant<Int64Codec, long>(key)]);
            return rb.Build();
        }

        /// <summary>
        /// <c>=&gt; (delete (f 1))</c>.
        /// </summary>
        static EGraphRuleId DeleteOne(EGraph egraph, FunctionId f)
        {
            var rb = egraph.NewRule("delete f(1)", true);
            rb.Remove(f, [egraph.BaseValueConstant<Int64Codec, long>(1)]);
            return rb.Build();
        }

        /// <summary>
        /// The keys of <c>f</c>'s rows that <paramref name="context"/> sees, with <c>" (s)"</c> for subsumed ones.
        /// </summary>
        static string[] FRows(EGraph egraph, EGraphContext context, FunctionId f)
        {
            var rows = new List<string>();
            context.ForEach(f, row => rows.Add($"{Unint(egraph, row.Values[0])}{(row.Subsumed ? " (s)" : "")}"));
            rows.Sort(StringComparer.Ordinal);
            return rows.ToArray();
        }

        /// <summary>
        /// The keys of <c>seen</c> that <paramref name="context"/> sees.
        /// </summary>
        static long[] Seen(EGraph egraph, EGraphContext context, FunctionId seen)
        {
            var keys = new List<long>();
            context.ForEach(seen, row => keys.Add(Unint(egraph, row.Values[0])));
            keys.Sort();
            return keys.ToArray();
        }

        [Fact]
        public void AChildsLaterRulesDoNotMatchARowItSubsumedAndItsParentStillDoes()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var subsume = SubsumeOne(egraph, f);
            var root = egraph.Root;

            using var child = root.CreateChild();
            Assert.Equal(2, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            child.RunRules(subsume);
            Assert.Equal(["1 (s)", "2"], FRows(egraph, child, f));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            // A rule over every row, subsumed or not, still finds the subsumed row in the child.
            var any = egraph.NewRule("any", false);
            any.QueryTable(f, [any.NewVar(i64), any.NewVar(ColumnTy.Id)], null);
            var anyRule = any.Build();
            Assert.Equal(2, child.RunRules(anyRule).RuleSetReport.NumMatches("any"));

            // The subsumption is the child's: the parent's row is not subsumed and its rules still match it.
            Assert.Equal(["1", "2"], FRows(egraph, root, f));
            Assert.Equal(2, root.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            Assert.Equal([1L, 2L], Seen(egraph, root, seen));

            // A grandchild inherits the child's subsumption, and a sibling does not see it.
            using var grandchild = child.CreateChild();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, grandchild, f));
            Assert.Equal(1, grandchild.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            using var sibling = root.CreateChild();
            Assert.Equal(2, sibling.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            child.CheckInvariants();
            grandchild.CheckInvariants();
            root.CheckInvariants();
        }

        [Fact]
        public void ASemiNaiveRuleInAChildDoesNotDeriveFromARowSubsumedBeforeItsFirstRun()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var rb = egraph.NewRule("live-seminaive", true);
            var k = rb.NewVar(i64);
            rb.QueryTable(f, [k, rb.NewVar(ColumnTy.Id)], false);
            rb.Set(seen, [k, RuleEntry.Const(unit, unitType)]);
            var live = rb.Build();
            var subsume = SubsumeOne(egraph, f);

            using var child = egraph.Root.CreateChild();
            child.RunRules(subsume);
            child.RunRules(live);
            Assert.Equal([2L], Seen(egraph, child, seen));
            Assert.Empty(Seen(egraph, egraph.Root, seen));
        }

        [Fact]
        public void ARuleDeleteInAChildOfAFrozenRootStaysHiddenFromTheChildAndAGrandchild()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var delete = DeleteOne(egraph, f);
            var root = egraph.Root;
            root.Freeze();

            using var child = root.CreateChild();
            child.RunRules(delete);
            Assert.False(child.IsFaulted);
            Assert.Equal(["2"], FRows(egraph, child, f));
            Assert.False(child.ContainsKey(f, [Int(egraph, 1)]));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            Assert.Equal([2L], Seen(egraph, child, seen));

            // A grandchild reads through the child's hide: neither its reads nor its rules find the deleted row.
            using var grandchild = child.CreateChild();
            Assert.Equal(["2"], FRows(egraph, grandchild, f));
            Assert.False(grandchild.ContainsKey(f, [Int(egraph, 1)]));
            var rb = egraph.NewRule("any", false);
            rb.QueryTable(f, [rb.NewVar(i64), rb.NewVar(ColumnTy.Id)], null);
            Assert.Equal(1, grandchild.RunRules(rb.Build()).RuleSetReport.NumMatches("any"));

            // The root still has the row.
            Assert.Equal(["1", "2"], FRows(egraph, root, f));
            Assert.True(root.ContainsKey(f, [Int(egraph, 1)]));
            child.CheckInvariants();
            grandchild.CheckInvariants();
        }

        [Fact]
        public void ARuleDeleteInAChildOfAWritableRootIsRefusedAndFaultsTheChild()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var delete = DeleteOne(egraph, f);
            var root = egraph.Root;

            using var child = root.CreateChild();
            using var sibling = root.CreateChild();
            var error = Assert.Throws<InvalidOperationException>(() => child.RunRules(delete));
            Assert.Contains("only under a frozen parent", error.Message);
            Assert.True(child.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => child.RunRules(live));

            // Nothing was retracted: the root and a sibling still see the row and match it.
            Assert.False(root.IsFaulted);
            Assert.Equal(["1", "2"], FRows(egraph, root, f));
            Assert.Equal(2, sibling.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            Assert.Equal(2, root.RunRules(live).RuleSetReport.NumMatches(LiveRule));
        }

        [Fact]
        public void ASubsumeAtAWritableRootWithALiveChildReachesTheChildOnCatchUp()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var subsume = SubsumeOne(egraph, f);
            var root = egraph.Root;

            using var child = root.CreateChild();
            using var grandchild = child.CreateChild();
            Assert.Equal(2, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            Assert.Equal(2, grandchild.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            // The root subsumes by a rule while it has live descendants: allowed, as a write is.
            root.RunRules(subsume);
            Assert.Equal(["1 (s)", "2"], FRows(egraph, root, f));
            Assert.Equal(1, root.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            // Caught up, the child and the grandchild see the row subsumed, and their rules over rows that are not
            // subsumed stop matching it.
            child.EnsureClean();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, child, f));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            grandchild.EnsureClean();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, grandchild, f));
            Assert.Equal(1, grandchild.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            child.CheckInvariants();
            grandchild.CheckInvariants();
            root.CheckInvariants();
        }

        /// <summary>
        /// A child's union rewrites a root row into a copy of its own, which shadows the root's; the root then subsumes
        /// the root row. The subsumption reaches the child's copy on catch-up, as an ancestor's value change does.
        /// </summary>
        [Fact]
        public void ASubsumeAtTheRootReachesARowTheChildRewroteByAUnion()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var root = egraph.Root;
            var one = root.LookupId(f, [Int(egraph, 1)])!.Value;
            var two = root.LookupId(f, [Int(egraph, 2)])!.Value;
            var (winner, loser, loserKey) = one.Rep < two.Rep ? (one, two, 2L) : (two, one, 1L);
            var winnerKey = 3 - loserKey;

            using var child = root.CreateChild();
            child.StageUnion(one, two);
            child.EnsureClean();
            Assert.Equal(winner, child.LookupId(f, [Int(egraph, loserKey)]));
            Assert.Equal(loser, root.LookupId(f, [Int(egraph, loserKey)]));
            Assert.Equal(2, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            root.RunRules(Subsume(egraph, f, loserKey));
            child.EnsureClean();
            Assert.Equal(new[] { $"{loserKey} (s)", $"{winnerKey}" }.Order(StringComparer.Ordinal), FRows(egraph, child, f));
            Assert.Equal(winner, child.LookupId(f, [Int(egraph, loserKey)]));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            child.CheckInvariants();
            root.CheckInvariants();
        }

        [Fact]
        public void ASubsumeInAChildWithALiveGrandchildReachesTheGrandchildOnCatchUp()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var subsume = SubsumeOne(egraph, f);
            var root = egraph.Root;

            using var child = root.CreateChild();
            using var grandchild = child.CreateChild();
            Assert.Equal(2, grandchild.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            // The child subsumes by a rule while it has a live descendant: allowed, as a write is.
            child.RunRules(subsume);
            Assert.False(child.IsFaulted);
            Assert.Equal(["1 (s)", "2"], FRows(egraph, child, f));

            // Caught up, the grandchild sees the row subsumed and its rule over rows that are not subsumed stops
            // matching it; the root's row is not subsumed.
            grandchild.EnsureClean();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, grandchild, f));
            Assert.Equal(1, grandchild.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            Assert.Equal(["1", "2"], FRows(egraph, root, f));
            child.CheckInvariants();
            grandchild.CheckInvariants();
            root.CheckInvariants();
        }

        /// <summary>
        /// A child subsumes a root row; the root then unions the row's output class into another, which rewrites the
        /// root's row. On catch-up the child's row takes the new output and stays subsumed: the subsumption is sticky.
        /// </summary>
        [Fact]
        public void AChildsSubsumptionSurvivesARootUnionOfTheRowsClass()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var root = egraph.Root;
            var one = root.LookupId(f, [Int(egraph, 1)])!.Value;
            var two = root.LookupId(f, [Int(egraph, 2)])!.Value;
            var (winner, loserKey) = one.Rep < two.Rep ? (one, 2L) : (two, 1L);
            var winnerKey = 3 - loserKey;

            using var child = root.CreateChild();
            child.RunRules(Subsume(egraph, f, loserKey));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            root.StageUnion(one, two);
            root.EnsureClean();
            Assert.Equal(winner, root.LookupId(f, [Int(egraph, loserKey)]));
            Assert.Equal(["1", "2"], FRows(egraph, root, f));

            child.EnsureClean();
            Assert.Equal(new[] { $"{loserKey} (s)", $"{winnerKey}" }.Order(StringComparer.Ordinal), FRows(egraph, child, f));
            Assert.Equal(winner, child.LookupId(f, [Int(egraph, loserKey)]));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
            child.CheckInvariants();
            root.CheckInvariants();
        }

        [Fact]
        public void AHostSubsumeAtAWritableRootWithALiveChildReachesTheChildOnCatchUp()
        {
            var (egraph, f, seen, i64, unitType, unit) = Setup();
            var live = Live(egraph, f, seen, i64, unitType, unit);
            var root = egraph.Root;

            using var child = root.CreateChild();
            Assert.Equal(2, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));

            var action = new TableAction(egraph, f);
            egraph.WithExecutionState(state =>
            {
                action.Subsume(state, [Int(egraph, 1).Rep]);
                return 0;
            });
            egraph.FlushUpdates();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, root, f));
            child.EnsureClean();
            Assert.Equal(["1 (s)", "2"], FRows(egraph, child, f));
            Assert.Equal(1, child.RunRules(live).RuleSetReport.NumMatches(LiveRule));
        }

    }

}
