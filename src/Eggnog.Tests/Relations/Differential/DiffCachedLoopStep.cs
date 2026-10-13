using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Plans one query once per configuration and keeps the cached plans (<see cref="RuleSet.BuildCachedPlan"/>) across
    /// rounds, as egglog-bridge keeps a rule's plans across iterations: each round instantiates every cached plan
    /// against the tables as they are then (<see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>), optionally restricted
    /// to the rows of one sorted atom that are new since the previous round, runs them, and then runs one interleaved
    /// step (loads, removals, unions, rebuilds, churn, clears, cloning), so a plan made earlier must keep working after
    /// compaction, new generations, cleared tables and a cloned database.
    /// </summary>
    sealed class DiffCachedLoopStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <param name="configs">The configurations it is planned under; all are cached.</param>
        /// <param name="focus">Per round, the atom restricted to rows new since the previous round, or -1 for none (the
        /// restriction applies only when the atom's table is sorted).</param>
        /// <param name="between">The steps run after each round but the last.</param>
        public DiffCachedLoopStep(DiffQuery query, DiffConfig[] configs, int[] focus, DiffStep[] between)
        {
            Query = query;
            Configs = configs;
            Focus = focus;
            Between = between;
        }

        /// <summary>
        /// Gets the query.
        /// </summary>
        public DiffQuery Query { get; }

        /// <summary>
        /// Gets the configurations.
        /// </summary>
        public DiffConfig[] Configs { get; }

        /// <summary>
        /// Gets, per round, the atom restricted to new rows, or -1.
        /// </summary>
        public int[] Focus { get; }

        /// <summary>
        /// Gets the steps run between rounds.
        /// </summary>
        public DiffStep[] Between { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var program = runner.Program;
            var recorder = new DiffRecorder();
            var function = runner.Db.AddExternalFunction(recorder.Function);

            // Plan once per configuration; the tags are fixed in the cached actions.
            var scratch = runner.Db.NewRuleSet();
            var planned = new RuleId[Configs.Length];
            for (var c = 0; c < Configs.Length; c++)
                planned[c] = runner.AddRule(scratch, Query, Configs[c] with { Cached = false }, (uint)c, function, Array.Empty<(int, Constraint)>())!.Value;

            CachedPlan[] cached;
            using (var rs0 = scratch.Build())
                cached = planned.Select(rs0.BuildCachedPlan).ToArray();

            runner.Stats.CachedRules += Configs.Length;
            var previous = 0u;
            for (var round = 0; round < Focus.Length; round++)
            {
                if (round > 0)
                {
                    Between[round - 1].Execute(runner);
                    runner.CompareAll();

                    // The tags repeat every round. (A clone of the database shares the recorder: clones share their
                    // external functions.)
                    recorder.Clear();
                }

                var extras = new List<(int Atom, Constraint Constraint)>();
                var focus = Focus[round];
                if (focus >= 0 && focus < Query.Atoms.Length && program.Tables[Query.Atoms[focus].Table].IsSorted)
                    extras.Add((focus, Constraint.GeConst(new ColumnId(program.Tables[Query.Atoms[focus].Table].TimestampColumn), new Value(previous))));

                var expected = runner.Reference(Query, Query.Atoms.Select((a, i) => a.WithConstraints(extras.Where(x => x.Atom == i).Select(x => x.Constraint).ToArray())).ToArray());
                previous = runner.Epoch;
                if (expected is null)
                {
                    runner.Advance();
                    continue;
                }

                var rsb = runner.Db.NewRuleSet();
                var rules = cached.Select(p => rsb.AddRuleFromCachedPlan(p, extras.Select(x => (new AtomId(x.Atom), x.Constraint)).ToArray())).ToArray();
                using var rs = rsb.Build();
                var report = runner.Db.RunRuleSet(rs, ReportLevel.TimeOnly, runner.Epoch);
                var batch = new DiffBatch();
                var survivors = runner.StageActions(Query, expected, batch);
                for (var c = 0; c < Configs.Length; c++)
                {
                    var label = $"cached plan under {Configs[c]}, round {round}" + (extras.Count > 0 ? $", atom {focus} new since epoch {extras[0].Constraint.Constant}" : "");
                    if (rules[c] is not { } id)
                    {
                        if (expected.Count > 0)
                            runner.Fail($"{label}: the cached plan was skipped as empty, but the oracle has {expected.Count} matches");

                        continue;
                    }

                    runner.CheckMatches(label, rs[id], expected, recorder.Matches((uint)c), report.NumMatches("r" + c), Query.MultiplicityDefined(program), survivors);
                }

                var changed = runner.Model.Merge(batch, runner.Epoch);
                if (report.Changed != changed)
                    runner.Fail($"round {round}: the run reported changed={report.Changed}, the model {changed}");

                runner.Advance();
            }

            runner.Db.FreeExternalFunction(function);
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program)
        {
            var b = new StringBuilder();
            b.Append("cached loop under [").Append(string.Join(" ", Configs)).Append("] focus [").Append(string.Join(" ", Focus)).Append("]: ").Append(Query.Format(program));
            for (var i = 0; i < Between.Length; i++)
                b.AppendLine().Append("       then ").Append(Between[i].Format(program));

            return b.ToString();
        }

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            for (var r = Focus.Length - 1; r >= 1; r--)
                yield return new DiffCachedLoopStep(Query, Configs, Focus[..r], Between[..(r - 1)]);

            if (Configs.Length > 1)
                foreach (var config in Configs)
                    yield return new DiffCachedLoopStep(Query, [config], Focus, Between);

            for (var r = 0; r < Focus.Length; r++)
                if (Focus[r] >= 0)
                    yield return new DiffCachedLoopStep(Query, Configs, Focus.Select((f, i) => i == r ? -1 : f).ToArray(), Between);

            for (var i = 0; i < Between.Length; i++)
                foreach (var simpler in Between[i].Simplify(program))
                    yield return new DiffCachedLoopStep(Query, Configs, Focus, Between.Select((s, j) => j == i ? simpler : s).ToArray());

            foreach (var simpler in Query.Simplify())
                yield return new DiffCachedLoopStep(simpler, Configs, Focus, Between);
        }

    }

}
