using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A rule of a bridge differential program: typed variables, atoms (the join), left-hand-side primitives, and actions,
    /// plus how the bridge plans it. Plain data, so a rule can be built into an e-graph (<see cref="BridgeDiffBridge"/>),
    /// evaluated naively (<see cref="BridgeDiffOracle"/>), printed and shrunk.
    /// </summary>
    /// <remarks>
    /// Evaluation order is the bridge's: the atoms join, then the primitives filter (or bind their results) in order, then
    /// the actions run in order, each reading only variables bound before it (<see cref="IsWellFormed"/>).
    /// </remarks>
    /// <param name="Name">The rule's description, unique in its program.</param>
    /// <param name="VarSorts">The sort of each variable.</param>
    /// <param name="Atoms">The atoms.</param>
    /// <param name="Prims">The left-hand-side primitives.</param>
    /// <param name="Actions">The actions.</param>
    /// <param name="Strategy">The plan's stage-ordering strategy.</param>
    /// <param name="NoDecomposition">Whether the planner skips tree decomposition.</param>
    sealed record BridgeDiffRule(string Name, int[] VarSorts, BridgeDiffAtom[] Atoms, BridgeDiffPrim[] Prims, BridgeDiffAction[] Actions, PlanStrategy Strategy, bool NoDecomposition)
    {

        /// <summary>
        /// Gets whether every primitive and action reads only variables bound before it: by an atom, by an earlier
        /// primitive's result, or by an earlier lookup or call. Shrinking keeps only well-formed rules.
        /// </summary>
        /// <returns><c>true</c> if the rule is well-formed.</returns>
        public bool IsWellFormed()
        {
            var bound = new bool[VarSorts.Length];
            foreach (var atom in Atoms)
                foreach (var entry in atom.Entries)
                    if (entry.IsVar)
                        bound[entry.Var] = true;

            bool Bound(BridgeDiffEntry entry) => entry.IsVar == false || bound[entry.Var];

            foreach (var prim in Prims)
            {
                if (Bound(prim.Left) == false || Bound(prim.Right) == false)
                    return false;

                if (prim.Result.IsVar)
                    bound[prim.Result.Var] = true;
            }

            foreach (var action in Actions)
            {
                if (action.Entries.All(Bound) == false)
                    return false;

                if (action.Binds)
                    bound[action.Result] = true;
            }

            return true;
        }

        /// <summary>
        /// Formats the rule egglog-style, with its plan settings as a comment.
        /// </summary>
        /// <param name="functions">The program's functions.</param>
        /// <returns>The text.</returns>
        public string Format(IReadOnlyList<BridgeDiffFunction> functions)
        {
            var b = new StringBuilder();
            b.Append("(rule (");
            b.AppendJoin(" ", Atoms.Select(a => a.Format(functions)).Concat(Prims.Select(p => p.ToString())));
            b.Append(") (");
            b.AppendJoin(" ", Actions.Select(a => a.Format(functions)));
            b.Append(") :name \"").Append(Name).Append("\")");
            b.Append(" ; ").Append(Strategy);
            if (NoDecomposition)
                b.Append(" no-decomp");

            b.Append(", vars [").AppendJoin(" ", VarSorts.Select((s, i) => "v" + i + ":" + BridgeDiffSort.Name(s))).Append(']');
            return b.ToString();
        }

    }

}
