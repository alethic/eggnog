using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Vectors of values, egglog's <c>Vec</c> container: registration, writes and reads, rules and their partial
    /// primitives, scopes, rendering, serialization and extraction.
    /// </summary>
    public sealed class VecTests
    {

        [Fact]
        public void RegistrationNamesTheVectorAndRefusesClasses()
        {
            var t = new NodeTheory();
            var type = t.Graph.Values.RegisterVec<string>();
            Assert.Equal("Vec<String>", type.Name);
            Assert.Same(type, t.Graph.Values.RegisterVec<string>());
            Assert.Equal("Vec<Pair<i64, i64>>", RegisterNested(t.Graph).Name);
            var sort = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterVec<Node>());
            Assert.Contains("Node is a sort", sort.Message, StringComparison.Ordinal);
            var register = Assert.Throws<ArgumentException>(() => t.Graph.Values.Register<Vec<long>>("V"));
            Assert.Contains("ValueStore.RegisterVec", register.Message, StringComparison.Ordinal);
            var unknown = Assert.Throws<ArgumentException>(() => t.Graph.Values.TypeOf<Vec<bool>>());
            Assert.Contains("ValueStore.RegisterVec", unknown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void VectorsRoundTripAndInternOnce()
        {
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            var f = graph.DefineFunction<long, Vec<long>>("f");
            var before = graph.Values.InternedCount;
            graph.Root.Assume(f.Of(1L).Is(Vec.Of(3L, 1L, 2L)), f.Of(2L).Is(Vec.Of(3L, 1L, 2L)), f.Of(3L).Is(Vec<long>.Empty));

            Assert.Equal(Vec.Of(3L, 1L, 2L), graph.Root.Get(f.Of(1L)));
            Assert.Empty(graph.Root.Get(f.Of(3L)));
            Assert.True(graph.Root.Check(f.Of(1L).Is(f.Of(2L))));
            Assert.Equal(before + 2, graph.Values.InternedCount);
            Assert.False(graph.Root.Check(f.Of(1L).Is(Vec.Of(1L, 2L, 3L))));
            Assert.Equal(before + 2, graph.Values.InternedCount);
        }

        [Fact]
        public void APartialVectorPrimitiveDropsABodyMatchAndFailsAHead()
        {
            // vec-get has no result out of range, as egglog's; vec-set has none either, as egglog 3.0.0's.
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            var v = graph.DefineFunction<long, Vec<long>>("v");
            var got = graph.DefineFunction<long, long>("got");
            var set = graph.DefineFunction<long, Vec<long>>("set");
            var body = graph.DefineRuleset("body");
            body.DefineRule("get", r =>
            {
                var (k, x) = r.Vars<long, Vec<long>>("k", "x");
                var e = r.Var<long>("e");
                r.When(x.Is(v.Of(k)), e.Is(Primitives.VecGet<long>().Of(x, 2L)));
                r.Then(got.Of(k).Is(e));
            });
            var head = graph.DefineRuleset("head");
            head.DefineRule("set", r =>
            {
                var (k, x) = r.Vars<long, Vec<long>>("k", "x");
                r.When(x.Is(v.Of(k)));
                r.Then(set.Of(k).Is(Primitives.VecSet<long>().Of(x, 2L, 9L)));
            });
            graph.Root.Assume(v.Of(1L).Is(Vec.Of(1L, 2L, 3L)), v.Of(2L).Is(Vec.Of(1L)));

            graph.Root.Run(Schedule.Run(body));
            Assert.Equal(3L, graph.Root.Get(got.Of(1L)));
            Assert.False(graph.Root.TryGet(got.Of(2L), out _));

            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(Schedule.Run(head)));
        }

        [Fact]
        public void ChildScopesInternVectorsOnce()
        {
            var graph = new EGraph();
            graph.Values.RegisterVec<string>();
            var f = graph.DefineFunction<long, Vec<string>>("f");
            graph.Root.Freeze();
            var before = graph.Values.InternedCount;
            using var a = graph.Root.CreateChild();
            using var b = graph.Root.CreateChild();
            a.Assume(f.Of(1L).Is(Vec.Of("x", "y")));
            b.Assume(f.Of(1L).Is(Vec.Of("x", "y")));

            // "x", "y" and the vector, each once.
            Assert.Equal(before + 3, graph.Values.InternedCount);
            Assert.Equal(Vec.Of("x", "y"), b.Get(f.Of(1L)));
        }

        [Fact]
        public void AVectorRendersSerializesAndExtractsAsEgglogs()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterVec<long>();
            var leaf = t.Graph.DefineConstructor<Vec<long>, Node>("Leaf");
            var x = t.Root.Make(leaf.Of(Primitives.VecOf<long>().Of(1L, 2L)));
            var empty = t.Root.Make(leaf.Of(Primitives.VecEmpty<long>().Of()));
            var rule = t.Rules.DefineRule("contains", r =>
            {
                var v = r.Var<Vec<long>>("v");
                var n = r.Var<Node>("n");
                r.When(n.Is(leaf.Of(v)));
                r.Where(Primitives.VecContains<long>().Of(v, 2L));
                r.Then(t.Tag.Of(n));
            });

            Assert.Equal("(rule ((= n (Leaf v)) (vec-contains v 2)) ((tag n)) :ruleset rules)", rule.ToString());
            Assert.Equal("(Leaf (vec-of 1 2))", t.Root.Extract(x).ToString());
            Assert.Equal(3, t.Root.Extract(x).Cost);
            Assert.Equal("(Leaf (vec-empty))", t.Root.Extract(empty).ToString());
            Assert.Equal(1, t.Root.Extract(empty).Cost);

            var serialized = t.Root.Serialize();
            var nodes = serialized.Nodes.Where(n => n.Op == "vec-of").ToArray();
            Assert.Equal(2, nodes.Length);
            Assert.Contains(nodes, n => n.Children.SequenceEqual(new[] { "primitive-i64-1", "primitive-i64-2" }));
            Assert.Contains(nodes, n => n.Children.Count == 0);
        }

        static ColumnType<Vec<Pair<long, long>>> RegisterNested(EGraph graph)
        {
            graph.Values.RegisterPair<long, long>();
            return graph.Values.RegisterVec<Pair<long, long>>();
        }

    }

}
