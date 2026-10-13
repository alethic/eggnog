namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The database of the <see cref="RuleBuilderTests"/>: <c>f(key) -&gt; value</c> (t0, one key column) and
    /// <c>g(a, b, c)</c> (t1, two key columns), a counter, an external function that adds one to its first argument
    /// and a fallback that returns 100.
    /// </summary>
    sealed class RuleBuilderFixture
    {

        public RuleBuilderFixture()
        {
            Db = new Database(ParallelHeuristics.Serial);
            F = Db.AddTable(QueryTestUtil.Strict(1, 2), name: "f");
            G = Db.AddTable(QueryTestUtil.Strict(2, 3), name: "g");
            Counter = Db.AddCounter();
            Ext = Db.AddExternalFunction(QueryTestUtil.Function(args => args.Length == 0 ? 0 : args[0] + 1));
            Fallback = Db.AddExternalFunction(QueryTestUtil.Function(_ => 100));
            DatabaseTestUtil.Stage(Db, F, [1, 10], [2, 20], [3, 30]);
            DatabaseTestUtil.Stage(Db, G, [1, 2, 3], [2, 3, 4]);
            Db.MergeAll();
        }

        public Database Db { get; }

        public TableId F { get; }

        public TableId G { get; }

        public CounterId Counter { get; }

        public ExternalFunctionId Ext { get; }

        public ExternalFunctionId Fallback { get; }

    }

}
