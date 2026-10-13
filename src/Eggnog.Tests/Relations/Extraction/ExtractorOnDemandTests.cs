using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for on-demand extraction (Knuth's algorithm over the classes reachable from the root) and for the
    /// staleness checks every extractor makes.
    /// </summary>
    public class ExtractorOnDemandTests
    {

        [Fact]
        public void OnlyTheClassesReachableFromTheRootAreRead()
        {
            // Two disjoint chains of 50 classes each: asking about the first reads only the first.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 1, fx.I64);
            var next = fx.Constructor("next", e, 1, e);
            Value Chain(long seed)
            {
                var current = fx.Make(leaf, fx.Int(seed));
                for (var i = 0; i < 49; i++)
                    current = fx.Make(next, current);

                return current;
            }

            var first = Chain(1);
            var second = Chain(2);

            var extractor = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(extractor.IsOnDemand);
            Assert.Empty(extractor.RootSorts);
            Assert.Equal(0, extractor.ClassCount);
            Assert.True(extractor.TryGetCost(e, first, out var cost));
            Assert.Equal(51, cost);
            Assert.Equal(50, extractor.ClassCount);
            Assert.Equal(50, extractor.NodeCount);

            // A later root extends the graph; classes already costed are untouched.
            Assert.True(extractor.TryGetCost(e, second, out cost));
            Assert.Equal(51, cost);
            Assert.Equal(100, extractor.ClassCount);
            Assert.True(extractor.TryGetCost(e, first, out _));
            Assert.Equal(100, extractor.NodeCount);

            var everything = Extractor.ComputeCostsFromRootSorts(fx.Schema);
            Assert.Equal(100, everything.ClassCount);
            Assert.False(everything.IsOnDemand);
        }

        [Fact]
        public void OnDemandAgreesWithTheRootSortExtractor()
        {
            var (fx, term, _, _, tb) = StepGraphForOnDemand();
            var onDemand = Extractor.CreateOnDemand(fx.Schema);
            var all = Extractor.ComputeCostsFromRootSorts(fx.Schema);
            var dag = new TermDag();
            Assert.True(onDemand.TryExtractBest(term, tb, dag, out var lazy));
            Assert.True(all.TryExtractBest(term, tb, dag, out var eager));
            Assert.Equal(eager, lazy);
            Assert.Equal(
                all.ExtractVariants(term, tb, 5, dag).ToArray(),
                onDemand.ExtractVariants(term, tb, 5, dag).ToArray());
        }

        static (ExtractionFixture Fx, EqSort Term, FunctionId BigStep, FunctionId SmallStep, Value Tb) StepGraphForOnDemand()
        {
            var fx = new ExtractionFixture();
            var term = new EqSort("Term");
            var origin = fx.Constructor("Origin", term, 0);
            var bigStep = fx.Constructor("BigStep", term, 10, term);
            var smallStep = fx.Constructor("SmallStep", term, 1, term);
            var t = fx.Make(origin);
            var tb = fx.Make(bigStep, t);
            var tbs = fx.Make(smallStep, tb);
            var tsss = fx.Make(smallStep, fx.Make(smallStep, fx.Make(smallStep, t)));
            fx.Union(tbs, tsss);
            fx.Union(fx.Make(smallStep, tsss), tb);
            return (fx, term, bigStep, smallStep, fx.Canon(tb));
        }

        [Fact]
        public void OnDemandReachesThroughContainers()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var vec = new TestVecSort("VecE", e);
            var leaf = fx.Constructor("leaf", e, 2, fx.I64);
            var list = fx.Constructor("list", e, 1, vec);
            var unrelated = fx.Make(leaf, fx.Int(99));
            var a = fx.Make(leaf, fx.Int(1));
            var b = fx.Make(leaf, fx.Int(2));
            var inner = fx.Make(list, fx.EGraph.GetContainerValue(new VecContainer(a.Rep, b.Rep)));
            var outer = fx.Make(list, fx.EGraph.GetContainerValue(new VecContainer(inner.Rep, a.Rep)));

            var extractor = Extractor.CreateOnDemand(fx.Schema);
            var dag = new TermDag();
            Assert.True(extractor.TryExtractBest(e, outer, dag, out var result));
            Assert.Equal("(list (vec-of (list (vec-of (leaf 1) (leaf 2))) (leaf 1)))", dag.ToString(result.Term));
            Assert.Equal(1 + (1 + 3 + 3) + 3, result.Cost);
            Assert.Equal(4, extractor.ClassCount);
            Assert.True(extractor.TryGetCost(e, unrelated, out _));
            Assert.Equal(5, extractor.ClassCount);
        }

        [Fact]
        public void UnknownValuesHaveNoTerm()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var other = new EqSort("Other");
            fx.Constructor("leaf", e, 1);
            var stray = fx.EGraph.FreshId();

            var all = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e]);
            Assert.False(all.TryGetCost(e, stray, out _));
            Assert.False(all.TryGetCost(other, stray, out _));
            Assert.Empty(all.ExtractVariants(e, stray, 3, new TermDag()));
            Assert.False(all.TryExtractTerm(e, stray, out _));
            Assert.Throws<InvalidOperationException>(() => all.ExtractTerm(e, stray));

            var onDemand = Extractor.CreateOnDemand(fx.Schema);
            Assert.False(onDemand.TryGetCost(e, stray, out _));
            Assert.False(onDemand.TryGetCost(other, stray, out _));
            Assert.Empty(onDemand.ExtractVariants(other, stray, 3, new TermDag()));
        }

        [Fact]
        public void ExtractorsGoStaleWhenTheEGraphChanges()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 1, fx.I64);
            var one = fx.Make(leaf, fx.Int(1));
            var all = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e]);
            var onDemand = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(all.TryGetCost(e, one, out _));

            fx.Make(leaf, fx.Int(2));
            Assert.Throws<InvalidOperationException>(() => all.TryGetCost(e, one, out _));
            Assert.Throws<InvalidOperationException>(() => onDemand.TryExtractBest(e, one, new TermDag(), out _));
            Assert.Throws<InvalidOperationException>(() => all.ExtractVariants(e, one, 1, new TermDag()));
            Assert.Throws<InvalidOperationException>(() => all.TryExtractTerm(e, one, out _));

            // Clearing a table does not advance the timestamp, but changes the table's version.
            var fresh = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e]);
            fx.EGraph.ClearTable(leaf);
            Assert.Throws<InvalidOperationException>(() => fresh.TryGetCost(e, one, out _));
        }

        [Fact]
        public void VariantsSkipNodesWithNoFiniteTerm()
        {
            // egglog unwraps every root node's cost (src/extract.rs:691) and panics on (Wrap y), whose child has no
            // term; this extractor skips the node.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var wrap = fx.Constructor("Wrap", e, 1, e);
            var leaf = fx.Constructor("Leaf", e, 5);
            var y = fx.EGraph.FreshId();
            var x = fx.Make(wrap, y);
            fx.Union(x, fx.Make(leaf));

            foreach (var extractor in new[] { Extractor.ComputeCostsFromRootSorts(fx.Schema), Extractor.CreateOnDemand(fx.Schema) })
            {
                var dag = new TermDag();
                var variants = extractor.ExtractVariants(e, fx.Canon(x), 5, dag);
                Assert.Equal(["(Leaf)"], variants.Select(v => dag.ToString(v.Term)).ToArray());
                Assert.Empty(extractor.ExtractVariants(e, x, 0, dag));
                Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractVariants(e, x, -1, dag));
            }
        }

        [Fact]
        public void BaseAndContainerVariantsAreTheSingleBestTerm()
        {
            // egglog: "extracting multiple variants for containers or primitives is not implemented, returning a single
            // variant" (src/extract.rs:724-731), whatever the count.
            var fx = new ExtractionFixture();
            var extractor = Extractor.CreateOnDemand(fx.Schema);
            var dag = new TermDag();
            var variants = extractor.ExtractVariants(fx.I64, fx.Int(4), 3, dag);
            Assert.Equal(["4"], variants.Select(v => dag.ToString(v.Term)).ToArray());
        }

        [Fact]
        public void QueriesCheckSortNamesWithoutBreakingTheExtractor()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 1);
            var l = fx.Make(leaf);
            var extractor = Extractor.CreateOnDemand(fx.Schema);

            Assert.Throws<ArgumentException>(() => extractor.TryGetCost(new EqSort("E"), l, out _));
            Assert.Throws<ArgumentException>(() => extractor.ExtractVariants(new BaseSort<Int64Codec, long>("E"), l, 1, new TermDag()));
            Assert.True(extractor.TryGetCost(new EqSort("Fresh"), l, out _) == false);
            Assert.Throws<ArgumentException>(() => extractor.TryExtractTerm(new EqSort("Fresh"), l, out _));
            Assert.True(extractor.TryGetCost(e, l, out var cost));
            Assert.Equal(1, cost);
            Assert.Throws<ArgumentException>(() => Extractor.ComputeCostsFromRootSorts(fx.Schema, [new EqSort("E")]));
        }

        [Fact]
        public void ExtractionDoesNotChangeTheEGraph()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 1, fx.I64);
            var pair = fx.Constructor("pair", e, 1, e, e);
            var p = fx.Make(pair, fx.Make(leaf, fx.Int(1)), fx.Make(leaf, fx.Int(2)));
            var before = fx.EGraph.NextTimestamp;
            var rows = BridgeTestUtil.Show(fx.EGraph, pair);

            var extractor = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(extractor.TryExtractTerm(e, p, out _));
            Assert.True(extractor.TryGetCost(e, p, out _));
            Assert.Equal(before, fx.EGraph.NextTimestamp);
            Assert.Equal(rows, BridgeTestUtil.Show(fx.EGraph, pair));
        }

        [Fact]
        public void ReplaysFollowTheChoicesBelowAClass()
        {
            // All costs 0; declared N < Cn < P < Qleaf < Dleaf. X = { N(C), P(Q) }, C = { Cn(D) }, D = { Dleaf },
            // Q = { Qleaf }. From scratch: Qleaf pops before Dleaf, so P(Q) is queued and pops before Dleaf; X is P(Q).
            // A warmed extractor that costed C first must replay C's finalization behind D's: replaying C alone, as if
            // final from the start, would release N(C) before P(Q) is queued.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var n = fx.Constructor("N", e, 0, e);
            var cn = fx.Constructor("Cn", e, 0, e);
            var p = fx.Constructor("P", e, 0, e);
            var qleaf = fx.Constructor("Qleaf", e, 0);
            var dleaf = fx.Constructor("Dleaf", e, 0);
            var c = fx.Make(cn, fx.Make(dleaf));
            var x = fx.Make(n, c);
            fx.Union(x, fx.Make(p, fx.Make(qleaf)));
            x = fx.Canon(x);
            c = fx.Canon(c);

            Assert.Equal("(P (Qleaf))", fx.Extract(e, x)?.Text);
            Assert.Equal("(P (Qleaf))", Extractor.CreateOnDemand(fx.Schema).ExtractTerm(e, x).ToString());

            var warmed = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(warmed.TryGetCost(e, c, out _));
            Assert.Equal(0, warmed.ReplayCount);
            Assert.Equal("(P (Qleaf))", warmed.ExtractTerm(e, x).ToString());
            Assert.Equal(2, warmed.ReplayCount);
        }

        [Fact]
        public void PositiveCostsReplayNothing()
        {
            // With positive head costs a class final since an earlier query always costs less than the nodes that
            // depend on it, so a from-scratch run finalizes it before they can matter: nothing is replayed.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var g = fx.Constructor("G", e, 1, e);
            var m = fx.Constructor("M", e, 1);
            var zf = fx.Constructor("Zf", e, 1);
            var y = fx.Make(m);
            var z = fx.Make(zf);
            fx.Union(fx.Make(g, y), fx.Make(g, z));
            var x = fx.Canon(fx.Make(g, y));

            var warmed = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(warmed.TryGetCost(e, z, out _));
            Assert.Equal("(G (M))", warmed.ExtractTerm(e, x).ToString());
            Assert.Equal(0, warmed.ReplayCount);
        }

        [Fact]
        public void DeepReplaysDoNotOverflowTheStack()
        {
            // A chain of 100,000 zero-cost classes, costed by one query; a second query reaches a class over its top,
            // which replays every finalization in the chain.
            const int Depth = 100_000;
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 0);
            var next = fx.Constructor("next", e, 0, e);
            var wrap = fx.Constructor("wrap", e, 0, e);
            var ids = Enumerable.Range(0, Depth + 2).Select(_ => fx.EGraph.FreshId()).ToArray();
            var rows = new (FunctionId, Value[])[Depth + 2];
            rows[0] = (leaf, [ids[0]]);
            for (var i = 1; i <= Depth; i++)
                rows[i] = (next, [ids[i - 1], ids[i]]);

            rows[Depth + 1] = (wrap, [ids[Depth], ids[Depth + 1]]);
            fx.EGraph.AddValues(rows);

            var warmed = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(warmed.TryGetCost(e, ids[Depth], out var cost));
            Assert.Equal(0, cost);
            Assert.True(warmed.TryGetChoice(e, ids[Depth + 1], out var function, out var row, out _));
            Assert.Equal(wrap, function);
            Assert.Equal(ids[Depth], row[0]);
            Assert.Equal(Depth + 1, warmed.ReplayCount);
        }

        [Fact]
        public void VariantsStartWithTheChoiceEvenWithZeroCosts()
        {
            // All costs 0; declared A < B < C. X = { A(Y), B }, Y = { C }. Knuth's algorithm pops B before C, so X
            // chooses B, although A(Y) comes first in content order; the first variant is still the choice.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var a = fx.Constructor("A", e, 0, e);
            var b = fx.Constructor("B", e, 0);
            var c = fx.Constructor("C", e, 0);
            var x = fx.Make(a, fx.Make(c));
            fx.Union(x, fx.Make(b));
            x = fx.Canon(x);

            foreach (var extractor in new[] { Extractor.ComputeCostsFromRootSorts(fx.Schema), Extractor.CreateOnDemand(fx.Schema) })
            {
                var dag = new TermDag();
                Assert.True(extractor.TryExtractBest(e, x, dag, out var best));
                Assert.Equal("(B)", dag.ToString(best.Term));
                var variants = extractor.ExtractVariants(e, x, 5, dag);
                Assert.Equal(["(B)", "(A (C))"], variants.Select(v => dag.ToString(v.Term)).ToArray());
                Assert.Equal(best, variants[0]);
            }
        }

    }

}
