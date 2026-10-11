using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for containers in an <see cref="EGraph"/> (egglog-bridge/src/lib.rs:231-257, 626-682): registration,
    /// interning, and the rebuild loop that rebuilds containers before tables and re-stamps the parents of containers
    /// whose meaning changed. The parts of egglog's <c>basic_container</c> (egglog-bridge/src/tests.rs:508-717) that do
    /// not need rules, with unions made directly.
    /// </summary>
    public class EGraphContainerTests
    {

        static List<string> DumpVecs(EGraph egraph)
        {
            var collector = new ContainerCollector<VecContainer>();
            egraph.ContainerValues.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref collector);
            return collector.Items.Select(item => string.Join(",", item.Container.Values.ToArray())).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        [Fact]
        public void ContainersAreInternedWithFreshIds()
        {
            var egraph = NewEGraph();
            var a = egraph.FreshId();
            var b = egraph.FreshId();

            var empty = egraph.GetContainerValue(new VecContainer());
            var va = egraph.GetContainerValue(new VecContainer(a.Rep));
            var vb = egraph.GetContainerValue(new VecContainer(b.Rep));
            Assert.Equal(va, egraph.GetContainerValue(new VecContainer(a.Rep)));
            Assert.Equal(new Value(2), empty);
            Assert.Equal(new Value(3), va);
            Assert.Equal(new Value(4), vb);
            Assert.Equal(5, egraph.Database.ReadCounter(egraph.IdCounter));

            Assert.True(egraph.ContainerValues.TryGetValue<VecContainer>(va.Rep, out var container));
            Assert.Equal([a.Rep], container.Values.ToArray());

            // Registering again keeps the type.
            Assert.Equal(egraph.RegisterContainerType<VecContainer>(), egraph.RegisterContainerType<VecContainer>());
            Assert.Equal(1, egraph.ContainerValues.TypeCount);
        }

        [Fact]
        public void EqualContainersMergeAndTheirParentsCollapse()
        {
            var egraph = NewEGraph();
            var vec = Constructor(egraph, "vec", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var va = egraph.GetContainerValue(new VecContainer(a.Rep));
            var vb = egraph.GetContainerValue(new VecContainer(b.Rep));
            var ta = egraph.AddTerm(vec, [va]);
            var tb = egraph.AddTerm(vec, [vb]);
            Assert.Equal(["0", "1"], DumpVecs(egraph));

            Union(egraph, a, b);

            // vec[b] became vec[a], so the two containers merged (keeping the smaller id), and so did their parents.
            Assert.Equal(["0"], DumpVecs(egraph));
            Assert.Equal(va, egraph.GetCanonRepr(vb, ColumnTy.Id));
            Assert.Equal(egraph.GetCanonRepr(ta, ColumnTy.Id), egraph.GetCanonRepr(tb, ColumnTy.Id));
            Assert.Equal([$"{va.Rep} {Math.Min(ta.Rep, tb.Rep)}"], Show(egraph, vec));
        }

        [Fact]
        public void ParentsOfAContainerRebuiltInPlaceAreRestamped()
        {
            var egraph = NewEGraph();
            var holder = Constructor(egraph, "holder", false, ColumnTy.Id);
            var a = egraph.FreshId();
            var b = egraph.FreshId();

            // vec[b] is the only vector; when b becomes a it is rebuilt to vec[a] and keeps its id.
            var vb = egraph.GetContainerValue(new VecContainer(b.Rep));
            var h = egraph.AddTerm(holder, [vb]);
            var stamped = PhysicalRow(egraph, holder, vb.Rep)[2];

            var before = egraph.NextTimestamp;
            Union(egraph, a, b);

            Assert.Equal(["0"], DumpVecs(egraph));
            Assert.Equal(vb, egraph.GetCanonRepr(vb, ColumnTy.Id));
            Assert.Equal(h, egraph.LookupId(holder, [vb]));

            // The row is the same, but its timestamp is new, so seminaive evaluation sees it (lib.rs:669-675).
            var restamped = PhysicalRow(egraph, holder, vb.Rep)[2];
            Assert.True(restamped > stamped);
            Assert.True(restamped >= before);
        }

        [Fact]
        public void NestedContainersRebuildToAFixpoint()
        {
            var egraph = NewEGraph();
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var inner1 = egraph.GetContainerValue(new VecContainer(a.Rep));
            var inner2 = egraph.GetContainerValue(new VecContainer(b.Rep));
            var outer1 = egraph.GetContainerValue(new VecContainer(inner1.Rep, inner1.Rep));
            var outer2 = egraph.GetContainerValue(new VecContainer(inner2.Rep, inner2.Rep));

            Union(egraph, a, b);

            // The inner vectors merge, which makes the outer ones equal, which merge in a later pass.
            Assert.Equal(egraph.GetCanonRepr(inner1, ColumnTy.Id), egraph.GetCanonRepr(inner2, ColumnTy.Id));
            Assert.Equal(egraph.GetCanonRepr(outer1, ColumnTy.Id), egraph.GetCanonRepr(outer2, ColumnTy.Id));
            Assert.Equal(2, DumpVecs(egraph).Count);
        }

        /// <summary>
        /// The scenario of egglog's comment on the order of the rebuild loop (egglog-bridge/src/lib.rs:635-665): a
        /// function without a merge maps each term to the vector of itself; when two terms become equal, the vectors
        /// become equal in the same rebuild, so the collapsed rows agree and no merge conflict is reported.
        /// </summary>
        [Fact]
        public void ContainersRebuildBeforeTablesSoNoMergeFunctionsAgree()
        {
            var egraph = NewEGraph();
            var toVec = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "to-vec",
            });
            var x2 = egraph.FreshId();
            var x3 = egraph.FreshId();
            var v2 = egraph.GetContainerValue(new VecContainer(x2.Rep));
            var v3 = egraph.GetContainerValue(new VecContainer(x3.Rep));
            egraph.AddValues([(toVec, [x2, v2]), (toVec, [x3, v3])]);

            Union(egraph, x3, x2);

            Assert.False(egraph.TryTakePanic(out var message), message);
            Assert.Equal([$"{x2.Rep} {v2.Rep}"], Show(egraph, toVec));
            Assert.Equal(v2, egraph.GetCanonRepr(v3, ColumnTy.Id));
        }

    }

}
