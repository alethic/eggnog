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
        /// Gets the atoms without variables, which no stage scans and the executor checks once for a visible row that
        /// satisfies their slow constraints. Callers must not modify the array.
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
        /// <see cref="InstantiateLayered{TEvaluator}"/> through <see cref="SingleLayerEvaluator{TEvaluator}"/>, with
        /// scratch of its own, so every header subset lies in layer 0 (C# cannot overload on constraints alone, hence the
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
                return InstantiateLayered(ref layered, extraConstraints, action, new HeaderScratch());
            }
            finally
            {
                evaluator = layered.Inner;
            }
        }

        /// <summary>
        /// Evaluates the headers for one run in a context whose tables may lie in several layers: egglog's
        /// <c>get_rule_with_extra_constraints</c> with every header subset a <see cref="LayeredSubset"/>.
        /// A header is empty iff it is empty in every layer; the run is then skipped before an action id is taken.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every extra constraint's atom is checked before any header is evaluated. The headers are evaluated into
        /// <paramref name="scratch"/>, in order, and the instance's arrays are allocated only once every header has
        /// proved nonempty: an instantiation that returns <c>null</c> (a semi-naive variant whose focus atom has no new
        /// rows) allocates nothing, given scratch that has held as many headers before. egglog's header vector
        /// allocates on its first push, so for a single plan whose first header is empty egglog allocates nothing
        /// either; this departs from it only for a decomposed plan and for an empty header after the first
        /// (<see cref="HeaderScratch"/>).
        /// </para>
        /// <para>
        /// O(headers) evaluations, typically one binary search per layer of a sorted timestamp column.
        /// </para>
        /// </remarks>
        /// <typeparam name="TEvaluator">The evaluator type: a context's <see cref="ContextView"/>, or a wrapped
        /// single-table evaluator.</typeparam>
        /// <param name="evaluator">Evaluates constraints against the context's views of the tables.</param>
        /// <param name="extraConstraints">Additional constraints, typically semi-naive timestamp bounds.</param>
        /// <param name="action">The action id of this run.</param>
        /// <param name="scratch">The caller's buffers; empty again when the call returns.</param>
        /// <returns>The instance, or <c>null</c> when some header selects no rows.</returns>
        /// <exception cref="ArgumentException">An extra constraint names an atom the plan does not have.</exception>
        /// <exception cref="InvalidOperationException">A constraint has no fast evaluation.</exception>
        public PlanInstance? InstantiateLayered<TEvaluator>(ref TEvaluator evaluator, ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extraConstraints, ActionId action, HeaderScratch scratch)
            where TEvaluator : ILayeredHeaderEvaluator
        {
            ArgumentNullException.ThrowIfNull(scratch);
            foreach (var (atom, _) in extraConstraints)
                if (Atoms.TryGetValue(atom, out _) == false)
                    throw new ArgumentException($"Atom {atom} is not part of the plan.", nameof(extraConstraints));

            scratch.BeginHeaders(extraConstraints.Length + Headers.Length);
            try
            {
                foreach (var (atom, constraint) in extraConstraints)
                    if (TryEvaluate(ref evaluator, atom, Atoms[atom].Table, new ReadOnlySpan<Constraint>(in constraint), scratch) == false)
                        return null;

                foreach (var header in Headers)
                    if (TryEvaluate(ref evaluator, header.Atom, Atoms[header.Atom].Table, header.Constraints, scratch) == false)
                        return null;

                var (specs, subsets) = scratch.TakeHeaders();
                return new PlanInstance(this, action, specs, subsets);
            }
            finally
            {
                scratch.ReleaseHeaders();
            }
        }

        /// <summary>
        /// Evaluates one header into <paramref name="scratch"/>: <c>false</c>, with nothing added, when it selects no rows.
        /// </summary>
        static bool TryEvaluate<TEvaluator>(ref TEvaluator evaluator, AtomId atom, TableId table, ReadOnlySpan<Constraint> constraints, HeaderScratch scratch)
            where TEvaluator : ILayeredHeaderEvaluator
        {
            var fast = scratch.Fast;
            var slow = scratch.Slow;
            fast.Clear();
            slow.Clear();
            var subset = evaluator.Evaluate(table, constraints, fast, slow, scratch);
            if (slow.Count > 0)
            {
                subset.Dispose(null);
                throw new InvalidOperationException($"Cached plans only support constraints with a fast pushdown. Got [{string.Join(", ", slow)}] for table {table}.");
            }

            if (subset.IsEmpty)
            {
                subset.Dispose(null);
                return false;
            }

            scratch.AddHeader(atom, fast, subset);
            return true;
        }

    }

}
