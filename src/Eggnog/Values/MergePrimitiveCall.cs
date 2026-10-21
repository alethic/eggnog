using System;

namespace Eggnog
{

    /// <summary>
    /// A primitive called by a merge expression (<see cref="FunctionMerge.Expression{T}"/>): it decodes the arguments,
    /// invokes the primitive under <see cref="EngineCallMarker"/> and encodes the result, as a rule's head computation
    /// does.
    /// </summary>
    /// <remarks>
    /// A primitive that throws, or a partial one with no result, fails the merge: the failure is recorded as an evaluation
    /// failure of the scope, naming the merge and its function, and the engine's merge panics, as egglog's does ("Merge
    /// function for f primitive call failed"). Immutable and thread-safe.
    /// </remarks>
    internal sealed class MergePrimitiveCall : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string functionName;
        readonly string mergeName;
        readonly Primitive primitive;
        readonly ColumnType[] parameters;
        readonly ColumnType result;
        readonly bool overClasses;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The name of the function whose merge calls it, for failures.</param>
        /// <param name="mergeName">The merge's rendering, for failures.</param>
        /// <param name="primitive">The primitive.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        public MergePrimitiveCall(EGraph graph, string functionName, string mergeName, Primitive primitive, ColumnType[] parameters, ColumnType result)
        {
            this.graph = graph;
            this.functionName = functionName;
            this.mergeName = mergeName;
            this.primitive = primitive;
            this.parameters = parameters;
            this.result = result;
            overClasses = result.InvolvesClasses || Array.Exists(parameters, p => p.InvolvesClasses);
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                result = primitive.InvokeCore(args, parameters, this.result, overClasses ? ScopeRuntime.Of(graph, state).Owner : null);
                return true;
            }
            catch (Exception exception)
            {
                var what = $"the merge {mergeName} of function '{functionName}': primitive '{primitive.Name}' threw {exception.GetType().Name}: {exception.Message}";
                ScopeRuntime.Of(graph, state).TryRecord(new EvaluationFailure(null, what, null, exception));
                state.ReportFailure($"function '{functionName}': merge {mergeName}: primitive '{primitive.Name}' threw {exception.GetType().Name}");
                result = 0;
                return false;
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

    }

}
