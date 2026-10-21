using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Pairs of values, egglog's <c>Pair</c> container: registration, writes and reads, rules, scopes, rendering,
    /// serialization and extraction.
    /// </summary>
    public sealed class PairTests
    {

        [Fact]
        public void RegistrationNamesThePairAndIsIdempotent()
        {
            var graph = new EGraph();
            var type = graph.Values.RegisterPair<long, string>();
            Assert.Equal("Pair<i64, String>", type.Name);
            Assert.Same(type, graph.Values.RegisterPair<long, string>());
            Assert.Same(type, graph.Values.TypeOf<Pair<long, string>>());
            Assert.Equal(ColumnKind.Value, type.Kind);
        }

        [Fact]
        public void PairsOfClassesAndOtherRegistrationsAreRefused()
        {
            var t = new NodeTheory();
            var sort = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterPair<long, Node>());
            Assert.Contains("Node is a sort", sort.Message, StringComparison.Ordinal);
            var register = Assert.Throws<ArgumentException>(() => t.Graph.Values.Register<Pair<long, long>>("P"));
            Assert.Contains("ValueStore.RegisterPair", register.Message, StringComparison.Ordinal);
            var unknown = Assert.Throws<ArgumentException>(() => t.Graph.Values.TypeOf<Pair<long, bool>>());
            Assert.Contains("ValueStore.RegisterPair", unknown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADefaultPairIsRefused()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<string, long>();
            var p = graph.DefineFunction<Pair<string, long>>("p");
            Assert.Throws<ArgumentException>(() => graph.Root.Assume(p.Of().Is(default(Pair<string, long>))));
            Assert.Equal(0, graph.Root.CountRows(p));
            Assert.Throws<ArgumentNullException>(() => new Pair<string, long>(null!, 1));
        }

        [Fact]
        public void PairsRoundTripNestedAndInternOnce()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, bool>();
            var nested = graph.Values.RegisterPair<string, Pair<long, bool>>();
            Assert.Equal("Pair<String, Pair<i64, bool>>", nested.Name);
            var f = graph.DefineFunction<long, Pair<string, Pair<long, bool>>>("f");
            var value = new Pair<string, Pair<long, bool>>("a", new Pair<long, bool>(3, true));

            var before = graph.Values.InternedCount;
            graph.Root.Assume(f.Of(1L).Is(value), f.Of(2L).Is(value));
            Assert.Equal(value, graph.Root.Get(f.Of(1L)));
            Assert.True(graph.Root.Check(f.Of(1L).Is(f.Of(2L))));

            // "a", the inner pair and the outer pair, each once.
            Assert.Equal(before + 3, graph.Values.InternedCount);

            // A read never interns, even of a pair never written.
            var unseen = new Pair<string, Pair<long, bool>>("a", new Pair<long, bool>(4, true));
            Assert.False(graph.Root.Check(f.Of(1L).Is(unseen)));
            Assert.Equal(before + 3, graph.Values.InternedCount);
        }

        [Fact]
        public void ARuleComputesPairs()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var pair = Primitives.PairOf<long, long>();
            var first = Primitives.PairFirst<long, long>();
            var second = Primitives.PairSecond<long, long>();
            var p = graph.DefineFunction<long, Pair<long, long>>("p");
            var swapped = graph.DefineFunction<long, Pair<long, long>>("swapped");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("swap", r =>
            {
                var k = r.Var<long>("k");
                var x = r.Var<Pair<long, long>>("x");
                r.When(x.Is(p.Of(k)));
                r.Then(swapped.Of(k).Is(pair.Of(second.Of(x), first.Of(x))));
            });
            graph.Root.Assume(p.Of(1L).Is(pair.Of(1L, 2L)), p.Of(2L).Is(pair.Of(5L, 5L)));

            graph.Root.Run(Schedule.Run(rules));

            Assert.Equal(new Pair<long, long>(2, 1), graph.Root.Get(swapped.Of(1L)));
            Assert.True(graph.Root.Check(swapped.Of(2L).Is(p.Of(2L))));
            Assert.Equal("(rule ((= x (p k))) ((set (swapped k) (pair (pair-second x) (pair-first x)))) :ruleset rules)", rule.ToString());
        }

        [Fact]
        public void ChildScopesInternPairsOnceAndKeepThem()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var pair = Primitives.PairOf<long, long>();
            var p = graph.DefineFunction<long, Pair<long, long>>("p");
            var q = graph.DefineFunction<long, Pair<long, long>>("q");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("copy", r =>
            {
                var k = r.Var<long>("k");
                var x = r.Var<Pair<long, long>>("x");
                r.When(x.Is(p.Of(k)));
                r.Then(q.Of(k).Is(pair.Of(Primitives.PairSecond<long, long>().Of(x), k)));
            });
            graph.Root.Freeze();

            var before = graph.Values.InternedCount;
            var a = graph.Root.CreateChild();
            using var b = graph.Root.CreateChild();
            a.Assume(p.Of(1L).Is(pair.Of(5L, 6L)));
            Assert.Equal(before + 1, graph.Values.InternedCount);
            b.Assume(p.Of(1L).Is(pair.Of(5L, 6L)));
            Assert.Equal(before + 1, graph.Values.InternedCount);

            a.Run(Schedule.Run(rules));
            Assert.Equal(new Pair<long, long>(6, 1), a.Get(q.Of(1L)));
            Assert.Equal(before + 2, graph.Values.InternedCount);
            a.Dispose();

            b.Run(Schedule.Run(rules));
            Assert.Equal(new Pair<long, long>(6, 1), b.Get(q.Of(1L)));
            Assert.Equal(before + 2, graph.Values.InternedCount);
        }

        [Fact]
        public void APairSerializesAsANodeOfItsElements()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var p = graph.DefineFunction<Pair<long, long>>("p");
            graph.Root.Assume(p.Of().Is(Primitives.PairOf<long, long>().Of(1L, 2L)));
            var before = graph.Values.InternedCount;

            var serialized = graph.Root.Serialize();

            var node = serialized.Nodes.Single(n => n.Op == "Pair<i64, i64>");
            Assert.Equal(new[] { "primitive-i64-1", "primitive-i64-2" }, node.Children);
            Assert.Equal("Pair<i64, i64>", serialized.Classes.Single(c => c.Id == node.Class).Type);
            Assert.True(serialized.TryGetNode("function-0-p", out var row));
            Assert.Equal(node.Class, row.Class);
            Assert.Equal(before, graph.Values.InternedCount);
        }

        [Fact]
        public void APairExtractsAsItsElementsAndCostsTheirSum()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterPair<long, long>();
            var leaf = t.Graph.DefineConstructor<Pair<long, long>, Node>("Leaf");
            var x = t.Root.Make(leaf.Of(Primitives.PairOf<long, long>().Of(1L, 2L)));

            var term = t.Root.Extract(x);
            Assert.Equal("(Leaf (pair 1 2))", term.ToString());
            Assert.Equal(3, term.Cost);
            Assert.Equal(new Pair<long, long>(1, 2), term.Value<Pair<long, long>>(0));

            var costs = CostModel.Default.WithValueCost(t.Graph.Values.TypeOf<long>(), 5);
            Assert.Equal(11, t.Root.Extract(x, costs).Cost);
            Assert.Throws<ArgumentException>(() => CostModel.Default.WithValueCost(t.Graph.Values.TypeOf<Pair<long, long>>(), 5));
        }

        [Fact]
        public void RunningOutOfIdsForAPairFaultsNothing()
        {
            // A pair takes an id, so running out of ids while a rule or a write interns one is the store's limit, as for a
            // term: OverflowException, the run's writes discarded, the scope unfaulted (architecture, section 2.12).
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var pair = Primitives.PairOf<long, long>();
            var num = graph.DefineRelation<long>("num");
            var p = graph.DefineFunction<long, Pair<long, long>>("p");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("pair", r =>
            {
                var k = r.Var<long>("k");
                r.When(num.Of(k));
                r.Then(p.Of(k).Is(pair.Of(k, k)));
            });
            graph.Root.Assume(num.Of(1L));
            var child = graph.Root.CreateChild();
            child.Assume(num.Of(2L));
            LeaveIds(graph, 0);

            Assert.Throws<OverflowException>(() => child.Run(Schedule.Run(rules)));
            Assert.False(child.IsFaulted);
            Assert.Equal(0, child.CountRows(p));
            Assert.Throws<OverflowException>(() => graph.Root.Run(Schedule.Run(rules)));
            Assert.False(graph.Root.IsFaulted);
            Assert.Throws<OverflowException>(() => graph.Root.Assume(p.Of(3L).Is(pair.Of(3L, 4L))));
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(0, graph.Root.CountRows(p));
        }

        [Fact]
        public void ACallbacksDefaultPairFailsTheRuleEvenWhenCaught()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<string, long>();
            var num = graph.DefineRelation<long>("num");
            var p = graph.DefineFunction<long, Pair<string, long>>("p");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("default", r =>
            {
                var k = r.Var<long>("k");
                r.When(num.Of(k));
                r.ThenCall(ctx =>
                {
                    try
                    {
                        ctx.Set(p, [ctx.Get(k)], default(Pair<string, long>));
                    }
                    catch (ArgumentException)
                    {
                    }
                });
            });
            graph.Root.Assume(num.Of(1L));

            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(Schedule.Run(rules)));
        }

        [Fact]
        public void ALiftedPairCountsAsAValue()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var pair = Primitives.PairOf<long, long>();
            var p = graph.DefineFunction<long, Pair<long, long>>("p");
            var keyed = graph.DefineRelation<Pair<long, long>>("keyed");
            using var cases = graph.Root.OpenCases();
            foreach (var @case in new[] { cases.Add(), cases.Add() })
                @case.Assume(p.Of(1L).Is(pair.Of(1L, 2L)), keyed.Of(pair.Of(8L, 8L)));

            var report = cases.Lift();

            Assert.Equal(1, report.Values);
            Assert.Equal(1, report.Rows);
            Assert.Equal(new Pair<long, long>(1, 2), graph.Root.Get(p.Of(1L)));
            Assert.True(graph.Root.Check(keyed.Of(pair.Of(8L, 8L))));
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

        [Fact]
        public void ALatticeWithACaseJoinOverAPairIsRefused()
        {
            var graph = new EGraph();
            graph.Values.RegisterPair<long, long>();
            var lattice = new ValueLattice<Pair<long, long>>("pick", static (a, _) => a, static (_, b) => b);
            var refused = Assert.Throws<ArgumentException>(() => graph.DefineFunction<Pair<long, long>>("m", lattice));
            Assert.Contains("case join", refused.Message, StringComparison.Ordinal);

            graph.DefineFunction<Pair<long, long>>("kept", new ValueLattice<Pair<long, long>>("first", static (a, _) => a));
        }

    }

}
