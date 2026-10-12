using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// R1's mechanism for class constants that lose a union (§6.4.5): the root adapter lowers the rule again with the
    /// canonical constants, under the description suffix <c>/k{generation}</c>, and the new engine rule starts at
    /// watermark 0, a full run. Deleted with <c>RootEngineScope</c> in R2.
    /// </summary>
    public sealed class RootRelowerTests
    {

        [Fact]
        public void TheReloweredVariantEndsWithK1AndRunsInFull()
        {
            var t = new NodeTheory();
            var link = t.Graph.DefineRelation<Node, long>("link");
            var hit = t.Graph.DefineRelation<long>("hit");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var rule = t.Rules.DefineRule("watch b", r =>
            {
                var n = r.Var<long>("n");
                r.When(link.Of(b, n));
                r.Then(hit.Of(n));
            });
            t.Root.Assume(link.Of(a, 3L), link.Of(b, 1L));
            var first = t.Root.Run(t.Rules);
            var original = rule.Compiled.Variants.Default!;
            Assert.Equal($"rules/watch b#{rule.Compiled.Serial}", original.Description);
            Assert.Equal(1, first.Rules[rule].Matches);

            t.Root.Union(a, b);
            var second = t.Root.Run(t.Rules);

            var relowered = rule.Compiled.Variants.Default!;
            Assert.Equal(original.Description + "/k1", relowered.Description);
            Assert.Equal(new[] { a.Id.Cell }, relowered.Constants);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(original.Description));
            Assert.Same(relowered, t.Graph.VariantsByDescription[relowered.Description]);

            // A full run: link(a, 3) is older than the rule's last run, and still matches.
            Assert.Equal(2, second.Rules[rule].Matches);
            Assert.True(t.Root.Check(hit.Of(3L)));

            var third = t.Root.Run(t.Rules);
            Assert.Same(relowered, rule.Compiled.Variants.Default);
            Assert.Equal(0, third.Rules[rule].Matches);
        }

    }

}
