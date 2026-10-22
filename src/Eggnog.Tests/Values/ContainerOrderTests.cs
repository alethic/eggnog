using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Sets, multisets and maps keep their elements in element order, which an order given to
    /// <see cref="ValueStore.Register{T}"/> is not, and compare in it: by value (<see cref="ColumnType{T}.Compare"/>) and
    /// by cell (the content order host reads sort rows by) alike.
    /// </summary>
    public sealed class ContainerOrderTests
    {

        [Fact]
        public void SortedContainersCompareInElementOrderByValueAndByCell()
        {
            // Ranked is registered descending, and Ranked's own order is ascending: {1, 3} comes before {2} in element
            // order, after it in the registered one.
            var graph = new EGraph();
            graph.Values.Register<Ranked>("Ranked", Comparer<Ranked>.Create((x, y) => y.Value.CompareTo(x.Value)));
            var sets = graph.Values.RegisterSet<Ranked>();
            var multisets = graph.Values.RegisterMultiSet<Ranked>();
            var maps = graph.Values.RegisterMap<Ranked, long>();
            var s = graph.DefineRelation<Set<Ranked>>("s");
            var m = graph.DefineRelation<MultiSet<Ranked>>("m");
            var p = graph.DefineRelation<Map<Ranked, long>>("p");
            var (one, two, three) = (new Ranked(1), new Ranked(2), new Ranked(3));
            var (setA, setB) = (Set.Of(one, three), Set.Of(two));
            var (multisetA, multisetB) = (MultiSet.Of(one, three), MultiSet.Of(two));
            var (mapA, mapB) = (Map.Of((one, 0L), (three, 0L)), Map.Of((two, 0L)));
            graph.Root.Assume(s.Of(setB), s.Of(setA), m.Of(multisetB), m.Of(multisetA), p.Of(mapB), p.Of(mapA));

            Assert.True(sets.Compare(setA, setB) < 0);
            Assert.True(multisets.Compare(multisetA, multisetB) < 0);
            Assert.True(maps.Compare(mapA, mapB) < 0);
            Assert.Equal(new object[] { setA, setB }, graph.Root.Rows(s).Select(r => r.Keys[0]));
            Assert.Equal(new object[] { multisetA, multisetB }, graph.Root.Rows(m).Select(r => r.Keys[0]));
            Assert.Equal(new object[] { mapA, mapB }, graph.Root.Rows(p).Select(r => r.Keys[0]));
        }

    }

}
