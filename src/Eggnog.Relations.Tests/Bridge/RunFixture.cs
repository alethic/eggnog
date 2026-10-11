using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A small e-graph: <c>num(i64)</c>, <c>add(id, id)</c> and the relation-like constructor <c>same(id, id)</c>.
    /// </summary>
    sealed class RunFixture
    {

        public RunFixture()
        {
            EGraph = NewEGraph();
            var i64 = IntType(EGraph);
            Num = Constructor(EGraph, "num", false, i64);
            Add = Constructor(EGraph, "add", true, ColumnTy.Id, ColumnTy.Id);
            Same = Constructor(EGraph, "same", false, ColumnTy.Id, ColumnTy.Id);
        }

        public EGraph EGraph { get; }

        public FunctionId Num { get; }

        public FunctionId Add { get; }

        public FunctionId Same { get; }

        public Value Number(long n) => EGraph.AddTerm(Num, [Int(EGraph, n)]);

        public Value Canon(Value v) => EGraph.GetCanonRepr(v, ColumnTy.Id);

        /// <summary>
        /// <c>same(x, y, o, t) =&gt; union(x, y)</c>, then optionally a panic.
        /// </summary>
        public RuleSet UnionRule(ExternalFunctionId? panic = null)
        {
            var rsb = new RuleSetBuilder(EGraph.Database);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var o = q.NewVariable("o");
            var t = q.NewVariable("t");
            q.AddAtom(EGraph.GetTable(Same), [x, y, o, t]);
            var rb = q.Build();
            var ts = rb.ReadCounter(EGraph.TimestampCounter);
            rb.Insert(EGraph.UnionFindTable, [x, y, ts]);
            if (panic is { } p)
                rb.CallExternal(p, []);

            rb.Build("union");
            return rsb.Build();
        }

        /// <summary>
        /// <c>add(x, y, id, t, s) =&gt; add(y, x, id, ts, 0)</c>: commutativity.
        /// </summary>
        public RuleSet CommRule()
        {
            var rsb = new RuleSetBuilder(EGraph.Database);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var id = q.NewVariable("id");
            var t = q.NewVariable("t");
            var s = q.NewVariable("s");
            q.AddAtom(EGraph.GetTable(Add), [x, y, id, t, s]);
            var rb = q.Build();
            var ts = rb.ReadCounter(EGraph.TimestampCounter);
            rb.Insert(EGraph.GetTable(Add), [y, x, id, ts, new Value(SchemaMath.NotSubsumed)]);
            rb.Build("comm");
            return rsb.Build();
        }

    }

}
