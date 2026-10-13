using System;
using System.Linq;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The e-graph a <see cref="RandomRule"/> program runs on: the constructor <c>f(id) -&gt; id</c>, the relation
    /// <c>r(id, id)</c> (its output is the unit value, its default inserts it, and it can subsume, so its atoms carry a
    /// subsumption entry), <c>low(id) -&gt; value</c> merged by the minimum of the raw values (a lattice), a fixed pool of
    /// fresh ids, a pool of values, and the external function behind the rules' "different" filter. Two programs created
    /// alike hold the same ids and values.
    /// </summary>
    sealed class RuleProgram
    {

        /// <summary>
        /// The number of fresh ids in the pool.
        /// </summary>
        public const int IdCount = 16;

        /// <summary>
        /// Initializes the e-graph with <paramref name="parallelism"/>.
        /// </summary>
        public RuleProgram(ParallelHeuristics parallelism)
        {
            EGraph = new EGraph(parallelism);
            var i64 = IntType(EGraph);
            ValueType = i64;
            UnitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default).Rep;
            var min = Register(EGraph, (_, args) => Math.Min(args[0], args[1]));
            Distinct = Register(EGraph, (_, args) => args[0] != args[1] ? 0u : null);
            Functions =
            [
                Constructor(EGraph, "f", false, ColumnTy.Id),
                EGraph.AddTable(new FunctionConfig
                {
                    Schema = [ColumnTy.Id, ColumnTy.Id, UnitType],
                    Default = DefaultVal.Const(new Value(Unit)),
                    Merge = MergeFn.AssertEq,
                    Name = "r",
                    CanSubsume = true,
                }),
                EGraph.AddTable(new FunctionConfig
                {
                    Schema = [ColumnTy.Id, i64],
                    Default = DefaultVal.Fail,
                    Merge = MergeFn.Primitive(min, MergeFn.Old, MergeFn.New).AsLattice(),
                    Name = "low",
                }),
            ];
            Tables =
            [
                new ReferenceTable("f", 1, [true, true], true),
                new ReferenceTable("r", 2, [true, true, false], true),
                new ReferenceTable("low", 1, [true, false], true, Math.Min),
            ];
            Ids = Enumerable.Range(0, IdCount).Select(_ => EGraph.FreshId().Rep).ToArray();
            Values = Enumerable.Range(0, 6).Select(i => Int(EGraph, i * 3).Rep).ToArray();
        }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets <c>f</c>, <c>r</c> and <c>low</c>.
        /// </summary>
        public FunctionId[] Functions { get; }

        /// <summary>
        /// Gets the reference schemas of <see cref="Functions"/>.
        /// </summary>
        public ReferenceTable[] Tables { get; }

        /// <summary>
        /// Gets the ids the facts and the rules' constants use.
        /// </summary>
        public uint[] Ids { get; }

        /// <summary>
        /// Gets the values <c>low</c> holds.
        /// </summary>
        public uint[] Values { get; }

        /// <summary>
        /// Gets the column type of <c>low</c>'s output.
        /// </summary>
        public ColumnTy ValueType { get; }

        /// <summary>
        /// Gets the column type of <c>r</c>'s output.
        /// </summary>
        public ColumnTy UnitType { get; }

        /// <summary>
        /// Gets the unit value, <c>r</c>'s only output.
        /// </summary>
        public uint Unit { get; }

        /// <summary>
        /// Gets the external function that produces a value only when its two arguments differ.
        /// </summary>
        public ExternalFunctionId Distinct { get; }

    }

}
