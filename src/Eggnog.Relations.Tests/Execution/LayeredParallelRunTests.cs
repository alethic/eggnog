using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's rules on the parallel executor (colored-contexts spec §17 M10.2, X37, I34; review findings R1-2, R2-2):
    /// rule tasks and action batches stage into tables the child never wrote, creating its layers during the search, and
    /// look up tables it never wrote through the rule set's pinned views. The run equals the serial one, a lookup that hits
    /// creates no layer, and nothing drops a view while the search runs.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class LayeredParallelRunTests
    {

        const int Nodes = 300;

        /// <summary>
        /// Builds a frozen root with edges over <see cref="Nodes"/> ids and a constructor row for every id, and a child
        /// that runs serially or on four threads, always on the parallel executor when <paramref name="parallel"/>.
        /// </summary>
        static LayeredRunWorld Build(bool parallel)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var edges = store.AddTable(Relation(2), name: "edge");
            var cons = store.AddTable(Constructor(store), [], [store.UnionFindTable], "cons");
            var @out = store.AddTable(Relation(2), name: "out");
            var best = store.AddTable(MaxFunction(1), name: "best");
            var root = store.Root;
            var ids = Enumerable.Range(0, Nodes).Select(_ => root.NewId()).ToArray();
            var terms = Enumerable.Range(0, Nodes).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                for (var i = 0; i < Nodes; i++)
                {
                    state.StageInsert(edges, [ids[i], ids[(i * 7 + 3) % Nodes], state.Timestamp]);
                    state.StageInsert(edges, [ids[i], ids[(i * 13 + 5) % Nodes], state.Timestamp]);
                    state.StageInsert(cons, [ids[i], terms[i], state.Timestamp]);
                }

                return 0;
            });

            root.Freeze();
            var options = new ContextOptions { Parallelism = parallel ? new ParallelHeuristics(4) : ParallelHeuristics.Serial };
            var child = root.CreateChild(options);
            if (parallel)
                child.Database.ParallelRuleSetsOverride = true;

            return new LayeredRunWorld(store, edges, cons, @out, best, child, ids);
        }

        /// <summary>
        /// Adds the rules: for every edge <c>(x, y)</c>, look up the constructor of <c>x</c> (always a hit) and of
        /// <c>y</c> through <c>LookupOrInsertDefault</c>, look up <c>best(x)</c> with a default, and write
        /// <c>out(cons(x), cons(y))</c> and <c>best(x) ≥ y</c>, two tables the child never wrote.
        /// </summary>
        static void AddRules(LayeredRunWorld w, RuleSetBuilder rsb, ExternalFunctionId? probe)
        {
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(w.Edges, [x, y, QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(w.Store.ClockCounter);
            var cx = rb.LookupOrInsert(w.Cons, [x], [WriteVal.FromCounter(w.Store.IdCounter), WriteVal.Of(ts)], new ColumnId(1));
            var cy = rb.LookupOrInsert(w.Cons, [y], [WriteVal.FromCounter(w.Store.IdCounter), WriteVal.Of(ts)], new ColumnId(1));
            var b = rb.LookupWithDefault(w.Best, [x], new Value(0), new ColumnId(1));
            if (probe is { } p)
                rb.CallExternal(p, [b]);

            rb.Insert(w.Out, [cx, cy, ts]);
            rb.Insert(w.Best, [x, y, ts]);
            rb.Build("edges");

            var q2 = rsb.NewRule();
            var u = q2.NewVariable("u");
            var t = q2.NewVariable("t");
            q2.AddAtom(w.Cons, [u, t, QueryEntry.Wildcard()]);
            var rb2 = q2.Build();
            var ts2 = rb2.ReadCounter(w.Store.ClockCounter);
            var again = rb2.LookupOrInsert(w.Cons, [u], [WriteVal.FromCounter(w.Store.IdCounter), WriteVal.Of(ts2)], new ColumnId(1));
            rb2.Insert(w.Out, [again, t, ts2]);
            rb2.Build("cons");
        }

        [Fact]
        public void TheParallelRunEqualsTheSerialOneAndCreatesLayersSafely()
        {
            var serial = Build(parallel: false);
            var parallel = Build(parallel: true);
            Assert.Equal(serial.Ids, parallel.Ids);

            var generations = new ConcurrentBag<long>();
            var layering = parallel.Child.Tables!;
            var probe = parallel.Store.Root.Database.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                generations.Add(layering.ViewGeneration);
                result = args[0];
                return true;
            }));

            var before = layering.ViewGeneration;
            LayeredRuleUtil.Run(serial.Child, rsb => AddRules(serial, rsb, null));
            LayeredRuleUtil.Run(parallel.Child, rsb => AddRules(parallel, rsb, probe));

            // No minting (every constructor lookup hits), so the two runs agree exactly.
            Assert.Equal(LayeredRuleUtil.Visible(serial.Child, serial.Out), LayeredRuleUtil.Visible(parallel.Child, parallel.Out));
            Assert.Equal(LayeredRuleUtil.Visible(serial.Child, serial.Best), LayeredRuleUtil.Visible(parallel.Child, parallel.Best));
            Assert.True(LayeredRuleUtil.Visible(parallel.Child, parallel.Out).Count >= Nodes);

            // The search read pinned views: nothing dropped one while it ran (I29, I34).
            Assert.NotEmpty(generations);
            Assert.All(generations, g => Assert.Equal(before, g));

            // Every write landed in exactly one adopted layer; a lookup that hits created none.
            Assert.NotNull(layering.TryGetLayer(parallel.Out));
            Assert.True(layering.TryGetLayer(parallel.Out)!.IsAdopted);
            Assert.NotNull(layering.TryGetLayer(parallel.Best));
            Assert.Null(layering.TryGetLayer(parallel.Cons));
            Assert.Null(serial.Child.Tables!.TryGetLayer(serial.Cons));
            Assert.Equal(0, layering.UnsealedCount);
            parallel.Child.CheckInvariants();
            LookupPaths.Check(parallel.Child);
            LookupPaths.Check(serial.Child);
        }

        [Fact]
        public void AHittingLookupCreatesNoLayerOnAnyMode()
        {
            foreach (var parallel in new[] { false, true })
            {
                var w = Build(parallel);
                LayeredRuleUtil.Run(w.Child, rsb =>
                {
                    var q = rsb.NewRule();
                    var x = q.NewVariable("x");
                    var y = q.NewVariable("y");
                    q.AddAtom(w.Edges, [x, y, QueryEntry.Wildcard()]);
                    var rb = q.Build();
                    var ts = rb.ReadCounter(w.Store.ClockCounter);
                    rb.LookupOrInsert(w.Cons, [y], [WriteVal.FromCounter(w.Store.IdCounter), WriteVal.Of(ts)], new ColumnId(1));
                    rb.Build("hit");
                });

                Assert.Null(w.Child.Tables!.TryGetLayer(w.Cons));
                Assert.Equal(0, w.Child.Tables!.Statistics.LayersCreated);
                w.Child.CheckInvariants();
            }
        }

    }

}
