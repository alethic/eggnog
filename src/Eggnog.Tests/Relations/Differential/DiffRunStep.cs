using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs rule sets: every query under every configuration, one rule each, in one rule set per iteration, rebuilt
    /// before each iteration as egglog's loop does. Each rule's matches are checked against the oracle (distinct, with
    /// multiplicity where defined, and against the report's count); then the actions' writes, merged by the engine at
    /// the end of the run, must leave the tables the model computes from the oracle's matches, and the report's change
    /// flag must agree.
    /// </summary>
    /// <remarks>
    /// All configurations of a query share its action, and actions only write deterministic tables (see
    /// <see cref="DiffTable.Deterministic"/>), so writing the same rows once per configuration leaves the same tables
    /// as writing them once, which is what the model does.
    /// </remarks>
    sealed class DiffRunStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="queries">The queries.</param>
        /// <param name="configs">The configurations every query is planned under.</param>
        /// <param name="iterations">The number of iterations.</param>
        public DiffRunStep(DiffQuery[] queries, DiffConfig[] configs, int iterations)
        {
            Queries = queries;
            Configs = configs;
            Iterations = iterations;
        }

        /// <summary>
        /// Gets the queries.
        /// </summary>
        public DiffQuery[] Queries { get; }

        /// <summary>
        /// Gets the configurations.
        /// </summary>
        public DiffConfig[] Configs { get; }

        /// <summary>
        /// Gets the number of iterations.
        /// </summary>
        public int Iterations { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            for (var it = 0; it < Iterations; it++)
            {
                if (it > 0)
                    runner.CompareAll();

                var expected = Queries.Select(q => runner.Reference(q, q.Atoms)).ToArray();
                if (expected.All(e => e is null))
                {
                    runner.Advance();
                    continue;
                }

                var recorder = new DiffRecorder();
                var function = runner.Db.AddExternalFunction(recorder.Function);
                var rsb = runner.Db.NewRuleSet();
                var rules = new List<(int Query, DiffConfig Config, uint Tag, RuleId? Rule)>();
                var tag = 0u;
                for (var qi = 0; qi < Queries.Length; qi++)
                {
                    if (expected[qi] is null)
                        continue;

                    foreach (var config in Configs)
                    {
                        var rule = runner.AddRule(rsb, Queries[qi], config, tag, function, Array.Empty<(int, Constraint)>());
                        rules.Add((qi, config, tag, rule));
                        tag++;
                    }
                }

                using var rs = rsb.Build();
                var report = runner.Db.RunRuleSet(rs, ReportLevel.TimeOnly, runner.Epoch);

                // The model runs each query's action once, on the snapshot, before merging anything.
                var batch = new DiffBatch();
                var survivors = expected.Select((m, qi) => m is null ? null : runner.StageActions(Queries[qi], m, batch)).ToArray();
                foreach (var (qi, config, t, rule) in rules)
                {
                    var label = $"query {qi} under {config}, iteration {it}";
                    if (rule is not { } id)
                    {
                        if (expected[qi]!.Count > 0)
                            runner.Fail($"{label}: the cached plan was skipped as empty, but the oracle has {expected[qi]!.Count} matches");

                        continue;
                    }

                    runner.CheckMatches(label, rs[id], expected[qi]!, recorder.Matches(t), report.NumMatches("r" + t), Queries[qi].MultiplicityDefined(runner.Program), survivors[qi]!);
                }

                var changed = runner.Model.Merge(batch, runner.Epoch);
                if (report.Changed != changed)
                    runner.Fail($"iteration {it}: the run reported changed={report.Changed}, the model {changed}");

                runner.Db.FreeExternalFunction(function);
                runner.Advance();
            }
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program)
        {
            var b = new StringBuilder();
            b.Append("run x").Append(Iterations).Append(" under [").Append(string.Join(" ", Configs)).Append(']');
            for (var i = 0; i < Queries.Length; i++)
                b.AppendLine().Append("       q").Append(i).Append(": ").Append(Queries[i].Format(program));

            return b.ToString();
        }

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Queries.Length > 1)
                for (var i = 0; i < Queries.Length; i++)
                    yield return new DiffRunStep(Queries.Where((_, j) => j != i).ToArray(), Configs, Iterations);

            if (Iterations > 1)
                yield return new DiffRunStep(Queries, Configs, 1);

            if (Configs.Length > 1)
                foreach (var config in Configs)
                    yield return new DiffRunStep(Queries, [config], Iterations);

            for (var i = 0; i < Queries.Length; i++)
                foreach (var simpler in Queries[i].Simplify())
                    yield return new DiffRunStep(Queries.Select((q, j) => j == i ? simpler : q).ToArray(), Configs, Iterations);
        }

    }

}
