using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The query halves of egglog's strategy tests (core-relations/src/tests.rs:140-385: <c>line_graph_1</c>,
    /// <c>line_graph_2</c>, <c>intersection_test</c>), for every strategy: the plan, run by the reference interpreter,
    /// must produce exactly the matches the Rust tests derive. The action halves need the executor.
    /// </summary>
    public class PlannerStrategyTests
    {

        static PlanStrategy S(string strategy) => Enum.Parse<PlanStrategy>(strategy);

        static string[] Matches(PlanTestDatabase db, Query query, bool reorder)
        {
            var output = query.GetUsedVariables();
            var expected = NaiveJoin.Evaluate(db, query, output);
            var plan = QueryPlanner.PlanQuery(query, db);
            var actual = new PlanInterpreter(db, plan, reorder).Run(output);
            Assert.True(actual.SetEquals(expected), plan.ToString());
            return actual.Order().ToArray();
        }

        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void LineGraph1(string strategy)
        {
            // edge(x, y), edge(y, z) => edge(x, z) over the path 0 -> 1 -> ... -> 9.
            var db = new PlanTestDatabase();
            var edges = db.AddTable(2, 2, Enumerable.Range(0, 9).Select(i => new uint[] { (uint)i, (uint)i + 1 }));
            var qb = new QueryBuilder(db) { Strategy = S(strategy) };
            var x = qb.NewVariable("x");
            var y = qb.NewVariable("y");
            var z = qb.NewVariable("z");
            qb.AddAtom(edges, [x, y]);
            qb.AddAtom(edges, [y, z]);
            qb.MarkUsed(x);
            qb.MarkUsed(z);
            var query = qb.Build(new ActionId(0));

            var expected = Enumerable.Range(0, 8).Select(i => $"{i},{i + 2}").Order().ToArray();
            Assert.Equal(expected, Matches(db, query, false));
            Assert.Equal(expected, Matches(db, query, true));
        }

        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void LineGraph2(string strategy)
        {
            // edge(x, y), edge(y, z) => edge(x, z) :where y > 1.
            var db = new PlanTestDatabase();
            var edges = db.AddTable(2, 2, Enumerable.Range(0, 9).Select(i => new uint[] { (uint)i, (uint)i + 1 }));
            var qb = new QueryBuilder(db) { Strategy = S(strategy) };
            var x = qb.NewVariable("x");
            var y = qb.NewVariable("y");
            var z = qb.NewVariable("z");
            qb.AddAtom(edges, [x, y], [Constraint.GtConst(new ColumnId(1), new Value(1))]);
            qb.AddAtom(edges, [y, z]);
            qb.MarkUsed(x);
            qb.MarkUsed(z);
            var query = qb.Build(new ActionId(0));

            var expected = Enumerable.Range(0, 8).Where(i => i + 1 > 1).Select(i => $"{i},{i + 2}").Order().ToArray();
            Assert.Equal(expected, Matches(db, query, false));
            Assert.Equal(expected, Matches(db, query, true));
        }

        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void Intersection(string strategy)
        {
            // R(x, x), S(x, x), T(x, x), x > 5 => U(x), each of R, S, T holding (x, x) for x below 10.
            var db = new PlanTestDatabase();
            var tables = Enumerable.Range(0, 3).Select(_ => db.AddTable(2, 2, Enumerable.Range(0, 10).Select(i => new uint[] { (uint)i, (uint)i }))).ToArray();
            var qb = new QueryBuilder(db) { Strategy = S(strategy) };
            var x = qb.NewVariable("x");
            foreach (var t in tables)
                qb.AddAtom(t, [x, x], [Constraint.GtConst(new ColumnId(0), new Value(5))]);
            qb.MarkUsed(x);
            var query = qb.Build(new ActionId(0));

            Assert.Equal(["6", "7", "8", "9"], Matches(db, query, false));
            Assert.Equal(["6", "7", "8", "9"], Matches(db, query, true));
        }

    }

}
