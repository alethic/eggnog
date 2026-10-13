using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An atom of a bridge differential rule: a row of a function, one entry per column (keys, then output), and which
    /// rows it matches by subsumption.
    /// </summary>
    /// <param name="Function">The function's index in the program.</param>
    /// <param name="Entries">One entry per column of the function.</param>
    /// <param name="Subsumed">For a function that can subsume: <c>false</c> matches rows that are not subsumed (what
    /// the frontend asks for), <c>true</c> only subsumed rows, <c>null</c> both; ignored otherwise.</param>
    sealed record BridgeDiffAtom(int Function, BridgeDiffEntry[] Entries, bool? Subsumed)
    {

        /// <summary>
        /// Formats the atom egglog-style, <c>(= v2 (Op0 v0 v1))</c>, or <c>(R v0 v1)</c> for a relation atom that names
        /// no output, with <c>:subsumed</c> or <c>:any</c> appended for the matching modes the frontend does not write.
        /// </summary>
        /// <param name="functions">The program's functions.</param>
        /// <returns>The text.</returns>
        public string Format(IReadOnlyList<BridgeDiffFunction> functions)
        {
            var f = functions[Function];
            var keys = string.Join("", Entries.Take(f.KeyCount).Select(e => " " + e));
            var mode = f.CanSubsume == false || Subsumed == false ? "" : Subsumed == true ? " :subsumed" : " :any";
            var output = Entries[f.KeyCount];
            return f.IsNoOutput(output) ? $"({f.Name}{keys}){mode}" : $"(= {output} ({f.Name}{keys}){mode})";
        }

    }

}
