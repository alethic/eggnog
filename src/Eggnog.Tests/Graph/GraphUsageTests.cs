using System;

using Eggnog.Tests.Scopes;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Graph
{

    /// <summary>
    /// <see cref="EGraph.Usage"/> reports the graph's interned values, ids and epochs against their limits, and moves as
    /// the graph's scopes draw on them. When the ids or the epochs run out, every path that needs one throws
    /// <see cref="OverflowException"/> and leaves the scope unfaulted: a host write that makes a term, a run whose rule
    /// makes one, at the root and in a child, a write batch, a child's creation, and a run that runs out part-way. The
    /// tests move the store's counters to just below their limits, as a long-lived graph would reach them.
    /// </summary>
    public sealed class GraphUsageTests
    {

        /// <summary>
        /// A new graph reports the limits of its ids and epochs, the size of its pools, and almost nothing used.
        /// </summary>
        [Fact]
        public void ANewGraphReportsItsLimits()
        {
            var graph = new EGraph();
            var usage = graph.Usage;

            Assert.Equal(Array.MaxLength, usage.IdLimit);
            Assert.Equal(uint.MaxValue - 1L, usage.EpochLimit);
            Assert.Equal(graph.Values.InternedCount, usage.InternedValues);
            Assert.InRange(usage.IdsReserved, 0, 16);
            Assert.InRange(usage.Epochs, 0, 16);
            Assert.InRange(usage.FractionUsed, 0, 1e-6);
        }

        /// <summary>
        /// <see cref="GraphUsage.FractionUsed"/> is the larger of the two shares, and a default value counts as unused.
        /// </summary>
        [Fact]
        public void FractionUsedIsTheLargerShareOfALimit()
        {
            Assert.Equal(0.5, new GraphUsage(7, 50, 100, 10, 1000).FractionUsed);
            Assert.Equal(0.25, new GraphUsage(0, 1, 100, 250, 1000).FractionUsed);
            Assert.Equal(1, new GraphUsage(0, 100, 100, 0, 1000).FractionUsed);
            Assert.Equal(0, default(GraphUsage).FractionUsed);
        }

        /// <summary>
        /// The gauge reads the graph's counters: a query with a new string interns two values, the string and its
        /// singleton set, and reserves one first id block and the set's id, which a container takes from the graph's id
        /// counter as egglog's do; and the epochs are the store clock's progress since its start.
        /// </summary>
        [Fact]
        public void TheGaugeMovesWithTheGraphsCounters()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            var store = churn.Graph.Engine.Store;
            churn.Derive(c.StrEq.Of("kind", "warm"));
            for (var i = 0; i < 3; i++)
            {
                var before = churn.Graph.Usage;
                churn.Derive(c.StrEq.Of("kind", "gauge" + i));
                var after = churn.Graph.Usage;

                Assert.Equal(before.InternedValues + 2, after.InternedValues);
                Assert.Equal(churn.Graph.Values.InternedCount, after.InternedValues);
                Assert.Equal(before.IdsReserved + Relations.IdBlockAllocator.FirstBlockSize + 1, after.IdsReserved);
                Assert.Equal(store.Counters.Read(store.IdCounter), after.IdsReserved);
                Assert.Equal(store.Counters.Read(store.ClockCounter) - 1, after.Epochs);
                Assert.True(after.Epochs > before.Epochs);
                Assert.True(after.FractionUsed > before.FractionUsed);
            }
        }

        /// <summary>
        /// A query whose first id block does not fit throws <see cref="OverflowException"/> from <c>Make</c>, unfaulted:
        /// it can still write what needs no id, and every later <c>Make</c>, in it or a sibling, throws the same way. The
        /// gauge reads 1.
        /// </summary>
        [Fact]
        public void AQueryThatCannotReserveItsFirstBlockThrowsOverflowUnfaulted()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            LeaveIds(churn.Graph, Relations.IdBlockAllocator.FirstBlockSize - 1);
            using var query = churn.Schema.CreateChild(c.Query);

            Assert.Throws<OverflowException>(() => query.Make(c.StrEq.Of("kind", "a")));
            Assert.False(query.IsFaulted);
            query.Assume(c.Present.Of("written without an id"));
            Assert.True(query.Check(c.Present.Of("written without an id")));
            Assert.Throws<OverflowException>(() => query.Make(c.StrEq.Of("kind", "a")));
            using (var sibling = churn.Schema.CreateChild(c.Query))
                Assert.Throws<OverflowException>(() => sibling.Make(c.StrEq.Of("kind", "a")));

            Assert.Equal(1, churn.Graph.Usage.FractionUsed);
            Assert.Equal(churn.Graph.Usage.IdLimit, churn.Graph.Usage.IdsReserved);
        }

        /// <summary>
        /// At a writable root, the last id is handed out and the next <c>Make</c> throws, unfaulted.
        /// </summary>
        [Fact]
        public void AMakeAtTheRootPastTheLastIdThrowsOverflowUnfaulted()
        {
            var t = new NodeTheory();
            LeaveIds(t.Graph, 1);
            t.Root.Make(t.A.Of());

            Assert.Throws<OverflowException>(() => t.Root.Make(t.B.Of()));
            Assert.False(t.Root.IsFaulted);
            t.Root.Assume(t.Num.Of(1));
            Assert.True(t.Root.Check(t.Num.Of(1)));
            Assert.Equal(1, t.Graph.Usage.FractionUsed);
        }

        /// <summary>
        /// A <c>Make</c> that runs out of ids writes nothing and leaves the scope unfaulted; the batch's calls before it
        /// stand.
        /// </summary>
        [Fact]
        public void AMakeThatRunsOutOfIdsWritesNothing()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            using var scope = t.Root.CreateChild();
            LeaveIds(t.Graph, 0);

            Assert.Throws<OverflowException>(() => scope.Write(w =>
            {
                w.Assume(t.Num.Of(1));
                w.Make(t.A.Of());
            }));

            Assert.False(scope.IsFaulted);
            Assert.True(scope.Check(t.Num.Of(1)));
            Assert.False(scope.Check(Fact.Exists(t.A.Of())));
        }

        /// <summary>
        /// A run whose rule makes a term throws <see cref="OverflowException"/> at the mint when the ids have run out,
        /// at a writable root and in a child alike: the iteration writes nothing, the scope is not faulted, and a second
        /// run throws the same way.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARunWhoseRuleMakesATermThrowsOverflowUnfaultedWhenTheIdsRunOut(bool atRoot)
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("a literal per number", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Tag.Of(t.Lit.Of(x)));
            });
            Scope scope;
            if (atRoot)
            {
                scope = t.Root;
            }
            else
            {
                t.Root.Freeze();
                scope = t.Root.CreateChild();
            }

            scope.Assume(t.Num.Of(1), t.Num.Of(2));
            LeaveIds(t.Graph, 0);

            Assert.Throws<OverflowException>(() => scope.Run(t.Rules));
            Assert.False(scope.IsFaulted);
            Assert.Equal(0, scope.CountRows(t.Tag));
            Assert.Throws<OverflowException>(() => scope.Run(t.Rules));
            Assert.False(scope.IsFaulted);
            Assert.True(scope.Check(t.Num.Of(2)));
        }

        /// <summary>
        /// Once the epochs have run out, no child is created, anywhere in the graph, and a live child's write throws;
        /// nothing is faulted.
        /// </summary>
        [Fact]
        public void OnceTheEpochsRunOutTheGraphCreatesNoChild()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            LeaveEpochs(t.Graph, 0);

            Assert.Throws<OverflowException>(() => t.Root.CreateChild());
            Assert.Throws<OverflowException>(() => child.CreateChild());
            Assert.Single(t.Root.Children);
            Assert.Empty(child.Children);
            Assert.Throws<OverflowException>(() => child.Assume(t.Num.Of(1)));
            Assert.False(child.IsFaulted);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1, t.Graph.Usage.FractionUsed);
            Assert.Equal(t.Graph.Usage.EpochLimit, t.Graph.Usage.Epochs);
            child.Dispose();
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// A query whose run, plain or split, runs out of epochs part-way throws <see cref="OverflowException"/>
        /// unfaulted; a split's cases are disposed with the run, and the query is disposed as any other.
        /// </summary>
        [Theory]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(true, 20)]
        public void ARunThatRunsOutOfEpochsPartWayThrowsOverflowUnfaulted(bool split, int epochsLeft)
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            var query = churn.Schema.CreateChild(c.Query);
            var p = query.Make(c.Not.Of(c.StrEq.Of("kind", "zz")));
            query.Assume(c.WhereClause.Of(p));
            LeaveEpochs(churn.Graph, epochsLeft);

            Assert.Throws<OverflowException>(() => query.Run(split ? churn.Split : churn.Plain));
            Assert.False(query.IsFaulted);
            Assert.Empty(query.Children);
            query.Dispose();
            Assert.Empty(churn.Schema.Children);
            Assert.Equal(0, churn.Schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// A write whose epochs run out before its merge writes nothing and leaves nothing staged: the scope reads as
        /// before, without an epoch.
        /// </summary>
        [Fact]
        public void AWriteThatRunsOutBeforeItsMergeIsDropped()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            using var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            LeaveEpochs(t.Graph, 0);

            Assert.Throws<OverflowException>(() => scope.Assume(t.Num.Of(1)));
            Assert.Throws<OverflowException>(() => scope.Union(a, b));
            Assert.False(scope.IsFaulted);
            Assert.False(scope.Check(t.Num.Of(1)));
            Assert.False(scope.AreEqual(a, b));
            Assert.Equal(0, scope.CountRows(t.Num));
        }

        /// <summary>
        /// A union whose rebuild runs out of epochs between two passes is written: the call throws, unfaulted, and the
        /// scope then reads the union without an epoch, since the passes that ran left nothing to do. With one epoch
        /// more, the rebuild takes the clock's last epoch and the union completes.
        /// </summary>
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void AUnionWhoseRebuildRunsOutBetweenPassesIsWritten(int epochsLeft)
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            using var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            scope.Assume(t.Tag.Of(t.A.Of()));
            LeaveEpochs(t.Graph, epochsLeft);

            if (epochsLeft == 2)
                Assert.Throws<OverflowException>(() => scope.Union(a, b));
            else
                Assert.True(scope.Union(a, b));

            Assert.False(scope.IsFaulted);
            Assert.Equal(1, t.Graph.Usage.FractionUsed);
            Assert.True(scope.AreEqual(a, b));
            Assert.True(scope.Check(t.Tag.Of(t.B.Of())));
            Assert.Equal(1, scope.CountRows(t.Tag));
        }

        /// <summary>
        /// A read of a writable scope that must catch up takes an epoch: once the epochs are out, <c>Check</c>,
        /// <c>TryGet</c>, <c>AreEqual</c>, <c>CountRows</c> and <c>Freeze</c> throw <see cref="OverflowException"/>,
        /// unfaulted, on a scope whose own rebuild ran out before its first pass, and the scope is disposed as any other.
        /// </summary>
        [Fact]
        public void AReadOfAScopeLeftUnrebuiltThrowsOverflowUnfaulted()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            scope.Assume(t.Tag.Of(t.A.Of()));
            LeaveEpochs(t.Graph, 1);

            Assert.Throws<OverflowException>(() => scope.Union(a, b));
            Assert.Throws<OverflowException>(() => scope.Check(t.Tag.Of(t.A.Of())));
            Assert.Throws<OverflowException>(() => scope.TryGet(t.A.Of(), out _));
            Assert.Throws<OverflowException>(() => scope.AreEqual(a, b));
            Assert.Throws<OverflowException>(() => scope.CountRows(t.Tag));
            Assert.Throws<OverflowException>(() => scope.Freeze());
            Assert.False(scope.IsFrozen);
            Assert.False(scope.IsFaulted);
            scope.Dispose();
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// A child of a writable root reads what the root wrote only after catching up, which takes an epoch: once the
        /// epochs are out, the child's reads throw <see cref="OverflowException"/>, unfaulted, while the root, clean,
        /// still reads.
        /// </summary>
        [Fact]
        public void AChildThatMustCatchUpWithItsParentThrowsOverflowOnRead()
        {
            var t = new NodeTheory();
            using var child = t.Root.CreateChild();
            LeaveEpochs(t.Graph, 1);
            t.Root.Assume(t.Num.Of(1));

            Assert.True(t.Root.Check(t.Num.Of(1)));
            Assert.Throws<OverflowException>(() => child.Check(t.Num.Of(1)));
            Assert.Throws<OverflowException>(() => child.CountRows(t.Num));
            Assert.False(child.IsFaulted);
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// The query reads run the query as a rule, which takes an epoch even on a frozen scope: once the epochs are out,
        /// <c>Any</c>, <c>Count</c> and <c>ForEachMatch</c> throw <see cref="OverflowException"/>, while <c>Check</c>
        /// still reads.
        /// </summary>
        [Fact]
        public void QueryReadsThrowOverflowOnceTheEpochsRunOut()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Tag.Of(t.A.Of()));
            t.Root.Freeze();
            var query = t.Graph.DefineQuery("tags", q => q.When(t.Tag.Of(q.Var<Node>("n"))));
            Assert.Equal(1, t.Root.Count(query));
            LeaveEpochs(t.Graph, 0);

            Assert.Throws<OverflowException>(() => t.Root.Any(query));
            Assert.Throws<OverflowException>(() => t.Root.Count(query));
            Assert.Throws<OverflowException>(() => t.Root.ForEachMatch(query, _ => true));
            Assert.True(t.Root.Check(t.Tag.Of(t.A.Of())));
        }

        /// <summary>
        /// Once the epochs are out, <see cref="CaseSet.Add"/> creates no case and leaves the parent unfaulted; the set is
        /// disposed as any other.
        /// </summary>
        [Fact]
        public void ACaseSetAddsNoCaseOnceTheEpochsRunOut()
        {
            var t = new NodeTheory();
            var cases = t.Root.OpenCases();
            LeaveEpochs(t.Graph, 0);

            Assert.Throws<OverflowException>(() => cases.Add());
            Assert.Empty(cases.Cases);
            Assert.False(t.Root.IsFaulted);
            cases.Dispose();
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// A lift whose round runs out of epochs, or of ids for a term it lifts, merges nothing of that round: the parent
        /// and the cases are not faulted, the parent still reads without what the round would have lifted, and the set
        /// is disposed as any other.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ALiftThatRunsOutMergesNothingOfItsRound(bool ids)
        {
            var t = new NodeTheory();
            var cases = t.Root.OpenCases();
            foreach (var @case in new[] { cases.Add(), cases.Add() })
                @case.Assume(t.Num.Of(1), t.Tag.Of(t.A.Of()));

            if (ids)
                LeaveIds(t.Graph, 0);
            else
                LeaveEpochs(t.Graph, 0);

            Assert.Throws<OverflowException>(() => cases.Lift());
            Assert.False(t.Root.IsFaulted);
            Assert.All(cases.Cases, @case => Assert.False(@case.IsFaulted));
            Assert.False(t.Root.Check(t.Num.Of(1)));
            Assert.Equal(0, t.Root.CountRows(t.Tag));
            cases.Dispose();
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// Moves the store's id counter so that <paramref name="left"/> ids remain.
        /// </summary>
        static void LeaveIds(EGraph graph, long left)
        {
            var store = graph.Engine.Store;
            store.Counters.IncrementBy(store.IdCounter, graph.Usage.IdLimit - left - store.Counters.Read(store.IdCounter));
            Assert.Equal(graph.Usage.IdLimit - left, graph.Usage.IdsReserved);
        }

        /// <summary>
        /// Moves the store's clock so that <paramref name="left"/> epochs remain.
        /// </summary>
        static void LeaveEpochs(EGraph graph, long left)
        {
            var store = graph.Engine.Store;
            store.Counters.IncrementBy(store.ClockCounter, graph.Usage.EpochLimit - left - graph.Usage.Epochs);
            Assert.Equal(graph.Usage.EpochLimit - left, graph.Usage.Epochs);
        }

    }

}
