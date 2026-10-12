using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// A theory and its database: column types, tables, rulesets, and the root of a tree of scopes. Holds only managed
    /// memory; not disposable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threads (§6.12): rule and ruleset definitions may be made from any thread at any time and take part from the next
    /// iteration of every run (D6). A table definition throws <see cref="InvalidOperationException"/> while another thread
    /// is using the root (from R2: until the root is frozen); sorts and value types may be registered from any thread.
    /// User delegates the engine invokes (primitives, lattices, filters, cost functions) must not call into a graph.
    /// </para>
    /// <para>
    /// R1 runs everything at the <see cref="Root"/>, on the engine's egglog-compatible root.
    /// </para>
    /// </remarks>
    public sealed partial class EGraph
    {

        int serial;

        /// <summary>
        /// Initializes an empty graph: its value types, its root, and its <see cref="Contradiction"/> relation.
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
            Values = new ValueStore(BaseValues);
            Unit = new UnitColumnType(BaseValues) { Store = Values };
            NotEqual = Engine.RegisterExternalFunction(new NotEqualFunction(Unit.Unit));
            Extraction = new ExtractionCatalog(this);
            RootOrigin = new ScopeOrigin(this, null, "root", false);
            Root = new Scope(this);
            Contradiction = DefineRelation("contradiction");
        }

        /// <summary>
        /// Gets the graph's column types.
        /// </summary>
        public ValueStore Values { get; }

        /// <summary>
        /// Gets the root scope, where R1 runs everything.
        /// </summary>
        public Scope Root { get; }

        /// <summary>
        /// Gets the built-in nullary relation "this scope has no model", named <c>contradiction</c>: a fact table like any
        /// other, which <see cref="Scope.IsContradictory"/> reads and <see cref="RunOptions.StopOnContradiction"/> obeys.
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
        /// Gets the bridge e-graph the graph runs on.
        /// </summary>
        internal Relations.EGraph Engine { get; }

        /// <summary>
        /// Gets the lock of definitions: table, sort, ruleset and rule definitions take it (lock order: definition lock,
        /// then engine lock).
        /// </summary>
        internal object DefinitionLock { get; } = new();

        /// <summary>
        /// Gets the lock of the bridge (R1, deleted in R2): every root engine call holds it for one call, and so do bridge
        /// mutations of definitions, which then wait for the current engine call.
        /// </summary>
        internal object EngineLock { get; } = new();

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
        /// Gets every live engine variant, by description (§6.4.3).
        /// </summary>
        internal ConcurrentDictionary<string, LoweredRule> VariantsByDescription { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the graph's options.
        /// </summary>
        internal EGraphOptions Options { get; }

        /// <summary>
        /// Gets the root's origin (D39).
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
