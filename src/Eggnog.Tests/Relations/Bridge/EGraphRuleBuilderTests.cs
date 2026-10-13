using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for recording and lowering e-graph rules (egglog-bridge/src/rule.rs:125-771): the layout of atoms (timestamp
    /// and subsumption entries), input checks, the core instructions each action lowers to, and the rule-based-rebuild
    /// helpers (<c>lookup_uf</c>, <c>check_for_update</c>, <c>rebuild_row</c>, sole focus).
    /// </summary>
    public class EGraphRuleBuilderTests
    {

        static InstrKind[] Lowered(EGraph egraph, EGraphRuleId rule)
        {
            var info = egraph.GetRuleInfo(rule);
            var plan = info.Query.BuildCachedPlan(egraph.Database, info.Description);
            return plan.Plan.Actions.Instrs.Select(i => i.Kind).ToArray();
        }

        [Fact]
        public void QueryTableAddsATimestampVariableAndTheSubsumptionEntry()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rb = egraph.NewRule("atoms", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var id = rb.NewVar(ColumnTy.Id);

            // add can subsume: [x, y, id, ts, NOT_SUBSUMED].
            Assert.Equal(new RuleAtomId(0), rb.QueryTable(fx.Add, [x, y, id], false));
            var atom = rb.Query.Atoms[0];
            Assert.Equal(egraph.GetTable(fx.Add), atom.Table);
            Assert.Equal(5, atom.Entries.Length);
            Assert.Equal([RuleEntry.Var(x), RuleEntry.Var(y), RuleEntry.Var(id)], atom.Entries[..3].ToArray());
            Assert.True(atom.Entries[3].IsVariable);
            Assert.Equal(new VariableId(3), atom.Entries[3].Variable.Id);
            Assert.Equal(ColumnTy.Id, rb.Query.Variables[3].Type);
            Assert.Equal(RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id), atom.Entries[4]);
            Assert.Equal(3, atom.Math.TimestampColumn);

            // Subsumed rows only, then either: a fresh variable after the timestamp variable.
            rb.QueryTable(fx.Add, [x, y, id], true);
            Assert.Equal(RuleEntry.Const(new Value(SchemaMath.Subsumed), ColumnTy.Id), rb.Query.Atoms[1].Entries[4]);
            rb.QueryTable(fx.Add, [x, y, id], null);
            var either = rb.Query.Atoms[2].Entries;
            Assert.Equal(new VariableId(5), either[3].Variable.Id);
            Assert.Equal(new VariableId(6), either[4].Variable.Id);

            // same cannot subsume: [x, y, id, ts]; the subsumption request is ignored.
            rb.QueryTable(fx.Same, [x, y, id], false);
            Assert.Equal(4, rb.Query.Atoms[3].Entries.Length);
            Assert.Equal(new VariableId(7), rb.Query.Atoms[3].Entries[3].Variable.Id);
            Assert.Equal(8, rb.Query.Variables.Count);
        }

        [Fact]
        public void QueryTableChecksArityTypesAndVariables()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var i64 = egraph.GetFunctionInfo(fx.Num).Schema[0];
            var rb = egraph.NewRule("checks", true);
            var x = rb.NewVar(ColumnTy.Id);
            var n = rb.NewVar(i64);

            Assert.Throws<ArgumentException>(() => rb.QueryTable(fx.Add, [x, x], false));
            Assert.Throws<ArgumentException>(() => rb.QueryTable(fx.Add, [x, n, x], false));
            Assert.Throws<ArgumentException>(() => rb.QueryTable(fx.Num, [x, x], false));
            Assert.Throws<ArgumentException>(() => rb.QueryTable(new FunctionId(99), [x], false));

            var other = egraph.NewRule("other", true);
            other.NewVar(ColumnTy.Id);
            other.NewVar(ColumnTy.Id);
            other.NewVar(ColumnTy.Id);
            var foreign = other.NewVar(ColumnTy.Id);
            Assert.Throws<ArgumentException>(() => rb.QueryTable(fx.Add, [x, x, foreign], false));

            // Nothing was recorded by the failed calls.
            Assert.Empty(rb.Query.Atoms);
            Assert.Equal(2, rb.Query.Variables.Count);

            // Constants are untyped.
            rb.QueryTable(fx.Num, [RuleEntry.Const(new Value(7), ColumnTy.Id), x], false);
            Assert.Single(rb.Query.Atoms);
        }

        [Fact]
        public void VariablesCarryTheirNames()
        {
            var egraph = NewEGraph();
            var rb = egraph.NewRule("names", true);
            var a = rb.NewVarNamed(ColumnTy.Id, "a");
            var b = rb.NewVar(ColumnTy.Id);
            Assert.Equal(new RuleVariable(new VariableId(0), "a"), a);
            Assert.Equal("a", a.ToString());
            Assert.Null(b.Name);
            Assert.NotEqual(new RuleVariable(new VariableId(0), null), a);
            Assert.Equal(RuleEntry.Var(a), (RuleEntry)a);
            Assert.True(((RuleEntry)a).IsVariable);
            Assert.Throws<InvalidOperationException>(() => ((RuleEntry)a).Constant);

            var c = RuleEntry.Const(new Value(5), ColumnTy.Id);
            Assert.False(c.IsVariable);
            Assert.Equal(new Value(5), c.Constant);
            Assert.Equal(ColumnTy.Id, c.ConstantType);
            Assert.Throws<InvalidOperationException>(() => c.Variable);
        }

        [Fact]
        public void ActionsLowerToTheInstructionsEgglogEmits()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var failing = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "partial",
            });
            var unit = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Const(new Value(42)),
                Merge = MergeFn.AssertEq,
                Name = "unit",
            });
            var identity = Register(egraph, (_, args) => args[0]);

            var rb = egraph.NewRule("all actions", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            var bound = rb.NewVar(ColumnTy.Id);
            rb.QueryPrim(identity, [x, bound], ColumnTy.Id);
            rb.QueryPrim(identity, [y, bound], ColumnTy.Id);
            var minted = rb.Lookup(fx.Add, [x, y], () => "add");
            var read = rb.Lookup(failing, [x], () => "partial");
            var constant = rb.Lookup(unit, [y], () => "unit");
            var called = rb.CallExternalFunc(identity, [minted], ColumnTy.Id, () => "identity");
            rb.Set(fx.Same, [read, constant, called]);
            rb.Union(x, called);
            rb.Remove(fx.Same, [y, x]);
            rb.Subsume(fx.Add, [y, x]);
            rb.Panic("stop");
            var rule = rb.Build();

            Assert.Equal(
            [
                InstrKind.ReadCounter,
                InstrKind.External,
                InstrKind.External, InstrKind.AssertEq,
                InstrKind.LookupOrInsertDefault,
                InstrKind.LookupWithFallback,
                InstrKind.LookupOrInsertDefault,
                InstrKind.ExternalWithFallback,
                InstrKind.Insert,
                InstrKind.Insert,
                InstrKind.Remove,
                InstrKind.LookupOrInsertDefault, InstrKind.LookupOrInsertDefault, InstrKind.InsertIfEq,
                InstrKind.External,
            ], Lowered(egraph, rule));

            // The lookup of a function without a default and the external call each own a lazy panic.
            Assert.Equal(2, egraph.GetRuleInfo(rule).Query.OwnedFunctions.Length);
            var kinds = egraph.GetRuleInfo(rule).Query.Actions.Select(a => a.Kind).ToArray();
            Assert.Equal(
            [
                RuleActionKind.QueryPrim, RuleActionKind.QueryPrim, RuleActionKind.LookupOrInsert, RuleActionKind.LookupWithFallback,
                RuleActionKind.LookupOrInsert, RuleActionKind.CallExternal, RuleActionKind.Insert, RuleActionKind.Union,
                RuleActionKind.Remove, RuleActionKind.LookupOrInsert, RuleActionKind.Subsume, RuleActionKind.Panic,
            ], kinds);
            Assert.All(egraph.GetRuleInfo(rule).Query.Actions, a => Assert.False(string.IsNullOrEmpty(a.ToString())));
        }

        [Fact]
        public void LookupsWriteTheDefaultTheTimestampAndTheSubsumptionCell()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rb = egraph.NewRule("mint", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            var minted = rb.Lookup(fx.Add, [x, y], () => "add");
            Assert.Equal(ColumnTy.Id, rb.Query.Variables[minted.Id.Index].Type);

            var action = rb.Query.Actions[0];
            Assert.Equal(RuleActionKind.LookupOrInsert, action.Kind);
            Assert.Equal(WriteValKind.IncrementCounter, action.Output.Kind);
            Assert.Equal(egraph.IdCounter, action.Output.Counter);
            Assert.Equal(RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id), action.Entry);
            Assert.Equal(minted.Id, action.Result);
        }

        [Fact]
        public void WritesCheckTheirShapes()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rb = egraph.NewRule("shapes", true);
            var x = rb.NewVar(ColumnTy.Id);

            Assert.Throws<ArgumentException>(() => rb.Set(fx.Add, [x, x]));
            Assert.Throws<ArgumentException>(() => rb.Remove(fx.Add, [x]));
            Assert.Throws<ArgumentException>(() => rb.Lookup(fx.Add, [x, x, x], () => ""));
            Assert.Throws<ArgumentException>(() => rb.Subsume(fx.Add, [x]));
            Assert.Throws<ArgumentException>(() => rb.Subsume(fx.Same, [x, x]));
            Assert.Throws<ArgumentException>(() => rb.QueryPrim(egraph.NewPanic("p"), [], ColumnTy.Id));
            Assert.Throws<ArgumentNullException>(() => rb.Lookup(fx.Add, [x, x], null!));
            Assert.Empty(rb.Query.Actions);
        }

        [Fact]
        public void BuildRegistersTheRuleOnceAndPicksMinCoverForOneAtom()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rb = egraph.NewRule("one atom", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            rb.NoDecomposition = true;
            Assert.True(rb.NoDecomposition);
            Assert.Equal(PlanStrategy.Gj, rb.PlanStrategy);
            var rule = rb.Build();
            Assert.Equal(rb.RuleId, rule);
            Assert.Equal(PlanStrategy.MinCover, egraph.GetRuleInfo(rule).Query.Strategy);
            Assert.True(egraph.GetRuleInfo(rule).Query.NoDecomposition);
            Assert.Equal("one atom", egraph.GetRuleInfo(rule).Description);
            Assert.Equal(0u, egraph.GetRuleInfo(rule).LastRunAt);
            Assert.Null(egraph.GetRuleInfo(rule).CachedPlan);

            Assert.Throws<InvalidOperationException>(() => rb.Build());
            Assert.Throws<InvalidOperationException>(() => rb.NewVar(ColumnTy.Id));
            Assert.Throws<InvalidOperationException>(() => rb.Union(x, y));
            Assert.Throws<InvalidOperationException>(() => rb.NoDecomposition = false);

            var two = egraph.NewRule("two atoms", true);
            var a = two.NewVar(ColumnTy.Id);
            var b = two.NewVar(ColumnTy.Id);
            two.QueryTable(fx.Same, [a, b, b], false);
            two.QueryTable(fx.Same, [b, a, a], false);
            Assert.Equal(PlanStrategy.Gj, egraph.GetRuleInfo(two.Build()).Query.Strategy);
        }

        [Fact]
        public void BuildRejectsReadsOfUnboundVariables()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;

            var rb = egraph.NewRule("unbound", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Num, [RuleEntry.Const(new Value(0), ColumnTy.Id), x], false);
            rb.Union(x, y);
            Assert.Throws<InvalidOperationException>(() => rb.Build());

            // A variable defined by an earlier action is bound; one defined later is not.
            var ok = egraph.NewRule("defined", true);
            var a = ok.NewVar(ColumnTy.Id);
            ok.QueryTable(fx.Num, [RuleEntry.Const(new Value(0), ColumnTy.Id), a], false);
            var minted = ok.Lookup(fx.Add, [a, a], () => "");
            ok.Union(a, minted);
            ok.Build();

            var late = egraph.NewRule("late", true);
            var b = late.NewVar(ColumnTy.Id);
            var c = late.NewVar(ColumnTy.Id);
            late.QueryTable(fx.Num, [RuleEntry.Const(new Value(0), ColumnTy.Id), b], false);
            late.QueryPrim(egraph.NewPanic("never"), [c, b], ColumnTy.Id);
            Assert.Throws<InvalidOperationException>(() => late.Build());

            // A primitive's unbound result is bound by it.
            var prim = egraph.NewRule("prim", true);
            var d = prim.NewVar(ColumnTy.Id);
            var e = prim.NewVar(ColumnTy.Id);
            prim.QueryTable(fx.Num, [RuleEntry.Const(new Value(0), ColumnTy.Id), d], false);
            prim.QueryPrim(Register(egraph, (_, args) => args[0]), [d, e], ColumnTy.Id);
            prim.Union(d, e);
            prim.Build();
        }

        [Fact]
        public void SubsumingAMissingKeyDoesNotHaltTheRestOfTheRule()
        {
            // Defect fixed (map §8.1 row 19): egglog halts the lane after subsuming a key the table lacks.
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", true, i64);
            var g = Constructor(egraph, "g", false, i64);
            var three = egraph.BaseValueConstant<Int64Codec, long>(3);

            var rb = egraph.NewRule("subsume then set", true);
            rb.Subsume(f, [three]);
            rb.Set(g, [three, RuleEntry.Const(new Value(9), ColumnTy.Id)]);
            egraph.RunRules(rb.Build());

            var rows = Rows(egraph, f);
            Assert.Single(rows);
            Assert.Equal([Int(egraph, 3), new Value(0)], rows[0].Values);
            Assert.True(rows[0].Subsumed);
            Assert.Equal([$"{Int(egraph, 3).Rep} 9"], Show(egraph, g));
        }

        [Fact]
        public void SubsumingAKeyAnEarlierLookupMintedKeepsTheSubsumption()
        {
            // egglog loses this subsumption: the lookup predicts the row as not subsumed, and the subsume then halts.
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", true, i64);
            var three = egraph.BaseValueConstant<Int64Codec, long>(3);

            var rb = egraph.NewRule("lookup then subsume", true);
            var minted = rb.Lookup(f, [three], () => "");
            rb.Subsume(f, [three]);
            egraph.RunRules(rb.Build());

            var rows = Rows(egraph, f);
            Assert.Single(rows);
            Assert.True(rows[0].Subsumed);
            Assert.Equal(new Value(0), rows[0].Values[1]);
        }

        [Fact]
        public void SubsumingKeepsAnAlreadySubsumedRow()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var f = Constructor(egraph, "f", true, i64);
            var one = egraph.BaseValueConstant<Int64Codec, long>(1);
            var rb = egraph.NewRule("subsume", false);
            rb.Subsume(f, [one]);
            var rule = rb.Build();

            Assert.True(egraph.RunRules(rule).Changed);
            var stamp = PhysicalRow(egraph, f, Int(egraph, 1).Rep)[2];

            // A naive rule subsumes again: the cell already reads subsumed, so nothing is written.
            Assert.False(egraph.RunRules(rule).Changed);
            Assert.Equal(stamp, PhysicalRow(egraph, f, Int(egraph, 1).Rep)[2]);
            Assert.Single(Rows(egraph, f));
        }

        /// <summary>
        /// A function <c>f(i64) -&gt; i64</c> that can subsume, merging by an addition that counts its calls, so that a
        /// row staged twice shows.
        /// </summary>
        static FunctionId CountingFunction(EGraph egraph, DefaultVal defaultValue, Action onMerge)
        {
            var i64 = IntType(egraph);
            var add = Register(egraph, (state, args) =>
            {
                onMerge();
                var a = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]));
                var b = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]));
                return state.BaseValues.Get<Int64Codec, long>(a + b).Rep;
            });

            return egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = defaultValue,
                Merge = MergeFn.Primitive(add, MergeFn.Old, MergeFn.New),
                Name = "f",
                CanSubsume = true,
            });
        }

        [Fact]
        public void SubsumingTheSameMissingKeyFromManyMatchesWritesItOnce()
        {
            // One batch of matches: the first lane's lookup stages the subsumed default row, which every later lane's
            // lookup and subsumption read finds predicted, as in egglog; nothing is staged twice.
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var merges = 0;
            var f = CountingFunction(egraph, DefaultVal.Const(Int(egraph, 5)), () => merges++);
            var src = Constructor(egraph, "src", false, i64);
            for (var i = 0; i < 100; i++)
                egraph.AddTerm(src, [Int(egraph, i)]);

            var rb = egraph.NewRule("subsume f(3) per match", true);
            rb.QueryTable(src, [rb.NewVar(i64), rb.NewVar(ColumnTy.Id)], false);
            rb.Subsume(f, [egraph.BaseValueConstant<Int64Codec, long>(3)]);
            Assert.Equal(100, egraph.RunRules(rb.Build()).RuleSetReport.NumMatches("subsume f(3) per match"));

            var rows = Rows(egraph, f);
            Assert.Single(rows);
            Assert.True(rows[0].Subsumed);
            Assert.Equal(5, Unint(egraph, rows[0].Values[1]));
            Assert.Equal(0, merges);
        }

        [Fact]
        public void SubsumingACommittedRowWritesTheSubsumedRowOnceAsEgglogDoes()
        {
            // egglog stages keys ++ [output, ts, SUBSUMED] once for a committed row that is not subsumed, and the merge
            // then runs on the equal outputs: with a non-idempotent merge the output changes, in egglog too.
            var egraph = NewEGraph();
            var merges = 0;
            var f = CountingFunction(egraph, DefaultVal.Fail, () => merges++);
            egraph.AddValues([(f, [Int(egraph, 1), Int(egraph, 7)])]);

            var rb = egraph.NewRule("subsume f(1)", true);
            rb.Subsume(f, [egraph.BaseValueConstant<Int64Codec, long>(1)]);
            egraph.RunRules(rb.Build());

            var rows = Rows(egraph, f);
            Assert.Single(rows);
            Assert.True(rows[0].Subsumed);
            Assert.Equal(14, Unint(egraph, rows[0].Values[1]));
            Assert.Equal(1, merges);

            // Without a default, subsuming a missing key panics, as in egglog.
            var missing = egraph.NewRule("subsume f(2)", true);
            missing.Subsume(f, [egraph.BaseValueConstant<Int64Codec, long>(2)]);
            var error = Assert.Throws<PanicException>(() => egraph.RunRules(missing.Build()));
            Assert.Equal("subsumed a nonextestent row!", error.PanicMessage);
            Assert.Single(Rows(egraph, f));
        }

        /// <summary>
        /// egglog's incremental rebuild rule for column 0 of a unary constructor (egglog-bridge/src/lib.rs:845-892),
        /// built from the internal helpers: the function atom and the union-find atom, the latter the sole focus, then
        /// <c>lookup_uf</c> of the other id column and <c>rebuild_row</c>.
        /// </summary>
        [Fact]
        public void RuleBasedIncrementalRebuildWithASoleFocus()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var fb = egraph.AddTerm(f, [b]);
            UnionWithoutRebuild(egraph, a, b);

            // f still holds [b, fb]: not canonical.
            Assert.Equal(2, egraph.TableSize(f));

            var rb = egraph.NewRule("incremental rebuild f/0", true);
            var v0 = rb.NewVar(ColumnTy.Id);
            var v1 = rb.NewVar(ColumnTy.Id);
            var canon = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(f, [v0, v1], null);
            rb.AddAtomWithTimestampAndFunc(egraph.UnionFindTable, null, null, [v0, canon]);
            rb.SetFocus(1);
            rb.PlanStrategy = PlanStrategy.MinCover;
            var c1 = rb.LookupUf(v1);
            rb.RebuildRow(f, [v0, v1], [canon, c1], null);
            var rule = rb.Build();
            Assert.Equal(1, egraph.GetRuleInfo(rule).Query.SoleFocus);
            Assert.Equal(3, egraph.GetRuleInfo(rule).Query.Atoms[1].Entries.Length);

            var report = egraph.RunRules(rule);
            Assert.True(report.Changed);
            Assert.Equal(1, report.RuleSetReport.NumMatches("incremental rebuild f/0"));
            Assert.Single(report.RuleSetReport.RuleReports["incremental rebuild f/0"]);
            Assert.Equal(egraph.GetCanonInUf(fa), egraph.GetCanonInUf(fb));
            Assert.Equal([$"{a.Rep} {Math.Min(fa.Rep, fb.Rep)}"], Show(egraph, f));

            // The only id displaced since (by the merge of f(a)'s outputs) keys no row of f.
            var again = egraph.RunRules(rule);
            Assert.False(again.Changed);
            Assert.Equal(0, again.RuleSetReport.NumMatches("incremental rebuild f/0"));
        }

        /// <summary>
        /// egglog's nonincremental rebuild rule (egglog-bridge/src/lib.rs:894-926): every row, canonicalized with
        /// <c>lookup_uf</c>, kept only where it changed (<c>check_for_update</c>), and rewritten with <c>rebuild_row</c>,
        /// carrying the subsumption cell over.
        /// </summary>
        [Fact]
        public void RuleBasedNonincrementalRebuildChecksForUpdates()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", true, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var c = egraph.FreshId();
            var fa = egraph.AddTerm(f, [a]);
            var fb = egraph.AddTerm(f, [b]);
            egraph.AddTerm(f, [c]);
            UnionWithoutRebuild(egraph, a, b);

            var rb = egraph.NewRule("rebuild f", false);
            var v0 = rb.NewVar(ColumnTy.Id);
            var v1 = rb.NewVar(ColumnTy.Id);
            var sub = rb.NewVar(ColumnTy.Id);
            rb.AddAtomWithTimestampAndFunc(egraph.GetTable(f), f, sub, [v0, v1]);
            var c0 = rb.LookupUf(v0);
            var c1 = rb.LookupUf(v1);
            rb.CheckForUpdate([v0, v1], [c0, c1]);
            rb.RebuildRow(f, [v0, v1], [c0, c1], sub);
            var rule = rb.Build();

            var report = egraph.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("rebuild f"));
            Assert.Equal(egraph.GetCanonInUf(fa), egraph.GetCanonInUf(fb));
            Assert.Equal(2, egraph.TableSize(f));
            Assert.All(Rows(egraph, f), r => Assert.False(r.Subsumed));
        }

        [Fact]
        public void RebuildHelpersCheckTheirInputs()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var i64 = egraph.GetFunctionInfo(fx.Num).Schema[0];
            var rb = egraph.NewRule("helpers", true);
            var x = rb.NewVar(ColumnTy.Id);
            var n = rb.NewVar(i64);
            Assert.Throws<ArgumentException>(() => rb.LookupUf(n));
            Assert.Throws<ArgumentException>(() => rb.CheckForUpdate([x], [x, x]));
            Assert.Throws<ArgumentException>(() => rb.CheckForUpdate([x], [n]));
            Assert.Throws<ArgumentException>(() => rb.RebuildRow(fx.Add, [x, x, x], [x, x], null));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.SetFocus(-1));
            rb.CheckForUpdate([x, RuleEntry.Const(new Value(1), ColumnTy.Id)], [RuleEntry.Const(new Value(2), i64), x]);

            // A sole focus beyond the atoms is rejected when the rule is built.
            rb.SetFocus(3);
            Assert.Throws<InvalidOperationException>(() => rb.Build());
        }

        /// <summary>
        /// Unions two ids and merges the union-find without rebuilding the tables, leaving them non-canonical.
        /// </summary>
        static void UnionWithoutRebuild(EGraph egraph, Value a, Value b)
        {
            egraph.WithExecutionState(state =>
            {
                egraph.ActionRegistry.UnionAction.Union(state, a.Rep, b.Rep);
                return 0;
            });
            egraph.Database.MergeAll(egraph.NextTimestamp);
            egraph.IncrementTimestamp();
        }

    }

}
