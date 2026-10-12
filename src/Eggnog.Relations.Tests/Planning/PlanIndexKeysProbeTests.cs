using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="PlanIndexKeys.Collect"/> on Free Join plans (colored-contexts spec §9.10): every probe's index is listed,
    /// a column index for a one-column key and a tuple index otherwise. <see cref="PlanIndexKeysTests"/> covers Generic
    /// Join scans and header equalities only.
    /// </summary>
    public class PlanIndexKeysProbeTests
    {

        [Theory]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void EveryProbeIndexOfAFreeJoinPlanIsListed(string strategyName)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var g = store.AddTable(Relation(3), name: "g");
            var db = store.Root.Database;
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = Enum.Parse<PlanStrategy>(strategyName);
            q.NoDecomposition = true;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
            q.AddAtom(g, [x, y, z, QueryEntry.Wildcard()]);
            q.AddAtom(e, [y, z, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.AssertEq(x, x);
            var rule = rb.Build("probes");
            using var rs = rsb.Build();
            var template = rs[rule].Template;

            var keys = new List<CachedIndexKey>();
            PlanIndexKeys.Collect(template, db, keys);
            var probes = ((SinglePlan)template).Stages.OfType<FusedIntersectStage>().SelectMany(s => s.ToIntersect).ToArray();
            Assert.NotEmpty(probes);
            foreach (var probe in probes)
                Assert.Contains(keys, k => k.Table == template.Atoms[probe.Scan.Atom].Table && k.Columns.SequenceEqual(probe.Scan.ToIndex.Columns));
        }

    }

}
