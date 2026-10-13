using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// T7 (colored-contexts spec §9.8, X19, I30): a store-backed context, the root included, runs every Id constant of a
    /// rule, in its query and its actions, in its canonical form there; a bare database runs them as written; cached plans
    /// keep the written plan, and each context's executed constants K select a cached variant.
    /// </summary>
    public class ConstantSubstitutionTests
    {

        /// <summary>
        /// A store with <c>r(id, id)</c> and <c>s(id, id)</c>, the root holding <c>r(x, c)</c>, and ids <c>c &lt; k</c>
        /// (so <c>k</c> loses a union with <c>c</c>).
        /// </summary>
        static (Store Store, TableId R, TableId S, uint X, uint C, uint K) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var r = store.AddTable(Relation(2), name: "r");
            var s = store.AddTable(Relation(2), name: "s");
            var root = store.Root;
            var x = root.NewId();
            var c = root.NewId();
            var k = root.NewId();
            Insert(root, r, x, c, 0);
            return (store, r, s, x, c, k);
        }

        /// <summary>
        /// Adds <c>r(v, k) → s(v, k)</c>: the query compares with <paramref name="constant"/> on an id column, and the
        /// action writes it into one.
        /// </summary>
        static void AddRule(RuleSetBuilder rsb, Store store, TableId r, TableId s, uint constant)
        {
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(constant), QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.Insert(s, [v, new Value(constant), ts]);
            rb.Build("r to s");
        }

        [Fact]
        public void AStoreBackedRootCanonicalizesAConstantThatLostAUnion()
        {
            var (store, r, s, x, c, k) = Setup();
            var root = store.Root;
            Assert.True(root.Union(c, k));
            root.Rebuild();
            Assert.Equal(c, root.Find(k));

            // egglog's cached plan would compare with k, which no canonical row holds; the root matches the class.
            var report = LayeredRuleUtil.Run(root, rsb => AddRule(rsb, store, r, s, k));
            Assert.Equal(1, report.NumMatches("r to s"));
            Assert.Equal([$"{x},{c}"], LayeredRuleUtil.Visible(root, s));
        }

        [Fact]
        public void ABareDatabaseRunsAConstantAsWritten()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var r = db.AddTable(Relation(2));
            var s = db.AddTable(Relation(2));
            DatabaseTestUtil.Stage(db, r, [1, 2, 0]);
            DatabaseTestUtil.Stage(db, uf, [2, 5, 0]);
            db.MergeAll();
            db.RebuildPass(uf, [r, s]);

            // 5 lost to 2, but a bare database has no designated union-find: the constant runs as written.
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(5), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(s, [v, new Value(5), new Value(1)]);
            var rule = rb.Build("written");
            using var rs = rsb.Build();
            Assert.Equal([5u, 5u], rs[rule].CanonicalConstants!);
            Assert.Same(rs[rule].WrittenTemplate, rs[rule].Template);
            Assert.False(db.View.CanCanonicalize);
            Assert.Equal(0, db.RunRuleSet(rs).NumMatches("written"));
        }

        [Fact]
        public void ActionConstantsAreCanonicalizedToo()
        {
            var (store, r, s, x, c, k) = Setup();
            var root = store.Root;
            root.Union(c, k);
            root.Rebuild();

            // r(v, c) → s(v, k): the query constant c is canonical, the action constant k is not.
            var log = new List<uint[]>();
            var recorder = root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            LayeredRuleUtil.Run(root, rsb =>
            {
                var q = rsb.NewRule();
                var v = q.NewVariable("v");
                q.AddAtom(r, [v, new Value(c), QueryEntry.Wildcard()]);
                var rb = q.Build();
                var ts = rb.ReadCounter(store.ClockCounter);
                rb.Insert(s, [v, new Value(k), ts]);
                rb.AssertEq(v, QueryEntry.IdConst(new Value(x)));
                rb.CallExternal(recorder, [QueryEntry.IdConst(new Value(k)), QueryEntry.Const(new Value(k))]);
                rb.Build("action");
            });

            Assert.Equal([$"{x},{c}"], LayeredRuleUtil.Visible(root, s));

            // A flagged argument is canonicalized; an unflagged one with no id column stays as written.
            Assert.Equal([c, k], Assert.Single(log));
        }

        [Fact]
        public void BuildCachedPlanKeepsTheWrittenPlanAndVariantsAreCached()
        {
            var (store, r, s, x, c, k) = Setup();
            var root = store.Root;
            root.Union(c, k);
            root.Rebuild();

            var rsb = root.Database.NewRuleSet();
            AddRule(rsb, store, r, s, k);
            using var rs = rsb.Build();
            var plan = rs[new RuleId(0)];
            Assert.Equal([c, c], plan.CanonicalConstants!);
            Assert.NotSame(plan.WrittenTemplate, plan.Template);
            Assert.Contains(Constraint.EqConst(new ColumnId(1), new Value(c)), plan.Template.Atoms[new AtomId(0)].FastConstraints);
            Assert.Contains(Constraint.EqConst(new ColumnId(1), new Value(k)), plan.WrittenTemplate.Atoms[new AtomId(0)].FastConstraints);
            Assert.Equal(c, ((InsertInstr)rs.GetAction(plan.Action).Instrs[1]).Values[1].Constant.Rep);

            // The cached plan never bakes the root's canonical forms.
            var cached = rs.BuildCachedPlan(new RuleId(0));
            Assert.Same(plan.WrittenTemplate, cached.Plan);
            Assert.Equal([k, k], cached.WrittenConstants!);
            Assert.Equal(k, ((InsertInstr)cached.Actions.Instrs[1]).Values[1].Constant.Rep);

            // Two rule sets in the context take one cached variant; a context where k is canonical takes the written plan.
            var first = root.Database.NewRuleSet();
            var a = first.AddRuleFromCachedPlan(cached)!.Value;
            using var rs1 = first.Build();
            var second = root.Database.NewRuleSet();
            var b = second.AddRuleFromCachedPlan(cached)!.Value;
            using var rs2 = second.Build();
            Assert.Same(rs1[a].Template, rs2[b].Template);
            Assert.Same(rs1.GetAction(rs1[a].Action), rs2.GetAction(rs2[b].Action));
            Assert.Equal(1, cached.VariantCount);
            Assert.Equal((cached.Plan, cached.Actions), cached.GetVariantFor([k, k]));
            Assert.Equal((cached.Plan, cached.Actions), cached.GetVariantFor(null));
            Assert.Equal(1, root.Database.RunRuleSet(rs1).NumMatches("r to s"));
        }

        [Fact]
        public void EachContextRunsItsOwnCanonicalForm()
        {
            var (store, r, s, x, c, k) = Setup();
            var root = store.Root;
            var rsb0 = root.Database.NewRuleSet();
            AddRule(rsb0, store, r, s, k);
            using var written = rsb0.Build();
            var cached = written.BuildCachedPlan(new RuleId(0));

            // At the root k is canonical: the written plan, no match.
            Assert.Equal(0, LayeredRuleUtil.Run(root, rsb => rsb.AddRuleFromCachedPlan(cached)).NumMatches("r to s"));
            Assert.Equal(0, cached.VariantCount);

            // A child unions k into c: its run matches the class and writes c (context = copy of a root that did so).
            root.Freeze();
            using var child = root.CreateChild();
            Assert.True(child.Union(c, k));
            child.Rebuild();
            Assert.Equal(1, LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached)).NumMatches("r to s"));
            Assert.Equal([$"{x},{c}"], LayeredRuleUtil.Visible(child, s));
            Assert.Empty(LayeredRuleUtil.Visible(root, s));
            Assert.Equal(1, cached.VariantCount);
            child.CheckInvariants();
        }

        [Fact]
        public void SubstitutionSharesWhatDoesNotChange()
        {
            var (store, r, s, x, c, k) = Setup();
            var root = store.Root;
            root.Union(c, k);
            root.Rebuild();
            var rsb = root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            var w = q.NewVariable("w");
            q.AddAtom(r, [v, new Value(k), QueryEntry.Wildcard()]);
            q.AddAtom(s, [v, w, QueryEntry.Wildcard()]);
            q.NoDecomposition = true;
            var rb = q.Build();
            rb.AssertEq(v, v);
            rb.Insert(s, [v, w, new Value(1)]);
            rb.Build("share");
            using var rs = rsb.Build();
            var plan = rs[new RuleId(0)];
            Assert.Same(plan.WrittenTemplate.Atoms[new AtomId(1)], plan.Template.Atoms[new AtomId(1)]);
            Assert.NotSame(plan.WrittenTemplate.Atoms[new AtomId(0)], plan.Template.Atoms[new AtomId(0)]);

            // No action constant changes, so the program is the written one.
            Assert.Same(plan.WrittenActions, rs.GetAction(plan.Action));

            // The substitution rewrites only the recorded positions.
            var substitution = ConstantSubstitution.Create(plan.IdConstants, [], plan.CanonicalConstants![..plan.IdConstants.Length])!;
            Assert.Equal(1, substitution.Query.Length);
            Assert.False(substitution.ChangesActions);
            var instrs = new Instr[] { new AssertEqInstr(QueryEntry.IdConst(new Value(k)), v), new AssertNeInstr(v, w) };
            var actions = new ActionInfo(instrs, [v, w], [new ActionIdConstant(0, 0, new Value(k))]);
            var actionSubstitution = ConstantSubstitution.Create([], actions.IdConstants, [c])!;
            var rewritten = ActionSubstituter.Substitute(actions, actionSubstitution);
            Assert.NotSame(actions.Instrs[0], rewritten.Instrs[0]);
            Assert.Same(actions.Instrs[1], rewritten.Instrs[1]);
            Assert.Equal(c, ((AssertEqInstr)rewritten.Instrs[0]).Left.Constant.Rep);
            Assert.True(((AssertEqInstr)rewritten.Instrs[0]).Left.IsIdConstant);
            Assert.Same(actions.IdConstants, rewritten.IdConstants);
        }

        [Fact]
        public void TheRuleBuilderRecordsActionConstants()
        {
            var (store, r, s, x, c, k) = Setup();
            var rsb = store.Root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(s, [new Value(k), v, new Value(9)]);
            rb.AssertNe(v, QueryEntry.Const(new Value(4)));
            rb.AssertNe(v, QueryEntry.IdConst(new Value(5)));
            rb.Remove(r, [v, new Value(c)]);
            rb.Lookup(store.UnionFindTable, [new Value(k)], new ColumnId(1));
            rb.Build("recorded");
            using var rs = rsb.Build();
            var action = rs.GetAction(new ActionId(0));

            // Insert column 0 (an id column) and not the timestamp; the flagged comparand only; the remove key's id column;
            // the union-find lookup's key.
            Assert.Equal([new ActionIdConstant(0, 0, new Value(k)), new ActionIdConstant(2, 1, new Value(5)), new ActionIdConstant(3, 1, new Value(c)), new ActionIdConstant(4, 0, new Value(k))], action.IdConstants);
        }

    }

}
