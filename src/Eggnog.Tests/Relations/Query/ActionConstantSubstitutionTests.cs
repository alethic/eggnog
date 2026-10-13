using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Id constants of actions and cached variants per context, beyond the insert, comparison and external-function
    /// operands of <see cref="ConstantSubstitutionTests"/>: remove keys, lookup-or-insert keys and entry defaults run
    /// in their canonical form at a store-backed root, and two contexts whose canonical forms differ each run their own
    /// variant of one cached plan.
    /// </summary>
    public class ActionConstantSubstitutionTests
    {

        /// <summary>
        /// A store with <c>r(id, id)</c> holding <c>r(x, c)</c> at the root, ids <c>x &lt; c &lt; k</c>, and <c>k</c>
        /// unioned into <c>c</c> at the root, rebuilt.
        /// </summary>
        static (Store Store, TableId R, Context Root, uint X, uint C, uint K) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var r = store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var x = root.NewId();
            var c = root.NewId();
            var k = root.NewId();
            Insert(root, r, x, c, 0);
            return (store, r, root, x, c, k);
        }

        /// <summary>
        /// Rows of <paramref name="table"/> as stored at the root, before any rebuild: what the run itself wrote.
        /// </summary>
        static List<string> Raw(Store store, TableId table) =>
            TableTestUtil.LiveRows(store.Schema[table]!.Root).Select(row => string.Join(",", row.Take(row.Length - 1))).OrderBy(s => s, StringComparer.Ordinal).ToList();

        [Fact]
        public void ARemoveKeyRunsInItsCanonicalForm()
        {
            var (store, r, root, x, c, k) = Setup();
            var s = store.AddTable(Relation(2), name: "s");
            Insert(root, s, x, c, 0);
            root.Union(c, k);
            root.Rebuild();

            // r(v, c) → remove s(v, k): k lost to c, so the remove names the class's row s(x, c).
            var rsb = root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(c), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Remove(s, [v, new Value(k)]);
            rb.Build("remove");
            using var rs = rsb.Build();
            Assert.Equal(1, root.Database.RunRuleSet(rs).NumMatches("remove"));
            Assert.Empty(Raw(store, s));
        }

        [Fact]
        public void ALookupOrInsertKeyRunsInItsCanonicalForm()
        {
            var (store, r, root, x, c, k) = Setup();
            var f = store.AddTable(Constructor(store), name: "f");
            var o = store.AddTable(Relation(2), name: "o");
            var t = root.NewId();
            Insert(root, f, c, t, 0);
            root.Union(c, k);
            root.Rebuild();
            var before = Raw(store, f);

            // r(v, c) → o(v, f(k)): f(k) is f(c) = t, a hit; written as k, it would miss and mint a new term.
            var rsb = root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(c), QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            var w = rb.LookupOrInsert(f, [new Value(k)], [WriteVal.FromCounter(store.IdCounter), WriteVal.Of(ts)], new ColumnId(1));
            rb.Insert(o, [v, w, ts]);
            rb.Build("lookup");
            using var rs = rsb.Build();
            Assert.Equal(1, root.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()).NumMatches("lookup"));
            Assert.Equal(before, Raw(store, f));
            Assert.Equal([$"{x},{t}"], Raw(store, o));
        }

        [Fact]
        public void ALookupOrInsertEntryDefaultRunsInItsCanonicalForm()
        {
            var (store, r, root, x, c, k) = Setup();
            var f = store.AddTable(Constructor(store), name: "f");
            root.Union(c, k);
            root.Rebuild();

            // r(v, c) → f(v) or else f(v) = k: the default lands in f's id column, so it is written as c.
            var rsb = root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(c), QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.LookupOrInsert(f, [v], [WriteVal.Of(new Value(k)), WriteVal.Of(ts)], new ColumnId(1));
            rb.Build("default");
            using var rs = rsb.Build();
            Assert.Equal(1, root.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()).NumMatches("default"));
            Assert.Equal([$"{x},{c}"], Raw(store, f));
        }

        [Fact]
        public void ContextsWithDifferentCanonicalFormsRunTheirOwnVariants()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var r = store.AddTable(Relation(2), name: "r");
            var s = store.AddTable(Relation(2), name: "s");
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var k = root.NewId();
            Insert(root, r, x, c, 0);
            Insert(root, r, y, d, 0);

            // r(v, k) → s(v, k), cached at the root, where k is canonical.
            var rsb = root.Database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            q.AddAtom(r, [v, new Value(k), QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.Insert(s, [v, new Value(k), ts]);
            rb.Build("r to s");
            using var written = rsb.Build();
            var cached = written.BuildCachedPlan(new RuleId(0));
            root.Freeze();

            // One child unions k into c, the other into d: each matches and writes its own class.
            using var one = root.CreateChild();
            using var two = root.CreateChild();
            Assert.True(one.Union(c, k));
            one.Rebuild();
            Assert.True(two.Union(d, k));
            two.Rebuild();
            Assert.Equal(1, LayeredRuleUtil.Run(one, b => b.AddRuleFromCachedPlan(cached)).NumMatches("r to s"));
            Assert.Equal(1, LayeredRuleUtil.Run(two, b => b.AddRuleFromCachedPlan(cached)).NumMatches("r to s"));
            Assert.Equal([$"{x},{c}"], LayeredRuleUtil.Visible(one, s));
            Assert.Equal([$"{y},{d}"], LayeredRuleUtil.Visible(two, s));
            Assert.Equal(2, cached.VariantCount);

            // Each context takes its variant again from the cache.
            Assert.Equal(1, LayeredRuleUtil.Run(two, b => b.AddRuleFromCachedPlan(cached)).NumMatches("r to s"));
            Assert.Equal([$"{y},{d}"], LayeredRuleUtil.Visible(two, s));
            Assert.Equal(2, cached.VariantCount);
        }

    }

}
