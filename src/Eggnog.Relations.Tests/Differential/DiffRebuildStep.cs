using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rebuilds every rebuilding table through the union-find (<see cref="Database.ApplyRebuild"/>) until a pass
    /// changes nothing or <see cref="MaxPasses"/> passes ran, the bridge's rebuild loop: each pass canonicalizes ids,
    /// merges colliding keys through their merge functions, and unions what those stage (congruence closure). Every
    /// pass is compared with the model's, change flag and tables.
    /// </summary>
    sealed class DiffRebuildStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="maxPasses">The most passes to run.</param>
        public DiffRebuildStep(int maxPasses)
        {
            MaxPasses = maxPasses;
        }

        /// <summary>
        /// Gets the most passes to run.
        /// </summary>
        public int MaxPasses { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var tables = runner.Program.RebuildTables.Select(runner.Id).ToArray();
            var uf = runner.Id(runner.Program.UnionFind);
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                var engine = runner.Db.ApplyRebuild(uf, tables, runner.Epoch);
                var model = runner.Model.RebuildPass(runner.Epoch);
                runner.Stats.RebuildPasses++;
                if (engine)
                    runner.Stats.ChangingRebuildPasses++;
                if (engine != model)
                    runner.Fail($"rebuild pass {pass} reported changed={engine}, the model {model}");

                runner.Advance();
                if (engine == false)
                    break;

                runner.CompareAll();
            }
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"rebuild {string.Join(" ", program.RebuildTables.Select(t => program.Tables[t].Name))} (at most {MaxPasses} passes)";

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (MaxPasses > 1)
                yield return new DiffRebuildStep(1);
        }

    }

}
