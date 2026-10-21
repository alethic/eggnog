using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Maps of values, ported from egglog's <c>tests/map.egg</c> at 5e655cae. egglog extracts the map itself; here it is
    /// extracted as the argument of a constructor, since the library extracts classes.
    /// </summary>
    public sealed class MapPortedTests
    {

        /// <summary>
        /// tests/map.egg: <c>map-insert</c>, <c>map-get</c> and the map's term, <c>(map-insert (map-insert (map-empty) 1
        /// "one") 2 "two")</c>, which egglog prints for <c>(extract $my_map2)</c>.
        /// </summary>
        [Fact]
        public void MapsOfValues()
        {
            var t = new NodeTheory();

            // (sort MyMap (Map i64 String))
            Assert.Equal("Map<i64, String>", t.Graph.Values.RegisterMap<long, string>().Name);
            var insert = Primitives.MapInsert<long, string>();
            var map1 = t.Graph.DefineFunction<Map<long, string>>("my_map1");
            var map2 = t.Graph.DefineFunction<Map<long, string>>("my_map2");
            var leaf = t.Graph.DefineConstructor<Map<long, string>, Node>("Leaf");

            // (let $my_map1 (map-insert (map-empty) 1 "one")) (let $my_map2 (map-insert $my_map1 2 "two"))
            t.Root.Assume(map1.Of().Is(insert.Of(Primitives.MapEmpty<long, string>().Of(), 1L, "one")));
            t.Root.Assume(map2.Of().Is(insert.Of(map1.Of(), 2L, "two")));

            // (check (= "one" (map-get $my_map1 1)))
            Assert.True(t.Root.Check(Primitives.MapGet<long, string>().Of(map1.Of(), 1L).Is("one")));

            // (extract $my_map2)
            var x = t.Root.Make(leaf.Of(map2.Of()));
            Assert.Equal("(Leaf (map-insert (map-insert (map-empty) 1 \"one\") 2 \"two\"))", t.Root.Extract(x).ToString());
        }

    }

}
