using System;
using System.Collections.Generic;
using System.Linq;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A small store-backed e-graph for the bridge's context tests: relations as the rule layer writes them (unit
    /// output and an assert-eq merge, which is a lattice merge, so children may write them), constructors, a lattice
    /// function, a nullary contradiction relation, and rule builders over them.
    /// </summary>
    sealed class ContextGraph
    {

        /// <summary>
        /// Initializes a serial graph, or one with the given parallelism.
        /// </summary>
        public ContextGraph(ParallelHeuristics? parallelism = null)
        {
            EGraph = new EGraph(parallelism ?? ParallelHeuristics.Serial);
            I64 = IntType(EGraph);
            UnitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default);
            Edge = Relation("edge", ColumnTy.Id, ColumnTy.Id);
            Path = Relation("path", ColumnTy.Id, ColumnTy.Id);
            Mark = Relation("mark", ColumnTy.Id);
            Num = Constructor(EGraph, "num", false, I64);
            Add = Constructor(EGraph, "add", false, ColumnTy.Id, ColumnTy.Id);
            Min = Register(EGraph, (state, args) => Math.Min(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])), state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))) is var m ? state.BaseValues.Get<Int64Codec, long>(m).Rep : null);
            Low = EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "low",
            });
            Bottom = Relation("bottom");
        }

        public EGraph EGraph { get; }

        public EGraphContext Root => EGraph.Root;

        public ColumnTy I64 { get; }

        public ColumnTy UnitType { get; }

        public Value Unit { get; }

        /// <summary>
        /// <c>edge(id, id)</c>, a relation.
        /// </summary>
        public FunctionId Edge { get; }

        /// <summary>
        /// <c>path(id, id)</c>, a relation.
        /// </summary>
        public FunctionId Path { get; }

        /// <summary>
        /// <c>mark(id)</c>, a relation.
        /// </summary>
        public FunctionId Mark { get; }

        /// <summary>
        /// <c>num(i64) -&gt; id</c>, a constructor.
        /// </summary>
        public FunctionId Num { get; }

        /// <summary>
        /// <c>add(id, id) -&gt; id</c>, a constructor.
        /// </summary>
        public FunctionId Add { get; }

        /// <summary>
        /// <c>low(id) -&gt; i64</c>, merged by the minimum (a lattice).
        /// </summary>
        public FunctionId Low { get; }

        /// <summary>
        /// <c>bottom()</c>, a nullary relation: the contradiction function when designated.
        /// </summary>
        public FunctionId Bottom { get; }

        /// <summary>
        /// The minimum of two i64 values.
        /// </summary>
        public ExternalFunctionId Min { get; }

        /// <summary>
        /// Declares a relation over <paramref name="keys"/>: unit output, constant default, assert-eq merge.
        /// </summary>
        public FunctionId Relation(string name, params ColumnTy[] keys)
        {
            return EGraph.AddTable(new FunctionConfig
            {
                Schema = [.. keys, UnitType],
                Default = DefaultVal.Const(Unit),
                Merge = MergeFn.AssertEq,
                Name = name,
            });
        }

        /// <summary>
        /// Boxes an i64.
        /// </summary>
        public Value Int(long value) => EGraph.BaseValues.Get<Int64Codec, long>(value);

        /// <summary>
        /// Stages a relation row in <paramref name="context"/> through the context API.
        /// </summary>
        public void Assert(EGraphContext context, FunctionId relation, params Value[] keys) => context.StageRow(relation, [.. keys, Unit]);

        /// <summary>
        /// <c>edge(x, y) =&gt; path(x, y)</c>.
        /// </summary>
        public EGraphRuleId EdgeToPath(bool semiNaive = true, string name = "edge-path")
        {
            var rb = EGraph.NewRule(name, semiNaive);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Edge, [x, y, rb.NewVar(UnitType)], false);
            rb.Set(Path, [x, y, RuleEntry.Const(Unit, UnitType)]);
            return rb.Build();
        }

        /// <summary>
        /// <c>path(x, y), edge(y, z) =&gt; path(x, z)</c>.
        /// </summary>
        public EGraphRuleId Transitive(bool semiNaive = true, string name = "transitive")
        {
            var rb = EGraph.NewRule(name, semiNaive);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var z = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Path, [x, y, rb.NewVar(UnitType)], false);
            rb.QueryTable(Edge, [y, z, rb.NewVar(UnitType)], false);
            rb.Set(Path, [x, z, RuleEntry.Const(Unit, UnitType)]);
            return rb.Build();
        }

        /// <summary>
        /// <c>edge(x, y) =&gt; union(x, y)</c>.
        /// </summary>
        public EGraphRuleId UnionEdges(string name = "union-edges")
        {
            var rb = EGraph.NewRule(name, true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Edge, [x, y, rb.NewVar(UnitType)], false);
            rb.Union(x, y);
            return rb.Build();
        }

        /// <summary>
        /// <c>mark(x) =&gt; bottom()</c>.
        /// </summary>
        public EGraphRuleId MarkIsBottom(string name = "mark-bottom")
        {
            var rb = EGraph.NewRule(name, true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Mark, [x, rb.NewVar(UnitType)], false);
            rb.Set(Bottom, [RuleEntry.Const(Unit, UnitType)]);
            return rb.Build();
        }

        /// <summary>
        /// Lists the rows of <paramref name="function"/> that <paramref name="context"/> sees, as sorted text.
        /// </summary>
        public static string[] Rows(EGraphContext context, FunctionId function)
        {
            var rows = new List<string>();
            context.ForEach(function, row => rows.Add(string.Join(" ", row.Values.ToArray().Select(v => v.Rep))));
            rows.Sort(StringComparer.Ordinal);
            return rows.ToArray();
        }

        /// <summary>
        /// Lists the rows of a relation as sorted key text, without the unit output.
        /// </summary>
        public static string[] Keys(EGraphContext context, FunctionId relation)
        {
            var rows = new List<string>();
            context.ForEach(relation, row => rows.Add(string.Join(" ", row.Values[..^1].ToArray().Select(v => v.Rep))));
            rows.Sort(StringComparer.Ordinal);
            return rows.ToArray();
        }

    }

}
