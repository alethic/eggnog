namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Continues the program on a clone of the database (<see cref="Database.Clone"/>): tables, cached indexes, strata
    /// and the union-find are copied, and the copy must behave exactly like the original from here on.
    /// </summary>
    sealed class DiffCloneStep : DiffStep
    {

        /// <inheritdoc />
        public override void Execute(DiffRunner runner) => runner.CloneDatabase();

        /// <inheritdoc />
        public override string Format(DiffProgram program) => "clone the database";

    }

}
