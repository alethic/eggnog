using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Builds the engine's lift spec of a graph: which tables a lift writes into a case's parent, and how.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every table with <see cref="TableHandle.Lift"/> set is listed, in declaration order, except
    /// <see cref="EGraph.Contradiction"/> (an all-vacuous lift writes it on its own terms); mirror tables are never
    /// listed (they are not in <see cref="EGraph.Tables"/>). A relation lifts as the engine's <c>Relation</c> kind over
    /// its unit output: a row present in every live case and absent in the parent is written with the output every live
    /// case holds, which for unit they always agree on, so exactly the rows present in every live case lift, counted as
    /// rows (<see cref="LiftReport.Rows"/>). That is what a function with a unit output and no case join would write
    /// too; the kind only keeps relation rows apart from function values in the report. A constructor lifts as a
    /// constructor. A function lifts with its lattice's case join (<see cref="FunctionHandle.CaseJoin"/>), or, without
    /// one, requires every live case to agree.
    /// </para>
    /// <para>
    /// The spec is cached on the graph and rebuilt when a table is defined (a check of the table count): one volatile read
    /// and one compare per call otherwise. Rebuilt specs list the same delegates, so the engine's lift memo, which compares
    /// specs by content, survives a rebuild that adds no lifted table.
    /// </para>
    /// </remarks>
    internal static class LiftSpecBuilder
    {

        /// <summary>
        /// Gets the lift spec of <paramref name="graph"/>'s tables as they are now. Thread-safe; lock-free.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <returns>The spec.</returns>
        public static Relations.LiftSpec Build(EGraph graph)
        {
            var tables = graph.Tables;
            var cached = Volatile.Read(ref graph.LiftSpecCache);
            if (cached is not null && cached.Item1 == tables.Count)
                return cached.Item2;

            var lifted = new List<Relations.LiftTable>(tables.Count);
            foreach (var table in tables)
            {
                if (table.Lift == false || ReferenceEquals(table, graph.Contradiction))
                    continue;

                switch (table)
                {
                    case ConstructorHandle:
                        lifted.Add(new Relations.LiftTable(table.Function, Relations.LiftTableKind.Constructor));
                        break;
                    case FunctionHandle function:
                        lifted.Add(new Relations.LiftTable(table.Function, Relations.LiftTableKind.Function, function.CaseJoin));
                        break;
                    default:
                        lifted.Add(new Relations.LiftTable(table.Function, Relations.LiftTableKind.Relation));
                        break;
                }
            }

            var spec = new Relations.LiftSpec(lifted);
            Volatile.Write(ref graph.LiftSpecCache, Tuple.Create(tables.Count, spec));
            return spec;
        }

    }

}
