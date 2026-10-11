using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Builds some of a table's cached indexes now (<see cref="TableInfo.GetColumnIndex"/>,
    /// <see cref="TableInfo.GetIndex"/>), as earlier rule runs would, so that later writes find them armed and they
    /// refresh incrementally, listing tombstones and superseded rows that every reader must skip. Changes no rows.
    /// </summary>
    sealed class DiffWarmStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The column lists: one column builds a column index, more a tuple index.</param>
        public DiffWarmStep(int table, int[][] columns)
        {
            Table = table;
            Columns = columns;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public int Table { get; }

        /// <summary>
        /// Gets the column lists.
        /// </summary>
        public int[][] Columns { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var info = runner.Db.GetTableInfo(runner.Id(Table));
            foreach (var columns in Columns)
            {
                if (columns.Length == 1)
                    info.GetColumnIndex(new ColumnId(columns[0]));
                else
                    info.GetIndex(columns.Select(c => new ColumnId(c)).ToArray());
            }
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"warm {program.Tables[Table].Name} {string.Join(" ", Columns.Select(cs => "[" + string.Join(",", cs.Select(c => "c" + c)) + "]"))}";

    }

}
