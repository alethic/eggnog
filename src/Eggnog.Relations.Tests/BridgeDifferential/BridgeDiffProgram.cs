using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A whole-program differential test of the bridge: a signature, seeds, rules and an iteration budget, generated from
    /// a seed (<see cref="BridgeDiffGenerator"/>), run by <see cref="BridgeDiffRunner"/> semi-naively, naively and through
    /// the oracle, shrunk when they disagree (<see cref="BridgeDiffShrinker"/>), and printed as the failure's
    /// reproduction.
    /// </summary>
    /// <param name="Seed">The seed it was generated from.</param>
    /// <param name="SortCount">The number of eq sorts.</param>
    /// <param name="Functions">The functions, in declaration order.</param>
    /// <param name="Seeds">The seeds, in order (their phases do not decrease).</param>
    /// <param name="Rules">The rules.</param>
    /// <param name="Schedule">The rules each iteration runs, by index, one entry per iteration: all of them, or a subset,
    /// as a schedule of rule sets would run them.</param>
    /// <param name="Iterations">The most iterations to run; fewer when the rules saturate or the tables outgrow
    /// <paramref name="RowCap"/>.</param>
    /// <param name="RowCap">The number of rows past which no further iteration runs.</param>
    /// <param name="MatchBudget">The most matches the oracle enumerates in one iteration; a program whose next iteration
    /// needs more stops before it.</param>
    /// <param name="Parallel">Whether both e-graphs use a parallel configuration (four threads, and the parallel rule-set
    /// executor whatever the database's size) rather than the serial one.</param>
    sealed record BridgeDiffProgram(int Seed, int SortCount, BridgeDiffFunction[] Functions, BridgeDiffSeed[] Seeds, BridgeDiffRule[] Rules, int[][] Schedule, int Iterations, int RowCap, int MatchBudget, bool Parallel)
    {

        /// <summary>
        /// Lists the program egglog-style: declarations, seeds (with their phases), rules (with their plan settings), and
        /// the run.
        /// </summary>
        /// <returns>The listing.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("; bridge differential program, seed ").Append(Seed).Append(Parallel ? ", e-graphs parallel" : "").AppendLine();
            foreach (var function in Functions)
                b.AppendLine(function.ToString());

            for (var i = 0; i < Seeds.Length; i++)
                b.AppendLine(Seeds[i].Format(i, Functions));

            foreach (var rule in Rules)
                b.AppendLine(rule.Format(Functions));

            if (Schedule.Take(Iterations).All(s => s.Length == Rules.Length))
            {
                b.Append("(run ").Append(Iterations).Append(')');
            }
            else
            {
                b.Append("(run-schedule");
                foreach (var step in Schedule.Take(Iterations))
                    b.Append(" (run (").AppendJoin(" ", step.Select(r => Rules[r].Name)).Append("))");

                b.Append(')');
            }

            b.Append(" ; at most ").Append(RowCap).Append(" rows, ").Append(MatchBudget).AppendLine(" matches per iteration");
            return b.ToString();
        }

    }

}
