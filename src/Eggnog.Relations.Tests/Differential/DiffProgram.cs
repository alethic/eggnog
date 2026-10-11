using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A differential program: tables and a list of steps, generated from a seed (<see cref="DiffGenerator"/>), run
    /// against the engine and the model in lockstep (<see cref="DiffRunner"/>), shrunk when they disagree
    /// (<see cref="DiffShrinker"/>), and printed as the failure's reproduction.
    /// </summary>
    /// <remarks>
    /// Programs are plain data and replay deterministically: steps read the epoch from the runner rather than storing
    /// it, so dropping a step while shrinking leaves the rest valid.
    /// </remarks>
    /// <param name="Seed">The seed it was generated from.</param>
    /// <param name="Profile">The name of the generator profile (<see cref="DiffProfile"/>).</param>
    /// <param name="Mode">The executor mode (<see cref="ExecutionTestUtil.Modes"/>).</param>
    /// <param name="Domain">The values cells are drawn from are below this.</param>
    /// <param name="Tables">The tables, in engine id order.</param>
    /// <param name="Steps">The steps.</param>
    /// <param name="DenseTables">The tables of four, one and three columns that queries of the lost-constraint shape
    /// (<see cref="DiffShape.Dense"/>) are written over, in that order, or <c>null</c> when the program has none.</param>
    sealed record DiffProgram(int Seed, string Profile, string Mode, int Domain, IReadOnlyList<DiffTable> Tables, IReadOnlyList<DiffStep> Steps, int[]? DenseTables = null)
    {

        /// <summary>
        /// Gets the index of the union-find, or -1.
        /// </summary>
        public int UnionFind => Tables.FirstOrDefault(t => t.IsUnionFind)?.Index ?? -1;

        /// <summary>
        /// Gets the indexes of the tables a rebuild rewrites.
        /// </summary>
        public int[] RebuildTables => Tables.Where(t => t.Rebuilds).Select(t => t.Index).ToArray();

        /// <summary>
        /// Lists the program completely (<see cref="DiffStep.Format"/>): a shrunk program, which its seed does not
        /// reproduce, can be rebuilt from the listing.
        /// </summary>
        /// <returns>The listing.</returns>
        public override string ToString() => List(complete: true);

        /// <summary>
        /// Lists the program for information, abbreviating long steps (<see cref="DiffStep.Summarize"/>); only for a
        /// program its seed reproduces, such as a freshly generated one.
        /// </summary>
        /// <returns>The listing.</returns>
        public string Summarize() => List(complete: false);

        string List(bool complete)
        {
            var b = new StringBuilder();
            b.Append("program seed=").Append(Seed).Append(" profile=").Append(Profile).Append(" mode=").Append(Mode).Append(" domain=").Append(Domain).AppendLine();
            foreach (var t in Tables)
                b.Append("  ").Append(t).AppendLine();

            for (var i = 0; i < Steps.Count; i++)
                b.Append("  ").Append(i).Append(": ").Append(complete ? Steps[i].Format(this) : Steps[i].Summarize(this)).AppendLine();

            return b.ToString();
        }

    }

}
