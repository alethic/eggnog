using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs a bridge differential program three ways in lockstep, each iteration running the rules its schedule lists,
    /// and compares them after the seeds and after every iteration: the bridge with semi-naive rules, the bridge with
    /// naive rules, and the naive oracle (<see cref="BridgeDiffOracle"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The views must agree modulo renaming (<see cref="BridgeDiffComparer"/>): equivalence classes over every id, and
    /// every function's rows. Each iteration's change flag (<see cref="IterationReport.Changed"/>, whether the rules'
    /// writes changed the e-graph) must agree with the oracle's. Iterations stop at the program's budget, when nothing
    /// changes in an iteration that ran every rule and no seed is left to run (saturation), when the tables outgrow the
    /// program's row cap, before an iteration the oracle cannot enumerate within its match budget, or after a panic.
    /// </para>
    /// <para>
    /// Panics: a right-hand-side read of a function without a default at a key it lacks panics. Every evaluation must
    /// panic in the same iteration, which does not depend on the evaluation (a match whose read misses is new when it
    /// first exists, so semi-naive evaluation sees it too). What a panicking iteration merged stays and is rebuilt
    /// (<see cref="EGraph.RunRules"/>), so each e-graph must still be well formed: canonical ids, one row per key,
    /// every class derived from the seeds. The run stops there, as the oracle applies nothing of a panicking iteration.
    /// </para>
    /// <para>
    /// With <see cref="BridgeDiffProgram.Parallel"/>, both e-graphs run with parallel settings
    /// (<see cref="BridgeDiffBridge"/>). A disagreement throws <see cref="BridgeDiffMismatchException"/>; anything else
    /// the bridge throws (a validation error) propagates as it is.
    /// </para>
    /// </remarks>
    static class BridgeDiffRunner
    {

        /// <summary>
        /// Runs <paramref name="program"/>.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="stats">Receives what the program exercised.</param>
        /// <returns>The semi-naive e-graph's last view, which the others agree with.</returns>
        /// <exception cref="BridgeDiffMismatchException">The evaluations disagree.</exception>
        public static BridgeDiffView Run(BridgeDiffProgram program, BridgeDiffStats stats)
        {
            var semi = new BridgeDiffBridge(program, semiNaive: true, program.Parallel);
            var naive = new BridgeDiffBridge(program, semiNaive: false, program.Parallel);
            var oracle = new BridgeDiffOracle(program, semi.Unit);
            if (naive.Unit != semi.Unit)
                throw new BridgeDiffMismatchException($"The two e-graphs store the unit value as {semi.Unit} and {naive.Unit}.");

            stats.Programs++;
            stats.LateSeedPrograms += program.Seeds.Any(s => s.Phase > 0) ? 1 : 0;
            stats.ParallelPrograms += program.Parallel ? 1 : 0;

            BridgeDiffView? last = null;
            void Check(string where)
            {
                var expected = oracle.View();
                last = semi.View();
                BridgeDiffComparer.Compare(program, expected, last, "the oracle", "the semi-naive bridge", where);
                BridgeDiffComparer.Compare(program, expected, naive.View(), "the oracle", "the naive bridge", where);
            }

            void RunSeeds(int phase)
            {
                semi.RunSeeds(phase);
                naive.RunSeeds(phase);
                oracle.RunSeeds(phase);
            }

            RunSeeds(0);
            Check("after the seeds");
            semi.BuildRules();
            naive.BuildRules();

            for (var iteration = 1; iteration <= program.Iterations; iteration++)
            {
                if (program.Seeds.Any(s => s.Phase == iteration))
                {
                    RunSeeds(iteration);
                    Check($"after the seeds before iteration {iteration}");
                }

                if (oracle.TotalRows > program.RowCap)
                {
                    stats.Capped++;
                    break;
                }

                var scheduled = program.Schedule[iteration - 1];
                var where = $"after iteration {iteration}";
                var unionsBefore = semi.EGraph.UnionCount;
                bool? expectedChanged;
                string? oraclePanic = null;
                try
                {
                    expectedChanged = oracle.Step(scheduled);
                }
                catch (BridgeDiffPanicException e)
                {
                    expectedChanged = false;
                    oraclePanic = e.Message;
                }

                if (expectedChanged is not bool changed)
                {
                    stats.OverBudget++;
                    break;
                }

                var (semiReport, semiPanic) = Step(semi, scheduled);
                var (naiveReport, naivePanic) = Step(naive, scheduled);
                if ((oraclePanic is null) != (semiPanic is null) || (oraclePanic is null) != (naivePanic is null))
                    throw new BridgeDiffMismatchException($"{where}: the oracle {Panicked(oraclePanic)}, the semi-naive bridge {Panicked(semiPanic)}, the naive bridge {Panicked(naivePanic)}.");

                if (oraclePanic is not null)
                {
                    last = semi.View();
                    BridgeDiffComparer.Compare(program, last, last, "the semi-naive bridge", "itself", where + ", which panicked");
                    var naiveView = naive.View();
                    BridgeDiffComparer.Compare(program, naiveView, naiveView, "the naive bridge", "itself", where + ", which panicked");
                    stats.Iterations++;
                    stats.Panicked++;
                    break;
                }

                Check(where);
                if (semiReport!.Changed != changed || naiveReport!.Changed != changed)
                    throw new BridgeDiffMismatchException($"{where}: the oracle {(changed ? "changed" : "did not change")}, the semi-naive bridge reports changed {semiReport.Changed}, the naive bridge {naiveReport!.Changed}.");

                stats.Iterations++;
                stats.ChangedIterations += changed ? 1 : 0;
                stats.RebuildingIterations += semi.EGraph.UnionCount != unionsBefore ? 1 : 0;
                stats.Minted += oracle.LastMinted;
                stats.OracleMatches += oracle.LastMatches;
                stats.PartialIterations += scheduled.Length < program.Rules.Length ? 1 : 0;
                foreach (var r in scheduled.Distinct())
                {
                    stats.SemiNaiveMatches += semiReport.RuleSetReport.NumMatches(program.Rules[r].Name);
                    stats.NaiveMatches += naiveReport.RuleSetReport.NumMatches(program.Rules[r].Name);
                }

                // Saturated: every rule ran and changed nothing, and no seed is left.
                if (changed == false && scheduled.Length == program.Rules.Length && program.Seeds.All(s => s.Phase <= iteration))
                {
                    stats.Saturated++;
                    break;
                }
            }

            var final = oracle.View();
            stats.MergedIds += final.IdCount - Enumerable.Range(0, final.IdCount).Select(id => final.Find((uint)id)).Distinct().Count();
            for (var f = 0; f < program.Functions.Length; f++)
            {
                var function = program.Functions[f];
                var rows = final.Rows(f);
                stats.SubsumedRows += rows.Count(r => r.Subsumed);
                stats.KeylessRows += function.KeyCount == 0 ? rows.Count : 0;
                stats.WideRows += function.KeyCount > 3 ? rows.Count : 0;
                switch (function.Kind)
                {
                    case BridgeDiffFunctionKind.Min:
                    case BridgeDiffFunctionKind.Max:
                        stats.LatticeRows += rows.Count;
                        break;
                    case BridgeDiffFunctionKind.Relation:
                        stats.RelationRows += rows.Count;
                        break;
                }
            }

            return last!;
        }

        /// <summary>
        /// Runs one iteration of <paramref name="bridge"/>, catching a panic.
        /// </summary>
        /// <returns>The report, or the panic's message.</returns>
        static (IterationReport? Report, string? Panic) Step(BridgeDiffBridge bridge, int[] scheduled)
        {
            try
            {
                return (bridge.Step(scheduled), null);
            }
            catch (PanicException e)
            {
                return (null, e.Message);
            }
        }

        /// <summary>
        /// Describes whether an evaluation panicked, for a disagreement's message.
        /// </summary>
        static string Panicked(string? panic) => panic is null ? "did not panic" : $"panicked ({panic})";

    }

}
