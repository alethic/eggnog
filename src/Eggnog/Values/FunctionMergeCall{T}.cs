using System;

namespace Eggnog
{

    /// <summary>
    /// The merge external function of a function whose merge is a function of the old and the new value
    /// (<see cref="FunctionMerge.Of{T}"/>): <c>[old, new] → Encode(Merge(Decode(old), Decode(new)))</c>, registered before
    /// its table and used as <c>MergeFn.Primitive(merge, Old, New)</c>, not a lattice merge.
    /// </summary>
    /// <remarks>
    /// The delegate runs under <see cref="EngineCallMarker"/>, on whatever thread merges the table. An exception from it
    /// is recorded as an evaluation failure of the scope, as a lattice's is (<see cref="LatticeMerge{T}"/>), and the old
    /// value is kept. Immutable and thread-safe.
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class FunctionMergeCall<T> : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string functionName;
        readonly FunctionMergeDefinition<T> merge;
        readonly ColumnType<T> type;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="merge">The merge, a function of the old and the new value.</param>
        /// <param name="type">The function's output type.</param>
        public FunctionMergeCall(EGraph graph, string functionName, FunctionMergeDefinition<T> merge, ColumnType<T> type)
        {
            this.graph = graph;
            this.functionName = functionName;
            this.merge = merge;
            this.type = type;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                result = type.Encode(merge.Function!(type.Decode(args[0], null), type.Decode(args[1], null)));
                return true;
            }
            catch (Exception exception)
            {
                var what = $"the merge '{merge.Name}' of function '{functionName}' threw {exception.GetType().Name}: {exception.Message}";
                ScopeRuntime.Of(graph, state).TryRecord(new EvaluationFailure(null, what, null, exception));
                state.ReportFailure($"function '{functionName}': merge '{merge.Name}' threw {exception.GetType().Name}");
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
