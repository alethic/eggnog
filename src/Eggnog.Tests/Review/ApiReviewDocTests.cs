using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of documented behaviour: each test asserts what the XML docs (and the spec they copy) promise.
    /// </summary>
    public sealed class ApiReviewDocTests
    {

        /// <summary>
        /// RuleEvaluationException.Bindings is documented (and §3.5, §6.1 specify it) as e.g. <c>d={a, b} v="b"</c>.
        /// </summary>
        [Fact]
        public void BindingsRenderASetAsDocumented()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineRelation<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var boom = Primitive.Create<ValueSet<string>, string, bool>("boom", (_, _) => throw new InvalidOperationException("boom"));
            rules.DefineRule("throws on a set", r =>
            {
                var (p, d, v) = r.Vars<string, ValueSet<string>, string>("p", "d", "v");
                r.When(d.Is(dom.Of(p)), pick.Of(p, v));
                r.Where(boom.Of(d, v));
                r.Then(pick.Of(v, p));
            });
            graph.Root.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), pick.Of("k", "b"));

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
            Assert.Equal("d={a, b} v=\"b\"", e.Bindings);
        }

        /// <summary>
        /// ValueStore.TypeOf: an unknown type's ArgumentException names a parameter of the method called.
        /// </summary>
        [Fact]
        public void TypeOfNamesNoForeignParameter()
        {
            var graph = new EGraph();
            var e = Assert.Throws<ArgumentException>(() => graph.Values.TypeOf<Point>());
            Assert.True(e.ParamName is null || e.ParamName == "T", $"ParamName is '{e.ParamName}'");
        }

        /// <summary>
        /// Enum values extract and render as their member names (§6.1's Format column).
        /// </summary>
        [Fact]
        public void EnumsExtractByMemberName()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            graph.Values.RegisterEnum<Shade>();
            var e = graph.DefineConstructor<Shade, Node>("E");
            var root = graph.Root;
            Assert.Equal("(E Dark)", root.Extract(root.Make(e.Of(Shade.Dark))).ToString());
        }

    }

}
