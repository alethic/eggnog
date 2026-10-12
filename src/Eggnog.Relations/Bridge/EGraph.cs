using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An e-graph: functions with functional dependencies, default values and merge functions, subsumption, a
    /// union-find with congruence closure by rebuilding, and the timestamps seminaive evaluation runs on, all lowered onto
    /// a <see cref="Relations.Database"/>. egglog-bridge's <c>EGraph</c> (egglog-bridge/src/lib.rs:113-964), the engine's
    /// main entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout. The union-find is the database's first table, <c>$uf</c>, a <see cref="DisplacedTable"/>. Each function
    /// is a <see cref="SortedWritesTable"/> laid out as <c>[keys..., output, timestamp, subsumed?]</c>
    /// (<see cref="SchemaMath"/>), sorted by the timestamp, rebuilding its id-typed key and output columns, whose merge is
    /// the function's <see cref="MergeFn"/> on the output and the maximum on the subsumption cell
    /// (<see cref="FunctionTableMerge"/>). Fresh ids come from one counter starting at 0, timestamps from another starting
    /// at 1 (lib.rs:140-189).
    /// </para>
    /// <para>
    /// Timestamps. Every row written in a run, a flush or a rebuild pass is stamped with the timestamp counter's value
    /// when it started, and the counter advances once after it (map §1.3, §4.2 C1). Rows newer than a rule's last run are
    /// what seminaive evaluation looks at.
    /// </para>
    /// <para>
    /// Writes outside rules (<see cref="AddValues"/>, <see cref="AddTerm"/>, <see cref="WithExecutionState{TResult}"/>
    /// followed by <see cref="FlushUpdates"/>) are staged and merged by <see cref="FlushUpdates"/>, which rebuilds when
    /// they unioned anything. Nothing canonicalizes its inputs: callers pass canonical ids (<see cref="GetCanonRepr"/>)
    /// when they need canonical rows.
    /// </para>
    /// <para>
    /// Panics. A conflict in a function without a merge, a failed lookup or primitive, or an explicit panic calls a
    /// panic function (<see cref="NewPanic"/>), which requests early stopping and records its message in the panic side
    /// channel; the run that observed it then throws <see cref="PanicException"/> after merging what it wrote. A panic
    /// recorded while flushing is reported by the next run, as in egglog.
    /// </para>
    /// <para>
    /// Not thread-safe: every method needs exclusive access (egglog's <c>&amp;mut self</c>); the parallelism is inside
    /// the database's operations.
    /// </para>
    /// </remarks>
    internal sealed partial class EGraph
    {

        /// <summary>
        /// The name of the union-find table.
        /// </summary>
        internal const string UnionFindName = "$uf";

        /// <summary>
        /// The message of the default panic function (lib.rs:160).
        /// </summary>
        internal const string DefaultPanicMessage = "primitive panicked";

        readonly Database db;
        readonly TableId ufTable;
        readonly DisplacedTable unionFind;
        readonly CounterId idCounter;
        readonly CounterId timestampCounter;
        readonly DenseIdMap<FunctionId, FunctionInfo> funcs;
        readonly List<TableId> functionTables;
        readonly ActionRegistry actionRegistry;
        readonly UnionContainerMerge containerMerge;

        /// <summary>
        /// Initializes an empty e-graph. egglog's <c>EGraph::default</c>.
        /// </summary>
        public EGraph()
            : this(ParallelHeuristics.Default)
        {

        }

        /// <summary>
        /// Initializes an empty e-graph whose database and tables use <paramref name="parallelism"/>.
        /// </summary>
        /// <param name="parallelism">When operations go parallel.</param>
        internal EGraph(ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(parallelism);

            db = new Database(parallelism);
            unionFind = new DisplacedTable();
            ufTable = db.AddTable(unionFind, name: UnionFindName);
            idCounter = db.AddCounter();
            timestampCounter = db.AddCounter();

            // Start the timestamp counter at 1 (lib.rs:151-152), so that a rule's initial last run, 0, sees every row.
            db.IncrementCounter(timestampCounter);

            funcs = new DenseIdMap<FunctionId, FunctionInfo>();
            functionTables = new List<TableId>();
            containerMerge = new UnionContainerMerge(ufTable, timestampCounter);

            // A default panic function, so that the frontend's panic action has an id to call; interning it also lets a
            // later NewPanic with the same message reuse it (lib.rs:154-165).
            var defaultPanicId = NewPanic(DefaultPanicMessage);
            actionRegistry = new ActionRegistry(new UnionAction(ufTable, timestampCounter), defaultPanicId);
        }

        /// <summary>
        /// Gets the database the e-graph is lowered onto.
        /// </summary>
        internal Database Database => db;

        /// <summary>
        /// Gets the union-find table, <c>$uf</c>.
        /// </summary>
        internal TableId UnionFindTable => ufTable;

        /// <summary>
        /// Gets the counter fresh e-class ids come from.
        /// </summary>
        internal CounterId IdCounter => idCounter;

        /// <summary>
        /// Gets the timestamp counter.
        /// </summary>
        internal CounterId TimestampCounter => timestampCounter;

        /// <summary>
        /// Gets the live registry of action handles, shared with primitives. egglog's <c>action_registry</c>.
        /// </summary>
        internal ActionRegistry ActionRegistry => actionRegistry;

        /// <summary>
        /// Gets or sets how much rule set reports record. egglog's <c>set_report_level</c>.
        /// </summary>
        internal ReportLevel ReportLevel { get; set; }

        /// <summary>
        /// Gets the number of declared functions; their ids are <c>[0, FunctionCount)</c>.
        /// </summary>
        public int FunctionCount => funcs.IdCount;

        /// <summary>
        /// Gets the current timestamp: what rows written now are stamped with. egglog's <c>next_ts</c>.
        /// </summary>
        /// <exception cref="OverflowException">The counter no longer fits in 32 bits.</exception>
        internal uint NextTimestamp => checked((uint)db.ReadCounter(timestampCounter));

        /// <summary>
        /// Gets the number of effective unions the union-find has recorded: a run or flush that leaves it unchanged
        /// needs no rebuild (lib.rs:594-613, 949-957).
        /// </summary>
        internal int UnionCount => unionFind.Count;

        /// <summary>
        /// Gets what the e-graph keeps about <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The function's information.</returns>
        /// <exception cref="ArgumentException">No such function.</exception>
        internal FunctionInfo GetFunctionInfo(FunctionId function)
        {
            if (funcs.TryGetValue(function, out var info))
                return info;

            throw new ArgumentException($"Function {function} has not been declared in this e-graph.", nameof(function));
        }

        /// <summary>
        /// Gets the table of <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The table's id.</returns>
        /// <exception cref="ArgumentException">No such function.</exception>
        internal TableId GetTable(FunctionId function) => GetFunctionInfo(function).Table;

        /// <summary>
        /// Gets the name of <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The name.</returns>
        /// <exception cref="ArgumentException">No such function.</exception>
        public string GetFunctionName(FunctionId function) => GetFunctionInfo(function).Name;

        /// <summary>
        /// Gets the tables of every function, in function order: what a rebuild rewrites.
        /// </summary>
        internal ReadOnlySpan<TableId> FunctionTables => CollectionsMarshal.AsSpan(functionTables);

        /// <summary>
        /// Mints a fresh e-class id. egglog's <c>fresh_id</c>.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="OverflowException">The id counter no longer fits in 32 bits.</exception>
        public Value FreshId() => new(checked((uint)db.IncrementCounter(idCounter)));

        /// <summary>
        /// Gets the canonical id of <paramref name="value"/> in the union-find; an id never unioned is its own canonical
        /// id. egglog's <c>get_canon_in_uf</c> (lib.rs:302-309). O(find), without writes.
        /// </summary>
        /// <param name="value">The id.</param>
        /// <returns>The canonical id: the smallest member of its class.</returns>
        internal Value GetCanonInUf(Value value) => new(unionFind.FindNaive(value.Rep));

        /// <summary>
        /// Gets the canonical representation of <paramref name="value"/> in a column of type <paramref name="type"/>: its
        /// canonical id for an id column, the value itself for a base value column. egglog's <c>get_canon_repr</c>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="type">The column type.</param>
        /// <returns>The canonical representation.</returns>
        public Value GetCanonRepr(Value value, ColumnTy type) => type.IsId ? GetCanonInUf(value) : value;

        /// <summary>
        /// Declares a function and returns its id. egglog's <c>add_table</c> (lib.rs:506-573).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The function's table is <c>[keys..., output, timestamp, subsumed?]</c>, sorted by the timestamp, rebuilding
        /// its id-typed columns. Its merge is the resolved <see cref="FunctionConfig.Merge"/>: resolving creates the panic
        /// functions the merge may call ("Illegal merge attempted for function ...", and so on) and turns function
        /// references into <see cref="TableAction"/>s. The merge's dependencies decide when the table merges: a
        /// <see cref="MergeFnKind.Function"/> merge reads and writes the function it looks up, so that function's table
        /// merges first; a union-id or primitive merge writes the union-find (<c>fill_deps</c>, lib.rs:1033-1057).
        /// </para>
        /// <para>
        /// The function is registered in the <see cref="ActionRegistry"/> under its name. The rule-based rebuild rules
        /// egglog creates here are not created: rebuild is the native path only (decision 10).
        /// </para>
        /// </remarks>
        /// <param name="config">The declaration.</param>
        /// <returns>The function's id.</returns>
        /// <exception cref="ArgumentException">The schema is empty, or a function merge names an undeclared function or
        /// passes it the wrong number of keys.</exception>
        public FunctionId AddTable(FunctionConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(config.Schema, nameof(config));
            ArgumentNullException.ThrowIfNull(config.Merge, nameof(config));
            ArgumentNullException.ThrowIfNull(config.Name, nameof(config));

            var schema = new ColumnTy[config.Schema.Count];
            for (var i = 0; i < schema.Length; i++)
                schema[i] = config.Schema[i];

            if (schema.Length == 0)
                throw new ArgumentException("must have at least one column in schema", nameof(config));

            // Validate before creating anything, so that a bad declaration leaves the e-graph unchanged.
            CheckMerge(config.Merge, config.Name);

            var math = new SchemaMath(schema.Length, config.CanSubsume);
            var toRebuild = new List<ColumnId>(schema.Length);
            for (var i = 0; i < schema.Length; i++)
                if (schema[i].IsId)
                    toRebuild.Add(new ColumnId(i));

            var readDeps = new List<TableId>();
            var writeDeps = new List<TableId>();
            FillDeps(config.Merge, readDeps, writeDeps);

            var merge = new FunctionTableMerge(math, Resolve(config.Merge, config.Name), config.Merge.IsLattice);
            var table = new SortedWritesTable(math.KeyCount, math.TableColumns, new ColumnId(math.TimestampColumn), CollectionsMarshal.AsSpan(toRebuild), merge, db.Parallelism);
            var tableId = db.AddTable(table, CollectionsMarshal.AsSpan(readDeps), CollectionsMarshal.AsSpan(writeDeps), config.Name);

            var next = funcs.NextId;
            var id = funcs.Push(new FunctionInfo(tableId, schema, config.Default, config.Merge, config.CanSubsume, config.Name));
            Debug.Assert(id == next, "function ids are dense");
            functionTables.Add(tableId);
            actionRegistry.RegisterTable(config.Name, new TableAction(this, id));
            return id;
        }

        /// <summary>
        /// Checks that every function a merge refers to is declared and gets one argument per key column (egglog asserts
        /// the arity while resolving, lib.rs:1131-1137).
        /// </summary>
        void CheckMerge(MergeFn merge, string functionName)
        {
            if (merge.Kind == MergeFnKind.Function)
            {
                if (funcs.TryGetValue(merge.FunctionId, out var target) == false)
                    throw new ArgumentException($"The merge function for {functionName} refers to {merge.FunctionId}, which has not been declared.", "config");

                if (target.Schema.Length != merge.Args.Length + 1)
                    throw new ArgumentException($"Merge function for {functionName} must match function arity for {target.Name}: {target.Schema.Length - 1} arguments, not {merge.Args.Length}.", "config");
            }

            foreach (var arg in merge.Args)
            {
                ArgumentNullException.ThrowIfNull(arg, "config");
                CheckMerge(arg, functionName);
            }
        }

        /// <summary>
        /// Collects the tables a merge reads and writes: egglog's <c>fill_deps</c> (lib.rs:1033-1057). Repeats are
        /// harmless; the database ignores them.
        /// </summary>
        void FillDeps(MergeFn merge, List<TableId> readDeps, List<TableId> writeDeps)
        {
            switch (merge.Kind)
            {
                case MergeFnKind.Primitive:
                    foreach (var arg in merge.Args)
                        FillDeps(arg, readDeps, writeDeps);

                    writeDeps.Add(ufTable);
                    break;
                case MergeFnKind.Function:
                    var table = funcs[merge.FunctionId].Table;
                    readDeps.Add(table);
                    writeDeps.Add(table);
                    foreach (var arg in merge.Args)
                        FillDeps(arg, readDeps, writeDeps);

                    break;
                case MergeFnKind.UnionId:
                    writeDeps.Add(ufTable);
                    break;
            }
        }

        /// <summary>
        /// Resolves a merge against this e-graph: egglog's <c>MergeFn::resolve</c> (lib.rs:1103-1151), in its order, so
        /// that panic functions get the same ids.
        /// </summary>
        ResolvedMergeFn Resolve(MergeFn merge, string functionName)
        {
            switch (merge.Kind)
            {
                case MergeFnKind.Const:
                    return ResolvedMergeFn.Const(merge.Constant.Rep);
                case MergeFnKind.Old:
                    return ResolvedMergeFn.Old();
                case MergeFnKind.New:
                    return ResolvedMergeFn.New();
                case MergeFnKind.AssertEq:
                    return ResolvedMergeFn.AssertEq(NewPanic($"Illegal merge attempted for function {functionName}"));
                case MergeFnKind.UnionId:
                    return ResolvedMergeFn.UnionId(ufTable);
                case MergeFnKind.Primitive:
                {
                    var args = ResolveArgs(merge, functionName);
                    var panic = NewPanic($"Merge function for {functionName} primitive call failed");
                    return ResolvedMergeFn.Primitive(merge.PrimitiveId, args, panic);
                }
                default:
                {
                    var target = funcs[merge.FunctionId];
                    var action = new TableAction(this, merge.FunctionId);
                    var panic = NewPanic($"Lookup on {target.Name} failed in the merge function for {functionName}");
                    return ResolvedMergeFn.Function(action, ResolveArgs(merge, functionName), panic);
                }
            }
        }

        /// <summary>
        /// Resolves the arguments of a primitive or function merge, in order.
        /// </summary>
        ResolvedMergeFn[] ResolveArgs(MergeFn merge, string functionName)
        {
            var args = merge.Args;
            var resolved = new ResolvedMergeFn[args.Length];
            for (var i = 0; i < args.Length; i++)
                resolved[i] = Resolve(args[i], functionName);

            return resolved;
        }

    }

}
