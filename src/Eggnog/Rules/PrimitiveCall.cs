using System;
using System.Collections.Concurrent;
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
        readonly bool joins;
        readonly bool overClasses;
        readonly Relations.UnionAction unionAction;
        readonly PrimitiveAccess context;
        readonly ConcurrentDictionary<Relations.FunctionId, Relations.TableAction>? actions;

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
        /// <param name="context">The call site's context, egglog's: what a primitive over the tables may do here.</param>
        /// <param name="joins">Whether the let's result is compared with a value the body binds otherwise, an atom's
        /// column or an earlier let's: a container of classes the scope cannot make, and its view does not hold, then
        /// drops the match, since it equals no cell.</param>
        public PrimitiveCall(EGraph graph, string ruleName, Primitive primitive, ColumnType[] parameters, ColumnType result, string[] argumentNames,
            bool inBody, PrimitiveAccess context, bool joins = false)
        {
            this.graph = graph;
            this.ruleName = ruleName;
            this.primitive = primitive;
            this.parameters = parameters;
            this.result = result;
            this.argumentNames = argumentNames;
            this.inBody = inBody;
            this.joins = joins;
            overClasses = result.InvolvesClasses || Array.Exists(parameters, p => p.InvolvesClasses);
            if (primitive.IsWrite)
                unionAction = new Relations.UnionAction(graph.Engine);

            this.context = context;
            if (primitive.UsesTables)
                actions = new();
        }

        /// <summary>
        /// Gets whether the primitive reads tables, a read or full primitive's: its result may then depend on rows a
        /// descendant context adds, so its rule never seeds a watermark from an ancestor's.
        /// </summary>
        /// <remarks>So does a function value's application, which reads where the context does.</remarks>
        public override bool ReadsTables => primitive.Access is PrimitiveAccess.Read or PrimitiveAccess.Full || primitive.DispatchesOnContext;

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            EngineCallMarker.Enter();
            Relations.ContainerView.EnterEngineCall(state);
            var canonicalizer = this.result.HoldsClasses ? ClassCanonicalizer.Enter(state.Canonicalize) : null;
            try
            {
                if (actions is not null)
                {
                    // A primitive over the tables: the run's own state, in the call site's context.
                    var tables = new EnginePrimitiveTables(
                        ScopeRuntime.Of(graph, state).Owner ?? throw new InvalidOperationException("A primitive ran in a context with no scope."),
                        state, context, primitive, actions);
                    try
                    {
                        var found = primitive.TryInvokeTables(args, parameters, this.result, tables, out result);
                        if (tables.Refusal is { } refused)
                            throw refused;

                        if (found == false)
                        {
                            if (inBody)
                                return false;

                            throw Primitive.NoResult(primitive.Name);
                        }
                    }
                    finally
                    {
                        tables.End();
                    }
                }
                else if (inBody)
                {
                    if (primitive.TryInvokeCore(args, parameters, this.result, ScopeOf(state), out result) == false)
                        return false;
                }
                else if (primitive.IsWrite)
                {
                    // egglog's write primitive in an action: its unions staged through the run's state, as a head's, its
                    // containers read in the state's context.
                    var action = unionAction;
                    if (primitive.TryInvokeWrite(args, parameters, this.result, (x, y) => action.Union(state, x, y), state.Containers, out result) == false)
                        throw Primitive.NoResult(primitive.Name);
                }
                else
                {
                    result = primitive.InvokeCore(args, parameters, this.result, ScopeOf(state));
                }

                // A class the primitive returns may be one the host kept, stale since: canonical through the run's own
                // find, which a parallel run's workers may use, so that it joins as its class before the next rebuild;
                // so is a container of classes.
                if (this.result.InvolvesClasses)
                    result = state.Canonicalize(result);

                return true;
            }
            catch (OverflowException) when (graph.IdsExhausted)
            {
                // The store's limit, met interning a result such as a pair: the run ends as for a term that needs an id,
                // discarding what it staged, and faults nothing.
                throw;
            }
            catch (UninternedContainerException) when (joins)
            {
                // A container of classes this scope cannot make and its view does not hold equals no cell it is compared
                // with, as egglog's newly interned one is in no row.
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
                if (this.result.HoldsClasses)
                    ClassCanonicalizer.Exit(canonicalizer);

                Relations.ContainerView.ExitEngineCall(state);
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Gets the scope a call runs in, which decodes and checks classes, and through whose container view the call reads
        /// containers of classes (<see cref="ScopeRuntime.CallScope"/>); only when a class crosses the call, so that a
        /// call over values pays nothing for it. For a query on a frozen scope it is that scope, whose view reads as the
        /// transient child's the state runs in does, since a transient child holds no container itself.
        /// </summary>
        Scope? ScopeOf(Relations.ExecutionState state) => overClasses ? ScopeRuntime.CallScope(graph, state) : null;

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
            var view = state.Containers;
            for (var i = 0; i < args.Length && i < parameters.Length; i++)
            {
                if (i > 0)
                    bindings.Append(' ');

                string value;
                try
                {
                    value = Binding(parameters[i], args[i], view);
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
        /// Renders one argument for <see cref="RuleEvaluationException.Bindings"/>: a set of values as <c>{a, b}</c>
        /// (<see cref="Set{T}.ToString"/>), any other value as its literal (<c>"b"</c>, <c>42</c>, a set of classes
        /// <c>(set-of #1 #2)</c>), which needs no scope.
        /// </summary>
        /// <param name="type">The argument's column type.</param>
        /// <param name="cell">The argument's cell.</param>
        /// <param name="view">The containers of the context the call ran in, which a container's cell is read
        /// through.</param>
        /// <returns>The rendering.</returns>
        static string Binding(ColumnType type, uint cell, Relations.ContainerView view)
        {
            if (type.ClrType.IsGenericType && type.ClrType.GetGenericTypeDefinition() == typeof(Set<>) && type.InvolvesClasses == false)
                return type.DecodeObject(cell, null).ToString() ?? string.Empty;

            return type.FormatCell(cell, view);
        }

    }

}
