using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Removes every row of a table and re-inserts it, restamped with the epoch, in one batch per round: each round
    /// tombstones every live row, so a few rounds push the table past its compaction threshold (tombstones above half
    /// its physical rows and 16), which renumbers rows and starts a new generation that cached indexes must notice.
    /// </summary>
    sealed class DiffChurnStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table; not the union-find.</param>
        /// <param name="rounds">The number of rounds.</param>
        public DiffChurnStep(int table, int rounds)
        {
            Table = table;
            Rounds = rounds;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public int Table { get; }

        /// <summary>
        /// Gets the number of rounds.
        /// </summary>
        public int Rounds { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var t = runner.Program.Tables[Table];
            var model = runner.Model.Table(Table);
            for (var round = 0; round < Rounds; round++)
            {
                var batch = new DiffBatch();
                foreach (var row in model.Rows())
                {
                    var copy = (uint[])row.Clone();
                    if (t.IsSorted)
                        copy[t.TimestampColumn] = runner.Epoch;

                    batch.Remove(Table, model.KeyOf(row));
                    batch.Insert(Table, copy);
                }

                runner.Merge(batch, $"churn round {round}");
                runner.Advance();
            }
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"churn {program.Tables[Table].Name} x{Rounds}";

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Rounds > 1)
                yield return new DiffChurnStep(Table, 1);
        }

    }

}
