using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A seed of a bridge differential program: a term, a fact or a union run outside the rules, before the first
    /// iteration or between two. Its id arguments name the classes of earlier term seeds
    /// (<see cref="BridgeDiffEntry.Variable"/> with the seed's index), canonicalized when the seed runs, as the
    /// frontend passes canonical ids.
    /// </summary>
    /// <remarks>
    /// A fact of a relation whose output is the unit constant (<see cref="BridgeDiffFunction.IsNoOutput"/>) is egglog's
    /// <c>(R a b)</c>: it runs as a term of the relation (<see cref="IsInsertion"/>), minting the row's id.
    /// </remarks>
    /// <param name="Kind">What the seed does.</param>
    /// <param name="Function">The function of a term or fact; -1 for a union.</param>
    /// <param name="Args">The keys of a term, the row (keys then output) of a fact, the two terms of a union, or the
    /// first key and the number of rows of a bulk load.</param>
    /// <param name="Phase">When the seed runs: 0 before the rules are built, <c>k</c> before iteration <c>k</c>
    /// (counting from 1).</param>
    sealed record BridgeDiffSeed(BridgeDiffSeedKind Kind, int Function, BridgeDiffEntry[] Args, int Phase)
    {

        /// <summary>
        /// Gets whether the seed inserts a relation row, egglog's <c>(R a b)</c>: a fact of a relation whose output is
        /// the unit constant. It runs as a term of the relation (<see cref="EGraph.AddTerm"/>) on its keys.
        /// </summary>
        /// <param name="functions">The program's functions.</param>
        /// <returns><c>true</c> for a relation insertion.</returns>
        public bool IsInsertion(IReadOnlyList<BridgeDiffFunction> functions) => Kind == BridgeDiffSeedKind.Set && functions[Function].IsNoOutput(Args[^1]);

        /// <summary>
        /// Gets the number of ids the seed mints: one for a term or a relation insertion (even when the row exists, as
        /// <see cref="EGraph.AddTerm"/> mints one and merges it), one per row for a bulk load, none otherwise. Every
        /// evaluation mints them in seed order, so the ids the seeds mint coincide.
        /// </summary>
        /// <param name="functions">The program's functions.</param>
        /// <returns>The number of ids.</returns>
        public int MintedIds(IReadOnlyList<BridgeDiffFunction> functions) => Kind switch
        {
            BridgeDiffSeedKind.Term => 1,
            BridgeDiffSeedKind.Bulk => (int)Args[1].Value,
            BridgeDiffSeedKind.Set => IsInsertion(functions) ? 1 : 0,
            _ => 0,
        };

        /// <summary>
        /// Formats the seed egglog-style: <c>(let $s3 (Op0 $s1 $s2))</c>, <c>(set (lo0 $s1) 3)</c>, <c>(R $s1 $s2)</c>,
        /// <c>(union $s1 $s2)</c>, <c>(bulk (NumA k) for k from 0, 12000 terms)</c>.
        /// </summary>
        /// <param name="index">The seed's index, which names a term's class.</param>
        /// <param name="functions">The program's functions.</param>
        /// <returns>The text.</returns>
        public string Format(int index, IReadOnlyList<BridgeDiffFunction> functions)
        {
            string Call(string name, IEnumerable<BridgeDiffEntry> args) => "(" + name + string.Join("", args.Select(a => " " + a.FormatSeed())) + ")";

            var text = Kind switch
            {
                BridgeDiffSeedKind.Term => $"(let $s{index} {Call(functions[Function].Name, Args)})",
                BridgeDiffSeedKind.Set when IsInsertion(functions) => Call(functions[Function].Name, Args.Take(functions[Function].KeyCount)),
                BridgeDiffSeedKind.Set => $"(set {Call(functions[Function].Name, Args.Take(functions[Function].KeyCount))} {Args[^1].FormatSeed()})",
                BridgeDiffSeedKind.Bulk => $"(bulk ({functions[Function].Name} k) for k from {Args[0].Value}, {Args[1].Value} terms)",
                _ => $"(union {Args[0].FormatSeed()} {Args[1].FormatSeed()})",
            };
            return Phase == 0 ? text : $"{text} ; before iteration {Phase}";
        }

    }

}
