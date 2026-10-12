using System;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// A call site of a primitive in a rule: a let (a left-hand-side <c>QueryPrim</c>) or a head computation
    /// (<c>CallExternalFunc</c>). It decodes the arguments, invokes the primitive under <see cref="EngineCallMarker"/>
    /// and encodes the result.
    /// </summary>
    /// <remarks>
    /// An exception from the primitive is recorded with the rule, the primitive and the decoded arguments (D31), reported
    /// with <c>ExecutionState.ReportFailure</c> (which stops the engine call early and ends it in a panic), and the call
    /// produces nothing. The exception is a partial primitive's "no result" (<see cref="Primitive.IsPartial"/>, an
    /// <see cref="OverflowException"/>) in a let: the call then produces nothing and records nothing, which drops the
    /// match, as egglog drops a body match whose <c>-?&gt;</c> primitive returns no result. Immutable and thread-safe:
    /// rule tasks may call it concurrently.
    /// </remarks>
    internal sealed class PrimitiveCall : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string ruleName;
        readonly Primitive primitive;
        readonly ColumnType[] parameters;
        readonly ColumnType result;
        readonly string[] argumentNames;
        readonly bool inBody;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="ruleName">The rule's name, for failures.</param>
        /// <param name="primitive">The primitive.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <param name="argumentNames">The arguments' source names, for failures.</param>
        /// <param name="inBody">Whether the call site is a let of the body (a <c>QueryPrim</c>), where a partial
        /// primitive's no result drops the match; <c>false</c> for a head computation, where it fails the rule.</param>
        public PrimitiveCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, ColumnType result, string[] argumentNames,
            bool inBody)
        {
            this.graph = graph;
            this.ruleName = ruleName;
            this.primitive = primitive;
            this.parameters = parameters;
            this.result = result;
            this.argumentNames = argumentNames;
            this.inBody = inBody;
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                result = primitive.InvokeCore(args, parameters, this.result);
                return true;
            }
            catch (OverflowException) when (inBody && primitive.IsPartial)
            {
                result = 0;
                return false;
            }
            catch (Exception exception)
            {
                Fail(graph, state, ruleName, primitive, parameters, argumentNames, args, exception);
                result = 0;
                return false;
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Records a primitive's failure in the scope's runtime and reports it to the engine (D31).
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="ruleName">The rule's name.</param>
        /// <param name="primitive">The primitive.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="argumentNames">The arguments' source names.</param>
        /// <param name="args">The argument cells.</param>
        /// <param name="exception">What the primitive threw.</param>
        internal static void Fail(EGraph graph, Relations.ExecutionState state, string ruleName, Primitive primitive, ColumnType[] parameters,
            string[] argumentNames, ReadOnlySpan<uint> args, Exception exception)
        {
            var bindings = new StringBuilder();
            for (var i = 0; i < args.Length && i < parameters.Length; i++)
            {
                if (i > 0)
                    bindings.Append(' ');

                string value;
                try
                {
                    value = Binding(parameters[i], args[i]);
                }
                catch (Exception)
                {
                    value = "?";
                }

                bindings.Append(i < argumentNames.Length ? argumentNames[i] : "arg" + i).Append('=').Append(value);
            }

            var what = $"rule '{ruleName}': primitive '{primitive.Name}' threw {exception.GetType().Name}: {exception.Message}";
            ScopeRuntime.Of(graph, state).TryRecord(new EvaluationFailure(ruleName, what, bindings.ToString(), exception));
            state.ReportFailure($"rule '{ruleName}': primitive '{primitive.Name}' threw {exception.GetType().Name}");
        }

        /// <summary>
        /// Renders one argument for <see cref="RuleEvaluationException.Bindings"/>: a set as <c>{a, b}</c>
        /// (<see cref="ValueSet{T}.ToString"/>), any other value as its literal (<c>"b"</c>, <c>42</c>).
        /// </summary>
        /// <param name="type">The argument's column type.</param>
        /// <param name="cell">The argument's cell.</param>
        /// <returns>The rendering.</returns>
        static string Binding(ColumnType type, uint cell)
        {
            if (type.ClrType.IsGenericType && type.ClrType.GetGenericTypeDefinition() == typeof(ValueSet<>))
                return type.DecodeObject(cell, null).ToString() ?? string.Empty;

            return type.FormatCell(cell);
        }

    }

}
