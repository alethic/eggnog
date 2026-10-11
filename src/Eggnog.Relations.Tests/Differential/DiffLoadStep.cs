using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Stages rows into one table through a database buffer, in order, and merges: the way programs load data. Rows
    /// with the same key fold through the merge function in staging order, which the model replays exactly, so loads
    /// may target tables of any merge function.
    /// </summary>
    sealed class DiffLoadStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="rows">The rows' cells without the timestamp, which is the epoch of the load.</param>
        public DiffLoadStep(int table, uint[][] rows)
        {
            Table = table;
            Rows = rows;
        }

        /// <summary>
        /// The most rows <see cref="Summarize"/> lists; <see cref="Format"/> lists them all.
        /// </summary>
        public const int FormatLimit = 200;

        /// <summary>
        /// Gets the table.
        /// </summary>
        public int Table { get; }

        /// <summary>
        /// Gets the rows' cells without the timestamp.
        /// </summary>
        public uint[][] Rows { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var t = runner.Program.Tables[Table];
            var batch = new DiffBatch();
            foreach (var row in Rows)
                batch.Insert(Table, t.IsSorted ? [.. row, runner.Epoch] : row);

            runner.Merge(batch, "the load");
            runner.Advance();
        }

        /// <inheritdoc />
        /// <remarks>Lists every row, so that a shrunk program, which its seed does not reproduce, can be rebuilt from the
        /// listing.</remarks>
        public override string Format(DiffProgram program)
        {
            return $"load {program.Tables[Table].Name} {string.Join(" ", Rows.Select(TableTestUtil.Show))}";
        }

        /// <inheritdoc />
        /// <remarks>Lists at most <see cref="FormatLimit"/> rows; the seed reproduces the rest.</remarks>
        public override string Summarize(DiffProgram program)
        {
            var rows = string.Join(" ", Rows.Take(FormatLimit).Select(TableTestUtil.Show));
            var more = Rows.Length > FormatLimit ? $" ... ({Rows.Length} rows in all; the seed reproduces the rest)" : "";
            return $"load {program.Tables[Table].Name} {rows}{more}";
        }

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Rows.Length > 2)
            {
                yield return new DiffLoadStep(Table, Rows[..(Rows.Length / 2)]);
                yield return new DiffLoadStep(Table, Rows[(Rows.Length / 2)..]);
            }

            if (Rows.Length > 1)
                for (var i = 0; i < Rows.Length; i++)
                    yield return new DiffLoadStep(Table, Rows.Where((_, j) => j != i).ToArray());
        }

    }

}
