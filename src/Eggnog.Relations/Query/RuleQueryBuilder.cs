using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds the query (left-hand side) of a rule: its variables, atoms and planning knobs. egglog's
    /// <c>QueryBuilder</c> (core-relations/src/query.rs:282-464), from <see cref="RuleSetBuilder.NewRule"/>;
    /// <see cref="Build"/> moves on to the right-hand side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin layer over the planner's <see cref="QueryBuilder"/>, which does egglog's <c>add_atom</c> work: constants
    /// become <see cref="ConstraintKind.EqConst"/> constraints after the given ones, the constraints are split into fast
    /// and slow ones by the database (<see cref="Database.ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/>),
    /// wildcards (<see cref="QueryEntry.Wildcard"/>) bind nothing, a repeated variable adds a slow
    /// <see cref="ConstraintKind.Eq"/> to its previous column, each variable records one occurrence per atom, and the
    /// key-column variables determine the value-column variables. This layer adds egglog's error reporting
    /// (<see cref="QueryException"/>), the link to the rule set, and the record of constant ids
    /// (<see cref="IdConstant"/>).
    /// </para>
    /// <para>
    /// Usable while it is the open rule of its <see cref="RuleSetBuilder"/> and until <see cref="Build"/>; afterwards
    /// every member throws <see cref="InvalidOperationException"/>. Not thread-safe. Cold path.
    /// </para>
    /// </remarks>
    internal sealed class RuleQueryBuilder
    {

        readonly RuleSetBuilder owner;
        readonly QueryBuilder query;
        readonly List<IdConstant> idConstants = new();
        int variableCount;

        /// <summary>
        /// Initializes a new instance. Called by <see cref="RuleSetBuilder.NewRule"/>.
        /// </summary>
        /// <param name="owner">The rule set builder.</param>
        /// <param name="query">The planner's query builder, over the owner's database.</param>
        internal RuleQueryBuilder(RuleSetBuilder owner, QueryBuilder query)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(query);
            this.owner = owner;
            this.query = query;
        }

        /// <summary>
        /// Gets or sets the stage-ordering strategy of the plan. egglog's <c>set_plan_strategy</c>; the default is
        /// <see cref="PlanStrategy.Gj"/>.
        /// </summary>
        public PlanStrategy Strategy
        {
            get => query.Strategy;
            set
            {
                owner.ThrowIfNotOpen(this);
                if (value is not (PlanStrategy.Gj or PlanStrategy.PureSize or PlanStrategy.MinCover))
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown plan strategy.");

                query.Strategy = value;
            }
        }

        /// <summary>
        /// Gets or sets whether the planner skips tree decomposition and evaluates the query as a single bag. egglog's
        /// <c>set_no_decomp</c>.
        /// </summary>
        public bool NoDecomposition
        {
            get => query.NoDecomposition;
            set
            {
                owner.ThrowIfNotOpen(this);
                query.NoDecomposition = value;
            }
        }

        /// <summary>
        /// Gets the number of variables created so far, query and action ones alike; variable ids are dense from zero.
        /// </summary>
        internal int VariableCount => variableCount;

        /// <summary>
        /// Gets the constant ids recorded so far.
        /// </summary>
        internal List<IdConstant> IdConstants => idConstants;

        /// <summary>
        /// Gets the planner's query builder.
        /// </summary>
        internal QueryBuilder Query => query;

        /// <summary>
        /// Creates a variable. egglog's <c>new_var</c> and <c>new_var_named</c>.
        /// </summary>
        /// <param name="name">A name for reports and plan dumps, or <c>null</c>.</param>
        /// <returns>The variable.</returns>
        public Variable NewVariable(string? name = null)
        {
            owner.ThrowIfNotOpen(this);
            return CreateVariable(name);
        }

        /// <summary>
        /// Adds an atom over <paramref name="table"/>: one entry per column (a variable, a constant or a wildcard) and
        /// extra constraints on its rows. egglog's <c>add_atom</c> (core-relations/src/query.rs:343-463).
        /// </summary>
        /// <remarks>
        /// Two different variables can be constrained equal this way (an <see cref="ConstraintKind.Eq"/> on their
        /// columns); it works but is not recommended, as egglog notes. The returned id names the atom in the extra
        /// constraints of <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>. A constant compared for equality with an
        /// id column of the table is recorded as an <see cref="IdConstant"/>.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="entries">One entry per column.</param>
        /// <param name="constraints">Extra constraints on the atom's rows.</param>
        /// <returns>The atom's id, the next in order.</returns>
        /// <exception cref="QueryException">The entry count is not the table's arity
        /// (<see cref="QueryErrorKind.BadArity"/>), or a constraint names a column beyond it
        /// (<see cref="QueryErrorKind.InvalidConstraint"/>).</exception>
        /// <exception cref="ArgumentException">An entry names a variable this rule did not create.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The database has no such table.</exception>
        public AtomId AddAtom(TableId table, ReadOnlySpan<QueryEntry> entries, ReadOnlySpan<Constraint> constraints = default)
        {
            owner.ThrowIfNotOpen(this);
            var arity = owner.Database.GetTableInfo(table).Spec.Arity;
            if (entries.Length != arity)
                throw QueryException.BadArity(table, arity, entries.Length);

            // egglog checks the given constraints, then the equalities made from constants; the latter name columns
            // below the arity by construction.
            foreach (var c in constraints)
            {
                if (c.Column >= arity)
                    throw QueryException.InvalidConstraint(table, c, c.Column, arity);
                if (c.Kind == ConstraintKind.Eq && c.OtherColumn >= arity)
                    throw QueryException.InvalidConstraint(table, c, c.OtherColumn, arity);
            }

            foreach (var e in entries)
                if (e.IsVariable && e.IsWildcard == false && IsKnown(e.Variable) == false)
                    throw new ArgumentException($"Unknown variable {e.Variable}: variables must be created by this rule.", nameof(entries));

            var atom = query.AddAtom(table, entries, constraints);
            RecordIdConstants(atom, table, entries, constraints);
            return atom;
        }

        /// <summary>
        /// Finishes the query and starts the right-hand side of the rule. egglog's <c>build</c>. This builder cannot be
        /// used afterwards.
        /// </summary>
        /// <returns>The builder of the rule's actions.</returns>
        public RuleBuilder Build()
        {
            owner.ThrowIfNotOpen(this);
            var rule = new RuleBuilder(owner, this);
            owner.HandOver(this, rule);
            return rule;
        }

        /// <summary>
        /// Creates a variable without checking that the rule is open; the rule builder creates the variables its
        /// instructions define through it.
        /// </summary>
        /// <param name="name">The name, or <c>null</c>.</param>
        /// <returns>The variable, <c>Variable(VariableCount)</c> before the call.</returns>
        internal Variable CreateVariable(string? name)
        {
            var variable = query.NewVariable(name);
            variableCount++;
            return variable;
        }

        /// <summary>
        /// Gets whether <paramref name="variable"/> was created by this rule.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if known.</returns>
        internal bool IsKnown(Variable variable) => (uint)variable.Index < (uint)variableCount;

        /// <summary>
        /// Records the constants <paramref name="atom"/> compares an id column with for equality: the given
        /// <see cref="ConstraintKind.EqConst"/> constraints first, then the constant entries, egglog's constraint order.
        /// </summary>
        void RecordIdConstants(AtomId atom, TableId table, ReadOnlySpan<QueryEntry> entries, ReadOnlySpan<Constraint> constraints)
        {
            var idColumns = owner.Database.GetIdColumns(table);
            if (idColumns.IsEmpty)
                return;

            foreach (var c in constraints)
                if (c.Kind == ConstraintKind.EqConst && idColumns.Contains(new ColumnId(c.Column)))
                    idConstants.Add(new IdConstant(atom, new ColumnId(c.Column), new Value(c.Constant)));

            for (var i = 0; i < entries.Length; i++)
                if (entries[i].IsVariable == false && idColumns.Contains(new ColumnId(i)))
                    idConstants.Add(new IdConstant(atom, new ColumnId(i), entries[i].Constant));
        }

    }

}
