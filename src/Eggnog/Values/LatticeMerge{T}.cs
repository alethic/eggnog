using System;

namespace Eggnog
{

    /// <summary>
    /// The merge external function of a function with a lattice: <c>[old, new] → Encode(Merge(Decode(old),
    /// Decode(new)))</c>, registered before its table and used as
    /// <c>MergeFn.Primitive(merge, Old, New).AsLattice()</c>.
    /// </summary>
    /// <remarks>
    /// The lattice's delegate runs under <see cref="EngineCallMarker"/>, on whatever thread merges the table. An
    /// exception from it is recorded as an evaluation failure of the scope (rule name <c>null</c>: no rule owns a
    /// merge) and reported with <c>ExecutionState.ReportFailure</c>, so the engine call ends in a panic that the rule
    /// layer surfaces as a <see cref="RuleEvaluationException"/>; the old value is kept. Immutable and thread-safe.
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class LatticeMerge<T> : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string functionName;
        readonly ValueLattice<T> lattice;
        readonly ColumnType<T> type;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="lattice">The lattice.</param>
        /// <param name="type">The function's output type.</param>
        public LatticeMerge(EGraph graph, string functionName, ValueLattice<T> lattice, ColumnType<T> type)
        {
            this.graph = graph;
            this.functionName = functionName;
            this.lattice = lattice;
            this.type = type;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                result = type.Encode(lattice.Merge(type.Decode(args[0], null), type.Decode(args[1], null)));
                return true;
            }
            catch (Exception exception)
            {
                var what = $"the lattice '{lattice.Name}' of function '{functionName}' threw {exception.GetType().Name}: {exception.Message}";
                ScopeRuntime.Of(graph, state).TryRecord(new EvaluationFailure(null, what, null, exception));
                state.ReportFailure($"function '{functionName}': lattice '{lattice.Name}' threw {exception.GetType().Name}");
                result = args[0];
                return true;
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

    }

}
