namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A small store-backed e-graph with an extraction schema over one e-class sort <c>E</c>, for the context extraction
    /// tests (colored-contexts spec §10.10): constructors <c>leaf(i64)</c>, <c>wrap(E)</c> and <c>pair(E, E)</c>, each
    /// of cost 1; a declared custom function <c>weight(E) -&gt; i64</c>, which extraction never uses; and an undeclared
    /// function <c>mark(E) -&gt; i64</c>.
    /// </summary>
    sealed class ContextExtractionGraph
    {

        /// <summary>
        /// Initializes the graph.
        /// </summary>
        public ContextExtractionGraph()
        {
            Fx = new ExtractionFixture();
            E = new EqSort("E");
            Leaf = Fx.Constructor("leaf", E, 1, Fx.I64);
            Wrap = Fx.Constructor("wrap", E, 1, E);
            Pair = Fx.Constructor("pair", E, 1, E, E);
            Weight = Fx.Function("weight", Fx.I64, E);
            Mark = Fx.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, Fx.ColumnOf(Fx.I64)],
                Default = DefaultVal.Const(Fx.Int(0)),
                Merge = MergeFn.AssertEq,
                Name = "mark",
            });
        }

        public ExtractionFixture Fx { get; }

        public EqSort E { get; }

        public FunctionId Leaf { get; }

        public FunctionId Wrap { get; }

        public FunctionId Pair { get; }

        public FunctionId Weight { get; }

        public FunctionId Mark { get; }

        public EGraphContext Root => Fx.EGraph.Root;

        public ExtractionSchema Schema => Fx.Schema;

        /// <summary>
        /// Boxes an i64.
        /// </summary>
        public Value Int(long value) => Fx.Int(value);

        /// <summary>
        /// <c>pair(x, y) = z =&gt; pair(x, y) = z</c>: matches every pair and restates it, changing nothing.
        /// </summary>
        public EGraphRuleId RestatePairs()
        {
            var rb = Fx.EGraph.NewRule("restate-pairs", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var z = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Pair, [x, y, z], false);
            rb.Set(Pair, [x, y, z]);
            return rb.Build();
        }

        /// <summary>
        /// Extracts the cheapest term of a class and renders it.
        /// </summary>
        public string Text(Extractor extractor, Value value) => extractor.ExtractTerm(E, value).ToString();

        /// <summary>
        /// Gets the cost of the cheapest term of a class, which must have one.
        /// </summary>
        public long Cost(Extractor extractor, Value value) => extractor.TryGetCost(E, value, out var cost) ? cost : throw new System.InvalidOperationException($"No term for {value}.");

    }

}
