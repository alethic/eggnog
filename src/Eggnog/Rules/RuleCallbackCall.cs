using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// The head action of a rule callback: egglog's <c>RustRuleRhs</c> (or <c>RustRuleFullRhs</c>), a write primitive the
    /// rule's head calls with its variables' values, which runs the callback over a <see cref="RuleCallbackContext"/> and
    /// produces the unit value. A callback that throws, or whose write is refused, records the failure as a primitive's
    /// does and ends the engine call in a panic.
    /// </summary>
    internal sealed class RuleCallbackCall : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly string ruleName;
        readonly RuleCallbackDefinition definition;
        readonly string[] names;
        readonly ConcurrentDictionary<Relations.FunctionId, Relations.TableAction> actions = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="ruleName">The rule's name.</param>
        /// <param name="definition">The callback.</param>
        /// <param name="variables">The variables the callback sees, in argument order.</param>
        /// <param name="types">Their column types.</param>
        public RuleCallbackCall(EGraph graph, string ruleName, RuleCallbackDefinition definition, Term[] variables, ColumnType[] types)
        {
            this.graph = graph;
            this.ruleName = ruleName;
            this.definition = definition;
            Types = types;
            names = new string[variables.Length];
            var indexes = new Dictionary<Term, int>(variables.Length, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < variables.Length; i++)
            {
                indexes.Add(variables[i], i);
                names[i] = variables[i].Shape.Name ?? "?";
            }

            Variables = indexes;
        }

        /// <summary>
        /// Gets the column types of the variables the callback sees.
        /// </summary>
        public ColumnType[] Types { get; }

        /// <summary>
        /// Gets the argument index of each variable the callback sees.
        /// </summary>
        public IReadOnlyDictionary<Term, int> Variables { get; }

        /// <summary>
        /// Gets whether the callback reads tables beyond its match: a full callback's does, so its rule never seeds a
        /// watermark from an ancestor's (and it is naive anyway); a writing callback's depends on its match alone.
        /// </summary>
        public override bool ReadsTables => definition.Full;

        /// <summary>
        /// Gets the engine's table action of a table, made once per table.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The action.</returns>
        public Relations.TableAction Action(TableHandle table) =>
            actions.GetOrAdd(table.Function, static (function, engine) => new Relations.TableAction(engine, function), graph.Engine);

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            result = graph.Unit.Unit;
            var runtime = ScopeRuntime.Of(graph, state);
            RuleCallbackContext? context = null;
            EngineCallMarker.Enter();
            try
            {
                var scope = runtime.Owner ?? throw new InvalidOperationException("A rule callback ran in a context with no scope.");
                context = definition.Full
                    ? new FullRuleCallbackContext(this, state, scope, args)
                    : new RuleCallbackContext(this, state, scope, args);
                definition.Invoke(context);

                // A refusal the callback caught still fails the rule.
                if (context.Refusal is { } refused)
                    return Fail(runtime, state, args, refused);

                return true;
            }
            catch (OverflowException) when (graph.IdsExhausted)
            {
                // The store's limit, met interning a value the callback wrote: as for a term that needs an id.
                throw;
            }
            catch (Exception exception)
            {
                return Fail(runtime, state, args, context?.Refusal ?? exception);
            }
            finally
            {
                context?.End();
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Records the callback's failure in the scope's runtime and reports it to the engine, which ends the call in a
        /// panic, as a primitive's failure.
        /// </summary>
        /// <returns><c>false</c>: the call has no result.</returns>
        bool Fail(ScopeRuntime runtime, Relations.ExecutionState state, ReadOnlySpan<uint> args, Exception exception)
        {
            var what = $"rule '{ruleName}': its callback threw {exception.GetType().Name}: {exception.Message}";
            runtime.TryRecord(new EvaluationFailure(ruleName, what, Bindings(args), exception));
            state.ReportFailure($"rule '{ruleName}': its callback threw {exception.GetType().Name}");
            return false;
        }

        /// <summary>
        /// Renders the variables' values for <see cref="RuleEvaluationException.Bindings"/>.
        /// </summary>
        string Bindings(ReadOnlySpan<uint> args)
        {
            var bindings = new StringBuilder();
            for (var i = 0; i < args.Length && i < Types.Length; i++)
            {
                if (i > 0)
                    bindings.Append(' ');

                string value;
                try
                {
                    value = Types[i].FormatCell(args[i]);
                }
                catch (Exception)
                {
                    value = "?";
                }

                bindings.Append(names[i]).Append('=').Append(value);
            }

            return bindings.ToString();
        }

    }

}
