using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// A theory and its database: column types, tables, rulesets, scope tags, queries, and the root of a tree of scopes.
    /// Holds only managed memory; not disposable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threads: rule, ruleset, query and tag definitions may be made from any thread at any time and take part
    /// from the next iteration of every run. A table definition may be made from any thread once the root is frozen;
    /// while it is not, it throws <see cref="InvalidOperationException"/> if another thread is using the root at that
    /// moment. A rule definition whose first scoped read of a table creates a mirror table counts as a table definition.
    /// Sorts and value types may be registered from any thread. User delegates the engine invokes (primitives, lattices,
    /// filters, cost functions) must not call into a graph.
    /// </para>
    /// <para>
    /// Every scope is a context of one e-graph, the <see cref="Root"/> included, and a child costs in proportion to
    /// what it touches. A graph defined with nothing written at its root serves an empty frozen root, a frozen
    /// scope per schema (children of the root), and a transient child per query beneath it.
    /// </para>
    /// </remarks>
    public sealed partial class EGraph
    {

        int serial;
        int scopeSerial;
        int engineCalls;

        /// <summary>
        /// Initializes an empty graph: its value types, its root (tagged <see cref="RootTag"/>), and its
        /// <see cref="Contradiction"/> relation, designated as the engine's contradiction function.
        /// </summary>
        /// <param name="options">The graph's options; <c>null</c> for <see cref="EGraphOptions.Default"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="EGraphOptions.DegreeOfParallelism"/> is less than
        /// 1.</exception>
        public EGraph(EGraphOptions? options = null)
        {
            EngineCallMarker.ThrowIfActive();
            options ??= EGraphOptions.Default;
            if (options.DegreeOfParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.DegreeOfParallelism, "The degree of parallelism must be at least 1.");

            Options = options;
            var parallelism = options.DegreeOfParallelism == 1 ? Relations.ParallelHeuristics.Serial : new Relations.ParallelHeuristics(options.DegreeOfParallelism);
            Engine = new Relations.EGraph(parallelism);
            if (options.RecordPlans)
                Engine.ReportLevel = Relations.ReportLevel.WithPlan;

            BaseValues = Engine.BaseValues;
            Values = new ValueStore(this);
            Unit = new UnitColumnType(BaseValues) { Store = Values };
            NotEqual = Engine.RegisterExternalFunction(new NotEqualFunction(Unit.Unit));
            Equal = Engine.RegisterExternalFunction(new EqualFunction(Unit.Unit));
            Extraction = new ExtractionCatalog(this);
            RuntimeSlot = Engine.NewLocalSlot(static _ => new ScopeRuntime());
            Mirrors = new MirrorRegistry(this);
            RootTag = new ScopeTag(this, RootTagName, 0);
            scopeTags = new[] { RootTag };
            scopeTagsView = Array.AsReadOnly(scopeTags);
            scopeTagsByName.Add(RootTagName, RootTag);
            RootOrigin = new ScopeOrigin(this, null, "root", false);
            Root = new Scope(this);
            Contradiction = DefineContradiction();
            Engine.DesignateContradiction(Contradiction.Function);
        }

        /// <summary>
        /// Gets the graph's column types.
        /// </summary>
        public ValueStore Values { get; }

        /// <summary>
        /// Gets the root scope: what holds unconditionally. Freeze it empty so that schema scopes can be sibling children.
        /// </summary>
        public Scope Root { get; }

        /// <summary>
        /// Gets the built-in nullary relation "this scope has no model", an addition egglog lacks, named
        /// <c>$contradiction</c>, a name no user's table can take (the <c>$</c> prefix is reserved), so that it leaves
        /// <c>contradiction</c> to an egglog program: a fact table like any other, which <see cref="Scope.IsContradictory"/>
        /// reads and <see cref="RunOptions.StopOnContradiction"/> obeys.
        /// </summary>
        public Relation Contradiction { get; }

        /// <summary>
        /// Gets every table, in declaration order (a snapshot); <see cref="Contradiction"/> is the first.
        /// </summary>
        public IReadOnlyList<TableHandle> Tables => Volatile.Read(ref tablesView);

        /// <summary>
        /// Gets every ruleset, in definition order (a snapshot).
        /// </summary>
        public IReadOnlyList<Ruleset> Rulesets => Volatile.Read(ref rulesetsView);

        /// <summary>
        /// Gets how far the graph has used its interned values, ids and epochs, none of which a disposed scope gives
        /// back: a snapshot, from any thread, at the cost of two counter reads and a sum over the intern pools. A host
        /// that keeps a graph for a long time watches <see cref="GraphUsage.FractionUsed"/> and retires the graph before
        /// it reaches 1, when the next write, run or child that needs an id or an epoch throws
        /// <see cref="OverflowException"/>.
        /// </summary>
        public GraphUsage Usage
        {
            get
            {
                var store = Engine.Store;
                return new GraphUsage(Values.InternedCount, store.IdsReserved, Relations.Store.IdLimit, store.EpochsUsed, Relations.Store.EpochLimit);
            }
        }

        /// <summary>
        /// Gets the bridge e-graph the graph runs on.
        /// </summary>
        internal Relations.EGraph Engine { get; }

        /// <summary>
        /// Gets the lock of definitions: table, sort, ruleset, tag, query and rule definitions take it, and so does a
        /// freeze while it syncs a tag's mirrors and counts the frozen scope. Nothing blocks while holding it except
        /// engine definitions and a freeze's engine work; guards never block.
        /// </summary>
        internal object DefinitionLock { get; } = new();

        /// <summary>
        /// Gets whether the graph has run out of ids: an <see cref="OverflowException"/> thrown then is the store's limit,
        /// which faults nothing (architecture, section 2.12), and the calls that catch a delegate's exceptions let it
        /// through rather than report it as the delegate's failure.
        /// </summary>
        internal bool IdsExhausted => Engine.Store.IdsExhausted;

        /// <summary>
        /// Gets the per-context slot of <see cref="ScopeRuntime"/>s: every engine context of the graph
        /// holds its own, built on first use.
        /// </summary>
        internal Relations.ContextLocalSlot<ScopeRuntime> RuntimeSlot { get; }

        /// <summary>
        /// Gets the mirror tables and copy rules of scoped reads, and the counts of live frozen scopes per tag.
        /// </summary>
        internal MirrorRegistry Mirrors { get; }

        /// <summary>
        /// Gets or sets whether children and transient children are created with watermark inheritance
        /// (<c>ContextOptions.InheritWatermarks</c>): <c>true</c> by default; differential tests compare
        /// runs with it off. Read when a child is created.
        /// </summary>
        internal bool InheritWatermarks { get; set; } = true;

        /// <summary>
        /// Gets the number of engine calls that may run user delegates (runs, queries, flushes, freezes) in progress on
        /// any thread: while it is not zero, a disposed query hands its variants to the graph instead of freeing them.
        /// One volatile read.
        /// </summary>
        internal int ActiveEngineCalls => Volatile.Read(ref engineCalls);

        /// <summary>
        /// Counts an engine call that may run user delegates as started. One interlocked increment.
        /// </summary>
        internal void EnterEngineCall() => Interlocked.Increment(ref engineCalls);

        /// <summary>
        /// Counts an engine call as ended. One interlocked decrement.
        /// </summary>
        internal void ExitEngineCall() => Interlocked.Decrement(ref engineCalls);

        /// <summary>
        /// Gets the next graph-unique serial number of a scope, for paths: <c>root/schema#2/query#7</c>.
        /// </summary>
        /// <returns>The serial number.</returns>
        internal int NextScopeSerial() => Interlocked.Increment(ref scopeSerial);

        /// <summary>
        /// Gets the engine's base value registry.
        /// </summary>
        internal Relations.BaseValues BaseValues { get; }

        /// <summary>
        /// Gets the unit type of relation outputs.
        /// </summary>
        internal UnitColumnType Unit { get; }

        /// <summary>
        /// Gets the extraction schemas and cost conversions.
        /// </summary>
        internal ExtractionCatalog Extraction { get; }

        /// <summary>
        /// Gets the graph's not-equal filter.
        /// </summary>
        internal Relations.ExternalFunctionId NotEqual { get; }

        /// <summary>
        /// Gets the graph's equal filter, for class-constant comparisons left to run time.
        /// </summary>
        internal Relations.ExternalFunctionId Equal { get; }

        /// <summary>
        /// Gets every live engine variant, by description.
        /// </summary>
        internal ConcurrentDictionary<string, LoweredRule> VariantsByDescription { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the graph's options.
        /// </summary>
        internal EGraphOptions Options { get; }

        /// <summary>
        /// Gets the root's origin.
        /// </summary>
        internal ScopeOrigin RootOrigin { get; }

        /// <summary>
        /// Gets the next graph-unique serial number, for descriptions.
        /// </summary>
        /// <returns>The serial number.</returns>
        internal int NextSerial() => Interlocked.Increment(ref serial);

        /// <summary>
        /// Gets the table of an engine function.
        /// </summary>
        /// <param name="function">The engine function.</param>
        /// <returns>The table.</returns>
        /// <exception cref="ArgumentException">The function is not a table of this graph.</exception>
        internal TableHandle TableOf(Relations.FunctionId function)
        {
            var byFunction = Volatile.Read(ref tablesByFunction);
            if ((uint)function.Rep < (uint)byFunction.Length && byFunction[function.Rep] is { } table)
                return table;

            throw new ArgumentException($"Function {function} is not a table of this graph.", nameof(function));
        }

    }

}
