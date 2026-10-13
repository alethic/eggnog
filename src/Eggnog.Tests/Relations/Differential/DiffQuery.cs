using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random conjunctive query with its action (<see cref="DiffGenerator"/>): atoms over the program's tables, and
    /// instructions run on every match after the recorder call that every differential rule starts with.
    /// </summary>
    /// <remarks>
    /// A query may project its matches onto a subset of its bound variables (<see cref="Projection"/>): the recorder and
    /// the instructions then read only those, so the others are variables the action never reads, which the planner may
    /// leave unexpanded, check through materialization lookups, or enumerate once per completion. The matches compared
    /// are the distinct projections, and their multiplicity is not defined.
    /// </remarks>
    /// <param name="Shape">The join graph it was generated from.</param>
    /// <param name="VariableCount">The number of query variables, created first and in order (<c>v0</c> ...); lookup
    /// results are numbered after them.</param>
    /// <param name="Atoms">The atoms; at least one.</param>
    /// <param name="Actions">The instructions after the recorder call; they read only recorded variables
    /// (<see cref="RecordedVariables"/>), constants, the epoch and lookup results.</param>
    /// <param name="Projection">The variables the recorder and the instructions may read, or <c>null</c> for every bound
    /// variable. Variables no atom binds are ignored, so the projection survives shrinking.</param>
    sealed record DiffQuery(DiffShape Shape, int VariableCount, DiffAtom[] Atoms, DiffAction[] Actions, int[]? Projection = null)
    {

        /// <summary>
        /// Gets the variables the atoms bind (every non-wildcard variable entry), ascending.
        /// </summary>
        public int[] BoundVariables()
        {
            var set = new SortedSet<int>();
            foreach (var atom in Atoms)
                foreach (var e in atom.Entries)
                    if (e.IsVariable && e.IsWildcard == false)
                        set.Add(e.Variable.Index);

            return set.ToArray();
        }

        /// <summary>
        /// Gets the bound variables the recorder reads, ascending: the columns of a match. All of them unless the query
        /// is projected.
        /// </summary>
        public int[] RecordedVariables()
        {
            var bound = BoundVariables();
            return Projection is null ? bound : bound.Where(v => Array.IndexOf(Projection, v) >= 0).ToArray();
        }

        /// <summary>
        /// Gets whether the projection leaves out some bound variable.
        /// </summary>
        public bool IsProjected => Projection is not null && RecordedVariables().Length < BoundVariables().Length;

        /// <summary>
        /// Gets the number of binding slots a model lane needs: the query variables and the lookup results.
        /// </summary>
        public int SlotCount => VariableCount + Actions.Count(a => a.Kind == DiffActionKind.Lookup);

        /// <summary>
        /// Gets whether every atom's key columns are bound and nothing is projected away, so that a match determines
        /// the row of every atom and distinct row combinations and distinct matches correspond: a single plan must then
        /// report each match exactly once (see <see cref="DiffRunner.CheckMatches"/>). A projected match may stand for
        /// any number of row combinations, which a plan may report once or once per completion.
        /// </summary>
        public bool MultiplicityDefined(DiffProgram program) => IsProjected == false && Atoms.All(a => a.KeysBound(program.Tables[a.Table]));

        /// <summary>
        /// Gets the copy with <paramref name="atoms"/>; action operands that name a query variable no longer recorded
        /// become the constant 0, so the action stays valid.
        /// </summary>
        public DiffQuery WithAtoms(DiffAtom[] atoms) => (this with { Atoms = atoms }).ReadingRecordedOnly();

        /// <summary>
        /// Gets the copy projected onto <paramref name="projection"/> (<c>null</c> for every bound variable); action
        /// operands that name a query variable no longer recorded become the constant 0.
        /// </summary>
        public DiffQuery WithProjection(int[]? projection) => (this with { Projection = projection }).ReadingRecordedOnly();

        /// <summary>
        /// Gets the copy without action <paramref name="index"/>; a removed lookup's result becomes the constant 0 in
        /// later instructions and the later lookups' results are renumbered to stay dense.
        /// </summary>
        public DiffQuery WithoutAction(int index)
        {
            var removed = Actions[index];
            var actions = Actions.Where((_, i) => i != index).ToArray();
            if (removed.Kind == DiffActionKind.Lookup)
            {
                var map = new Dictionary<int, int>();
                var next = VariableCount;
                foreach (var a in Actions)
                    if (a.Kind == DiffActionKind.Lookup && a.Result != removed.Result)
                        map[a.Result] = next++;

                actions = actions.Select(a => a with
                {
                    Result = a.Kind == DiffActionKind.Lookup ? map[a.Result] : a.Result,
                    Entries = a.Entries.Select(e => Rename(e, removed.Result, map)).ToArray(),
                }).ToArray();
            }

            return this with { Actions = actions };
        }

        /// <summary>
        /// Lists smaller variants for the shrinker: without an atom, without an instruction, without an atom's
        /// constraints, with a constant or variable entry turned into a wildcard, with one recorded variable fewer, and
        /// without the projection.
        /// </summary>
        public IEnumerable<DiffQuery> Simplify()
        {
            if (Atoms.Length > 1)
                for (var i = 0; i < Atoms.Length; i++)
                    yield return WithAtoms(Atoms.Where((_, j) => j != i).ToArray());

            for (var i = Actions.Length - 1; i >= 0; i--)
                yield return WithoutAction(i);

            for (var i = 0; i < Atoms.Length; i++)
                if (Atoms[i].Constraints.Length > 0)
                    yield return Replace(i, Atoms[i] with { Constraints = [] });

            for (var i = 0; i < Atoms.Length; i++)
            {
                for (var c = 0; c < Atoms[i].Entries.Length; c++)
                {
                    if (Atoms[i].Entries[c].IsWildcard)
                        continue;

                    var entries = (QueryEntry[])Atoms[i].Entries.Clone();
                    entries[c] = QueryEntry.Wildcard();
                    yield return WithAtoms(Atoms.Select((a, j) => j == i ? a with { Entries = entries } : a).ToArray());
                }
            }

            if (Projection is not null)
            {
                var recorded = RecordedVariables();
                foreach (var v in recorded)
                    yield return WithProjection(recorded.Where(w => w != v).ToArray());

                yield return WithProjection(null);
            }
        }

        /// <summary>
        /// Formats the query with the program's table names.
        /// </summary>
        public string Format(DiffProgram program)
        {
            var b = new StringBuilder();
            b.Append(string.Join(", ", Atoms.Select(a => a.Format(program))));
            b.Append(" => record(").Append(string.Join(", ", RecordedVariables().Select(v => "v" + v))).Append(')');
            foreach (var a in Actions)
                b.Append("; ").Append(a.Format(program));

            b.Append("   [").Append(Shape).Append(IsProjected ? ", projected" : "").Append(']');
            return b.ToString();
        }

        DiffQuery Replace(int index, DiffAtom atom) => this with { Atoms = Atoms.Select((a, j) => j == index ? atom : a).ToArray() };

        /// <summary>
        /// Gets the copy whose action operands name only recorded query variables: the others become the constant 0.
        /// </summary>
        DiffQuery ReadingRecordedOnly()
        {
            var recorded = new HashSet<int>(RecordedVariables());
            return this with
            {
                Actions = Actions.Select(a => a with { Entries = a.Entries.Select(e => IsUnrecorded(e, recorded) ? QueryEntry.Const(new Value(0)) : e).ToArray() }).ToArray(),
            };
        }

        bool IsUnrecorded(QueryEntry e, HashSet<int> recorded) => e.IsVariable && e.IsWildcard == false && e.Variable.Index < VariableCount && recorded.Contains(e.Variable.Index) == false;

        static QueryEntry Rename(QueryEntry e, int removed, Dictionary<int, int> map)
        {
            if (e.IsVariable == false || e.IsWildcard)
                return e;
            if (e.Variable.Index == removed)
                return QueryEntry.Const(new Value(0));

            return map.TryGetValue(e.Variable.Index, out var renamed) ? QueryEntry.Var(new Variable(renamed)) : e;
        }

    }

}
