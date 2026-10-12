using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers for running rules in a context at the storage level (colored-contexts spec §9, M10.2), as the bridge's
    /// <c>RunRules</c> will (§10.3): rebuild the context, begin an epoch, note the run (the semi-naive floor), build the
    /// rule set on the context's database and run it at the epoch, end the epoch, and rebuild when the run grew the
    /// union-find.
    /// </summary>
    static class LayeredRuleUtil
    {

        /// <summary>
        /// Runs the rules <paramref name="add"/> adds, once, in <paramref name="context"/>.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="add">Adds the rules to the context's rule set builder.</param>
        /// <param name="level">How much to report.</param>
        /// <returns>The run's report.</returns>
        public static RuleSetReport Run(Context context, Action<RuleSetBuilder> add, ReportLevel level = ReportLevel.TimeOnly)
        {
            context.Rebuild();
            var epoch = context.BeginEpoch();
            context.NoteRuleRun(epoch);
            var before = context.DisplacedCount;
            RuleSetReport report;
            try
            {
                var rsb = context.Database.NewRuleSet();
                add(rsb);
                using var rs = rsb.Build();
                report = context.Database.RunRuleSet(rs, level, epoch);
            }
            finally
            {
                context.EndEpoch();
            }

            if (context.DisplacedCount != before || context.IsClean == false)
                context.Rebuild();

            return report;
        }

        /// <summary>
        /// Runs <paramref name="add"/>'s rules in <paramref name="context"/> until nothing changes, at most
        /// <paramref name="limit"/> times.
        /// </summary>
        /// <returns>The number of runs.</returns>
        public static int Saturate(Context context, Action<RuleSetBuilder> add, int limit = 50)
        {
            for (var i = 1; i <= limit; i++)
            {
                var stamp = context.Version;
                var report = Run(context, add);
                if (report.Changed == false && context.Version == stamp)
                    return i;
            }

            return limit;
        }

        /// <summary>
        /// Lists the rows <paramref name="context"/> sees of <paramref name="table"/>, without their timestamp column,
        /// as sorted strings.
        /// </summary>
        public static List<string> Visible(Context context, TableId table)
        {
            var schema = context.Store.Schema[table]!;
            var rows = new List<string>();
            if (context.IsRoot)
            {
                foreach (var row in TableTestUtil.LiveRows(schema.Root))
                    rows.Add(Format(row, schema.SortColumn));
            }
            else
            {
                foreach (var (_, _, cells) in LayeredTestUtil.ScanVisible(context.GetView(table)))
                    rows.Add(Format(cells, schema.SortColumn));
            }

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        /// <summary>
        /// Formats a row without its sort column.
        /// </summary>
        public static string Format(uint[] row, int sortColumn) => string.Join(",", row.Where((_, i) => i != sortColumn));

    }

}
