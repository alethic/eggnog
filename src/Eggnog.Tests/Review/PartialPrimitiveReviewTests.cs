using Eggnog.Tests.Support;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probe: egglog's <c>+</c> is partial (<c>checked_add</c>, <c>-?&gt;</c>): in a body, an overflow drops the
    /// match; only a head call panics. Eggnog's <see cref="Primitives.Add"/> throws, so a body overflow fails the run and
    /// faults the root (D31).
    /// </summary>
    public sealed class PartialPrimitiveReviewTests
    {

        [Fact]
        public void ABodyOverflowDropsTheMatchAsInEgglog()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(long.MaxValue));
            t.Rules.DefineRule("plus one", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), y.Is(Primitives.Add.Of(x, 1L)));
                r.Then(hit.Of(y));
            });
            var thrown = Record.Exception(() => t.Root.Run(Schedule.Saturate(t.Rules)));
            Assert.Null(thrown);
            Assert.False(t.Root.IsFaulted);
        }

    }

}
