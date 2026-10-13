using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A store-backed e-graph for the case-split benchmarks of colored contexts (colored-contexts spec §17 M10.4, L6, L7):
    /// <c>edge(x, y)</c> and <c>path(x, y)</c> relations (unit output, assert-eq merge), <c>mark(x)</c>, the constructors
    /// <c>num(i64) → id</c> and <c>add(id, id) → id</c>, the nullary <c>bottom()</c> designated as the contradiction, and
    /// the rule <c>edge(x, y) ⇒ path(x, y)</c>. The root holds <see cref="Rows"/> ids, <c>edge(x_i, x_{i+1})</c> for each
    /// and <c>mark(x_i)</c> for every tenth, loaded in one flush.
    /// </summary>
    /// <remarks>
    /// Each id is named by two <c>edge</c> rows and at most one <c>mark</c> row, so a case that unions a fixed number of
    /// ids touches a fixed number of root rows whatever <see cref="Rows"/> is.
    /// </remarks>
    internal sealed class CaseGraph
    {

        /// <summary>
        /// Builds the graph and its root rows; the root is left writable.
        /// </summary>
        /// <param name="rows">The number of root ids; at least 2.</param>
        /// <param name="runCopy">Whether the root runs <see cref="Copy"/> once, so <c>path</c> mirrors <c>edge</c> and the
        /// rule's watermark is set for children to inherit.</param>
        public CaseGraph(int rows, bool runCopy = false)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 2);
            Rows = rows;
            EGraph = new EGraph(ParallelHeuristics.Serial);
            var i64 = ColumnTy.Base(EGraph.BaseValues.RegisterType<Int64Codec, long>());
            var unitType = ColumnTy.Base(EGraph.BaseValues.RegisterType<UnitCodec, ValueTuple>());
            Unit = EGraph.BaseValues.Get<UnitCodec, ValueTuple>(default);
            Edge = Relation("edge", unitType, ColumnTy.Id, ColumnTy.Id);
            Path = Relation("path", unitType, ColumnTy.Id, ColumnTy.Id);
            Mark = Relation("mark", unitType, ColumnTy.Id);
            Bottom = Relation("bottom", unitType);
            Num = Constructor("num", i64);
            Add = Constructor("add", ColumnTy.Id, ColumnTy.Id);
            EGraph.DesignateContradiction(Bottom);

            var rb = EGraph.NewRule("edge-path", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Edge, [x, y, rb.NewVar(unitType)], false);
            rb.Set(Path, [x, y, RuleEntry.Const(Unit, unitType)]);
            Copy = rb.Build();

            var root = EGraph.Root;
            Ids = new Value[rows];
            for (var i = 0; i < rows; i++)
                Ids[i] = root.FreshId();

            var batch = new List<(FunctionId, Value[])>(rows + rows / 10);
            for (var i = 0; i < rows; i++)
            {
                batch.Add((Edge, [Ids[i], Ids[(i + 1) % rows], Unit]));
                if (i % 10 == 0)
                    batch.Add((Mark, [Ids[i], Unit]));
            }

            root.AddValues(batch);
            if (runCopy)
                root.RunRules(Copy);
        }

        /// <summary>
        /// Gets the number of root ids.
        /// </summary>
        public int Rows { get; }

        /// <summary>
        /// Gets the e-graph.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the root context.
        /// </summary>
        public EGraphContext Root => EGraph.Root;

        /// <summary>
        /// Gets the unit value, the output of every relation row.
        /// </summary>
        public Value Unit { get; }

        /// <summary>
        /// Gets the root's ids, ascending.
        /// </summary>
        public Value[] Ids { get; }

        /// <summary>
        /// Gets <c>edge(x, y)</c>.
        /// </summary>
        public FunctionId Edge { get; }

        /// <summary>
        /// Gets <c>path(x, y)</c>.
        /// </summary>
        public FunctionId Path { get; }

        /// <summary>
        /// Gets <c>mark(x)</c>.
        /// </summary>
        public FunctionId Mark { get; }

        /// <summary>
        /// Gets <c>bottom()</c>, the contradiction.
        /// </summary>
        public FunctionId Bottom { get; }

        /// <summary>
        /// Gets <c>num(i64) → id</c>.
        /// </summary>
        public FunctionId Num { get; }

        /// <summary>
        /// Gets <c>add(id, id) → id</c>.
        /// </summary>
        public FunctionId Add { get; }

        /// <summary>
        /// Gets <c>edge(x, y) ⇒ path(x, y)</c>.
        /// </summary>
        public EGraphRuleId Copy { get; }

        /// <summary>
        /// Gets the lift spec over every table but the contradiction.
        /// </summary>
        public LiftSpec Spec => new(
            new LiftTable(Edge, LiftTableKind.Relation),
            new LiftTable(Path, LiftTableKind.Relation),
            new LiftTable(Mark, LiftTableKind.Relation),
            new LiftTable(Num, LiftTableKind.Constructor),
            new LiftTable(Add, LiftTableKind.Constructor));

        /// <summary>
        /// Boxes an i64.
        /// </summary>
        public Value Int(long value) => EGraph.BaseValues.Get<Int64Codec, long>(value);

        FunctionId Relation(string name, ColumnTy unitType, params ColumnTy[] keys) => EGraph.AddTable(new FunctionConfig
        {
            Schema = [.. keys, unitType],
            Default = DefaultVal.Const(Unit),
            Merge = MergeFn.AssertEq,
            Name = name,
        });

        FunctionId Constructor(string name, params ColumnTy[] keys) => EGraph.AddTable(new FunctionConfig
        {
            Schema = [.. keys, ColumnTy.Id],
            Default = DefaultVal.FreshId,
            Merge = MergeFn.UnionId,
            Name = name,
        });

    }

}
