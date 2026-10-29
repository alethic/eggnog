using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// An e-graph: functions with functional dependencies, default values and merge functions, subsumption, a
    /// union-find with congruence closure by rebuilding, and the timestamps seminaive evaluation runs on, all lowered onto
    /// a <see cref="Relations.Store"/>. egglog-bridge's <c>EGraph</c> (egglog-bridge/src/lib.rs:113-964), the engine's
    /// main entry point, extended with colored contexts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout. The e-graph builds a <see cref="Relations.Store"/>: the union-find is its table 0, <c>$uf</c>, fresh ids
    /// come from its counter 0 and timestamps from its clock, counter 1, which starts at 1 (lib.rs:140-189). Each
    /// function is a <see cref="SortedWritesTable"/> registered with the store, laid out as
    /// <c>[keys..., output, timestamp, subsumed?]</c> (<see cref="SchemaMath"/>), sorted by the timestamp, rebuilding its
    /// id-typed key and output columns, whose merge is the function's <see cref="MergeFn"/> on the output and the maximum
    /// on the subsumption cell (<see cref="FunctionTableMerge"/>).
    /// </para>
    /// <para>
    /// Definitions and contexts. The e-graph holds the definitions: functions, rules (a <see cref="RuleTable"/>),
    /// panic functions, the contradiction function, and context-local slots. Everything a context runs, writes and reads
    /// lives in an <see cref="EGraphContext"/>: <see cref="Root"/>, and the children created from it. The root-only
    /// egglog API of this type (<see cref="RunRules"/>, <see cref="FlushUpdates"/>, <see cref="AddValues"/>,
    /// <see cref="AddTerm"/>, <see cref="LookupId"/>, <see cref="TableSize"/>, <see cref="ForEach(FunctionId, FunctionRowCallback)"/>,
    /// <see cref="WithExecutionState{TResult}"/>, <see cref="FreshId"/>, <see cref="ClearTable"/>, <see cref="Rebuild"/>)
    /// runs on <see cref="Root"/> with egglog's semantics: rows are written as given and read as committed, and a panic a
    /// flush records is reported by the next run. It departs from egglog in two ways: a rule's semi-naive progress is
    /// not committed after a run that recorded a failure, and every Id constant of a rule runs in its canonical form,
    /// so a rule whose constant lost a union keeps matching its class.
    /// </para>
    /// <para>
    /// Timestamps. Every row written in a run, a flush or a rebuild pass is stamped with the clock's value when it
    /// started, and the clock advances once after it. Rows newer than a rule's last run are what seminaive evaluation
    /// looks at. A program that creates no child and never freezes gets egglog's timestamps exactly.
    /// </para>
    /// <para>
    /// Panics. A conflict in a function without a merge, a failed lookup or primitive, or an explicit panic calls a
    /// panic function (<see cref="NewPanic"/>), which reports its message as the calling state's failure and stops the
    /// run early; the context whose run observed it throws <see cref="PanicException"/> after merging what it wrote.
    /// </para>
    /// <para>
    /// Threading. Definitions (<see cref="AddTable"/>, <see cref="NewRule"/>, <see cref="NewPanic"/>,
    /// <see cref="RegisterExternalFunction"/>, <see cref="NewLocalSlot{T}"/>,
    /// <see cref="RegisterContainerType{TContainer}"/>) may be made from any thread (a table or a container type, once
    /// the root is frozen or while no other thread uses it): they publish copy-on-write. A context and every descendant
    /// created under it while it is not frozen form one thread domain; children of a frozen context may run concurrently
    /// (<see cref="ConcurrentChildren"/>, within <see cref="ParallelismBudget"/>). The parallelism of a single operation
    /// is inside the database's operations.
    /// </para>
    /// </remarks>
    internal sealed partial class EGraph
    {

        /// <summary>
        /// The name of the union-find table.
        /// </summary>
        internal const string UnionFindName = Store.UnionFindName;

        /// <summary>
        /// The message of the default panic function (lib.rs:160).
        /// </summary>
        internal const string DefaultPanicMessage = "primitive panicked";

        readonly Store store;
        readonly Database db;
        readonly TableId ufTable;
        readonly DisplacedTable unionFind;
        readonly CounterId idCounter;
        readonly CounterId timestampCounter;
        readonly ActionRegistry actionRegistry;
        readonly UnionContainerMerge containerMerge;
        readonly EGraphContext root;
        readonly ParallelismBudget parallelismBudget;

        /// <summary>
        /// Serializes definitions: functions and the contradiction function.
        /// </summary>
        readonly object definitionGate = new();

        /// <summary>
        /// The declared functions, by id, published copy-on-write: readers on any thread see a complete array.
        /// </summary>
        FunctionInfo[] functions = Array.Empty<FunctionInfo>();

        /// <summary>
        /// The tables of every function, in function order, published copy-on-write with <see cref="functions"/>.
        /// </summary>
        TableId[] functionTables = Array.Empty<TableId>();

        /// <summary>
        /// Initializes an empty e-graph. egglog's <c>EGraph::default</c>.
        /// </summary>
        public EGraph()
            : this(ParallelHeuristics.Default)
        {

        }

        /// <summary>
        /// Initializes an empty e-graph whose store and tables use <paramref name="parallelism"/> (the root of a
        /// store-backed e-graph honours the host's degree of parallelism, and children of writable contexts inherit
        /// it).
        /// </summary>
        /// <param name="parallelism">When operations go parallel.</param>
        internal EGraph(ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(parallelism);

            // The store lays out the union-find and the two counters (lib.rs:140-152): table 0, counter 0 for ids,
            // counter 1 for timestamps, started at 1 so that a rule's initial last run, 0, sees every row.
            store = new Store(parallelism);
            parallelismBudget = new ParallelismBudget(parallelism.DegreeOfParallelism);
            db = store.Root.Database;
            ufTable = store.UnionFindTable;
            unionFind = store.RootUnionFind;
            idCounter = store.IdCounter;
            timestampCounter = store.ClockCounter;
            containerMerge = new UnionContainerMerge(ufTable, timestampCounter);

            // A default panic function, so that the frontend's panic action has an id to call; interning it also lets a
            // later NewPanic with the same message reuse it (lib.rs:154-165).
            var defaultPanicId = NewPanic(DefaultPanicMessage);
            actionRegistry = new ActionRegistry(new UnionAction(ufTable, timestampCounter), defaultPanicId);
            root = new EGraphContext(this, null, store.Root);
        }

        /// <summary>
        /// Gets the store the e-graph is lowered onto.
        /// </summary>
        internal Store Store => store;

        /// <summary>
        /// Gets the root context: what is true unconditionally. The egglog-compatible API of this type runs on it.
        /// </summary>
        internal EGraphContext Root => root;

        /// <summary>
        /// Gets the root context's database, egglog's database.
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
        /// Gets the timestamp counter: the store's clock.
        /// </summary>
        internal CounterId TimestampCounter => timestampCounter;

        /// <summary>
        /// Gets the live registry of action handles, shared with primitives. egglog's <c>action_registry</c>.
        /// </summary>
        internal ActionRegistry ActionRegistry => actionRegistry;

        /// <summary>
        /// Gets or sets how much rule set reports record, in every context. egglog's <c>set_report_level</c>.
        /// </summary>
        internal ReportLevel ReportLevel { get; set; }

        /// <summary>
        /// Gets the number of declared functions; their ids are <c>[0, FunctionCount)</c>.
        /// </summary>
        public int FunctionCount => Volatile.Read(ref functions).Length;

        /// <summary>
        /// Gets the current timestamp: what rows written now are stamped with. egglog's <c>next_ts</c>.
        /// </summary>
        /// <exception cref="OverflowException">The clock does not fit in 32 bits.</exception>
        internal uint NextTimestamp => store.ReadClock();

        /// <summary>
        /// Gets the number of effective unions the root's union-find has recorded: a run or flush that leaves it
        /// unchanged needs no rebuild (lib.rs:594-613, 949-957).
        /// </summary>
        internal int UnionCount => unionFind.Count;

        /// <summary>
        /// Gets what the e-graph keeps about <paramref name="function"/>. Lock-free; any thread.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The function's information.</returns>
        /// <exception cref="ArgumentException">No such function.</exception>
        internal FunctionInfo GetFunctionInfo(FunctionId function)
        {
            var current = Volatile.Read(ref functions);
            if ((uint)function.Index < (uint)current.Length)
                return current[function.Index];

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
        /// Gets the tables of every function, in function order: what a rebuild rewrites. A snapshot.
        /// </summary>
        internal ReadOnlySpan<TableId> FunctionTables => Volatile.Read(ref functionTables);

        /// <summary>
        /// Mints a fresh e-class id at the root. egglog's <c>fresh_id</c>.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="OverflowException">The store has run out of ids: the next one would exceed
        /// <see cref="UnionFind.MaxId"/> (<see cref="Database.IncrementCounter"/> bounds the root's mints).</exception>
        public Value FreshId() => new((uint)db.IncrementCounter(idCounter));

        /// <summary>
        /// Gets the canonical id of <paramref name="value"/> in the root's union-find; an id never unioned is its own
        /// canonical id. egglog's <c>get_canon_in_uf</c> (lib.rs:302-309). O(find), without writes.
        /// </summary>
        /// <param name="value">The id.</param>
        /// <returns>The canonical id: the smallest member of its class.</returns>
        internal Value GetCanonInUf(Value value) => new(unionFind.FindNaive(value.Rep));

        /// <summary>
        /// Gets the canonical representation of <paramref name="value"/> in a column of type <paramref name="type"/> at the
        /// root: its canonical id for an id column, the value itself for a base value column. egglog's
        /// <c>get_canon_repr</c>.
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
        /// merges first; a union-id or primitive merge writes the union-find (<c>fill_deps</c>, lib.rs:1033-1057). An
        /// <see cref="MergeFnKind.IdConst"/> merge reads the class as the last rebuild pass left it, which the database
        /// keeps (<see cref="Database.AddClassConstant"/>), so it depends on no table.
        /// </para>
        /// <para>
        /// The table is added to the store (<see cref="Relations.Store.AddTable"/>): at any time, children or not;
        /// while the root is not frozen, not while another thread uses it. A child sees the new function as an empty
        /// table it may write once it has caught up. Only lattice merges may be written in a child.
        /// </para>
        /// <para>
        /// The function is registered in the <see cref="ActionRegistry"/> under its name. The rule-based rebuild rules
        /// egglog creates here are not created: rebuild is the native path only. Thread-safe: definitions serialize,
        /// and the function list is published copy-on-write.
        /// </para>
        /// </remarks>
        /// <param name="config">The declaration.</param>
        /// <returns>The function's id.</returns>
        /// <exception cref="ArgumentException">The schema is empty, or a function merge names an undeclared function or
        /// passes it the wrong number of keys.</exception>
        /// <exception cref="InvalidOperationException">The root's thread domain is held by another thread.</exception>
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

            lock (definitionGate)
            {
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
                var tableId = store.AddTable(table, CollectionsMarshal.AsSpan(readDeps), CollectionsMarshal.AsSpan(writeDeps), config.Name,
                    config.ContextPrivate);

                var current = functions;
                var id = new FunctionId(current.Length);
                var info = new FunctionInfo(tableId, schema, config.Default, config.Merge, config.CanSubsume, config.Name);
                var nextFunctions = new FunctionInfo[current.Length + 1];
                Array.Copy(current, nextFunctions, current.Length);
                nextFunctions[^1] = info;
                var nextTables = new TableId[nextFunctions.Length];
                Array.Copy(functionTables, nextTables, functionTables.Length);
                nextTables[^1] = tableId;
                Volatile.Write(ref functionTables, nextTables);
                Volatile.Write(ref functions, nextFunctions);
                Debug.Assert(id.Index == nextFunctions.Length - 1, "function ids are dense");
                actionRegistry.RegisterTable(config.Name, new TableAction(this, id));
                return id;
            }
        }

        /// <summary>
        /// Checks that every function a merge refers to is declared and gets one argument per key column (egglog asserts
        /// the arity while resolving, lib.rs:1131-1137).
        /// </summary>
        void CheckMerge(MergeFn merge, string functionName)
        {
            if (merge.Kind == MergeFnKind.Function)
            {
                var current = functions;
                if ((uint)merge.FunctionId.Index >= (uint)current.Length)
                    throw new ArgumentException($"The merge function for {functionName} refers to {merge.FunctionId}, which has not been declared.", "config");

                var target = current[merge.FunctionId.Index];
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
                    var table = functions[merge.FunctionId.Index].Table;
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
                case MergeFnKind.IdConst:
                {
                    // egglog's lowered global holds the class as it was canonical when the global was made, and as each
                    // rebuild leaves it after that.
                    var id = merge.Constant.Rep;
                    return ResolvedMergeFn.IdConst(id, db.AddClassConstant(id, unionFind.FindNaive(id)));
                }
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
                    var target = functions[merge.FunctionId.Index];
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
