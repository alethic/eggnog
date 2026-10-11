using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Random small databases and conjunctive queries for differential planner tests: constants, wildcards, repeated
    /// variables, functional dependencies (tables whose key is a strict prefix), fast and slow constraints, atoms
    /// without variables, and a random set of variables read by the action.
    /// </summary>
    static class RandomQueries
    {

        public static (PlanTestDatabase Db, Query Query) Generate(Random rng, int maxAtoms = 6, int maxVars = 6, int maxArity = 3, int prefixKeyOdds = 3, int maxRows = 12)
        {
            var db = new PlanTestDatabase();
            var tableCount = rng.Next(2, 5);
            var tables = new List<TableId>();
            for (var t = 0; t < tableCount; t++)
            {
                var arity = rng.Next(1, maxArity + 1);
                var keyCount = arity > 1 && rng.Next(prefixKeyOdds) == 0 ? rng.Next(1, arity) : arity;
                var sortColumn = rng.Next(4) == 0 ? rng.Next(arity) : -1;
                var uncacheable = new bool[arity];
                for (var c = 0; c < arity; c++)
                    uncacheable[c] = rng.Next(5) == 0;

                var rows = new List<uint[]>();
                var rowCount = rng.Next(0, maxRows + 1);
                for (var r = 0; r < rowCount; r++)
                {
                    var row = new uint[arity];
                    for (var c = 0; c < arity; c++)
                        row[c] = (uint)rng.Next(4);
                    rows.Add(row);
                }

                tables.Add(db.AddTable(arity, keyCount, rows, sortColumn, uncacheable));
            }

            var qb = new QueryBuilder(db);
            var varCount = rng.Next(1, maxVars + 1);
            var vars = new Variable[varCount];
            for (var v = 0; v < varCount; v++)
                vars[v] = qb.NewVariable();

            var appearing = new HashSet<Variable>();
            var atomCount = rng.Next(1, maxAtoms + 1);
            for (var a = 0; a < atomCount; a++)
            {
                var table = tables[rng.Next(tables.Count)];
                var arity = db.GetArity(table);
                var entries = new QueryEntry[arity];
                for (var c = 0; c < arity; c++)
                {
                    var roll = rng.Next(20);
                    if (roll < 15)
                    {
                        entries[c] = vars[rng.Next(varCount)];
                        appearing.Add(entries[c].Variable);
                    }
                    else if (roll < 18)
                    {
                        entries[c] = new Value((uint)rng.Next(4));
                    }
                    else
                    {
                        entries[c] = QueryEntry.Wildcard();
                    }
                }

                var constraints = new List<Constraint>();
                if (rng.Next(6) == 0)
                {
                    var col = new ColumnId(rng.Next(arity));
                    var val = new Value((uint)rng.Next(4));
                    constraints.Add(rng.Next(6) switch
                    {
                        0 => Constraint.EqConst(col, val),
                        1 => Constraint.LtConst(col, val),
                        2 => Constraint.GtConst(col, val),
                        3 => Constraint.LeConst(col, val),
                        4 => Constraint.GeConst(col, val),
                        _ => Constraint.Eq(col, new ColumnId(rng.Next(arity))),
                    });
                }

                qb.AddAtom(table, entries, constraints.ToArray());
            }

            foreach (var v in appearing)
                if (rng.Next(2) == 0)
                    qb.MarkUsed(v);

            return (db, qb.Build(new ActionId(0)));
        }

    }

}
