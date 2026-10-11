using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the action tests.
    /// </summary>
    static class ActionTestUtil
    {

        /// <summary>
        /// Builds a variable.
        /// </summary>
        public static Variable V(int index) => new(index);

        /// <summary>
        /// Builds a constant operand.
        /// </summary>
        public static QueryEntry K(uint value) => QueryEntry.Const(new Value(value));

        /// <summary>
        /// Builds a column id.
        /// </summary>
        public static ColumnId C(int column) => new(column);

        /// <summary>
        /// Creates an unsorted sorted-writes table whose merge takes the incoming row.
        /// </summary>
        public static SortedWritesTable Plain(int keyCount, int arity, MergeFunction? merge = null)
        {
            return new SortedWritesTable(keyCount, arity, null, ReadOnlySpan<ColumnId>.Empty, merge ?? ClosureMerge.TakeNew, ParallelHeuristics.Serial);
        }

        /// <summary>
        /// Stages <paramref name="rows"/> into <paramref name="table"/> and merges the database.
        /// </summary>
        public static void Fill(Database db, TableId table, params uint[][] rows)
        {
            DatabaseTestUtil.Stage(db, table, rows);
            db.MergeAll();
        }

        /// <summary>
        /// Builds a batch with the given columns.
        /// </summary>
        public static Bindings Batch(params (int Var, uint[] Values)[] columns)
        {
            var bindings = new Bindings();
            foreach (var (v, values) in columns)
                bindings.Insert(V(v), values);

            return bindings;
        }

        /// <summary>
        /// Copies the values of a variable out of a batch.
        /// </summary>
        public static uint[] Column(Bindings bindings, int variable) => bindings[V(variable)].ToArray();

        /// <summary>
        /// Flushes the state and merges the database.
        /// </summary>
        public static bool Commit(Database db, ExecutionState state)
        {
            state.Flush();
            return db.MergeAll();
        }

        /// <summary>
        /// Creates an external function from a lambda over an argument array that returns <c>null</c> to fail, and logs
        /// every call into <paramref name="log"/>.
        /// </summary>
        public static ExternalFunction Function(Func<ExecutionState, uint[], uint?> body, List<(string Name, uint[] Args)>? log = null, string name = "f")
        {
            return ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var copy = args.ToArray();
                log?.Add((name, copy));
                var r = body(state, copy);
                result = r ?? 0;
                return r.HasValue;
            });
        }

        /// <summary>
        /// The stale marker.
        /// </summary>
        public const uint S = Value.StaleRep;

    }

}
