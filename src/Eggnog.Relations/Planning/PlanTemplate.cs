using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// An immutable query plan, independent of table contents: egglog's <c>Plan</c>
    /// (core-relations/src/free_join/plan.rs:211-290) with the header subsets taken out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Either a <see cref="SinglePlan"/> (<see cref="PlanKind.Single"/>) or a <see cref="DecomposedPlan"/>
    /// (<see cref="PlanKind.Decomposed"/>). Produced by <see cref="QueryPlanner"/> once per rule and shared by every run,
    /// thread and context: stages, specs and atoms never change, and nothing in a template refers to rows. The per-run
    /// part (header subsets, the action id of this run) lives in a <see cref="PlanInstance"/>, made by
    /// <see cref="InstantiateLayered{TEvaluator}"/> (or <see cref="Instantiate{TEvaluator}"/> over a single table), which
    /// is egglog's cached-plan instantiation (core-relations/src/query.rs:191-274).
    /// </para>
    /// <para>
    /// <see cref="Atoms"/> is the full atom map of the original query in both kinds; the planner's synthetic covering
    /// atoms never appear in a template. Callers must treat it as read-only.
    /// </para>
    /// </remarks>
    internal abstract class PlanTemplate
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">The kind of the concrete plan.</param>
        /// <param name="atoms">The query's atoms; owned by the new instance.</param>
        /// <param name="variableCount">The number of variable slots of the query.</param>
        /// <param name="headers">The header specs; owned by the new instance.</param>
        /// <param name="existenceChecks">The atoms without variables to check; owned by the new instance.</param>
        /// <param name="action">The action the plan feeds.</param>
        private protected PlanTemplate(PlanKind kind, DenseIdMap<AtomId, QueryAtom> atoms, int variableCount, JoinHeaderSpec[] headers, ExistenceCheck[] existenceChecks, ActionId action)
        {
            ArgumentNullException.ThrowIfNull(atoms);
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(existenceChecks);
            Kind = kind;
            Atoms = atoms;
            VariableCount = variableCount;
            Headers = headers;
            ExistenceChecks = existenceChecks;
            Action = action;
        }

        /// <summary>
        /// Gets the kind of plan.
        /// </summary>
        public PlanKind Kind { get; }

        /// <summary>
        /// Gets the query's atoms, by identifier. Read-only.
        /// </summary>
        public DenseIdMap<AtomId, QueryAtom> Atoms { get; }

        /// <summary>
        /// Gets the number of variable slots of the query (one more than the highest variable id), for sizing bindings.
        /// </summary>
        public int VariableCount { get; }

        /// <summary>
        /// Gets the header specs: the fast constraints of each atom that has any, in atom order. Callers must not
        /// modify the array.
        /// </summary>
        public JoinHeaderSpec[] Headers { get; }

        /// <summary>
        /// Gets the atoms without variables, which no stage scans and the executor checks once (map §8.1 row 2).
        /// Callers must not modify the array.
        /// </summary>
        public ExistenceCheck[] ExistenceChecks { get; }

        /// <summary>
        /// Gets the action of the query the plan was built from.
        /// </summary>
        public ActionId Action { get; }

        /// <summary>
        /// Evaluates the headers for one run: egglog's <c>get_rule_with_extra_constraints</c>
        /// (core-relations/src/query.rs:191-243). Each extra constraint becomes a header of its own, followed by each of
        /// the template's headers re-evaluated against the current tables. Forwards to
        /// <see cref="InstantiateLayered{TEvaluator}"/> through <see cref="SingleLayerEvaluator{TEvaluator}"/>, so every
        /// header subset lies in layer 0 (colored-contexts spec §9.4; C# cannot overload on constraints alone, hence the
        /// second name).
        /// </summary>
        /// <typeparam name="TEvaluator">The evaluator type; a struct type argument specializes the calls.</typeparam>
        /// <param name="evaluator">Evaluates constraints against the current tables.</param>
        /// <param name="extraConstraints">Additional constraints, typically semi-naive timestamp bounds.</param>
        /// <param name="action">The action id of this run.</param>
        /// <returns>The instance, or <c>null</c> when some header selects no rows, so the run can be skipped before an
        /// action id is committed (egglog returns <c>None</c>).</returns>
        /// <exception cref="ArgumentException">An extra constraint names an atom the plan does not have.</exception>
        /// <exception cref="InvalidOperationException">A constraint has no fast evaluation; cached plans only accept
        /// fast constraints, as in egglog (core-relations/src/query.rs:140-145).</exception>
        public PlanInstance? Instantiate<TEvaluator>(ref TEvaluator evaluator, ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extraConstraints, ActionId action)
            where TEvaluator : IHeaderEvaluator
        {
            var layered = new SingleLayerEvaluator<TEvaluator>(evaluator);
            try
            {
                return InstantiateLayered(ref layered, extraConstraints, action);
            }
            finally
            {
                evaluator = layered.Inner;
            }
        }

        /// <summary>
        /// Evaluates the headers for one run in a context whose tables may lie in several layers (colored-contexts spec
        /// §9.4): egglog's <c>get_rule_with_extra_constraints</c> with every header subset a <see cref="LayeredSubset"/>.
        /// A header is empty iff it is empty in every layer; the run is then skipped before an action id is taken.
        /// </summary>
        /// <typeparam name="TEvaluator">The evaluator type: a context's <see cref="ContextView"/>, or a wrapped
        /// single-table evaluator.</typeparam>
        /// <param name="evaluator">Evaluates constraints against the context's views of the tables.</param>
        /// <param name="extraConstraints">Additional constraints, typically semi-naive timestamp bounds.</param>
        /// <param name="action">The action id of this run.</param>
        /// <returns>The instance, or <c>null</c> when some header selects no rows.</returns>
        /// <exception cref="ArgumentException">An extra constraint names an atom the plan does not have.</exception>
        /// <exception cref="InvalidOperationException">A constraint has no fast evaluation.</exception>
        public PlanInstance? InstantiateLayered<TEvaluator>(ref TEvaluator evaluator, ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extraConstraints, ActionId action)
            where TEvaluator : ILayeredHeaderEvaluator
        {
            var count = extraConstraints.Length + Headers.Length;
            var specs = new JoinHeaderSpec[count];
            var subsets = new LayeredSubset[count];
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var n = 0;
            for (var i = 0; i < extraConstraints.Length; i++)
            {
                var (atom, constraint) = extraConstraints[i];
                if (Atoms.TryGetValue(atom, out var info) == false)
                {
                    Release(subsets, n);
                    throw new ArgumentException($"Atom {atom} is not part of the plan.", nameof(extraConstraints));
                }

                if (TryEvaluate(ref evaluator, atom, info.Table, [constraint], fast, slow, specs, subsets, n) == false)
                    return null;

                n++;
            }

            foreach (var header in Headers)
            {
                if (TryEvaluate(ref evaluator, header.Atom, Atoms[header.Atom].Table, header.Constraints, fast, slow, specs, subsets, n) == false)
                    return null;

                n++;
            }

            return new PlanInstance(this, action, specs, subsets);
        }

        static bool TryEvaluate<TEvaluator>(ref TEvaluator evaluator, AtomId atom, TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow, JoinHeaderSpec[] specs, LayeredSubset[] subsets, int n)
            where TEvaluator : ILayeredHeaderEvaluator
        {
            fast.Clear();
            slow.Clear();
            var subset = evaluator.Evaluate(table, constraints, fast, slow);
            if (slow.Count > 0)
            {
                subset.Dispose(null);
                Release(subsets, n);
                throw new InvalidOperationException($"Cached plans only support constraints with a fast pushdown. Got [{string.Join(", ", slow)}] for table {table}.");
            }

            if (subset.IsEmpty)
            {
                subset.Dispose(null);
                Release(subsets, n);
                return false;
            }

            specs[n] = new JoinHeaderSpec(atom, fast.ToArray());
            subsets[n] = subset;
            return true;
        }

        static void Release(LayeredSubset[] subsets, int n)
        {
            for (var i = 0; i < n; i++)
                subsets[i].Dispose(null);
        }

    }

}
