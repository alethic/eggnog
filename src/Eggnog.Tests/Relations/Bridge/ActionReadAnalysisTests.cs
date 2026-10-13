using System;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="ActionReadAnalysis"/> (colored-contexts spec §10.2, X41): which instructions are query-determined, for
    /// each instruction kind; constructors (core and bridge) against lattice functions and private tables; externals with
    /// and without <see cref="ExternalFunction.ReadsTables"/>; and the analysis as rules see it
    /// (<see cref="RuleInfo.AllowInheritance"/>).
    /// </summary>
    public class ActionReadAnalysisTests
    {

        static readonly Variable X = new(0);
        static readonly Variable Y = new(1);
        static readonly Variable Z = new(2);
        static QueryEntry V(Variable v) => QueryEntry.Var(v);

        [Fact]
        public void WritesComparisonsAndCountersAreQueryDetermined()
        {
            var s = new ActionReadAnalysisSetup();
            var edge = s.Table(s.Graph.Edge);
            Assert.True(s.Determined(new InsertInstr(edge, [V(X), V(X), V(Y), V(Y)])));
            Assert.True(s.Determined(new InsertIfEqInstr(edge, V(X), V(Y), [V(X), V(X), V(Y), V(Y)])));
            Assert.True(s.Determined(new RemoveInstr(edge, [V(X), V(Y)])));
            Assert.True(s.Determined(new AssertEqInstr(V(X), V(Y))));
            Assert.True(s.Determined(new AssertNeInstr(V(X), V(Y))));
            Assert.True(s.Determined(new AssertAnyNeInstr([V(X), V(Y)], 1)));
            Assert.True(s.Determined(new ReadCounterInstr(s.Graph.EGraph.TimestampCounter, X)));
        }

        [Fact]
        public void ConstructorLookupsAreQueryDeterminedAndLatticeLookupsAreNot()
        {
            var s = new ActionReadAnalysisSetup();
            var num = s.Table(s.Graph.Num);
            var low = s.Table(s.Graph.Low);
            var edge = s.Table(s.Graph.Edge);
            var numMath = s.Graph.EGraph.GetFunctionInfo(s.Graph.Num).Math;
            WriteVal[] numDefaults = [WriteVal.FromCounter(s.Graph.EGraph.IdCounter), V(Y)];

            // A bridge constructor (a function-table merge over a union) and a core one (UnionIdMerge), by key or by the
            // union column.
            Assert.True(s.Schema[num]!.IsConstructor);
            Assert.True(s.Schema[s.CoreConstructor]!.IsConstructor);
            Assert.True(s.Determined(new LookupOrInsertDefaultInstr(num, [V(X)], numDefaults, new ColumnId(numMath.OutputColumn), Z)));
            Assert.True(s.Determined(new LookupOrInsertDefaultInstr(num, [V(X)], numDefaults, new ColumnId(0), Z)));
            Assert.True(s.Determined(new LookupOrInsertDefaultInstr(s.CoreConstructor, [V(X)], [WriteVal.FromCounter(s.Graph.EGraph.IdCounter), V(Y)], new ColumnId(1), Z)));

            // A constructor's other value cell (its timestamp, a subsumption flag) can change in a descendant.
            Assert.False(s.Determined(new LookupOrInsertDefaultInstr(num, [V(X)], numDefaults, new ColumnId(numMath.TimestampColumn), Z)));

            // A lattice function, a relation and a private constructor are not constructors a descendant reads alike.
            Assert.False(s.Schema[low]!.IsConstructor);
            Assert.False(s.Schema[edge]!.IsConstructor);
            Assert.False(s.Determined(new LookupOrInsertDefaultInstr(low, [V(X)], [V(Y), V(Y)], new ColumnId(1), Z)));
            Assert.False(s.Determined(new LookupOrInsertDefaultInstr(s.PrivateConstructor, [V(X)], [WriteVal.FromCounter(s.Graph.EGraph.IdCounter), V(Y)], new ColumnId(1), Z)));

            // Plain lookups of any table but the union-find read rows a descendant can change.
            Assert.False(s.Determined(new LookupInstr(num, [V(X)], new ColumnId(1), Y)));
            Assert.False(s.Determined(new LookupInstr(low, [V(X)], new ColumnId(1), Y)));
            Assert.False(s.Determined(new LookupWithDefaultInstr(low, [V(X)], new ColumnId(1), Y, V(X))));
            Assert.False(s.Determined(new LookupWithFallbackInstr(low, [V(X)], s.Pure, [], new ColumnId(1), Y)));
        }

        [Fact]
        public void UnionFindLookupsAreCanonicalization()
        {
            var s = new ActionReadAnalysisSetup();
            var uf = s.Graph.EGraph.UnionFindTable;
            Assert.True(s.Determined(new LookupInstr(uf, [V(X)], new ColumnId(1), Y)));
            Assert.True(s.Determined(new LookupWithDefaultInstr(uf, [V(X)], new ColumnId(1), Y, V(X))));
            Assert.True(s.Determined(new LookupOrInsertDefaultInstr(uf, [V(X)], [V(X), V(X)], new ColumnId(1), Z)));
            Assert.True(s.Determined(new LookupWithFallbackInstr(uf, [V(X)], s.Pure, [], new ColumnId(1), Y)));
            Assert.False(s.Determined(new LookupWithFallbackInstr(uf, [V(X)], s.Reading, [], new ColumnId(1), Y)));
        }

        [Fact]
        public void ExternalsAreQueryDeterminedUnlessTheyReadTables()
        {
            var s = new ActionReadAnalysisSetup();
            Assert.False(ExternalFunction.Create(static (ExecutionState _, ReadOnlySpan<uint> _, out uint r) =>
            {
                r = 0;
                return true;
            }).ReadsTables);
            Assert.True(s.Externals[s.Reading].ReadsTables);
            Assert.True(s.Determined(new ExternalInstr(s.Pure, [V(X)], Y)));
            Assert.False(s.Determined(new ExternalInstr(s.Reading, [V(X)], Y)));
            Assert.True(s.Determined(new ExternalWithFallbackInstr(s.Pure, [V(X)], s.Pure, [], Y)));
            Assert.False(s.Determined(new ExternalWithFallbackInstr(s.Pure, [V(X)], s.Reading, [], Y)));
            Assert.False(s.Determined(new ExternalWithFallbackInstr(s.Reading, [V(X)], s.Pure, [], Y)));

            // An id that names no registered function is treated as reading.
            var freed = s.Graph.EGraph.RegisterExternalFunction(new PanicFunction("gone"));
            s.Graph.EGraph.FreeExternalFunction(freed);
            Assert.False(s.Determined(new ExternalInstr(freed, [], Y)));

            // The bridge's own panics and the early stop read nothing.
            Assert.False(new PanicFunction("p").ReadsTables);
            Assert.False(new StopOnContradictionFunction().ReadsTables);
        }

        [Fact]
        public void AProgramIsQueryDeterminedOnlyWhenEveryInstructionIs()
        {
            var s = new ActionReadAnalysisSetup();
            var edge = s.Table(s.Graph.Edge);
            var low = s.Table(s.Graph.Low);
            var good = new ActionInfo([new ReadCounterInstr(s.Graph.EGraph.TimestampCounter, X), new InsertInstr(edge, [V(Y), V(Y), V(Y), V(X)])], [Y]);
            var bad = new ActionInfo([new ReadCounterInstr(s.Graph.EGraph.TimestampCounter, X), new LookupInstr(low, [V(Y)], new ColumnId(1), new Variable(2)), new InsertInstr(edge, [V(Y), V(Y), V(Y), V(X)])], [Y]);
            Assert.True(ActionReadAnalysis.IsQueryDetermined(good, s.Schema, s.Externals));
            Assert.False(ActionReadAnalysis.IsQueryDetermined(bad, s.Schema, s.Externals));
        }

        /// <summary>
        /// The analysis as rules see it, through the bridge's lowering: writes, unions, canonicalization and constructor
        /// terms inherit; a naive rule, a function lookup, a subsumption, a table-reading primitive on either side do not.
        /// </summary>
        [Fact]
        public void BridgeRulesInheritExactlyWhenTheirActionsAreQueryDetermined()
        {
            var g = new ContextGraph();
            var egraph = g.EGraph;
            var reading = egraph.RegisterExternalFunction(ExternalFunction.Create(static (ExecutionState _, ReadOnlySpan<uint> a, out uint r) =>
            {
                r = a[0];
                return true;
            }, readsTables: true));
            var pure = egraph.RegisterExternalFunction(ExternalFunction.Create(static (ExecutionState _, ReadOnlySpan<uint> a, out uint r) =>
            {
                r = a[0];
                return true;
            }));
            var subsumable = Constructor(egraph, "sub", true, ColumnTy.Id);

            EGraphRuleId Rule(string name, bool semiNaive, Action<EGraphRuleBuilder, RuleVariable, RuleVariable> body)
            {
                var rb = egraph.NewRule(name, semiNaive);
                var x = rb.NewVar(ColumnTy.Id);
                var y = rb.NewVar(ColumnTy.Id);
                rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
                body(rb, x, y);
                return rb.Build();
            }

            var unit = RuleEntry.Const(g.Unit, g.UnitType);
            var cases = new (EGraphRuleId Rule, bool Inherits)[]
            {
                (Rule("set", true, (rb, x, y) => rb.Set(g.Path, [x, y, unit])), true),
                (Rule("union", true, (rb, x, y) => rb.Union(x, y)), true),
                (Rule("remove", true, (rb, x, y) => rb.Remove(g.Path, [x, y])), true),
                (Rule("lookup-uf", true, (rb, x, y) => rb.Set(g.Path, [rb.LookupUf(x), y, unit])), true),
                (Rule("constructor", true, (rb, x, y) => rb.Set(g.Path, [rb.Lookup(g.Add, [x, y], () => ""), y, unit])), true),
                (Rule("pure-rhs", true, (rb, x, y) => rb.Set(g.Path, [rb.CallExternalFunc(pure, [x], ColumnTy.Id, () => ""), y, unit])), true),
                (Rule("pure-lhs", true, (rb, x, y) => rb.QueryPrim(pure, [x, x], ColumnTy.Id)), true),
                (Rule("naive", false, (rb, x, y) => rb.Set(g.Path, [x, y, unit])), false),
                (Rule("lookup-function", true, (rb, x, y) => rb.Lookup(g.Low, [x], () => "")), false),
                (Rule("lookup-relation", true, (rb, x, y) => rb.Lookup(g.Path, [x, y], () => "")), false),
                (Rule("subsume", true, (rb, x, y) => rb.Subsume(subsumable, [x])), false),
                (Rule("reading-rhs", true, (rb, x, y) => rb.CallExternalFunc(reading, [x], ColumnTy.Id, () => "")), false),
                (Rule("reading-lhs", true, (rb, x, y) => rb.QueryPrim(reading, [x, x], ColumnTy.Id)), false),
            };

            foreach (var (rule, inherits) in cases)
            {
                var info = egraph.GetRuleInfo(rule);
                Assert.False(info.AllowInheritance);
                info.GetOrBuildCachedPlan(egraph.Database);
                Assert.True(inherits == info.AllowInheritance, $"{info.Description}: expected AllowInheritance {inherits}");
            }
        }

    }

}
