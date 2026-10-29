using System;
using System.Collections.Concurrent;

namespace Eggnog
{

    /// <summary>
    /// A primitive called by a merge expression (<see cref="FunctionMerge.Expression{T}"/>): it decodes the arguments,
    /// invokes the primitive under <see cref="EngineCallMarker"/> and encodes the result, as a rule's head computation
    /// does, but takes its classes as they are, as egglog's merge primitives do: a container it returns holds them
    /// uncanonicalized, and its tables read and write them so (<see cref="EnginePrimitiveTables"/>), so that within a
    /// run it does not see the run's own unions; the next rebuild makes what it returns canonical.
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
        readonly Relations.UnionAction unionAction;
        readonly ConcurrentDictionary<Relations.FunctionId, Relations.TableAction>? actions;

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
            if (primitive.IsWrite)
                unionAction = new Relations.UnionAction(graph.Engine);
            else if (primitive.UsesTables)
                actions = new();
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            Relations.ContainerView.EnterEngineCall(state);
            // No canonicalizer, not even an outer call's: the merge takes its classes as they are.
            var canonicalizer = ClassCanonicalizer.Enter(null);
            try
            {
                if (primitive.IsWrite)
                {
                    // A write primitive in a merge, as egglog's (vec-union old new): its unions staged through the
                    // merge's state, which the rebuild applies, its containers read in the state's context.
                    var action = unionAction;
                    if (primitive.TryInvokeWrite(args, parameters, this.result, (x, y) => action.Union(state, x, y), state.Containers, out result) == false)
                        throw Primitive.NoResult(primitive.Name);

                    return true;
                }

                if (actions is not null)
                {
                    // A write primitive over the tables, in egglog's write context of a merge: its writes staged through
                    // the merge's state.
                    var tables = new EnginePrimitiveTables(
                        ScopeRuntime.Of(graph, state).Owner ?? throw new InvalidOperationException("A merge ran in a context with no scope."),
                        state, PrimitiveAccess.Write, primitive, actions, canonical: false);
                    try
                    {
                        var found = primitive.TryInvokeTables(args, parameters, this.result, tables, out result);
                        if (tables.Refusal is { } refused)
                            throw refused;

                        return found ? true : throw Primitive.NoResult(primitive.Name);
                    }
                    finally
                    {
                        tables.End();
                    }
                }

                result = primitive.InvokeCore(args, parameters, this.result, overClasses ? ScopeRuntime.CallScope(graph, state) : null);
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
                ClassCanonicalizer.Exit(canonicalizer);
                Relations.ContainerView.ExitEngineCall(state);
                EngineCallMarker.Exit();
            }
        }

    }

}
