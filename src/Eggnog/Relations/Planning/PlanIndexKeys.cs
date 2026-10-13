using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Lists the cached indexes a plan may read, so a freezing context can build them before it is shared
    /// (<see cref="FreezeWarmup.From"/>): its concurrent descendants then rarely build one lazily under the frozen
    /// catalogs' serial refresh, where each lazy build runs on its caller's thread without parallel work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For cacheable columns only (<see cref="IQuerySchema.IsCacheable"/>), these are the indexes the executor and header
    /// evaluation take from a table's catalog: each <see cref="IntersectStage"/> scan's column (a dense piece over most of a
    /// layer reads the cached column index), each probe's <see cref="Probe.Scan"/> index (a column index for one column, a
    /// tuple index otherwise), and each <see cref="ConstraintKind.EqConst"/> column of a header (header evaluation promotes
    /// it through the column index).
    /// </para>
    /// <para>
    /// Cold path: once per cached plan per freeze.
    /// </para>
    /// </remarks>
    internal static class PlanIndexKeys
    {

        /// <summary>
        /// Appends the cached indexes <paramref name="template"/> may read to <paramref name="into"/>, skipping keys already
        /// there.
        /// </summary>
        /// <param name="template">The plan.</param>
        /// <param name="schema">The schema that says which columns are cacheable.</param>
        /// <param name="into">Receives the keys.</param>
        public static void Collect(PlanTemplate template, IQuerySchema schema, List<CachedIndexKey> into)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(into);
            var atoms = template.Atoms;
            foreach (var header in template.Headers)
            {
                var table = atoms[header.Atom].Table;
                foreach (var constraint in header.Constraints)
                    if (constraint.Kind == ConstraintKind.EqConst)
                        Add(schema, into, table, [new ColumnId(constraint.Column)]);
            }

            switch (template)
            {
                case SinglePlan single:
                    CollectStages(single.Stages, atoms, schema, into);
                    break;
                case DecomposedPlan decomposed:
                    foreach (var block in decomposed.Blocks)
                        CollectStages(block.Stages, atoms, schema, into);

                    CollectStages(decomposed.ResultBlock, atoms, schema, into);
                    break;
            }
        }

        /// <summary>
        /// Appends the keys of the stages' scans and probes.
        /// </summary>
        static void CollectStages(JoinStage[] stages, DenseIdMap<AtomId, QueryAtom> atoms, IQuerySchema schema, List<CachedIndexKey> into)
        {
            foreach (var stage in stages)
            {
                switch (stage)
                {
                    case IntersectStage intersect:
                        foreach (var scan in intersect.Scans)
                            Add(schema, into, atoms[scan.Atom].Table, [scan.Column]);

                        break;
                    case FusedIntersectStage fused:
                        foreach (var probe in fused.ToIntersect)
                            Add(schema, into, atoms[probe.Scan.Atom].Table, probe.Scan.ToIndex.Columns);

                        break;
                    case FusedIntersectMatStage mat:
                        foreach (var probe in mat.ToIntersect)
                            Add(schema, into, atoms[probe.Scan.Atom].Table, probe.Scan.ToIndex.Columns);

                        break;
                }
            }
        }

        /// <summary>
        /// Appends the key of <paramref name="columns"/> of <paramref name="table"/> when every column is cacheable and the
        /// key is not listed yet.
        /// </summary>
        static void Add(IQuerySchema schema, List<CachedIndexKey> into, TableId table, ColumnId[] columns)
        {
            if (columns.Length == 0 || table == TableId.Dummy)
                return;

            foreach (var column in columns)
                if (schema.IsCacheable(table, column) == false)
                    return;

            foreach (var key in into)
                if (key.Table == table && key.Columns.AsSpan().SequenceEqual(columns))
                    return;

            into.Add(new CachedIndexKey(table, columns));
        }

    }

}
