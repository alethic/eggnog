using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A right-hand-side action of a bridge differential rule.
    /// </summary>
    /// <param name="Kind">What the action does.</param>
    /// <param name="Function">The function a lookup, set or subsumption names; -1 otherwise.</param>
    /// <param name="Primitive">The primitive a call runs.</param>
    /// <param name="Entries">The keys of a lookup or subsumption, the row (keys then output) of a set, the two ids of a
    /// union, or the two arguments of a call.</param>
    /// <param name="Result">The variable a lookup or call binds; -1 otherwise.</param>
    sealed record BridgeDiffAction(BridgeDiffActionKind Kind, int Function, BridgeDiffPrimitive Primitive, BridgeDiffEntry[] Entries, int Result)
    {

        /// <summary>
        /// Gets whether the action binds <see cref="Result"/>.
        /// </summary>
        public bool Binds => Kind is BridgeDiffActionKind.Lookup or BridgeDiffActionKind.Call;

        /// <summary>
        /// Formats the action egglog-style: <c>(let v5 (Op0 v1 v2))</c>, <c>(set (lo0 v1) v3)</c>, <c>(R v1 v2)</c>,
        /// <c>(union v2 v5)</c>, <c>(subsume (Op0 v0 v1))</c>, <c>(let v6 (+ v3 1))</c>.
        /// </summary>
        /// <param name="functions">The program's functions.</param>
        /// <returns>The text.</returns>
        public string Format(IReadOnlyList<BridgeDiffFunction> functions)
        {
            string Call(string name, IEnumerable<BridgeDiffEntry> args) => "(" + name + string.Join("", args.Select(a => " " + a)) + ")";

            return Kind switch
            {
                BridgeDiffActionKind.Lookup => $"(let v{Result} {Call(functions[Function].Name, Entries)})",
                BridgeDiffActionKind.Set when functions[Function].IsNoOutput(Entries[^1]) => Call(functions[Function].Name, Entries.Take(functions[Function].KeyCount)),
                BridgeDiffActionKind.Set => $"(set {Call(functions[Function].Name, Entries.Take(functions[Function].KeyCount))} {Entries[^1]})",
                BridgeDiffActionKind.Union => $"(union {Entries[0]} {Entries[1]})",
                BridgeDiffActionKind.Subsume => $"(subsume {Call(functions[Function].Name, Entries)})",
                _ => $"(let v{Result} {Call(BridgeDiffPrimitives.Name(Primitive), Entries)})",
            };
        }

    }

}
