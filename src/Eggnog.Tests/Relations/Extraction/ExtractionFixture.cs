using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An e-graph with an extraction schema, and helpers that declare a function in both at once and build terms the
    /// way egglog's frontend does (keys canonicalized, as its globals and expressions evaluate to canonical ids).
    /// </summary>
    sealed class ExtractionFixture
    {

        readonly Dictionary<Sort, ColumnTy> baseColumns = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Initializes a serial e-graph with the i64, bool, f64 and String base sorts registered.
        /// </summary>
        public ExtractionFixture()
        {
            EGraph = BridgeTestUtil.NewEGraph();
            Schema = new ExtractionSchema(EGraph);
            I64 = new BaseSort<Int64Codec, long>("i64");
            Bool = new BaseSort<BooleanCodec, bool>("bool");
            F64 = new BaseSort<DoubleCodec, double>("f64");
            String = new BaseSort<StringCodec, string>("String");
            baseColumns.Add(I64, ColumnTy.Base(EGraph.BaseValues.RegisterType<Int64Codec, long>()));
            baseColumns.Add(Bool, ColumnTy.Base(EGraph.BaseValues.RegisterType<BooleanCodec, bool>()));
            baseColumns.Add(F64, ColumnTy.Base(EGraph.BaseValues.RegisterType<DoubleCodec, double>()));
            baseColumns.Add(String, ColumnTy.Base(EGraph.BaseValues.RegisterType<StringCodec, string>()));
        }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the extraction schema over <see cref="EGraph"/>.
        /// </summary>
        public ExtractionSchema Schema { get; }

        /// <summary>
        /// Gets the <c>i64</c> base sort.
        /// </summary>
        public BaseSort<Int64Codec, long> I64 { get; }

        /// <summary>
        /// Gets the <c>bool</c> base sort.
        /// </summary>
        public BaseSort<BooleanCodec, bool> Bool { get; }

        /// <summary>
        /// Gets the <c>f64</c> base sort.
        /// </summary>
        public BaseSort<DoubleCodec, double> F64 { get; }

        /// <summary>
        /// Gets the <c>String</c> base sort.
        /// </summary>
        public BaseSort<StringCodec, string> String { get; }

        /// <summary>
        /// Gets the bridge column type of a sort.
        /// </summary>
        public ColumnTy ColumnOf(Sort sort) => sort.Kind == SortKind.Base ? baseColumns[sort] : ColumnTy.Id;

        /// <summary>
        /// Declares a constructor (fresh-id default, union-id merge, subsumable) in the e-graph and the schema.
        /// </summary>
        public FunctionId Constructor(string name, EqSort output, long? cost, params Sort[] inputs)
        {
            return ConstructorWith(name, output, new FunctionSignature { Inputs = inputs, Output = output, IsConstructor = true, Cost = cost });
        }

        /// <summary>
        /// Declares a constructor with a custom signature (flags, term constructor) in the e-graph and the schema.
        /// </summary>
        public FunctionId ConstructorWith(string name, EqSort output, FunctionSignature signature)
        {
            var function = EGraph.AddTable(new FunctionConfig
            {
                Schema = [.. signature.Inputs.Select(ColumnOf), ColumnOf(output)],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = name,
                CanSubsume = true,
            });
            Schema.Declare(function, signature);
            return function;
        }

        /// <summary>
        /// Declares a custom function (failing default, assert-equal merge), which extraction never extracts from.
        /// </summary>
        public FunctionId Function(string name, Sort output, params Sort[] inputs)
        {
            var function = EGraph.AddTable(new FunctionConfig
            {
                Schema = [.. inputs.Select(ColumnOf), ColumnOf(output)],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = name,
            });
            Schema.Declare(function, new FunctionSignature { Inputs = inputs, Output = output });
            return function;
        }

        /// <summary>
        /// Adds the term <paramref name="function"/>(<paramref name="keys"/>), canonicalizing id keys first, and
        /// returns its canonical id.
        /// </summary>
        public Value Make(FunctionId function, params Value[] keys)
        {
            var schema = EGraph.GetFunctionInfo(function).Schema;
            var canonical = new Value[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                canonical[i] = EGraph.GetCanonRepr(keys[i], schema[i]);

            return EGraph.AddTerm(function, canonical);
        }

        /// <summary>
        /// Sets a custom function's output for a key.
        /// </summary>
        public void Set(FunctionId function, Value[] keys, Value output) => EGraph.AddValues([(function, [.. keys, output])]);

        /// <summary>
        /// Looks up a function's output for a key, which must exist.
        /// </summary>
        public Value Get(FunctionId function, params Value[] keys) => EGraph.LookupId(function, keys) ?? throw new InvalidOperationException("No such row.");

        /// <summary>
        /// Unions two ids and rebuilds.
        /// </summary>
        public void Union(Value a, Value b) => BridgeTestUtil.Union(EGraph, a, b);

        /// <summary>
        /// Gets the canonical id of an id.
        /// </summary>
        public Value Canon(Value id) => EGraph.GetCanonRepr(id, ColumnTy.Id);

        /// <summary>
        /// Marks the row of <paramref name="function"/> with key <paramref name="keys"/> subsumed, as egglog's
        /// <c>subsume</c> command does.
        /// </summary>
        public void Subsume(FunctionId function, params Value[] keys)
        {
            var schema = EGraph.GetFunctionInfo(function).Schema;
            var key = new uint[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                key[i] = EGraph.GetCanonRepr(keys[i], schema[i]).Rep;

            var action = new TableAction(EGraph, function);
            EGraph.WithExecutionState(state =>
            {
                action.Subsume(state, key);
                return 0;
            });
            EGraph.FlushUpdates();
        }

        /// <summary>
        /// Boxes an <c>i64</c>.
        /// </summary>
        public Value Int(long value) => EGraph.BaseValues.Get<Int64Codec, long>(value);

        /// <summary>
        /// Boxes a <c>bool</c>.
        /// </summary>
        public Value Boolean(bool value) => EGraph.BaseValues.Get<BooleanCodec, bool>(value);

        /// <summary>
        /// Boxes an <c>f64</c>.
        /// </summary>
        public Value Float(double value) => EGraph.BaseValues.Get<DoubleCodec, double>(value);

        /// <summary>
        /// Boxes a <c>String</c>.
        /// </summary>
        public Value Str(string value) => EGraph.BaseValues.Get<StringCodec, string>(value);

        /// <summary>
        /// Extracts with a fresh root-sort extractor and renders the term, or returns <c>null</c> when there is none.
        /// </summary>
        public (string Text, long Cost)? Extract(Sort sort, Value value, CostModel? costs = null)
        {
            var extractor = Extractor.ComputeCostsFromRootSorts(Schema, [sort], costs);
            var dag = new TermDag();
            return extractor.TryExtractBest(sort, value, dag, out var result) ? (dag.ToString(result.Term), result.Cost) : null;
        }

    }

}
