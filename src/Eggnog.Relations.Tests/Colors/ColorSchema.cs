using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The store-backed e-graph the rule differentials of colored-contexts spec §17 M10.3 run over, with each function's
    /// logical shape as a <see cref="SpikeTable"/>, so that <see cref="ReferenceRuleContext"/> (through
    /// <see cref="SpikeReferenceView"/>) and the engine exchange the same rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Functions, by index into <see cref="Tables"/>: the relations <c>edge(id, id)</c>, <c>path(id, id)</c> and
    /// <c>mark(id)</c> (unit output, assert-eq merge, writable in children, rule-layer D27); the lattice
    /// <c>low(id) → i64</c> merged by the minimum; and the constructors <c>num(i64) → id</c>, <c>add(id, id) → id</c> and
    /// <c>wrap(id) → id</c> (fresh-id default, union-id merge). Values stay below 2³¹, so an i64's cell is the number
    /// itself and the reference merges cells directly. The relation <c>answer(id, id)</c> receives the conjunctive queries
    /// of <see cref="ContextRuleCopyTests"/>; no program rule reads or writes it.
    /// </para>
    /// <para>
    /// Primitives: <see cref="Ne"/> and <see cref="Less"/> keep a match whose two ids differ or are ordered (the history
    /// family's non-monotone filters; <see cref="Less"/> depends on id order); <see cref="LowOr"/> reads <c>low</c> through
    /// the running context's view (<see cref="TableAction"/>), returning <see cref="LowDefault"/> for a missing row, and
    /// says so (<see cref="ExternalFunction.ReadsTables"/>), so a rule that calls it never inherits a watermark (X41).
    /// </para>
    /// </remarks>
    sealed class ColorSchema
    {

        /// <summary>
        /// The index of <c>edge</c> in <see cref="Tables"/>.
        /// </summary>
        public const int Edge = 0;

        /// <summary>
        /// The index of <c>path</c>.
        /// </summary>
        public const int Path = 1;

        /// <summary>
        /// The index of <c>mark</c>.
        /// </summary>
        public const int Mark = 2;

        /// <summary>
        /// The index of <c>low</c>.
        /// </summary>
        public const int Low = 3;

        /// <summary>
        /// The index of <c>num</c>.
        /// </summary>
        public const int Num = 4;

        /// <summary>
        /// The index of <c>add</c>.
        /// </summary>
        public const int Add = 5;

        /// <summary>
        /// The index of <c>wrap</c>.
        /// </summary>
        public const int Wrap = 6;

        /// <summary>
        /// The index of <c>answer</c>, which only the conjunctive queries write.
        /// </summary>
        public const int Answer = 7;

        /// <summary>
        /// What <see cref="LowOr"/> returns for an id without a <c>low</c> row.
        /// </summary>
        public const ulong LowDefault = 7;

        /// <summary>
        /// The tables' names, by index.
        /// </summary>
        public static readonly string[] Names = ["edge", "path", "mark", "low", "num", "add", "wrap", "answer"];

        /// <summary>
        /// Whether each logical column of each table holds an id, by index: keys, then a function's output.
        /// </summary>
        public static readonly bool[][] IdColumns = [[true, true], [true, true], [true], [true, false], [false, true], [true, true, true], [true, true], [true, true]];

        /// <summary>
        /// The number of key columns of each table, by index.
        /// </summary>
        public static readonly int[] KeyCounts = [2, 2, 1, 1, 1, 2, 1, 2];

        /// <summary>
        /// Initializes the e-graph and its functions, serial unless <paramref name="parallelism"/> says otherwise.
        /// </summary>
        public ColorSchema(ParallelHeuristics? parallelism = null)
        {
            EGraph = new EGraph(parallelism ?? ParallelHeuristics.Serial);
            I64 = BridgeTestUtil.IntType(EGraph);
            UnitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default);

            var min = BridgeTestUtil.Register(EGraph, (state, args) => Math.Min(args[0], args[1]));
            var functions = new FunctionId[8];
            functions[Edge] = Relation("edge", 2);
            functions[Path] = Relation("path", 2);
            functions[Mark] = Relation("mark", 1);
            functions[Low] = EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "low",
            });
            functions[Num] = BridgeTestUtil.Constructor(EGraph, "num", false, I64);
            functions[Add] = BridgeTestUtil.Constructor(EGraph, "add", false, ColumnTy.Id, ColumnTy.Id);
            functions[Wrap] = BridgeTestUtil.Constructor(EGraph, "wrap", false, ColumnTy.Id);
            functions[Answer] = Relation("answer", 2);
            Functions = functions;

            Tables =
            [
                new SpikeTable("edge", EGraph.GetTable(functions[Edge]), null, [true, true], null, null),
                new SpikeTable("path", EGraph.GetTable(functions[Path]), null, [true, true], null, null),
                new SpikeTable("mark", EGraph.GetTable(functions[Mark]), null, [true], null, null),
                new SpikeTable("low", EGraph.GetTable(functions[Low]), null, [true], false, Math.Min),
                new SpikeTable("num", EGraph.GetTable(functions[Num]), null, [false], true, null),
                new SpikeTable("add", EGraph.GetTable(functions[Add]), null, [true, true], true, null),
                new SpikeTable("wrap", EGraph.GetTable(functions[Wrap]), null, [true], true, null),
                new SpikeTable("answer", EGraph.GetTable(functions[Answer]), null, [true, true], null, null),
            ];

            Ne = BridgeTestUtil.Register(EGraph, (state, args) => args[0] != args[1] ? args[0] : null);
            Less = BridgeTestUtil.Register(EGraph, (state, args) => args[0] < args[1] ? args[0] : null);
            Keep = BridgeTestUtil.Register(EGraph, (state, args) => args[0]);
            var lowAction = new TableAction(EGraph, functions[Low]);
            LowOr = EGraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (lowAction.TryLookup(state, args, out result) == false)
                    result = (uint)LowDefault;

                return true;
            }, readsTables: true));
        }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the root context.
        /// </summary>
        public EGraphContext Root => EGraph.Root;

        /// <summary>
        /// Gets the i64 column type.
        /// </summary>
        public ColumnTy I64 { get; }

        /// <summary>
        /// Gets the unit column type of the relations' outputs.
        /// </summary>
        public ColumnTy UnitType { get; }

        /// <summary>
        /// Gets the unit value.
        /// </summary>
        public Value Unit { get; }

        /// <summary>
        /// Gets the functions, by table index.
        /// </summary>
        public IReadOnlyList<FunctionId> Functions { get; }

        /// <summary>
        /// Gets each function's logical shape, by table index.
        /// </summary>
        public IReadOnlyList<SpikeTable> Tables { get; }

        /// <summary>
        /// Gets the primitive keeping a match whose two ids differ.
        /// </summary>
        public ExternalFunctionId Ne { get; }

        /// <summary>
        /// Gets the primitive keeping a match whose first id is smaller than its second.
        /// </summary>
        public ExternalFunctionId Less { get; }

        /// <summary>
        /// Gets the primitive that keeps every match and returns its first argument: called on every body variable, it
        /// makes the executor enumerate distinct full bindings, so match counts do not depend on the plan.
        /// </summary>
        public ExternalFunctionId Keep { get; }

        /// <summary>
        /// Gets the primitive returning <c>low(x)</c> in the running context, or <see cref="LowDefault"/>.
        /// </summary>
        public ExternalFunctionId LowOr { get; }

        /// <summary>
        /// Gets whether table <paramref name="table"/> is a relation (no output column in its logical rows).
        /// </summary>
        public bool IsRelation(int table) => Tables[table].HasOutput == false;

        /// <summary>
        /// Gets whether table <paramref name="table"/> is a constructor.
        /// </summary>
        public bool IsConstructor(int table) => Tables[table].OutputIsId;

        /// <summary>
        /// Gets the index of the logical table <paramref name="table"/>.
        /// </summary>
        public int IndexOf(SpikeTable table)
        {
            for (var i = 0; i < Tables.Count; i++)
                if (ReferenceEquals(Tables[i], table))
                    return i;

            throw new ArgumentException($"{table} is not a table of the schema.", nameof(table));
        }

        /// <summary>
        /// Builds the engine row of a logical row of table <paramref name="table"/>: the unit output appended for a
        /// relation.
        /// </summary>
        public Value[] EngineRow(int table, ReadOnlySpan<ulong> logical)
        {
            var relation = IsRelation(table);
            var row = new Value[logical.Length + (relation ? 1 : 0)];
            for (var i = 0; i < logical.Length; i++)
                row[i] = new Value(checked((uint)logical[i]));

            if (relation)
                row[^1] = Unit;

            return row;
        }

        /// <summary>
        /// Lists the logical rows of table <paramref name="table"/> that <paramref name="context"/> sees: relations without
        /// their unit output. The context must be clean.
        /// </summary>
        public List<ulong[]> Rows(EGraphContext context, int table)
        {
            ArgumentNullException.ThrowIfNull(context);
            var arity = Tables[table].Arity;
            var rows = new List<ulong[]>();
            context.ForEach(Functions[table], row =>
            {
                var cells = new ulong[arity];
                for (var i = 0; i < arity; i++)
                    cells[i] = row.Values[i].Rep;

                rows.Add(cells);
            });
            return rows;
        }

        /// <summary>
        /// Declares a relation over <paramref name="arity"/> id columns: unit output, constant default, assert-eq merge.
        /// </summary>
        FunctionId Relation(string name, int arity)
        {
            var schema = new ColumnTy[arity + 1];
            for (var i = 0; i < arity; i++)
                schema[i] = ColumnTy.Id;

            schema[arity] = UnitType;
            return EGraph.AddTable(new FunctionConfig
            {
                Schema = schema,
                Default = DefaultVal.Const(Unit),
                Merge = MergeFn.AssertEq,
                Name = name,
            });
        }

    }

}
