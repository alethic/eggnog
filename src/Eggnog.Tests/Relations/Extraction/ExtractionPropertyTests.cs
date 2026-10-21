using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Property tests of extraction on small random e-graphs (<see cref="RandomExtractionGraph"/>) against independent
    /// references: every class's cost equals a Bellman-Ford fixpoint over the rows; every class's choice is the parent
    /// edge egglog's extractor, run literally, chooses (<see cref="EgglogExtractionReference"/>), and a class egglog
    /// cannot build a term for throws; the extracted term's cost, recomputed over its tree, equals the reported cost;
    /// the term evaluates back to its class; no term found by brute-force enumeration is cheaper; on-demand and
    /// root-sort extractors agree, whatever order classes are asked in (also with mostly zero and saturated costs); and
    /// the variants are egglog's, the class's ten first nodes with a cost by cost, function name and raw row values,
    /// and as object terms they are the same terms with their own costs.
    /// </summary>
    public class ExtractionPropertyTests
    {

        /// <summary>
        /// The depth up to which terms are enumerated.
        /// </summary>
        const int EnumerationDepth = 4;

        /// <summary>
        /// The batches of 100 seeds, each with positive costs and with costs that may be zero.
        /// </summary>
        public static TheoryData<int, bool> Seeds()
        {
            var data = new TheoryData<int, bool>();
            for (var batch = 0; batch < 8; batch++)
            {
                data.Add(batch, true);
                data.Add(batch, false);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void ExtractionIsOptimalSoundAndDeterministic(int batch, bool positiveCosts)
        {
            var coverage = new int[3];
            for (var seed = batch * 100; seed < (batch + 1) * 100; seed++)
                Check(new RandomExtractionGraph(seed, positiveCosts), seed, positiveCosts, coverage);

            // The batch exercised every kind of check: costed classes, classes with no finite term, and optima reached
            // within the enumeration depth.
            Assert.True(coverage[0] >= 400 && coverage[1] >= 20 && coverage[2] >= 300, $"coverage {string.Join(", ", coverage)}");
        }

        static void Check(RandomExtractionGraph graph, int seed, bool positiveCosts, int[] coverage)
        {
            var fx = graph.Fx;
            var all = Extractor.ComputeCostsFromRootSorts(fx.Schema);
            var onDemand = Extractor.CreateOnDemand(fx.Schema);
            var egglog = new EgglogExtractionReference(graph);
            var dag = new TermDag();
            var order = graph.Classes.OrderBy(c => (c * 2654435761u) ^ (uint)seed).ToArray();

            foreach (var owner in order)
            {
                var context = $"seed {seed}, positive {positiveCosts}, class {owner}";
                var value = new Value(owner);
                var hasReference = graph.ReferenceCosts.TryGetValue(owner, out var reference);

                // The cost matches the fixpoint, and both extractors agree.
                Assert.True(hasReference == all.TryGetCost(graph.E, value, out var cost), context);
                Assert.True(hasReference == onDemand.TryGetCost(graph.E, value, out var lazyCost), context);
                if (hasReference == false)
                {
                    coverage[1]++;
                    Assert.Empty(graph.Enumerate(owner, EnumerationDepth));
                    Assert.False(all.TryExtractTerm(graph.E, value, out _), context);
                    continue;
                }

                Assert.True(reference == cost, context);
                Assert.True(reference == lazyCost, context);
                Assert.True(egglog.Costs[owner] == cost, context);
                coverage[0]++;

                // The choice is egglog's parent edge.
                var hasChoice = egglog.Choices.TryGetValue(owner, out var choice);
                Assert.True(hasChoice == all.TryGetChoice(graph.E, value, out var function, out var row), context);
                Assert.True(hasChoice == onDemand.TryGetChoice(graph.E, value, out var lazyFunction, out var lazyRow), context);
                if (hasChoice)
                {
                    Assert.Equal(graph.Functions[choice.Function], function);
                    Assert.Equal(choice.Row, row);
                    Assert.Equal(function, lazyFunction);
                    Assert.Equal(row, lazyRow);
                }

                // Where egglog's reconstruction panics, extraction throws.
                if (egglog.CanBuild(owner) == false)
                {
                    Assert.Throws<InvalidOperationException>(() => all.ExtractTerm(graph.E, value));
                    Assert.Throws<InvalidOperationException>(() => onDemand.ExtractTerm(graph.E, value));
                    continue;
                }

                // The term: its tree cost is its reported cost, every subterm is optimal for its class, and it
                // evaluates back to the class.
                var term = all.ExtractTerm(graph.E, value);
                Assert.Equal(cost, term.Cost);
                Assert.Equal(cost, TreeCost(graph, term, new Dictionary<ExtractedTerm, long>(ReferenceEqualityComparer.Instance)));
                Assert.Equal(owner, Evaluate(graph, term, new Dictionary<ExtractedTerm, uint>(ReferenceEqualityComparer.Instance)));
                Assert.Equal(term.ToString(), onDemand.ExtractTerm(graph.E, value).ToString());

                Assert.True(all.TryExtractBest(graph.E, value, dag, out var best), context);
                Assert.Equal(cost, best.Cost);
                Assert.Equal(term.ToString(), dag.ToString(best.Term));

                // No term of bounded depth is cheaper, and the optimum is reached within the bound when the term fits.
                var enumerated = graph.Enumerate(owner, EnumerationDepth);
                if (enumerated.Count > 0)
                    Assert.True(enumerated[0].Cost >= cost, $"{context}: enumerated {enumerated[0].Text} at {enumerated[0].Cost} beats {term} at {cost}");

                if (Depth(term, new Dictionary<ExtractedTerm, int>(ReferenceEqualityComparer.Instance)) <= EnumerationDepth)
                {
                    Assert.NotEmpty(enumerated);
                    Assert.Equal(cost, enumerated[0].Cost);
                    coverage[2]++;
                }

                // The variants are egglog's, in its order, the first as cheap as the best term; where egglog cannot
                // build one of them, extraction throws.
                var expectedVariants = egglog.Variants(owner, 10);
                if (expectedVariants.Any(v => graph.ChildClasses(v.Function, v.Row).All(egglog.CanBuild) == false))
                {
                    Assert.Throws<InvalidOperationException>(() => all.ExtractVariants(graph.E, value, 10, dag));
                    continue;
                }

                var variants = all.ExtractVariants(graph.E, value, 10, dag);
                Assert.Equal(best.Cost, variants[0].Cost);
                Assert.True(variants.Zip(variants.Skip(1)).All(p => p.First.Cost <= p.Second.Cost), context);
                Assert.Equal(
                    expectedVariants.Select(v => (graph.Functions[v.Function], string.Join(" ", v.Row[..^1].Select(c => c.Rep)))),
                    all.ExtractVariantTerms(graph.E, value, 10)
                        .Select(t => (t.Function, string.Join(" ", t.Children.ToArray().Select(c => c.Value.Rep)))));

                // They are the class's nodes that are not subsumed and have a cost, the ten cheapest of them.
                var nodeCosts = graph.Nodes
                    .Where(n => n.Row[^1].Rep == owner)
                    .Select(n => graph.NodeCost(n.Function, n.Row, graph.ReferenceCosts))
                    .OfType<long>()
                    .Order()
                    .Take(10)
                    .ToArray();
                Assert.Equal(nodeCosts, variants.Select(v => v.Cost).ToArray());
                Assert.Equal(variants.ToArray(), onDemand.ExtractVariants(graph.E, value, 10, dag).ToArray());

                // As object trees they are the same terms: each root carries the class and its own cost, the head's
                // plus its children's, which are the cheapest terms of their classes.
                var variantTerms = onDemand.ExtractVariantTerms(graph.E, value, 10);
                Assert.Equal(variants.Select(v => dag.ToString(v.Term)), variantTerms.Select(t => t.ToString()));
                foreach (var (variant, variantTerm) in variants.Zip(variantTerms))
                {
                    Assert.Equal(owner, variantTerm.Value.Rep);
                    var headAndChildren = graph.Costs[Array.IndexOf(graph.Functions, variantTerm.Function)];
                    foreach (var child in variantTerm.Children)
                        headAndChildren = RandomExtractionGraph.Add(headAndChildren, TreeCost(graph, child, new Dictionary<ExtractedTerm, long>(ReferenceEqualityComparer.Instance)));

                    Assert.Equal(variant.Cost, variantTerm.Cost);
                    Assert.Equal(headAndChildren, variantTerm.Cost);
                }

            }
        }

        /// <summary>
        /// Recomputes a term's cost over its tree, checking each subterm's reported cost and each node's optimality.
        /// </summary>
        static long TreeCost(RandomExtractionGraph graph, ExtractedTerm term, Dictionary<ExtractedTerm, long> memo)
        {
            if (memo.TryGetValue(term, out var known))
                return known;

            long cost = 0;
            switch (term.Kind)
            {
                case ExtractedTermKind.Base:
                    cost = 1;
                    break;
                case ExtractedTermKind.Container:
                    foreach (var child in term.Children)
                        cost = RandomExtractionGraph.Add(cost, TreeCost(graph, child, memo));

                    break;
                default:
                    cost = graph.Costs[Array.IndexOf(graph.Functions, term.Function)];
                    foreach (var child in term.Children)
                        cost = RandomExtractionGraph.Add(cost, TreeCost(graph, child, memo));

                    Assert.Equal(graph.ReferenceCosts[term.Value.Rep], cost);
                    break;
            }

            Assert.Equal(cost, term.Cost);
            memo[term] = cost;
            return cost;
        }

        /// <summary>
        /// Evaluates a term in the e-graph: a node looks up its function's row for its children's values, a container
        /// checks that its elements are its children's values, and a base value is itself.
        /// </summary>
        static uint Evaluate(RandomExtractionGraph graph, ExtractedTerm term, Dictionary<ExtractedTerm, uint> memo)
        {
            if (memo.TryGetValue(term, out var known))
                return known;

            var children = new List<uint>();
            foreach (var child in term.Children)
                children.Add(Evaluate(graph, child, memo));

            uint result;
            switch (term.Kind)
            {
                case ExtractedTermKind.Base:
                    result = term.Value.Rep;
                    break;
                case ExtractedTermKind.Container:
                    Assert.Equal(graph.Elements(term.Value.Rep), children.ToArray());
                    result = term.Value.Rep;
                    break;
                default:
                    var output = graph.Fx.EGraph.LookupId(term.Function, children.Select(c => new Value(c)).ToArray());
                    Assert.NotNull(output);
                    result = graph.Fx.Canon(output.Value).Rep;
                    Assert.Equal(term.Value.Rep, result);
                    break;
            }

            memo[term] = result;
            return result;
        }

        /// <summary>
        /// Gets a term's depth: one per node, none for base values and the vector around elements.
        /// </summary>
        static int Depth(ExtractedTerm term, Dictionary<ExtractedTerm, int> memo)
        {
            if (memo.TryGetValue(term, out var known))
                return known;

            var deepest = 0;
            foreach (var child in term.Children)
                deepest = Math.Max(deepest, Depth(child, memo));

            var depth = term.Kind == ExtractedTermKind.Node ? deepest + 1 : deepest;
            memo[term] = depth;
            return depth;
        }

        /// <summary>
        /// The batches of 100 seeds, each in mode 0 (mostly zero head costs) and mode 1 (costs that saturate).
        /// </summary>
        public static TheoryData<int, int> OrderSeeds()
        {
            var data = new TheoryData<int, int>();
            for (var batch = 0; batch < 4; batch++)
            {
                data.Add(batch, 0);
                data.Add(batch, 1);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(OrderSeeds))]
        public void OnDemandChoicesDoNotDependOnQueryOrder(int batch, int mode)
        {
            // Mostly zero head costs (mode 0) or costs that saturate (mode 1): a node can cost what its child costs, so
            // when a class reached its cost in egglog's passes decides ties. Every class's choice, asked in random orders
            // of a warmed on-demand extractor, is the root-sort extractor's and egglog's.
            var choices = mode == 0 ? new long[] { 0, 0, 0, 1 } : new long[] { 0, 1, long.MaxValue / 2, long.MaxValue };
            var compared = 0;
            var unbuildable = 0;
            for (var seed = batch * 100; seed < (batch + 1) * 100; seed++)
            {
                var graph = new RandomExtractionGraph(seed, false, choices);
                var egglog = new EgglogExtractionReference(graph);
                var all = Extractor.ComputeCostsFromRootSorts(graph.Fx.Schema);
                for (var order = 0; order < 3; order++)
                {
                    var random = new Random(seed * 3 + order);
                    var onDemand = Extractor.CreateOnDemand(graph.Fx.Schema);
                    foreach (var owner in graph.Classes.OrderBy(_ => random.Next()).ToArray())
                    {
                        var context = $"seed {seed}, mode {mode}, order {order}, class {owner}";
                        var value = new Value(owner);
                        var expected = egglog.Choices.TryGetValue(owner, out var choice);
                        Assert.True(expected == all.TryGetChoice(graph.E, value, out var function, out var row), context);
                        Assert.True(expected == onDemand.TryGetChoice(graph.E, value, out var lazyFunction, out var lazyRow), context);
                        if (expected == false)
                            continue;

                        Assert.True(graph.Functions[choice.Function] == function, context);
                        Assert.True(choice.Row.SequenceEqual(row), context);
                        Assert.True(function == lazyFunction, context);
                        Assert.True(row.SequenceEqual(lazyRow), context);
                        if (egglog.CanBuild(owner) == false)
                        {
                            Assert.Throws<InvalidOperationException>(() => onDemand.ExtractTerm(graph.E, value));
                            unbuildable++;
                            continue;
                        }

                        Assert.Equal(all.ExtractTerm(graph.E, value).ToString(), onDemand.ExtractTerm(graph.E, value).ToString());
                        compared++;
                    }
                }
            }

            // The batch compared many choices.
            Assert.True(compared >= 1000, $"compared {compared}, unbuildable {unbuildable}");
        }

        [Fact]
        public void LargeRandomGraphsAgreeWithTheFixpoint()
        {
            // Bigger graphs, costs only: the fixpoint is the reference, enumeration would be too slow.
            for (var seed = 0; seed < 10; seed++)
            {
                var random = new Random(seed);
                var fx = new ExtractionFixture();
                var e = new EqSort("E");
                var costs = new long[] { random.Next(0, 3), random.Next(0, 3), random.Next(0, 3), random.Next(0, 3) };
                var leaf = fx.Constructor("leaf", e, costs[0], fx.I64);
                var un = fx.Constructor("un", e, costs[1], e);
                var bin = fx.Constructor("bin", e, costs[2], e, e);
                var tri = fx.Constructor("tri", e, costs[3], e, e, e);
                var ids = new List<Value>();
                for (var i = 0; i < 400; i++)
                {
                    Value Pick() => fx.Canon(ids[random.Next(ids.Count)]);
                    ids.Add((ids.Count < 5 ? 0 : random.Next(4)) switch
                    {
                        0 => fx.Make(leaf, fx.Int(random.Next(100))),
                        1 => fx.Make(un, Pick()),
                        2 => fx.Make(bin, Pick(), Pick()),
                        _ => fx.Make(tri, Pick(), Pick(), Pick()),
                    });
                    if (random.Next(8) == 0)
                        fx.Union(Pick(), Pick());
                }

                var reference = new Dictionary<uint, long>();
                var nodes = new[] { leaf, un, bin, tri }
                    .SelectMany((f, index) => BridgeTestUtil.Rows(fx.EGraph, f).Select(r => (Index: index, r.Values)))
                    .ToList();
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var (index, values) in nodes)
                    {
                        var cost = index == 0 ? costs[0] + 1 : costs[index];
                        var complete = true;
                        for (var k = 0; index > 0 && k < values.Length - 1; k++)
                        {
                            if (reference.TryGetValue(values[k].Rep, out var child) == false)
                            {
                                complete = false;
                                break;
                            }

                            cost = RandomExtractionGraph.Add(cost, child);
                        }

                        if (complete && (reference.TryGetValue(values[^1].Rep, out var current) == false || cost < current))
                        {
                            reference[values[^1].Rep] = cost;
                            changed = true;
                        }
                    }
                }

                var all = Extractor.ComputeCostsFromRootSorts(fx.Schema);
                var onDemand = Extractor.CreateOnDemand(fx.Schema);
                foreach (var (_, values) in nodes)
                {
                    var owner = values[^1];
                    Assert.True(all.TryGetCost(e, owner, out var cost));
                    Assert.Equal(reference[owner.Rep], cost);
                    Assert.True(onDemand.TryGetCost(e, owner, out var lazy));
                    Assert.Equal(cost, lazy);
                }
            }
        }

    }

}
