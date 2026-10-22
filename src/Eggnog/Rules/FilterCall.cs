using System;
using System.Collections.Concurrent;

namespace Eggnog
{

    /// <summary>
    /// A call site of a boolean filter in a rule body: produces the unit value when the primitive returns true, and
    /// nothing (which drops the match) when it returns false or, being partial, has no result. Failures are handled as in
    /// <see cref="PrimitiveCall"/>.
    /// </summary>
    internal sealed class FilterCall : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string ruleName;
        readonly Primitive primitive;
        readonly ColumnType[] parameters;
        readonly ColumnType<bool> boolean;
        readonly uint trueCell;
        readonly uint unit;
        readonly string[] argumentNames;
        readonly bool overClasses;
        readonly PrimitiveAccess context;
        readonly ConcurrentDictionary<Relations.FunctionId, Relations.TableAction>? actions;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="ruleName">The rule's name, for failures.</param>
        /// <param name="primitive">The primitive; its result is a <c>bool</c>.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="argumentNames">The arguments' source names, for failures.</param>
        /// <param name="context">The body's context, egglog's: what a primitive over the tables may do here.</param>
        public FilterCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, string[] argumentNames, PrimitiveAccess context)
        {
            this.graph = graph;
            this.ruleName = ruleName;
            this.primitive = primitive;
            this.parameters = parameters;
            this.argumentNames = argumentNames;
            boolean = graph.Values.TypeOf<bool>();
            trueCell = boolean.Encode(true);
            unit = graph.Unit.Unit;
            overClasses = Array.Exists(parameters, p => p.InvolvesClasses);
            this.context = context;
            if (primitive.UsesTables)
                actions = new();
        }

        /// <summary>
        /// Gets whether the primitive reads tables, a read primitive's (see <see cref="PrimitiveCall.ReadsTables"/>).
        /// </summary>
        public override bool ReadsTables => primitive.Access is PrimitiveAccess.Read or PrimitiveAccess.Full || primitive.DispatchesOnContext;

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                bool found;
                uint cell;
                if (actions is not null)
                {
                    // A primitive over the tables: the run's own state, in the body's context.
                    var tables = new EnginePrimitiveTables(
                        ScopeRuntime.Of(graph, state).Owner ?? throw new InvalidOperationException("A primitive ran in a context with no scope."),
                        state, context, primitive, actions);
                    try
                    {
                        found = primitive.TryInvokeTables(args, parameters, boolean, tables, out cell);
                        if (tables.Refusal is { } refused)
                            throw refused;
                    }
                    finally
                    {
                        tables.End();
                    }
                }
                else
                {
                    // The scope decodes classes; a filter over values pays nothing for it.
                    var scope = overClasses ? ScopeRuntime.Of(graph, state).Owner : null;
                    found = primitive.TryInvokeCore(args, parameters, boolean, scope, out cell);
                }

                if (found && cell == trueCell)
                {
                    result = unit;
                    return true;
                }

                result = 0;
                return false;
            }
            catch (Exception exception)
            {
                PrimitiveCall.Fail(graph, state, ruleName, primitive, parameters, argumentNames, args, exception);
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
