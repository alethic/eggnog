using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A slow reference for running a <see cref="RuleSet"/>, standing in for egglog's <c>run_rule_set</c>
    /// (core-relations/src/free_join/execute.rs:347-635) until the executor exists: for every rule it intersects each
    /// atom's live rows with the rule's header subsets, filters them by the atom's slow constraints, enumerates the
    /// distinct bindings by nested loops in atom order, runs the action program over them in batches through
    /// <see cref="ExecutionState.RunInstrs(Instr[], Bindings)"/>, and finally merges the database.
    /// </summary>
    /// <remarks>
    /// It reads only what a rule set provides (the header subsets of <see cref="RulePlan.Instance"/>, the atoms of the
    /// template, the action program and its used variables), so it checks that the builders produce runnable rules.
    /// Fast constraints are deliberately not re-applied: the headers must carry them. Match counts are surviving lanes
    /// over distinct full bindings, which can differ from egglog's plan-dependent counts.
    /// </remarks>
    static class NaiveRuleRunner
    {

        /// <summary>
        /// Runs every rule against the current tables, then merges.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="rules">The rule set.</param>
        /// <returns>Whether the merge changed anything, and the surviving lanes per rule.</returns>
        public static (bool Changed, int[] Matches) Run(Database db, RuleSet rules)
        {
            var matches = new int[rules.Count];
            for (var r = 0; r < rules.Count; r++)
            {
                var plan = rules[new RuleId(r)];
                if (plan.Instance is null)
                    continue;

                var action = rules.GetAction(plan.Action);
                var (bound, bindings) = Enumerate(db, plan);
                matches[r] = Apply(db, action, bound, bindings);
            }

            return (db.MergeAll(), matches);
        }

        /// <summary>
        /// Enumerates the distinct bindings of every variable an atom of <paramref name="plan"/> binds.
        /// </summary>
        public static (Variable[] Bound, List<uint[]> Bindings) Enumerate(Database db, RulePlan plan)
        {
            var template = plan.Template;
            var instance = plan.Instance ?? throw new ArgumentException("The rule is empty.", nameof(plan));
            var atoms = new List<(QueryAtom Info, List<uint[]> Rows)>();
            var boundSet = new SortedSet<int>();
            foreach (var (atom, info) in template.Atoms)
            {
                HashSet<int>? allowed = null;
                for (var h = 0; h < instance.Headers.Length; h++)
                {
                    if (instance.Headers[h].Atom != atom)
                        continue;

                    var ids = new HashSet<int>();
                    foreach (var row in instance.HeaderSubsets[h])
                        ids.Add(row);

                    if (allowed is null)
                        allowed = ids;
                    else
                        allowed.IntersectWith(ids);
                }

                var table = db.GetTable(info.Table);
                var all = table.All();
                var rows = new List<uint[]>();
                foreach (var (id, row) in TableTestUtil.DumpSubset(table, all.AsRef()))
                    if ((allowed is null || allowed.Contains(id)) && Constraint.EvaluateAll(info.SlowConstraints, row))
                        rows.Add(row);

                all.Dispose();
                atoms.Add((info, rows));
                foreach (var (_, variable) in info.VarColumns)
                    boundSet.Add(variable.Index);
            }

            var bound = new Variable[boundSet.Count];
            var k = 0;
            foreach (var v in boundSet)
                bound[k++] = new Variable(v);

            var values = new uint[template.VariableCount];
            var isBound = new bool[template.VariableCount];
            var seen = new HashSet<string>();
            var result = new List<uint[]>();
            Join(0);
            return (bound, result);

            void Join(int depth)
            {
                if (depth == atoms.Count)
                {
                    var key = new uint[bound.Length];
                    for (var i = 0; i < bound.Length; i++)
                        key[i] = values[bound[i].Index];

                    if (seen.Add(string.Join(",", key)))
                        result.Add((uint[])values.Clone());

                    return;
                }

                var (info, rows) = atoms[depth];
                var newly = new List<int>();
                foreach (var row in rows)
                {
                    var ok = true;
                    foreach (var (column, variable) in info.VarColumns)
                    {
                        var v = variable.Index;
                        if (isBound[v])
                        {
                            if (values[v] != row[column.Index])
                            {
                                ok = false;
                                break;
                            }
                        }
                        else
                        {
                            isBound[v] = true;
                            values[v] = row[column.Index];
                            newly.Add(v);
                        }
                    }

                    if (ok)
                        Join(depth + 1);

                    foreach (var v in newly)
                        isBound[v] = false;

                    newly.Clear();
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> over <paramref name="bindings"/> in batches of
        /// <see cref="Bindings.DefaultBatchSize"/>, each with a fresh execution state that is flushed afterwards, as
        /// egglog's action buffer does.
        /// </summary>
        static int Apply(Database db, ActionInfo action, Variable[] bound, List<uint[]> bindings)
        {
            var total = 0;
            for (var start = 0; start < bindings.Count; start += Bindings.DefaultBatchSize)
            {
                var batch = new Bindings();
                var end = Math.Min(bindings.Count, start + Bindings.DefaultBatchSize);
                for (var i = start; i < end; i++)
                    batch.Push(bindings[i], bound, action.UsedVariables);

                var state = db.CreateExecutionState();
                total += state.RunInstrs(action.Instrs, batch);
                state.Flush();
                db.RecordFailure(state);
            }

            return total;
        }

    }

}
