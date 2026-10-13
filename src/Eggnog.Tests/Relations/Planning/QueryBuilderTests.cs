using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="QueryBuilder"/>: egglog's <c>add_atom</c> (core-relations/src/query.rs:355-463) and the
    /// variable marks of <c>RuleBuilder</c>.
    /// </summary>
    public class QueryBuilderTests
    {

        static ColumnId C(int i) => new(i);

        static PlanTestDatabase Db(out TableId t3, out TableId f2)
        {
            var db = new PlanTestDatabase();
            t3 = db.AddTable(3, 3, [[1, 2, 3], [3, 3, 3], [4, 5, 6]], sortColumn: 2, uncacheable: [false, true, false]);
            f2 = db.AddTable(2, 1, [[1, 7], [2, 8]]);
            return db;
        }

        [Fact]
        public void ConstantsBecomeEqConstAfterTheGivenConstraints()
        {
            var db = Db(out var t3, out _);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable("x");
            var a = qb.AddAtom(t3, [x, new Value(5), new Value(6)], [Constraint.GtConst(C(0), new Value(0))]);
            var atom = qb.Build(new ActionId(3)).Atoms[a];

            // c2 is the sort column, so its constant is answered by the table itself; c1 is uncacheable, so its constant
            // stays slow, after the user's slow GtConst.
            Assert.Equal(["c2 == 6"], atom.FastConstraints.Select(c => c.ToString()));
            Assert.Equal(["c0 > 0", "c1 == 5"], atom.SlowConstraints.Select(c => c.ToString()));
        }

        [Fact]
        public void RepeatedVariablesChainEqualities()
        {
            var db = Db(out var t3, out _);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            var a = qb.AddAtom(t3, [x, x, x]);
            var query = qb.Build(new ActionId(0));
            var atom = query.Atoms[a];

            Assert.Equal(["c1 == c0", "c2 == c1"], atom.SlowConstraints.Select(c => c.ToString()));
            Assert.Equal(C(2), atom.GetColumn(x));
            Assert.Equal([(C(0), x), (C(1), x), (C(2), x)], Enumerate(atom));
            var occ = Assert.Single(query.Variables[x].Occurrences);
            Assert.Equal("a0[c0,c1,c2]", occ.ToString());
        }

        static (ColumnId, Variable)[] Enumerate(QueryAtom atom)
        {
            var list = new System.Collections.Generic.List<(ColumnId, Variable)>();
            foreach (var (c, v) in atom.VarColumns)
                list.Add((c, v));

            return list.ToArray();
        }

        [Fact]
        public void WildcardsBindNothing()
        {
            var db = Db(out var t3, out _);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            var a = qb.AddAtom(t3, [QueryEntry.Wildcard(), x, QueryEntry.Wildcard()]);
            var atom = qb.Build(new ActionId(0)).Atoms[a];
            Assert.Equal([(C(1), x)], Enumerate(atom));
            Assert.Empty(atom.FastConstraints);
            Assert.Empty(atom.SlowConstraints);
        }

        [Fact]
        public void OccurrencesAreInAtomOrder()
        {
            var db = Db(out var t3, out var f2);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            var y = qb.NewVariable();
            qb.AddAtom(f2, [x, y]);
            qb.AddAtom(t3, [y, x, y]);
            qb.AddAtom(f2, [y, new Value(1)]);
            var query = qb.Build(new ActionId(0));
            Assert.Equal(["a0[c0]", "a1[c1]"], query.Variables[x].Occurrences.Select(o => o.ToString()));
            Assert.Equal(["a0[c1]", "a1[c0,c2]", "a2[c0]"], query.Variables[y].Occurrences.Select(o => o.ToString()));
        }

        [Fact]
        public void KeysDetermineValues()
        {
            var db = Db(out var t3, out var f2);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            var y = qb.NewVariable();
            var z = qb.NewVariable();
            qb.AddAtom(f2, [x, y]);
            qb.AddAtom(f2, [new Value(1), z]);
            qb.AddAtom(f2, [QueryEntry.Wildcard(), z]);
            qb.AddAtom(t3, [x, y, z]);
            var query = qb.Build(new ActionId(0));

            // f2(x, y): x -> y. f2(1, z): the key is a constant, so the antecedent is empty and dropped (egglog).
            // f2(_, z): the key is a wildcard, so z is not determined (egglog passes the wildcard into the dependency
            // as if it were a variable).
            // t3(x, y, z): every column is a key, a dependency with an empty consequent.
            Assert.Equal(2, query.FunDeps.Count);
            Assert.Equal([x], query.FunDeps[0].Antecedent);
            Assert.Equal([y], query.FunDeps[0].Consequent);
            Assert.Equal([x, y, z], query.FunDeps[1].Antecedent);
            Assert.Empty(query.FunDeps[1].Consequent);
        }

        [Fact]
        public void UsedVariablesExcludeDefinedOnes()
        {
            var db = Db(out _, out var f2);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            var y = qb.NewVariable();
            var z = qb.NewVariable();
            qb.AddAtom(f2, [x, y]);
            qb.MarkUsed(y);
            qb.MarkUsed(x);
            qb.MarkUsed(z);
            qb.MarkDefined(z);
            var query = qb.Build(new ActionId(9));
            Assert.Equal([x, y], query.GetUsedVariables());
            Assert.Equal(new ActionId(9), query.Action);
        }

        [Fact]
        public void BadInputThrows()
        {
            var db = Db(out var t3, out var f2);
            var qb = new QueryBuilder(db);
            var x = qb.NewVariable();
            Assert.Throws<ArgumentException>(() => qb.AddAtom(f2, [x]));
            Assert.Throws<ArgumentException>(() => qb.AddAtom(f2, [x, x], [Constraint.LtConst(C(2), new Value(0))]));
            Assert.Throws<ArgumentException>(() => qb.AddAtom(f2, [x, x], [Constraint.Eq(C(0), C(5))]));
            Assert.Throws<ArgumentException>(() => qb.AddAtom(t3, [x, x, new Variable(7)]));
            Assert.Throws<ArgumentException>(() => qb.MarkUsed(new Variable(7)));
            qb.Build(new ActionId(0));
            Assert.Throws<InvalidOperationException>(() => qb.NewVariable());
            Assert.Throws<InvalidOperationException>(() => qb.Build(new ActionId(0)));
        }

        [Fact]
        public void KnobsReachTheQuery()
        {
            var db = Db(out _, out _);
            var qb = new QueryBuilder(db) { Strategy = PlanStrategy.MinCover, NoDecomposition = true };
            var query = qb.Build(new ActionId(0));
            Assert.Equal(PlanStrategy.MinCover, query.Strategy);
            Assert.True(query.NoDecomposition);
            Assert.Equal(PlanStrategy.Gj, default(PlanStrategy));
        }

    }

}
