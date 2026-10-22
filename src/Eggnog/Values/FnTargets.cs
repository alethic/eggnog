using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The function values' targets of a graph (<see cref="FnTarget"/>), by id, which their containers hold: each table or
    /// primitive once per number of partially applied arguments, resolved against a function sort as egglog's
    /// <c>resolve_function_container_target_with_context</c> (src/lib.rs:2275-2393) resolves a name. Thread-safe.
    /// </summary>
    internal sealed class FnTargets
    {

        readonly EGraph graph;
        readonly object sync = new();
        readonly Dictionary<object, List<FnTarget>> byTarget = new(ReferenceEqualityComparer.Instance);
        FnTarget[] byId = [];

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public FnTargets(EGraph graph)
        {
            this.graph = graph;
        }

        /// <summary>
        /// Gets the target of an id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The target.</returns>
        public FnTarget this[int id] => Volatile.Read(ref byId)[id];

        /// <summary>
        /// Gets or resolves the target of a function value of a function sort: its partially applied arguments are the
        /// target's first parameters, and the sort's inputs and output must be its other parameters and its result, by
        /// type, as egglog's resolution requires.
        /// </summary>
        /// <param name="target">A constructor or a function of the graph, or a primitive.</param>
        /// <param name="inputs">The function sort's inputs.</param>
        /// <param name="output">The function sort's output.</param>
        /// <returns>The target.</returns>
        /// <exception cref="ArgumentException">The target is not a table of the graph or a primitive, or its signature
        /// does not end with the sort's inputs and output.</exception>
        public FnTarget Resolve(object target, ColumnType[] inputs, ColumnType output)
        {
            ArgumentNullException.ThrowIfNull(target);
            var (kind, parameters, result) = Signature(target);
            var partial = parameters.Length - inputs.Length;
            if (partial < 0 || ReferenceEquals(result, output) == false || Matches(parameters, partial, inputs) == false)
                throw new ArgumentException(
                    $"function container lookup for '{Name(target)}' expected ({Names(parameters, 0, Math.Max(partial, 0))}{(partial > 0 && inputs.Length > 0 ? ", " : "")}{Names(inputs, 0, inputs.Length)}) -> {output.Name}, found ({Names(parameters, 0, parameters.Length)}) -> {result.Name}.",
                    nameof(target));

            lock (sync)
            {
                if (byTarget.TryGetValue(target, out var resolved))
                {
                    foreach (var existing in resolved)
                        if (existing.Partial.Length == partial)
                            return existing;
                }
                else
                {
                    byTarget.Add(target, resolved = new List<FnTarget>());
                }

                var made = new FnTarget(byId.Length, kind, target, parameters[..partial], inputs, output);
                resolved.Add(made);
                Volatile.Write(ref byId, [.. byId, made]);
                return made;
            }
        }

        /// <summary>
        /// Gets a target's kind, parameters and result.
        /// </summary>
        (FnTargetKind Kind, ColumnType[] Parameters, ColumnType Result) Signature(object target)
        {
            switch (target)
            {
                case TableHandle table:
                {
                    if (ReferenceEquals(table.Graph, graph) == false)
                        throw new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(target));

                    if (table.ReadScope is not null)
                        throw new ArgumentException($"'{table}' is a scoped view, which a function value does not apply.", nameof(target));

                    if (table.IsRelation)
                        throw FnValue.RelationIsNoTarget(table);

                    return (table.Kind == TableKind.Constructor ? FnTargetKind.Constructor : FnTargetKind.Function, table.KeyArray, table.Output!);
                }
                case Primitive primitive:
                {
                    var parameters = new ColumnType[primitive.ParameterTypes.Count];
                    for (var i = 0; i < parameters.Length; i++)
                        parameters[i] = graph.Values.TypeOf(primitive.ParameterTypes[i]);

                    return (FnTargetKind.Primitive, parameters, graph.Values.TypeOf(primitive.ResultType));
                }
                default:
                    throw new ArgumentException($"A function value applies a constructor, a function or a primitive, not a {target.GetType().Name}.", nameof(target));
            }
        }

        /// <summary>
        /// Gets whether a target's parameters end with the inputs.
        /// </summary>
        static bool Matches(ColumnType[] parameters, int partial, ColumnType[] inputs)
        {
            for (var i = 0; i < inputs.Length; i++)
                if (ReferenceEquals(parameters[partial + i], inputs[i]) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Gets a target's name.
        /// </summary>
        static string Name(object target) => target is TableHandle table ? table.Name : ((Primitive)target).Name;

        /// <summary>
        /// Lists column types' names.
        /// </summary>
        static string Names(ColumnType[] types, int start, int count) =>
            string.Join(", ", Array.ConvertAll(types[start..(start + count)], type => type.Name));

    }

}
