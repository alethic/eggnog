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
    /// An exception from the primitive is recorded with the rule, the primitive and the decoded arguments, reported with
    /// <c>ExecutionState.ReportFailure</c> (which stops the engine call early and ends it in a panic), and the call
    /// produces nothing. A partial primitive's call with no result (<see cref="Primitive.IsPartial"/>) in a let produces
    /// nothing and records nothing, which drops the match, as egglog drops a body match whose <c>-?&gt;</c> primitive
    /// returns no result; in a head the primitive's function throws there, which fails the rule, where egglog panics.
    /// Immutable and thread-safe: rule tasks may call it concurrently.
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
        readonly bool overClasses;

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
            overClasses = result.Kind == ColumnKind.Sort || Array.Exists(parameters, p => p.Kind == ColumnKind.Sort);
        }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            try
            {
                if (inBody)
                {
                    if (primitive.TryInvokeCore(args, parameters, this.result, ScopeOf(state), out result) == false)
                        return false;
                }
                else
                {
                    result = primitive.InvokeCore(args, parameters, this.result, ScopeOf(state));
                }

                // A class the primitive returns may be one the host kept, stale since: canonical through the run's own
                // find, which a parallel run's workers may use, so that it joins as its class before the next rebuild.
                if (this.result.Kind == ColumnKind.Sort)
                    result = state.Canonicalize(result);

                return true;
            }
            catch (OverflowException) when (graph.IdsExhausted)
            {
                // The store's limit, met interning a result such as a pair: the run ends as for a term that needs an id,
                // discarding what it staged, and faults nothing.
                throw;
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
        /// Gets the scope a call runs in, which decodes and checks classes; only when a class crosses the call, so that a
        /// call over values pays nothing for it.
        /// </summary>
        Scope? ScopeOf(Relations.ExecutionState state) => overClasses ? ScopeRuntime.Of(graph, state).Owner : null;

        /// <summary>
        /// Records a primitive's failure in the scope's runtime and reports it to the engine, which stops the engine call
        /// early and ends it in a panic.
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
