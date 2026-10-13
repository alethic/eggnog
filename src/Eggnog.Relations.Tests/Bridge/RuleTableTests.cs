using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="RuleTable"/> (colored-contexts spec §10.2, §17 M10.3): reservation and publication, id reuse last freed
    /// first with a new generation, lock-free reads, and definitions from many threads.
    /// </summary>
    public class RuleTableTests
    {

        static RuleInfo Info(EGraphRuleId id, int generation) => new(new BridgeQuery(new TableId(0), new CounterId(0), new CounterId(1), id, true), "r" + id.Rep, null, generation);

        [Fact]
        public void IdsAreReservedPublishedAndReusedLastFreedFirst()
        {
            var table = new RuleTable();
            var (a, ga) = table.Reserve();
            var (b, gb) = table.Reserve();
            Assert.Equal(new EGraphRuleId(0), a);
            Assert.Equal(new EGraphRuleId(1), b);
            Assert.Equal(1, ga);
            Assert.Equal(1, gb);
            Assert.Equal(0, table.Count);
            Assert.Equal(2, table.Capacity);
            Assert.False(table.TryGet(a, out _));

            table.Publish(a, Info(a, ga));
            table.Publish(b, Info(b, gb));
            Assert.Equal(2, table.Count);
            Assert.True(table.TryGet(a, out var found));
            Assert.Equal(a, found.Id);

            Assert.True(table.Remove(b, out var removed));
            Assert.Equal(b, removed.Id);
            Assert.True(table.Remove(a, out _));
            Assert.False(table.Remove(a, out _));
            Assert.Equal(0, table.Count);

            // Last freed first, each with the next generation of its slot.
            Assert.Equal((a, 2), table.Reserve());
            Assert.Equal((b, 2), table.Reserve());
            Assert.Equal((new EGraphRuleId(2), 1), table.Reserve());
        }

        [Fact]
        public void PublishChecksTheReservation()
        {
            var table = new RuleTable();
            Assert.Throws<InvalidOperationException>(() => table.Publish(new EGraphRuleId(0), Info(new EGraphRuleId(0), 1)));
            var (id, generation) = table.Reserve();
            Assert.Throws<InvalidOperationException>(() => table.Publish(id, Info(id, generation + 1)));
            table.Publish(id, Info(id, generation));
            Assert.Throws<InvalidOperationException>(() => table.Publish(id, Info(id, generation)));

            // A reserved id that was never built is left alone by a removal, as egglog's free_rule leaves it.
            var (unbuilt, _) = table.Reserve();
            Assert.False(table.Remove(unbuilt, out _));
            Assert.Equal(new EGraphRuleId(2), table.Reserve().Id);
        }

        [Fact]
        public void AddReservesAndPublishesInOneStep()
        {
            var table = new RuleTable();
            var id = table.Add((rule, generation) => Info(rule, generation));
            Assert.True(table.TryGet(id, out var info));
            Assert.Equal(1, info.Generation);
            var live = new List<RuleInfo>();
            table.CopyLive(live);
            Assert.Single(live);
            Assert.False(table.TryGet(new EGraphRuleId(5), out _));
            Assert.False(table.TryGet(new EGraphRuleId(-1), out _));
        }

        /// <summary>
        /// Definitions from many threads (rule-layer D6) get distinct ids, and readers on other threads never see a torn
        /// table.
        /// </summary>
        [Fact]
        public void ConcurrentDefinitionsAndReadsAgree()
        {
            var table = new RuleTable();
            var ids = new ConcurrentBag<EGraphRuleId>();
            var stop = false;
            var seen = 0;
            Exception? failure = null;

            // A dedicated thread, so it reads while the definitions run however busy the pool is; it scans at least once.
            var reader = new System.Threading.Thread(() =>
            {
                try
                {
                    do
                    {
                        var capacity = table.Capacity;
                        for (var i = 0; i < capacity; i++)
                            if (table.TryGet(new EGraphRuleId(i), out var info) && info.Id.Rep != i)
                                throw new InvalidOperationException("a slot holds another rule");

                        seen++;
                    }
                    while (System.Threading.Volatile.Read(ref stop) == false);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            reader.Start();

            Parallel.For(0, 2000, i =>
            {
                var id = table.Add((rule, generation) => Info(rule, generation));
                ids.Add(id);
                if (i % 3 == 0)
                    table.Remove(id, out _);
            });

            System.Threading.Volatile.Write(ref stop, true);
            reader.Join();
            Assert.Null(failure);
            Assert.True(seen > 0);
            Assert.Equal(2000, ids.Count);
            var live = new List<RuleInfo>();
            table.CopyLive(live);
            Assert.Equal(table.Count, live.Count);
            Assert.Equal(live.Count, live.Select(r => r.Id).Distinct().Count());
        }

        /// <summary>
        /// The e-graph's rules live in a rule table: a freed id is reused with a new generation, and the rule info built in
        /// it carries that generation.
        /// </summary>
        [Fact]
        public void TheEGraphsRulesCarryTheirGeneration()
        {
            var g = new ContextGraph();
            var first = g.EdgeToPath(name: "first");
            Assert.Equal(1, g.EGraph.GetRuleInfo(first).Generation);
            Assert.Equal(1, g.EGraph.RuleCount);
            g.EGraph.FreeRule(first);
            Assert.Equal(0, g.EGraph.RuleCount);
            var second = g.EdgeToPath(name: "second");
            Assert.Equal(first, second);
            Assert.Equal(2, g.EGraph.GetRuleInfo(second).Generation);
            Assert.Equal("second", g.EGraph.GetRuleInfo(second).Description);
        }

    }

}
