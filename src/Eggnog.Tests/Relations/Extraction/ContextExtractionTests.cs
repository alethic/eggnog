using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Extraction over a context: a child sees its parent's terms and its own, a class merged in the child extracts the
    /// child's cheapest member, staleness follows the extraction tables and the union-find of the context and its
    /// non-frozen ancestors and never the clock, and a frozen context serves any number of extractors on any threads.
    /// </summary>
    public class ContextExtractionTests
    {

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildSeesItsParentsTerms(bool freezeParent)
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var b = g.Root.AddTerm(g.Leaf, [g.Int(2)]);
            var p = g.Root.AddTerm(g.Pair, [a, b]);
            if (freezeParent)
                g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var extractor = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Same(child, extractor.Context);
            Assert.True(extractor.IsOnDemand);
            Assert.Equal("(pair (leaf 1) (leaf 2))", g.Text(extractor, p));
            Assert.Equal(5, g.Cost(extractor, p));
            Assert.Equal(3, extractor.ClassCount);

            // The grandchild reads through two levels it never wrote.
            using var grandchild = child.CreateChild();
            Assert.Equal("(pair (leaf 1) (leaf 2))", g.Text(Extractor.CreateOnDemand(g.Schema, grandchild), p));
        }

        [Fact]
        public void TheRootContextFormMatchesTheRootApi()
        {
            for (var seed = 0; seed < 40; seed++)
            {
                var graph = new RandomExtractionGraph(seed, positiveCosts: seed % 2 == 0);
                var schema = graph.Fx.Schema;
                var root = Extractor.CreateOnDemand(schema);
                var context = Extractor.CreateOnDemand(schema, graph.Fx.EGraph.Root);
                Assert.Same(graph.Fx.EGraph.Root, root.Context);
                foreach (var id in graph.Classes)
                {
                    var value = new Value(id);
                    var found = root.TryExtractTerm(graph.E, value, out var expected);
                    Assert.Equal(found, context.TryExtractTerm(graph.E, value, out var actual));
                    Assert.Equal(expected?.ToString(), actual?.ToString());
                    Assert.Equal(expected?.Cost, actual?.Cost);
                    Assert.Equal(root.TryGetChoice(graph.E, value, out var f1, out var r1, out _), context.TryGetChoice(graph.E, value, out var f2, out var r2, out _));
                    Assert.Equal(f1, f2);
                    Assert.Equal(r1, r2);
                }
            }
        }

        [Fact]
        public void ChildOnlyTermsAreSeenInTheChildAndBelowOnly()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            using var child = g.Root.CreateChild();
            var c = child.AddTerm(g.Leaf, [g.Int(7)]);
            var q = child.AddTerm(g.Pair, [a, c]);
            Assert.True(child.Context.IsLocalId(q.Rep));

            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(pair (leaf 1) (leaf 7))", g.Text(inChild, q));
            Assert.Equal(5, g.Cost(inChild, q));

            // The root never sees the child's rows; its own classes are unaffected.
            var atRoot = Extractor.CreateOnDemand(g.Schema, g.Root);
            Assert.False(atRoot.TryGetCost(g.E, q, out _));
            Assert.False(atRoot.TryGetCost(g.E, c, out _));
            Assert.Equal("(leaf 1)", g.Text(atRoot, a));

            // A sibling does not see them either; a grandchild does.
            using var sibling = g.Root.CreateChild();
            Assert.False(Extractor.CreateOnDemand(g.Schema, sibling).TryGetCost(g.E, q, out _));
            using var grandchild = child.CreateChild();
            var r = grandchild.AddTerm(g.Wrap, [q]);
            Assert.Equal("(wrap (pair (leaf 1) (leaf 7)))", g.Text(Extractor.CreateOnDemand(g.Schema, grandchild), r));
        }

        /// <summary>
        /// A union in the child makes the class extract the child's cheapest member, whichever side wins: when the
        /// expensive side loses, the parent's rows naming it are hidden in the child and copied canonically, and
        /// discovery must read the copies, not the hidden rows. The parent's extraction is unchanged.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AClassMergedInTheChildExtractsTheChildsCheapestMember(bool cheapFirst)
        {
            var g = new ContextExtractionGraph();
            var cheap = cheapFirst ? g.Root.AddTerm(g.Leaf, [g.Int(9)]) : default;
            var deep = g.Root.AddTerm(g.Wrap, [g.Root.AddTerm(g.Wrap, [g.Root.AddTerm(g.Leaf, [g.Int(1)])])]);
            if (cheapFirst == false)
                cheap = g.Root.AddTerm(g.Leaf, [g.Int(9)]);

            var top = g.Root.AddTerm(g.Pair, [deep, g.Root.AddTerm(g.Leaf, [g.Int(1)])]);
            var atRoot = Extractor.CreateOnDemand(g.Schema, g.Root);
            Assert.Equal("(pair (wrap (wrap (leaf 1))) (leaf 1))", g.Text(atRoot, top));

            using var child = g.Root.CreateChild();
            child.StageUnion(deep, cheap);
            child.FlushUpdates();
            Assert.Equal(child.Find(cheap), child.Find(deep));

            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(leaf 9)", g.Text(inChild, deep));
            Assert.Equal("(leaf 9)", g.Text(inChild, cheap));
            Assert.Equal("(pair (leaf 9) (leaf 1))", g.Text(inChild, top));
            Assert.Equal(5, g.Cost(inChild, top));
            Assert.Equal(2, inChild.ExtractVariants(g.E, deep, 5, new TermDag()).Count);

            // The parent still extracts its own terms, with the extractor made before the child's union.
            Assert.Equal("(pair (wrap (wrap (leaf 1))) (leaf 1))", g.Text(atRoot, top));
            Assert.Equal("(wrap (wrap (leaf 1)))", g.Text(atRoot, deep));
        }

        /// <summary>
        /// A child under a frozen parent subsumes or deletes the parent's cheapest node of a class: the parent's row is
        /// hidden in the child (a subsumed copy, or nothing, takes its place), and the child's extraction must skip it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARowRetractedInTheChildIsNotExtracted(bool delete)
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var w = g.Root.AddTerm(g.Wrap, [a]);
            var p = g.Root.AddTerm(g.Pair, [a, a]);
            g.Root.StageUnion(w, p);
            g.Root.Freeze();
            var x = g.Root.Find(w);
            Assert.Equal("(wrap (leaf 1))", g.Text(Extractor.CreateOnDemand(g.Schema, g.Root), x));

            using var child = g.Root.CreateChild();
            var action = new TableAction(g.Fx.EGraph, g.Wrap);
            var key = new uint[] { a.Rep };
            child.WithExecutionState(state =>
            {
                if (delete)
                    action.Remove(state, key);
                else
                    action.Subsume(state, key);

                return 0;
            });
            child.FlushUpdates();

            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(inChild, x));
            Assert.Equal(5, g.Cost(inChild, x));
            Assert.Single(inChild.ExtractVariants(g.E, x, 5, new TermDag()));
            Assert.Equal("(wrap (leaf 1))", g.Text(Extractor.CreateOnDemand(g.Schema, g.Root), x));
        }

        /// <summary>
        /// An extraction at the root refreshes the output-column indexes after the last merge; the root's freeze then
        /// compacts the tables it rebuilt, renumbering their rows. The frozen catalogs must not keep indexes over the
        /// old row ids, which a child's extraction (or a child's rules) would otherwise probe: the freeze rebuilds
        /// them.
        /// </summary>
        [Fact]
        public void AnIndexReadBeforeTheFreezeIsRebuiltWhenTheFreezeCompacts()
        {
            var g = new ContextExtractionGraph();
            var leaves = Enumerable.Range(0, 40).Select(i => g.Root.AddTerm(g.Leaf, [g.Int(i)])).ToArray();
            var pairs = leaves.Select(l => g.Root.AddTerm(g.Pair, [l, l])).ToArray();
            // A few unions leave a few tombstones, below the merge's compaction threshold; the freeze compacts them.
            for (var i = 1; i < 6; i++)
                g.Root.StageUnion(leaves[0], leaves[i]);

            g.Root.FlushUpdates();
            var x = g.Root.Find(pairs[5]);
            var before = Extractor.CreateOnDemand(g.Schema, g.Root);
            Assert.Equal("(pair (leaf 0) (leaf 0))", g.Text(before, x));
            var generation = g.Fx.EGraph.Database.GetTable(g.Fx.EGraph.GetTable(g.Pair)).Version.Major;

            g.Root.Freeze();
            Assert.NotEqual(generation, g.Fx.EGraph.Database.GetTable(g.Fx.EGraph.GetTable(g.Pair)).Version.Major);
            using var child = g.Root.CreateChild();
            var y = child.AddTerm(g.Wrap, [x]);
            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(wrap (pair (leaf 0) (leaf 0)))", g.Text(inChild, y));
            Assert.Equal("(pair (leaf 0) (leaf 0))", g.Text(Extractor.CreateOnDemand(g.Schema, g.Root), x));
        }

        [Fact]
        public void UnrelatedTablesKeepAnExtractorCurrent()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var p = g.Root.AddTerm(g.Pair, [a, a]);
            using var child = g.Root.CreateChild();
            var atRoot = Extractor.CreateOnDemand(g.Schema, g.Root);
            var legacy = Extractor.CreateOnDemand(g.Schema);
            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(inChild, p));

            // Writes to a declared function extraction never uses and to an undeclared one, in the child and in its
            // parent, which is not frozen. The child is then behind its parent, and still reads only what it read
            // before.
            child.AddValues([(g.Weight, [a, g.Int(3)]), (g.Mark, [p, g.Int(4)])]);
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(inChild, p));
            g.Root.AddValues([(g.Weight, [p, g.Int(5)]), (g.Mark, [a, g.Int(6)])]);
            Assert.False(child.IsClean);
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(inChild, p));
            Assert.Equal(5, g.Cost(inChild, p));
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(atRoot, p));
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(legacy, p));
            child.EnsureClean();
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(inChild, p));
        }

        /// <summary>
        /// Flushes with nothing staged, batches that restate rows already there, rule runs that derive nothing and
        /// <c>EnsureClean</c> tick the clock, but change nothing an extractor reads, so its extractors stay current.
        /// </summary>
        [Fact]
        public void NoOpBatchesAndRunsKeepAnExtractorCurrent()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var p = g.Root.AddTerm(g.Pair, [a, a]);
            var restate = g.RestatePairs();
            using var child = g.Root.CreateChild();
            var w = child.AddTerm(g.Wrap, [p]);
            var atRoot = Extractor.CreateOnDemand(g.Schema, g.Root);
            var legacy = Extractor.CreateOnDemand(g.Schema);
            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            var clock = g.Fx.EGraph.NextTimestamp;

            Assert.False(child.FlushUpdates());
            Assert.False(g.Root.FlushUpdates());
            child.AddValues([(g.Pair, [a, a, p]), (g.Wrap, [p, w]), (g.Leaf, [g.Int(1), a])]);
            g.Root.AddValues([(g.Pair, [a, a, p]), (g.Leaf, [g.Int(1), a])]);
            Assert.False(g.Root.RunRules(restate).Changed);
            Assert.False(child.RunRules(restate).Changed);
            child.EnsureClean();
            g.Root.EnsureClean();
            Assert.True(g.Fx.EGraph.NextTimestamp > clock);

            Assert.Equal("(wrap (pair (leaf 1) (leaf 1)))", g.Text(inChild, w));
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(atRoot, p));
            Assert.Equal("(pair (leaf 1) (leaf 1))", g.Text(legacy, p));
        }

        [Fact]
        public void ARelevantWriteOrUnionInTheContextMakesItStale()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            using var child = g.Root.CreateChild();
            var b = child.AddTerm(g.Leaf, [g.Int(2)]);

            var extractor = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(leaf 2)", g.Text(extractor, b));
            child.AddTerm(g.Wrap, [a]);
            Assert.Contains("wrap changed", Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(g.E, a, out _)).Message);

            // A union moves the union-find, even one that touches no row.
            extractor = Extractor.CreateOnDemand(g.Schema, child);
            child.StageUnion(a, b);
            child.FlushUpdates();
            Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() => extractor.ExtractTerm(g.E, a)).Message);
            extractor = Extractor.CreateOnDemand(g.Schema, child);
            child.StageUnion(child.FreshId(), child.FreshId());
            child.FlushUpdates();
            Assert.Contains("e-graph changed", Assert.Throws<InvalidOperationException>(() => extractor.ExtractVariants(g.E, a, 1, new TermDag())).Message);
        }

        [Fact]
        public void ARelevantWriteOrUnionInANonFrozenAncestorMakesItStale()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var b = g.Root.AddTerm(g.Leaf, [g.Int(2)]);
            var c = g.Root.AddTerm(g.Leaf, [g.Int(3)]);
            using var child = g.Root.CreateChild();
            using var grandchild = child.CreateChild();

            var extractor = Extractor.CreateOnDemand(g.Schema, grandchild);
            Assert.Equal("(leaf 1)", g.Text(extractor, a));
            g.Root.AddTerm(g.Wrap, [a]);
            Assert.Contains("wrap changed", Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(g.E, a, out _)).Message);

            grandchild.EnsureClean();
            extractor = Extractor.CreateOnDemand(g.Schema, grandchild);
            g.Root.StageUnion(g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            Assert.Contains("e-graph changed", Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(g.E, a, out _)).Message);

            // A subsumption in an ancestor rewrites its row.
            grandchild.EnsureClean();
            extractor = Extractor.CreateOnDemand(g.Schema, grandchild);
            g.Fx.Subsume(g.Leaf, g.Int(3));
            Assert.Contains("leaf changed", Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(g.E, a, out _)).Message);
            grandchild.EnsureClean();
            Assert.False(Extractor.CreateOnDemand(g.Schema, grandchild).TryGetCost(g.E, c, out _));

            extractor = Extractor.CreateOnDemand(g.Schema, grandchild);
            child.StageUnion(a, b);
            child.FlushUpdates();
            Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(g.E, a, out _));
            grandchild.EnsureClean();
            Assert.Equal(grandchild.Find(a), grandchild.Find(b));
            Assert.Equal("(leaf 1)", g.Text(Extractor.CreateOnDemand(g.Schema, grandchild), b));
        }

        [Fact]
        public void FrozenAncestorsAreNotWatched()
        {
            // Under a frozen parent the child's own writes still count, and nothing else can change.
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            var inChild = Extractor.CreateOnDemand(g.Schema, child);
            var atRoot = Extractor.CreateOnDemand(g.Schema, g.Root);
            child.AddTerm(g.Wrap, [a]);
            Assert.Throws<InvalidOperationException>(() => inChild.TryGetCost(g.E, a, out _));
            Assert.Equal("(leaf 1)", g.Text(atRoot, a));

            // An extractor made before its context froze stays current through the freeze.
            var before = Extractor.CreateOnDemand(g.Schema, child);
            child.Freeze();
            Assert.Equal("(leaf 1)", g.Text(before, a));
            Assert.Equal("(wrap (leaf 1))", g.Text(Extractor.CreateOnDemand(g.Schema, child), child.LookupId(g.Wrap, [a])!.Value));

            // A clear hides the frozen parent's rows: the layer's structure moves although it holds no row.
            using var clearing = g.Root.CreateChild();
            var cleared = Extractor.CreateOnDemand(g.Schema, clearing);
            Assert.Equal("(leaf 1)", g.Text(cleared, a));
            clearing.ClearTable(g.Leaf);
            Assert.Contains("leaf changed", Assert.Throws<InvalidOperationException>(() => cleared.TryGetCost(g.E, a, out _)).Message);
            Assert.False(Extractor.CreateOnDemand(g.Schema, clearing).TryGetCost(g.E, a, out _));
        }

        [Fact]
        public void CreationAndQueriesCheckTheContext()
        {
            var g = new ContextExtractionGraph();
            var a = g.Root.AddTerm(g.Leaf, [g.Int(1)]);
            var other = new ContextExtractionGraph();
            Assert.Throws<ArgumentException>(() => Extractor.CreateOnDemand(g.Schema, other.Root));
            Assert.Throws<ArgumentNullException>(() => Extractor.CreateOnDemand(g.Schema, (EGraphContext)null!));

            // Not clean: a host write is staged.
            var child = g.Root.CreateChild();
            child.StageRow(g.Leaf, [g.Int(5), child.FreshId()]);
            Assert.Throws<InvalidOperationException>(() => Extractor.CreateOnDemand(g.Schema, child));
            child.FlushUpdates();

            var extractor = Extractor.CreateOnDemand(g.Schema, child);
            Assert.Equal("(leaf 1)", g.Text(extractor, a));

            // Another thread may not enter the domain of a context that is not frozen while this one holds it.
            Exception? fromAnotherThread = null;
            var visited = false;
            child.ForEach(g.Leaf, row =>
            {
                if (visited)
                    return;

                visited = true;
                var other = new Thread(() => fromAnotherThread = Record.Exception(() => extractor.TryGetCost(g.E, a, out var unused)));
                other.Start();
                other.Join();
            });
            Assert.True(visited);
            Assert.IsType<InvalidOperationException>(fromAnotherThread);
            Assert.False(child.Context.Domain.IsHeld);

            // The refused query broke nothing: it failed before it started.
            Assert.Equal(2, g.Cost(extractor, a));

            // A cost function may read the context it costs, but not write it; a query that throws leaves no hold.
            var reads = Extractor.CreateOnDemand(g.Schema, child, CostModel.Default.WithNodeCost(g.Leaf, (_, row) => child.Find(row.Values[1]).Rep >= 0 ? 2 : 0));
            Assert.Equal(3, g.Cost(reads, a));
            var writes = Extractor.CreateOnDemand(g.Schema, child, CostModel.Default.WithNodeCost(g.Leaf, (_, _) => child.FlushUpdates() ? 1 : 1));
            Assert.Throws<InvalidOperationException>(() => writes.TryGetCost(g.E, a, out _));
            Assert.False(child.Context.Domain.IsHeld);
            Assert.Equal("(leaf 1)", g.Text(Extractor.CreateOnDemand(g.Schema, child), a));

            child.Dispose();
            Assert.Throws<ObjectDisposedException>(() => extractor.TryGetCost(g.E, a, out _));
            Assert.Throws<ObjectDisposedException>(() => Extractor.CreateOnDemand(g.Schema, child));
        }

        /// <summary>
        /// Eight threads extract from one frozen context at once, each with extractors of its own and in an order of
        /// its own; the frozen layers' output-column indexes are built lazily by whichever thread needs them first.
        /// Every answer equals the serial one. Root extractors over the frozen root run alongside.
        /// </summary>
        [Fact]
        public void AFrozenContextServesEightThreadsExtractingConcurrently()
        {
            const int Threads = 8;
            var g = new ContextExtractionGraph();
            var random = new Random(17);
            var ids = new List<Value>();
            for (var i = 0; i < 60; i++)
                ids.Add(g.Root.AddTerm(g.Leaf, [g.Int(i % 13)]));

            for (var i = 0; i < 120; i++)
            {
                var x = ids[random.Next(ids.Count)];
                ids.Add(random.Next(2) == 0 ? g.Root.AddTerm(g.Wrap, [x]) : g.Root.AddTerm(g.Pair, [x, ids[random.Next(ids.Count)]]));
            }

            for (var i = 0; i < 15; i++)
                g.Root.StageUnion(ids[random.Next(ids.Count)], ids[random.Next(ids.Count)]);

            g.Root.Freeze();
            var rootClasses = ids.Select(id => g.Root.Find(id)).Distinct().ToArray();
            using var child = g.Root.CreateChild();
            for (var i = 0; i < 80; i++)
            {
                var x = ids[random.Next(ids.Count)];
                ids.Add(random.Next(2) == 0 ? child.AddTerm(g.Wrap, [x]) : child.AddTerm(g.Pair, [x, ids[random.Next(ids.Count)]]));
            }

            for (var i = 0; i < 10; i++)
                child.StageUnion(ids[random.Next(ids.Count)], ids[random.Next(ids.Count)]);

            child.Freeze();
            var classes = ids.Select(id => child.Find(id)).Distinct().ToArray();
            var serial = Extractor.CreateOnDemand(g.Schema, child);
            var expected = classes.ToDictionary(c => c, c => (g.Text(serial, c), g.Cost(serial, c)));
            var serialRoot = Extractor.CreateOnDemand(g.Schema);
            var expectedRoot = rootClasses.ToDictionary(c => c, c => (g.Text(serialRoot, c), g.Cost(serialRoot, c)));

            using var start = new Barrier(Threads);
            var failures = new Exception?[Threads];
            var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
            {
                try
                {
                    var order = new Random(t);
                    start.SignalAndWait();
                    for (var round = 0; round < 6; round++)
                    {
                        var extractor = Extractor.CreateOnDemand(g.Schema, child);
                        foreach (var c in classes.OrderBy(_ => order.Next()).ToArray())
                            Assert.Equal(expected[c], (g.Text(extractor, c), g.Cost(extractor, c)));

                        var atRoot = (t + round) % 2 == 0 ? Extractor.CreateOnDemand(g.Schema) : Extractor.CreateOnDemand(g.Schema, g.Root);
                        foreach (var c in rootClasses.OrderBy(_ => order.Next()).ToArray())
                            Assert.Equal(expectedRoot[c], (g.Text(atRoot, c), g.Cost(atRoot, c)));
                    }
                }
                catch (Exception e)
                {
                    failures[t] = e;
                }
            })).ToArray();

            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                thread.Join();

            Assert.All(failures, Assert.Null);
        }

    }

}
