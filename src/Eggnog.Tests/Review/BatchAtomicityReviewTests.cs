using System;
using Eggnog.Tests.Support;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of D30/D32/D39 batch atomicity: "If write throws or a call is rejected, nothing is written and the
    /// class values the batch's Make calls returned are invalid" (§3.1 Scope.Write; §6.6.1).
    /// </summary>
    public sealed class BatchAtomicityReviewTests
    {

        [Fact]
        public void ARejectedCallThatTheWriteDelegateCatchesStillWritesNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var version = t.Root.Version;
            var made = default(Node);
            t.Root.Write(w =>
            {
                w.Assume(t.Num.Of(5L));
                made = w.Make(t.A.Of());
                try
                {
                    w.Assume(t.Num.Of(f.Of(1L)));
                }
                catch (ArgumentException)
                {
                    // The call was rejected; the batch must write nothing.
                }
            });

            Assert.False(t.Root.Check(t.Num.Of(5L)));
            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<ArgumentException>(() => t.Root.Find(made));
        }

        [Fact]
        public void ARejectedConflictCaughtInsideWriteStillWritesNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Write(w =>
            {
                w.Assume(f.Of("k").Is(1L));
                try
                {
                    w.Assume(f.Of("k").Is(2L));
                }
                catch (FactConflictException)
                {
                    // Rejected.
                }
            });

            Assert.False(t.Root.TryGet(f.Of("k"), out _));
        }

        [Fact]
        public void APartlyPlannedFactThatIsRejectedLeavesNoTerm()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var link = t.Graph.DefineRelation<Node, long>("link");
            t.Root.Write(w =>
            {
                try
                {
                    // Plans Wrap(A) and A as new terms, then rejects the fact at its second argument (a function read).
                    w.Assume(link.Of(t.Wrap.Of(t.A.Of()), f.Of(1L)));
                }
                catch (ArgumentException)
                {
                    // Rejected.
                }
            });

            Assert.False(t.Root.Check(Fact.Exists(t.A.Of())));
            Assert.False(t.Root.Check(Fact.Exists(t.Wrap.Of(t.A.Of()))));
        }

    }

}
