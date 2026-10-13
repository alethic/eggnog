using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The index warm-up of a freeze builds indexes current with the frozen tables. Planning before the freeze
    /// refreshes an index; the table's freeze then compacts the stale rows a rebuild left, which renumbers the rows
    /// without a merge. Unless the freeze re-arms the indexes of a table it compacted, the warm-up reuses the index
    /// cell refreshed against the uncompacted table, and the frozen catalog, which is never reset again, publishes an
    /// index whose row ids do not name the table's rows.
    /// </summary>
    public class FreezeIndexWarmupTests
    {

        /// <summary>
        /// <c>mark(#c) → path(#c, #c)</c>, planned at the freeze; the root's union of <c>c</c> into <c>a</c> left the
        /// stale row of <c>mark(c)</c>, which the freeze compacts. The warmed index is current, and a child of the
        /// frozen root runs the rule (its constant canonically <c>a</c>) through it and derives <c>path(a, a)</c>.
        /// </summary>
        [Fact]
        public void TheWarmUpBuildsIndexesAgainstTheCompactedTables()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var c = g.Root.FreshId();
            var rb = g.EGraph.NewRule("mark-c", true);
            rb.QueryTable(g.Mark, [RuleEntry.Const(c, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [RuleEntry.Const(c, ColumnTy.Id), RuleEntry.Const(c, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Assert(g.Root, g.Mark, c);
            g.Root.FlushUpdates();
            g.Root.StageUnion(a, c);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            var info = g.Root.Context.Database.GetTableInfo(g.EGraph.GetTable(g.Mark));
            foreach (var entry in info.ColumnIndexes.Entries)
                if (entry.Index.TryGet(out var index))
                    Assert.False(index.NeedsRefresh(info.Table));

            using var child = g.Root.CreateChild();
            child.RunRules(rule);
            Assert.Equal([$"{a.Rep} {a.Rep}"], ContextGraph.Keys(child, g.Path));
            child.CheckInvariants();
        }

    }

}
