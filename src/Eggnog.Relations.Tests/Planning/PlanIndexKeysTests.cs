using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Prebuilding at freeze (colored-contexts spec §9.10, §8.1): <see cref="PlanIndexKeys.Collect"/> lists the cached
    /// indexes a plan reads (scan columns, probe indexes, header equalities; cacheable columns only), and
    /// <see cref="FreezeWarmup.From"/> builds them when a context freezes, so its descendants find them built.
    /// </summary>
    public class PlanIndexKeysTests
    {

        [Fact]
        public void APlansIndexesAreListedOnceEach()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var f = store.AddTable(MaxFunction(1), name: "f");
            var db = store.Root.Database;
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.Gj;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
            q.AddAtom(e, [y, z, QueryEntry.Wildcard()]);
            q.AddAtom(f, [z, new Value(3), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.AssertEq(x, x);
            var rule = rb.Build("path");
            using var rs = rsb.Build();
            var template = rs[rule].Template;

            var keys = new List<CachedIndexKey>();
            PlanIndexKeys.Collect(template, db, keys);
            PlanIndexKeys.Collect(template, db, keys);

            // Each key once; the header equality on f's value column; every scan column of a Generic Join plan.
            Assert.Equal(keys.Count, keys.Select(k => $"{k.Table}:{string.Join(",", k.Columns)}").Distinct().Count());
            Assert.Contains(keys, k => k.Table == f && k.Columns.SequenceEqual([new ColumnId(1)]));
            var scans = ((SinglePlan)template).Stages.OfType<IntersectStage>().SelectMany(s => s.Scans).ToArray();
            Assert.NotEmpty(scans);
            foreach (var scan in scans)
                Assert.Contains(keys, k => k.Table == template.Atoms[scan.Atom].Table && k.Columns.SequenceEqual([scan.Column]));
        }

        [Fact]
        public void UncacheableColumnsAreLeftOut()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.Gj;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(t, [x, y]);
            q.AddAtom(t, [y, x]);
            var rb = q.Build();
            rb.AssertEq(x, y);
            var rule = rb.Build("r");
            using var rs = rsb.Build();
            var keys = new List<CachedIndexKey>();
            PlanIndexKeys.Collect(rs[rule].Template, new NothingCacheable(db), keys);
            Assert.Empty(keys);
        }

        [Fact]
        public void AFreezeWarmsTheIndexesOfThePlans()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var root = store.Root;
            var ids = Enumerable.Range(0, 10).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                    state.StageInsert(e, [ids[i], ids[(i + 1) % ids.Length], state.Timestamp]);

                return 0;
            });

            root.Freeze();
            var child = root.CreateChild();
            Insert(child, e, ids[0], ids[5], 0);

            // A plan made in the child, cached, then the child's freeze warms the indexes it reads on the child's layer.
            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.Gj;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
            q.AddAtom(e, [y, z, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.AssertEq(x, x);
            var rule = rb.Build("path");
            using var rs = rsb.Build();
            var cached = rs.BuildCachedPlan(rule);
            var warmup = FreezeWarmup.From([cached.Plan], child.Database);
            Assert.NotEmpty(warmup.Keys.ToArray());
            child.Freeze(warmup);

            var info = child.Tables!.TryGetLayer(e)!.Info;
            foreach (var key in warmup.Keys)
            {
                if (key.Columns.Length == 1)
                    Assert.Contains(info.ColumnIndexes.Entries.ToArray(), entry => entry.Key.SequenceEqual(key.Columns) && entry.Index.IsCompleted);
                else
                    Assert.Contains(info.Indexes.Entries.ToArray(), entry => entry.Key.SequenceEqual(key.Columns) && entry.Index.IsCompleted);
            }
        }

    }

}
