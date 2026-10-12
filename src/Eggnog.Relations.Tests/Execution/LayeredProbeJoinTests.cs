using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Match multiplicity of probed sides in a child (colored-contexts spec §9.5, I27, decision 6): a probed atom whose key
    /// lies in several layers is refined to the union of its hits, so it yields one frame, not one per layer. Only a
    /// driver's key may yield one frame per layer. The property tests compare distinct bindings only, so they cannot see a
    /// probe split by layer; these runs count matches.
    /// </summary>
    public class LayeredProbeJoinTests
    {

        /// <summary>
        /// A store with <c>a(x)</c> holding one id and <c>b(k, x)</c> holding, in the root and in the child, many rows,
        /// two of which (one per layer) name that id; the child is returned with the id.
        /// </summary>
        static (Store Store, TableId A, TableId B, Context Root, Context Child, uint V) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var a = store.AddTable(Relation(1), name: "a");
            var b = store.AddTable(Relation(2), name: "b");
            var root = store.Root;
            var v = root.NewId();
            var others = Enumerable.Range(0, 40).Select(_ => root.NewId()).ToArray();
            var keys = Enumerable.Range(0, 80).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                state.StageInsert(a, [v, state.Timestamp]);
                state.StageInsert(b, [keys[0], v, state.Timestamp]);
                for (var i = 0; i < 20; i++)
                    state.StageInsert(b, [keys[2 + i], others[i], state.Timestamp]);

                return 0;
            });

            root.Freeze();
            var child = root.CreateChild();
            child.Write(state =>
            {
                state.StageInsert(b, [keys[1], v, state.Timestamp]);
                for (var i = 20; i < 40; i++)
                    state.StageInsert(b, [keys[2 + i], others[i], state.Timestamp]);

                return 0;
            });

            return (store, a, b, root, child, v);
        }

        /// <summary>
        /// <c>a(x), b(_, x)</c> under the strategy <paramref name="strategyName"/>: the driver (or cover) is <c>a</c>, with one row in one
        /// layer, and <c>b</c> is probed with <c>x</c>, which it holds in both layers. One binding, one match.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        public void AProbedKeyHeldInSeveralLayersYieldsOneMatch(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var (store, a, b, _, child, v) = Setup();
            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var report = LayeredRuleUtil.Run(child, rsb =>
            {
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                q.AddAtom(a, [x, QueryEntry.Wildcard()]);
                q.AddAtom(b, [QueryEntry.Wildcard(), x, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.CallExternal(recorder, [x]);
                rb.Build("a and b");
            });

            Assert.Equal([v], Assert.Single(log));
            Assert.Equal(1, report.NumMatches("a and b"));
        }

        /// <summary>
        /// The same through the two-scan intersection kernel with the probed side on the left: <c>b(_, x), a(x)</c>.
        /// </summary>
        [Fact]
        public void AProbedKeyHeldInSeveralLayersYieldsOneMatchWhateverTheAtomOrder()
        {
            var (store, a, b, _, child, v) = Setup();
            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var report = LayeredRuleUtil.Run(child, rsb =>
            {
                var q = rsb.NewRule();
                q.Strategy = PlanStrategy.Gj;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                q.AddAtom(b, [QueryEntry.Wildcard(), x, QueryEntry.Wildcard()]);
                q.AddAtom(a, [x, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.CallExternal(recorder, [x]);
                rb.Build("b and a");
            });

            Assert.Equal([v], Assert.Single(log));
            Assert.Equal(1, report.NumMatches("b and a"));
        }

        /// <summary>
        /// I28, §9.6: one node driven in some frames and probed in others caches its refinements under different slots.
        /// <c>a(z), s(z, y), r(w, y)</c>: the stage on <c>y</c> intersects <c>s</c>'s node, refined by <c>z</c>, with
        /// <c>r</c>'s root node, shared by every frame. Under <c>z1</c>, <c>s</c> holds one <c>y</c> and drives, so
        /// <c>r</c> is probed with <c>v</c> and refined to its rows of both layers; under <c>z2</c>, <c>s</c> holds many
        /// and <c>r</c> drives, refining to the rows of one layer per frame. Both refinements are large enough for the
        /// node's child cache, so a probe cached under a driver's layer slot (or the reverse) loses or repeats rows.
        /// </summary>
        [Fact]
        public void ANodeDrivenInOneFrameAndProbedInAnotherCachesEachRefinementApart()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var a = store.AddTable(Relation(1), name: "a");
            var s = store.AddTable(Relation(2), name: "s");
            var r = store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var z1 = root.NewId();
            var z2 = root.NewId();
            var v = root.NewId();
            var other = root.NewId();
            var spread = Enumerable.Range(0, 9).Select(_ => root.NewId()).ToArray();
            var rootW = Enumerable.Range(0, 40).Select(_ => root.NewId()).ToArray();
            var childW = Enumerable.Range(0, 40).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                state.StageInsert(a, [z1, state.Timestamp]);
                state.StageInsert(a, [z2, state.Timestamp]);
                state.StageInsert(s, [z1, v, state.Timestamp]);
                state.StageInsert(s, [z2, v, state.Timestamp]);
                foreach (var y in spread)
                    state.StageInsert(s, [z2, y, state.Timestamp]);

                foreach (var w in rootW)
                {
                    state.StageInsert(r, [w, v, state.Timestamp]);
                    state.StageInsert(r, [w, other, state.Timestamp]);
                }

                return 0;
            });

            root.Freeze();
            using var child = root.CreateChild();
            child.Write(state =>
            {
                foreach (var w in childW)
                {
                    state.StageInsert(r, [w, v, state.Timestamp]);
                    state.StageInsert(r, [w, other, state.Timestamp]);
                }

                return 0;
            });

            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var report = LayeredRuleUtil.Run(child, rsb =>
            {
                var q = rsb.NewRule();
                q.Strategy = PlanStrategy.Gj;
                q.NoDecomposition = true;
                var z = q.NewVariable("z");
                var y = q.NewVariable("y");
                var w = q.NewVariable("w");
                q.AddAtom(a, [z, QueryEntry.Wildcard()]);
                q.AddAtom(s, [z, y, QueryEntry.Wildcard()]);
                q.AddAtom(r, [w, y, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.CallExternal(recorder, [z, y, w]);
                rb.Build("a, s and r");
            });

            var expected = new[] { z1, z2 }.SelectMany(z => rootW.Concat(childW).Select(w => $"{z},{v},{w}")).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(expected, ExecutionTestUtil.Distinct(log));

            // Every match is one combination of rows: no driver key here lies in two layers of a refined node twice.
            Assert.Equal(160, report.NumMatches("a, s and r"));
        }

    }

}
