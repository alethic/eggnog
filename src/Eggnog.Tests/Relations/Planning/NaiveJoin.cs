using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The reference semantics of a conjunctive query: a join over the atoms in id order, every row checked against
    /// all of the atom's constraints and the variables bound so far, projected onto the output variables. After each
    /// atom the partial assignments are projected onto the variables still needed and deduplicated, which keeps
    /// Cartesian products of disconnected atoms small without changing the result.
    /// </summary>
    static class NaiveJoin
    {

        const uint Unbound = uint.MaxValue;

        public static HashSet<string> Evaluate(PlanTestDatabase db, Query query, Variable[] output)
        {
            var atoms = new List<QueryAtom>();
            foreach (var (_, atom) in query.Atoms)
                atoms.Add(atom);

            var slots = query.Variables.IdCount;
            var partials = new List<uint[]> { Enumerable.Repeat(Unbound, slots).ToArray() };
            for (var i = 0; i < atoms.Count; i++)
            {
                var atom = atoms[i];
                var table = db[atom.Table];
                var next = new List<uint[]>();
                foreach (var partial in partials)
                {
                    for (var r = 0; r < table.Rows.Count; r++)
                    {
                        if (table.Matches(r, atom.FastConstraints) == false || table.Matches(r, atom.SlowConstraints) == false)
                            continue;

                        var row = table.Rows[r];
                        var extended = (uint[])partial.Clone();
                        var ok = true;
                        foreach (var (column, variable) in atom.VarColumns)
                        {
                            var value = row[column.Index];
                            if (extended[variable.Index] == Unbound)
                            {
                                extended[variable.Index] = value;
                            }
                            else if (extended[variable.Index] != value)
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (ok)
                            next.Add(extended);
                    }
                }

                // Keep only the variables a later atom or the output still needs.
                var needed = new bool[slots];
                foreach (var v in output)
                    needed[v.Index] = true;
                for (var j = i + 1; j < atoms.Count; j++)
                    foreach (var (_, v) in atoms[j].VarColumns)
                        needed[v.Index] = true;

                var seen = new HashSet<string>();
                partials = new List<uint[]>();
                foreach (var p in next)
                {
                    for (var k = 0; k < slots; k++)
                        if (needed[k] == false)
                            p[k] = Unbound;

                    if (seen.Add(string.Join(",", p)))
                        partials.Add(p);
                }
            }

            return partials.Select(p => string.Join(",", output.Select(v => p[v.Index]))).ToHashSet();
        }

    }

}
