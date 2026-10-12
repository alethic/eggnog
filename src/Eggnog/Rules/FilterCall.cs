using System;

namespace Eggnog
{

    /// <summary>
    /// A call site of a boolean filter in a rule body: produces the unit value when the primitive returns true, and
    /// nothing (which drops the match) when it returns false. Failures are handled as in <see cref="PrimitiveCall"/>.
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

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="ruleName">The rule's name, for failures.</param>
        /// <param name="primitive">The primitive; its result is a <c>bool</c>.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="argumentNames">The arguments' source names, for failures.</param>
        public FilterCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, string[] argumentNames)
        {
            this.graph = graph;
            this.ruleName = ruleName;
            this.primitive = primitive;
            this.parameters = parameters;
            this.argumentNames = argumentNames;
            boolean = graph.Values.TypeOf<bool>();
            trueCell = boolean.Encode(true);
            unit = graph.Unit.Unit;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                if (primitive.InvokeCore(args, parameters, boolean) == trueCell)
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
