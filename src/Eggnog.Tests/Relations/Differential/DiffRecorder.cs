using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The external function every differential rule calls first: it records, per rule tag (its first argument), the
    /// values of the rule's recorded variables (the other arguments; <see cref="DiffQuery.RecordedVariables"/>), so that
    /// the engine's matches can be compared with the oracle's, distinct and with multiplicity. Thread-safe: parallel runs
    /// call it from several threads.
    /// </summary>
    sealed class DiffRecorder
    {

        readonly Dictionary<uint, List<string>> logs = new();

        /// <summary>
        /// Gets the external function.
        /// </summary>
        public ExternalFunction Function => ExternalFunction.Create(Record);

        /// <summary>
        /// Gets the keys (<see cref="DiffReferenceJoin.Key"/>) recorded under <paramref name="tag"/>, in call order.
        /// </summary>
        public List<string> Matches(uint tag)
        {
            lock (logs)
                return logs.TryGetValue(tag, out var list) ? list : new List<string>();
        }

        /// <summary>
        /// Forgets everything recorded.
        /// </summary>
        public void Clear()
        {
            lock (logs)
                logs.Clear();
        }

        bool Record(ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            var key = DiffReferenceJoin.Key(args[1..]);
            lock (logs)
            {
                if (logs.TryGetValue(args[0], out var list) == false)
                    logs.Add(args[0], list = new List<string>());

                list.Add(key);
            }

            result = 0;
            return true;
        }

    }

}
