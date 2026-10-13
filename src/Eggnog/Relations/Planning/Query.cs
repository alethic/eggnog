using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The planner's input: a conjunctive query (variables, atoms, functional dependencies) plus the action it feeds
    /// and the planning knobs. egglog's <c>Query</c> (core-relations/src/query.rs:1040-1051).
    /// </summary>
    /// <remarks>
    /// Built by <see cref="QueryBuilder"/>. <see cref="QueryPlanner.PlanQuery(Query, IPlanStatistics)"/> never
    /// modifies it: the planner works on deep copies of the variable infos.
    /// </remarks>
    internal sealed class Query
    {

        /// <summary>
        /// Initializes a new instance. The collections are owned by the new instance.
        /// </summary>
        /// <param name="variables">The variables, by identifier.</param>
        /// <param name="atoms">The atoms, by identifier.</param>
        /// <param name="funDeps">The functional dependencies.</param>
        /// <param name="action">The action fed by the query's matches.</param>
        /// <param name="strategy">The stage-ordering strategy.</param>
        /// <param name="noDecomposition">Whether to skip tree decomposition.</param>
        public Query(DenseIdMap<Variable, VarInfo> variables, DenseIdMap<AtomId, QueryAtom> atoms, FunDeps funDeps, ActionId action, PlanStrategy strategy, bool noDecomposition)
        {
            ArgumentNullException.ThrowIfNull(variables);
            ArgumentNullException.ThrowIfNull(atoms);
            ArgumentNullException.ThrowIfNull(funDeps);
            Variables = variables;
            Atoms = atoms;
            FunDeps = funDeps;
            Action = action;
            Strategy = strategy;
            NoDecomposition = noDecomposition;
        }

        /// <summary>
        /// Gets the variables, by identifier.
        /// </summary>
        public DenseIdMap<Variable, VarInfo> Variables { get; }

        /// <summary>
        /// Gets the atoms, by identifier.
        /// </summary>
        public DenseIdMap<AtomId, QueryAtom> Atoms { get; }

        /// <summary>
        /// Gets the functional dependencies.
        /// </summary>
        public FunDeps FunDeps { get; }

        /// <summary>
        /// Gets the action fed by the query's matches.
        /// </summary>
        public ActionId Action { get; }

        /// <summary>
        /// Gets the stage-ordering strategy.
        /// </summary>
        public PlanStrategy Strategy { get; }

        /// <summary>
        /// Gets whether tree decomposition is skipped (egglog's <c>no_decomp</c>).
        /// </summary>
        public bool NoDecomposition { get; }

        /// <summary>
        /// Gets the variables the action reads that the query binds: used in the right-hand side and not defined there,
        /// in ascending order. egglog's <c>ActionInfo::used_vars</c> (core-relations/src/query.rs:555-561).
        /// </summary>
        /// <returns>The variables.</returns>
        public Variable[] GetUsedVariables()
        {
            var used = new List<Variable>();
            foreach (var (variable, info) in Variables)
                if (info.UsedInRhs && info.DefinedInRhs == false)
                    used.Add(variable);

            return used.ToArray();
        }

    }

}
