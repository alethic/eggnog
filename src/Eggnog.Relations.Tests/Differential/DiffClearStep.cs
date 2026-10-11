namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Clears one table (<see cref="Database.ClearTable"/>): its rows and staged writes go, it starts a new generation
    /// if it had rows, and its cached indexes are reset. Clearing the union-find forgets every union.
    /// </summary>
    sealed class DiffClearStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        public DiffClearStep(int table)
        {
            Table = table;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public int Table { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            runner.Db.ClearTable(runner.Id(Table));
            runner.Model.Clear(Table);
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"clear {program.Tables[Table].Name}";

    }

}
