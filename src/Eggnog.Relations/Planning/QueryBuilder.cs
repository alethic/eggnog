using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds a <see cref="Query"/>: variables, atoms with their constraints and functional dependencies, and the marks
    /// the action sets on the variables it reads or defines. egglog's <c>QueryBuilder</c> plus the variable marking of
    /// <c>RuleBuilder</c> (core-relations/src/query.rs:282-463, 517-576).
    /// </summary>
    /// <remarks>
    /// Single use: <see cref="Build"/> hands the collected state to the query, after which the builder throws.
    /// Not thread-safe. Cold path.
    /// </remarks>
    internal sealed class QueryBuilder
    {

        readonly IQuerySchema schema;
        readonly DenseIdMap<Variable, VarInfo> variables = new();
        readonly DenseIdMap<AtomId, QueryAtom> atoms = new();
        readonly FunDeps funDeps = new();
        bool built;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="schema">Describes the tables atoms may name.</param>
        public QueryBuilder(IQuerySchema schema)
        {
            ArgumentNullException.ThrowIfNull(schema);
            this.schema = schema;
        }

        /// <summary>
        /// Gets or sets the stage-ordering strategy. egglog's <c>set_plan_strategy</c>.
        /// </summary>
        public PlanStrategy Strategy { get; set; }

        /// <summary>
        /// Gets or sets whether the planner skips tree decomposition. egglog's <c>set_no_decomp</c>.
        /// </summary>
        public bool NoDecomposition { get; set; }

        /// <summary>
        /// Creates a variable. egglog's <c>new_var</c> / <c>new_var_named</c>.
        /// </summary>
        /// <param name="name">A name for diagnostics, or <c>null</c>.</param>
        /// <returns>The variable.</returns>
        public Variable NewVariable(string? name = null)
        {
            ThrowIfBuilt();
            return variables.Push(new VarInfo(name));
        }

        /// <summary>
        /// Marks <paramref name="variable"/> as read by the action. egglog's <c>mark_used</c>. Wildcards are ignored.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public void MarkUsed(Variable variable)
        {
            ThrowIfBuilt();
            if (variable.IsPlaceholder == false)
                GetInfo(variable).UsedInRhs = true;
        }

        /// <summary>
        /// Marks <paramref name="variable"/> as defined by the action (bound by a lookup or a call, not by the query).
        /// egglog's <c>mark_defined</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public void MarkDefined(Variable variable)
        {
            ThrowIfBuilt();
            if (variable.IsPlaceholder == false)
                GetInfo(variable).DefinedInRhs = true;
        }

        /// <summary>
        /// Adds an atom over <paramref name="table"/>. egglog's <c>add_atom</c> (core-relations/src/query.rs:355-463):
        /// each constant becomes an <see cref="ConstraintKind.EqConst"/> after the given constraints; the constraints
        /// are split into fast and slow ones; wildcards bind nothing; a repeated variable adds a slow
        /// <see cref="ConstraintKind.Eq"/> to its previous column (so <c>R(x,x,x)</c> gives <c>c1 == c0</c>,
        /// <c>c2 == c1</c>); each variable gets one occurrence listing its columns; and the key-column variables
        /// determine the value-column variables.
        /// </summary>
        /// <remarks>
        /// Fix (core-relations/src/query.rs:448-460): egglog passes a wildcard into the functional dependency as if it
        /// were a variable; in a consequent it would be inserted into the closure (index <c>u32::MAX</c>). Here a
        /// dependency whose key holds a wildcard is dropped, since the remaining key variables do not determine the
        /// values, and wildcards are left out of consequents.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="entries">One entry per column.</param>
        /// <param name="constraints">Extra constraints on the atom's rows.</param>
        /// <returns>The atom's identifier.</returns>
        /// <exception cref="ArgumentException">The entry count differs from the arity, a constraint names a column out
        /// of range, or an entry names an unknown variable.</exception>
        public AtomId AddAtom(TableId table, ReadOnlySpan<QueryEntry> entries, ReadOnlySpan<Constraint> constraints = default)
        {
            ThrowIfBuilt();
            var arity = schema.GetArity(table);
            if (entries.Length != arity)
                throw new ArgumentException($"Table {table} expected {arity} columns but got {entries.Length}.", nameof(entries));

            foreach (var c in constraints)
            {
                if (c.Column >= arity || (c.Kind == ConstraintKind.Eq && c.OtherColumn >= arity))
                    throw new ArgumentException($"Constraint {c} on table {table} references a column beyond its arity {arity}.", nameof(constraints));
            }

            for (var i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.IsVariable && e.IsWildcard == false && variables.ContainsKey(e.Variable) == false)
                    throw new ArgumentException($"Unknown variable {e.Variable}.", nameof(entries));
            }

            var all = new List<Constraint>(constraints.Length + entries.Length);
            foreach (var c in constraints)
                all.Add(c);
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].IsVariable == false)
                    all.Add(Constraint.EqConst(new ColumnId(i), entries[i].Constant));

            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            schema.SplitConstraints(table, CollectionsMarshal.AsSpan(all), fast, slow);

            var atomId = atoms.NextId;
            var varColumns = new VarColumnMap();
            var occurrenceVars = new List<Variable>();
            var occurrenceColumns = new List<List<ColumnId>>();
            for (var i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.IsVariable == false || e.IsWildcard)
                    continue;

                var column = new ColumnId(i);
                if (varColumns.Insert(e.Variable, column, out var previous))
                    slow.Add(Constraint.Eq(column, previous));

                var k = occurrenceVars.IndexOf(e.Variable);
                if (k < 0)
                {
                    occurrenceVars.Add(e.Variable);
                    occurrenceColumns.Add(new List<ColumnId>());
                    k = occurrenceVars.Count - 1;
                }

                occurrenceColumns[k].Add(column);
            }

            for (var k = 0; k < occurrenceVars.Count; k++)
                GetInfo(occurrenceVars[k]).Occurrences.Add(new SubAtom(atomId, occurrenceColumns[k].ToArray()));

            var keyCount = schema.GetKeyCount(table);
            var antecedent = new List<Variable>();
            var consequent = new List<Variable>();
            var keyHasWildcard = false;
            for (var i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.IsVariable == false)
                    continue;

                if (i < keyCount)
                {
                    if (e.IsWildcard)
                        keyHasWildcard = true;
                    else
                        antecedent.Add(e.Variable);
                }
                else if (e.IsWildcard == false)
                {
                    consequent.Add(e.Variable);
                }
            }

            if (keyHasWildcard == false)
                funDeps.AddDependency(antecedent.ToArray(), consequent.ToArray());

            var pushed = atoms.Push(new QueryAtom(table, varColumns, fast.ToArray(), slow.ToArray()));
            Debug.Assert(pushed == atomId);
            return atomId;
        }

        /// <summary>
        /// Finishes the query. The builder cannot be used afterwards.
        /// </summary>
        /// <param name="action">The action the query's matches feed.</param>
        /// <returns>The query.</returns>
        public Query Build(ActionId action)
        {
            ThrowIfBuilt();
            built = true;
            return new Query(variables, atoms, funDeps, action, Strategy, NoDecomposition);
        }

        VarInfo GetInfo(Variable variable)
        {
            if (variables.TryGetValue(variable, out var info) == false)
                throw new ArgumentException($"Unknown variable {variable}.", nameof(variable));

            return info;
        }

        void ThrowIfBuilt()
        {
            if (built)
                throw new InvalidOperationException("The query has already been built.");
        }

    }

}
