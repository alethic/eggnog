using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Maps of values, egglog's <c>Map</c> container: the map type itself, registration, writes and reads, rules,
    /// rendering, serialization and extraction in egglog's key-cell order.
    /// </summary>
    public sealed class MapTests
    {

        [Fact]
        public void AMapIsEqualByContentAndEnumeratesInKeyOrder()
        {
            var map = Map.Of(("b", 1L), ("a", 2L), ("b", 3L));
            Assert.Equal(2, map.Count);
            Assert.Equal(new[] { "a", "b" }, map.Keys);
            Assert.Equal(3L, map["b"]);
            Assert.Equal(map, Map.Of(("a", 2L), ("b", 3L)));
            Assert.Equal(map.GetHashCode(), Map.Of(("a", 2L), ("b", 3L)).GetHashCode());
            Assert.Equal(Map.Of(("a", 2L), ("b", 4L)), map.SetItem("b", 4L));
            Assert.Same(map, map.SetItem("b", 3L));
            Assert.Equal(Map.Of(("b", 3L)), map.Remove("a"));
            Assert.Same(map, map.Remove("c"));
            Assert.False(map.TryGetValue("c", out _));
            Assert.Throws<KeyNotFoundException>(() => map["c"]);
            Assert.Equal("{a: 2, b: 3}", map.ToString());
        }

        [Fact]
        public void RegistrationRefusesClassesAndIncomparableKeys()
        {
            var t = new NodeTheory();
            Assert.Equal("Map<String, i64>", t.Graph.Values.RegisterMap<string, long>().Name);
            var sort = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMap<long, Node>());
            Assert.Contains("Node is a sort", sort.Message, StringComparison.Ordinal);
            t.Graph.Values.Register<NumericBound>("NumericBound");
            t.Graph.Values.RegisterPair<NumericBound, long>();
            var key = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMap<NumericBound, long>());
            Assert.Contains("IComparable", key.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMap<Pair<NumericBound, long>, long>());
            t.Graph.Values.RegisterPair<long, long>();
            Assert.Equal("Map<Pair<i64, i64>, i64>", t.Graph.Values.RegisterMap<Pair<long, long>, long>().Name);
            var register = Assert.Throws<ArgumentException>(() => t.Graph.Values.Register<Map<long, long>>("M"));
            Assert.Contains("ValueStore.RegisterMap", register.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MapsRoundTripAndInternOnce()
        {
            var graph = new EGraph();
            graph.Values.RegisterMap<long, bool>();
            var f = graph.DefineFunction<long, Map<long, bool>>("f");
            var before = graph.Values.InternedCount;
            graph.Root.Assume(f.Of(1L).Is(Map.Of((3L, true), (1L, false))), f.Of(2L).Is(Map.Of((1L, false), (3L, true))));

            Assert.Equal(Map.Of((1L, false), (3L, true)), graph.Root.Get(f.Of(1L)));
            Assert.True(graph.Root.Check(f.Of(1L).Is(f.Of(2L))));
            Assert.Equal(before + 1, graph.Values.InternedCount);
            Assert.False(graph.Root.Check(f.Of(1L).Is(Map.Of((1L, true)))));
            Assert.Equal(before + 1, graph.Values.InternedCount);
        }

        [Fact]
        public void AMapExtractsAndSerializesInKeyCellOrder()
        {
            // (sort SM (Map String i64)) (let $s (map-insert (map-insert (map-empty) "b" 1) "a" 2)) (extract $s) prints
            // (map-insert (map-insert (map-empty) "b" 1) "a" 2): "b" was interned first, and egglog's BTreeMap orders
            // the keys by their cells.
            var t = new NodeTheory();
            t.Graph.Values.RegisterMap<string, long>();
            var insert = Primitives.MapInsert<string, long>();
            var s = t.Graph.DefineFunction<Map<string, long>>("s");
            var leaf = t.Graph.DefineConstructor<Map<string, long>, Node>("Leaf");
            t.Root.Assume(s.Of().Is(insert.Of(insert.Of(Primitives.MapEmpty<string, long>().Of(), "b", 1L), "a", 2L)));
            var x = t.Root.Make(leaf.Of(s.Of()));

            Assert.Equal("(Leaf (map-insert (map-insert (map-empty) \"b\" 1) \"a\" 2))", t.Root.Extract(x).ToString());
            Assert.Equal(1 + 4, t.Root.Extract(x).Cost);
            Assert.Equal(Map.Of(("a", 2L), ("b", 1L)), t.Root.Get(s.Of()));

            var serialized = t.Root.Serialize();
            var node = serialized.Nodes.Single(n => n.Op == "Map<String, i64>");
            var children = node.Children.Select(c => serialized.Nodes.Single(n => n.Id == c).Op).ToArray();
            Assert.Equal(new[] { "\"b\"", "1", "\"a\"", "2" }, children);
        }

        [Fact]
        public void ARuleReadsAndWritesMaps()
        {
            var graph = new EGraph();
            graph.Values.RegisterMap<long, long>();
            var m = graph.DefineFunction<long, Map<long, long>>("m");
            var got = graph.DefineFunction<long, long>("got");
            var grown = graph.DefineFunction<long, Map<long, long>>("grown");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("get", r =>
            {
                var (k, x) = r.Vars<long, Map<long, long>>("k", "x");
                var v = r.Var<long>("v");
                r.When(x.Is(m.Of(k)), v.Is(Primitives.MapGet<long, long>().Of(x, 7L)));
                r.Where(Primitives.MapNotContains<long, long>().Of(x, 8L));
                r.Then(got.Of(k).Is(v), grown.Of(k).Is(Primitives.MapInsert<long, long>().Of(x, 8L, v)));
            });
            graph.Root.Assume(m.Of(1L).Is(Map.Of((7L, 70L))), m.Of(2L).Is(Map.Of((6L, 60L))));

            graph.Root.Run(Schedule.Run(rules));

            Assert.Equal(70L, graph.Root.Get(got.Of(1L)));
            Assert.False(graph.Root.TryGet(got.Of(2L), out _));
            Assert.Equal(Map.Of((7L, 70L), (8L, 70L)), graph.Root.Get(grown.Of(1L)));
            Assert.Equal(
                "(rule ((= x (m k)) (= v (map-get x 7)) (map-not-contains x 8)) ((set (got k) v) (set (grown k) (map-insert x 8 v))) :ruleset rules)",
                rule.ToString());
        }

    }

}
