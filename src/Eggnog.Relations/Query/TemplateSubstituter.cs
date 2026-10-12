using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Rewrites a plan template's Id constants into their canonical form in a context (colored-contexts spec §9.8, X19):
    /// every <see cref="ConstraintKind.EqConst"/> that a <see cref="ConstantSubstitution"/> query entry names (atom,
    /// column and written constant), wherever the planner placed it: the atoms, the headers, the existence checks, and
    /// the scans and probes of every stage and block.
    /// </summary>
    /// <remarks>
    /// The stage order and everything else the planner chose stay as planned (on the written constants' statistics);
    /// only the compared values change, so the plan runs the canonical constants (I30). Unchanged sub-objects (atoms,
    /// specs, stages, stage arrays) are shared with the written template; a changed stage array is new, so it gets its
    /// own <see cref="PreparedBlock"/>. Cold path: once per (rule, distinct K), cached in a <see cref="ConstantVariant"/>.
    /// </remarks>
    internal static class TemplateSubstituter
    {

        /// <summary>
        /// Creates the template <paramref name="template"/> runs as under <paramref name="substitution"/>.
        /// </summary>
        /// <param name="template">The written template.</param>
        /// <param name="substitution">The query constants that change.</param>
        /// <returns>The substituted template, or <paramref name="template"/> itself when no constraint changes.</returns>
        public static PlanTemplate Substitute(PlanTemplate template, ConstantSubstitution substitution)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(substitution);
            if (substitution.Query.IsEmpty)
                return template;

            var changed = false;
            var atoms = SubstituteAtoms(template.Atoms, substitution, ref changed);
            var headers = Map(template.Headers, h => SubstituteHeader(h, substitution), ref changed);
            var checks = Map(template.ExistenceChecks, c => SubstituteCheck(c, substitution), ref changed);
            switch (template)
            {
                case SinglePlan single:
                    var stages = SubstituteStages(single.Stages, substitution, ref changed);
                    return changed ? new SinglePlan(atoms, single.VariableCount, headers, checks, stages, single.Action) : template;
                case DecomposedPlan decomposed:
                    var blocks = Map(decomposed.Blocks, b =>
                    {
                        var blockChanged = false;
                        var blockStages = SubstituteStages(b.Stages, substitution, ref blockChanged);
                        return blockChanged ? new PlanBlock(blockStages, b.Spec) : b;
                    }, ref changed);
                    var result = SubstituteStages(decomposed.ResultBlock, substitution, ref changed);
                    return changed ? new DecomposedPlan(atoms, decomposed.VariableCount, headers, checks, blocks, result, decomposed.Action) : template;
                default:
                    throw new ArgumentException($"Unknown plan kind {template.Kind}.", nameof(template));
            }
        }

        /// <summary>
        /// Rewrites the constraints of <paramref name="atom"/> that the substitution names; returns the array itself when
        /// none changes.
        /// </summary>
        /// <param name="atom">The atom the constraints belong to.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="substitution">The substitution.</param>
        /// <returns>The constraints.</returns>
        public static Constraint[] SubstituteConstraints(AtomId atom, Constraint[] constraints, ConstantSubstitution substitution)
        {
            ArgumentNullException.ThrowIfNull(constraints);
            ArgumentNullException.ThrowIfNull(substitution);
            Constraint[]? copy = null;
            for (var i = 0; i < constraints.Length; i++)
            {
                var c = constraints[i];
                if (c.Kind != ConstraintKind.EqConst || substitution.TryGetQuery(atom, c.Column, c.Constant, out var canonical) == false)
                    continue;

                copy ??= (Constraint[])constraints.Clone();
                copy[i] = Constraint.EqConst(new ColumnId(c.Column), new Value(canonical));
            }

            return copy ?? constraints;
        }

        static DenseIdMap<AtomId, QueryAtom> SubstituteAtoms(DenseIdMap<AtomId, QueryAtom> atoms, ConstantSubstitution substitution, ref bool changed)
        {
            DenseIdMap<AtomId, QueryAtom>? copy = null;
            foreach (var (id, info) in atoms)
            {
                var fast = SubstituteConstraints(id, info.FastConstraints, substitution);
                var slow = SubstituteConstraints(id, info.SlowConstraints, substitution);
                if (ReferenceEquals(fast, info.FastConstraints) && ReferenceEquals(slow, info.SlowConstraints))
                    continue;

                copy ??= atoms.Clone();
                copy.Insert(id, new QueryAtom(info.Table, info.VarColumns, fast, slow));
            }

            if (copy is null)
                return atoms;

            changed = true;
            return copy;
        }

        static JoinHeaderSpec SubstituteHeader(JoinHeaderSpec header, ConstantSubstitution substitution)
        {
            var constraints = SubstituteConstraints(header.Atom, header.Constraints, substitution);
            return ReferenceEquals(constraints, header.Constraints) ? header : new JoinHeaderSpec(header.Atom, constraints);
        }

        static ExistenceCheck SubstituteCheck(ExistenceCheck check, ConstantSubstitution substitution)
        {
            var constraints = SubstituteConstraints(check.Atom, check.SlowConstraints, substitution);
            return ReferenceEquals(constraints, check.SlowConstraints) ? check : new ExistenceCheck(check.Atom, constraints);
        }

        static JoinStage[] SubstituteStages(JoinStage[] stages, ConstantSubstitution substitution, ref bool changed) =>
            Map(stages, s => SubstituteStage(s, substitution), ref changed);

        static JoinStage SubstituteStage(JoinStage stage, ConstantSubstitution substitution)
        {
            switch (stage)
            {
                case IntersectStage intersect:
                    var unused = false;
                    var scans = Map(intersect.Scans, s =>
                    {
                        var constraints = SubstituteConstraints(s.Atom, s.Constraints, substitution);
                        return ReferenceEquals(constraints, s.Constraints) ? s : new SingleScanSpec(s.Atom, s.Column, constraints);
                    }, ref unused);
                    return ReferenceEquals(scans, intersect.Scans) ? stage : new IntersectStage(intersect.Variable, scans);
                case FusedIntersectStage fused:
                    var cover = SubstituteScan(fused.Cover, substitution);
                    var probes = SubstituteProbes(fused.ToIntersect, substitution);
                    return ReferenceEquals(cover, fused.Cover) && ReferenceEquals(probes, fused.ToIntersect) ? stage : new FusedIntersectStage(cover, fused.Bind, probes);
                case FusedIntersectMatStage mat:
                    var matProbes = SubstituteProbes(mat.ToIntersect, substitution);
                    return ReferenceEquals(matProbes, mat.ToIntersect) ? stage : new FusedIntersectMatStage(mat.Cover, mat.Mode, mat.ModeVariables, mat.Bind, matProbes);
                default:
                    throw new ArgumentException($"Unknown stage kind {stage.Kind}.", nameof(stage));
            }
        }

        static ScanSpec SubstituteScan(ScanSpec scan, ConstantSubstitution substitution)
        {
            var constraints = SubstituteConstraints(scan.Atom, scan.Constraints, substitution);
            return ReferenceEquals(constraints, scan.Constraints) ? scan : new ScanSpec(scan.ToIndex, constraints);
        }

        static Probe[] SubstituteProbes(Probe[] probes, ConstantSubstitution substitution)
        {
            var unused = false;
            return Map(probes, p =>
            {
                var scan = SubstituteScan(p.Scan, substitution);
                return ReferenceEquals(scan, p.Scan) ? p : new Probe(scan, p.Key);
            }, ref unused);
        }

        /// <summary>
        /// Maps every element; returns the array itself when every element maps to itself, else a new array, and sets
        /// <paramref name="changed"/>.
        /// </summary>
        static T[] Map<T>(T[] items, Func<T, T> map, ref bool changed)
            where T : class
        {
            T[]? copy = null;
            for (var i = 0; i < items.Length; i++)
            {
                var mapped = map(items[i]);
                if (ReferenceEquals(mapped, items[i]))
                    continue;

                copy ??= (T[])items.Clone();
                copy[i] = mapped;
            }

            if (copy is null)
                return items;

            changed = true;
            return copy;
        }

    }

}
