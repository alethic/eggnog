using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the executor tests. A mode string picks how <see cref="Database.RunRuleSet"/> runs, standing in
    /// for egglog's <c>run_serial_and_parallel</c> (core-relations/src/tests.rs:29-38): <c>"Serial"</c> (one thread, the
    /// deterministic path), <c>"Parallel1"</c> (the parallel path on one thread) and <c>"Parallel4"</c> (the parallel
    /// path on four threads), the latter two forced regardless of the database's size.
    /// </summary>
    static class ExecutionTestUtil
    {

        /// <summary>
        /// The modes, for theories.
        /// </summary>
        public static readonly string[] Modes = ["Serial", "Parallel1", "Parallel4"];

        /// <summary>
        /// Gets the parallelism settings of a mode.
        /// </summary>
        public static ParallelHeuristics Parallelism(string mode) => mode switch
        {
            "Serial" => ParallelHeuristics.Serial,
            "Parallel1" => ParallelHeuristics.Serial,
            "Parallel4" => new ParallelHeuristics(4),
            _ => throw new ArgumentException("Unknown mode " + mode, nameof(mode)),
        };

        /// <summary>
        /// Creates an empty database running rule sets in <paramref name="mode"/>.
        /// </summary>
        public static Database NewDatabase(string mode)
        {
            var db = new Database(Parallelism(mode));
            Configure(db, mode);
            return db;
        }

        /// <summary>
        /// Makes <paramref name="db"/> run rule sets in <paramref name="mode"/>.
        /// </summary>
        public static void Configure(Database db, string mode)
        {
            db.ParallelRuleSetsOverride = mode == "Serial" ? null : true;
        }

        /// <summary>
        /// Gets the sliding windows of <paramref name="size"/> items.
        /// </summary>
        public static IEnumerable<uint[]> Windows(uint[] items, int size)
        {
            for (var i = 0; i + size <= items.Length; i++)
                yield return items[i..(i + size)];
        }

        /// <summary>
        /// Creates an external function that records its arguments (thread-safely) and returns its first argument, or
        /// zero without arguments.
        /// </summary>
        public static ExternalFunction Recorder(List<uint[]> log)
        {
            return ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var copy = args.ToArray();
                lock (log)
                    log.Add(copy);

                result = copy.Length == 0 ? 0 : copy[0];
                return true;
            });
        }

        /// <summary>
        /// Lists the row ids of a subset.
        /// </summary>
        public static int[] Ids(SubsetRef subset)
        {
            var ids = new List<int>();
            foreach (var row in subset)
                ids.Add(row);

            return ids.ToArray();
        }

        /// <summary>
        /// Formats recorded tuples as a sorted set of strings.
        /// </summary>
        public static SortedSet<string> Distinct(IEnumerable<uint[]> tuples) => new(tuples.Select(t => string.Join(",", t)), StringComparer.Ordinal);

    }

}
