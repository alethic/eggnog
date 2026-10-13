using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The fixture of <see cref="ActionReadAnalysisTests"/>: a <see cref="ContextGraph"/> plus a core constructor (a
    /// <see cref="UnionIdMerge"/> table), a context-private constructor, and two externals, one that reads no tables
    /// and one that declares <see cref="ExternalFunction.ReadsTables"/>.
    /// </summary>
    sealed class ActionReadAnalysisSetup
    {

        /// <summary>
        /// Builds the graph, adds the two constructors to its store and registers the two externals.
        /// </summary>
        public ActionReadAnalysisSetup()
        {
            Graph = new ContextGraph();
            var store = Graph.EGraph.Store;
            Schema = store.Schema;
            Externals = store.Root.Database.ExternalFunctions;
            CoreConstructor = store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0), new ColumnId(1)], new UnionIdMerge(new ColumnId(1), store.UnionFindTable, new ColumnId(2))), name: "core");
            PrivateConstructor = store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0), new ColumnId(1)], new UnionIdMerge(new ColumnId(1), store.UnionFindTable, new ColumnId(2))), name: "private", contextPrivate: true);
            Pure = Graph.EGraph.RegisterExternalFunction(ExternalFunction.Create(static (ExecutionState _, ReadOnlySpan<uint> a, out uint r) =>
            {
                r = (uint)a.Length;
                return true;
            }));
            Reading = Graph.EGraph.RegisterExternalFunction(ExternalFunction.Create(static (ExecutionState _, ReadOnlySpan<uint> a, out uint r) =>
            {
                r = (uint)a.Length;
                return true;
            }, readsTables: true));
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public ContextGraph Graph { get; }

        /// <summary>
        /// Gets the store's layered schema, which the analysis reads merge kinds from.
        /// </summary>
        public LayeredSchema Schema { get; }

        /// <summary>
        /// Gets the root database's external functions.
        /// </summary>
        public ExternalFunctionRegistry Externals { get; }

        /// <summary>
        /// Gets a core constructor: a table whose merge is <see cref="UnionIdMerge"/>.
        /// </summary>
        public TableId CoreConstructor { get; }

        /// <summary>
        /// Gets a context-private constructor.
        /// </summary>
        public TableId PrivateConstructor { get; }

        /// <summary>
        /// Gets an external that reads no tables.
        /// </summary>
        public ExternalFunctionId Pure { get; }

        /// <summary>
        /// Gets an external that declares it reads tables.
        /// </summary>
        public ExternalFunctionId Reading { get; }

        /// <summary>
        /// Gets the table of function <paramref name="f"/>.
        /// </summary>
        public TableId Table(FunctionId f) => Graph.EGraph.GetTable(f);

        /// <summary>
        /// Gets whether <paramref name="instr"/> alone is query-determined
        /// (<see cref="ActionReadAnalysis.IsQueryDetermined(Instr, LayeredSchema, ExternalFunctionRegistry)"/>).
        /// </summary>
        public bool Determined(Instr instr) => ActionReadAnalysis.IsQueryDetermined(instr, Schema, Externals);

    }

}
