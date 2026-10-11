using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the <see cref="EGraph"/> tests.
    /// </summary>
    static class BridgeTestUtil
    {

        /// <summary>
        /// Creates a serial e-graph, so that runs are deterministic (decision 4).
        /// </summary>
        public static EGraph NewEGraph() => new(ParallelHeuristics.Serial);

        /// <summary>
        /// Registers the 64-bit integer base type and returns its column type.
        /// </summary>
        public static ColumnTy IntType(EGraph egraph) => ColumnTy.Base(egraph.BaseValues.RegisterType<Int64Codec, long>());

        /// <summary>
        /// Boxes a 64-bit integer.
        /// </summary>
        public static Value Int(EGraph egraph, long value) => egraph.BaseValues.Get<Int64Codec, long>(value);

        /// <summary>
        /// Unboxes a 64-bit integer.
        /// </summary>
        public static long Unint(EGraph egraph, Value value) => egraph.BaseValues.Unwrap<Int64Codec, long>(value);

        /// <summary>
        /// Declares a constructor (fresh-id default, union-id merge) over the given key types with an id output.
        /// </summary>
        public static FunctionId Constructor(EGraph egraph, string name, bool canSubsume, params ColumnTy[] keys)
        {
            return egraph.AddTable(new FunctionConfig
            {
                Schema = [.. keys, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = name,
                CanSubsume = canSubsume,
            });
        }

        /// <summary>
        /// Lists the rows of a function, sorted, as (values, subsumed) pairs.
        /// </summary>
        public static List<(Value[] Values, bool Subsumed)> Rows(EGraph egraph, FunctionId function)
        {
            var rows = new List<(Value[] Values, bool Subsumed)>();
            egraph.ForEach(function, row => rows.Add((row.Values.ToArray(), row.Subsumed)));
            rows.Sort((a, b) => Compare(a.Values, b.Values));
            return rows;
        }

        /// <summary>
        /// Lists the rows of a function, sorted, as text: <c>"k0 k1 out"</c>, with <c>" (s)"</c> for subsumed rows.
        /// </summary>
        public static string[] Show(EGraph egraph, FunctionId function)
        {
            return Rows(egraph, function).Select(r => string.Join(" ", r.Values.Select(v => v.Rep)) + (r.Subsumed ? " (s)" : "")).ToArray();
        }

        /// <summary>
        /// Gets the physical row (timestamp and subsumption cell included) of a function for a key, which must exist.
        /// </summary>
        public static uint[] PhysicalRow(EGraph egraph, FunctionId function, params uint[] key)
        {
            var table = egraph.Database.GetTable(egraph.GetTable(function));
            var row = new uint[table.Arity];
            return table.TryGetRow(key, row, out _) ? row : throw new InvalidOperationException($"{egraph.GetFunctionName(function)} has no row for [{string.Join(", ", key)}].");
        }

        /// <summary>
        /// Unions two ids through a <see cref="UnionAction"/> and flushes, as the frontend's out-of-band union does.
        /// </summary>
        public static void Union(EGraph egraph, Value a, Value b)
        {
            egraph.WithExecutionState(state =>
            {
                egraph.ActionRegistry.UnionAction.Union(state, a.Rep, b.Rep);
                return 0;
            });
            egraph.FlushUpdates();
        }

        /// <summary>
        /// Registers an external function from a lambda that returns <c>null</c> to produce nothing.
        /// </summary>
        public static ExternalFunctionId Register(EGraph egraph, Func<ExecutionState, uint[], uint?> body)
        {
            return egraph.RegisterExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var r = body(state, args.ToArray());
                result = r ?? 0;
                return r.HasValue;
            }));
        }

        /// <summary>
        /// Compares value rows lexicographically.
        /// </summary>
        public static int Compare(Value[] a, Value[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = a[i].Rep.CompareTo(b[i].Rep);
                if (c != 0)
                    return c;
            }

            return a.Length.CompareTo(b.Length);
        }

    }

}
